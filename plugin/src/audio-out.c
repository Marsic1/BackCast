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

#define COBJMACROS
#define INITGUID
#include "audio-out.h"

#include <obs.h>
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <propkey.h>
#include <propsys.h>
#include <functiondiscoverykeys_devpkey.h>
#include <objbase.h>
#include <stdlib.h>
#include <string.h>

/* IIDs (plain C — no __uuidof) */
static const CLSID bc_CLSID_MMDeviceEnumerator = {
	0xbcde0395, 0xe52f, 0x467c, {0x8e, 0x3d, 0xc4, 0x57, 0x92, 0x91, 0x69, 0x2e}};
static const IID bc_IID_IMMDeviceEnumerator = {
	0xa95664d2, 0x9614, 0x4f35, {0xa7, 0x46, 0xde, 0x8d, 0xb6, 0x36, 0x17, 0xe6}};
static const IID bc_IID_IAudioClient = {
	0x1cb9ad4c, 0xdbfa, 0x4c32, {0xb1, 0x78, 0xc2, 0xf5, 0x68, 0xa7, 0x03, 0xb2}};
static const IID bc_IID_IAudioRenderClient = {
	0xf294acfc, 0x3146, 0x4483, {0xa7, 0xbf, 0xad, 0xdc, 0xa7, 0xc2, 0x60, 0xe2}};

/* interleaved stereo float, 48 kHz */
#define BC_CHANNELS 2
#define BC_RATE 48000
/* ring capacity: power of two (ring math masks), ~0.68 s; overflow drops
 * (live audio — old samples are worthless) */
#define BC_RING_FRAMES 32768

struct bc_audio {
	HANDLE thread;
	HANDLE stop_event;
	HANDLE buffer_event; /* WASAPI event */
	volatile LONG running;

	/* ring buffer (frame = 2 floats) */
	CRITICAL_SECTION ring_lock;
	float *ring;
	size_t head, tail; /* frame indices, wrapped by capacity */

	char *endpoint_id; /* UTF-8, malloc'd */

	IAudioClient *client;
	IAudioRenderClient *render;
	UINT32 buffer_frames;

};

/* ---- helpers ---- */

static wchar_t *utf8_to_wide(const char *s)
{
	int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
	if (n <= 0)
		return NULL;
	wchar_t *w = malloc((size_t)n * sizeof(wchar_t));
	MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
	return w;
}

static char *wide_to_utf8(const wchar_t *w)
{
	int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
	if (n <= 0)
		return NULL;
	char *s = malloc((size_t)n);
	WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
	return s;
}

static char *endpoint_id_of(IMMDevice *dev)
{
	wchar_t *wid = NULL;
	if (FAILED(IMMDevice_GetId(dev, &wid)))
		return NULL;
	char *id = wide_to_utf8(wid);
	CoTaskMemFree(wid);
	return id;
}

static char *endpoint_name_of(IMMDevice *dev)
{
	IPropertyStore *ps = NULL;
	if (FAILED(IMMDevice_OpenPropertyStore(dev, STGM_READ, &ps)))
		return NULL;
	PROPVARIANT pv;
	PropVariantInit(&pv);
	char *name = NULL;
	if (SUCCEEDED(IPropertyStore_GetValue(ps, &PKEY_Device_FriendlyName, &pv)) &&
	    pv.vt == VT_LPWSTR) {
		name = wide_to_utf8(pv.pwszVal);
		PropVariantClear(&pv);
	}
	IPropertyStore_Release(ps);
	return name;
}

/* ---- endpoint enumeration / picking ---- */

