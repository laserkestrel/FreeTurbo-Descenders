using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DescendersBridge.Gui;

// The Workouts tab's own controls: the left / right picker, the effort graph, and the panel that sets two cards side by side.
// Like the rest of the design system they only paint and lay out; the rules are the pure PickerModel and EffortGraph.

/// <summary>
/// A picker that never opens a list: the chosen entry in the middle with its detail line under it, the previous and the next
/// entry either side, smaller and greyed, and a small line above saying what is picked and where the choice stands ("Session · 8 of 24").
/// Left / right step one entry and stop at the ends. The mouse: a click on an arrow or a neighbour steps to it, holding it repeats;
/// a click on the chosen entry in the middle raises <see cref="Activated"/> (that starts it, there is no Start button).
/// The chosen entry's name wears the larger <see cref="Theme.Picker"/> font, and the row may be given more height than it asks for.
/// The keys are not read here: the window maps them to <see cref="StepBy"/>, as any other input source can.
/// </summary>
public sealed class PickerRow : Control, IMeasured
{
    /// <summary>The rider has stopped stepping when no step came for this long.</summary>
    public const int SettleMs = 300;
    private const int HoldMs = 400, RepeatMs = 70;
    private const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

    private readonly PickerModel _model;
    private readonly System.Windows.Forms.Timer _repeat = new(), _settle = new() { Interval = SettleMs };
    private int _held, _hot;   // the side the mouse holds down / is over: -1 left, +1 right, 0 neither
    private bool _pressedCentre;   // the mouse went down on the chosen entry: letting go there activates it

    /// <summary>The most a card makes a row taller than it asks for.</summary>
    public static int MaxGrow => Theme.S(28);

    public PickerRow(string label)
    {
        _model = new PickerModel(label);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Margin = Padding.Empty;
        AccessibleName = label;
        Size = Measure(0);
        _repeat.Tick += (_, _) => { _repeat.Interval = RepeatMs; if (_held == 0 || !StepBy(_held)) _repeat.Stop(); };
        _settle.Tick += (_, _) => SettleNow();
    }

    /// <summary>The rider stepped to another entry (raised at once, on every step).</summary>
    public event EventHandler? Changed;
    /// <summary>The rider stepped and then stopped: the place for work too heavy to do on every step of a held key.</summary>
    public event EventHandler? Settled;
    /// <summary>The rider clicked the chosen entry in the middle (never raised by a click on an arrow or a neighbour, nor by a step).</summary>
    public event EventHandler? Activated;

    public PickerModel Model => _model;
    public int Index => _model.Index;
    public int Count => _model.Count;
    /// <summary>The rider is still stepping: <see cref="Settled"/> has not been raised for the last step yet.</summary>
    public bool Pending => _settle.Enabled;

    /// <summary>New entries from the window (not the rider's doing: no event is raised, and a step still waiting to settle is dropped).</summary>
    public void Fill(IReadOnlyList<PickerEntry> entries, int selected)
    {
        _settle.Stop();
        _model.Fill(entries, selected);
        Sync();
    }

    /// <summary>The rider's step: -1 = previous, +1 = next. False at the end of the list (nothing moved).</summary>
    public bool StepBy(int delta)
    {
        if (!_model.Step(delta)) return false;
        Sync();
        Changed?.Invoke(this, EventArgs.Empty);
        _settle.Stop();
        _settle.Start();
        return true;
    }

    /// <summary>Raise <see cref="Settled"/> now if a step is still waiting for it.</summary>
    public void SettleNow()
    {
        if (!_settle.Enabled) return;
        _settle.Stop();
        Settled?.Invoke(this, EventArgs.Empty);
    }

    private void Sync()
    {
        var now = _model.Current;
        Text = now?.Title ?? "";
        AccessibleDescription = now is { } e ? e.Title + (e.Detail.Length > 0 ? ", " + e.Detail : "") + " (" + _model.Position + ")" : "Nothing to choose";
        Invalidate();
    }

    private static int CaptionHeight => Theme.Small.Height + Theme.S(3);

    public Size Measure(int width) =>
        new(width > 0 ? width : Theme.S(300), CaptionHeight + Theme.Picker.Height + Theme.Small.Height + Theme.S(18));

    private static int Arrow => Theme.S(22);

