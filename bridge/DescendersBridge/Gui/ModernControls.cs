using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DescendersBridge.Gui;

// The window's reusable controls. Each one is a standard WinForms control underneath (Label, Button, ComboBox, ListView,
// TabControl, Panel), so Names / AutomationIds, keyboard use and UI Automation patterns are what they always were; only the painting and
// the layout are ours. Colours, fonts and sizes come from Theme.

// ================= text =================

/// <summary>
/// A label that wraps to the width it is given and tells the layout panels how tall that makes it (the stock Label's auto-size needs a fixed
/// maximum width, which does not follow the window).
/// </summary>
public class TextLabel : Label, IMeasured
{
    private bool _wrap = true;

    public TextLabel()
    {
        AutoSize = false;
        UseMnemonic = false;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Margin = Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public TextLabel(string text, Font? font = null, Color? color = null) : this()
    {
        if (font is not null) Font = font;
        if (color is { } c) ForeColor = c;
        Text = text;
    }

    /// <summary>False: one line, cut with "..." when it does not fit.</summary>
    public bool Wrap { get => _wrap; set { _wrap = value; Invalidate(); } }

    /// <summary>False inside a control that lays its own children out (the reading tiles): a new text then does not re-run the page layout.</summary>
    public bool AutoRelayout { get; set; } = true;

    /// <summary>Space kept free on the left (the status light).</summary>
    protected virtual int Lead => 0;

    /// <summary>No blank margin left and right of the text, so a neighbour can sit right against it (the big number and its unit).</summary>
    public bool Tight { get; set; }

    /// <summary>A one-line label is never narrower than this (headings of rows under each other, so what follows them lines up).</summary>
    public int MinWidth { get; set; }

    private TextFormatFlags OneLine => TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Tight ? TextFormatFlags.NoPadding : 0);
    private TextFormatFlags Flags => _wrap
        ? TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
        : OneLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

    public virtual Size Measure(int width)
    {
        if (Text.Length == 0) return Size.Empty;
        if (width <= 0 || !_wrap)
        {
            var one = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), OneLine);
            int w = Math.Max(one.Width + Lead, MinWidth);
            return new Size(width > 0 ? Math.Min(w, width) : w, one.Height);
        }
        var s = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(Theme.S(40), width - Lead), int.MaxValue), Flags);
        return new Size(Math.Min(width, s.Width + Lead), s.Height);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        if (AutoRelayout) Ui.Relayout(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Lead, 0, Width - Lead, Height), ForeColor, Flags);
    }
}

/// <summary>A card's title.</summary>
public sealed class SectionHeader : TextLabel
{
    public SectionHeader(string text) : base(text, Theme.Heading, Theme.Text) { }
}

/// <summary>A small coloured light with words next to it (the words always say what the colour means). A hollow light = nothing there.</summary>
public class StatusIndicator : TextLabel
{
    private Color? _light;
    private bool _hollow;

    public StatusIndicator()
    {
        ForeColor = Theme.TextSecondary;
        Wrap = false;
    }

    /// <summary>The light's diameter: the same for every light in the window, not taken from the font.</summary>
    public static int Dot => Theme.LightSize;
    protected override int Lead => _light is null ? 0 : Dot + Theme.S(7);

    /// <summary>The light as last set (null = words only), for `selftest`.</summary>
    public Color? Light => _light;

    /// <param name="light">null = words only, no light.</param>
    public void Set(string text, Color? light, bool hollow = false)
    {
        bool leadChanged = (_light is null) != (light is null);
        if (_light != light || _hollow != hollow)
        {
            _light = light;
            _hollow = hollow;
            Invalidate();
        }
        if (Text != text) Text = text;
        else if (leadChanged && AutoRelayout) Ui.Relayout(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_light is not { } c) return;
        int d = Dot;
        int y = Wrap ? Math.Max(0, (Font.Height - d) / 2 + 1) : (Height - d) / 2 + 1;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hollow) { using var pen = new Pen(c, 1.6f); g.DrawEllipse(pen, 1, y + 1, d - 2, d - 2); }
        else { using var b = new SolidBrush(c); g.FillEllipse(b, 0, y, d, d); }
    }
}

