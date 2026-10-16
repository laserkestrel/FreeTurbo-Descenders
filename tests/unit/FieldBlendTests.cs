using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// The night blend (src\FieldBlend.cs, used by src\DayCycle.cs on the game's post-processing settings): two values of the same
/// struct mixed field by field, floats blended, everything else the nearer one's. Test structs shaped like the game's (a colour inside a
/// settings struct inside another, whole numbers, a switch, a choice, a curve object).
/// </summary>
public static class FieldBlendTests
{
    private enum Mode { Off, Neutral, Filmic }
    private struct Colour { public float r, g, b, a; }
    private sealed class Curve { }
    private struct Basic { public float postExposure, saturation; public Colour tint; }
    private struct Settings { public Basic basic; public int logMin; public bool dynamicKeyValue; public Mode tonemapper; public Curve curve; private float hidden; public float Hidden { get { return hidden; } set { hidden = value; } } }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("FieldBlend: the game's post-processing settings mixed field by field at dusk");
        var curveA = new Curve(); var curveB = new Curve();
        var day = new Settings { basic = new Basic { postExposure = 0f, saturation = 1f, tint = new Colour { r = 1f, g = 1f, b = 1f, a = 1f } }, logMin = -8, dynamicKeyValue = false, tonemapper = Mode.Neutral, curve = curveA, Hidden = 2f };
        var night = new Settings { basic = new Basic { postExposure = -2f, saturation = 0.5f, tint = new Colour { r = 0.2f, g = 0.4f, b = 1f, a = 1f } }, logMin = -4, dynamicKeyValue = true, tonemapper = Mode.Filmic, curve = curveB, Hidden = 4f };

        var q = (Settings)FieldBlend.Lerp(day, night, 0.25f);
        check(Near(q.basic.postExposure, -0.5f) && Near(q.basic.saturation, 0.875f), "a quarter of the way: floats a quarter of the way, inside a nested struct");
        check(Near(q.basic.tint.r, 0.8f) && Near(q.basic.tint.g, 0.85f) && q.basic.tint.b == 1f, "a colour (floats inside) blends too");
        check(Near(q.Hidden, 2.5f), "private fields blend as well (the game's are obfuscated, often not public)");
        check(q.logMin == -8 && !q.dynamicKeyValue && q.tonemapper == Mode.Neutral && ReferenceEquals(q.curve, curveA), "whole numbers, switches, choices and objects: the first one's up to half way");
        var h = (Settings)FieldBlend.Lerp(day, night, 0.75f);
        check(h.logMin == -4 && h.dynamicKeyValue && h.tonemapper == Mode.Filmic && ReferenceEquals(h.curve, curveB), "... and the second one's after");
        check(Near(((Settings)FieldBlend.Lerp(day, night, 0f)).basic.postExposure, 0f) && Near(((Settings)FieldBlend.Lerp(day, night, 1f)).basic.postExposure, -2f), "0 is the first, 1 the second");
        check(Near(((Settings)FieldBlend.Lerp(day, night, -3f)).basic.postExposure, 0f) && Near(((Settings)FieldBlend.Lerp(day, night, 7f)).basic.postExposure, -2f)
              && Near(((Settings)FieldBlend.Lerp(day, night, float.NaN)).basic.postExposure, 0f), "kept within 0 .. 1; nonsense is the first");
        check(day.basic.postExposure == 0f && night.basic.postExposure == -2f, "neither input is changed");
        check(Near((float)FieldBlend.Lerp(1f, 3f, 0.5f), 2f) && (int)FieldBlend.Lerp(1, 3, 0.4f) == 1 && Equals(FieldBlend.Lerp("a", 5, 0.6f), 5), "plain values; different types: the nearer one");

        // the game's colour-grading curves are objects: a mixer makes a new one from both (DayCycle.MixCurve), at any depth
        int mixed = 0;
        FieldBlend.Mixer mix = (x, y, w) => { if (!(x is Curve)) return null; mixed++; return new Curve(); };
        var m = (Settings)FieldBlend.Lerp(day, night, 0.25f, mix);
        check(mixed == 1 && m.curve != null && !ReferenceEquals(m.curve, curveA) && !ReferenceEquals(m.curve, curveB), "an object the mixer knows: a new mixed one, also a quarter of the way");
        check(Near(m.basic.postExposure, -0.5f) && m.tonemapper == Mode.Neutral, "the mixer changes nothing else");
        mixed = 0;
        var same = night; same.curve = curveA;
        FieldBlend.Lerp(day, same, 0.5f, mix);
        check(mixed == 0, "the very same object on both sides is not mixed");
        check(ReferenceEquals(((Settings)FieldBlend.Lerp(day, night, 0.75f, (x, y, w) => null)).curve, curveB), "a mixer that cannot: the nearer one, as without one");
        check(ReferenceEquals(((Settings)FieldBlend.Lerp(day, night, 0f, mix)).curve, curveA) && ReferenceEquals(((Settings)FieldBlend.Lerp(day, night, 1f, mix)).curve, curveB), "at 0 and 1 the real objects, nothing made");
        var hard = new List<string>();
        check(FieldBlend.Differences(day, night, "", hard) == 5 && hard.Count == 4 && hard.Exists(s => s.StartsWith("tonemapper: Neutral -> Filmic")) && hard.Exists(s => s.StartsWith("curve: Curve#")),
            "the log names what cannot be blended (got " + string.Join("; ", hard) + ")");

        check(FieldBlend.Same(day, day) && !FieldBlend.Same(day, night), "the same settings are the same, different ones are not");
        var almost = day; almost.basic.tint.g = 0.999f;
        check(!FieldBlend.Same(day, almost), "one float deep inside differs: not the same");
        var other = day; other.curve = new Curve();
        check(!FieldBlend.Same(day, other), "a different curve object: not the same");
        check(FieldBlend.Same(null, null) && !FieldBlend.Same(day, null) && !FieldBlend.Same(1, 1f), "nothing, and different types");
    }

    private static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-5f; }
}
