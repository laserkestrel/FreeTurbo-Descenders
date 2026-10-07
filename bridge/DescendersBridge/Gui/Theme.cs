using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DescendersBridge.Gui;

/// <summary>
/// The window's design system: one dark palette, one set of fonts, one spacing scale. Every control in <c>ModernControls.cs</c>
/// and the window itself take their colours and sizes from here, so the look is changed in one place.
/// Text wears the text colours; the status colours are for the small lights only (always next to words, never colour alone).
/// </summary>
public static class Theme
{
    // surfaces, darkest first: the window, a card on it, an input field on a card
    public static readonly Color Page = Hex(0x0F1115), Surface = Hex(0x181B21), Field = Hex(0x232831), FieldHover = Hex(0x2C323D), Border = Hex(0x2F3541);
    // text
    public static readonly Color Text = Hex(0xF2F4F7), TextSecondary = Hex(0xB9BFCA), Muted = Hex(0x8D94A1);
    // the one accent (primary buttons, the selected tab, the graphs, focus): teal #00A0B1, blue #3987E5 before it.
    // `window.accentColour` in the settings file changes it (SetAccent, called before the window builds its controls).
    private static AccentPalette _accent = AccentRules.For(AccentRules.Default, Page, Surface);
    public static Color Accent => _accent.Accent;
    public static Color AccentWash => _accent.Wash;
    // filled buttons carry white text, so they use deeper steps of the accent / the red (4.5:1 or better)
    public static Color AccentButton => _accent.Button;
    public static Color AccentButtonHover => _accent.ButtonHover;
    public static Color AccentButtonPressed => _accent.ButtonPressed;

    /// <summary>Use this accent colour ("#RRGGBB") for everything built or painted from now on; not a colour = the standard teal.</summary>
    public static void SetAccent(string? hex) =>
        _accent = AccentRules.For(AccentRules.TryParse(hex, out var c) ? c : AccentRules.Default, Page, Surface);

    public static readonly Color DangerButton = Hex(0xD03B3B), DangerButtonHover = Hex(0xBA3434), DangerButtonPressed = Hex(0xA32B2B);
    // status lights
    public static readonly Color Good = Hex(0x0CA30C), Warning = Hex(0xFAB219), Serious = Hex(0xEC835A), Critical = Hex(0xD03B3B);
    // the effort zones of a workout's graph, easy to hard: the palette's own grey, accent and status colours
    public static readonly Color ZoneEasy = Hex(0x6B7280);
    public static Color ZoneColor(EffortZone z) => z switch
    {
        EffortZone.Endurance => Accent,
        EffortZone.Tempo => Good,
        EffortZone.Threshold => Warning,
        EffortZone.Hard => Critical,
        _ => ZoneEasy,
    };
    // message bar backgrounds
    public static readonly Color ErrorWash = Hex(0x351B1E);
    public static Color InfoWash => _accent.InfoWash;

    public static readonly Font Body = Make(10f, false), BodyBold = Make(10f, true), Small = Make(9f, false), Caption = Make(8.5f, true), Picker = Make(12f, true),
        Heading = Make(12.5f, true), State = Make(14f, true), Metric = Make(34f, true), ButtonLarge = Make(11.5f, true), Mono = new("Consolas", 9f);

    private static readonly float Dpi = ScreenDpi();

    /// <summary>A size in pixels at 100 % scaling, grown for the screen's scaling (fonts grow by themselves).</summary>
    public static int S(int px) => (int)Math.Round(px * Dpi / 96f);

    /// <summary>The diameter of every status light in the window: one size, whatever font the words next to it wear.</summary>
    public static int LightSize => S(9);

    /// <summary>The screen's scaling: 1 = 100 %, 1.25 = 125 %.</summary>
    public static double Scale => Dpi / 96.0;

    public static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static Color HealthColor(Health h) => h switch
    {
        Health.Good => Good,
        Health.Weak => Warning,
        Health.Searching => Accent,
        Health.NotFound => Serious,
        Health.Lost => Critical,
        _ => Muted,
    };

    /// <summary>WCAG contrast ratio between two colours (1 = the same, 21 = black on white). Pure: `selftest` holds the palette to it.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        static double Luminance(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        static double Lin(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    }

    /// <summary>Segoe UI (Semibold for the bold weight where it is installed, else Segoe UI Bold).</summary>
    private static Font Make(float size, bool strong)
    {
        if (!strong) return new Font("Segoe UI", size);
        var semi = new Font("Segoe UI Semibold", size);
        if (semi.Name == "Segoe UI Semibold") return semi;
        semi.Dispose();
        return new Font("Segoe UI", size, FontStyle.Bold);
    }

    private static float ScreenDpi()
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiX; }
        catch { return 96f; }
    }

    /// <summary>The distance from the top of a line of text to its baseline, to sit a small unit next to a big number.</summary>
    public static int Ascent(Font f)
    {
        var fam = f.FontFamily;
        return (int)Math.Round(f.GetHeight(Dpi) * fam.GetCellAscent(f.Style) / fam.GetLineSpacing(f.Style));
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A filled rounded rectangle, with an optional 1 px border.</summary>
    public static void FillRounded(Graphics g, Rectangle r, int radius, Color fill, Color? border = null)
    {
        if (r.Width <= 1 || r.Height <= 1) return;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inner = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using var path = Rounded(inner, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
        if (border is { } c) { using var pen = new Pen(c); g.DrawPath(pen, path); }
        g.SmoothingMode = old;
    }

    // ---- Windows dark mode for the parts the system draws (title bar, scroll bars); harmless where it is not supported ----

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    public static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));   // 19 before Windows 10 2004
        }
        catch { /* older Windows: the title bar stays light */ }
    }

    public static void DarkScrollBars(Control c)
    {
        void Apply() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { /* older Windows: light scroll bars */ } }
        if (c.IsHandleCreated) Apply();
        c.HandleCreated += (_, _) => Apply();
    }
}