    /// <summary>The rounded row under the caption, and inside it: the left arrow and neighbour, the chosen entry, the right neighbour and arrow.</summary>
    private (Rectangle Row, Rectangle Left, Rectangle Centre, Rectangle Right) Zones()
    {
        var row = Rectangle.FromLTRB(0, CaptionHeight, Width, Height);
        int side = PickerLayout.Side(row.Width, Arrow);
        var left = new Rectangle(row.Left, row.Top, side, row.Height);
        var right = new Rectangle(row.Right - side, row.Top, side, row.Height);
        return (row, left, Rectangle.FromLTRB(left.Right + Theme.S(6), row.Top, right.Left - Theme.S(6), row.Bottom), right);
    }

    /// <summary>What the point is over; the caption above the row is nothing.</summary>
    private PickerHit HitAt(Point p) => p.Y < CaptionHeight || p.Y >= Height || _model.Current is null ? PickerHit.None : PickerLayout.Hit(p.X, Width, Arrow);

    private int SideAt(Point p) => HitAt(p) switch
    {
        PickerHit.Previous when _model.CanPrevious => -1,
        PickerHit.Next when _model.CanNext => 1,
        _ => 0,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Select();
        _pressedCentre = e.Button == MouseButtons.Left && HitAt(e.Location) == PickerHit.Centre;
        int side = e.Button == MouseButtons.Left ? SideAt(e.Location) : 0;
        if (side == 0) return;
        _held = side;
        StepBy(side);
        _repeat.Interval = HoldMs;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Release();
        bool activate = _pressedCentre && e.Button == MouseButtons.Left && HitAt(e.Location) == PickerHit.Centre;
        _pressedCentre = false;
        if (activate) Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Release(); if (_hot != 0) { _hot = 0; Invalidate(); } }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Release(); _pressedCentre = false; Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = SideAt(e.Location);
        // a hand over what a click does something on: a side that can step, and the chosen entry when a click starts it
        Cursor = hot != 0 || (Activated is not null && HitAt(e.Location) == PickerHit.Centre) ? Cursors.Hand : Cursors.Default;
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    private void Release()
    {
        _held = 0;
        _repeat.Stop();
    }

