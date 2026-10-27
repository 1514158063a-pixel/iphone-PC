#include <gst/gst.h>
#include <gst/app/gstappsrc.h>
#include <atomic>
#include <chrono>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>
#include "audio_renderer.h"
#include "test_report.h"

namespace {
std::mutex log_mutex;
std::vector<std::string> error_logs;
std::atomic<unsigned int> handoffs{0};
logger_t* fixture_logger = nullptr;
unsigned int healthy_window_handoffs = 0;
long long healthy_window_milliseconds = 0;

void pump_context() {
    while (g_main_context_iteration(nullptr, FALSE)) {}
}
class ProductionBusWatch {
    unsigned int source_id = 0;
public:
    ProductionBusWatch() : source_id(audio_renderer_listen(nullptr, 3)) {
        require_true(source_id != 0, "Cannot install the production PCM bus callback");
    }
    ~ProductionBusWatch() { if (source_id) g_source_remove(source_id); }
};

void log_callback(void*, int level, const char* message) {
    if (level <= LOGGER_ERR) {
        std::lock_guard<std::mutex> lock(log_mutex);
        error_logs.emplace_back(message ? message : "");
    }
}
void glib_log_callback(const gchar* domain, GLogLevelFlags level, const gchar* message, gpointer) {
    if (level & (G_LOG_LEVEL_ERROR | G_LOG_LEVEL_CRITICAL)) {
        std::lock_guard<std::mutex> lock(log_mutex);
        error_logs.emplace_back(std::string(domain ? domain : "GLib") + ": " + (message ? message : ""));
    }
    g_log_default_handler(domain, level, message, nullptr);
}
void count_handoff(GstElement*, GstBuffer*, GstPad*, gpointer) { ++handoffs; }

void wait_for(const std::function<bool()>& predicate, const std::string& message,
              unsigned int timeout_ms = 2000) {
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        pump_context();
        if (predicate()) return;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    pump_context();
    require_true(predicate(), message);
}

GstElement* active_pipeline() {
    GstElement* pipeline = audio_renderer_test_ref_pipeline();
    require_true(pipeline != nullptr, "No active production audio pipeline");
    return pipeline;
}
void assert_no_bus_error(GstElement* pipeline) {
    GstBus* bus = gst_element_get_bus(pipeline);
    GstMessage* message = gst_bus_pop_filtered(bus, GST_MESSAGE_ERROR);
    gst_object_unref(bus);
    if (!message) return;
    GError* error = nullptr;
    gchar* details = nullptr;
    gst_message_parse_error(message, &error, &details);
    std::string text = error ? error->message : "Unknown GStreamer error";
    if (error) g_error_free(error);
    g_free(details);
    gst_message_unref(message);
    throw std::runtime_error(text);
}
void assert_playing_and_base(GstElement* pipeline) {
    GstState state = GST_STATE_VOID_PENDING, pending = GST_STATE_VOID_PENDING;
    GstStateChangeReturn changed = gst_element_get_state(pipeline, &state, &pending, 2 * GST_SECOND);
    assert_no_bus_error(pipeline);
    require_true(changed != GST_STATE_CHANGE_FAILURE && state == GST_STATE_PLAYING,
                 "Production pipeline did not reach PLAYING");
    GstClockTime actual = gst_element_get_base_time(pipeline);
    require_true(GST_CLOCK_TIME_IS_VALID(actual), "Actual pipeline base time is invalid");
    require_true(audio_renderer_test_base_time() == actual,
                 "Renderer cached base time differs from the running pipeline");
}
void hook_sink(GstElement* pipeline) {
    GstElement* sink = gst_bin_get_by_name(GST_BIN(pipeline), "audio_test_sink");
    require_true(sink != nullptr, "The silent fakesink is missing");
    if (!g_object_get_data(G_OBJECT(sink), "test-handoff-hook")) {
        g_signal_connect(sink, "handoff", G_CALLBACK(count_handoff), nullptr);
        g_object_set_data(G_OBJECT(sink), "test-handoff-hook", GINT_TO_POINTER(1));
    }
    gst_object_unref(sink);
}
void push_silence_and_wait(GstElement* pipeline, unsigned int milliseconds = 10) {
    hook_sink(pipeline);
    unsigned int previous = handoffs.load();
    GstClock* clock = gst_element_get_clock(pipeline);
    require_true(clock != nullptr, "The production pipeline has no clock");
    uint64_t timestamp = gst_clock_get_time(clock) + 10 * GST_MSECOND;
    gst_object_unref(clock);
    // Ordinary zero-filled stereo S16LE PCM: no encoded samples,
    // authentication data, speakers, or network service are involved.
    std::vector<unsigned char> silence(44100 * 2 * 2 * milliseconds / 1000, 0);
    int bytes = static_cast<int>(silence.size());
    unsigned short sequence = 1;
    audio_renderer_render_buffer(silence.data(), &bytes, &sequence, &timestamp);
    wait_for([&] { return handoffs.load() > previous; },
             "Valid silent PCM did not reach fakesink after state recovery");
    assert_no_bus_error(pipeline);
}
void configure_test_timestamps(GstElement* pipeline) {
    GstElement* source = gst_bin_get_by_name(GST_BIN(pipeline), "audio_source");
    require_true(source != nullptr, "The production appsrc is missing");
    // PCM's fixture path has sync=false. Let the real appsrc stamp this fixture's
    // ordinary silent buffers so actual level messages describe current output.
    g_object_set(source, "do-timestamp", TRUE, nullptr);
    gst_object_unref(source);
}
void end_stream_and_wait_for_recovery_state(GstElement* pipeline) {
    GstElement* source = gst_bin_get_by_name(GST_BIN(pipeline), "audio_source");
    require_true(source != nullptr, "The production appsrc is missing");
    GstFlowReturn accepted = gst_app_src_end_of_stream(GST_APP_SRC(source));
    gst_object_unref(source);
    require_true(accepted == GST_FLOW_OK, "The clean test EOS was not accepted");
    wait_for([] { return audio_renderer_test_needs_reset() != 0; },
             "The real downstream EOS did not reach the production bus callback");
}
void submit_silence_without_wait(GstElement* pipeline) {
    GstClock* clock = gst_element_get_clock(pipeline);
    require_true(clock != nullptr, "The production pipeline has no clock");
    uint64_t timestamp = gst_clock_get_time(clock) + 10 * GST_MSECOND;
    gst_object_unref(clock);
    unsigned char silence[1764]{};
    int length = sizeof(silence);
    unsigned short sequence = 1;
    audio_renderer_render_buffer(silence, &length, &sequence, &timestamp);
}
void initialize() {
    bool audio_sync = true, video_sync = true;
    audio_renderer_init(fixture_logger,
        "fakesink name=audio_test_sink signal-handoffs=true async=false",
        &audio_sync, &video_sync, "");
}
void choose(unsigned char type) { audio_renderer_start(&type); }
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    TestReport report;
    fixture_logger = logger_init();
    logger_set_callback(fixture_logger, log_callback, nullptr);
    logger_set_level(fixture_logger, LOGGER_INFO);
    GLogFunc previous_glib_handler = g_log_set_default_handler(glib_log_callback, nullptr);
    report.check("gstreamer.required-features", [] {
        require_true(gstreamer_init(), "Required production GStreamer plugins are missing");
    });
    report.check("renderer.initialise-without-active-format", [] {
        initialize();
        GstElement* active = audio_renderer_test_ref_pipeline();
        if (active) gst_object_unref(active);
        require_true(active == nullptr, "Init unexpectedly selects an audio format");
    });
    report.check("aac.same-format-start-restores-playing-and-base", [] {
        choose(8);
        GstElement* pipeline = active_pipeline();
        assert_playing_and_base(pipeline);
        uint64_t before_pause = gst_element_get_base_time(pipeline);
        gst_element_set_state(pipeline, GST_STATE_PAUSED);
        GstState state, pending;
        gst_element_get_state(pipeline, &state, &pending, GST_SECOND);
        require_true(state == GST_STATE_PAUSED, "The test pause did not complete");
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
        choose(8);
        assert_playing_and_base(pipeline);
        require_true(gst_element_get_base_time(pipeline) > before_pause,
                     "Resumed pipeline did not refresh its paused base time");
        gst_object_unref(pipeline);
    });
    report.check("aac.healthy-repeated-setup-preserves-pipeline-and-base", [] {
        GstElement* original = active_pipeline();
        assert_playing_and_base(original);
        uint64_t original_base = gst_element_get_base_time(original);
        for (int cycle = 0; cycle < 10; ++cycle) {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            choose(8);
            GstElement* current = active_pipeline();
            assert_playing_and_base(current);
            require_true(current == original, "Healthy repeated SETUP replaced the active pipeline");
            require_true(gst_element_get_base_time(current) == original_base,
                         "Healthy repeated SETUP disturbed the running audio timeline");
            gst_object_unref(current);
        }
        gst_object_unref(original);
    });
    report.check("pcm.first-valid-silent-buffer-flows", [] {
        choose(1);
        GstElement* pipeline = active_pipeline();
        assert_playing_and_base(pipeline);
        push_silence_and_wait(pipeline);
        gst_object_unref(pipeline);
    });
    report.check("pcm.same-format-start-is-repeatable", [] {
        for (int cycle = 0; cycle < 10; ++cycle) {
            choose(1);
            GstElement* pipeline = active_pipeline();
            assert_playing_and_base(pipeline);
            push_silence_and_wait(pipeline);
            gst_object_unref(pipeline);
        }
    });
    report.check("pcm.stop-start-clears-eos-ten-cycles", [] {
        for (int cycle = 0; cycle < 10; ++cycle) {
            audio_renderer_stop();
            GstElement* stopped = audio_renderer_test_ref_pipeline();
            if (stopped) gst_object_unref(stopped);
            require_true(stopped == nullptr, "Stop retained an active renderer");
            choose(1);
            GstElement* pipeline = active_pipeline();
            assert_playing_and_base(pipeline);
            push_silence_and_wait(pipeline);
            gst_object_unref(pipeline);
        }
    });
    report.check("pcm.flush-clears-eos-and-preserves-flow-ten-cycles", [] {
        for (int cycle = 0; cycle < 10; ++cycle) {
            GstElement* pipeline = active_pipeline();
            GstElement* source = gst_bin_get_by_name(GST_BIN(pipeline), "audio_source");
            require_true(source != nullptr, "The production appsrc is missing");
            require_true(gst_app_src_end_of_stream(GST_APP_SRC(source)) == GST_FLOW_OK,
                         "The clean test EOS was not accepted");
            gst_object_unref(source);
            audio_renderer_flush();
            assert_playing_and_base(pipeline);
            push_silence_and_wait(pipeline);
            gst_object_unref(pipeline);
        }
    });
    report.check("pcm.automatic-recovery-rearms-after-real-downstream-health-window", [] {
        choose(1);
        GstElement* pipeline = active_pipeline();
        configure_test_timestamps(pipeline);
        ProductionBusWatch watch;
        push_silence_and_wait(pipeline);
        end_stream_and_wait_for_recovery_state(pipeline);
        push_silence_and_wait(pipeline);
        require_true(audio_renderer_test_recovery_attempts() == 1,
                     "The first genuine EOS did not consume exactly one automatic recovery attempt");
        auto began = std::chrono::steady_clock::now();
        auto deadline = began + std::chrono::seconds(6);
        unsigned int previous_handoffs = handoffs.load();
        while (audio_renderer_test_recovery_attempts() != 0 && std::chrono::steady_clock::now() < deadline) {
            auto cycle_began = std::chrono::steady_clock::now();
            // 100 ms per 100 ms avoids Windows short-sleep granularity starving
            // the stream and exercises actual sustained downstream progress.
            push_silence_and_wait(pipeline, 100);
            pump_context();
            std::this_thread::sleep_until(cycle_began + std::chrono::milliseconds(100));
        }
        healthy_window_handoffs = handoffs.load() - previous_handoffs;
        healthy_window_milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - began).count();
        require_true(audio_renderer_test_recovery_attempts() == 0 && healthy_window_handoffs >= 25
                     && healthy_window_milliseconds >= 2900,
                     "Real downstream level/PCM activity did not replenish recovery after its health window");
        end_stream_and_wait_for_recovery_state(pipeline);
        push_silence_and_wait(pipeline);
        require_true(audio_renderer_test_recovery_attempts() == 1 && !audio_renderer_test_needs_reset(),
                     "A later independent EOS could not recover after the healthy output window");
        assert_playing_and_base(pipeline);
        gst_object_unref(pipeline);
    });
    report.check("pcm.repeated-eos-without-healthy-output-does-not-loop-rebuild", [] {
        choose(1);
        GstElement* pipeline = active_pipeline();
        configure_test_timestamps(pipeline);
        ProductionBusWatch watch;
        push_silence_and_wait(pipeline);
        end_stream_and_wait_for_recovery_state(pipeline);
        push_silence_and_wait(pipeline);
        require_true(audio_renderer_test_recovery_attempts() == 1,
                     "The first automatic recovery did not consume its bounded attempt");
        end_stream_and_wait_for_recovery_state(pipeline);
        uint64_t base = gst_element_get_base_time(pipeline);
        unsigned int before = handoffs.load();
        for (int retry = 0; retry < 5; ++retry) {
            submit_silence_without_wait(pipeline);
            pump_context();
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        require_true(audio_renderer_test_recovery_attempts() == 1 && audio_renderer_test_needs_reset()
                     && gst_element_get_base_time(pipeline) == base && handoffs.load() == before,
                     "Repeated EOS without a healthy window triggered unbounded automatic rebuilds");
        audio_renderer_flush();
        push_silence_and_wait(pipeline);
        assert_playing_and_base(pipeline);
        gst_object_unref(pipeline);
    });
    report.check("renderer.stop-and-destroy-are-idempotent", [] {
        audio_renderer_stop();
        audio_renderer_stop();
        audio_renderer_flush();
        audio_renderer_destroy();
        audio_renderer_destroy();
        GstElement* active = audio_renderer_test_ref_pipeline();
        if (active) gst_object_unref(active);
        require_true(active == nullptr, "Destroy retained an active renderer");
    });
    report.check("renderer.reinitialize-after-destroy", [] {
        for (int cycle = 0; cycle < 3; ++cycle) {
            initialize();
            choose(1);
            GstElement* pipeline = active_pipeline();
            assert_playing_and_base(pipeline);
            push_silence_and_wait(pipeline);
            gst_object_unref(pipeline);
            audio_renderer_destroy();
        }
    });
    report.check("renderer.no-native-error-events", [] {
        std::lock_guard<std::mutex> lock(log_mutex);
        require_true(error_logs.empty(), error_logs.empty() ? "" : error_logs.front());
    });
    audio_renderer_destroy();
    logger_destroy(fixture_logger);
    g_log_set_default_handler(previous_glib_handler, nullptr);
    std::ostringstream observed;
    observed << "{\"silentPcmHandoffs\": " << handoffs.load()
             << ", \"healthyWindowPcmHandoffs\": " << healthy_window_handoffs
             << ", \"healthyWindowMilliseconds\": " << healthy_window_milliseconds
             << ", \"nativeErrorLogs\": " << error_logs.size()
             << ", \"testFormats\": 4, \"releaseFormatsUnchanged\": true,"
             << " \"sink\": \"fakesink\", \"speakerOutputUsed\": false}";
    report.write(argv[1], "production audio renderer lifecycle", observed.str(), {
        "PCM is enabled only by the explicit test compilation macro; Release retains its normal format count.",
        "AAC state recovery is checked without creating encoded AAC samples; iPhone AAC decoding and audible A/V sync need a real client test.",
        "The fakesink handoff measures buffer delivery, not physical speaker latency.",
        "No real receiver, AirPlay socket, device volume or network adapter setting is used."
    });
    return report.passed() ? 0 : 1;
}