/// <summary>Every colour the window takes from its accent: the accent itself (the bar under the selected tab, focus, links, the graphs),
/// the two dark backgrounds tinted with it (a selected row, the information bar) and the three states of a filled button.</summary>
public sealed record AccentPalette(Color Accent, Color Wash, Color InfoWash, Color Button, Color ButtonHover, Color ButtonPressed);

/// <summary>
/// The accent is one colour, `window.accentColour` in TrainerBridgeConfig.json ("#RRGGBB", edited by hand), and the rest of the
/// accent's colours are worked out from it. Teal <see cref="DefaultHex"/> is the standard; the blue it replaced is <see cref="OldBlueHex"/>,
/// and writing that in the file gives exactly the window from before the issue. Pure.
/// </summary>
public static class AccentRules
{
    public const string DefaultHex = "#00A0B1";   // teal, RGB 0, 160, 177
    public const string OldBlueHex = "#3987E5";   // the blue earlier, RGB 57, 135, 229

    public static readonly Color Default = Color.FromArgb(255, 0x00, 0xA0, 0xB1), OldBlue = Color.FromArgb(255, 0x39, 0x87, 0xE5);

    // the blue's hand-picked steps, as they were earlier
    private static readonly AccentPalette OldBluePalette = new(OldBlue, Color.FromArgb(255, 0x1C, 0x2C, 0x45), Color.FromArgb(255, 0x15, 0x25, 0x3C),
        Color.FromArgb(255, 0x25, 0x6A, 0xBF), Color.FromArgb(255, 0x2A, 0x78, 0xD6), Color.FromArgb(255, 0x1C, 0x5C, 0xAB));

    /// <summary>"#00A0B1" or "00a0b1" (six hex digits, spaces around it allowed). False for anything else.</summary>
    public static bool TryParse(string? text, out Color c)
    {
        c = Default;
        string s = (text ?? "").Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) return false;
        int rgb = Convert.ToInt32(s, 16);
        c = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// The accent's colours on this page / card. A colour too dark to see on a card is lightened until it stands out 3:1; the filled
    /// buttons carry white text, so they are deeper steps of the accent (hover 4.45:1 against white or better, then two steps darker).
    /// </summary>
    public static AccentPalette For(Color accent, Color page, Color surface)
    {
        if (accent.ToArgb() == OldBlue.ToArgb()) return OldBluePalette;
        Color a = Color.FromArgb(255, accent.R, accent.G, accent.B);
        for (int i = 0; i < 40 && Theme.Contrast(a, surface) < 3.05; i++) a = Mix(a, Color.White, 0.1);
        Color hover = a;
        for (int pct = 99; pct >= 0 && Theme.Contrast(Color.White, hover) < 4.45; pct--) hover = Mix(Color.Black, a, pct / 100.0);
        return new AccentPalette(a, Mix(page, a, 0.23), Mix(page, a, 0.18), Mix(Color.Black, hover, 0.9), hover, Mix(Color.Black, hover, 0.8));
    }

    /// <summary><paramref name="from"/> with <paramref name="share"/> (0..1) of <paramref name="to"/> mixed in.</summary>
    private static Color Mix(Color from, Color to, double share)
    {
        static int Ch(int f, int t, double s) => (int)Math.Round(f + (t - f) * s);
        return Color.FromArgb(255, Ch(from.R, to.R, share), Ch(from.G, to.G, share), Ch(from.B, to.B, share));
    }
}

/// <summary>A control that says how big it wants to be for a given width, so the layout panels can stack and wrap without guessing.</summary>
public interface IMeasured
{
    /// <param name="width">the width on offer; 0 or less = as wide as it likes (one line).</param>
    Size Measure(int width);
}

/// <summary>Layout helpers shared by the panels in <c>ModernControls.cs</c>.</summary>
public static class Ui
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hidden = new();

    public static Size Measure(Control c, int width) => c is IMeasured m ? m.Measure(width) : c.Size;

    /// <summary>The Xbox pad was the last thing used: the focused button and tab show their focus mark, which Windows
    /// otherwise keeps hidden until a key is pressed.</summary>
    public static bool PadInUse { get; set; }

    /// <summary>
    /// Show or hide a control inside a layout panel. The panels cannot use <see cref="Control.Visible"/> to decide what takes space: it also
    /// reads false for everything on a tab that is not open.
    /// </summary>
    public static void SetVisible(Control c, bool visible)
    {
        if (visible) Hidden.Remove(c); else Hidden.AddOrUpdate(c, c);
        c.Visible = visible;
        Relayout(c);
    }

    public static bool IsHidden(Control c) => Hidden.TryGetValue(c, out _);

    /// <summary>A control's wanted size changed (new text, shown / hidden): lay out every layout panel above it again, outermost first.</summary>
    public static void Relayout(Control c)
    {
        var chain = new List<Control>();
        for (var p = c.Parent; p is not null; p = p.Parent) if (p is LayoutPanel) chain.Add(p);
        for (int i = chain.Count - 1; i >= 0; i--) chain[i].PerformLayout();
    }
}
