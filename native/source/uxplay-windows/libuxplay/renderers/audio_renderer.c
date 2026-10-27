/**
 * RPiPlay - An open-source AirPlay mirroring server for Raspberry Pi
 * Copyright (C) 2019 Florian Draschbacher
 * Modified for:
 * UxPlay - An open-source AirPlay mirroring server
 * Copyright (C) 2021-23 F. Duncanh
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301  USA
 */

#include <math.h>
#include <gst/gst.h>
#include <gst/app/gstappsrc.h>
#include "audio_renderer.h"
#define SECOND_IN_NSECS 1000000000UL

#ifdef UXPLAY_AUDIO_LIFECYCLE_TESTS
#define NFORMATS 4     /* Offline tests use the production PCM path with a silent fakesink. */
#else
#define NFORMATS 2
#endif

static GstClockTime gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
static logger_t *logger = NULL;
const char * format[NFORMATS];

static const gchar *avdec_aac = "avdec_aac";
static const gchar *avdec_alac = "avdec_alac";
static gboolean aac = FALSE;
static gboolean alac = FALSE;
static gboolean render_audio = FALSE;
static gboolean async = FALSE;
static gboolean vsync = FALSE;
static gboolean sync = FALSE;
static gboolean audio_rtp = FALSE;
static GRecMutex audio_lock;
static unsigned int stale_timestamp_count = 0;
static gint64 last_stale_timestamp_log = 0;

typedef struct audio_renderer_s {
    GstElement *appsrc; 
    GstElement *pipeline;
    GstElement *volume;
    GstBus *bus;
    unsigned char ct;
    gboolean needs_reset;
    unsigned int recovery_attempts;
    gint64 healthy_since;
    gint64 last_health_message;
    GstClockTime first_healthy_running_time;
    GstClockTime last_healthy_running_time;
} audio_renderer_t ;
static audio_renderer_t *renderer_type[NFORMATS];
static audio_renderer_t *renderer = NULL;

/* GStreamer Caps strings for Airplay-defined audio compression types (ct) */

/* ct = 1; linear PCM (uncompressed): 44100/16/2, S16LE */
static const char lpcm_caps[]="audio/x-raw,rate=(int)44100,channels=(int)2,format=S16LE,layout=interleaved";

/* ct = 2; codec_data is ALAC magic cookie:  44100/16/2 spf = 352 */    
static const char alac_caps[] = "audio/x-alac,mpegversion=(int)4,channels=(int)2,rate=(int)44100,stream-format=raw,codec_data=(buffer)"
                           "00000024""616c6163""00000000""00000160""0010280a""0e0200ff""00000000""00000000""0000ac44";

/* ct = 4; codec_data from MPEG v4 ISO 14996-3 Section 1.6.2.1:  AAC-LC 44100/2 spf = 1024 */
static const char aac_lc_caps[] ="audio/mpeg,mpegversion=(int)4,channels=(int)2,rate=(int)44100,stream-format=raw,codec_data=(buffer)1210";

/* ct = 8; codec_data from MPEG v4 ISO 14996-3 Section 1.6.2.1: AAC_ELD 44100/2  spf = 480 */
static const char aac_eld_caps[] ="audio/mpeg,mpegversion=(int)4,channels=(int)2,rate=(int)44100,stream-format=raw,codec_data=(buffer)f8e85000";

static gboolean check_plugins (void)
{
    GstRegistry *registry = NULL;
    const gchar *needed[] = { "app", "libav", "playback", "autodetect", "videoparsersbad",  NULL};
    const gchar *gst[] = {"plugins-base", "libav", "plugins-base", "plugins-good", "plugins-bad", NULL};
    registry = gst_registry_get ();
    gboolean ret = TRUE;
    for (int i = 0; i < g_strv_length ((gchar **) needed); i++) {
        GstPlugin *plugin = NULL;
        plugin = gst_registry_find_plugin (registry, needed[i]);
        if (!plugin) {
            g_print ("Required gstreamer plugin '%s' not found\n"
                     "Missing plugin is contained in  '[GStreamer 1.x]-%s'\n",needed[i], gst[i]);
            ret = FALSE;
            continue;
        }
        gst_object_unref (plugin);
        plugin = NULL;
    }
    if (ret == FALSE) {
        g_print ("\nif the plugin is installed, but not found, your gstreamer registry may have been corrupted.\n"
                 "to rebuild it when gstreamer next starts, clear your gstreamer cache with:\n"
                 "\"rm -rf ~/.cache/gstreamer-1.0\"\n\n");
    }
    return ret;
}

