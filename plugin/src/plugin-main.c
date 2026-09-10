/*
Backcast Projector — near-zero-latency program output window for OBS.
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

#include <obs-module.h>
#include <obs-frontend-api.h>
#include <obs-config.h>
#include <util/platform.h>
#include <media-io/audio-io.h>
#include <windows.h>
#include <windowsx.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <math.h>
#include <string.h>
#include "plugin-support.h"
#include "audio-out.h"

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE(PLUGIN_NAME, "en-US")

/* ------------------------------------------------------------------ */
/* state — everything below exists ONLY while the window is open      */
/* ------------------------------------------------------------------ */

#define WM_APP_REFIT (WM_APP + 1)

/* ---- WebStage palette (matches the Backcast exe UI) ---- */
#define BC_RGB(r, g, b) ((COLORREF)((r) | ((g) << 8) | ((b) << 16)))
static const COLORREF ColPanel = BC_RGB(21, 26, 29);    /* #151A1D */
static const COLORREF ColHover = BC_RGB(34, 43, 48);    /* #222B30 */
static const COLORREF ColFg = BC_RGB(232, 236, 236);    /* #E8ECEC */
static const COLORREF ColGray = BC_RGB(139, 150, 152);  /* #8B9698 */
static const COLORREF ColSep = BC_RGB(44, 53, 58);      /* #2C353A */
static const COLORREF ColAccent = BC_RGB(61, 220, 132); /* #3DDC84 */

#define HEADER_H 40
#define HOVER_ZONE 100       /* cursor this close to the top reveals the header */
#define HOVER_POLL_MS 60
#define REHIDE_MS 700        /* grace after the cursor leaves the zone */
#define TIMER_HOVER 1
#define TIMER_REHIDE 2
#define IDR_APPICON 1

static struct {
	bool open;
	HWND window;   /* top-level frame */
	HWND render;   /* child HWND the obs display renders into (full client) */
	HWND header;   /* overlay child: hover-revealed control strip above the video */
	obs_display_t *display;
	struct bc_audio *audio; /* master-mix WASAPI renderer */
	uint32_t last_canvas_w, last_canvas_h;
	wchar_t title[128];
	wchar_t branded_title[256]; /* "<name> — BackCast · <state>" */
	bool live;      /* share/stream active → LIVE pill */
	bool pulse;     /* OFF-AIR LED pulse phase */
	bool hover_pin, hover_min, hover_close; /* header button hover */
	bool menu_tracking;
	bool header_shown; /* header overlay currently revealed (clean share when false) */
	bool rehide_pending;
} bcp;

static void bcp_stop(void);
static void bcp_teardown(bool user_closed);
static void open_context_menu(HWND hwnd, POINT pt);

/* master mix tap: OBS converts to interleaved float stereo 48 kHz for us */
static void audio_cb(void *param, size_t mix_idx, struct audio_data *data)
{
	UNUSED_PARAMETER(param);
	UNUSED_PARAMETER(mix_idx);
	if (bcp.audio && data->frames)
		bca_write(bcp.audio, (const float *)data->data[0], data->frames);
}

/* ---- config (portable-aware module config path) ------------------- */

static void config_path(char *dst, size_t dst_size)
{
	char *p = obs_module_config_path("config.json");
	if (!p) {
		dst[0] = 0;
		return;
	}
	strcpy_s(dst, dst_size, p);
	bfree(p);
}

static obs_data_t *cfg_load(void)
{
	char path[512];
	config_path(path, sizeof(path));
	obs_data_t *d = obs_data_create_from_json_file_safe(path, ".bak");
	return d ? d : obs_data_create();
}

static void cfg_save(obs_data_t *d)
{
	char path[512];
	config_path(path, sizeof(path));

	/* create the config dir tree if it does not exist yet (os_mkdirs
	 * handles the forward-slash relative paths libobs produces) */
	char dir[512];
	strcpy_s(dir, sizeof(dir), path);
	char *slash = strrchr(dir, '/');
	if (slash) {
		*slash = 0;
		os_mkdirs(dir);
	}
	obs_data_save_json_safe(d, path, ".tmp", ".bak");
}

/* obs_data getters take no default — wrap with has_user_value checks */
static long long cfg_get_int(obs_data_t *d, const char *name, long long def)
{
	return obs_data_has_user_value(d, name) ? obs_data_get_int(d, name) : def;
}

static bool cfg_get_bool(obs_data_t *d, const char *name, bool def)
{
	return obs_data_has_user_value(d, name) ? obs_data_get_bool(d, name) : def;
}

/* ------------------------------------------------------------------ */
/* rendering — draw callback runs on the graphics thread               */
/* ------------------------------------------------------------------ */

static void draw_cb(void *param, uint32_t cx, uint32_t cy)
{
	UNUSED_PARAMETER(param);

	struct obs_video_info ovi;
	if (!obs_get_video_info(&ovi))
		return;

	/* canvas aspect change: ask the UI thread to refit the window
	 * (keep window area, adjust to the new aspect) */
	if (bcp.last_canvas_w != ovi.base_width || bcp.last_canvas_h != ovi.base_height) {
		bcp.last_canvas_w = ovi.base_width;
		bcp.last_canvas_h = ovi.base_height;
		if (bcp.window)
			PostMessage(bcp.window, WM_APP_REFIT, 0, 0);
	}

	uint32_t cw = ovi.base_width, ch = ovi.base_height;
	if (!cw || !ch || !cx || !cy)
		return;

	/* letterbox the canvas into the render child */
	float win_aspect = (float)cx / (float)cy;
	float src_aspect = (float)cw / (float)ch;
	uint32_t dw, dh;
	if (win_aspect > src_aspect) {
		dh = cy;
		dw = (uint32_t)((float)cy * src_aspect);
	} else {
		dw = cx;
		dh = (uint32_t)((float)cx / src_aspect);
	}
	uint32_t dx = (cx - dw) / 2, dy = (cy - dh) / 2;

	/* OBS renders its textures Y-down: top=0, bottom=height */
	gs_ortho(0.0f, (float)cw, 0.0f, (float)ch, -100.0f, 100.0f);
	gs_set_viewport(dx, dy, dw, dh);
	obs_render_main_texture();
	gs_set_viewport(0, 0, cx, cy);
}

/* ------------------------------------------------------------------ */
/* header layout / painting                                            */
/* ------------------------------------------------------------------ */

/* module handle of THIS dll — GetModuleHandle(NULL) would return obs64.exe,
 * where our icon resource does not live */
static HMODULE bcp_module(void)
{
	HMODULE mod = NULL;
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
				   GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			   (LPCWSTR)&bcp_module, &mod);
	return mod;
}

static void header_buttons(int client_w, RECT *pin, RECT *min, RECT *close)
{
	SetRect(pin, client_w - 132, 0, client_w - 88, HEADER_H);
	SetRect(min, client_w - 88, 0, client_w - 44, HEADER_H);
	SetRect(close, client_w - 44, 0, client_w, HEADER_H);
}

/* window title carries the branding + state, so Discord's share picker
 * and the "live" indicator advertise the project: "<name> — BackCast · …" */
static void update_window_title(void)
{
	if (!bcp.window)
		return;
	/* constant branding — the tagline never changes with state; the pill
	 * carries LIVE/OFF-AIR */
	if (!bcp.title[0] || wcscmp(bcp.title, L"BackCast") == 0)
		wcscpy_s(bcp.branded_title, 256, L"BackCast Off-Air Stream");
	else
		swprintf_s(bcp.branded_title, 256, L"%ls - BackCast Off-Air Stream",
			   bcp.title);
	SetWindowTextW(bcp.window, bcp.branded_title);
	if (bcp.header)
		InvalidateRect(bcp.header, NULL, FALSE);
}

/* LIVE / OFF-AIR pill. WGC gives no notification when a share attaches, and
 * on this machine the yellow capture border is not drawn either (calibration
 * showed only dark pixels). The signal that DOES toggle: Discord's sound
 * share patches our IAudioRenderClient's ReleaseBuffer with an inline hook —
 * bca_render_hooked() compares the function's first bytes against the
 * pristine snapshot taken when the audio stream started. The border check is
 * kept as a secondary signal, and OBS streaming/recording is OR-ed in. */
static bool is_border_yellow(COLORREF c)
{
	int r = GetRValue(c), g = GetGValue(c), b = GetBValue(c);
	return r > 170 && g > 110 && b < 120 && r > b + 50 && g > b + 30;
}

