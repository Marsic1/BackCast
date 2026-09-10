using System.Runtime.InteropServices;
using Backcast.Controls;

namespace Backcast;

/// <summary>
/// Borderless main window: 36px dark header (status pill, title, topmost pin,
/// minimize, close), mpv video panel, status card when not LIVE, dark
/// right-click menu, F9/F10 hotkeys, tray icon. Everything user-facing is
/// derived from AppSettings (transport/port/path) — no URL editing.
/// </summary>
internal sealed class MainForm : Form
{
    private const int HeaderHeight = 44;

    private readonly AppSettings _settings;
    private MpvPlayer? _player;

    private readonly Panel _videoPanel = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Panel _header;
    private readonly StatusPill _pill = new();
    private readonly Label _titleLabel = new();
    private readonly HeaderButton _pinButton = new() { Glyph = HeaderButton.GlyphKind.Pin };
    private readonly HeaderButton _minButton = new() { Glyph = HeaderButton.GlyphKind.Minimize };
    private readonly HeaderButton _closeButton = new() { Glyph = HeaderButton.GlyphKind.Close, Danger = true };

    private readonly Panel _statusCard = new();
    private readonly Spinner _spinner = new();
    private readonly Label _statusLabel = new();
    private readonly Label _hintLabel = new();

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _relayFollow = new() { Interval = 2000 };
    private string? _activeRelayPath;
    // clean-share mode: while LIVE the header is hidden so a Discord window
    // share shows only the picture (the taskbar/title still carries status)
    private bool _cleanShare = true;

    // WM_NCHITTEST results for the borderless window
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11,
        HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15,
        HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int ResizeGrip = 6;

    private const int WMNclButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public MainForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Backcast";
        Icon = LoadEmbeddedIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bg;
        Font = Theme.Font();
        MinimumSize = new Size(420, 260);
        DoubleBuffered = true;
        KeyPreview = true;

        // window bounds from settings (clamped to a visible screen)
        var w = _settings.Window;
        Size = new Size(Math.Max(w.W, MinimumSize.Width), Math.Max(w.H, MinimumSize.Height));
        if (w.X is int x && w.Y is int y && ScreenIsValid(x, y, Size))
            Location = new Point(x, y);
        else
            StartPosition = FormStartPosition.CenterScreen;
        TopMost = _settings.AlwaysOnTop;
        _pinButton.ActiveState = TopMost;

