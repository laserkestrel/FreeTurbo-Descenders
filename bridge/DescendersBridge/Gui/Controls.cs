using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DescendersBridge.Gui;

/// <summary>
/// A small line graph of the last minutes of one reading (<see cref="RollingHistory.WindowSeconds"/>): a 2 px line over a soft fill, a dot on the newest value. Gaps (no reading) are left
/// blank. Its accessible name carries a text summary for UI Automation. A Panel, not a bare Control: WinForms gives only its own control types a
/// UI Automation provider, and without one the graph shows up with a window number for an id and no name (tests\bridge-gui\GuiSmoke.ps1 reads it).
/// </summary>
public sealed class Sparkline : Panel
{
    public RollingHistory History { get; } = new();
    /// <summary>The top of the scale never drops below this, so a steady value does not look like a wild swing.</summary>
    public double MinScale { get; set; } = 10;
    public int Decimals { get; set; }
    public double Now { get; set; }

    public Sparkline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        ForeColor = Theme.Accent;
        AccessibleRole = AccessibleRole.Chart;
    }

    /// <summary>The start of the accessible name; the history summary follows it ("Power history, last 300 s: ...").</summary>
    public string Title { get; set; } = "";

    public void Add(double t, double? v)
    {
        Now = t;
        History.Add(t, v);
        string name = Title + ", " + History.Summary(Decimals);   // the UIA Name, so screen readers and tests can read the graph
        if (AccessibleName != name) AccessibleName = name;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width < 12 || Height < 12) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pts = History.Points;
        double top = Math.Max(MinScale, (History.Max ?? 0) * 1.15);
        int marker = Theme.S(4);   // the newest value's dot: room for it on the right and at the top
        float W = Width - marker - 3, H = Height - marker - 3, floor = Height - 1.5f;
        double span = History.WindowSeconds;
        PointF P(double t, double v) => new(1 + (float)((t - (Now - span)) / span * W), floor - (float)(v / top * H));

        using (var axis = new Pen(Theme.Border)) g.DrawLine(axis, 0, Height - 1, Width, Height - 1);

        using var pen = new Pen(ForeColor, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(ForeColor);
        using var fill = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(70, ForeColor), Color.FromArgb(0, ForeColor), LinearGradientMode.Vertical);
        var run = new List<PointF>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                using var area = new GraphicsPath();
                area.AddLines(run.ToArray());
                area.AddLine(run[^1].X, floor, run[0].X, floor);
                area.CloseFigure();
                g.FillPath(fill, area);
                g.DrawLines(pen, run.ToArray());
            }
            else if (run.Count == 1) g.FillEllipse(dot, run[0].X - 1.5f, run[0].Y - 1.5f, 3, 3);
            run.Clear();
        }
        PointF? newest = null;
        foreach (var (t, v) in pts)
        {
            if (v is { } x) { newest = P(t, x); run.Add(newest.Value); }
            else { newest = null; Flush(); }
        }
        Flush();
        if (newest is { } n)
        {
            using var ring = new SolidBrush(BackColor);
            g.FillEllipse(ring, n.X - marker - 2, n.Y - marker - 2, 2 * marker + 4, 2 * marker + 4);
            g.FillEllipse(dot, n.X - marker, n.Y - marker, 2 * marker, 2 * marker);
        }
    }
}

/// <summary>
/// One live reading on the Ride tab, as a card: name, large number with its unit, history graph, and the health light of the device it comes
/// from. With room to spare the graph sits under the number; in a short tile (the minimum window) it sits beside it and the light moves up
/// next to the name. Child control names (the UI Automation ids) are &lt;key&gt;Value, &lt;key&gt;Graph and &lt;key&gt;Health, e.g. PowerValue.
/// </summary>
public sealed class ReadingTile : Panel
{
    private readonly TextLabel _title, _value, _unit;
    private readonly StatusIndicator _health;
    private readonly ToolTip _tip;
    private readonly int _valueColumn;
    private string _advice = "";
    public Sparkline Graph { get; }

