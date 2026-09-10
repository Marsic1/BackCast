using Backcast.Controls;

namespace Backcast;

/// <summary>
/// First-run wizard — WebStage-styled with measured stacking (no hardcoded
/// positions, nothing can overlap): step 1 selects Direct / Relay via large
/// option cards; step 2 picks the silent audio output.
/// </summary>
internal sealed class FirstRunWizard : Form
{
    private readonly AppSettings _settings;
    private readonly MpvPlayer _player;

    private readonly Panel _stepTransport = new() { Dock = DockStyle.Fill, Visible = true };
    private readonly Panel _stepAudio = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly DarkButton _nextButton = new() { Text = "Next  →", Size = new Size(118, 32) };
    private readonly DarkButton _backButton = new() { Text = "←  Back", Size = new Size(98, 32) };

    // step 1
    private OptionCard _directCard = null!;
    private OptionCard _relayCard = null!;

    // step 2
    private DarkCombo _deviceList = null!;
    private readonly DarkButton _refreshButton = new() { Text = "Refresh list", Size = new Size(124, 32) };
    private readonly DarkButton _installCable = new() { Text = "Install VB-Cable  (free)", Size = new Size(184, 32) };
    private readonly Label _audioStatus = new();
    private List<MpvPlayer.AudioDevice> _devices = new();

    private int _step;
    private const int EdgeX = 36, TopY = 34, ContentW = 704;

    public FirstRunWizard(AppSettings settings, MpvPlayer player)
    {
        _settings = settings;
        _player = player;

        Text = "Welcome to Backcast";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(776, 700);
        BackColor = Theme.Bg;
        Font = Theme.Font();
        ShowInTaskbar = false;
        Load += (_, _) => { Theme.EnableDarkFrame(Handle); BuildTransportStep(); BuildAudioStep(); };

        _backButton.Location = new Point(EdgeX, ClientSize.Height - 46);
        _backButton.Click += (_, _) => SetStep(0);
        _backButton.Visible = false;
        _nextButton.Location = new Point(ClientSize.Width - EdgeX - 118, ClientSize.Height - 46);
        _nextButton.Click += (_, _) =>
        {
            if (_step == 0) SetStep(1);
            else Finish();
        };
        Controls.Add(_backButton);
        Controls.Add(_nextButton);
        Controls.Add(_stepTransport);
        Controls.Add(_stepAudio);
    }

    private void SetStep(int step)
    {
        _step = step;
        _stepTransport.Visible = step == 0;
        _stepAudio.Visible = step == 1;
        _backButton.Visible = step == 1;
        _nextButton.Text = step == 0 ? "Next  →" : "Finish  ✓";
        if (step == 1) RefreshDeviceList();
    }

    // ---- measured layout helpers (no hardcoded label positions) ----

    /// <summary>Adds a wrapped label at y; returns y below it (+ gap).</summary>
    private int StackLabel(Panel parent, ref int y, string text, Font font, Color color, int? width = null)
    {
        int w = width ?? ContentW;
        var size = Measure(parent, text, font, w);
        Label lbl = new()
        {
            Text = text,
            Font = font,
            ForeColor = color,
            Bounds = new Rectangle(EdgeX, y, w, size.Height),
            AutoSize = false,
        };
        parent.Controls.Add(lbl);
        return y + size.Height + 10;
    }

    private static Size Measure(Control parent, string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);

    // ---- step 1 ----

