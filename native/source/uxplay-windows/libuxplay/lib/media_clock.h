/* Shared audio/video presentation mapping. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef UXPLAY_MEDIA_CLOCK_H
#define UXPLAY_MEDIA_CLOCK_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ntp_local_ns == 0 means no validated timing sample is available yet. */
uint64_t media_clock_map(uint64_t remote_ns, uint64_t ntp_local_ns, uint64_t fallback_local_now_ns);
void media_clock_adjust(uint64_t presentation_delta_ns);
void media_clock_reset(void);
#ifdef __cplusplus
}
#endif
#endif
