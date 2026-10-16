#nullable disable

using System;
using System.Reflection;

// Pure logic (no Unity, no logging): also compiled into the bridge (bridge/DescendersBridge.csproj links this file) so `selftest` checks it
// (tests\unit\FieldBlendTests.cs).
namespace FreeTurboDescenders
{
    /// <summary>
    /// A blend of two values of the same struct, field by field: every float (and so every Unity Color / Vector, which are floats inside)
    /// is mixed, everything else (whole numbers, switches, choices, objects such as curves) is the first value's up to half way (unless a Mixer knows how to mix it) and the
    /// second's after. Used for the game's post-processing settings at dusk (DayCycle.cs), whose struct types are the game's own.
    /// </summary>
    internal static class FieldBlend
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Mixes two objects of a kind the caller knows (the game's colour-grading curves), or returns null when it cannot.</summary>
        public delegate object Mixer(object a, object b, float t);

        /// <summary><paramref name="a"/> at 0, <paramref name="b"/> at 1, kept within 0 .. 1. Values of different types: whichever is nearer.</summary>
        public static object Lerp(object a, object b, float t) { return Lerp(a, b, t, null); }

        /// <summary>As Lerp, and objects (not values) are given to <paramref name="mix"/> first, at any depth; the nearer one when it cannot mix them.</summary>
        public static object Lerp(object a, object b, float t, Mixer mix)
        {
            if (float.IsNaN(t)) t = 0f;
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            if (a == null || b == null || !object.ReferenceEquals(a.GetType(), b.GetType())) return t < 0.5f ? a : b;
            if (a is float) { float x = (float)a, y = (float)b; return x + (y - x) * t; }
            if (a is double) { double x = (double)a, y = (double)b; return x + (y - x) * t; }
            Type type = a.GetType();
            if (!type.IsValueType && mix != null && !object.ReferenceEquals(a, b))
            {
                object mixed = mix(a, b, t);
                if (mixed != null) return mixed;
            }
            if (type.IsPrimitive || type.IsEnum || !type.IsValueType) return t < 0.5f ? a : b;
            object result = Activator.CreateInstance(type);
            foreach (FieldInfo f in type.GetFields(Fields))
                f.SetValue(result, Lerp(f.GetValue(a), f.GetValue(b), t, mix));
            return result;
        }

        /// <summary>The fields (dotted paths under <paramref name="prefix"/>) that differ and cannot be blended, "path: a -> b", into <paramref name="into"/>;
        /// returns how many blendable floats differ.</summary>
        public static int Differences(object a, object b, string prefix, System.Collections.Generic.List<string> into)
        {
            if (Same(a, b)) return 0;
            if (a == null || b == null || !object.ReferenceEquals(a.GetType(), b.GetType())) { into.Add(prefix + ": " + Show(a) + " -> " + Show(b)); return 0; }
            if (a is float || a is double) return 1;
            Type type = a.GetType();
            if (type.IsPrimitive || type.IsEnum || !type.IsValueType) { into.Add(prefix + ": " + Show(a) + " -> " + Show(b)); return 0; }
            int floats = 0;
            foreach (FieldInfo f in type.GetFields(Fields))
                floats += Differences(f.GetValue(a), f.GetValue(b), prefix.Length == 0 ? f.Name : prefix + "." + f.Name, into);
            return floats;
        }

        private static string Show(object o) { return o == null ? "null" : o.GetType().IsValueType ? o.ToString() : o.GetType().Name + "#" + o.GetHashCode(); }

        /// <summary>Whether two values are the same all the way down (floats exactly, objects by reference or Equals).</summary>
        public static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (!object.ReferenceEquals(a.GetType(), b.GetType())) return false;
            Type type = a.GetType();
            if (type.IsPrimitive || type.IsEnum || !type.IsValueType) return object.ReferenceEquals(a, b) || a.Equals(b);
            foreach (FieldInfo f in type.GetFields(Fields))
                if (!Same(f.GetValue(a), f.GetValue(b))) return false;
            return true;
        }
    }
}
