using System.Drawing.Drawing2D;

namespace ClaudeUsageMonitor;

/// <summary>
/// Minimal custom-painted horizontal slider matching the app's dark aesthetic:
/// gray track, cyan fill, white draggable thumb. Used in NotificationsDialog
/// for the high-usage threshold. WinForms TrackBar is intentionally avoided
/// because it renders poorly on dark themes.
/// </summary>
internal sealed class Slider : Control
{
    private const int ThumbR = 7;

    private int _min = 50;
    private int _max = 99;
    private int _value = 90;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor
               | ControlStyles.UserPaint, true);
        Height = 24;
        TabStop = true;
        BackColor = Color.Transparent;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Minimum { get => _min; set { _min = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => _max; set { _max = value; Invalidate(); } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    private static int TrackLeft  => ThumbR + 2;
    private int TrackRight => Width - ThumbR - 2;
    private int TrackWidth => Math.Max(1, TrackRight - TrackLeft);

    private int ValueToX(int v)
        => TrackLeft + (int)Math.Round((double)(v - _min) / (_max - _min) * TrackWidth);

    private int XToValue(int x)
    {
        var frac = (double)(x - TrackLeft) / TrackWidth;
        return Math.Clamp(_min + (int)Math.Round(frac * (_max - _min)), _min, _max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        int thumbX = ValueToX(_value);

        using (var trackPen = new Pen(Color.FromArgb(0x44, 0x44, 0x44), 4f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(trackPen, TrackLeft, cy, TrackRight, cy);

        if (thumbX > TrackLeft)
            using (var fillPen = new Pen(Color.FromArgb(56, 189, 248), 4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fillPen, TrackLeft, cy, thumbX, cy);

        var thumbColor = Enabled ? Color.White : Color.FromArgb(120, 120, 120);
        using (var thumbBrush = new SolidBrush(thumbColor))
            g.FillEllipse(thumbBrush, thumbX - ThumbR, cy - ThumbR, ThumbR * 2, ThumbR * 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        _dragging = true;
        Focus();
        Value = XToValue(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Value = XToValue(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left)  Value -= 1;
        if (e.KeyCode == Keys.Right) Value += 1;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}
