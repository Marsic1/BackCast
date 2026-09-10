namespace Backcast.Controls;

/// <summary>
/// WebStage-style input: a borderless TextBox inside a rounded panel that
/// paints its own ring — Sep at rest, Accent when focused — instead of the
/// white 3D border stock textboxes get.
/// </summary>
internal sealed class DarkInput : Control
{
    private readonly TextBox _inner = new() { BorderStyle = BorderStyle.None };

    public bool ReadOnly
    {
        get => _inner.ReadOnly;
        set
        {
            _inner.ReadOnly = value;
            // the native textbox must match the ring it sits in: read-only
            // fields paint the outer panel Bg (dimmed) — keep the inner box
            // in sync or it shows as a lighter rectangle inside the ring
            _inner.BackColor = value ? Theme.Bg : Theme.BgEdit;
            Invalidate();
        }
    }

    public bool Multiline
    {
        get => _inner.Multiline;
        set => _inner.Multiline = value;
    }

    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    public override Color ForeColor
    {
        get => _inner.ForeColor;
        set => _inner.ForeColor = value;
    }

    public Color ValueColor
    {
        get => _inner.ForeColor;
        set => _inner.ForeColor = value;
    }

    public event EventHandler? TextChanged
    {
        add => _inner.TextChanged += value;
        remove => _inner.TextChanged -= value;
    }

    public DarkInput()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgEdit;
        _inner.BackColor = Theme.BgEdit;
        _inner.ForeColor = Theme.Fg;
        _inner.Font = Theme.Font();
        Height = 32;
        _inner.Location = new Point(11, 6);
        _inner.Width = 200;
        Controls.Add(_inner);
        _inner.GotFocus += (_, _) => Invalidate();
        _inner.LostFocus += (_, _) => Invalidate();
        Cursor = Cursors.IBeam;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _inner.Width = Width - 22;
        _inner.Height = Height - 12;
        if (_inner.Multiline)
            _inner.Location = new Point(11, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(ReadOnly ? Theme.Bg : Theme.BgEdit);
        Theme.FillRoundedRect(g, fill, ClientRectangle, 8);
        using var border = new Pen(_inner.Focused ? Theme.Accent : Theme.Sep, 1f);
        Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 8);
    }

    // forward focus into the actual textbox
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _inner.Focus();
    }
}
