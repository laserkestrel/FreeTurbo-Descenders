using System.Reflection;
using FreeTurboDescenders;

namespace DescendersBridge;

/// <summary>
/// src\StartGate.cs reads State_PreGame.OnUpdate's IL to confirm it calls the "any button pressed" getter it patches;
/// src\IlReader.cs decodes the calls in order. Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class IlReaderTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("IlReader: call order from real IL, switch operands, undecodable bytes");
        var sample = typeof(IlReaderTests).GetMethod(nameof(IlSample), BindingFlags.NonPublic | BindingFlags.Static)!;
        var names = IlReader.Calls(sample).Select(c => c == null ? "?" : c.DeclaringType!.Name + "." + c.Name).ToList();
        string got = string.Join(", ", names);
        int iWrite = names.IndexOf("Console.Write"), iAbs = names.IndexOf("Math.Abs"), iCtor = names.IndexOf("String..ctor"), iLen = names.IndexOf("String.get_Length");
        check(iWrite >= 0 && iAbs > iWrite && iCtor > iAbs && iLen > iCtor, $"Write, Abs, new string, Length in IL order across a switch (got: {got})");

        // nop; call 0x0A000011; ldc.i4.s 7; ceq (0xFE 0x01); callvirt 0x06000022; ret
        byte[] il = { 0x00, 0x28, 0x11, 0x00, 0x00, 0x0A, 0x1F, 0x07, 0xFE, 0x01, 0x6F, 0x22, 0x00, 0x00, 0x06, 0x2A };
        var toks = IlReader.CallTokens(il);
        check(toks.Count == 2 && toks[0] == 0x0A000011 && toks[1] == 0x06000022, "call + callvirt tokens decoded past a 1-byte operand and a 2-byte opcode");
        check(IlReader.CallTokens(new byte[] { 0x28, 0x11, 0x00 }).Count == 0, "truncated operand: no token, no exception");
        check(IlReader.CallTokens(new byte[] { 0x28, 0x11, 0x00, 0x00, 0x0A, 0xA6, 0x28, 0x22, 0x00, 0x00, 0x0A }).Count == 1, "stops at an undefined opcode (0xA6)");
        check(IlReader.CallTokens(null!).Count == 0, "null body: empty");

        // src\DayCycle.cs finds the game's obfuscated session -> level -> night setting fields from the fields UpdateCameraEffects reads
        var chain = typeof(IlReaderTests).GetMethod(nameof(FieldSample), BindingFlags.NonPublic | BindingFlags.Static)!;
        var reads = IlReader.FieldLoads(chain).Select(f => f == null ? "?" : f.DeclaringType!.Name + "." + f.Name).ToList();
        check(string.Join(", ", reads) == "SampleSession.Current, SampleOuter.Level, SampleLevel.Kind, SampleOuter.Count",
            "field reads in IL order, a static one and a chain of instance ones (got: " + string.Join(", ", reads) + ")");
        // ldarg.0; ldfld 0x04000005; ldsfld 0x04000006; stfld 0x04000007; ret
        byte[] fl = { 0x02, 0x7B, 0x05, 0x00, 0x00, 0x04, 0x7E, 0x06, 0x00, 0x00, 0x04, 0x7D, 0x07, 0x00, 0x00, 0x04, 0x2A };
        var ftoks = IlReader.FieldLoadTokens(fl);
        check(ftoks.Count == 2 && ftoks[0] == 0x04000005 && ftoks[1] == 0x04000006, "ldfld + ldsfld tokens, not the stfld");
        check(IlReader.CallTokens(fl).Count == 0 && IlReader.FieldLoadTokens(il).Count == 0, "calls and field reads kept apart");
    }

    private static int IlSample(int x)
    {
        if (x > 3) Console.Write("");
        int s = Math.Abs(x);
        switch (x) { case 0: s += 2; break; case 1: s += 5; break; case 2: s += 7; break; case 3: s += 11; break; }
        return s + new string('a', x & 1).Length;
    }

    private sealed class SampleLevel { public int Kind = 2; }
    private sealed class SampleOuter { public SampleLevel Level = new SampleLevel(); public int Count = 1; }
    private static class SampleSession { public static SampleOuter Current = new SampleOuter(); }

    private static int FieldSample()
    {
        SampleOuter o = SampleSession.Current;
        return o.Level.Kind + o.Count;
    }}
