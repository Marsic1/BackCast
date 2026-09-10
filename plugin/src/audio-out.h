/*
Backcast Projector — master-mix WASAPI renderer.
Copyright (C) 2026 Marsic1

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License along
with this program. If not, see <https://www.gnu.org/licenses/>
*/

#pragma once

#include <stddef.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Renders interleaved float stereo 48 kHz to a chosen render endpoint.
 * The output lives inside the OBS process: Discord window-share hooks
 * this process's WASAPI renders, so the shared window carries the audio. */

struct bc_audio;

/* Start rendering. endpoint_id = WASAPI device id (UTF-8), or NULL to
 * auto-pick a non-default endpoint. Returns NULL on failure. */
struct bc_audio *bca_start(const char *endpoint_id);

/* Full teardown: stops the stream, joins the render thread. */
void bca_stop(struct bc_audio *a);

/* Feed interleaved stereo float samples (48 kHz). Non-blocking; drops
 * on overflow (live stream — old audio is worthless). */
void bca_write(struct bc_audio *a, const float *data, size_t frames);

/* Number of render endpoints; fills id/name (UTF-8, malloc'd) for each. */
int bca_enum_endpoints(char ***ids, char ***names);

/* Auto-pick: first render endpoint that is neither the default console
 * nor the default communication device (prefers TV/HDMI/Digital names).
 * Returns a malloc'd UTF-8 id, or NULL when none spare exists. */
char *bca_pick_spare_endpoint(void);

/* Id of the endpoint actually in use (malloc'd UTF-8). */
char *bca_current_endpoint(const struct bc_audio *a);

/* Discord sound-share detection: true while our render client's
 * ReleaseBuffer appears hooked. */
bool bca_render_hooked(struct bc_audio *a);

/* Calibration helper: current first 16 bytes of ReleaseBuffer. */
void bca_render_hook_bytes(const struct bc_audio *a, unsigned char out[16]);

#ifdef __cplusplus
}
#endif