static bool capture_border_active(HWND frame)
{
	RECT wr;
	GetWindowRect(frame, &wr);
	HDC dc = GetDC(NULL);
	if (!dc)
		return false;
	struct {
		int x, y;
	} pts[] = {
		{(wr.left + wr.right) / 2, wr.top - 2},
		{(wr.left + wr.right) / 2, wr.top - 4},
		{(wr.left + wr.right) / 2, wr.bottom + 2},
		{(wr.left + wr.right) / 2, wr.bottom + 4},
		{wr.left - 2, (wr.top + wr.bottom) / 2},
		{wr.left - 4, (wr.top + wr.bottom) / 2},
		{wr.right + 2, (wr.top + wr.bottom) / 2},
		{wr.right + 4, (wr.top + wr.bottom) / 2},
	};
	bool found = false;
	for (int i = 0; i < 8 && !found; i++) {
		if (pts[i].x < 0 || pts[i].y < 0)
			continue;
		COLORREF c = GetPixel(dc, pts[i].x, pts[i].y);
		if (c != CLR_INVALID && is_border_yellow(c))
			found = true;
	}
	ReleaseDC(NULL, dc);
	return found;
}

/* CALIBRATION pass 3: the share indicator is not a titled window. Watch
 * instead (a) every MODULE loaded in this process (a second Discord DLL
 * may load/unload per share), (b) THREADS whose start address lies inside
 * a Discord module (the hook spawns capture threads while sharing). Log
 * both on change — one of them is the share on/off signal. */
struct dw_enum {
	wchar_t digest[4096];
	size_t len;
};

/* thread start address query (undocumented but stable) */
typedef NTSTATUS (WINAPI *pqsa_t)(HANDLE, int, PVOID, ULONG, PULONG);
static pqsa_t p_qsa;

struct disc_range {
	HMODULE base;
	size_t size;
};

static BOOL CALLBACK discord_win_cb(HWND h, LPARAM lp)
{
	struct dw_enum *d = (struct dw_enum *)lp;
	DWORD pid = 0;
	GetWindowThreadProcessId(h, &pid);
	if (!IsWindowVisible(h) || pid == 0)
		return TRUE;
	HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
	if (!proc)
		return TRUE;
	wchar_t exe[MAX_PATH];
	DWORD exe_len = MAX_PATH;
	bool is_discord = false;
	if (QueryFullProcessImageNameW(proc, 0, exe, &exe_len)) {
		_wcslwr(exe);
		if (wcsstr(exe, L"\\discord"))
			is_discord = true;
	}
	CloseHandle(proc);
	if (!is_discord)
		return TRUE;
	wchar_t title[128];
	int n = GetWindowTextW(h, title, 128);
	if (n <= 0)
		return TRUE;
	wchar_t cls[64];
	GetClassNameW(h, cls, 64);
	_snwprintf(d->digest + d->len, (sizeof(d->digest) / 2) - d->len - 1,
		   L"[%ls|%ls] ", cls, title);
	d->len = wcslen(d->digest);
	return TRUE;
}

static void discord_calib_tick(void)
{
	/* (a) module list digest + Discord module ranges */
	HMODULE mods[1024];
	DWORD cb = 0;
	if (!EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &cb))
		return;
	DWORD n = cb / sizeof(HMODULE);
	if (n > 1024)
		n = 1024;

	static wchar_t last_mods[4096] = L"";
	static struct disc_range ranges[64];
	static int nranges = 0;
	wchar_t mod_digest[4096] = L"";
	size_t ml = 0;
	nranges = 0;

	for (DWORD i = 0; i < n; i++) {
		wchar_t path[MAX_PATH];
		if (!GetModuleFileNameW(mods[i], path, MAX_PATH))
			continue;
		wchar_t low[MAX_PATH];
		wcsncpy(low, path, MAX_PATH - 1);
		low[MAX_PATH - 1] = 0;
		_wcslwr(low);
		bool disc = wcsstr(low, L"\\discord") != NULL;
		if (disc && nranges < 64) {
			MODULEINFO mi;
			if (GetModuleInformation(GetCurrentProcess(), mods[i], &mi, sizeof(mi))) {
				ranges[nranges].base = mods[i];
				ranges[nranges].size = mi.SizeOfImage;
				nranges++;
			}
			const wchar_t *name = wcsrchr(path, L'\\');
			name = name ? name + 1 : path;
			_snwprintf(mod_digest + ml, 2048 - ml - 1, L"[%ls] ", name);
			ml = wcslen(mod_digest);
		}
	}
	if (wcscmp(mod_digest, last_mods) != 0) {
		char utf8[4096];
		WideCharToMultiByte(CP_UTF8, 0, mod_digest, -1, utf8, sizeof(utf8), NULL, NULL);
		blog(LOG_INFO, "[bcp] calib discord modules: %s", utf8);
		wcsncpy(last_mods, mod_digest, 4095);
	}

	/* (b) threads whose start address is inside a Discord module */
	if (!p_qsa) {
		HMODULE nt = GetModuleHandleW(L"ntdll.dll");
		if (nt)
			p_qsa = (pqsa_t)GetProcAddress(nt, "NtQueryInformationThread");
	}
	if (!p_qsa || nranges == 0)
		return;

	HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
	if (snap == INVALID_HANDLE_VALUE)
		return;
	THREADENTRY32 te = { .dwSize = sizeof(te) };
	DWORD mypid = GetCurrentProcessId();
	int disc_threads = 0;
	if (Thread32First(snap, &te)) {
		do {
			if (te.th32OwnerProcessID != mypid)
				continue;
			HANDLE th = OpenThread(THREAD_QUERY_INFORMATION, FALSE, te.th32ThreadID);
			if (!th)
				continue;
			void *start = NULL;
			ULONG ret = 0;
			if (p_qsa(th, 9 /* ThreadQuerySetWin32StartAddress */, &start, sizeof(start), &ret) == 0 &&
			    start) {
				for (int r = 0; r < nranges; r++) {
					if ((uintptr_t)start >= (uintptr_t)ranges[r].base &&
					    (uintptr_t)start < (uintptr_t)ranges[r].base + ranges[r].size) {
						disc_threads++;
						break;
					}
				}
			}
			CloseHandle(th);
		} while (Thread32Next(snap, &te));
	}
	CloseHandle(snap);

	static int last_threads = -1;
}

/* Discord's injected hook DLL — appears when a sound share attaches.
 * It never unloads, so this detects 'attached at least once'; the thread
 * and module calibration above is the path to a true on/off signal. */
static bool discord_hook_loaded(void)
{
	HMODULE mods[1024];
	DWORD cb = 0;
	if (!EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &cb))
		return false;
	DWORD n = cb / sizeof(HMODULE);
	if (n > 1024)
		n = 1024;
	for (DWORD i = 0; i < n; i++) {
		wchar_t path[MAX_PATH];
		if (GetModuleFileNameW(mods[i], path, MAX_PATH)) {
			_wcslwr(path);
			if (wcsstr(path, L"\\discord"))
				return true;
		}
	}
	return false;
}

static void live_tick(void)
{
	HWND frame = bcp.window;
	bool live = obs_frontend_streaming_active() || obs_frontend_recording_active() ||
		    (frame ? capture_border_active(frame) : false) ||
		    bca_render_hooked(bcp.audio) ||
		    discord_hook_loaded();

	discord_calib_tick();

	if (live != bcp.live) {
		bcp.live = live;
		update_window_title();
		if (bcp.header)
			InvalidateRect(bcp.header, NULL, FALSE);
	}
}

