# BackCast

> The opposite of broadcast — your OBS scene with friends on Discord, without going live.

One window in OBS carries **video + audio** to a Discord screen-share at **~1 frame of latency**. No encoder round-trip, no relay, no VB-Cable.

**How it works (v0.3):** an OBS plugin (`backcast-projector.dll`) opens a borderless window that renders the OBS **program mix** through `obs_render_main_texture()` — the exact code path OBS's own projector uses — and taps the OBS **master audio mix** (`audio_output_connect`, mix 0), playing it through its own WASAPI stream inside the OBS process. Because the window and the audio both live in `obs64.exe`, a single Discord window-share captures both. The BackCast.exe app is just the installer and settings UI for the plugin.

Previous architecture (UDP/RTSP + mpv) carried a 1.5–2 s delay inherent to the TS/RTSP pipeline — confirmed with ffplay, so it wasn't the app. That code is gone (see git history, v0.2 tag).

---

## For the user

## Installing — two ways

### A. The BackCast app (recommended for most people)

**Release = one file** (`BackCast.exe`, everything inside — including the plugin). Copy it anywhere, double-click.

1. **First launch** — the wizard finds your OBS (standard or portable — a running OBS is detected automatically) and installs the plugin into it.
2. **In OBS**: `Tools → Backcast window` (or set a hotkey in OBS `Settings → Hotkeys` — "Backcast: toggle window", "Backcast: toggle always on top").
3. **In Discord**: share the **Backcast** window with **sound on**.

The app also offers the **one-click VB-Cable download/install** (Settings → Audio, or the button on the main window) for when your machine has no spare audio output.

- Video is a frame behind your scene; audio is the exact master mix — the same thing your stream/recording hears.
- **When closed, the plugin uses no resources at all** — no window, no audio stream, no threads. Open it only when you want to share.
- The plugin plays the mix to an **unused audio device** (auto-picked; TV/HDMI/dummy outputs are preferred) so you don't hear everything twice. Change it in Backcast → Settings → Audio, or right-click the window → Audio device.
- **Keep audio monitoring OFF on your OBS sources** — Discord captures everything OBS plays, so monitored sources would double into the share.
- Settings/tray: repair or uninstall the plugin, change the audio device, re-run the wizard.

Diagnostic log: `%TEMP%\Backcast.log`. App settings: `%APPDATA%\Backcast\settings.json`. Plugin settings (per OBS install): `config\obs-studio\plugin_config\backcast-projector\config.json` (portable) or the same path under `%APPDATA%` (standard).

### B. Direct plugin install (plugin managers / manual copy)

Grab `backcast-projector-<version>-windows-x64.zip` from the releases and:

- **Plugin manager (StreamUP etc.)**: the zip uses the standard plugin layout they expect — point the manager at it.
- **Standard OBS, manual**: extract, copy the `backcast-projector` folder into `C:\ProgramData\obs-studio\plugins\` (no admin needed; OBS loads `ProgramData\obs-studio\plugins\<name>\bin\64bit\<name>.dll`).
- **Portable OBS, manual**: copy `bin\64bit\backcast-projector.dll` → `<OBS root>\obs-plugins\64bit\` and `data\locale\en-US.ini` → `<OBS root>\data\obs-plugins\backcast-projector\locale\`.

Everything the plugin needs is configurable from its own right-click menu (audio device, rename, always on top), so a direct install works without the app — you only miss the wizard, the VB-Cable one-click, and the settings UI.

## For the developer

Requirements: .NET 8 SDK, CMake ≥ 3.28, Visual Studio 2022/2026 (C++ workload), Windows 10/11 x64.

```bash
# 1. build the OBS plugin (first configure downloads prebuilt libobs via buildspec)
cd plugin
cmake --preset windows-x64
cmake --build --preset windows-x64 --config Release

# 2. build/publish the app (embeds the plugin DLL as a resource)
cd ../src/Backcast
dotnet publish -c Release -r win-x64 --self-contained

# 3. package the plugin zip for direct installs / plugin managers
cd ../..
powershell -File tools/package-plugin.ps1
```

The plugin's cmake presets pin the Windows SDK version — adjust `architecture` in `plugin/CMakePresets.json` if yours differs (search for `version=`).

Repo layout:

```
plugin/          C OBS plugin (obs-plugintemplate base)
  src/plugin-main.c   lazy window + obs_display + draw callback + lifecycle
  src/audio-out.c     master-mix WASAPI renderer + endpoint picking
src/Backcast/    .NET 8 WinForms installer/settings/tray (WebStage dark UI)
tools/           (legacy test transmitters from the mpv era)
```

### Why not the alternatives

- **Spout2** — zero-latency video but no audio transport; you'd rebuild the audio path anyway.
- **NDI (DistroAV)** — 150 ms–2 s and CPU-heavy; same latency class as the old UDP/RTSP pipeline.
- **Reparenting an OBS projector** (`SetParent`) — resets OBS's DPI awareness and breaks input handling.
- **obs-websocket** — screenshots only, no live video.

## Known limitations

- Discord captures **all** of OBS's audio: monitoring on sources mixes into the share (wizard warns).
- If no spare audio endpoint exists, the plugin falls back to the default device — you'll hear the mix locally until you pick another device in settings.
- Hotkeys live in OBS's own hotkey system (by design — they work anywhere in OBS and are registered/unregistered with the window's lifecycle).

## History

- **v0.1** — UDP/RTSP + mpv player, WebStage UI, wizard, relay.
- **v0.2** — latency work, global hotkeys, clean share; confirmed pipeline floor ~1.5 s.
- **v0.3** — OBS plugin architecture: ~1 frame video, in-process master-mix audio, start/stop with zero idle cost, portable support. The exe becomes installer/config UI.