    private void BuildTransportStep()
    {
        var titleFont = new Font(Theme.FontBold().FontFamily, 15f, FontStyle.Bold);
        int y = TopY;
        y = StackLabel(_stepTransport, ref y, "Connect OBS to Backcast", titleFont, Theme.Fg);
        y = StackLabel(_stepTransport, ref y,
            "Pick how OBS sends the picture to this app. Nothing goes to the internet — everything stays on this PC.",
            Theme.Font(), Theme.Gray) + 4;

        // ---- card 1: direct ----
        _directCard = new OptionCard
        {
            Title = "Direct",
            Subtitle = "Simplest — recommended",
            Badge = "1",
            Description =
                "Works with OBS's normal Stream button. Paste this into OBS → Settings → Stream →\nCustom server (the key can be anything):",
            Url = _settings.ObsUrl,
        };
        _directCard.IsSelected = true;
        _directCard.Location = new Point(EdgeX, y);
        _directCard.Size = new Size(ContentW, OptionCard.MeasureHeight(_directCard, ContentW));
        _directCard.SelectionChanged += (_, _) => OnCardSelected(_directCard);
        _stepTransport.Controls.Add(_directCard);
        y += _directCard.Height + 14;

        // ---- card 2: relay ----
        _relayCard = new OptionCard
        {
            Title = "Relay",
            Subtitle = "If you use the Aitum Multistream plugin",
            Badge = "2",
            Description =
                "Aitum destinations are RTMP-only, so a tiny local relay converts the feed (downloaded below,\nstarted automatically). In Aitum Multistream add a destination: Custom RTMP, this server, key \"discord\":",
            Url = $"rtmp://127.0.0.1:1935/{_settings.RtmpPath}",
            Extra = BuildRelayExtra(),
        };
        _relayCard.Location = new Point(EdgeX, y);
        _relayCard.Size = new Size(ContentW, OptionCard.MeasureHeight(_relayCard, ContentW));
        _relayCard.SelectionChanged += (_, _) => OnCardSelected(_relayCard);
        _stepTransport.Controls.Add(_relayCard);
    }