    /// <summary><paramref name="text"/>, or as much of it as fits <paramref name="width"/> with "…" at the end.</summary>
    private static string Fit(Graphics g, string text, Font font, int width)
    {
        int Wide(string s) => TextRenderer.MeasureText(g, s, font, new Size(int.MaxValue, int.MaxValue), OneLine).Width;
        if (width <= 0) return "";
        if (Wide(text) <= width) return text;
        int lo = 0, hi = text.Length - 1;   // the most characters that fit, found by halving
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Wide(PickerText.Cut(text, mid)) <= width) lo = mid; else hi = mid - 1;
        }
        return PickerText.Cut(text, lo);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        bool focus = Focused;
        var (row, left, centre, right) = Zones();
        TextRenderer.DrawText(g, _model.Position, Theme.Small, new Rectangle(0, 0, Width, Theme.Small.Height), focus ? Theme.Text : Theme.Muted,
            OneLine | TextFormatFlags.EndEllipsis);

        // the focused row is marked: the accent's wash and a strong accent edge
        Theme.FillRounded(g, row, Theme.S(7), focus ? Theme.AccentWash : Theme.Field, focus ? Theme.Accent : Theme.Border);
        if (focus)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Theme.Rounded(new Rectangle(row.X + 1, row.Y + 1, row.Width - 3, row.Height - 3), Theme.S(6));
            using var pen = new Pen(Theme.Accent, 2f);
            g.DrawPath(pen, path);
        }
        if (_model.Current is not { } now)
        {
            TextRenderer.DrawText(g, "Nothing to choose yet", Theme.Small, row, Theme.Muted, OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        int arrow = Arrow, pad = Theme.S(2);
        if (_hot != 0) Theme.FillRounded(g, Rectangle.Inflate(_hot < 0 ? left : right, -Theme.S(3), -Theme.S(3)), Theme.S(5), focus ? Theme.Field : Theme.FieldHover);
        if (_model.Previous is { } before)
        {
            Chevron(g, new Rectangle(left.Left, left.Top, arrow, left.Height), -1);
            var r = Rectangle.FromLTRB(left.Left + arrow, left.Top, left.Right - pad, left.Bottom);
            TextRenderer.DrawText(g, Fit(g, before.Title, Theme.Small, r.Width), Theme.Small, r, Theme.Muted, OneLine | TextFormatFlags.Left);
        }
        if (_model.Next is { } after)
        {
            Chevron(g, new Rectangle(right.Right - arrow, right.Top, arrow, right.Height), 1);
            var r = Rectangle.FromLTRB(right.Left + pad, right.Top, right.Right - arrow, right.Bottom);
            TextRenderer.DrawText(g, Fit(g, after.Title, Theme.Small, r.Width), Theme.Small, r, Theme.Muted, OneLine | TextFormatFlags.Right);
        }

        int titleH = Theme.Picker.Height, detailH = now.Detail.Length > 0 ? Theme.Small.Height : 0, top = centre.Top + (centre.Height - titleH - detailH) / 2;
        TextRenderer.DrawText(g, Fit(g, now.Title, Theme.Picker, centre.Width), Theme.Picker, new Rectangle(centre.Left, top, centre.Width, titleH), Theme.Text,
            OneLine | TextFormatFlags.HorizontalCenter);
        if (detailH > 0)
            TextRenderer.DrawText(g, Fit(g, now.Detail, Theme.Small, centre.Width), Theme.Small, new Rectangle(centre.Left, top + titleH, centre.Width, detailH), Theme.TextSecondary,
                OneLine | TextFormatFlags.HorizontalCenter);
    }

    private void Chevron(Graphics g, Rectangle r, int side)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, a = Theme.S(4);
        using var pen = new Pen(_hot == side ? Theme.Text : Theme.TextSecondary, 1.6f);
        g.DrawLines(pen, new[] { new PointF(cx - side * a / 2, cy - a), new PointF(cx + side * a / 2, cy), new PointF(cx - side * a / 2, cy + a) });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _repeat.Dispose(); _settle.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The effort graph of the workout in the middle of a picker: target power over time as blocks (width = how long, height = how
/// hard, sloped for a warm-up, a cool-down or a ramp), coloured by zone, a dashed line at FTP, a dashed outline for a free-ride step, and
/// under it a line with the total time and the hardest target. A workout that cannot be shown gives its message in place of the graph.
/// Its <see cref="Control.Text"/> is that line (or the message), for a test or a screen reader. A click on a graph starts
/// what it shows (<see cref="Activated"/>), so the mouse is a hand over one.
/// </summary>
internal sealed class EffortGraphView : Control
{
    private EffortGraph _graph = EffortGraph.Empty;
    private bool _pressed;

    /// <summary>The rider clicked the graph (the left button went down and came up on it).</summary>
    public event EventHandler? Activated;

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = e.Button == MouseButtons.Left; }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool activate = _pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        _pressed = false;
        if (activate) Activated?.Invoke(this, EventArgs.Empty);
    }

    public EffortGraphView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Margin = Padding.Empty;
        AccessibleRole = AccessibleRole.Graphic;
        Size = new Size(Theme.S(200), MinHeight);
    }

    /// <summary>The least height the graph is useful at: the line of text and a low strip of blocks.</summary>
    public static int MinHeight => Theme.Small.Height + Theme.S(44);

    public EffortGraph Graph
    {
        get => _graph;
        set
        {
            _graph = value;
            Text = value.Text;
            Cursor = value.Ok ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        if (_graph.Message is { } message)
        {
            TextRenderer.DrawText(g, message, Theme.Small, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        int textH = Theme.Small.Height + Theme.S(2);
        var chart = Rectangle.FromLTRB(0, Theme.S(2), Width - 1, Height - textH - Theme.S(2));
        TextRenderer.DrawText(g, _graph.Summary, Theme.Small, new Rectangle(0, Height - textH, Width, textH), Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (chart.Height < Theme.S(10) || chart.Width < Theme.S(20)) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float Y(float share) => chart.Bottom - Math.Clamp(share, 0f, 1f) * chart.Height;
        foreach (var b in _graph.Blocks)
        {
            float x0 = chart.Left + b.X0 * chart.Width, x1 = chart.Left + b.X1 * chart.Width;
            x1 = EffortGraph.BlockEnd(x0, x1);   // a gap between neighbouring blocks
            if (b.FreeRide)
            {
                var box = RectangleF.FromLTRB(x0, Y(b.Y0), x1, chart.Bottom);
                using var dash = new Pen(Theme.Muted, 1f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(dash, box.X, box.Y, Math.Max(1f, box.Width - 1f), Math.Max(1f, box.Height - 1f));
                var label = TextRenderer.MeasureText(g, "free ride", Theme.Caption);
                if (label.Width + Theme.S(4) <= box.Width && label.Height <= box.Height)
                    TextRenderer.DrawText(g, "free ride", Theme.Caption, Rectangle.Round(box), Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                continue;
            }
            using var fill = new SolidBrush(Theme.ZoneColor(b.Zone));
            g.FillPolygon(fill, new[] { new PointF(x0, chart.Bottom), new PointF(x0, Y(b.Y0)), new PointF(x1, Y(b.Y1)), new PointF(x1, chart.Bottom) });
        }

        g.SmoothingMode = SmoothingMode.None;
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
        if (_graph.FtpY > 0f)
        {
            int y = (int)Math.Round(Y(_graph.FtpY));
            using var dash = new Pen(Theme.TextSecondary, 1f) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, chart.Left, y, chart.Right, y);
            int capH = Theme.Caption.Height;
            if (y - capH >= 0)
                TextRenderer.DrawText(g, "FTP", Theme.Caption, new Rectangle(chart.Right - Theme.S(40), y - capH, Theme.S(40), capH), Theme.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>
/// A card of the Workouts tab. Its <see cref="StackPanel.Fill"/> is the effort graph, which no longer takes all the height
/// the other lines leave: <see cref="CardRules.Share"/> gives it part, makes the picker rows taller with another part, and spreads the
/// rest between the lines above the graph (the line under the graph stays against it).
/// </summary>
public sealed class MenuCard : Card
{
    protected override void Arrange()
    {
        var kids = Shown();
        int inner = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        var heights = new int[kids.Count];
        int total = Padding.Vertical, n = 0, rows = 0, above = 0, fill = kids.IndexOf(Fill!);
        for (int i = 0; i < kids.Count; i++)
        {
            heights[i] = i == fill ? FillMin : Math.Max(0, Ui.Measure(kids[i], inner).Height);
            if (heights[i] <= 0) continue;
            n++;
            total += heights[i];
            if (kids[i] is PickerRow) rows++;
            if (i < fill) above++;
        }
        total += Gap * Math.Max(0, n - 1);
        var (graph, row, gap) = CardRules.Share(ClientSize.Height - total, rows, above, PickerRow.MaxGrow);

        int y = Padding.Top;
        for (int i = 0; i < kids.Count; i++)
        {
            int h = heights[i] <= 0 ? 0 : heights[i] + (i == fill ? graph : kids[i] is PickerRow ? row : 0);
            kids[i].SetBounds(Padding.Left, y, inner, h);
            if (h > 0) y += h + Gap + (i < fill ? gap : 0);
        }
    }
}

/// <summary>A one-line hint that keeps its room while it has nothing to say, so the page does not move when it comes and goes.</summary>
public sealed class HintLine : TextLabel
{
    public HintLine() : base("", Theme.Small, Theme.Muted)
    {
        Wrap = false;
        AutoRelayout = false;
    }

    /// <summary>In line with the text of the card above it.</summary>
    protected override int Lead => Theme.S(14);

    public override Size Measure(int width) => new(width > 0 ? width : Theme.S(200), Font.Height);
}

/// <summary>Children side by side at equal widths, all as tall as the panel, which is as tall as the tallest of them needs (two cards next to each other).</summary>
public sealed class Columns : LayoutPanel
{
    private int Cell(int width, int count) => Math.Max(1, (width - Padding.Horizontal - Gap * (count - 1)) / count);

    public override Size Measure(int width)
    {
        var kids = Shown();
        if (width <= 0) width = Width;
        if (kids.Count == 0) return new Size(width, Padding.Vertical);
        int cell = Cell(width, kids.Count), tallest = 0;
        foreach (var c in kids) tallest = Math.Max(tallest, Ui.Measure(c, cell).Height);
        return new Size(width, tallest + Padding.Vertical);
    }

    protected override void Arrange()
    {
        var kids = Shown();
        if (kids.Count == 0) return;
        int cell = Cell(ClientSize.Width, kids.Count);
        for (int i = 0; i < kids.Count; i++)
            kids[i].SetBounds(Padding.Left + i * (cell + Gap), Padding.Top, cell, Math.Max(0, ClientSize.Height - Padding.Vertical));
    }
}
