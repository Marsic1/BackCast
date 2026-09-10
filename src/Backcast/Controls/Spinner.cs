namespace Backcast.Controls;

/// <summary>Rotating-arc spinner for the WAITING status card.</summary>
internal sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 };
    private int _angle;

    public Spinner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(32, 32);
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 24) % 360;
            Invalidate();
        };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = Rectangle.Inflate(ClientRectangle, -4, -4);
        using var pen = new Pen(Theme.Gray, 2.5f);
        using var active = new Pen(Theme.Accent, 2.5f);
        g.DrawArc(pen, r, _angle + 90, 240);
        g.DrawArc(active, r, _angle, 90);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
