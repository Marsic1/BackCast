

namespace Backcast.Controls;

/// <summary>
/// WebStage HotkeyDialog behavior in control form: shows the current combo;
/// click (or focus + keypress) to capture; combos need Ctrl/Shift/Alt, bare
/// keys allowed only for function keys; Esc cancels, Backspace clears.
/// </summary>
internal sealed class HotkeyBox : Control
{
    private bool _capturing;

    public HotkeyCombo Combo { get; set; }

    public HotkeyBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 30;
        Width = 150;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e)
    {
        _capturing = true;
        Invalidate();
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        if (e.KeyCode == Keys.Escape)
        {
            _capturing = false;
        }
        else if (e.KeyCode == Keys.Back)
        {
            Combo = HotkeyCombo.None;
            _capturing = false;
        }
        else if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return; // wait for the actual key; modifiers shown live
        }
        else
        {
            var mods = Keys.None;
            if (e.Control) mods |= Keys.Control;
            if (e.Shift) mods |= Keys.Shift;
            if (e.Alt) mods |= Keys.Alt;

            // WebStage rule: combos need a real modifier; bare keys only OK
            // for function keys (F1-F12) to avoid hijacking typing.
            bool bare = mods == Keys.None;
            bool isFunctionKey = e.KeyCode is >= Keys.F1 and <= Keys.F24;
            if (bare && !isFunctionKey)
                return; // keep capturing

            Combo = new HotkeyCombo(mods, e.KeyCode);
            _capturing = false;
        }
        Invalidate();
        base.OnKeyDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturing) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _capturing = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        string text;
        Color textColor;
        if (_capturing)
        {
            // live modifiers while waiting for the key
            List<string> live = new();
            if (ModifierKeys.HasFlag(Keys.Control)) live.Add("Ctrl");
            if (ModifierKeys.HasFlag(Keys.Shift)) live.Add("Shift");
            if (ModifierKeys.HasFlag(Keys.Alt)) live.Add("Alt");
            text = live.Count > 0 ? string.Join("+", live) + "+…" : "press a key…";
            textColor = Theme.Gray;
        }
        else
        {
            text = Combo.IsSet ? Combo.ToString() : "(none)";
            textColor = Combo.IsSet ? Theme.Fg : Theme.Gray;
        }

        using var bg = new SolidBrush(_capturing ? Theme.Hover : Theme.BgEdit);
        Theme.FillRoundedRect(g, bg, ClientRectangle, 6);
        using var border = new Pen(Focused || _capturing ? Theme.Accent : Theme.Sep, 1f);
        Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 6);

        TextRenderer.DrawText(g, text, Theme.Font(), ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