// ================= buttons =================

/// <summary>Primary = the one main action of a page (filled, accent); Secondary = everything else; Danger = stop; Ghost = quiet (Dismiss).</summary>
public enum ButtonKind { Primary, Secondary, Danger, Ghost }

/// <summary>A rounded, flat button. Still a <see cref="Button"/>: click, Enter / Space, focus and UI Automation Invoke are unchanged.</summary>
public sealed class ModernButton : Button, IMeasured
{
    private ButtonKind _kind = ButtonKind.Secondary;
    private bool _hover, _down, _large;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
        AutoSize = false;
        Font = Theme.BodyBold;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
        Size = Measure(0);
    }

    public ButtonKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

    /// <summary>The focus ring shows whenever the button has the focus, not only once a key was pressed (a question box's buttons).</summary>
    public bool FocusAlwaysShown { get; set; }

    /// <summary>The big Start / Stop button.</summary>
    public bool Large
    {
        get => _large;
        set { _large = value; Font = value ? Theme.ButtonLarge : Theme.BodyBold; Size = Measure(0); }
    }

    public Size Measure(int width)
    {
        var t = TextRenderer.MeasureText(Text.Length > 0 ? Text : "X", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(Math.Max(Theme.S(_large ? 160 : 84), t.Width + Theme.S(_large ? 48 : 30)), Font.Height + Theme.S(_large ? 24 : 14));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = Measure(0);
        Ui.Relayout(this);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color back = Parent?.BackColor ?? Theme.Page;
        g.Clear(back);
        Color fill, text = Color.White;
        Color? border = null;
        if (!Enabled) { fill = Theme.Field; text = Theme.Muted; }
        else switch (_kind)
        {
            case ButtonKind.Primary: fill = _down ? Theme.AccentButtonPressed : _hover ? Theme.AccentButtonHover : Theme.AccentButton; break;
            case ButtonKind.Danger: fill = _down ? Theme.DangerButtonPressed : _hover ? Theme.DangerButtonHover : Theme.DangerButton; break;
            case ButtonKind.Ghost: fill = _down || _hover ? Theme.FieldHover : back; text = Theme.TextSecondary; break;
            default: fill = _down ? Theme.Border : _hover ? Theme.FieldHover : Theme.Field; text = Theme.Text; border = Theme.Border; break;
        }
        Theme.FillRounded(g, ClientRectangle, Theme.S(8), fill, border);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && (ShowFocusCues || FocusAlwaysShown || Ui.PadInUse))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 2, Width - 5, Height - 5);
            using var path = Theme.Rounded(r, Theme.S(6));
            using var pen = new Pen(Theme.Text, 1.5f);
            g.DrawPath(pen, path);
        }
    }
}

// ================= question box =================

/// <summary>
/// A yes / no question in the window's look (the stock MessageBox cannot be driven from the Xbox pad): the question, then
/// "Yes" and "No" side by side, "No" focused when it opens. The keys work as in any dialog (left / right move, Enter or Space presses
/// the focused button, Esc = No); the pad comes in through <see cref="Pad"/>. ShowDialog gives <see cref="DialogResult.Yes"/> or No.
/// </summary>
public sealed class QuestionBox : Form
{
    private readonly ModernButton _yes, _no;

