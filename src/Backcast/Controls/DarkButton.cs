namespace Backcast.Controls;

/// <summary>WebStage-style rounded dark button (owner-drawn).</summary>
internal sealed class DarkButton : Control
{
    public bool Danger { get; set; }

    public DarkButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Font();
        Height = 28;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.PaintButton(e, ClientRectangle, Text,
            pressed: MouseButtons == MouseButtons.Left && ClientRectangle.Contains(PointToClient(MousePosition)),
            hovered: ClientRectangle.Contains(PointToClient(MousePosition)),
            focused: Focused, enabled: Enabled, danger: Danger);
    }

    protected override void OnMouseEnter(EventArgs e) { Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Invalidate(); Focus(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Invalidate(); base.OnMouseUp(e); }

    // WinForms does not invalidate on Enabled changes — a disabled button
    // kept its enabled look (e.g. "Download relay" stayed clickable-looking
    // after the relay was installed)
    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override bool IsInputKey(Keys keyData) => true; // keep focus ring on arrows

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}