static gboolean check_plugin_feature (const gchar *needed_feature)
{
    GstPluginFeature *plugin_feature = NULL;
    GstRegistry *registry = gst_registry_get ();
    gboolean ret = TRUE;

    plugin_feature = gst_registry_find_feature (registry, needed_feature, GST_TYPE_ELEMENT_FACTORY);
    if (!plugin_feature) {
        g_print ("Required gstreamer libav plugin feature '%s' not found:\n\n"
	         "This may be missing because the FFmpeg package used by GStreamer-1.x-libav is incomplete.\n"
	         "(Some distributions provide an incomplete FFmpeg due to License or Patent issues:\n"
	         "in such cases a complete version for that distribution is usually made available elsewhere)\n",
	         needed_feature);
        ret = FALSE;
    } else {
        gst_object_unref (plugin_feature);
        plugin_feature = NULL;
    }
    if (ret == FALSE) {
        g_print ("\nif the plugin feature is installed, but not found, your gstreamer registry may have been corrupted.\n"
                 "to rebuild it when gstreamer next starts, clear your gstreamer cache with:\n"
                 "\"rm -rf ~/.cache/gstreamer-1.0\"\n\n");
    }
    return ret;
}

bool gstreamer_init(){
    gst_init(NULL,NULL);    
    return (bool) check_plugins ();
}