    public QuestionBox(string question)
    {
        Name = "QuestionBox";
        Text = "Turbo Trainer Bridge";
        Font = Theme.Body;
        BackColor = Theme.Page;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new StackPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(22), Theme.S(20), Theme.S(22), Theme.S(18)), Gap = Theme.S(18) };
        root.Controls.Add(new TextLabel(question) { Name = "QuestionText" });
        _yes = new ModernButton { Name = "QuestionYes", Text = "Yes", DialogResult = DialogResult.Yes, FocusAlwaysShown = true };
        _no = new ModernButton { Name = "QuestionNo", Text = "No", DialogResult = DialogResult.No, FocusAlwaysShown = true };
        var space = new TextLabel();
        var buttons = new Row { Gap = Theme.S(10), Stretch = space };
        buttons.Controls.AddRange(new Control[] { space, _yes, _no });
        root.Controls.Add(buttons);
        Controls.Add(root);
        CancelButton = _no;
        int width = Theme.S(440);
        ClientSize = new Size(width, root.Measure(width).Height);
        ActiveControl = _no;
    }

    /// <summary>The button that has the focus is "Yes".</summary>
    public bool OnYes => ReferenceEquals(ActiveControl, _yes);

    /// <summary>One press of the pad (<see cref="PadRules.Question"/>).</summary>
    public void Pad(PadAction action)
    {
        switch (PadRules.Question(action))
        {
            case QuestionStep.FocusYes: _yes.Select(); break;
            case QuestionStep.FocusNo: _no.Select(); break;
            case QuestionStep.PressFocused: (OnYes ? _yes : _no).PerformClick(); break;
            case QuestionStep.No: _no.PerformClick(); break;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }
}

// ================= layout panels and cards =================

/// <summary>
/// A panel that places its own children (see <see cref="StackPanel"/>, <see cref="Row"/>, <see cref="UniformGrid"/>) and can look like a
/// card: a rounded, slightly lighter surface. Children hidden with <see cref="Ui.SetVisible"/> take no space.
/// </summary>
public abstract class LayoutPanel : Panel, IMeasured
{
    private bool _card;

