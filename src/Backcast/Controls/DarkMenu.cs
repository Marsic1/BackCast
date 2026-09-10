using System.Runtime.InteropServices;

namespace Backcast.Controls;

/// <summary>
/// Builds consistently-styled dark menus (WebStage metrics + renderer) and
/// rounds popup corners with a window region while removing the DWM border
/// that Windows draws around borderless popups (it renders light and gets
/// clipped by the rounded region).
/// Items are sized the moment they are added — label and shortcut hint each
/// get their own measured space, correct from the first paint, which matters
/// for the tray menu: NotifyIcon shows it without any involvement of Show().
/// </summary>
internal static class DarkMenu
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Creates a styled menu; show via <see cref="Show"/> (or let the tray show it).</summary>
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Theme.BgPanel,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = Theme.Font(),
            Padding = new Padding(5, 7, 5, 7),
        };
        menu.ItemAdded += (_, e) =>
        {
            if (e.Item is ToolStripMenuItem mi)
            {
                mi.Padding = new Padding(14, 8, 14, 8);
                SizeMenuItem(mi);
            }
        };
        menu.Opened += (_, _) => RoundCorners(menu.Handle);
        return menu;
    }

    /// <summary>Shows the menu, sizes items and rounds the popup corners.</summary>
    public static void Show(ContextMenuStrip menu, Point screenPt)
    {
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem mi) SizeMenuItem(mi);
        menu.Show(screenPt);
        RoundCorners(menu.Handle);
    }

    /// <summary>
    /// Explicit width: label + reserved shortcut column + check mark + padding
    /// (WinForms AutoSize ignores the shortcut hint, causing overlap).
    /// </summary>
    private static void SizeMenuItem(ToolStripMenuItem mi)
    {
        if (mi.HasDropDownItems) return;
        string text = mi.Text ?? "";
        int tab = text.IndexOf('\t');
        string label = tab >= 0 ? text[..tab] : text;
        string shortcut = mi.ShortcutKeyDisplayString ?? (tab >= 0 ? text[(tab + 1)..] : "");

        int labelW = TextRenderer.MeasureText(label, Theme.Font(),
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width;
        int shortcutW = shortcut.Length > 0 ? 110 : 0;
        int checkW = mi.Checked ? 22 : 0;
        int width = 16 + checkW + labelW + shortcutW + 16;
        mi.AutoSize = false;
        mi.Height = 36;
        mi.Width = Math.Max(width, 240);
    }

    /// <summary>Rounds the popup and removes its 1px DWM border.</summary>
    internal static void RoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (GetWindowRect(hwnd, out var r))
        {
            int rad = 10;
            IntPtr rgn = CreateRoundRectRgn(0, 0, r.Right - r.Left + 1, r.Bottom - r.Top + 1, rad * 2, rad * 2);
            if (rgn != IntPtr.Zero)
                _ = SetWindowRgn(hwnd, rgn, true);
        }
        Theme.RemoveSystemBorder(hwnd);
    }
}
