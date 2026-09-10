namespace Backcast.Controls;

/// <summary>
/// Read-only, fully custom-painted URL chip: rounded dark field with
/// accent-colored text, a subtle copy hint, click anywhere to copy (brief ✓
/// feedback). No native textbox involved.
/// </summary>
internal sealed class UrlChip : Control
{
    private bool _copied;
    private bool _hover;
    private readonly System.Windows.Forms.Timer _reset = new() { Interval = 1200 };

    public UrlChip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 38;
        _reset.Tick += (_, _) => { _copied = false; _reset.Stop(); Invalidate(); };
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        try
        {
            if (!string.IsNullOrEmpty(Text)) Clipboard.SetText(Text);
            _copied = true;
            _reset.Start();
            Invalidate();
        }
        catch { /* clipboard busy — ignore */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var fill = new SolidBrush(_hover || _copied ? Theme.Hover : Theme.BgEdit);
        Theme.FillRoundedRect(g, fill, ClientRectangle, 10);
        using var border = new Pen(_copied ? Theme.Accent : Theme.Sep, 1f);
        Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 10);

        string text = _copied ? "copied to clipboard ✓" : (Text ?? "");
        TextRenderer.DrawText(g, text, Theme.Font(),
            new Rectangle(14, 0, Width - 34, Height), Theme.Accent,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        if (!_copied)
        {
            // subtle copy glyph on the right
            int cx = Width - 17, cy = Height / 2;
            using var pen = new Pen(Theme.Gray, 1.4f);
            g.DrawRectangle(pen, cx - 5, cy - 4, 6, 6);          // back sheet
            g.DrawRectangle(pen, cx - 2, cy - 1, 6, 6);          // front sheet
            g.DrawLine(pen, cx - 2, cy - 1, cx - 5, cy - 1);
        }
    }
}