    public ReadingTile(string key, string title, string unit, double minScale, int decimals, ToolTip tip)
    {
        Name = key + "Tile";
        _tip = tip;
        Margin = Padding.Empty;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

        _title = new TextLabel(title, Theme.BodyBold, Theme.TextSecondary) { Name = key + "Title", Wrap = false, AutoRelayout = false };
        _value = new TextLabel(ReadingText.None, Theme.Metric, Theme.Text) { Name = key + "Value", Wrap = false, Tight = true, AutoRelayout = false };
        _unit = new TextLabel(unit, Theme.Body, Theme.Muted) { Name = key + "Unit", Wrap = false, Tight = true, AutoRelayout = false };
        Graph = new Sparkline { Name = key + "Graph", MinScale = minScale, Decimals = decimals, Title = title + " history", AccessibleName = title + " history" };
        _health = new StatusIndicator { Name = key + "Health", Font = Theme.Small, AutoRelayout = false };
        _health.Set(HealthRules.Text(Health.NotChosen), null);
        Controls.AddRange(new Control[] { _title, _value, _unit, Graph, _health });

        // the widest number a tile shows, so the graph beside it does not move as the digits change
        _valueColumn = TextRenderer.MeasureText("000.0", Theme.Metric, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width
            + Theme.S(7) + _unit.Measure(0).Width;
        _value.TextChanged += (_, _) => PerformLayout();
        _health.TextChanged += (_, _) => PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int p = Theme.S(16), top = Theme.S(12), w = Math.Max(0, Width - 2 * p);
        var ts = _title.Measure(0);
        _title.SetBounds(p, top, Math.Min(ts.Width, w), ts.Height);
        var vs = _value.Measure(0);
        _value.SetBounds(p + Theme.S(2), _title.Bottom, vs.Width, vs.Height);
        var us = _unit.Measure(0);
        _unit.SetBounds(_value.Right + Theme.S(5), _value.Top + Theme.Ascent(_value.Font) - Theme.Ascent(_unit.Font), us.Width, us.Height);
        var hs = _health.Measure(0);
        int hh = Math.Max(hs.Height, _health.Font.Height);

        bool tall = Height >= _value.Bottom + Theme.S(4) + Theme.S(44) + Theme.S(8) + hh + top;
        if (tall)
        {
            _health.SetBounds(p, Height - top - hh, w, hh);
            int gy = _value.Bottom + Theme.S(4);
            Graph.SetBounds(p, gy, w, Math.Max(0, _health.Top - Theme.S(8) - gy));
        }
        else
        {
            int hw = Math.Max(0, Math.Min(hs.Width, Width - p - (_title.Right + Theme.S(12))));
            _health.SetBounds(Width - p - hw, top + (ts.Height - hh) / 2, hw, hh);
            int gx = p + _valueColumn + Theme.S(12), gy = _title.Bottom + Theme.S(8);
            Graph.SetBounds(gx, gy, Math.Max(0, Width - p - gx), Math.Max(0, Height - Theme.S(14) - gy));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Page);
        Theme.FillRounded(e.Graphics, ClientRectangle, Theme.S(10), Theme.Surface);
    }

    public void Show(string value, Health health, string device, bool bluetooth = false)
    {
        if (_value.Text != value) _value.Text = value;
        _health.Set(HealthRules.Text(health) +(device.Length > 0 ? " - " + device : ""), Theme.HealthColor(health), hollow: health == Health.NotChosen);
        Advice(HealthRules.Advice(health, bluetooth));
    }

    /// <summary>Not riding: the reading and a hint instead of a health light.</summary>
    public void ShowIdle(string value, string hint)
    {
        if (_value.Text != value) _value.Text = value;
        _health.Set(hint, null);
        Advice("");
    }

    private void Advice(string text)
    {
        if (_advice == text) return;
        _advice = text;
        _tip.SetToolTip(_health, text);
    }
}