void audio_renderer_init(logger_t *render_logger, const char* audiosink, const bool* audio_sync, const bool* video_sync, const char *artp_pipeline) {
    g_rec_mutex_lock(&audio_lock);
    /* Initialization is also safe after a previous instance was disposed. */
    audio_renderer_destroy();
    GError *error = NULL;
    GstCaps *caps = NULL;
    GstClock *clock = gst_system_clock_obtain();
    g_object_set(clock, "clock-type", GST_CLOCK_TYPE_REALTIME, NULL);

    audio_rtp = (bool) strlen(artp_pipeline);
    if (audio_rtp) {
        g_print("*** Audio RTP mode enabled: sending to %s\n", artp_pipeline);
    }

    logger = render_logger;
    gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
    render_audio = FALSE;
    sync = FALSE;
    stale_timestamp_count = 0;
    last_stale_timestamp_log = 0;
    
    aac = check_plugin_feature (avdec_aac);
    alac = check_plugin_feature (avdec_alac);

    for (int i = 0; i < NFORMATS ; i++) {
        renderer_type[i] = (audio_renderer_t *)  calloc(1,sizeof(audio_renderer_t));
        g_assert(renderer_type[i]);
        GString *launch = g_string_new("appsrc name=audio_source ! ");
        g_string_append(launch, "queue ! ");
        switch (i) {
        case 0:    /* AAC-ELD */
        case 2:    /* AAC-LC */
            if (aac) g_string_append(launch, "avdec_aac ! ");
            break;
        case 1:    /* ALAC */
            if (alac) g_string_append(launch, "avdec_alac ! ");
            break;
        case 3:   /*PCM*/
            break;
        default:
            break;
        }
        g_string_append (launch, "audioconvert ! ");
        g_string_append (launch, "audioresample quality=10 ! ");    /* maximum resampling quality for 44.1kHz -> 48kHz audio */
        g_string_append (launch, "volume name=volume ! ");

        if (!audio_rtp) {
            /* Normal path: local audio output */
            g_string_append (launch, "level ! ");
            g_string_append (launch, audiosink);
            switch(i) {
            case 1:  /*ALAC*/
                if (*audio_sync) {
                    g_string_append (launch, " sync=true");
                    async = TRUE;
                } else {
                    g_string_append (launch, " sync=false");
                    async = FALSE;
                }
                break;
            default:
                if (*video_sync) {
                    g_string_append (launch, " sync=true");
                    vsync = TRUE;
                } else {
                    g_string_append (launch, " sync=false");
                    vsync = FALSE;
                }
                break;
            }
        } else {
            /* RTP path: send decoded PCM over RTP */
            /* rtpL16pay requires S16BE (big-endian) format */
            g_string_append (launch, "audioconvert ! audio/x-raw,format=S16BE,rate=44100,channels=2 ! ");
            g_string_append (launch, "rtpL16pay ");
            g_string_append (launch, artp_pipeline);
        }
        renderer_type[i]->pipeline  = gst_parse_launch(launch->str, &error);
	if (error) {
          g_error ("gst_parse_launch error (audio %d):\n %s\n", i+1, error->message);
          g_clear_error (&error);
        }

        g_assert (renderer_type[i]->pipeline);
        gst_pipeline_use_clock(GST_PIPELINE_CAST(renderer_type[i]->pipeline), clock);
        renderer_type[i]->bus = gst_element_get_bus(renderer_type[i]->pipeline);
        renderer_type[i]->appsrc = gst_bin_get_by_name (GST_BIN (renderer_type[i]->pipeline), "audio_source");
        renderer_type[i]->volume = gst_bin_get_by_name (GST_BIN (renderer_type[i]->pipeline), "volume");
        switch (i) {
        case 0:
            caps =  gst_caps_from_string(aac_eld_caps);
            renderer_type[i]->ct = 8;
            format[i] = "AAC-ELD 44100/2";
            break;
        case 1:
            caps =  gst_caps_from_string(alac_caps);
            renderer_type[i]->ct = 2;
            format[i] = "ALAC 44100/16/2";
            break;
        case 2:
            caps =  gst_caps_from_string(aac_lc_caps);
            renderer_type[i]->ct = 4;
            format[i] = "AAC-LC 44100/2";
            break;
        case 3:
            caps =  gst_caps_from_string(lpcm_caps);
            renderer_type[i]->ct = 1;
            format[i] = "PCM 44100/16/2 S16LE";
            break;
        default:
            break;
        }
        logger_log(logger, LOGGER_DEBUG, "Audio format %d: %s",i+1,format[i]);
        logger_log(logger, LOGGER_DEBUG, "GStreamer audio pipeline %d: \"%s\"", i+1, launch->str);
        g_string_free(launch, TRUE);
        g_object_set(renderer_type[i]->appsrc, "caps", caps, "stream-type", 0, "is-live", TRUE, "format", GST_FORMAT_TIME, NULL);
        gst_caps_unref(caps);
    }
    /* Upstream c3137b6: gst_system_clock_obtain() above owns one reference. */
    g_object_unref(clock);
    logger_log(logger, LOGGER_INFO, "audio lifecycle initialized: formats=%d realtime-clock sync-config retained", NFORMATS);
    g_rec_mutex_unlock(&audio_lock);
}

void audio_renderer_stop() {
    g_rec_mutex_lock(&audio_lock);
    if (renderer) {
        gst_element_set_state (renderer->pipeline, GST_STATE_NULL);
        renderer->needs_reset = FALSE;
        logger_log(logger, LOGGER_INFO, "audio lifecycle stopped: ct=%u", renderer->ct);
        renderer = NULL;
    }
    render_audio = FALSE;
    gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
    stale_timestamp_count = 0;
    g_rec_mutex_unlock(&audio_lock);
}

static void get_renderer_type(unsigned char *ct, int *id) {
    render_audio = FALSE;
    *id = -1;
    for (int i = 0; i < NFORMATS; i++) {
        if (renderer_type[i] && renderer_type[i]->ct == *ct) {
	    *id = i;
            break;
        }
    }
    switch (*id) {
    case 2:
    case 0:
        if (aac) {
            render_audio = TRUE;
        } else {
            logger_log(logger, LOGGER_INFO, "*** GStreamer libav plugin feature avdec_aac is missing, cannot decode AAC audio");
        }
        sync = vsync;
        break;
    case 1:
        if (alac) {
            render_audio = TRUE;
        } else {
            logger_log(logger, LOGGER_INFO, "*** GStreamer libav plugin feature avdec_alac is missing, cannot decode ALAC audio");
        }
        sync = async;
        break;
    case 3:
        render_audio = TRUE;
	sync = FALSE;
        break;
    default:
        break;
    }
}

