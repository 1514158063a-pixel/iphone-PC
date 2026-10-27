/* Shared audio/video presentation mapping. SPDX-License-Identifier: GPL-3.0-or-later */
#include "media_clock.h"
#include <stdbool.h>
#include <pthread.h>

static pthread_mutex_t mapping_mutex = PTHREAD_MUTEX_INITIALIZER;
static bool have_fallback = false;
static uint64_t fallback_offset = 0;
static uint64_t presentation_adjustment = 0;

uint64_t media_clock_map(uint64_t remote_ns, uint64_t ntp_local_ns, uint64_t fallback_local_now_ns) {
    pthread_mutex_lock(&mapping_mutex);
    uint64_t mapped;
    if (ntp_local_ns) {
        mapped = ntp_local_ns;
    } else {
        if (!have_fallback) {
            /* Unsigned subtraction/addition preserves signed offsets modulo 2^64. */
            fallback_offset = fallback_local_now_ns - remote_ns;
            have_fallback = true;
        }
        mapped = remote_ns + fallback_offset;
    }
    mapped += presentation_adjustment;
    pthread_mutex_unlock(&mapping_mutex);
    return mapped;
}

void media_clock_adjust(uint64_t presentation_delta_ns) {
    pthread_mutex_lock(&mapping_mutex);
    presentation_adjustment += presentation_delta_ns;
    pthread_mutex_unlock(&mapping_mutex);
}

void media_clock_reset(void) {
    pthread_mutex_lock(&mapping_mutex);
    have_fallback = false;
    fallback_offset = 0;
    presentation_adjustment = 0;
    pthread_mutex_unlock(&mapping_mutex);
}