    /// <summary>The relay card's extra content: download button + status.</summary>
    private Control BuildRelayExtra()
    {
        Panel row = new() { AutoSize = false, Height = 40, BackColor = Color.Transparent };
        var download = new DarkButton { Text = "Download relay  (one-time)", Size = new Size(214, 32), Location = new Point(18, 0) };
        var status = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Gray,
            Location = new Point(244, 8),
            Font = Theme.Font(),
        };
        if (MediaMtx.IsDownloaded)
        {
            download.Enabled = false;
            status.Text = "relay ready ✓";
            status.ForeColor = Theme.Accent;
        }
        download.Click += (_, _) =>
        {
            download.Enabled = false;
            status.Text = "downloading…";
            status.ForeColor = Theme.Amber;
            Task.Run(() =>
            {
                try
                {
                    MediaMtx.EnsureDownloaded();
                    BeginInvoke(() => { status.Text = "relay ready ✓"; status.ForeColor = Theme.Accent; });
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => { status.Text = ex.Message; status.ForeColor = Theme.Stop; download.Enabled = true; });
                }
            });
        };
        row.Controls.Add(download);
        row.Controls.Add(status);
        return row;
    }

    private void OnCardSelected(OptionCard card)
    {
        if (!card.IsSelected) return;
        var other = card == _directCard ? _relayCard : _directCard;
        if (other.IsSelected) other.IsSelected = false;
    }

    // ---- step 2 ----

    private void BuildAudioStep()
    {
        var titleFont = new Font(Theme.FontBold().FontFamily, 15f, FontStyle.Bold);
        int y = TopY;
        y = StackLabel(_stepAudio, ref y, "Where should the sound go?", titleFont, Theme.Fg);
        y = StackLabel(_stepAudio, ref y,
            "Backcast must keep playing the sound for Discord to capture it — but you don't want to hear it twice.\n" +
            "So it plays into a \"silent\" output: either VB-Cable, or any HDMI/monitor output with nothing attached\n" +
            "to it. Your friends hear everything either way.",
            Theme.Font(), Theme.Gray) + 6;

        y = StackLabel(_stepAudio, ref y, "Silent output device", Theme.FontBold(), Theme.Fg);

        _deviceList = new DarkCombo { Bounds = new Rectangle(EdgeX, y, ContentW, 34) };
        _stepAudio.Controls.Add(_deviceList);
        y += 44;

        _refreshButton.Location = new Point(EdgeX, y);
        _refreshButton.Click += (_, _) => RefreshDeviceList();
        _installCable.Location = new Point(EdgeX + 132, y);
        _installCable.Click += (_, _) => InstallCable();
        _stepAudio.Controls.Add(_refreshButton);
        _stepAudio.Controls.Add(_installCable);
        y += 42;

        _audioStatus.ForeColor = Theme.Gray;
        _audioStatus.Font = Theme.Font();
        _audioStatus.AutoSize = false;
        _audioStatus.Bounds = new Rectangle(EdgeX, y, ContentW, 36);
        _stepAudio.Controls.Add(_audioStatus);
        y += 44;

        StackLabel(_stepAudio, ref y,
            "One rule: never mute Backcast in the Windows volume mixer — that would mute what your friends hear.",
            Theme.Font(), Theme.Gray);
    }

    private void RefreshDeviceList()
    {
        try
        {
            _devices = _player.GetAudioDevices().ToList();
        }
        catch
        {
            _devices = new List<MpvPlayer.AudioDevice>();
        }
        _deviceList.Items.Clear();
        var silent = SilentCandidates(_devices).ToList();
        foreach (var d in _devices)
            _deviceList.Items.Add(DeviceLabel(d, silent.Contains(d)));
        if (_deviceList.Items.Count > 0)
        {
            _deviceList.SelectedIndex = silent.Count > 0
                ? _devices.IndexOf(silent[0])
                : 0;
        }
        _audioStatus.Text = silent.Count > 0
            ? "Good silent outputs are marked with ✓ — pick one of those if unsure."
            : "No obvious silent output found. Install VB-Cable below, or pick any HDMI output.";
    }

    private void InstallCable()
    {
        _installCable.Enabled = false;
        _audioStatus.Text = "downloading VB-Cable…";
        _audioStatus.ForeColor = Theme.Amber;
        Task.Run(() =>
        {
            try
            {
                VbCable.RunInstaller();
                BeginInvoke(() =>
                {
                    _audioStatus.Text = "Installer launched — click \"Install driver\" in it, then press Refresh list.";
                    _audioStatus.ForeColor = Theme.Fg;
                    _installCable.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    _audioStatus.Text = ex.Message;
                    _audioStatus.ForeColor = Theme.Stop;
                    _installCable.Enabled = true;
                });
            }
        });
    }

    private void Finish()
    {
        _settings.Transport = _relayCard.IsSelected ? "rtmp" : "udp";
        if (_deviceList.SelectedIndex >= 0 && _deviceList.SelectedIndex < _devices.Count)
            _settings.Audio.SelectedDevice = _devices[_deviceList.SelectedIndex].Name;
        _settings.FirstRun = false;
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    // ---- helpers ----

    private static IEnumerable<MpvPlayer.AudioDevice> SilentCandidates(
        IReadOnlyList<MpvPlayer.AudioDevice> devices)
    {
        bool IsSilent(MpvPlayer.AudioDevice d)
        {
            if (d.Name == "auto") return false;
            if (d.Description.Contains("default", StringComparison.OrdinalIgnoreCase)) return false;
            string[] markers =
            {
                "CABLE", "HDMI", "DisplayPort", "High Definition Audio",
                "Digital", "SPDIF", "TV", "PROJECTOR", "Dummy", "Monitor",
            };
            return markers.Any(m => d.Description.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        return devices.Where(IsSilent)
            .OrderByDescending(d => d.Description.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
    }

    private static string DeviceLabel(MpvPlayer.AudioDevice d, bool silent) =>
        (silent ? "✓   " : "") + d.Description;

    /// <summary>
    /// Selectable option card with measured internal stacking: badge + title,
    /// subtitle, description, URL chip (click to copy), optional extra row.
    /// </summary>
    private sealed class OptionCard : Panel
    {
        private const int PadX = 18, PadTop = 18, RowGap = 10;
        private readonly Panel _badge = new() { Size = new Size(30, 30) };
        private readonly Label _badgeText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.FontBold(), ForeColor = Theme.Bg };
        private readonly Label _title = new() { Font = new Font(Theme.FontBold().FontFamily, 12f, FontStyle.Bold), ForeColor = Theme.Fg, AutoSize = true };
        private readonly Label _subtitle = new() { Font = Theme.Font(), ForeColor = Theme.Gray, AutoSize = true };
        private readonly Label _description = new() { Font = Theme.Font(), ForeColor = Theme.Fg };
        private readonly UrlChip _url = new();
        private Control? _extra;
        private bool _selected;

        public event EventHandler? SelectionChanged;

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                UpdateVisual();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string Title { init { _title.Text = value; } }
        public string Subtitle { init { _subtitle.Text = value; } }
        public string Badge { init { _badgeText.Text = value; } }
        public string Description
        {
            init
            {
                _description.Text = value;
                _description.ForeColor = Theme.Fg;
            }
        }
        public string Url { init { _url.Text = value; } }
        public Control Extra
        {
            init
            {
                _extra = value;
                Controls.Add(value);
            }
        }

        public OptionCard()
        {
            BackColor = Theme.BgPanel;
            Cursor = Cursors.Hand;
            _badge.Controls.Add(_badgeText);
            Controls.Add(_badge);
            Controls.Add(_title);
            Controls.Add(_subtitle);
            Controls.Add(_description);
            Controls.Add(_url);

            Click += Select;
            foreach (Control c in Controls)
            {
                c.Click += Select;
                c.Cursor = Cursors.Hand;
            }
            UpdateVisual();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.Click += Select;
            e.Control.Cursor = Cursors.Hand;
            // wire children added later (URL input contents etc.)
            foreach (Control gc in e.Control.Controls)
            {
                gc.Click += Select;
                gc.Cursor = Cursors.Hand;
            }
        }

        private void Select(object? sender, EventArgs e) => IsSelected = true;

        /// <summary>Total height needed at the given width (measured, nothing hardcoded).</summary>
        public static int MeasureHeight(OptionCard card, int width)
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            int innerW = Math.Max(60, width - PadX * 2 - 10);

            var titleSize = TextRenderer.MeasureText(g, card._title.Text, card._title.Font);
            var subSize = TextRenderer.MeasureText(g, card._subtitle.Text, card._subtitle.Font);
            int headerH = Math.Max(32, titleSize.Height + subSize.Height + 4);

            var descSize = TextRenderer.MeasureText(g, card._description.Text, card._description.Font,
                new Size(innerW - 10, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);

            int extraH = card._extra is null ? 0 : card._extra.Height + RowGap;
            return PadTop + headerH + RowGap + descSize.Height + RowGap + 34 + extraH + PadTop;
        }

        /// <summary>Positions all children; call after the card is sized.</summary>
        public void LayoutChildren()
        {
            int x = PadX;
            _badge.Location = new Point(x, PadTop);
            _title.Location = new Point(x + 42, PadTop - 2);
            _subtitle.Location = new Point(x + 42, PadTop + _title.PreferredHeight - 4);

            int y = PadTop + Math.Max(34, _title.PreferredHeight + _subtitle.PreferredHeight);
            using var g = CreateGraphics();
            var descSize = TextRenderer.MeasureText(g, _description.Text, _description.Font,
                new Size(Width - PadX * 2 - 10, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            _description.SetBounds(x + 28, y, Width - PadX * 2 - 10, descSize.Height);
            y += descSize.Height + RowGap;
            _url.SetBounds(x + 28, y, Width - PadX * 2 - 10, 34);
            y += 34 + RowGap;
            _extra?.SetBounds(x + 28, y, Width - PadX * 2 - 10, _extra.Height);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 80) LayoutChildren();
        }

        private void UpdateVisual()
        {
            BackColor = _selected ? Theme.ChipBg : Theme.BgPanel;
            _badge.BackColor = _selected ? Theme.Accent : Theme.Sep;
            _title.ForeColor = _selected ? Theme.Accent : Theme.Fg;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(_selected ? Theme.ChipBg : Theme.BgPanel))
                Theme.FillRoundedRect(g, fill, ClientRectangle, 10);
            using (var border = new Pen(_selected ? Theme.ChipBorder : Theme.Sep, _selected ? 1.6f : 1f))
                Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 10);
            using (var badgeFill = new SolidBrush(_selected ? Theme.Accent : Theme.Sep))
                g.FillEllipse(badgeFill, _badge.Bounds);
        }
    }
}