int bca_enum_endpoints(char ***ids, char ***names)
{
	*ids = NULL;
	*names = NULL;
	HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
	bool uninit = SUCCEEDED(hr);

	IMMDeviceEnumerator *en = NULL;
	hr = CoCreateInstance(&bc_CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL,
			      &bc_IID_IMMDeviceEnumerator, (void **)&en);
	if (FAILED(hr)) {
		if (uninit)
			CoUninitialize();
		return 0;
	}

	IMMDeviceCollection *col = NULL;
	hr = IMMDeviceEnumerator_EnumAudioEndpoints(en, eRender, DEVICE_STATE_ACTIVE, &col);
	if (FAILED(hr)) {
		IMMDeviceEnumerator_Release(en);
		if (uninit)
			CoUninitialize();
		return 0;
	}

	UINT count = 0;
	IMMDeviceCollection_GetCount(col, &count);
	if (count == 0) {
		IMMDeviceCollection_Release(col);
		IMMDeviceEnumerator_Release(en);
		if (uninit)
			CoUninitialize();
		return 0;
	}

	char **oid = calloc(count, sizeof(char *));
	char **onm = calloc(count, sizeof(char *));
	int n = 0;
	for (UINT i = 0; i < count; i++) {
		IMMDevice *dev = NULL;
		if (FAILED(IMMDeviceCollection_Item(col, i, &dev)))
			continue;
		char *id = endpoint_id_of(dev);
		char *nm = endpoint_name_of(dev);
		if (id) {
			oid[n] = id;
			onm[n] = nm ? nm : _strdup(id);
			n++;
		} else {
			free(nm);
		}
		IMMDevice_Release(dev);
	}
	IMMDeviceCollection_Release(col);
	IMMDeviceEnumerator_Release(en);
	if (uninit)
		CoUninitialize();
	*ids = oid;
	*names = onm;
	return n;
}

char *bca_pick_spare_endpoint(void)
{
	char **ids = NULL, **names = NULL;
	int n = bca_enum_endpoints(&ids, &names);

	/* defaults to exclude */
	char *def_console = NULL, *def_comm = NULL;
	IMMDeviceEnumerator *en = NULL;
	HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
	bool uninit = SUCCEEDED(hr);
	if (SUCCEEDED(CoCreateInstance(&bc_CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL,
				       &bc_IID_IMMDeviceEnumerator, (void **)&en))) {
		IMMDevice *d = NULL;
		if (SUCCEEDED(IMMDeviceEnumerator_GetDefaultAudioEndpoint(en, eRender, eConsole, &d))) {
			def_console = endpoint_id_of(d);
			IMMDevice_Release(d);
		}
		if (SUCCEEDED(IMMDeviceEnumerator_GetDefaultAudioEndpoint(en, eRender, eCommunications, &d))) {
			def_comm = endpoint_id_of(d);
			IMMDevice_Release(d);
		}
		IMMDeviceEnumerator_Release(en);
	}
	if (uninit)
		CoUninitialize();

	static const char *markers[] = {"TV", "HDMI", "Digital", "DisplayPort", "SPDIF",
					"Dummy", "Monitor", "CABLE", "Virtual"};
	char *best = NULL;
	int best_score = -1;
	for (int i = 0; i < n; i++) {
		if (def_console && strcmp(ids[i], def_console) == 0)
			continue;
		if (def_comm && strcmp(ids[i], def_comm) == 0)
			continue;
		int score = 0;
		for (int m = 0; m < (int)(sizeof(markers) / sizeof(markers[0])); m++)
			if (strstr(names[i], markers[m]))
				score++;
		if (score > best_score) {
			best_score = score;
			free(best);
			best = _strdup(ids[i]);
		}
	}

	for (int i = 0; i < n; i++) {
		free(ids[i]);
		free(names[i]);
	}
	free(ids);
	free(names);
	free(def_console);
	free(def_comm);
	return best;
}

char *bca_current_endpoint(const struct bc_audio *a)
{
	return a && a->endpoint_id ? _strdup(a->endpoint_id) : NULL;
}

/* ---- ring ---- */

static size_t ring_used(const struct bc_audio *a)
{
	return (a->head - a->tail) & (BC_RING_FRAMES - 1);
}

void bca_write(struct bc_audio *a, const float *data, size_t frames)
{
	if (!a || !a->running)
		return;
	EnterCriticalSection(&a->ring_lock);
	size_t space = BC_RING_FRAMES - 1 - ring_used(a);
	size_t write = frames < space ? frames : space;
	for (size_t i = 0; i < write; i++) {
		size_t pos = (a->head + i) & (BC_RING_FRAMES - 1);
		a->ring[pos * 2] = data[i * 2];
		a->ring[pos * 2 + 1] = data[i * 2 + 1];
	}
	a->head = (a->head + write) & (BC_RING_FRAMES - 1);
	LeaveCriticalSection(&a->ring_lock);
}