/* An explicit new stream/FLUSH establishes a fresh appsrc segment.
 * READY clears previous EOS/queued media. GStreamer chooses the new realtime base;
 * the video clock and the receiver's shared NTP mapping are deliberately untouched. */
static gboolean audio_renderer_activate(const char *event) {
    if (!renderer || !renderer->pipeline) return FALSE;
    GstState state = GST_STATE_NULL, pending = GST_STATE_VOID_PENDING;
    gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
    gst_bus_set_flushing(renderer->bus, TRUE);
    gst_element_set_state(renderer->pipeline, GST_STATE_READY);
    gst_bus_set_flushing(renderer->bus, FALSE);
    GstStateChangeReturn result = gst_element_set_state(renderer->pipeline, GST_STATE_PLAYING);
    if (result != GST_STATE_CHANGE_FAILURE) {
        gst_element_get_state(renderer->pipeline, &state, &pending, 150 * GST_MSECOND);
        gst_audio_pipeline_base_time = gst_element_get_base_time(renderer->pipeline);
    }
    renderer->needs_reset = FALSE;
    renderer->healthy_since = 0;
    renderer->last_health_message = 0;
    stale_timestamp_count = 0;
    last_stale_timestamp_log = 0;
    logger_log(logger, LOGGER_INFO,
               "audio lifecycle %s: ct=%u state=%s pending=%s result=%d base_ns=%" G_GUINT64_FORMAT " sync=%d",
               event, renderer->ct, gst_element_state_get_name(state), gst_element_state_get_name(pending),
               result, gst_audio_pipeline_base_time, sync);
    return result != GST_STATE_CHANGE_FAILURE;
}

void audio_renderer_start(unsigned char *ct) {
    g_rec_mutex_lock(&audio_lock);
    if (!ct) {
        g_rec_mutex_unlock(&audio_lock);
        return;
    }
    int id = -1;
    get_renderer_type(ct, &id);
    if (id >= 0) {
        gboolean same_renderer = renderer == renderer_type[id];
        const char *event = same_renderer ? "same-format-reset" : "start";
        if (renderer && renderer != renderer_type[id]) {
            gst_element_set_state(renderer->pipeline, GST_STATE_NULL);
            renderer->needs_reset = FALSE;
        }
        renderer = renderer_type[id];
        renderer->recovery_attempts = 0;
        renderer->healthy_since = 0;
        renderer->last_health_message = 0;
        GstState state = GST_STATE_NULL, pending = GST_STATE_VOID_PENDING;
        gst_element_get_state(renderer->pipeline, &state, &pending, 0);
        if (same_renderer && !renderer->needs_reset && state == GST_STATE_PLAYING && pending == GST_STATE_VOID_PENDING) {
            /* Repeated healthy SETUP must not flush audio or change its base. */
            gst_audio_pipeline_base_time = gst_element_get_base_time(renderer->pipeline);
            logger_log(logger, LOGGER_INFO, "audio lifecycle same-format-kept-playing: ct=%u base_ns=%" G_GUINT64_FORMAT,
                       renderer->ct, gst_audio_pipeline_base_time);
        } else if (same_renderer && !renderer->needs_reset && state == GST_STATE_PAUSED) {
            /* Resume a live paused pipeline without discarding its decoder/segment. */
            GstStateChangeReturn result = gst_element_set_state(renderer->pipeline, GST_STATE_PLAYING);
            gst_element_get_state(renderer->pipeline, &state, &pending, 150 * GST_MSECOND);
            gst_audio_pipeline_base_time = gst_element_get_base_time(renderer->pipeline);
            if (result == GST_STATE_CHANGE_FAILURE) render_audio = FALSE;
            logger_log(logger, LOGGER_INFO, "audio lifecycle same-format-resumed: ct=%u state=%s result=%d base_ns=%" G_GUINT64_FORMAT,
                       renderer->ct, gst_element_state_get_name(state), result, gst_audio_pipeline_base_time);
        } else if (!audio_renderer_activate(event)) {
            render_audio = FALSE;
        }
    } else {
        logger_log(logger, LOGGER_ERR, "unknown audio compression type ct = %d", *ct);
    }
    g_rec_mutex_unlock(&audio_lock);
}

