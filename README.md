# Backcast

> The opposite of broadcast — your OBS scene with friends on Discord, without going live.

One exe. Run it, follow the two-step first-run wizard, share the window on Discord. Your friends see your OBS scene and hear your full OBS mix.

---

## For the user

**Release = one file** (`Backcast.exe`, ~108 MB, everything inside). Copy it anywhere, double-click.

- **First launch** opens a 2-step setup:
  1. *How OBS connects*: **Direct** (recommended — paste one URL into OBS's Stream settings) or **Relay** (if you use the Aitum Multistream plugin — the wizard downloads the tiny local relay for you and shows the Aitum destination to paste).
  2. *Where the sound goes*: pick a silent audio output (VB-Cable or an unused HDMI/monitor output) from the list; one click installs VB-Cable if you don't have it.
- After that it just works: start streaming in OBS → the window goes **🔴 LIVE** → share it on Discord with **Sound ON**.
- OBS stops streaming → the window shows **⏳ waiting**; OBS restarts → recovers by itself, zero clicks.
- All settings via the tray icon or window right-click → **Settings…** (connection, audio output, display name, hotkeys, always-on-top). Nothing to edit by hand.
- **Never mute Backcast in the Windows volume mixer** — that mutes what your friends hear.

Diagnostic log (if troubleshooting): `%TEMP%\Backcast.log`. Settings: `%APPDATA%\Backcast\settings.json`.

### The two connection modes (why both)

- **Direct (UDP)** — OBS's own Stream output sends to `udp://127.0.0.1:1234?pkt_size=1316`. Lowest latency, zero extra software. Use this if you stream with OBS's plain Start Streaming button.
- **Relay (RTMP→RTSP)** — Aitum Multistream destinations are RTMP-only, so they can't send UDP. Backcast bundles a tiny local relay (MediaMTX): Aitum sends `rtmp://127.0.0.1:1935/discord`, Backcast plays the RTSP side. Downloaded on first setup, started/stopped with the app automatically. Adds ~0.5–1.5 s latency.

## For the developer

Requirements: .NET 8 SDK, Windows 10/11 x64.

```powershell
# fetch libmpv once (dev builds need the DLL next to the exe;
# the single-file release bundles it inside Backcast.exe)
powershell -ExecutionPolicy Bypass -File tools\fetch-libmpv.ps1

# build & run (note: output lands in bin/Debug/net8.0-windows/win-x64/)
dotnet run --project src\Backcast

# the one-file release
dotnet publish src\Backcast\Backcast.csproj -c Release
# → src\Backcast\bin\Release\net8.0-windows\win-x64\publish\Backcast.exe
```

Test without OBS (the acceptance transmitter):

```cmd
tools\test-transmitter.cmd         :: 1080p60 test pattern + 440 Hz tone → udp://127.0.0.1:1234
tools\test-transmitter-stop.cmd
```

Architecture: WinForms x64 + libmpv embedded via the `wid` option (mpv renders into a
child HWND). `MpvPlayer` owns the context, observed properties (`core-idle`,
`eof-reached`, `time-pos`) and the reconnect watchdog (LIVE/WAITING/ERROR, retry
every 2 s after 1.5 s without data, never gives up). Dark UI is a port of the
WebStage theme (palette + owner-drawn controls + tray menu).

## OBS-side settings (context)

Direct mode server string: `udp://127.0.0.1:1234?pkt_size=1316` (stream key ignored).
Relay mode Aitum destination: Custom RTMP `rtmp://127.0.0.1:1935/discord`.

Encoder settings for low latency (OBS → Output → Streaming):

| Setting           | x264                    | NVENC                      |
|-------------------|-------------------------|----------------------------|
| Rate control      | CBR                     | CBR                        |
| Bitrate           | 6000–8000 kbps @1080p60 | 8000 kbps                  |
| Keyframe interval | 1 s                     | 1 s                        |
| Preset            | veryfast                | P4                         |
| Custom options    | `tune=zerolatency`      | Psycho Visual Tuning ON, Look-ahead OFF |
| Audio             | AAC 48 kHz stereo, 160–192 kbps | same              |

## Discord sharing

1. Join a voice channel → **Share your screen** → **Application Window** → pick
   Backcast (title shows `🔴 <name> — LIVE · Backcast`, easy to spot).
2. Toggle **Sound** ON; pick 1080p60 (Nitro) or 720p30.
3. Alternative: Discord → Game Activity → register `Backcast.exe` → Go Live as a game.

Audio fallback mode (`voiceCable`, set in Settings): app renders into VB-Cable Input,
Discord's input device is `CABLE Output` with noise suppression/echo/AGC off and manual
sensitivity fully open — then share with the Sound toggle **OFF** (audio rides the voice
channel). Don't run both modes at once.

## Latency budget (direct path)

| Stage | Typical |
|---|---|
| OBS encode | 30–120 ms |
| MPEG-TS mux + UDP | 50–150 ms |
| mpv demux + decode | 150–400 ms |
| Discord delivery | 500–1500 ms |
| **End-to-end for friends** | **~1–2.5 s** |

## Deviations from the original build spec

1. **libmpv source**: official `mpv-player/mpv` releases don't ship a dev package;
   `tools/fetch-libmpv.ps1` pulls `mpv-dev-x86_64` from `shinchiro/mpv-winbuild-cmake`.
   The DLL is named `libmpv-2.dll` (not `mpv-2.dll` as the spec assumed).
2. **`demuxer-lavf-o` syntax**: spec's colon separators are invalid — mpv key-value
   list options are comma-separated. Fixed in code.
3. **`probesize`**: spec's 32768 is ~40 ms of data at 6 Mbps 1080p60 (demuxer can't
   reach the first keyframe — every loadfile failed). App uses
   `probesize=5000000,analyzeduration=2000000`.
4. **Event IDs**: constants follow `include/mpv/client.h`
   (`MPV_EVENT_PROPERTY_CHANGE=22`, etc.).
5. **Audio-device timing**: set right after `mpv_initialize` (audio-device-list only
   exists then), not before; runtime switching via `set audio-device` per spec.
6. **v1.1 redesign per user request**: single-file self-contained exe (libmpv bundled),
   first-run wizard replaces manual configuration, MediaMTX relay downloaded and
   managed automatically for the Aitum path, VB-Cable one-click installer, tray menu.
   No JSON hand-editing; settings file is internal-only. The spec's raw
   `extraMpvOptions` escape hatch lives under Settings → Advanced.

## Out of scope

obs-websocket Phase 2, virtual audio drivers (VB-Cable covers that), Discord bots,
browser/Electron stacks, OBS plugin development, session-mixer automation.
