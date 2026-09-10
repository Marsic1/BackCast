using Backcast.Controls;

namespace Backcast;

/// <summary>
/// Settings window — WebStage layout: wide (580px), section headers with
/// divider rules, rounded DarkInput fields, generous spacing.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly MpvPlayer? _player;

    private readonly DarkCombo _transport = new();
    private readonly DarkNumber _udpPort = new();
    private readonly DarkInput _rtmpPath = new();
    private readonly DarkInput _obsUrl = new() { ReadOnly = true, ValueColor = Theme.Accent };
    private readonly DarkButton _copyUrl = new() { Text = "Copy", Size = new Size(80, 32) };
    private readonly DarkButton _relayDownload = new() { Text = "Download relay", Size = new Size(150, 32) };
    private readonly Label _relayStatus = new();

    private readonly DarkCombo _audioMode = new();
    private readonly DarkCombo _deviceList = new();
    private readonly DarkButton _refreshDevices = new() { Text = "Refresh", Size = new Size(90, 32) };
    private readonly DarkButton _installCable = new() { Text = "Install VB-Cable", Size = new Size(150, 32) };

    private readonly DarkInput _displayName = new();
    private readonly CheckBox _autoStart = new() { Text = "Start playing automatically on launch" };
    private readonly CheckBox _onTop = new() { Text = "Always on top" };
    private readonly HotkeyBox _reloadKey = new();
    private readonly HotkeyBox _topmostKey = new();

    private readonly DarkInput _extraOptions = new() { Multiline = true, Height = 68 };
    private readonly DarkButton _rerunWizard = new() { Text = "Run setup wizard again", Size = new Size(190, 32) };

    private List<MpvPlayer.AudioDevice> _devices = new();

    private const int RowH = 34, Edge = 24, LabelX = 24, InputX = 190, InputW = 500;

    public SettingsForm(AppSettings settings, MpvPlayer? player)
    {
        _settings = settings;
        _player = player;

        Text = "Backcast — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 760);
        BackColor = Theme.Bg;
        Font = Theme.Font();
        ShowInTaskbar = false;
        AutoScroll = true;
        Load += (_, _) => Theme.EnableDarkFrame(Handle);

        int y = 22;
        BuildConnection(ref y);
        y = Section(y, "AUDIO");
        BuildAudio(ref y);
        y = Section(y, "WINDOW");
        BuildWindow(ref y);
        y = Section(y, "ADVANCED");
        BuildAdvanced(ref y);

        y += 6;
        WireWizardButton();
        _rerunWizard.Text = "↻   Run the setup wizard again";
        _rerunWizard.Size = new Size(ClientSize.Width - Edge * 2, 42);
        _rerunWizard.Location = new Point(Edge, y);
        Controls.Add(_rerunWizard);
        y += 54;
        var ok = new DarkButton { Text = "OK", Size = new Size(104, 34), Location = new Point(ClientSize.Width - Edge - 224, y) };
        var cancel = new DarkButton { Text = "Cancel", Size = new Size(104, 34), Location = new Point(ClientSize.Width - Edge - 112, y) };
        ok.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(ClientSize.Width, y + 34 + Edge);

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            // Esc cancels an in-progress hotkey capture FIRST — closing the
            // window mid-capture loses the keystroke (WebStage dialog rule)
            if (_reloadKey.IsCapturing || _topmostKey.IsCapturing) return;
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
        };

        RefreshDevices();
        UpdateDerived();
        UpdateRelayState();
    }

    /// <summary>
    /// The download button and status reflect the real relay state whenever
    /// the dialog opens (previously only correct on the very first open:
    /// a re-opened dialog showed a pressable button and a gray "ready").
    /// </summary>
    private void UpdateRelayState()
    {
        if (MediaMtx.IsDownloaded)
        {
            _relayDownload.Enabled = false;
            _relayStatus.Text = "relay ready ✓";
            _relayStatus.ForeColor = Theme.Accent;
        }
        else
        {
            _relayDownload.Enabled = true;
            _relayStatus.Text = "not downloaded yet";
            _relayStatus.ForeColor = Theme.Gray;
        }
    }

    // ---- sections ----

    private int Section(int y, string title)
    {
        Label h = new()
        {
            Text = title,
            ForeColor = Theme.Accent,
            Font = Theme.FontBold(),
            AutoSize = true,
            Location = new Point(Edge, y),
        };
        Controls.Add(h);
        y += 26;
        Panel rule = new()
        {
            AutoSize = false,
            Size = new Size(ClientSize.Width - Edge * 2, 1),
            BackColor = Theme.Sep,
            Location = new Point(Edge, y),
        };
        Controls.Add(rule);
        return y + 12;
    }

    /// <summary>A wrapped gray note measured before the next row — cannot overlap.</summary>
    private int StackNote(int y, string text)
    {
        var size = TextRenderer.MeasureText(text, Theme.Font(),
            new Size(ClientSize.Width - Edge * 2 - LabelX, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        Label lbl = new()
        {
            Text = text,
            ForeColor = Theme.Gray,
            Bounds = new Rectangle(LabelX, y, ClientSize.Width - Edge * 2 - LabelX, size.Height),
            AutoSize = false,
        };
        Controls.Add(lbl);
        return y + size.Height + 12;
    }

    private Label RowLabel(ref int y, string text)
    {
        Label lbl = new()
        {
            Text = text,
            ForeColor = Theme.Fg,
            AutoSize = true,
            Location = new Point(LabelX, y + 8),
        };
        Controls.Add(lbl);
        y += RowH + 10;
        return lbl;
    }

    private void Place(Control input, int y, int? width = null)
    {
        input.Location = new Point(InputX, y);
        input.Size = new Size(width ?? InputW, RowH);
        Controls.Add(input);
    }

    private void BuildConnection(ref int y)
    {
        y = Section(y, "CONNECTION");
        _transport.Items.Add("Direct  —  OBS streams straight to Backcast (recommended)");
        _transport.Items.Add("Relay  —  for the Aitum Multistream plugin");
        _transport.SelectedIndex = _settings.UseRelay ? 1 : 0;
        _transport.SelectedIndexChanged += (_, _) => UpdateDerived();
        RowLabel(ref y, "How OBS connects");
        Place(_transport, y - RowH - 10);

        _udpPort.Minimum = 1024; _udpPort.Maximum = 65535;
        _udpPort.Value = _settings.UdpPort;
        _udpPort.ValueChanged += (_, _) => UpdateDerived();
        RowLabel(ref y, "UDP port");
        Place(_udpPort, y - RowH - 10, 120);

        _rtmpPath.Text = _settings.RtmpPath;
        _rtmpPath.TextChanged += (_, _) => UpdateDerived();
        RowLabel(ref y, "Relay stream key");
        Place(_rtmpPath, y - RowH - 10, 160);

        y = StackNote(y, "Paste this into OBS → Settings → Stream (Custom), or into an Aitum Custom RTMP destination:");

        _obsUrl.Text = _settings.ObsUrl;
        _obsUrl.Location = new Point(InputX, y);
        _obsUrl.Size = new Size(InputW - 96, RowH);
        _copyUrl.Location = new Point(InputX + InputW - 88, y);
        _copyUrl.Click += (_, _) => Clipboard.SetText(_obsUrl.Text);
        Controls.Add(_obsUrl);
        Controls.Add(_copyUrl);
        y += RowH + 10;

        _relayDownload.Click += (_, _) => DownloadRelay();
        _relayDownload.Location = new Point(InputX, y);
        _relayStatus.AutoSize = true;
        _relayStatus.ForeColor = Theme.Gray;
        // right of the install button (150 wide at InputX+105) — never on top of it
        _relayStatus.Location = new Point(InputX + 105 + 150 + 12, y + 8);
        if (MediaMtx.IsDownloaded)
        {
            _relayDownload.Enabled = false;
            _relayStatus.Text = "relay ready ✓";
            _relayStatus.ForeColor = Theme.Accent;
        }
        else
        {
            _relayStatus.Text = "not downloaded yet";
            _relayStatus.ForeColor = Theme.Gray;
        }
        Controls.Add(_relayDownload);
        Controls.Add(_relayStatus);
        y += RowH + 4;
    }

    private void DownloadRelay()
    {
        _relayDownload.Enabled = false;
        _relayStatus.Text = "downloading…";
        _relayStatus.ForeColor = Theme.Amber;
        Task.Run(() =>
        {
            try
            {
                MediaMtx.EnsureDownloaded();
                BeginInvoke(UpdateRelayState);
            }
            catch (Exception ex)
            {
                BeginInvoke(() => { _relayStatus.Text = ex.Message; _relayStatus.ForeColor = Theme.Stop; _relayDownload.Enabled = true; });
            }
        });
    }

    private void BuildAudio(ref int y)
    {
        _audioMode.Items.Add("silentEndpoint — sound goes to a silent output (recommended)");
        _audioMode.Items.Add("voiceCable — sound rides the Discord voice channel");
        _audioMode.SelectedIndex = _settings.Audio.Mode == "voiceCable" ? 1 : 0;
        RowLabel(ref y, "Mode");
        Place(_audioMode, y - RowH - 10);

        RowLabel(ref y, "Silent output device");
        Place(_deviceList, y - RowH - 10);

        _refreshDevices.Click += (_, _) => RefreshDevices();
        _installCable.Click += (_, _) =>
        {
            try
            {
                VbCable.RunInstaller();
                _installCable.Text = "Installer launched — then Refresh";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VB-Cable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _refreshDevices.Location = new Point(InputX, y);
        _installCable.Location = new Point(InputX + 105, y);
        Controls.Add(_refreshDevices);
        Controls.Add(_installCable);
        y += RowH + 4;

        y = StackNote(y, "Never mute Backcast in the Windows volume mixer — that mutes what your friends hear.");
    }

    private void BuildWindow(ref int y)
    {
        _displayName.Text = _settings.DisplayName;
        RowLabel(ref y, "Display name");
        Place(_displayName, y - RowH - 10, 220);

        _autoStart.Checked = _settings.AutoStart;
        _onTop.Checked = _settings.AlwaysOnTop;
        StyleCheckBox(_autoStart);
        StyleCheckBox(_onTop);
        _autoStart.Location = new Point(InputX, y);
        Controls.Add(_autoStart);
        y += 26;
        _onTop.Location = new Point(InputX, y);
        Controls.Add(_onTop);
        y += 34;

        RowLabel(ref y, "Reload hotkey");
        Place(_reloadKey, y - RowH - 10, 150);
        _reloadKey.Combo = HotkeyCombo.Parse(_settings.Hotkeys.Reload);
        AddHotkeyResetButton(ref y, _reloadKey, "F9");
        RowLabel(ref y, "Always-on-top hotkey");
        Place(_topmostKey, y - RowH - 10, 150);
        _topmostKey.Combo = HotkeyCombo.Parse(_settings.Hotkeys.ToggleTopmost);
        AddHotkeyResetButton(ref y, _topmostKey, "F10");
    }

    /// <summary>Small "Reset" chip right of a hotkey box — restores the default combo.</summary>
    private void AddHotkeyResetButton(ref int y, HotkeyBox box, string defaultCombo)
    {
        var reset = new DarkButton
        {
            Text = "Reset",
            Size = new Size(70, RowH),
            Location = new Point(InputX + 160, y - RowH),
        };
        reset.Click += (_, _) =>
        {
            box.Combo = HotkeyCombo.Parse(defaultCombo);
            box.Invalidate();
        };
        Controls.Add(reset);
    }

    private void BuildAdvanced(ref int y)
    {
        y = StackNote(y, "Extra mpv options (name=value, one per line) — leave empty unless you know why:");
        _extraOptions.Text = string.Join(Environment.NewLine, _settings.ExtraMpvOptions);
        _extraOptions.Location = new Point(InputX, y);
        _extraOptions.Size = new Size(InputW, 68);
        Controls.Add(_extraOptions);
        y += 80;

        y += 8;
    }

    private void WireWizardButton()
    {
        _rerunWizard.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
            using var wizard = new FirstRunWizard(_settings, _player!)
                { StartPosition = FormStartPosition.CenterParent };
            wizard.ShowDialog();
        };
    }

    // ---- data ----

    private void UpdateDerived()
    {
        bool relay = _transport.SelectedIndex == 1;
        _udpPort.Enabled = !relay;
        _rtmpPath.Enabled = relay;
        _obsUrl.Text = relay
            ? $"rtmp://127.0.0.1:1935/{_rtmpPath.Text.Trim()}"
            : $"udp://127.0.0.1:{(int)_udpPort.Value}?pkt_size=1316";
    }

    private void RefreshDevices()
    {
        try { _devices = (_player?.GetAudioDevices() ?? Enumerable.Empty<MpvPlayer.AudioDevice>()).ToList(); }
        catch { _devices = new List<MpvPlayer.AudioDevice>(); }
        _deviceList.Items.Clear();
        foreach (var d in _devices)
            _deviceList.Items.Add(d.Description);
        int idx = _devices.FindIndex(d => d.Name == _settings.Audio.SelectedDevice);
        if (idx < 0)
        {
            var hint = AudioEndpointPicker.Find(_devices, _settings.Audio.EndpointHint)
                ?? _devices.FirstOrDefault(d => d.Name != "auto");
            idx = hint != null ? _devices.IndexOf(hint) : -1;
        }
        if (idx >= 0) _deviceList.SelectedIndex = idx;
    }

    private void SaveAndClose()
    {
        _settings.Transport = _transport.SelectedIndex == 1 ? "rtmp" : "udp";
        _settings.UdpPort = (int)_udpPort.Value;
        _settings.RtmpPath = string.IsNullOrWhiteSpace(_rtmpPath.Text) ? "discord" : _rtmpPath.Text.Trim();
        _settings.Audio.Mode = _audioMode.SelectedIndex == 1 ? "voiceCable" : "silentEndpoint";
        if (_deviceList.SelectedIndex >= 0 && _deviceList.SelectedIndex < _devices.Count)
            _settings.Audio.SelectedDevice = _devices[_deviceList.SelectedIndex].Name;
        _settings.DisplayName = string.IsNullOrWhiteSpace(_displayName.Text) ? "Backcast" : _displayName.Text.Trim();
        _settings.AutoStart = _autoStart.Checked;
        _settings.AlwaysOnTop = _onTop.Checked;
        _settings.Hotkeys.Reload = _reloadKey.Combo.IsSet ? _reloadKey.Combo.ToString() : "F9";
        _settings.Hotkeys.ToggleTopmost = _topmostKey.Combo.IsSet ? _topmostKey.Combo.ToString() : "F10";
        _settings.ExtraMpvOptions = _extraOptions.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void StyleCheckBox(CheckBox cb)
    {
        cb.ForeColor = Theme.Fg;
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = Theme.Bg;
        cb.AutoSize = true;
    }
}
