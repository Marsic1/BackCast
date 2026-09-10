namespace Backcast.Controls;

/// <summary>
/// Small square header button with hand-drawn glyphs (close / minimize /
/// pin) so every glyph has the same visual weight, WebStage hover behavior.
/// </summary>
internal sealed class HeaderButton : Control
{
    public enum GlyphKind { Close, Minimize, Pin }

    private bool _hover;

    /// <summary>Hover color (close button goes red).</summary>
    public bool Danger { get; set; }

    /// <summary>Active state (topmost on) tints the glyph green.</summary>
    public bool ActiveState { get; set; }

    public GlyphKind Glyph { get; set; } = GlyphKind.Pin;

    public HeaderButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(46, 40);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (_hover)
        {
            using var bg = new SolidBrush(Danger ? Theme.Stop : Theme.Hover);
            g.FillRectangle(bg, ClientRectangle);
        }
        Color fg = ActiveState ? Theme.Accent : Theme.Fg;
        int cx = Width / 2, cy = Height / 2;

        switch (Glyph)
        {
            case GlyphKind.Close:
                using (var pen = new Pen(fg, 1.6f))
                {
                    g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                    g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                }
                break;

            case GlyphKind.Minimize:
                using (var pen = new Pen(fg, 1.6f))
                    g.DrawLine(pen, cx - 4, cy + 4, cx + 4, cy + 4);
                break;

            case GlyphKind.Pin:
                // the classic pushpin emoji — matches the original look the
                // user prefers; tinted green when topmost is active
                Color pinColor = ActiveState ? Theme.Accent : fg;
                TextRenderer.DrawText(g, "📌", Theme.Font(),
                    ClientRectangle, pinColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                break;
        }
    }
}