/* ---- render thread ---- */

static bool ring_read(struct bc_audio *a, float *dst, size_t frames)
{
	bool got_any = false;
	EnterCriticalSection(&a->ring_lock);
	size_t avail = ring_used(a);
	size_t read = frames < avail ? frames : avail;
	for (size_t i = 0; i < read; i++) {
		size_t pos = (a->tail + i) & (BC_RING_FRAMES - 1);
		dst[i * 2] = a->ring[pos * 2];
		dst[i * 2 + 1] = a->ring[pos * 2 + 1];
	}
	a->tail = (a->tail + read) & (BC_RING_FRAMES - 1);
	got_any = read > 0;
	LeaveCriticalSection(&a->ring_lock);
	return got_any;
}

static DWORD WINAPI render_thread(LPVOID param)
{
	struct bc_audio *a = param;

	HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
	bool com = SUCCEEDED(hr);

	IMMDeviceEnumerator *en = NULL;
	IMMDevice *dev = NULL;

	hr = CoCreateInstance(&bc_CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL,
			      &bc_IID_IMMDeviceEnumerator, (void **)&en);
	if (FAILED(hr)) {
		blog(LOG_ERROR, "[bca] CoCreateInstance(enumerator) failed: 0x%08lX", (unsigned long)hr);
		goto done;
	}

	if (a->endpoint_id) {
		wchar_t *wid = utf8_to_wide(a->endpoint_id);
		if (wid) {
			IMMDeviceEnumerator_GetDevice(en, wid, &dev);
			free(wid);
		}
	}
	if (!dev) {
		/* configured endpoint gone: fall back to default */
		IMMDeviceEnumerator_GetDefaultAudioEndpoint(en, eRender, eConsole, &dev);
	}
	if (!dev) {
		blog(LOG_ERROR, "[bca] no audio device found");
		goto done;
	}

	hr = IMMDevice_Activate(dev, &bc_IID_IAudioClient, CLSCTX_ALL, NULL, (void **)&a->client);
	if (FAILED(hr)) {
		blog(LOG_ERROR, "[bca] Activate(IAudioClient) failed: 0x%08lX", (unsigned long)hr);
		goto done;
	}

	/* shared mode, event-driven, mix format (shared mode requires it) */
	WAVEFORMATEX *mix = NULL;
	hr = IAudioClient_GetMixFormat(a->client, &mix);
	if (FAILED(hr)) {
		blog(LOG_ERROR, "[bca] GetMixFormat failed: 0x%08lX", (unsigned long)hr);
		IAudioClient_Release(a->client);
		a->client = NULL;
		goto done;
	}
	bool format_ok = mix->nChannels == BC_CHANNELS && mix->nSamplesPerSec == BC_RATE &&
			 (mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
			  (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mix->cbSize >= 22 &&
			   ((WAVEFORMATEXTENSIBLE *)mix)->SubFormat.Data1 == 0x00000003));
	if (!format_ok)
		blog(LOG_WARNING, "[bca] unexpected mix format: %u ch %u Hz tag %u — continuing anyway",
		     mix->nChannels, mix->nSamplesPerSec, mix->wFormatTag);

	hr = IAudioClient_Initialize(a->client, AUDCLNT_SHAREMODE_SHARED,
				     AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200000, 0,
				     mix, NULL);
	CoTaskMemFree(mix);
	if (FAILED(hr)) {
		blog(LOG_ERROR, "[bca] IAudioClient_Initialize failed: 0x%08lX", (unsigned long)hr);
		IAudioClient_Release(a->client);
		a->client = NULL;
		goto done;
	}

	REFERENCE_TIME def_period = 0, min_period = 0;
	IAudioClient_GetDevicePeriod(a->client, &def_period, &min_period);

	UINT32 buf_frames = 0;
	IAudioClient_GetBufferSize(a->client, &buf_frames);
	a->buffer_frames = buf_frames;

	a->buffer_event = CreateEventW(NULL, FALSE, FALSE, NULL);
	if (!a->buffer_event) {
		blog(LOG_ERROR, "[bca] CreateEvent failed");
		goto done;
	}
	if (FAILED(IAudioClient_SetEventHandle(a->client, a->buffer_event))) {
		blog(LOG_ERROR, "[bca] SetEventHandle failed");
		goto done;
	}
	if (FAILED(IAudioClient_GetService(a->client, &bc_IID_IAudioRenderClient,
					   (void **)&a->render))) {
		blog(LOG_ERROR, "[bca] GetService(IAudioRenderClient) failed");
		goto done;
	}

	blog(LOG_INFO, "[bca] WASAPI stream up: %u frames buffer", buf_frames);

	/* pre-fill silence so the stream starts flowing */
	{
		BYTE *ptr = NULL;
		if (SUCCEEDED(IAudioRenderClient_GetBuffer(a->render, buf_frames, &ptr))) {
			memset(ptr, 0, (size_t)buf_frames * BC_CHANNELS * sizeof(float));
			IAudioRenderClient_ReleaseBuffer(a->render, buf_frames, 0);
		}
	}
	IAudioClient_Start(a->client);

	float *chunk = malloc(16384 * BC_CHANNELS * sizeof(float));

	HANDLE waits[2] = {a->stop_event, a->buffer_event};
	while (WaitForMultipleObjects(2, waits, FALSE, 2000) == WAIT_OBJECT_0 + 1) {
		UINT32 padding = 0;
		if (FAILED(IAudioClient_GetCurrentPadding(a->client, &padding)))
			break;
		UINT32 need = padding < a->buffer_frames ? a->buffer_frames - padding : 0;
		while (need > 0) {
			UINT32 take = need < 16384 ? need : 16384;
			bool got = ring_read(a, chunk, take);
			UINT32 frames = got ? take : 0;
			BYTE *ptr = NULL;
			if (frames > 0 &&
			    SUCCEEDED(IAudioRenderClient_GetBuffer(a->render, frames, &ptr))) {
				memcpy(ptr, chunk, (size_t)frames * BC_CHANNELS * sizeof(float));
				IAudioRenderClient_ReleaseBuffer(a->render, frames, 0);
				need -= frames;
			} else if (!got) {
				break; /* underrun: write nothing, wait for data */
			}
		}
	}

	free(chunk);
	IAudioClient_Stop(a->client);

done:
	if (a->render) {
		IAudioRenderClient_Release(a->render);
		a->render = NULL;
	}
	if (a->client) {
		IAudioClient_Release(a->client);
		a->client = NULL;
	}
	if (dev)
		IMMDevice_Release(dev);
	if (en)
		IMMDeviceEnumerator_Release(en);
	if (com)
		CoUninitialize();
	return 0;
}

/* ---- lifecycle ---- */

struct bc_audio *bca_start(const char *endpoint_id)
{
	struct bc_audio *a = calloc(1, sizeof(*a));
	a->ring = malloc(BC_RING_FRAMES * BC_CHANNELS * sizeof(float));
	InitializeCriticalSection(&a->ring_lock);
	a->stop_event = CreateEventW(NULL, TRUE, FALSE, NULL);
	if (endpoint_id && *endpoint_id)
		a->endpoint_id = _strdup(endpoint_id);

	a->running = 1;
	a->thread = CreateThread(NULL, 0, render_thread, a, 0, NULL);
	return a;
}

void bca_stop(struct bc_audio *a)
{
	if (!a)
		return;
	a->running = 0;
	SetEvent(a->stop_event);
	if (a->thread) {
		WaitForSingleObject(a->thread, 3000);
		CloseHandle(a->thread);
	}
	if (a->buffer_event)
		CloseHandle(a->buffer_event);
	CloseHandle(a->stop_event);
	DeleteCriticalSection(&a->ring_lock);
	free(a->ring);
	free(a->endpoint_id);
	free(a);
}
