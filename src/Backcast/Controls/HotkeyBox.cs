
namespace Backcast.Controls;/// <summary>
/// WebStage HotkeyDialog behavior in control form: shows the current combo;
/// click to capture, then press a key combination. WebStage rules:
/// Esc cancels, Backspace clears, modifiers alone are ignored while the
/// real key is pressed, and a combo needs Ctrl/Alt (bare keys only valid
/// for function keys so typing is never hijacked).
/// </summary>
internal sealed class HotkeyBox : Control
{
    private bool _capturing;

    /// <summary>True while a key press is being captured (Esc cancels capture).</summary>
    public bool IsCapturing => _capturing;

    public HotkeyCombo Combo { get; set; }

    /// <summary>Fires when the combo changes via capture (not programmatic set).</summary>
    public event EventHandler? ComboChanged;

    public string Label { get; set; } = "";

    public HotkeyBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 34;
        Width = 150;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e)
    {
        _capturing = true;
        Focus(); // capture needs keyboard focus to receive the keystroke
        Invalidate();
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        if (e.KeyCode == Keys.Escape)
        {
            _capturing = false; // WebStage: Esc cancels, keeps old combo
        }
        else if (e.KeyCode == Keys.Back)
        {
            Combo = HotkeyCombo.None;
            _capturing = false;
            ComboChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                 or Keys.LWin or Keys.RWin or Keys.LShiftKey or Keys.RShiftKey
                 or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
        {
            Invalidate(); // show live modifier while waiting for the real key
            return;
        }
        else
        {
            var mods = Keys.None;
            if (e.Control) mods |= Keys.Control;
            if (e.Shift) mods |= Keys.Shift;
            if (e.Alt) mods |= Keys.Alt;

            // WebStage rule: combos need a real modifier; bare keys only OK
            // for function keys (F1-F24) to avoid hijacking typing.
            bool bare = mods == Keys.None;
            bool isFunctionKey = e.KeyCode is >= Keys.F1 and <= Keys.F24;
            if (bare && !isFunctionKey)
            {
                Invalidate();
                return; // keep capturing
            }

            Combo = new HotkeyCombo(mods, e.KeyCode);
            _capturing = false;
            ComboChanged?.Invoke(this, EventArgs.Empty);
        }
        Invalidate();
        base.OnKeyDown(e);
    }

    // NOTE: no ProcessCmdKey override here. Returning true from it while
    // capturing would swallow EVERY key before OnKeyDown fires — ProcessCmdKey
    // runs earlier in the WinForms key pipeline than KeyDown, so capture would
    // never see a keystroke. The settings form's KeyPreview guard handles
    // keeping its Esc-to-close from firing mid-capture.

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
        Theme.FillRoundedRect(g, bg, ClientRectangle, 8);
        using var border = new Pen(Focused || _capturing ? Theme.Accent : Theme.Sep, 1f);
        Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 8);

        TextRenderer.DrawText(g, text, Theme.Font(), ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        if (_capturing && Label.Length > 0)
            TextRenderer.DrawText(g, Label, Theme.Font(),
                new Rectangle(0, Height + 2, Width * 3, 20), Theme.Gray,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}
