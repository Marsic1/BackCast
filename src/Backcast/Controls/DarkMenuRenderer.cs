namespace Backcast.Controls;

/// <summary>
/// Dark owner-drawn renderer for context menus and the tray menu, WebStage
/// quality. All text (label + shortcut hint, each with its own measured
/// space) is painted by US in the background pass; the framework's own text
/// pass is suppressed in OnRenderItemText — that's the only way to get
/// deterministic colors for disabled status rows (WinForms grays them) and
/// a layout where label and shortcut never share pixels.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripRenderer
{
    private const int RowHPad = 16, ShortcutW = 92;

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rc = new Rectangle(Point.Empty, e.Item.Size);
        if (rc.Width <= 0 || rc.Height <= 0) return;

        using (var surface = new SolidBrush(Theme.BgPanel))
            g.FillRectangle(surface, rc);

        if (e.Item.Selected && e.Item.Enabled)
        {
            var pill = Rectangle.Inflate(rc, -5, -2);
            using var fill = new SolidBrush(Theme.Hover);
            Theme.FillRoundedRect(g, fill, pill, 6);
        }

        // ---- full text pass (ours) ----
        var mi = (ToolStripMenuItem)e.Item;
        string text = mi.Text ?? "";
        int tab = text.IndexOf('\t');
        string label = tab >= 0 ? text[..tab] : text;
        // shortcut: explicit display string, or one WinForms appended via \t
        string? shortcut = mi.ShortcutKeyDisplayString;
        if (string.IsNullOrEmpty(shortcut) && tab >= 0) shortcut = text[(tab + 1)..];

        bool check = mi.Checked;
        int textX = RowHPad + (check ? 22 : 0);

        // status rows carry their color via Tag; they render colored even
        // though disabled (WinFrames would force gray in its own pass)
        Color labelColor = Theme.Fg;
        if (e.Item.Tag is string tag)
        {
            if (tag == "status:live") labelColor = Theme.Accent;
            else if (tag == "status:waiting") labelColor = Theme.Amber;
            else if (tag == "status:error") labelColor = Theme.Stop;
        }
        else if (!e.Item.Enabled)
        {
            labelColor = Theme.Gray;
        }

        int shortcutSpace = string.IsNullOrEmpty(shortcut) ? 0 : ShortcutW;
        var labelRc = new Rectangle(textX, 0, Math.Max(30, rc.Width - textX - RowHPad - shortcutSpace), rc.Height);
        TextRenderer.DrawText(g, label, Theme.Font(), labelRc, labelColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
            | TextFormatFlags.Left);

        if (!string.IsNullOrEmpty(shortcut))
        {
            var scRc = new Rectangle(rc.Width - RowHPad - ShortcutW, 0, ShortcutW, rc.Height);
            TextRenderer.DrawText(g, shortcut, Theme.Font(), scRc,
                e.Item.Enabled ? Theme.Gray : Theme.Gray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
        }

        if (check)
        {
            var box = new Rectangle(RowHPad, 0, 18, rc.Height);
            TextRenderer.DrawText(g, "✓", Theme.FontBold(), box, Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
    }

    /// <summary>Framework text pass disabled — everything is drawn in the background pass.</summary>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var bg = new SolidBrush(Theme.BgPanel);
        e.Graphics.FillRectangle(bg, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no border: the popup's rounded window region defines the shape
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // no image gutter: keep the surface clean
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Sep, 1f);
        e.Graphics.DrawLine(pen, RowHPad, y, e.Item.Width - RowHPad, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Gray, 1.5f);
        var r = e.ArrowRectangle;
        if (r.Width == 0) return;
        e.Graphics.DrawLine(pen, r.Left, r.Top, r.Right - 5, r.Top + r.Height / 2);
        e.Graphics.DrawLine(pen, r.Right - 5, r.Top + r.Height / 2, r.Left, r.Bottom);
    }
}
