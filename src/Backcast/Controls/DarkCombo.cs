namespace Backcast.Controls;

/// <summary>
/// Fully custom dark combo — no native ComboBox child (its themed borders
/// and white dropdown list can't be tamed). The closed state is painted
/// here; the dropdown is a borderless dark form with rounded corners and
/// owner-drawn rows.
/// </summary>
internal sealed class DarkCombo : Control
{
    public List<object> Items { get; } = new();

    private int _selectedIndex = -1;
    private bool _hover;
    private Form? _drop;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set { _selectedIndex = value; Invalidate(); }
    }

    public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    public event EventHandler? SelectedIndexChanged;

    public DarkCombo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 34;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        ShowDropdown();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Enter or Keys.Space or Keys.Down or Keys.Up;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
        {
            ShowDropdown();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var fill = new SolidBrush(Focused || _hover ? Theme.Hover : Theme.BgEdit);
        Theme.FillRoundedRect(g, fill, ClientRectangle, 8);
        using var border = new Pen(Focused ? Theme.Accent : Theme.Sep, 1f);
        Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 8);

        // current item
        if (_selectedIndex >= 0 && _selectedIndex < Items.Count)
        {
            TextRenderer.DrawText(g, Items[_selectedIndex].ToString() ?? "", Theme.Font(),
                new Rectangle(12, 0, Width - 40, Height), Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // chevron
        int cx = Width - 18, cy = Height / 2;
        using var pen = new Pen(Theme.Gray, 1.6f);
        g.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
        g.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
    }

    // ---- dropdown ----

    private void ShowDropdown()
    {
        if (_drop != null || Items.Count == 0) return;

        _drop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Theme.BgPanel,
        };
        var list = new ListBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.BgPanel,
            ForeColor = Theme.Fg,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
            IntegralHeight = false,
            Font = Theme.Font(),
        };
        foreach (var item in Items) list.Items.Add(item);
        if (_selectedIndex >= 0) list.SelectedIndex = _selectedIndex;

        int maxVisible = Math.Min(Items.Count, 9);
        list.SetBounds(6, 6, Width - 12, maxVisible * 30 + 4);
        _drop.ClientSize = new Size(Width, list.Height + 12);

        var loc = PointToScreen(new Point(0, Height + 2));
        var screen = Screen.FromControl(this).WorkingArea;
        if (loc.Y + _drop.Height > screen.Bottom) loc.Y = PointToScreen(Point.Empty).Y - _drop.Height - 2;
        if (loc.X + _drop.Width > screen.Right) loc.X = screen.Right - _drop.Width;
        _drop.Location = loc;

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Theme.Hover : Theme.BgPanel);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index].ToString() ?? "", Theme.Font(),
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                Theme.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            SelectedIndex = list.SelectedIndex;
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            CloseDropdown();
        };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) CloseDropdown(); };

        _drop.Deactivate += (_, _) => CloseDropdown();
        _drop.Controls.Add(list);
        _drop.Show();
        // rounded corners AND kill the 1px DWM border Windows draws around
        // the borderless popup (it rendered white)
        DarkMenu.RoundCorners(_drop.Handle);
        Theme.RemoveSystemBorder(_drop.Handle);
        list.Focus();
    }

    private void CloseDropdown()
    {
        if (_drop == null) return;
        var drop = _drop;
        _drop = null;
        drop.Close();
        drop.Dispose();
        Invalidate();
    }
}

/// <summary>
/// Fully custom numeric field — the native NumericUpDown's spin buttons
/// render white no matter how they're themed, so this draws its own value
/// and ▲▼ stepper on the right, WebStage style.
/// </summary>
internal sealed class DarkNumber : Control
{
    private decimal _value;
    private bool _spinHover;      // mouse over the stepper column
    private bool _spinPressed;    // which half is pressed (true = up)
    private bool _hover;

    public decimal Minimum { get; set; } = 1024;
    public decimal Maximum { get; set; } = 65535;

    public decimal Value
    {
        get => _value;
        set
        {
            decimal v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    public event EventHandler? ValueChanged;

    public DarkNumber()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    private Rectangle StepperRect => new(Width - 30, 1, 28, Height - 2);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _spinHover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool overSpin = StepperRect.Contains(e.Location);
        if (overSpin != _spinHover) { _spinHover = overSpin; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (StepperRect.Contains(e.Location))
        {
            _spinPressed = e.Y < Height / 2;
            Nudge(_spinPressed ? 1 : -1);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _spinPressed = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Nudge(1); e.Handled = true; break;
            case Keys.Down: Nudge(-1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    private void Nudge(int delta)
    {
        decimal v = Math.Clamp(_value + delta, Minimum, Maximum);
        if (v == _value) return;
        _value = v;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // field
        using (var fill = new SolidBrush(_hover || Focused ? Theme.Hover : Theme.BgEdit))
            Theme.FillRoundedRect(g, fill, ClientRectangle, 8);
        using (var border = new Pen(Focused ? Theme.Accent : Theme.Sep, 1f))
            Theme.DrawRoundedRect(g, border, Rectangle.Inflate(ClientRectangle, -1, -1), 8);

        // value
        TextRenderer.DrawText(g, _value.ToString("0"), Theme.Font(),
            new Rectangle(12, 0, Width - 44, Height), Theme.Fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        // stepper column
        var spin = StepperRect;
        int mid = spin.Y + spin.Height / 2;
        Color arrow = _spinHover ? Theme.Fg : Theme.Gray;
        using var pen = new Pen(arrow, 1.5f);
        // ▲
        g.DrawLine(pen, spin.X + 8, mid - 4, spin.X + 12, mid - 8);
        g.DrawLine(pen, spin.X + 12, mid - 8, spin.X + 16, mid - 4);
        // ▼
        g.DrawLine(pen, spin.X + 8, mid + 3, spin.X + 12, mid + 7);
        g.DrawLine(pen, spin.X + 12, mid + 7, spin.X + 16, mid + 3);
        // divider between arrows and value
        using var divider = new Pen(Theme.Sep, 1f);
        g.DrawLine(divider, spin.X - 2, spin.Y + 2, spin.X - 2, spin.Y + spin.Height - 2);
    }
}