void audio_renderer_render_buffer(unsigned char* data, int *data_len, unsigned short *seqnum, uint64_t *ntp_time) {
    g_rec_mutex_lock(&audio_lock);
    GstBuffer *buffer = NULL;

    if (!render_audio || !renderer || !data || !data_len || *data_len <= 0 || !ntp_time) goto done;
    if (renderer->needs_reset) {
        /* A persistent sink error must not cause a reset/log on every packet. */
        if (renderer->recovery_attempts >= 1) goto done;
        renderer->recovery_attempts++;
        if (!audio_renderer_activate("recover")) goto done;
    }

    /* GstPipeline may complete PLAYING asynchronously. Read its real base instead
     * of retaining the previous segment's cached value. */
    gst_audio_pipeline_base_time = gst_element_get_base_time(renderer->pipeline);
    if (!GST_CLOCK_TIME_IS_VALID(gst_audio_pipeline_base_time)) goto done;

    GstClockTime pts = (GstClockTime) *ntp_time ;    /* now in nsecs */
    //GstClockTimeDiff latency = GST_CLOCK_DIFF(gst_element_get_current_clock_time (renderer->appsrc), pts);
    if (sync) {
        if (pts >= gst_audio_pipeline_base_time) {
            pts -= gst_audio_pipeline_base_time;
        } else {
            stale_timestamp_count++;
            gint64 now = g_get_monotonic_time();
            if (!last_stale_timestamp_log || now - last_stale_timestamp_log >= 5000000) {
                logger_log(logger, LOGGER_INFO,
                           "audio lifecycle stale-timestamp: skipped=%u delta_ns=%" G_GUINT64_FORMAT " base_ns=%" G_GUINT64_FORMAT,
                           stale_timestamp_count, gst_audio_pipeline_base_time - pts, gst_audio_pipeline_base_time);
                last_stale_timestamp_log = now;
            }
            goto done;
        }
    }
    if (stale_timestamp_count) {
        logger_log(logger, LOGGER_INFO, "audio lifecycle timestamps-resumed: skipped=%u", stale_timestamp_count);
        stale_timestamp_count = 0;
        last_stale_timestamp_log = 0;
    }

    /* all audio received seems to be either ct = 8 (AAC_ELD 44100/2 spf 460 ) AirPlay Mirror protocol *
     * or ct = 2 (ALAC 44100/16/2 spf 352) AirPlay protocol.                                           *
     * first byte data[0] of ALAC frame is 0x20,                                                       *
     * first byte of AAC_ELD is 0x8c, 0x8d or 0x8e: 0x100011(00,01,10) in modern devices               *
     *                   but is 0x80, 0x81 or 0x82: 0x100000(00,01,10) in ios9, ios10 devices          *
     * first byte of AAC_LC should be 0xff (ADTS) (but has never been  seen).                          */
    
    buffer = gst_buffer_new_allocate(NULL, *data_len, NULL);
    g_assert(buffer != NULL);
    //g_print("audio latency %8.6f\n", (double) latency / SECOND_IN_NSECS);
    if (sync) {
        GST_BUFFER_PTS(buffer) = pts;
    }
    gst_buffer_fill(buffer, 0, data, *data_len);
    bool valid = false;
    switch (renderer->ct){
    case 8: /*AAC-ELD*/
        switch (data[0]){
        case 0x8c:
        case 0x8d:
        case 0x8e:
        case 0x80:
        case 0x81:
        case 0x82:
            valid = true;
            break;          
        default:
            valid = false;
            break;
        }
        break;
    case 2: /*ALAC*/
        valid = (data[0] == 0x20);
        break;
    case 4:  /*AAC_LC */
        valid = (data[0] == 0xff );
 	break;
    default:
        valid = true;
        break;
    }
    if (valid) {
        GstFlowReturn flow = gst_app_src_push_buffer(GST_APP_SRC(renderer->appsrc), buffer);
        if (flow == GST_FLOW_EOS || flow == GST_FLOW_FLUSHING || flow == GST_FLOW_ERROR) {
            renderer->needs_reset = TRUE;
            renderer->healthy_since = 0;
            renderer->last_health_message = 0;
            logger_log(logger, LOGGER_INFO, "audio lifecycle flow-reset-pending: flow=%s", gst_flow_get_name(flow));
        }
    } else {
        gst_buffer_unref(buffer);
        logger_log(logger, LOGGER_ERR, "*** ERROR invalid  audio frame (compression_type %d) skipped ", renderer->ct);
    }
done:
    g_rec_mutex_unlock(&audio_lock);
}

