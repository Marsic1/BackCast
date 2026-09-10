using System.Runtime.InteropServices;

namespace Backcast;

/// <summary>
/// Dark theme ported from the WebStage (HTTP2SpoutSender) UI: palette,
/// fonts, dark titlebar, and drawing helpers shared by all custom controls.
/// </summary>
internal static class Theme
{
    // ---- palette (WebStage Ui.h) ----
    internal static readonly Color Bg = Color.FromArgb(0x0E, 0x11, 0x13);
    internal static readonly Color BgPanel = Color.FromArgb(0x15, 0x1A, 0x1D);
    internal static readonly Color BgEdit = Color.FromArgb(0x1C, 0x23, 0x27);
    internal static readonly Color Hover = Color.FromArgb(0x22, 0x2B, 0x30);
    internal static readonly Color Fg = Color.FromArgb(0xE8, 0xEC, 0xEC);
    internal static readonly Color Gray = Color.FromArgb(0x8B, 0x96, 0x98);
    internal static readonly Color Sep = Color.FromArgb(0x2C, 0x35, 0x3A);
    internal static readonly Color Accent = Color.FromArgb(0x3D, 0xDC, 0x84);
    internal static readonly Color Stop = Color.FromArgb(0xD9, 0x64, 0x59);
    internal static readonly Color Amber = Color.FromArgb(0xE0, 0xA4, 0x3C); // WAITING
    internal static readonly Color ChipBg = Color.FromArgb(0x1A, 0x33, 0x2A);
    internal static readonly Color ChipBorder = Color.FromArgb(0x24, 0x68, 0x46);

    // ---- fonts ----
    private static Font? _font, _fontBold;

    internal static Font Font()
    {
        if (_font == null) _font = CreateFont(false);
        return _font;
    }

    internal static Font FontBold()
    {
        if (_fontBold == null) _fontBold = CreateFont(true);
        return _fontBold;
    }

    private static Font CreateFont(bool bold)
    {
        try
        {
            var f = new Font("Segoe UI Variable Text", 9f, bold ? FontStyle.Bold : FontStyle.Regular);
            return f;
        }
        catch
        {
            return new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    // ---- dark titlebar for standard-chrome windows (Settings dialog) ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appid, string? nullSubId);

    /// <summary>Immersive dark mode + caption/text colors in the theme palette.</summary>
    internal static void EnableDarkFrame(IntPtr hwnd)
    {
        int dark = 1;
        // 20 (DWMWA_USE_IMMERSIVE_DARK_MODE), 19 on older builds
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        int caption = ColorToWin32(Bg);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        int captionText = ColorToWin32(Fg);
        DwmSetWindowAttribute(hwnd, 36, ref captionText, sizeof(int)); // DWMWA_TEXT_COLOR
        SetRoundedCorners(hwnd, 8);
    }

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = 33; ROUND = 2 (Win11; ignored downlevel).</summary>
    internal static void SetRoundedCorners(IntPtr hwnd, int radiusDlu = 8)
    {
        int pref = radiusDlu >= 6 ? 2 /* DWMWCP_ROUND */ : 1 /* ROUND SMALL */;
        _ = DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
    }

    /// <summary>
    /// Removes the 1px DWM border Windows draws around borderless popups
    /// (menus, dropdown forms) — it renders white/system-colored and gets
    /// visibly cut where a rounded window region clips it. WebStage's
    /// ApplyMenuDwmAttrs uses the same DWMWA_COLOR_NONE trick.
    /// </summary>
    internal static void RemoveSystemBorder(IntPtr hwnd)
    {
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
    }

    private static int ColorToWin32(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>Dark scrollbar/edge theme for stock Win32 edit controls.</summary>
    internal static void DarkControlTheme(IntPtr hwnd) =>
        SetWindowTheme(hwnd, "DarkMode_Explorer", null);

    /// <summary>
    /// Dark-themes a control AND all of its native child windows (e.g. the
    /// spin buttons of a NumericUpDown, which otherwise render white).
    /// </summary>
    internal static void DarkControlTree(IntPtr hwnd)
    {
        SetWindowTheme(hwnd, "DarkMode_Explorer", null);
        EnumChildWindows(hwnd, (child, _) =>
        {
            SetWindowTheme(child, "DarkMode_Explorer", null);
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    // ---- drawing helpers ----

    internal static void PaintButton(PaintEventArgs e, Rectangle bounds, string text,
        bool pressed, bool hovered, bool focused, bool enabled, bool danger = false)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var back = new SolidBrush(enabled ? (pressed ? Hover : BgEdit) : Bg);
        using var border = new Pen(
            !enabled ? Sep :
            focused ? Accent :
            danger ? Stop :
            Sep, 1f);

        FillRoundedRect(g, back, bounds, 8);
        DrawRoundedRect(g, border, Rectangle.Inflate(bounds, -1, -1), 8);

        TextRenderer.DrawText(g, text, Font(), bounds,
            enabled ? Fg : Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    internal static void FillRoundedRect(Graphics g, Brush b, Rectangle r, int radius)
    {
        using var path = RoundedPath(r, radius);
        g.FillPath(b, path);
    }

    internal static void DrawRoundedRect(Graphics g, Pen p, Rectangle r, int radius)
    {
        using var path = RoundedPath(r, radius);
        g.DrawPath(p, path);
    }

    internal static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