static void paint_header(HWND hwnd, HDC dc)
{
	/* hwnd is the header overlay child — its client IS the strip */
	RECT rc;
	GetClientRect(hwnd, &rc);

	HBRUSH panel = CreateSolidBrush(ColPanel);
	FillRect(dc, &rc, panel);

	/* icon (24px, like the app's own header) + LIVE pill + title */
	HICON icon = LoadIconW(bcp_module(), MAKEINTRESOURCEW(IDR_APPICON));
	if (icon)
		DrawIconEx(dc, 10, (rc.bottom - 24) / 2, icon, 24, 24, 0, NULL, DI_NORMAL);
	SetBkMode(dc, TRANSPARENT);

	/* the v0.2 status pill: glowing LED dot + bold caption; OFF-AIR pulses */
	{
		int led_r = bcp.live ? 0x3D : (bcp.pulse ? 0xE0 : 0x60);
		int led_g = bcp.live ? 0xDC : (bcp.pulse ? 0xA4 : 0x46);
		int led_b = bcp.live ? 0x84 : (bcp.pulse ? 0x3C : 0x1A);
		COLORREF txt = bcp.live ? ColAccent : ColGray;
		int cy = rc.bottom / 2;
		int cx = 52;

		/* glow: concentric circles pre-blended toward the solid panel
		 * color (the header background is flat, so alpha can be baked
		 * into the color — no GDI+ needed in plain C) */
		{
			const int panel_r = 21, panel_g = 26, panel_b = 29;
			for (int i = 3; i >= 1; i--) {
				int rad = 4 + i * 4;
				int alpha = 90 / i; /* 30, 45, 90 going inward */
				int br = panel_r + ((led_r - panel_r) * alpha) / 255;
				int bg = panel_g + ((led_g - panel_g) * alpha) / 255;
				int bb = panel_b + ((led_b - panel_b) * alpha) / 255;
				HBRUSH b = CreateSolidBrush(RGB(br, bg, bb));
				HRGN ring = CreateEllipticRgn(cx - rad, cy - rad, cx + rad, cy + rad);
				FillRgn(dc, ring, b);
				DeleteObject(ring);
				DeleteObject(b);
			}
			HBRUSH core = CreateSolidBrush(RGB(led_r, led_g, led_b));
			HRGN dot = CreateEllipticRgn(cx - 4, cy - 4, cx + 4, cy + 4);
			FillRgn(dc, dot, core);
			DeleteObject(dot);
			DeleteObject(core);
		}

		HFONT pf = CreateFontW(-13, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
				       0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH,
				       L"Segoe UI Variable Text");
		if (!pf)
			pf = CreateFontW(-13, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
					 0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
		HFONT pold = SelectObject(dc, pf);
		SetTextColor(dc, txt);
		RECT pr = {62, 0, 140, rc.bottom};
		DrawTextW(dc, bcp.live ? L"LIVE" : L"OFF-AIR", -1, &pr,
			  DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
		SelectObject(dc, pold);
		DeleteObject(pf);
	}

	HFONT font = CreateFontW(-15, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
				 0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH,
				 L"Segoe UI Variable Text");
	if (!font)
		font = CreateFontW(-15, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
				   0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
	HFONT old = SelectObject(dc, font);
	SetTextColor(dc, ColFg);
	/* title + branding centered in the header; the pill keeps its spot */
	RECT tr = {150, 0, rc.right - 150, rc.bottom};
	DrawTextW(dc, bcp.branded_title[0] ? bcp.branded_title : bcp.title, -1, &tr,
		  DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
	SelectObject(dc, old);
	DeleteObject(font);

	/* pin + minimize + close buttons */
	RECT pinR, minR, closeR;
	header_buttons(rc.right, &pinR, &minR, &closeR);
	if (bcp.hover_pin)
		FillRect(dc, &pinR, CreateSolidBrush(ColHover));
	if (bcp.hover_min)
		FillRect(dc, &minR, CreateSolidBrush(ColHover));
	if (bcp.hover_close)
		FillRect(dc, &closeR, CreateSolidBrush(ColHover));

	bool topmost = (GetWindowLongW(bcp.window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

	font = CreateFontW(-18, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
			   0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI Emoji");
	old = SelectObject(dc, font);
	SetTextColor(dc, topmost ? ColAccent : ColGray);
	/* the v0.2 pin: the pushpin emoji */
	DrawTextW(dc, L"\U0001F4CC", -1, &pinR, DT_SINGLELINE | DT_VCENTER | DT_CENTER);
	SelectObject(dc, old);
	DeleteObject(font);

	font = CreateFontW(-15, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
			   0, 0, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
	old = SelectObject(dc, font);
	SetTextColor(dc, ColGray);
	RECT mr = {minR.left, minR.top + 8, minR.right, minR.bottom};
	DrawTextW(dc, L"\u2014", -1, &mr, DT_SINGLELINE | DT_VCENTER | DT_CENTER); /* minimize */
	SetTextColor(dc, ColGray);
	DrawTextW(dc, L"\u2715", -1, &closeR, DT_SINGLELINE | DT_VCENTER | DT_CENTER); /* close ✕ */
	SelectObject(dc, old);
	DeleteObject(font);

	DeleteObject(panel);
}

/* the header overlay child: own window so it can sit ABOVE the video
 * without ever moving or rescaling it */
static LRESULT CALLBACK header_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	switch (msg) {
	case WM_ERASEBKGND:
		return 1;

	case WM_PAINT: {
		PAINTSTRUCT ps;
		HDC dc = BeginPaint(hwnd, &ps);
		paint_header(hwnd, dc);
		EndPaint(hwnd, &ps);
		return 0;
	}

	case WM_SETCURSOR:
		SetCursor(LoadCursorW(NULL, (LPCWSTR)IDC_ARROW));
		return TRUE;

	case WM_MOUSEMOVE: {
		POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
		RECT rc, pinR, minR, closeR;
		GetClientRect(hwnd, &rc);
		header_buttons(rc.right, &pinR, &minR, &closeR);
		bool hp = PtInRect(&pinR, pt) != 0;
		bool hm = PtInRect(&minR, pt) != 0;
		bool hc = PtInRect(&closeR, pt) != 0;
		if (hp != bcp.hover_pin || hm != bcp.hover_min || hc != bcp.hover_close) {
			bcp.hover_pin = hp;
			bcp.hover_min = hm;
			bcp.hover_close = hc;
			InvalidateRect(hwnd, NULL, FALSE);
		}
		if (!bcp.menu_tracking) {
			TRACKMOUSEEVENT tme = { .cbSize = sizeof(tme), .dwFlags = TME_LEAVE, .hwndTrack = hwnd };
			TrackMouseEvent(&tme);
			bcp.menu_tracking = true;
		}
		return 0;
	}

	case WM_MOUSELEAVE:
		if (bcp.hover_pin || bcp.hover_min || bcp.hover_close) {
			bcp.hover_pin = bcp.hover_min = bcp.hover_close = false;
			InvalidateRect(hwnd, NULL, FALSE);
		}
		bcp.menu_tracking = false;
		return 0;

	case WM_LBUTTONDOWN: {
		POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
		RECT rc, pinR, minR, closeR;
		GetClientRect(hwnd, &rc);
		header_buttons(rc.right, &pinR, &minR, &closeR);
		if (PtInRect(&pinR, pt)) {
			bool on = !(GetWindowLongW(bcp.window, GWL_EXSTYLE) & WS_EX_TOPMOST);
			SetWindowPos(bcp.window, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
				     SWP_NOMOVE | SWP_NOSIZE);
			InvalidateRect(hwnd, NULL, FALSE);
		} else if (PtInRect(&minR, pt)) {
			ShowWindow(bcp.window, SW_MINIMIZE);
		} else if (PtInRect(&closeR, pt)) {
			PostMessage(bcp.window, WM_CLOSE, 0, 0);
		} else {
			/* rest of the strip drags the window */
			ReleaseCapture();
			SendMessageW(bcp.window, WM_NCLBUTTONDOWN, HTCAPTION, 0);
		}
		return 0;
	}

	case WM_CONTEXTMENU: {
		POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
		open_context_menu(bcp.window, pt);
		return 0;
	}
	}
	return DefWindowProcW(hwnd, msg, wp, lp);
}

/* ------------------------------------------------------------------ */
/* custom dark popup menu (WebStage style) — borderless top-level      */
/* windows we fully control, instead of the unstylable #32768 classic  */
/* menu: no white border, rounded corners, real side submenu for the   */
/* audio device picker                                                */
/* ------------------------------------------------------------------ */

typedef struct menu_item {
	wchar_t text[160];
	bool check;
	bool gray;
	bool sep;
	bool popup; /* opens the side submenu */
	int id;
} menu_item;

#define MENU_MAX_ITEMS 80
#define MENU_ROW_H 30
#define MENU_SEP_H 9

typedef struct menu_win {
	HWND wnd;
	menu_item items[MENU_MAX_ITEMS];
	int nitems;
	int width, height;
	int hover; /* row index or -1 */
	int press; /* row index the button went down on, or -1 */
	bool used;
} menu_win;

static menu_win mmain, msub; /* main menu + audio-device side submenu */
static int menu_result;      /* selected item id or -1 */
static bool menu_closed;

static int menu_row_height(const menu_item *it)
{
	return it->sep ? MENU_SEP_H : MENU_ROW_H;
}

static void menu_draw_row(HDC dc, const menu_item *it, RECT rc, bool selected)
{
	if (it->sep) {
		HBRUSH sep = CreateSolidBrush(ColSep);
		RECT line = {rc.left + 12, (rc.top + rc.bottom) / 2, rc.right - 12,
			     (rc.top + rc.bottom) / 2 + 1};
		FillRect(dc, &line, sep);
		DeleteObject(sep);
		return;
	}

	if (selected && !it->gray) {
		HBRUSH bg = CreateSolidBrush(ColHover);
		FillRect(dc, &rc, bg);
		DeleteObject(bg);
	}

	SetBkMode(dc, TRANSPARENT);

	if (it->check) {
		HFONT font = CreateFontW(-14, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
					 CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
		HFONT old = SelectObject(dc, font);
		SetTextColor(dc, ColAccent);
		RECT cr = {rc.left, rc.top, rc.left + 32, rc.bottom};
		DrawTextW(dc, L"\u2713", -1, &cr, DT_SINGLELINE | DT_VCENTER | DT_CENTER);
		SelectObject(dc, old);
		DeleteObject(font);
	}

	HFONT font = CreateFontW(-14, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
				 CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
	HFONT old = SelectObject(dc, font);
	SetTextColor(dc, it->gray ? ColGray : ColFg);
	RECT tr = {rc.left + 34, rc.top, rc.right - (it->popup ? 34 : 12), rc.bottom};
	DrawTextW(dc, it->text, -1, &tr, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
	if (it->popup) {
		SetTextColor(dc, ColGray);
		RECT ar = {rc.right - 30, rc.top, rc.right - 10, rc.bottom};
		DrawTextW(dc, L"\u25B8", -1, &ar, DT_SINGLELINE | DT_VCENTER | DT_CENTER);
	}
	SelectObject(dc, old);
	DeleteObject(font);
}

static void menu_paint(menu_win *mw)
{
	PAINTSTRUCT ps;
	HDC dc = BeginPaint(mw->wnd, &ps);
	RECT rc;
	GetClientRect(mw->wnd, &rc);
	HBRUSH bg = CreateSolidBrush(ColPanel);
	FillRect(dc, &rc, bg);
	DeleteObject(bg);

	int y = 0;
	for (int i = 0; i < mw->nitems; i++) {
		int h = menu_row_height(&mw->items[i]);
		RECT row = {0, y, rc.right, y + h};
		menu_draw_row(dc, &mw->items[i], row, i == mw->hover);
		y += h;
	}
	EndPaint(mw->wnd, &ps);
}

static int menu_row_at(menu_win *mw, int y)
{
	int top = 0;
	for (int i = 0; i < mw->nitems; i++) {
		int h = menu_row_height(&mw->items[i]);
		if (y >= top && y < top + h)
			return i;
		top += h;
	}
	return -1;
}

static void menu_add(menu_win *mw, const wchar_t *text, bool check, bool gray, bool sep, bool popup, int id)
{
	if (mw->nitems >= MENU_MAX_ITEMS)
		return;
	menu_item *it = &mw->items[mw->nitems++];
	wcsncpy(it->text, text, 159);
	it->check = check;
	it->gray = gray;
	it->sep = sep;
	it->popup = popup;
	it->id = id;
}

static void menu_measure(menu_win *mw)
{
	HDC dc = GetDC(NULL);
	HFONT font = CreateFontW(-14, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
				 CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
	HFONT old = SelectObject(dc, font);
	int maxw = 180;
	int total_h = 0;
	for (int i = 0; i < mw->nitems; i++) {
		if (mw->items[i].sep) {
			total_h += MENU_SEP_H;
			continue;
		}
		RECT tr = {0, 0, 0, 0};
		DrawTextW(dc, mw->items[i].text, -1, &tr,
			  DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
		if (tr.right + 56 > maxw)
			maxw = tr.right + 56;
		total_h += MENU_ROW_H;
	}
	SelectObject(dc, old);
	DeleteObject(font);
	ReleaseDC(NULL, dc);
	mw->width = maxw;
	mw->height = total_h;
}

/* submenu lifecycle */
static void menu_sub_close(void)
{
	if (!msub.used)
		return;
	DestroyWindow(msub.wnd);
	msub.wnd = NULL;
	msub.used = false;
}

static void menu_sub_open(int row)
{
	if (msub.used)
		return;

	msub.used = true;
	msub.nitems = 0;
	msub.hover = -1;
	msub.press = -1;

	/* device list; checkmark from the CONFIGURED endpoint (what persists) */
	char **ids = NULL, **names = NULL;
	int ndev = bca_enum_endpoints(&ids, &names);
	obs_data_t *cfgE = cfg_load();
	const char *cfg_id = obs_data_get_string(cfgE, "endpoint_id");
	bool auto_mode = !cfg_id || !*cfg_id;
	for (int i = 0; i < ndev && msub.nitems < MENU_MAX_ITEMS - 1; i++) {
		wchar_t wname[160];
		MultiByteToWideChar(CP_UTF8, 0, names[i], -1, wname, 160);
		bool check = !auto_mode && strcmp(cfg_id, ids[i]) == 0;
		menu_add(&msub, wname, check, false, false, false, 100 + i);
	}
	menu_add(&msub, L"Automatic (unused device)", auto_mode, false, false, false, 99);
	obs_data_release(cfgE);
	for (int i = 0; i < ndev; i++) { free(ids[i]); free(names[i]); }
	free(ids); free(names);

	menu_measure(&msub);

	/* position: right of the main menu, aligned with the hovered row */
	RECT mr;
	GetWindowRect(mmain.wnd, &mr);
	int row_top = mr.top;
	for (int i = 0; i < row && i < mmain.nitems; i++)
		row_top += menu_row_height(&mmain.items[i]);

	int x = mr.right + 2;
	int y = row_top;
	HMONITOR mon = MonitorFromPoint((POINT){x, y}, MONITOR_DEFAULTTONEAREST);
	MONITORINFO mi;
	mi.cbSize = sizeof(mi);
	GetMonitorInfoW(mon, &mi);
	if (x + msub.width > mi.rcWork.right)
		x = mr.left - msub.width - 2; /* flip to the left when no room */
	if (y + msub.height > mi.rcWork.bottom)
		y = mi.rcWork.bottom - msub.height;
	if (y < mi.rcWork.top)
		y = mi.rcWork.top;

	msub.wnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"BackcastMenu", L"",
				   WS_POPUP, x, y, msub.width, msub.height,
				   mmain.wnd, NULL, bcp_module(), NULL);
	SetWindowLongPtrW(msub.wnd, GWLP_USERDATA, (LONG_PTR)&msub);

	int no_border = 0xFFFFFFFE; /* DWMWA_COLOR_NONE */
	DwmSetWindowAttribute(msub.wnd, 34, &no_border, sizeof(no_border));
	int round = 2; /* DWMWCP_ROUND */
	DwmSetWindowAttribute(msub.wnd, 33, &round, sizeof(round));
	int dark = 1; /* DWMWA_USE_IMMERSIVE_DARK_MODE */
	DwmSetWindowAttribute(msub.wnd, 20, &dark, sizeof(dark));

	ShowWindow(msub.wnd, SW_SHOWNOACTIVATE);
}

/* mouse routing: the capture lives on the main window, so ALL input arrives
 * there — route it to the main menu or the submenu by cursor position */
static void menu_route_move(POINT pt)
{
	if (msub.used) {
		RECT sr;
		GetWindowRect(msub.wnd, &sr);
		if (PtInRect(&sr, pt)) {
			int row = menu_row_at(&msub, pt.y - sr.top);
			if (row != msub.hover) {
				msub.hover = row;
				InvalidateRect(msub.wnd, NULL, FALSE);
			}
			return;
		}
	}

	RECT mr;
	GetWindowRect(mmain.wnd, &mr);
	int row = menu_row_at(&mmain, pt.y - mr.top);
	if (row != mmain.hover) {
		mmain.hover = row;
		InvalidateRect(mmain.wnd, NULL, FALSE);
	}
	if (row >= 0 && mmain.items[row].popup)
		menu_sub_open(row); /* hover opens the side submenu */
	else
		menu_sub_close();
}

static void menu_route_down(POINT pt)
{
	if (msub.used) {
		RECT sr;
		GetWindowRect(msub.wnd, &sr);
		if (PtInRect(&sr, pt)) {
			msub.press = menu_row_at(&msub, pt.y - sr.top);
			return;
		}
	}
	RECT mr;
	GetWindowRect(mmain.wnd, &mr);
	mmain.press = menu_row_at(&mmain, pt.y - mr.top);
}

static void menu_route_up(POINT pt)
{
	if (msub.used) {
		RECT sr;
		GetWindowRect(msub.wnd, &sr);
		if (PtInRect(&sr, pt)) {
			int row = menu_row_at(&msub, pt.y - sr.top);
			if (row >= 0 && row == msub.press && !msub.items[row].gray &&
			    !msub.items[row].sep) {
				menu_result = msub.items[row].id;
				menu_closed = true;
			}
			msub.press = -1;
			return;
		}
	}

	RECT mr;
	GetWindowRect(mmain.wnd, &mr);
	int row = menu_row_at(&mmain, pt.y - mr.top);
	if (row < 0 || row != mmain.press) {
		mmain.press = -1;
		return;
	}
	menu_item *it = &mmain.items[row];
	if (it->gray || it->sep) {
		mmain.press = -1;
		return;
	}
	if (it->popup) {
		menu_sub_open(row); /* click opens it too */
	} else {
		menu_result = it->id;
		menu_closed = true;
	}
	mmain.press = -1;
}

static LRESULT CALLBACK menu_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	menu_win *mw = (menu_win *)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
	switch (msg) {
	case WM_ERASEBKGND:
		return 1;

	case WM_PAINT:
		if (mw)
			menu_paint(mw);
		else {
			PAINTSTRUCT ps;
			BeginPaint(hwnd, &ps);
			EndPaint(hwnd, &ps);
		}
		return 0;

	case WM_MOUSEMOVE: {
		POINT pt;
		GetCursorPos(&pt);
		menu_route_move(pt);
		return 0;
	}

	case WM_LBUTTONDOWN: {
		POINT pt;
		GetCursorPos(&pt);
		menu_route_down(pt);
		return 0;
	}

	case WM_LBUTTONUP: {
		POINT pt;
		GetCursorPos(&pt);
		menu_route_up(pt);
		return 0;
	}

	case WM_RBUTTONDOWN:
	case WM_NCRBUTTONDOWN:
		menu_closed = true;
		return 0;

	case WM_CAPTURECHANGED:
		menu_closed = true;
		return 0;
	}
	return DefWindowProcW(hwnd, msg, wp, lp);
}

/* restart the WASAPI renderer on a new endpoint, keeping the mix tap */
static void bcp_restart_audio(const char *endpoint_id)
{
	if (obs_get_audio())
		audio_output_disconnect(obs_get_audio(), 0, audio_cb, NULL);
	bca_stop(bcp.audio);
	bcp.audio = NULL;

	if (endpoint_id && *endpoint_id) {
		bcp.audio = bca_start(endpoint_id);
	} else {
		char *picked = bca_pick_spare_endpoint();
		bcp.audio = bca_start(picked ? picked : "");
		free(picked);
	}
	if (bcp.audio && obs_get_audio()) {
		struct audio_convert_info conv = {
			.format = AUDIO_FORMAT_FLOAT,
			.samples_per_sec = 48000,
			.speakers = SPEAKERS_STEREO,
		};
		audio_output_connect(obs_get_audio(), 0, &conv, audio_cb, NULL);
	}
}

static void open_context_menu(HWND hwnd, POINT pt)
{
	WNDCLASSEXW wc = {0};
	wc.cbSize = sizeof(wc);
	wc.hInstance = bcp_module();
	wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
	wc.hbrBackground = NULL;
	wc.lpszClassName = L"BackcastMenu";
	wc.lpfnWndProc = menu_proc;
	RegisterClassExW(&wc);

	memset(&mmain, 0, sizeof(mmain));
	memset(&msub, 0, sizeof(msub));
	menu_result = -1;
	menu_closed = false;

	bool topmost = (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
	menu_add(&mmain, L"Always on top", topmost, false, false, false, 1);
	menu_add(&mmain, L"Rename window\u2026", false, false, false, false, 3);
	menu_add(&mmain, L"Audio device", false, false, false, true, 0);

	struct obs_video_info ovi;
	if (obs_get_video_info(&ovi)) {
		wchar_t info[160];
		swprintf_s(info, 160, L"Canvas  %u\u00D7%u  \u00B7  %u fps",
			   ovi.base_width, ovi.base_height,
			   ovi.fps_num / (ovi.fps_den ? ovi.fps_den : 1));
		menu_add(&mmain, info, false, true, false, false, 0);
	}

	menu_add(&mmain, L"", false, false, true, false, 0);
	menu_add(&mmain, L"Close", false, false, false, false, 2);

	menu_measure(&mmain);

	/* clamp to the monitor */
	int x = pt.x, y = pt.y;
	HMONITOR mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
	MONITORINFO mi;
	mi.cbSize = sizeof(mi);
	GetMonitorInfoW(mon, &mi);
	if (x + mmain.width > mi.rcWork.right)
		x = mi.rcWork.right - mmain.width;
	if (y + mmain.height > mi.rcWork.bottom)
		y = mi.rcWork.bottom - mmain.height;
	if (x < mi.rcWork.left) x = mi.rcWork.left;
	if (y < mi.rcWork.top) y = mi.rcWork.top;

	mmain.wnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"BackcastMenu", L"",
				    WS_POPUP, x, y, mmain.width, mmain.height,
				    hwnd, NULL, wc.hInstance, NULL);
	SetWindowLongPtrW(mmain.wnd, GWLP_USERDATA, (LONG_PTR)&mmain);
	mmain.used = true;

	/* borderless + rounded, exactly like the exe's dark popups */
	int no_border = 0xFFFFFFFE; /* DWMWA_COLOR_NONE */
	DwmSetWindowAttribute(mmain.wnd, 34, &no_border, sizeof(no_border));
	int round = 2; /* DWMWCP_ROUND */
	DwmSetWindowAttribute(mmain.wnd, 33, &round, sizeof(round));
	int dark = 1; /* DWMWA_USE_IMMERSIVE_DARK_MODE */
	DwmSetWindowAttribute(mmain.wnd, 20, &dark, sizeof(dark));

	ShowWindow(mmain.wnd, SW_SHOWNOACTIVATE);
	SetCapture(mmain.wnd);

	/* modal-ish loop; clicks outside BOTH windows close the menu */
	MSG msg;
	while (!menu_closed && GetMessageW(&msg, NULL, 0, 0) > 0) {
		if (msg.message == WM_LBUTTONDOWN || msg.message == WM_RBUTTONDOWN ||
		    msg.message == WM_NCLBUTTONDOWN || msg.message == WM_NCRBUTTONDOWN) {
			POINT cp;
			GetCursorPos(&cp);
			RECT mr, sr;
			GetWindowRect(mmain.wnd, &mr);
			bool inside = PtInRect(&mr, cp);
			if (!inside && msub.used) {
				GetWindowRect(msub.wnd, &sr);
				inside = PtInRect(&sr, cp);
			}
			if (!inside) {
				menu_closed = true;
				break;
			}
		}
		TranslateMessage(&msg);
		DispatchMessageW(&msg);
	}

	ReleaseCapture();
	menu_sub_close();
	DestroyWindow(mmain.wnd);
	mmain.wnd = NULL;
	mmain.used = false;

	int cmd = menu_result;

	if (cmd == 1) {
		bool on = !(GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST);
		SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
	} else if (cmd == 2) {
		PostMessage(hwnd, WM_CLOSE, 0, 0);
	} else if (cmd == 3) {
		SetTimer(hwnd, 3, 50, NULL); /* open the rename dialog once we unwind */
	} else if (cmd == 99) {
		obs_data_t *cfg = cfg_load();
		obs_data_set_string(cfg, "endpoint_id", "");
		cfg_save(cfg);
		obs_data_release(cfg);
		bcp_restart_audio(NULL);
	} else if (cmd >= 100) {
		char **ids = NULL, **names = NULL;
		int ndev = bca_enum_endpoints(&ids, &names);
		if (cmd - 100 < ndev) {
			const char *id = ids[cmd - 100];
			obs_data_t *cfg = cfg_load();
			obs_data_set_string(cfg, "endpoint_id", id);
			cfg_save(cfg);
			obs_data_release(cfg);
			bcp_restart_audio(id);
		}
		for (int i = 0; i < ndev; i++) { free(ids[i]); free(names[i]); }
		free(ids); free(names);
	}
}

/* ------------------------------------------------------------------ */
/* hover-reveal header (0.2 behavior): clean share by default, header  */
/* pops in when the cursor nears the top edge                          */
/* ------------------------------------------------------------------ */

static void set_header(HWND frame, bool show)
{
	if (bcp.header_shown == show)
		return;
	bcp.header_shown = show;
	/* the header is a top-level popup owned by the frame — the video
	 * swapchain composites above any GDI *sibling*, so an overlay child
	 * can't sit on it; a popup composites above the whole window. It is
	 * synced to the frame's position (WM_MOVE/WM_SIZE) and never touches
	 * the video itself. */
	if (show) {
		RECT wr;
		GetWindowRect(frame, &wr);
		/* topmost while shown (no activation): the popup must sit above
		 * everything to be visible, but must never steal focus or raise
		 * the BackCast window's owner chain */
		SetWindowPos(bcp.header, HWND_TOPMOST, wr.left, wr.top,
			     wr.right - wr.left, HEADER_H,
			     SWP_NOACTIVATE | SWP_SHOWWINDOW);
	} else {
		ShowWindow(bcp.header, SW_HIDE);
	}
}

static void hover_tick(HWND frame)
{
	RECT wr;
	GetWindowRect(frame, &wr);
	POINT pt;
	GetCursorPos(&pt);
	/* only when OUR window is actually under the cursor — not when the
	 * cursor merely crosses the zone while another window covers us */
	HWND under = WindowFromPoint(pt);
	bool ours = under == frame || under == bcp.render || under == bcp.header;
	bool in_zone = ours &&
		       pt.x >= wr.left && pt.x <= wr.right &&
		       pt.y >= wr.top && pt.y <= wr.top + HOVER_ZONE;
	if (in_zone) {
		if (bcp.rehide_pending) {
			KillTimer(frame, TIMER_REHIDE);
			bcp.rehide_pending = false;
		}
		set_header(frame, true);
	} else if (bcp.header_shown && !bcp.rehide_pending) {
		SetTimer(frame, TIMER_REHIDE, REHIDE_MS, NULL);
		bcp.rehide_pending = true;
	}
}

/* ------------------------------------------------------------------ */
/* rename dialog: small dark modal with one edit field                 */
/* ------------------------------------------------------------------ */

static wchar_t rename_buf[128];

static LRESULT CALLBACK rename_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	switch (msg) {
	case WM_ERASEBKGND:
		return 1;
	case WM_PAINT: {
		PAINTSTRUCT ps;
		HDC dc = BeginPaint(hwnd, &ps);
		RECT rc;
		GetClientRect(hwnd, &rc);
		HBRUSH bg = CreateSolidBrush(ColPanel);
		FillRect(dc, &rc, bg);
		DeleteObject(bg);
		SetBkMode(dc, TRANSPARENT);
		HFONT f = CreateFontW(-14, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
				      CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
		HFONT old = SelectObject(dc, f);
		SetTextColor(dc, ColFg);
		RECT tr = {16, 12, rc.right - 16, 32};
		DrawTextW(dc, L"Window title", -1, &tr, DT_SINGLELINE | DT_NOPREFIX);
		RECT hr = {16, 74, rc.right - 16, 92};
		SetTextColor(dc, ColGray);
		DrawTextW(dc, L"Enter to apply  ·  Esc to cancel", -1, &hr,
			  DT_SINGLELINE | DT_NOPREFIX);
		SelectObject(dc, old);
		DeleteObject(f);
		EndPaint(hwnd, &ps);
		return 0;
	}
	case WM_CTLCOLOREDIT: {
		HDC dc = (HDC)wp;
		SetBkColor(dc, BC_RGB(28, 35, 39));
		SetTextColor(dc, ColFg);
		static HBRUSH edit_brush = NULL;
		if (!edit_brush)
			edit_brush = CreateSolidBrush(BC_RGB(28, 35, 39));
		return (LRESULT)edit_brush;
	}
	}
	return DefWindowProcW(hwnd, msg, wp, lp);
}

static void open_rename_dialog(HWND parent)
{
	WNDCLASSEXW wc = {0};
	wc.cbSize = sizeof(wc);
	wc.hInstance = bcp_module();
	wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
	wc.hbrBackground = NULL;
	wc.lpszClassName = L"BackcastRename";
	wc.lpfnWndProc = rename_proc;
	RegisterClassExW(&wc);

	RECT pr;
	GetWindowRect(parent, &pr);
	int w = 380, h = 120;
	int x = pr.left + ((pr.right - pr.left) - w) / 2;
	int y = pr.top + ((pr.bottom - pr.top) - h) / 2;
	RECT wr = {0, 0, w, h};
	AdjustWindowRect(&wr, WS_OVERLAPPEDWINDOW & ~(WS_MAXIMIZEBOX | WS_MINIMIZEBOX), FALSE);

	HWND dlg = CreateWindowExW(0, L"BackcastRename", L"BackCast",
				   WS_OVERLAPPEDWINDOW & ~(WS_MAXIMIZEBOX | WS_MINIMIZEBOX),
				   x, y, wr.right - wr.left, wr.bottom - wr.top,
				   parent, NULL, wc.hInstance, NULL);
	/* dark caption */
	int dark = 1;
	DwmSetWindowAttribute(dlg, 20, &dark, sizeof(dark));

	HWND edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", bcp.title,
				     WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
				     14, 38, w - 28, 26, dlg, NULL, wc.hInstance, NULL);
	HFONT ef = CreateFontW(-15, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
				CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
	SendMessageW(edit, WM_SETFONT, (WPARAM)ef, TRUE);
	SetWindowTheme(edit, L"DarkMode_Explorer", NULL);

	EnableWindow(parent, FALSE);
	ShowWindow(dlg, SW_SHOW);
	SetFocus(edit);
	SendMessageW(edit, EM_SETSEL, 0, -1);

	bool done = false, ok = false;
	MSG msg;
	while (!done && GetMessageW(&msg, NULL, 0, 0) > 0) {
		if (msg.message == WM_KEYDOWN && (msg.hwnd == edit || msg.hwnd == dlg)) {
			if (msg.wParam == VK_RETURN) {
				ok = true;
				done = true;
				continue;
			}
			if (msg.wParam == VK_ESCAPE) {
				done = true;
				continue;
			}
		}
		TranslateMessage(&msg);
		DispatchMessageW(&msg);
		if (!IsWindow(dlg))
			done = true;
	}

	if (ok && GetWindowTextW(edit, rename_buf, 128) > 0) {
		wcsncpy(bcp.title, rename_buf, 127);
		update_window_title();
		InvalidateRect(parent, NULL, TRUE);
		/* persist as UTF-8 */
		char utf8[384];
		WideCharToMultiByte(CP_UTF8, 0, bcp.title, -1, utf8, sizeof(utf8), NULL, NULL);
		obs_data_t *cfg = cfg_load();
		obs_data_set_string(cfg, "title", utf8);
		cfg_save(cfg);
		obs_data_release(cfg);
	}

	DestroyWindow(dlg);
	EnableWindow(parent, TRUE);
	SetForegroundWindow(parent);
}

/* ------------------------------------------------------------------ */
/* window proc — dark header + render child, borderless resize edges    */
/* ------------------------------------------------------------------ */

static LRESULT CALLBACK render_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	switch (msg) {
	case WM_ERASEBKGND:
		return 1; /* obs display swapchain paints this window */
	case WM_NCHITTEST:
		/* let the mouse fall through to the frame window: the video
		 * area then drags the window and right-clicks open the menu,
		 * while the frame's cursor poll drives the hover header */
		return HTTRANSPARENT;
	}
	return DefWindowProcW(hwnd, msg, wp, lp);
}

static LRESULT CALLBACK frame_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	switch (msg) {
	case WM_ERASEBKGND:
		return 1;

	case WM_PAINT: {
		/* the render child paints the video; the header overlay paints
		 * itself — nothing to do here but validate */
		PAINTSTRUCT ps;
		BeginPaint(hwnd, &ps);
		EndPaint(hwnd, &ps);
		return 0;
	}

	case WM_SIZE: {
		if (wp == SIZE_MINIMIZED)
			return 0; /* a 0x0 resize kills the display swapchain */
		if (!bcp.display)
			break;
		RECT rc;
		GetClientRect(hwnd, &rc);
		MoveWindow(bcp.render, 0, 0, rc.right, rc.bottom, FALSE);
		obs_display_resize(bcp.display, (uint32_t)rc.right, (uint32_t)rc.bottom);
		if (bcp.header_shown && bcp.header) {
			RECT wr;
			GetWindowRect(hwnd, &wr);
			SetWindowPos(bcp.header, HWND_TOP, wr.left, wr.top,
				     wr.right - wr.left, HEADER_H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
		}
		return 0;
	}

	case WM_MOVE:
		/* keep the header popup glued to the frame while dragging */
		if (bcp.header_shown && bcp.header) {
			RECT wr;
			GetWindowRect(hwnd, &wr);
			SetWindowPos(bcp.header, NULL, wr.left, wr.top,
				     wr.right - wr.left, HEADER_H,
				     SWP_NOZORDER | SWP_NOACTIVATE);
		}
		return 0;

	case WM_TIMER:
		if (wp == TIMER_HOVER) {
			hover_tick(hwnd);
			return 0;
		}
		if (wp == TIMER_REHIDE) {
			KillTimer(hwnd, TIMER_REHIDE);
			bcp.rehide_pending = false;
			RECT wr;
			GetWindowRect(hwnd, &wr);
			POINT pt;
			GetCursorPos(&pt);
			HWND under = WindowFromPoint(pt);
			bool ours = under == hwnd || under == bcp.render || under == bcp.header;
			bool in_zone = ours &&
				       pt.x >= wr.left && pt.x <= wr.right &&
				       pt.y >= wr.top && pt.y <= wr.top + HOVER_ZONE;
			if (!in_zone)
				set_header(hwnd, false);
			return 0;
		}
		if (wp == 3) {
			KillTimer(hwnd, 3);
			open_rename_dialog(hwnd);
			return 0;
		}
		if (wp == 4) {
			live_tick();
			return 0;
		}
		if (wp == 5) {
			/* v0.2 pill animation: the OFF-AIR LED pulses; LIVE is steady */
			if (bcp.live) {
				if (!bcp.pulse) {
					bcp.pulse = true;
					if (bcp.header)
						InvalidateRect(bcp.header, NULL, FALSE);
				}
			} else {
				bcp.pulse = !bcp.pulse;
				if (bcp.header)
					InvalidateRect(bcp.header, NULL, FALSE);
			}
			return 0;
		}
		break;

	case WM_SIZING: {
		/* lock the drag rectangle to the canvas aspect — no letterbox
		 * bars, ever */
		RECT *rc = (RECT *)lp;
		double aspect = bcp.last_canvas_w && bcp.last_canvas_h
			? (double)bcp.last_canvas_w / (double)bcp.last_canvas_h
			: 16.0 / 9.0;
		if (aspect <= 0.0)
			break;
		switch (wp) {
		case WMSZ_LEFT:
		case WMSZ_RIGHT: {
			LONG w = rc->right - rc->left;
			rc->bottom = rc->top + (LONG)((double)w / aspect);
			break;
		}
		case WMSZ_TOP:
		case WMSZ_BOTTOM: {
			LONG h = rc->bottom - rc->top;
			rc->right = rc->left + (LONG)((double)h * aspect);
			break;
		}
		case WMSZ_TOPLEFT:
			rc->top = rc->bottom - (LONG)((double)(rc->right - rc->left) / aspect);
			break;
		case WMSZ_TOPRIGHT:
			rc->top = rc->bottom - (LONG)((double)(rc->right - rc->left) / aspect);
			break;
		case WMSZ_BOTTOMLEFT:
		case WMSZ_BOTTOMRIGHT:
			rc->bottom = rc->top + (LONG)((double)(rc->right - rc->left) / aspect);
			break;
		}
		return TRUE;
	}

	case WM_NCRBUTTONUP: {
		/* the video area is HTCAPTION (drag) — DefWindowProc would show
		 * the system menu instead of our context menu, so handle the
		 * non-client right-click ourselves */
		POINT pt;
		GetCursorPos(&pt);
		open_context_menu(hwnd, pt);
		return 0;
	}

	case WM_APP_REFIT: {
		/* canvas aspect changed: refit window keeping its area */
		RECT wr;
		GetWindowRect(hwnd, &wr);
		double aspect = (double)bcp.last_canvas_w / (double)bcp.last_canvas_h;
		if (aspect <= 0.0)
			return 0;
		LONG area = (wr.right - wr.left) * (wr.bottom - wr.top);
		LONG w = (LONG)floor(sqrt((double)area * aspect) + 0.5);
		LONG h = (LONG)floor(sqrt((double)area / aspect) + 0.5);

		HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
		MONITORINFO mi;
		mi.cbSize = sizeof(mi);
		GetMonitorInfoW(mon, &mi);
		LONG x = wr.left + ((wr.right - wr.left) - w) / 2;
		LONG y = wr.top + ((wr.bottom - wr.top) - h) / 2;
		if (x < mi.rcWork.left) x = mi.rcWork.left;
		if (y < mi.rcWork.top) y = mi.rcWork.top;
		if (x + w > mi.rcWork.right) x = mi.rcWork.right - w;
		if (y + h > mi.rcWork.bottom) y = mi.rcWork.bottom - h;
		SetWindowPos(hwnd, NULL, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
		return 0;
	}

	case WM_NCCALCSIZE:
		/* borderless: swallow the frame, keep resize behavior */
		if (wp)
			return 0;
		break;

	case WM_NCHITTEST: {
		LRESULT hit = DefWindowProcW(hwnd, msg, wp, lp);
		if (hit == HTCLIENT) {
			POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
			POINT client = pt;
			ScreenToClient(hwnd, &client);

			/* resize edges first (borderless frame swallowed them) */
			RECT wr;
			GetWindowRect(hwnd, &wr);
			int bx = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
			int by = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
			bool left = pt.x < wr.left + bx;
			bool right = pt.x >= wr.right - bx;
			bool top = pt.y < wr.top + by;
			bool bottom = pt.y >= wr.bottom - by;
			if (top && left) return HTTOPLEFT;
			if (top && right) return HTTOPRIGHT;
			if (bottom && left) return HTBOTTOMLEFT;
			if (bottom && right) return HTBOTTOMRIGHT;
			if (left) return HTLEFT;
			if (right) return HTRIGHT;
			if (top) return HTTOP;
			if (bottom) return HTBOTTOM;

			/* video area drags the window too */
			return HTCAPTION;
		}
		return hit;
	}

	case WM_CONTEXTMENU: {
		POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
		if (pt.x == -1 && pt.y == -1) /* keyboard-invoked: no coords */
			GetCursorPos(&pt);
		open_context_menu(hwnd, pt);
		return 0;
	}

	case WM_CLOSE:
		bcp_stop();
		return 0;
	}
	return DefWindowProcW(hwnd, msg, wp, lp);
}

/* ------------------------------------------------------------------ */
/* start / stop — full lifecycle, zero resources when closed           */
/* ------------------------------------------------------------------ */

static void bcp_start(void)
{
	if (bcp.open)
		return;

	HINSTANCE inst = bcp_module();
	HICON icon = LoadIconW(inst, MAKEINTRESOURCEW(IDR_APPICON));

	WNDCLASSEXW wc = {0};
	wc.cbSize = sizeof(wc);
	wc.style = CS_HREDRAW | CS_VREDRAW;
	wc.hInstance = inst;
	wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
	wc.hIcon = icon;
	wc.hIconSm = icon;
	wc.lpszClassName = L"BackcastProjector";
	wc.lpfnWndProc = frame_proc;
	RegisterClassExW(&wc);

	wc.style = 0;
	wc.hIcon = wc.hIconSm = NULL;
	wc.hbrBackground = GetStockObject(BLACK_BRUSH);
	wc.lpszClassName = L"BackcastRender";
	wc.lpfnWndProc = render_proc;
	RegisterClassExW(&wc);

	wc.hbrBackground = NULL;
	wc.lpszClassName = L"BackcastHeader";
	wc.lpfnWndProc = header_proc;
	RegisterClassExW(&wc);

	/* restore bounds or default 16:9 centered; window title from config */
	obs_data_t *cfg = cfg_load();
	long x = (long)cfg_get_int(cfg, "x", -1);
	long y = (long)cfg_get_int(cfg, "y", -1);
	long w = (long)cfg_get_int(cfg, "w", 0);
	long h = (long)cfg_get_int(cfg, "h", 0);
	bool topmost = cfg_get_bool(cfg, "topmost", false);
	const char *title8 = obs_data_get_string(cfg, "title");
	if (title8 && *title8)
		MultiByteToWideChar(CP_UTF8, 0, title8, -1, bcp.title, 128);
	else
		wcsncpy(bcp.title, L"BackCast", 127);
	obs_data_release(cfg);
	if (w < 200 || h < 120) {
		w = 960;
		h = 560 + HEADER_H;
		x = -1;
		y = -1;
	}

	RECT wr = {0, 0, w, h};
	AdjustWindowRect(&wr, WS_OVERLAPPEDWINDOW, FALSE);

	if (x < 0 || y < 0) {
		x = (GetSystemMetrics(SM_CXSCREEN) - (wr.right - wr.left)) / 2;
		y = (GetSystemMetrics(SM_CYSCREEN) - (wr.bottom - wr.top)) / 2;
	}

	HWND frame = CreateWindowExW(
		topmost ? WS_EX_TOPMOST : 0, L"BackcastProjector", bcp.title,
		WS_OVERLAPPEDWINDOW & ~(WS_MAXIMIZEBOX),
		x, y, wr.right - wr.left, wr.bottom - wr.top,
		NULL, NULL, inst, NULL);
	if (!frame)
		return;

	bcp.window = frame;
	RECT rc;
	GetClientRect(frame, &rc);
	/* clean share: video fills the whole window; the header overlay pops
	 * in above it on hover (set_header), never moving the video */
	bcp.render = CreateWindowExW(0, L"BackcastRender", L"", WS_CHILD | WS_VISIBLE,
		0, 0, rc.right, rc.bottom, frame, NULL, inst, NULL);
	/* top-level popup owned by the frame: DWM composites it above the
	 * whole window, including the video swapchain (a GDI *child* cannot
	 * overlay a flip-model swapchain). Synced on move/size; never
	 * touches the video. */
	RECT wr2;
	GetWindowRect(frame, &wr2);
	bcp.header = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"BackcastHeader",
		L"", WS_POPUP, wr2.left, wr2.top, wr2.right - wr2.left, HEADER_H,
		frame, NULL, inst, NULL);

	struct gs_init_data gs = {0};
	gs.window.hwnd = bcp.render;
	gs.cx = (uint32_t)rc.right;
	gs.cy = (uint32_t)rc.bottom;
	gs.num_backbuffers = 2;
	gs.format = GS_BGRA;
	gs.zsformat = GS_ZS_NONE;
	gs.adapter = 0;
	bcp.display = obs_display_create(&gs, 0xFF000000);
	if (!bcp.display) {
		DestroyWindow(frame);
		memset(&bcp, 0, sizeof(bcp));
		return;
	}
	obs_display_add_draw_callback(bcp.display, draw_cb, NULL);
	obs_display_resize(bcp.display, (uint32_t)rc.right, (uint32_t)rc.bottom);

	bcp.open = true;
	update_window_title();
	ShowWindow(frame, SW_SHOW);
	UpdateWindow(frame);
	SetTimer(frame, TIMER_HOVER, HOVER_POLL_MS, NULL);
	SetTimer(frame, 4, 1000, NULL); /* LIVE pill poll */
	SetTimer(frame, 5, 650, NULL);  /* OFF-AIR pill pulse */

	/* remember it was open, so OBS restart brings it back */
	obs_data_t *cfgOpen = cfg_load();
	obs_data_set_bool(cfgOpen, "open", true);
	cfg_save(cfgOpen);
	obs_data_release(cfgOpen);

	/* audio: configured endpoint, else auto-pick a spare one and remember */
	obs_data_t *cfg2 = cfg_load();
	const char *endpoint_id = obs_data_get_string(cfg2, "endpoint_id");
	char *picked = NULL;
	if (!endpoint_id || !*endpoint_id) {
		picked = bca_pick_spare_endpoint();
		if (picked) {
			obs_data_set_string(cfg2, "endpoint_id", picked);
			cfg_save(cfg2);
			obs_log(LOG_INFO, "auto-picked audio endpoint");
		} else {
			obs_log(LOG_WARNING, "no spare audio endpoint — falling back to default "
					     "(you will hear the mix locally; pick another device in settings)");
		}
		endpoint_id = picked ? picked : "";
	}
	bcp.audio = bca_start(endpoint_id);

	/* log which device the mix plays to (name lookup for readability) */
	{
		char **ids = NULL, **names = NULL;
		int nd = bca_enum_endpoints(&ids, &names);
		for (int i = 0; i < nd; i++) {
			if (strcmp(ids[i], endpoint_id) == 0)
				blog(LOG_INFO, "audio endpoint: %s", names[i]);
			free(ids[i]);
			free(names[i]);
		}
		free(ids);
		free(names);
	}

	obs_data_release(cfg2);
	free(picked);

	/* tap mix 0 = master mix; plays through our WASAPI stream in this
	 * process — Discord captures it when sharing the Backcast window */
	if (bcp.audio && obs_get_audio()) {
		struct audio_convert_info conv = {
			.format = AUDIO_FORMAT_FLOAT,
			.samples_per_sec = 48000,
			.speakers = SPEAKERS_STEREO,
		};
		if (audio_output_connect(obs_get_audio(), 0, &conv, audio_cb, NULL))
			obs_log(LOG_INFO, "master mix connected");
		else
			obs_log(LOG_ERROR, "audio_output_connect failed");
	}

	obs_log(LOG_INFO, "backcast window opened");
}

static void bcp_stop(void)
{
	/* user-initiated close: the window stays closed next OBS start */
	bcp_teardown(true);
}

static void bcp_teardown(bool user_closed)
{
	if (!bcp.open)
		return;

	if (user_closed) {
		/* persist bounds + closed state */
		RECT wr;
		GetWindowRect(bcp.window, &wr);
		bool topmost = (GetWindowLongW(bcp.window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
		obs_data_t *cfg = cfg_load();
		obs_data_set_int(cfg, "x", wr.left);
		obs_data_set_int(cfg, "y", wr.top);
		obs_data_set_int(cfg, "w", wr.right - wr.left);
		obs_data_set_int(cfg, "h", wr.bottom - wr.top);
		obs_data_set_bool(cfg, "topmost", topmost);
		obs_data_set_bool(cfg, "open", false);
		cfg_save(cfg);
		obs_data_release(cfg);
	}

	if (bcp.display) {
		obs_display_remove_draw_callback(bcp.display, draw_cb, NULL);
		obs_display_destroy(bcp.display);
	}
	if (obs_get_audio())
		audio_output_disconnect(obs_get_audio(), 0, audio_cb, NULL);
	bca_stop(bcp.audio);
	HWND frame = bcp.window;
	KillTimer(frame, TIMER_HOVER);
	KillTimer(frame, TIMER_REHIDE);
	KillTimer(frame, 3);
	KillTimer(frame, 4);
	KillTimer(frame, 5);
	memset(&bcp, 0, sizeof(bcp));
	DestroyWindow(frame);

	obs_log(LOG_INFO, "backcast window closed — no resources held");
}

static void tools_toggle(void *private_data)
{
	UNUSED_PARAMETER(private_data);
	if (bcp.open)
		bcp_stop();
	else
		bcp_start();
}

/* ---- hotkeys (OBS Settings -> Hotkeys; work anywhere in OBS) ---- */

static void hk_toggle_window(void *data, obs_hotkey_id id, obs_hotkey_t *hotkey, bool pressed)
{
	UNUSED_PARAMETER(data);
	UNUSED_PARAMETER(id);
	UNUSED_PARAMETER(hotkey);
	if (pressed)
		tools_toggle(NULL);
}

static void hk_toggle_topmost(void *data, obs_hotkey_id id, obs_hotkey_t *hotkey, bool pressed)
{
	UNUSED_PARAMETER(data);
	UNUSED_PARAMETER(id);
	UNUSED_PARAMETER(hotkey);
	if (!pressed || !bcp.open || !bcp.window)
		return;
	bool on = !(GetWindowLongW(bcp.window, GWL_EXSTYLE) & WS_EX_TOPMOST);
	SetWindowPos(bcp.window, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
		     SWP_NOMOVE | SWP_NOSIZE);
}

/* ------------------------------------------------------------------ */
/* frontend events + module lifecycle                                  */
/* ------------------------------------------------------------------ */

static void frontend_event(enum obs_frontend_event event, void *private_data)
{
	UNUSED_PARAMETER(private_data);
	switch (event) {
	case OBS_FRONTEND_EVENT_FINISHED_LOADING: {
		/* reopen only if it was open when OBS was closed */
		obs_data_t *cfg = cfg_load();
		bool open = cfg_get_bool(cfg, "open", false);
		obs_data_release(cfg);
		if (open)
			bcp_start();
		break;
	}
	case OBS_FRONTEND_EVENT_EXIT:
		/* OBS is closing: tear down WITHOUT clearing the open flag, so
		 * the window comes back on the next OBS start */
		bcp_teardown(false);
		break;
	}
}

bool obs_module_load(void)
{
	obs_frontend_add_tools_menu_item(obs_module_text("Tools.ToggleWindow"), tools_toggle, NULL);
	obs_frontend_add_event_callback(frontend_event, NULL);
	obs_hotkey_register_frontend("Backcast.ToggleWindow",
				     obs_module_text("Hotkey.ToggleWindow"),
				     hk_toggle_window, NULL);
	obs_hotkey_register_frontend("Backcast.ToggleTopmost",
				     obs_module_text("Hotkey.ToggleTopmost"),
				     hk_toggle_topmost, NULL);
	blog(LOG_INFO, "plugin loaded (version %s)", PLUGIN_VERSION);
	return true;
}

void obs_module_unload(void)
{
	bcp_teardown(false);
	blog(LOG_INFO, "plugin unloaded");
}