void audio_renderer_set_volume(double volume) {
    g_rec_mutex_lock(&audio_lock);
    if (!renderer) {
       g_rec_mutex_unlock(&audio_lock);
       return;
    }
    volume = (volume > 10.0) ? 10.0 : volume;
    volume = (volume < 0.0) ? 0.0 : volume;
    g_object_set(renderer->volume, "volume", volume, NULL);
    g_rec_mutex_unlock(&audio_lock);
}

void audio_renderer_flush() {
    g_rec_mutex_lock(&audio_lock);
    if (renderer && render_audio) {
        renderer->recovery_attempts = 0;
        audio_renderer_activate("flush");
    }
    g_rec_mutex_unlock(&audio_lock);
}

void audio_renderer_destroy() {
    g_rec_mutex_lock(&audio_lock);
    audio_renderer_stop();
    for (int i = 0; i < NFORMATS ; i++ ) {
        if (!renderer_type[i]) continue;
        gst_element_set_state(renderer_type[i]->pipeline, GST_STATE_NULL);
        gst_object_unref (renderer_type[i]->bus);
        renderer_type[i]->bus = NULL;
        gst_object_unref (renderer_type[i]->volume);
        renderer_type[i]->volume = NULL;
        gst_object_unref (renderer_type[i]->appsrc);
        renderer_type[i]->appsrc = NULL;
        gst_object_unref (renderer_type[i]->pipeline);
        renderer_type[i]->pipeline = NULL;
        free(renderer_type[i]);
        renderer_type[i] = NULL;
    }
    renderer = NULL;
    render_audio = FALSE;
    gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
    g_rec_mutex_unlock(&audio_lock);
}

/* Re-arm one automatic recovery only after sustained downstream progress. A
 * successful appsrc enqueue alone says nothing about decoder/sink health. */
static void audio_renderer_observe_health(audio_renderer_t *active, GstMessage *message) {
    const GstStructure *structure = gst_message_get_structure(message);
    if (active != renderer || !active->recovery_attempts || active->needs_reset ||
        !structure || !gst_structure_has_name(structure, "level")) return;
    guint64 running_time = GST_CLOCK_TIME_NONE;
    if (!gst_structure_get_uint64(structure, "running-time", &running_time) ||
        !GST_CLOCK_TIME_IS_VALID(running_time)) return;
    GstClockTime current_running_time = gst_element_get_current_running_time(active->pipeline);
    if (!GST_CLOCK_TIME_IS_VALID(current_running_time) ||
        running_time > current_running_time + GST_SECOND ||
        (current_running_time > running_time && current_running_time - running_time > GST_SECOND)) {
        active->healthy_since = 0;
        active->last_health_message = 0;
        return;
    }
    gint64 now = g_get_monotonic_time();
    if (!active->healthy_since || now - active->last_health_message > 1000000 ||
        running_time < active->last_healthy_running_time) {
        active->healthy_since = now;
        active->first_healthy_running_time = running_time;
    }
    active->last_health_message = now;
    active->last_healthy_running_time = running_time;
    if (now - active->healthy_since >= 3000000 &&
        running_time - active->first_healthy_running_time >= 2 * GST_SECOND) {
        active->recovery_attempts = 0;
        active->healthy_since = 0;
        logger_log(logger, LOGGER_INFO, "audio lifecycle recovery-budget-restored: downstream-health-ms=3000");
    }
}

