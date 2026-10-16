using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// With no ANT+ stick plugged in, the window's Logs tab showed the whole exception (type names, the inner exception, two stack
/// traces). The rider reads that tab, so no stick is now one plain ERROR line; every other failure keeps its detail for a bug report.
/// Run by `FreeTurbo-Bridge selftest`.
/// </summary>
public static class NoStickLogTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("No ANT+ stick: one plain line in the log, no stack trace");

        // as Ride.RunAsync throws it: the library's exception inside, both thrown so they carry a stack trace
        const string library = "ANTLibrary Exception: No ANT devices detected: ensure an ANT device is connected to your system and try again";
        Exception noStick = Thrown(() => throw new AntStickException("ANT+ stick not available: " + library, Thrown(() => throw new InvalidOperationException(library))));
        string line = FriendlyText.LogText(noStick);
        check(noStick.ToString().Contains("   at ") && noStick.ToString().Contains("--->"), "the test's exception has a stack trace and an inner exception, as in the issue");
        check(line == FriendlyText.NoStickLog, "no stick plugged in: the log line is the plain sentence and nothing else (" + line + ")");
        check(!line.Contains('\n') && !line.Contains("   at ") && !line.Contains("Exception") && !line.Contains("DescendersBridge") && !line.Contains("--->"),
            "one line: no stack trace, no exception type, no inner exception");
        check(line.Contains("ANT+ USB stick") && line.Contains("plug one in"), "it says what is missing and what to do");

        check(FriendlyText.LogText(new Exception("outer", noStick)) == FriendlyText.NoStickLog, "a stick exception wrapped in another one is found too");
        check(FriendlyText.LogText(new AntStickException("ANT+ stick not available: test", null)) == FriendlyText.NoStickLog, "no inner exception: still the plain sentence");

        string busy = FriendlyText.LogText(Thrown(() => throw new AntStickException("ANT+ stick not available: x", new InvalidOperationException("ANTLibrary Exception: Unable to open USB device\r\nsecond line"))));
        check(busy == FriendlyText.NoStickLog + " (Unable to open USB device)", "another reason from the stick is kept, short, on the same line (" + busy + ")");

        Exception other = Thrown(() => throw new NullReferenceException("Object reference not set"));
        check(FriendlyText.LogText(other) == other.ToString() && FriendlyText.LogText(other).Contains("   at "), "any other failure keeps its full detail and stack trace");
    }

    private static Exception Thrown(Action a)
    {
        try { a(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("the action did not throw");
    }
}