    protected LayoutPanel()
    {
        Margin = Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Space between children.</summary>
    public int Gap { get; set; } = Theme.S(8);

    public bool CardStyle
    {
        get => _card;
        set
        {
            _card = value;
            if (!value) return;
            BackColor = Theme.Surface;
            if (Padding == Padding.Empty) Padding = new Padding(Theme.S(18), Theme.S(16), Theme.S(18), Theme.S(16));
        }
    }

    public abstract Size Measure(int width);
    protected abstract void Arrange();

    protected List<Control> Shown()
    {
        var list = new List<Control>(Controls.Count);
        foreach (Control c in Controls) if (!Ui.IsHidden(c)) list.Add(c);
        return list;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        // a scroll bar that appears or goes while arranging changes the width: arrange again for the new width
        for (int pass = 0; pass < 3; pass++)
        {
            int width = ClientSize.Width;
            Arrange();
            base.OnLayout(e);
            if (ClientSize.Width == width) break;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_card) { base.OnPaintBackground(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Page);
        Theme.FillRounded(e.Graphics, ClientRectangle, Theme.S(10), Theme.Surface);
    }
}

/// <summary>
/// Children one under the other, each as wide as the panel and as tall as it needs. One child may be the <see cref="Fill"/>: it takes the
/// height that is left. With <see cref="Scrolls"/> the panel scrolls when the window is too short for everything.
/// </summary>
public class StackPanel : LayoutPanel
{
    /// <summary>The child that takes the height left over (never less than <see cref="FillMin"/>, or than what it needs).</summary>
    public Control? Fill { get; set; }
    public int FillMin { get; set; }
    /// <summary>A thin line between neighbouring rows, so a list of settings is easy to scan.</summary>
    public bool Dividers { get; set; }

    public bool Scrolls
    {
        get => AutoScroll;
        set { AutoScroll = value; if (value) Theme.DarkScrollBars(this); }
    }

    private static int HeightOf(Control c, int width) => c is IMeasured m ? m.Measure(width).Height : c.Height;

    private int FillMinFor(int width)
    {
        if (Fill is null) return 0;
        if (Fill is StackPanel { Scrolls: true } || Fill is not IMeasured m) return FillMin;   // a scrolling panel takes whatever is left
        return Math.Max(FillMin, m.Measure(width).Height);
    }

    public override Size Measure(int width)
    {
        if (width <= 0) width = Width;
        int inner = Math.Max(1, width - Padding.Horizontal), total = Padding.Vertical, n = 0;
        foreach (var c in Shown())
        {
            int h = c == Fill ? FillMinFor(inner) : HeightOf(c, inner);
            if (h <= 0) continue;
            total += h + (n++ > 0 ? Gap : 0);
        }
        return new Size(width, total);
    }

    protected override void Arrange()
    {
        var kids = Shown();
        int inner = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        var heights = new int[kids.Count];
        int total = Padding.Vertical, n = 0, fill = -1;
        for (int i = 0; i < kids.Count; i++)
        {
            if (kids[i] == Fill) { fill = i; n++; continue; }
            heights[i] = Math.Max(0, HeightOf(kids[i], inner));
            if (heights[i] > 0) n++;
            total += heights[i];
        }
        total += Gap * Math.Max(0, n - 1);
        if (fill >= 0)
        {
            heights[fill] = Math.Max(FillMinFor(inner), ClientSize.Height - total);
            total += heights[fill];
        }
        if (AutoScroll)
        {
            var min = new Size(0, total);
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
        }
        var origin = AutoScroll ? AutoScrollPosition : Point.Empty;
        int y = Padding.Top + origin.Y;
        for (int i = 0; i < kids.Count; i++)
        {
            kids[i].SetBounds(Padding.Left + origin.X, y, inner, heights[i]);
            if (heights[i] > 0 || i == fill) y += heights[i] + Gap;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Dividers) return;
        var kids = Shown();
        using var pen = new Pen(Theme.Border);
        for (int i = 1; i < kids.Count; i++)
        {
            if (kids[i - 1] is not Row || kids[i] is not Row) continue;
            int y = (kids[i - 1].Bottom + kids[i].Top) / 2;
            e.Graphics.DrawLine(pen, Padding.Left, y, Width - Padding.Right, y);
        }
    }
}

/// <summary>A card: a rounded surface with an optional title and a line of explanation, then whatever is added to it.</summary>
public class Card : StackPanel
{
    public Card(string? title = null, string? intro = null)
    {
        CardStyle = true;
        Gap = Theme.S(10);
        if (title is not null) Controls.Add(new SectionHeader(title));
        if (intro is not null) Controls.Add(new TextLabel(intro, Theme.Body, Theme.TextSecondary));
    }
}

/// <summary>
/// Children side by side, centred vertically, each at its own width; the <see cref="Stretch"/> child takes the width that is left (a text
/// wraps inside it). Put the stretch child before a button to push the button to the right edge.
/// </summary>
public class Row : LayoutPanel
{
    private Color? _accentBar;

    public Control? Stretch { get; set; }
    /// <summary>A coloured bar down the left edge (the message bar: the accent colour = information, red = a problem).</summary>
    public Color? AccentBar { get => _accentBar; set { _accentBar = value; Invalidate(); } }
    /// <summary>The row is never lower than this (a heading alone, as tall as the heading-with-a-button next to it).</summary>
    public int MinHeight { get; set; }

    private Size[] Sizes(List<Control> kids, int inner)
    {
        var sizes = new Size[kids.Count];
        int used = Gap * Math.Max(0, kids.Count - 1), stretch = -1;
        for (int i = 0; i < kids.Count; i++)
        {
            if (kids[i] == Stretch) { stretch = i; continue; }
            sizes[i] = Ui.Measure(kids[i], 0);
            used += sizes[i].Width;
        }
        if (stretch >= 0)
        {
            int w = Math.Max(Theme.S(60), inner - used);
            sizes[stretch] = new Size(w, Ui.Measure(kids[stretch], w).Height);
        }
        return sizes;
    }

    public override Size Measure(int width)
    {
        var kids = Shown();
        if (width <= 0)
        {
            int w = Padding.Horizontal + Gap * Math.Max(0, kids.Count - 1), h = 0;
            foreach (var c in kids) { var s = Ui.Measure(c, 0); w += s.Width; h = Math.Max(h, s.Height); }
            return new Size(w, Math.Max(MinHeight, h + Padding.Vertical));
        }
        int tallest = 0;
        foreach (var s in Sizes(kids, width - Padding.Horizontal)) tallest = Math.Max(tallest, s.Height);
        return new Size(width, Math.Max(MinHeight, tallest + Padding.Vertical));
    }

    protected override void Arrange()
    {
        var kids = Shown();
        var sizes = Sizes(kids, ClientSize.Width - Padding.Horizontal);
        int h = ClientSize.Height - Padding.Vertical, x = Padding.Left;
        for (int i = 0; i < kids.Count; i++)
        {
            kids[i].SetBounds(x, Padding.Top + Math.Max(0, (h - sizes[i].Height) / 2), sizes[i].Width, sizes[i].Height);
            x += sizes[i].Width + Gap;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_accentBar is not { } c) return;
        using var b = new SolidBrush(c);
        e.Graphics.FillRectangle(b, 0, 0, Theme.S(4), Height);
    }
}

/// <summary>One setting: its name and a line of help on the left, the control that edits it on the right.</summary>
public sealed class SettingRow : Row
{
    public SettingRow(string title, string help, Control editor)
    {
        Gap = Theme.S(20);
        Padding = new Padding(0, Theme.S(5), 0, Theme.S(5));
        var text = new StackPanel { Gap = Theme.S(2) };
        text.Controls.Add(new TextLabel(title, Theme.BodyBold, Theme.Text));
        if (help.Length > 0) text.Controls.Add(new TextLabel(help, Theme.Small, Theme.Muted));
        Controls.Add(text);
        Controls.Add(editor);
        Stretch = text;
    }
}

/// <summary>Equal cells filling the panel, row by row (the four reading tiles).</summary>
public sealed class UniformGrid : LayoutPanel
{
    public int Columns { get; set; } = 2;
    /// <summary>The least height a cell is given; below that the page runs out of room rather than squashing the cells.</summary>
    public int MinCellHeight { get; set; } = Theme.S(100);

    public override Size Measure(int width)
    {
        int cols = Math.Max(1, Columns), rows = (Shown().Count + cols - 1) / cols;
        return new Size(width > 0 ? width : Width, rows * MinCellHeight + Gap * Math.Max(0, rows - 1));
    }

    protected override void Arrange()
    {
        var kids = Shown();
        if (kids.Count == 0) return;
        int cols = Math.Max(1, Columns), rows = (kids.Count + cols - 1) / cols;
        int w = (ClientSize.Width - Gap * (cols - 1)) / cols, h = (ClientSize.Height - Gap * (rows - 1)) / rows;
        for (int i = 0; i < kids.Count; i++)
            kids[i].SetBounds(i % cols * (w + Gap), i / cols * (h + Gap), Math.Max(0, w), Math.Max(0, h));
    }
}

// ================= input fields =================

/// <summary>
/// The rounded frame around a text box or a number box. The real control sits inside without its own border, so typing, the Value pattern
/// and its Name are unchanged. A number box gets a - and a + on either side (the arrow keys and the mouse wheel work as before).
/// </summary>
public sealed class FieldFrame : Control
{
    private readonly Control _inner;
    private readonly Panel? _clip;
    private readonly StepButton? _minus, _plus;

    public FieldFrame(Control inner, int width)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Margin = Padding.Empty;
        BackColor = Theme.Field;
        _inner = inner;
        inner.Font = Theme.Body;
        inner.BackColor = Theme.Field;
        inner.ForeColor = Theme.Text;
        inner.Margin = Padding.Empty;
        if (inner is NumericUpDown n)
        {
            n.BorderStyle = BorderStyle.None;
            n.TextAlign = HorizontalAlignment.Center;
            // the number box's own little arrows are clipped away (they cannot be themed); the - and + here do the same job
            _clip = new Panel { BackColor = Theme.Field, Margin = Padding.Empty };
            _clip.Controls.Add(n);
            _minus = new StepButton("−", "Decrease", () => Step(n, up: false));
            _plus = new StepButton("+", "Increase", () => Step(n, up: true));
            Controls.Add(_minus);
            Controls.Add(_clip);
            Controls.Add(_plus);
        }
        else
        {
            if (inner is TextBox t) t.BorderStyle = BorderStyle.None;
            Controls.Add(inner);
        }
        inner.Enter += (_, _) => Invalidate();
        inner.Leave += (_, _) => Invalidate();
        Size = new Size(width, Theme.Body.Height + Theme.S(14));
    }

    private static void Step(NumericUpDown n, bool up)
    {
        if (!n.Enabled || n.ReadOnly) return;
        n.Focus();
        if (up) n.UpButton(); else n.DownButton();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int inset = Theme.S(3);
        if (_clip is not null && _minus is not null && _plus is not null)
        {
            int b = Height - 2 * inset;
            _minus.SetBounds(inset, inset, b, b);
            _plus.SetBounds(Width - inset - b, inset, b, b);
            _clip.SetBounds(inset + b + 2, inset, Math.Max(0, Width - 2 * (inset + b + 2)), b);
            int spin = 0;
            foreach (Control c in _inner.Controls) if (c is not TextBox) spin = Math.Max(spin, c.Width);
            if (spin == 0) spin = SystemInformation.VerticalScrollBarWidth;
            _inner.SetBounds(0, Math.Max(0, (b - _inner.Height) / 2), _clip.Width + spin, _inner.Height);
        }
        else _inner.SetBounds(Theme.S(10), Math.Max(0, (Height - _inner.Height) / 2), Math.Max(0, Width - Theme.S(20)), _inner.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.FillRounded(e.Graphics, ClientRectangle, Theme.S(7), Theme.Field, ContainsFocus ? Theme.Accent : Theme.Border);
    }

    /// <summary>The - / + of a number box. Mouse only: the keyboard uses the arrow keys in the box itself.</summary>
    private sealed class StepButton : Control
    {
        private readonly string _glyph;
        private readonly Action _step;
        private bool _hover;

        public StepButton(string glyph, string name, Action step)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            _glyph = glyph;
            _step = step;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = name;
            Cursor = Cursors.Hand;
            BackColor = Theme.Field;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) _step(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Field);
            if (_hover) Theme.FillRounded(e.Graphics, ClientRectangle, Theme.S(5), Theme.FieldHover);
            TextRenderer.DrawText(e.Graphics, _glyph, Theme.BodyBold, ClientRectangle, _hover ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>A drop-down list (pick one) in the window's colours. Still a <see cref="ComboBox"/>; the closed box and the list rows are painted here.</summary>
public sealed class ModernCombo : ComboBox
{
    private const int WmPaint = 0x000F, WmPrint = 0x0317, WmPrintClient = 0x0318;
    private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    public ModernCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Body;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Margin = Padding.Empty;
        ItemHeight = Theme.Body.Height + Theme.S(8);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool closedBox = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !closedBox && (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(selected ? Theme.AccentWash : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var r = Rectangle.FromLTRB(Theme.S(10), e.Bounds.Top, e.Bounds.Right - Theme.S(4), e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, Theme.Text, TextFlags);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmPaint && IsHandleCreated) { using var g = Graphics.FromHwnd(Handle); PaintClosed(g); }
        else if ((m.Msg == WmPrint || m.Msg == WmPrintClient) && m.WParam != IntPtr.Zero) { using var g = Graphics.FromHdc(m.WParam); PaintClosed(g); }   // DrawToBitmap
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    /// <summary>The closed box: a rounded field, the chosen text, a small arrow.</summary>
    private void PaintClosed(Graphics g)
    {
        var r = ClientRectangle;
        using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(b, r);
        Theme.FillRounded(g, r, Theme.S(7), Theme.Field, Focused ? Theme.Accent : Theme.Border);
        int arrow = Theme.S(28);
        string text = SelectedIndex >= 0 ? GetItemText(SelectedItem) ?? "" : "";
        TextRenderer.DrawText(g, text, Font, Rectangle.FromLTRB(Theme.S(10), 0, r.Right - arrow, r.Height), Enabled ? Theme.Text : Theme.Muted, TextFlags);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = r.Right - arrow / 2f - Theme.S(2), cy = r.Height / 2f, a = Theme.S(4);
        using var pen = new Pen(Theme.TextSecondary, 1.6f);
        g.DrawLines(pen, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
    }
}

/// <summary>A thin progress line (the device search).</summary>
public sealed class ModernProgress : Control
{
    private int _value;

    public ModernProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        Size = new Size(Theme.S(160), Theme.S(6));
    }

    /// <summary>0 to 100.</summary>
    public int Value
    {
        get => _value;
        set { value = Math.Clamp(value, 0, 100); if (_value == value) return; _value = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.FillRounded(e.Graphics, ClientRectangle, Height / 2, Theme.Field);
        int w = Width * _value / 100;
        if (w >= Height) Theme.FillRounded(e.Graphics, new Rectangle(0, 0, w, Height), Height / 2, Theme.Accent);
    }
}

// ================= list and tabs =================

/// <summary>
/// A details list in the window's colours (the devices found by a search). Still a <see cref="ListView"/>: rows, selection and UI
/// Automation are unchanged; the header and cells are painted here, and the last column takes the width that is left.
/// </summary>
public sealed class ModernList : ListView
{
    private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    private readonly ImageList _rowHeight = new();
    private bool _sizing, _fitQueued;

    public ModernList()
    {
        View = View.Details;
        FullRowSelect = true;
        OwnerDraw = true;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Font = Theme.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Margin = Padding.Empty;
        // the only way to make a ListView's rows taller (28, not 30,: five devices, a trainer found by ANT+ and by
        // Bluetooth among them, show without a scroll bar at the size the window opens at)
        _rowHeight.ImageSize = new Size(1, Theme.S(28));
        SmallImageList = _rowHeight;
        Theme.DarkScrollBars(this);
    }

    /// <summary>A status light at the start of a cell: (column, the cell's text) gives its colour, or null for none.</summary>
    public Func<int, string, Color?>? CellLight { get; set; }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        var r = Rectangle.FromLTRB(e.Bounds.Left + Theme.S(8), e.Bounds.Top, e.Bounds.Right - Theme.S(4), e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, (e.Header?.Text ?? "").ToUpperInvariant(), Theme.Caption, r, Theme.Muted, TextFlags);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) { /* every cell is painted in OnDrawSubItem */ }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) return;
        var g = e.Graphics;
        // the first cell reports the whole row as its bounds
        var cell = e.ColumnIndex == 0 && Columns.Count > 0 ? new Rectangle(e.Bounds.X, e.Bounds.Y, Columns[0].Width, e.Bounds.Height) : e.Bounds;
        using (var b = new SolidBrush(e.Item.Selected ? Theme.AccentWash : Theme.Surface)) g.FillRectangle(b, cell);
        int x = cell.Left + Theme.S(8);
        if (CellLight?.Invoke(e.ColumnIndex, e.SubItem.Text) is { } light)
        {
            int d = Theme.LightSize;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(light)) g.FillEllipse(b, x, cell.Top + (cell.Height - d) / 2, d, d);
            g.SmoothingMode = old;
            x += d + Theme.S(7);
        }
        TextRenderer.DrawText(g, e.SubItem.Text, Font, Rectangle.FromLTRB(x, cell.Top, cell.Right - Theme.S(4), cell.Bottom),
            e.ColumnIndex == 0 ? Theme.Text : Theme.TextSecondary, TextFlags);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _fitQueued = false; FillLastColumn(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); FillLastColumn(); FillLastColumnLater(); }

    /// <summary>
    /// A list made narrower keeps a sideways scroll bar it no longer needs: Windows works its scroll bars out before the fit
    /// above and does not look again. So fit once more when the resize is over, setting the width afresh to make it look.
    /// </summary>
    private void FillLastColumnLater()
    {
        if (_fitQueued || !IsHandleCreated) return;
        _fitQueued = true;
        BeginInvoke(new MethodInvoker(() => { _fitQueued = false; FillLastColumn(afresh: true); }));
    }

    private void FillLastColumn(bool afresh = false)
    {
        if (_sizing || Columns.Count == 0) return;
        _sizing = true;
        try
        {
            int others = 0;
            for (int i = 0; i < Columns.Count - 1; i++) others += Columns[i].Width;
            int w = Math.Max(Theme.S(120), ClientSize.Width - others);
            if (afresh && Columns[^1].Width == w) Columns[^1].Width = w - 1;
            if (Columns[^1].Width != w) Columns[^1].Width = w;
        }
        finally { _sizing = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rowHeight.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The tab strip in the window's colours: flat text tabs, an accent line under the open one. Still a <see cref="TabControl"/>, so the tabs
/// are real tab items for the keyboard (arrow keys, Ctrl+Tab) and for UI Automation (SelectionItem).
/// </summary>
public sealed class ModernTabs : TabControl
{
    private int _hot = -1;

    public ModernTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        Font = Theme.BodyBold;
        ItemSize = new Size(Theme.S(116), Theme.BodyBold.Height + Theme.S(24));
        Margin = System.Windows.Forms.Padding.Empty;   // (TabControl.Padding is a Point)
    }

    /// <summary>All tabs share the strip: equal widths that fit the window, within sensible limits. Pure (`selftest`).</summary>
    public static int TabWidth(int clientWidth, int tabs, int min, int max) => tabs <= 0 ? max : Math.Clamp((clientWidth - 8) / tabs, min, max);

    /// <summary>The page area: everything under the strip, edge to edge (the stock control keeps a border around it).</summary>
    public override Rectangle DisplayRectangle
    {
        get
        {
            var r = base.DisplayRectangle;
            return TabCount == 0 ? r : Rectangle.FromLTRB(0, r.Top, Width, Math.Max(r.Top, Height));
        }
    }

    private void FitTabs()
    {
        if (TabCount == 0) return;
        int w = TabWidth(ClientSize.Width, TabCount, Theme.S(84), Theme.S(150));
        if (ItemSize.Width != w) ItemSize = new Size(w, ItemSize.Height);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); FitTabs(); }
    protected override void OnResize(EventArgs e) { FitTabs(); base.OnResize(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hot != -1) { _hot = -1; Invalidate(); } }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = -1;
        for (int i = 0; i < TabCount; i++) if (GetTabRect(i).Contains(e.Location)) { hot = i; break; }
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Page);
        if (TabCount == 0) return;
        int bottom = DisplayRectangle.Top;
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, bottom - 1, Width, bottom - 1);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool selected = i == SelectedIndex;
            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, selected ? Theme.Text : i == _hot ? Theme.TextSecondary : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!selected) continue;
            int bar = Theme.S(3);
            Theme.FillRounded(g, new Rectangle(r.Left + Theme.S(14), bottom - bar - 1, Math.Max(2, r.Width - Theme.S(28)), bar + 1), bar, Theme.Accent);
            if (Focused && (ShowFocusCues || Ui.PadInUse)) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -Theme.S(6), -Theme.S(8)), Theme.Text, Theme.Page);
        }
    }
}
