namespace Backcast.Controls;

/// <summary>
/// Header status pill in the WebStage "send toggle" style: LED dot + colored
/// bold caption. WAITING pulses its LED; ERROR offers a retry when clicked.
/// </summary>
internal sealed class StatusPill : Control
{
    private PlaybackState _state = PlaybackState.Waiting;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 650 };
    private bool _pulseOn;

    public event EventHandler? RetryRequested;

    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontBold();
        Height = 26;
        Width = 112;
        _pulse.Tick += (_, _) =>
        {
            _pulseOn = !_pulseOn;
            Invalidate();
        };
    }

    public PlaybackState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            _pulse.Enabled = value == PlaybackState.Waiting;
            _pulseOn = true;
            Invalidate();
        }
    }

    private (Color led, string label) Visuals =>
        _state switch
        {
            PlaybackState.Live => (Theme.Accent, "LIVE"),
            PlaybackState.Error => (Theme.Stop, "ERROR"),
            _ => (Theme.Amber, "WAITING"),
        };

    protected override void OnPaint(PaintEventArgs e)
    {
        var (led, label) = Visuals;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // pill background
        Color face = _state switch
        {
            PlaybackState.Live => Theme.ChipBg,
            PlaybackState.Error => Color.FromArgb(0x2C, 0x1B, 0x19),
            _ => Color.FromArgb(0x2A, 0x22, 0x18),
        };
        using (var bg = new SolidBrush(face))
            Theme.FillRoundedRect(g, bg, ClientRectangle, ClientRectangle.Height / 2);
        using (var border = new Pen(_state switch
        {
            PlaybackState.Live => Theme.ChipBorder,
            PlaybackState.Error => Color.FromArgb(0x6B, 0x34, 0x2D),
            _ => Color.FromArgb(0x5C, 0x47, 0x26),
        }, 1f))
            Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), ClientRectangle.Height / 2);

        // LED + label centered as a unit: measure the text, put LED and
        // label around the pill's centerline
        bool bright = _state != PlaybackState.Waiting || _pulseOn;
        Color ledColor = bright ? led : Color.FromArgb(led.A / 2, led);
        int d = Math.Max(7, Height / 3);
        int gap = 7;
        var textSize = TextRenderer.MeasureText(g, label, Font, proposedSize: new Size(int.MaxValue, Height),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int unitW = d + gap + textSize.Width;
        int startX = Math.Max(10, (Width - unitW) / 2);
        int cy = Height / 2;

        using (var b = new SolidBrush(ledColor))
            g.FillEllipse(b, startX, cy - d / 2, d, d);

        var textRect = new Rectangle(startX + d + gap - 2, 0, Width - startX - d - gap, Height);
        TextRenderer.DrawText(g, label, Font, textRect, led,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_state == PlaybackState.Error)
            RetryRequested?.Invoke(this, EventArgs.Empty);
        base.OnMouseClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulse.Dispose();
        base.Dispose(disposing);
    }
}