static gboolean gstreamer_audio_pipeline_bus_callback(GstBus *bus, GstMessage *message, void *loop) {
    g_rec_mutex_lock(&audio_lock);
    audio_renderer_t *event_renderer = NULL;
    for (int i = 0; i < NFORMATS; i++) {
        if (renderer_type[i] && renderer_type[i]->bus == bus) {
            event_renderer = renderer_type[i];
            break;
        }
    }
    if (!event_renderer) {
        g_rec_mutex_unlock(&audio_lock);
        return TRUE;
    }
    switch (GST_MESSAGE_TYPE(message)) {
    case GST_MESSAGE_ERROR: {
        GError *err = NULL;
        gchar *debug = NULL;
        gst_message_parse_error (message, &err, &debug);
        logger_log(logger, LOGGER_INFO, "GStreamer error (audio): %s %s", GST_MESSAGE_SRC_NAME(message),err->message);
        g_error_free(err);
        g_free(debug);
        event_renderer->needs_reset = TRUE;
        event_renderer->healthy_since = 0;
        event_renderer->last_health_message = 0;
        gst_element_set_state(event_renderer->pipeline, GST_STATE_READY);
        if (event_renderer == renderer) gst_audio_pipeline_base_time = GST_CLOCK_TIME_NONE;
        /* This is a local audio recovery. Quitting the shared receiver main loop
         * would tear down the healthy video connection before recovery can run. */
	break;
    }
    case GST_MESSAGE_EOS:
        logger_log(logger, LOGGER_INFO, "GStreamer: End-Of-Stream (audio)");
        event_renderer->needs_reset = TRUE;
        event_renderer->healthy_since = 0;
        event_renderer->last_health_message = 0;
        break;
    case GST_MESSAGE_ELEMENT:
        audio_renderer_observe_health(event_renderer, message);
        break;
    default:
        /* unhandled message */
        logger_log(logger, LOGGER_DEBUG,"GStreamer unhandled audio bus message: src = %s type = %s",
                   GST_MESSAGE_SRC_NAME(message), GST_MESSAGE_TYPE_NAME(message));
        break;
    }
    g_rec_mutex_unlock(&audio_lock);
    return TRUE;
}

unsigned int audio_renderer_listen(void *loop, int id) {
    g_assert(id >= 0 && id < NFORMATS);
    if (!renderer_type[id]) return 0;
    return (unsigned int) gst_bus_add_watch(renderer_type[id]->bus,(GstBusFunc)
                                            gstreamer_audio_pipeline_bus_callback, (gpointer) loop); 
}

#ifdef UXPLAY_AUDIO_LIFECYCLE_TESTS
GstElement *audio_renderer_test_ref_pipeline(void) {
    g_rec_mutex_lock(&audio_lock);
    GstElement *pipeline = renderer ? gst_object_ref(renderer->pipeline) : NULL;
    g_rec_mutex_unlock(&audio_lock);
    return pipeline;
}

uint64_t audio_renderer_test_base_time(void) {
    g_rec_mutex_lock(&audio_lock);
    uint64_t base = gst_audio_pipeline_base_time;
    g_rec_mutex_unlock(&audio_lock);
    return base;
}

unsigned int audio_renderer_test_recovery_attempts(void) {
    g_rec_mutex_lock(&audio_lock);
    unsigned int attempts = renderer ? renderer->recovery_attempts : 0;
    g_rec_mutex_unlock(&audio_lock);
    return attempts;
}

bool audio_renderer_test_needs_reset(void) {
    g_rec_mutex_lock(&audio_lock);
    bool needs_reset = renderer && renderer->needs_reset;
    g_rec_mutex_unlock(&audio_lock);
    return needs_reset;
}
#endif