        // ---- header ----
        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = Theme.BgPanel,
        };
        _header.MouseDoubleClick += (_, e) => ToggleMaximize();
        _header.MouseDown += HeaderDrag;
        _header.MouseClick += (_, e) => ShowBodyMenu(_header.PointToScreen(e.Location));

        _pill.RetryRequested += (_, _) => ReloadPlayer();
        _pill.Location = new Point(12, (HeaderHeight - _pill.Height + 1) / 2);

        _titleLabel.Text = $"{_settings.DisplayName} · OFF-AIR HANGOUT";
        _titleLabel.ForeColor = Theme.Fg;
        _titleLabel.AutoSize = false;
        _titleLabel.Height = HeaderHeight;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Location = new Point(_pill.Right + 10, 0);
        _titleLabel.Width = 360;
        _titleLabel.MouseDown += HeaderDrag;
        _titleLabel.MouseDoubleClick += (_, e) => ToggleMaximize();

        _closeButton.Click += (_, _) => Close();
        _minButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _pinButton.Click += (_, _) => ToggleTopmost();

        _header.Controls.Add(_pill);
        _header.Controls.Add(_titleLabel);
        _header.Controls.Add(_pinButton);
        _header.Controls.Add(_minButton);
        _header.Controls.Add(_closeButton);
        // WinForms Z-order: earlier-added = on top. The wide title label
        // would otherwise swallow clicks on the pin/minimize buttons.
        _closeButton.BringToFront();
        _minButton.BringToFront();
        _pinButton.BringToFront();
        // the docked header's width settles on its own schedule — relayout
        // the header on every layout pass (including the first paint)
        _header.Layout += (s, e) => LayoutHeaderButtons();
        _header.Resize += (s, e) => LayoutHeaderButtons();

        // ---- video + status card ----
        _videoPanel.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowBodyMenu(_videoPanel.PointToScreen(e.Location));
        };

        _statusCard.AutoSize = false;
        _statusCard.Size = new Size(300, 150);
        _statusCard.BackColor = Theme.BgPanel;
        _statusCard.Visible = false;

        _spinner.Location = new Point((300 - _spinner.Width) / 2, 22);
        _statusLabel.Text = "Waiting for OBS stream…";
        _statusLabel.ForeColor = Theme.Fg;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(300, 20);
        _statusLabel.Location = new Point(0, 68);
        _hintLabel.ForeColor = Theme.Gray;
        _hintLabel.TextAlign = ContentAlignment.MiddleCenter;
        _hintLabel.AutoSize = false;
        _hintLabel.Size = new Size(288, 52);
        _hintLabel.Location = new Point(6, 92);

        _statusCard.Controls.Add(_spinner);
        _statusCard.Controls.Add(_statusLabel);
        _statusCard.Controls.Add(_hintLabel);

        // dock order = layout order: Top FIRST so the Fill panel is laid
        // out BELOW the header (video starts under it, not behind it — the
        // top of the video used to be cut). Fill still z-sits above the
        // video surface for clicks, header stays topmost via BringToFront.
        Controls.Add(_header);
        Controls.Add(_videoPanel);
        Controls.Add(_statusCard);
        _header.BringToFront();

        // ---- tray (WebStage-style) ----
        _tray = new NotifyIcon
        {
            Text = "Backcast",
            Icon = LoadEmbeddedIcon() ?? Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? SystemIcons.Application,
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowFromTray();

        WatchTopEdge();
        Load += OnLoad;
        _relayFollow.Tick += (_, _) => FollowPublishedPath();
        FormClosing += OnFormClosing;
        Resize += OnResize;
        KeyDown += OnKeyDown;
        ResizeEnd += (_, _) => SaveBounds();
    }

    /// <summary>
    /// The embedded multi-size app.ico (16-256px): the tray, titlebar and
    /// taskbar each get the right size instead of a stretched 32px
    /// ExtractAssociatedIcon image.
    /// </summary>
    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("Backcast.app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null; // fall back to the exe icon
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        Theme.SetRoundedCorners(Handle, 8);
        OnResize(EventArgs.Empty); // buttons + title laid out before first paint
        ApplyHotkeys();
        if (_settings.Window.Maximized) WindowState = FormWindowState.Maximized;
        CreatePlayer();

        if (_settings.FirstRun)
        {
            using var wizard = new FirstRunWizard(_settings, _player!);
            wizard.ShowDialog(this);
            // transport (and maybe audio device) changed: rebuild the player
            _player?.Dispose();
            _player = null;
            CreatePlayer();
        }
    }

    private void CreatePlayer()
    {
        // relay transport: the local MediaMTX relay must be up before mpv dials RTSP
        if (_settings.UseRelay)
        {
            if (MediaMtx.IsDownloaded)
            {
                if (!MediaMtx.Start())
                {
                    _statusLabel.Text = "Relay failed to start";
                    _hintLabel.Text = "The local relay could not start (details in %TEMP%\\Backcast.log). " +
                                      "Try again, or use the Direct connection in Settings.";
                    SetState(PlaybackState.Error, "relay failed to start");
                    return;
                }
            }
            else
            {
                _statusLabel.Text = "Relay not installed";
                _hintLabel.Text = "Right-click → Settings… to download the relay, or rerun the wizard.";
                SetState(PlaybackState.Waiting);
            }
        }

        // mpv needs its host visible to create the render surface
        _videoPanel.Visible = true;
        try
        {
            // relay: follow the path the publisher actually uses (RTMP keys
            // get appended to the server path, e.g. discord/discord)
            string url = _settings.PlaybackUrl;
            if (_settings.UseRelay)
            {
                string path = MediaMtx.LatestPublishedPath ?? _settings.RtmpPath;
                _activeRelayPath = path;
                url = $"rtsp://127.0.0.1:8554/{path}";
                Log.Write($"relay playback path: {path}");
            }
            _player = new MpvPlayer(_videoPanel.Handle, url, _settings.ExtraMpvOptions);
        }
        catch (Exception ex)
        {
            Log.Write($"player init failed: {ex.GetType().Name}: {ex.Message}");
            SetState(PlaybackState.Error, $"Player failed to start — {ex.Message}");
            return;
        }

        _player.StateChanged += (state, detail) => BeginInvoke(() => SetState(state, detail));
        ApplyAudioDevice();
        _relayFollow.Enabled = _settings.UseRelay;

        if (_settings.AutoStart)
            _player.Load();
        else
            SetState(PlaybackState.Waiting, "press Reload (F9) to start");

        // the waiting card only ever appeared on state *transitions* — with
        // autostart the player begins (and often stays) in WAITING without
        // one, so the spinner/status card never showed. Paint it now.
        if (_settings.AutoStart)
            SetState(PlaybackState.Waiting);
    }

    /// <summary>
    /// Adapts the window shape when the OBS canvas aspect changes (e.g.
    /// 16:9 → vertical): keep the window's AREA, adjust w/h to the new
    /// aspect, clamp to screen. Skipped when maximized (user's choice wins)
    /// and only when the aspect actually differs meaningfully — this never
    /// fights ordinary manual resizes.
    /// </summary>
    private double _lastFitAspect;
    private void FitWindowToStream(double aspect)
    {
        if (WindowState == FormWindowState.Maximized) return;
        if (Math.Abs(aspect - _lastFitAspect) < 0.02) return;
        _lastFitAspect = aspect;

        int area = Width * Height;
        int newW = (int)Math.Round(Math.Sqrt(area * aspect));
        int newH = (int)Math.Round(Math.Sqrt(area / aspect));

        var screen = Screen.FromControl(this).WorkingArea;
        newW = Math.Min(newW, screen.Width);
        newH = Math.Min(newH, screen.Height);
        if (newW < MinimumSize.Width) newW = MinimumSize.Width;
        if (newH < MinimumSize.Height) newH = MinimumSize.Height;

        int x = Math.Clamp(Location.X, screen.X, screen.Right - newW);
        int y = Math.Clamp(Location.Y, screen.Y, screen.Bottom - newH);
        SetBounds(x, y, newW, newH);
    }

    /// <summary>Exact device from the wizard/settings, else hint match, else default.</summary>
    private void ApplyAudioDevice()
    {
        if (_player == null) return;
        var devices = _player.GetAudioDevices();
        MpvPlayer.AudioDevice? chosen = null;
        if (!string.IsNullOrEmpty(_settings.Audio.SelectedDevice))
            chosen = devices.FirstOrDefault(d => d.Name == _settings.Audio.SelectedDevice);
        chosen ??= AudioEndpointPicker.Find(devices, _settings.Audio.EndpointHint);

        if (chosen != null)
        {
            _player.SetAudioDevice(chosen.Name);
        }
        else
        {
            _player.SetAudioDevice("auto");
            _audioHintPending = true; // shown on the status card while WAITING
        }
    }

    private bool _audioHintPending;

    private void ReloadPlayer()
    {
        if (_player == null) { CreatePlayer(); return; }
        _player.Load();
    }

    /// <summary>
    /// While waiting on the relay transport, watch what the publisher is
    /// actually pushing and follow it — RTMP clients append the stream key
    /// to the server path, so the configured path is often doubled.
    /// </summary>
    private void FollowPublishedPath()
    {
        if (!_settings.UseRelay) return;
        if (MediaMtx.LatestPublishedPath is not { } path) return;
        if (path == _activeRelayPath) return;

        Log.Write($"publisher moved to path '{path}' — reconnecting");
        _player?.Dispose();
        _player = null;
        CreatePlayer();
    }

    private void SetState(PlaybackState state, string detail = "")
    {
        _pill.State = state;
        _titleLabel.Text = $"{_settings.DisplayName} · OFF-AIR HANGOUT";
        switch (state)
        {
            case PlaybackState.Live:
                Text = $"🔴 {_settings.DisplayName} — LIVE · Backcast";
                _tray.Text = "Backcast — LIVE";
                _statusCard.Visible = false;
                ApplyCleanShare(true);
                // mpv renders into a NATIVE child window that sits above any
                // WinForms control — the only way to show the waiting card is
                // to hide the video panel itself while there's no picture
                _videoPanel.Visible = true;
                break;
            case PlaybackState.Error:
                Text = $"❌ {_settings.DisplayName} — Backcast";
                ApplyCleanShare(false);
                _tray.Text = "Backcast — error";
                _statusLabel.Text = "Playback error";
                _hintLabel.Text = detail.Length > 0 ? detail : "Reload (F9) or check the OBS setup in Settings.";
                ShowStatusCard();
                break;
            default:
                Text = $"⏳ {_settings.DisplayName} — waiting for OBS";
                _tray.Text = "Backcast — waiting for OBS";
                ApplyCleanShare(false);
                _statusLabel.Text = "Waiting for OBS stream…";
                _hintLabel.Text = detail.Length > 0 ? detail :
                    (_audioHintPending
                        ? "Tip: no silent audio output is set — pick one in Settings → Audio."
                        : "Start streaming in OBS and this window lights up by itself.");
                ShowStatusCard();
                break;
        }
    }

    /// <summary>
    /// Clean share: hide the header while LIVE so a Discord window share
    /// contains only the video. Moving the mouse to the top edge reveals it
    /// transiently (buttons stay reachable mid-stream).
    /// </summary>
    private void ApplyCleanShare(bool live)
    {
        if (!_cleanShare)
        {
            SetHeaderShown(true);
            return;
        }
        SetHeaderShown(!live);
    }

    /// <summary>
    /// Visibility alone doesn't help: a hidden DOCKED control still reserves
    /// its layout strip, leaving a dead band at the top of the share. Toggle
    /// the header's dock participation instead.
    /// </summary>
    private void SetHeaderShown(bool shown)
    {
        if (_headerShown == shown) return;
        _headerShown = shown;
        _header.Visible = shown;
        // detach from layout when hidden so Fill expands to the full window
        if (shown)
        {
            _header.Dock = DockStyle.Top;
            _header.BringToFront();
        }
        else
        {
            _headerPeeked = false;
            _header.Dock = DockStyle.None;
            _header.SendToBack();
        }
        PerformLayout();
    }

    private bool _headerShown = true;
    // While LIVE the native mpv child window owns the entire client area —
    // the form never sees WM_MOUSEMOVE, and no WinForms control can be
    // transparent over a native sibling (an overlay would paint a solid
    // band over the video). Poll the GLOBAL cursor position instead: it is
    // valid regardless of which window is under the mouse.
    private const int HoverZoneHeight = 100;
    private readonly System.Windows.Forms.Timer _mousePoll = new() { Interval = 60 };
    private readonly System.Windows.Forms.Timer _rehide = new() { Interval = 500 };
    private bool _headerPeeked;

    private void WatchTopEdge()
    {
        _mousePoll.Tick += (_, _) => PollTopEdge();
        _rehide.Tick += (_, _) =>
        {
            _rehide.Stop();
            if (_pill.State != PlaybackState.Live || !_headerPeeked) return;
            if (!CursorInHoverZone()) { _headerPeeked = false; SetHeaderShown(false); }
        };
        _mousePoll.Start();
    }

    private void PollTopEdge()
    {
        if (WindowState == FormWindowState.Minimized) return;
        if (CursorInHoverZone())
        {
            if (!_headerShown)
            {
                _headerPeeked = true;
                _rehide.Stop();
                SetHeaderShown(true);
            }
        }
        else if (_headerPeeked && _headerShown)
        {
            // grace so a quick dip below the zone doesn't flicker the header
            if (!_rehide.Enabled) _rehide.Start();
        }
    }

    private bool CursorInHoverZone()
    {
        var pt = PointToClient(Cursor.Position);
        return pt.X >= 0 && pt.X <= ClientSize.Width
            && pt.Y >= 0 && pt.Y <= HoverZoneHeight;
    }

    private void ShowStatusCard()
    {
        // mpv's native child window covers everything: hide the host panel
        // so the waiting card + spinner are actually visible
        _videoPanel.Visible = false;
        _statusCard.Location = new Point(
            (ClientSize.Width - _statusCard.Width) / 2,
            HeaderHeight + Math.Max(0, (ClientSize.Height - HeaderHeight - _statusCard.Height) / 2));
        _statusCard.Visible = true;
        _statusCard.BringToFront();
    }

    // ---- window chrome ----

    private void OnResize(object? sender, EventArgs e)
    {
        LayoutHeaderButtons();
        if (WindowState != FormWindowState.Minimized && _statusCard.Visible)
            ShowStatusCard();
    }

    /// <summary>
    /// Buttons right-aligned in the header, then the title measured against
    /// them. Runs on form resize, header resize AND header layout so the
    /// first paint already has every button in place.
    /// </summary>
    private bool _layoutingHeader;
    private void LayoutHeaderButtons()
    {
        if (_header is null || IsDisposed || _layoutingHeader) return;
        _layoutingHeader = true;
        try
        {
            _closeButton.Location = new Point(_header.Width - _closeButton.Width - 8, 2);
            _minButton.Location = new Point(_closeButton.Left - _minButton.Width, 2);
            _pinButton.Location = new Point(_minButton.Left - _pinButton.Width, 2);
            _titleLabel.Width = Math.Max(0, _closeButton.Left - 16 - _titleLabel.Left);
        }
        finally
        {
            _layoutingHeader = false;
        }
    }

    private void HeaderDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        _ = SendMessage(Handle, WMNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    private void ToggleTopmost()
    {
        TopMost = !TopMost;
        _pinButton.ActiveState = TopMost;
        _settings.AlwaysOnTop = TopMost;
        _settings.Save();
    }

    /// <summary>Borderless hit test: edges resize, header drags, body is client.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HkReload: ReloadPlayer(); return;
                case HkTopmost: ToggleTopmost(); return;
            }
        }
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            var screenPt = new Point((int)m.LParam);
            Point pt = PointToClient(screenPt);
            if (pt.Y <= HeaderHeight)
            {
                bool onControl = _header.Controls.Cast<Control>()
                    .Any(c => c.Visible && c.Bounds.Contains(pt));
                if (!onControl && pt.X > ResizeGrip && pt.X < Width - ResizeGrip)
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }
            }
            int left = pt.X <= ResizeGrip ? 1 : 0;
            int right = pt.X >= Width - ResizeGrip ? 1 : 0;
            int top = pt.Y <= ResizeGrip ? 1 : 0;
            int bottom = pt.Y >= Height - ResizeGrip ? 1 : 0;
            if (left + right + top + bottom >= 2)
            {
                m.Result = (IntPtr)(HTTOPLEFT + (right << 1) + (bottom << 2) + (left & top));
                return;
            }
            if (left != 0) { m.Result = (IntPtr)HTLEFT; return; }
            if (right != 0) { m.Result = (IntPtr)HTRIGHT; return; }
            if (top != 0) { m.Result = (IntPtr)HTTOP; return; }
            if (bottom != 0) { m.Result = (IntPtr)HTBOTTOM; return; }
        }
        base.WndProc(ref m);
    }

    // ---- menus / hotkeys / tray ----

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = DarkMenu.Create();
        var status = new ToolStripMenuItem("○  Waiting for OBS…")
        { Enabled = false, Tag = "status:waiting" };
        // the menu is built once at startup: refresh the status line from the
        // live playback state every time it opens (it used to be frozen at
        // whatever the state was at launch); colors ride the Tag through the
        // renderer (waiting = amber, not gray)
        menu.Opening += (_, _) =>
        {
            switch (_pill.State)
            {
                case PlaybackState.Live:
                    status.Text = "●  LIVE";
                    status.Tag = "status:live";
                    break;
                case PlaybackState.Error:
                    status.Text = "✕  Playback error";
                    status.Tag = "status:error";
                    break;
                default:
                    status.Text = "○  Waiting for OBS…";
                    status.Tag = "status:waiting";
                    break;
            }
        };
        var show = new ToolStripMenuItem("Show window", null, (_, _) => ShowFromTray());
        var reload = new ToolStripMenuItem("Reload stream", null, (_, _) => ReloadPlayer())
        { ShortcutKeyDisplayString = HotkeyCombo.Parse(_settings.Hotkeys.Reload).ToString() };
        var top = new ToolStripMenuItem("Always on top", null, (_, _) => ToggleTopmost())
        {
            Checked = TopMost,
            ShortcutKeyDisplayString = HotkeyCombo.Parse(_settings.Hotkeys.ToggleTopmost).ToString(),
        };
        var copy = new ToolStripMenuItem("Copy OBS setup URL", null, (_, _) => Clipboard.SetText(_settings.ObsUrl));
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => BeginInvoke(OpenSettings));
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => Close());
        menu.Items.AddRange(new ToolStripItem[] { status, new ToolStripSeparator(), show, reload, top, copy, settings, new ToolStripSeparator(), exit });
        return menu;
    }

    private void ShowFromTray()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Visible = true;
        Activate();
    }

    private void ShowBodyMenu(Point screenPt)
    {
        using var menu = DarkMenu.Create();
        menu.Items.Add(new ToolStripMenuItem("Reload stream", null, (_, _) => ReloadPlayer())
        { ShortcutKeyDisplayString = HotkeyCombo.Parse(_settings.Hotkeys.Reload).ToString() });
        var topItem = new ToolStripMenuItem("Always on top", null, (_, _) => ToggleTopmost())
        {
            Checked = TopMost,
            ShortcutKeyDisplayString = HotkeyCombo.Parse(_settings.Hotkeys.ToggleTopmost).ToString(),
        };
        menu.Items.Add(topItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Copy OBS setup URL", null, (_, _) =>
            Clipboard.SetText(_settings.ObsUrl)));
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        DarkMenu.Show(menu, screenPt);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // hotkeys are GLOBAL (RegisterHotKey, WebStage-style) — they fire via
        // WM_HOTKEY even when the native mpv child holds focus. This in-form
        // path is only a fallback for when registration failed (combo already
        // in use by another app).
        if (!_hotkeysRegistered)
        {
            if (MatchesHotkey(e, _settings.Hotkeys.Reload))
            {
                ReloadPlayer();
                e.Handled = true;
            }
            else if (MatchesHotkey(e, _settings.Hotkeys.ToggleTopmost))
            {
                ToggleTopmost();
                e.Handled = true;
            }
        }
    }

    private static bool MatchesHotkey(KeyEventArgs e, string hotkey) =>
        HotkeyCombo.Parse(hotkey).Matches(e.Modifiers, e.KeyCode);

    // ---- global hotkeys (WebStage: RegisterHotKey + WM_HOTKEY) ----

    private const int HkReload = 1, HkTopmost = 2;
    private bool _hotkeysRegistered;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4,
        MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// (Re)register global hotkeys from settings. WebStage behavior: a combo
    /// already owned by another app logs a line and is skipped (the in-form
    /// KeyDown fallback still covers it while our window has focus).
    /// </summary>
    private void ApplyHotkeys()
    {
        if (IsDisposed) return;
        if (_hotkeysRegistered)
        {
            UnregisterHotKey(Handle, HkReload);
            UnregisterHotKey(Handle, HkTopmost);
            _hotkeysRegistered = false;
        }
        if (!IsHandleCreated) return;

        _hotkeysRegistered = true;
        foreach (var (id, spec) in new[] { (HkReload, _settings.Hotkeys.Reload), (HkTopmost, _settings.Hotkeys.ToggleTopmost) })
        {
            var hk = HotkeyCombo.Parse(spec);
            if (!hk.IsSet) continue;
            uint mods = (hk.Modifiers.HasFlag(Keys.Control) ? MOD_CONTROL : 0)
                      | (hk.Modifiers.HasFlag(Keys.Shift) ? MOD_SHIFT : 0)
                      | (hk.Modifiers.HasFlag(Keys.Alt) ? MOD_ALT : 0)
                      | MOD_NOREPEAT;
            if (!RegisterHotKey(Handle, id, mods, (uint)hk.Key))
            {
                _hotkeysRegistered = false;
                Log.Write($"hotkey '{hk}' registration failed (already in use?) — in-window fallback active");
            }
        }
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings, _player);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            // hotkeys may have changed: re-register globals before restart
            ApplyHotkeys();
            // transport/port/audio changes: restart player (and the relay)
            _player?.Dispose();
            _player = null;
            if (!_settings.UseRelay) MediaMtx.Stop();
            CreatePlayer();
        }
        _titleLabel.Text = $"{_settings.DisplayName} · OFF-AIR HANGOUT";
        TopMost = _settings.AlwaysOnTop;
        _pinButton.ActiveState = TopMost;
    }

    // ---- persistence / teardown ----

    private void SaveBounds()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.Window.X = Location.X;
            _settings.Window.Y = Location.Y;
            _settings.Window.W = Width;
            _settings.Window.H = Height;
        }
        _settings.Window.Maximized = WindowState == FormWindowState.Maximized;
        _settings.Save();
    }

    private static bool ScreenIsValid(int x, int y, Size size)
    {
        var rect = new Rectangle(x, y, Math.Min(size.Width, 200), Math.Min(size.Height, 100));
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveBounds();
        UnregisterHotKey(Handle, HkReload);
        UnregisterHotKey(Handle, HkTopmost);
        _relayFollow.Stop();
        _mousePoll.Stop();
        _rehide.Stop();
        _player?.Dispose();
        MediaMtx.Stop();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
