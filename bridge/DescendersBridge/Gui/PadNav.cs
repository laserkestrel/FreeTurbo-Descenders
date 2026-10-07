namespace DescendersBridge.Gui;

// The pure rules of driving the bridge window with the Xbox pad: no window code and no XInput here, so `selftest` covers
// them (tests\unit\PadNavTests.cs). The pad itself is read in PadInput.cs; the window acts on what PadReader gives it (BridgeWindow.PadStep).

/// <summary>The pad buttons the window reads, with XInput's own bit values (the sticks, triggers and the other buttons are not read).</summary>
[Flags]
public enum PadButtons
{
    None = 0,
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    LB = 0x0100, RB = 0x0200,
    A = 0x1000, B = 0x2000,
}

/// <summary>What one press asks of the window.</summary>
public enum PadAction { TabPrevious, TabNext, Up, Down, Left, Right, Select, Back }

/// <summary>What a pad press does to a question box ("are you sure?").</summary>
public enum QuestionStep { None, FocusYes, FocusNo, PressFocused, No }

public static class PadRules
{
    /// <summary>A held D-pad direction repeats after this long, then at this pace. LB / RB / A / B never repeat.</summary>
    public const double RepeatDelaySeconds = 0.4, RepeatEverySeconds = 0.15;

    /// <summary>The line under a Workouts card when the pad was the last thing used (else <see cref="NavRules.StartHint"/>).</summary>
    public const string StartHint = "A to start";

    public const PadButtons Dpad = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;
    public const PadButtons Read = Dpad | PadButtons.LB | PadButtons.RB | PadButtons.A | PadButtons.B;

    /// <summary>The button -> action table: LB / RB the tabs, the D-pad the arrow keys, A = Enter, B = back.</summary>
    public static readonly IReadOnlyList<(PadButtons Button, PadAction Action)> Table = new[]
    {
        (PadButtons.LB, PadAction.TabPrevious), (PadButtons.RB, PadAction.TabNext),
        (PadButtons.Up, PadAction.Up), (PadButtons.Down, PadAction.Down), (PadButtons.Left, PadAction.Left), (PadButtons.Right, PadAction.Right),
        (PadButtons.A, PadAction.Select), (PadButtons.B, PadAction.Back),
    };

    /// <summary>XInput's wButtons, cut down to the buttons the window reads.</summary>
    public static PadButtons FromXInput(ushort buttons) => (PadButtons)buttons & Read;

    /// <summary>
    /// XInput has no focus, every program sees every press: the window acts on the pad only while it (or one of its own question boxes)
    /// is the foreground window AND no game is connected. So while Descenders runs nothing in the window moves, whichever is in front.
    /// </summary>
    public static bool Listen(bool foreground, bool gameConnected) => foreground && !gameConnected;

    /// <summary>The window asks for the foreground when the game goes from connected to not connected, and only then.</summary>
    public static bool BringToFront(bool wasConnected, bool connected) => wasConnected && !connected;

    /// <summary>The tab LB (<paramref name="delta"/> -1) / RB (+1) goes to from <paramref name="index"/>, wrapping at the ends.</summary>
    public static int NextTab(int index, int count, int delta) => count <= 0 ? index : (((index + delta) % count) + count) % count;

    /// <summary>The arrow key / Enter a pad action stands for; null for the ones that are not a key (LB / RB / B).</summary>
    public static NavCommand? Command(PadAction action) => action switch
    {
        PadAction.Up => NavCommand.Up,
        PadAction.Down => NavCommand.Down,
        PadAction.Left => NavCommand.Previous,
        PadAction.Right => NavCommand.Next,
        PadAction.Select => NavCommand.Activate,
        _ => null,
    };

    /// <summary>A question box: D-pad left / right moves between Yes (left) and No (right), A presses the focused one, B is "No".</summary>
    public static QuestionStep Question(PadAction action) => action switch
    {
        PadAction.Left => QuestionStep.FocusYes,
        PadAction.Right => QuestionStep.FocusNo,
        PadAction.Select => QuestionStep.PressFocused,
        PadAction.Back => QuestionStep.No,
        _ => QuestionStep.None,
    };
}

/// <summary>
/// Turns the pad's state, polled every few milliseconds, into actions: a press acts once, on the button going down; a held D-pad
/// direction repeats (<see cref="PadRules.RepeatDelaySeconds"/>, then every <see cref="PadRules.RepeatEverySeconds"/>); a button already
/// held when listening starts must be let go before it counts (no stray A carried over from the game).
/// </summary>
public sealed class PadReader
{
    private static readonly PadAction[] Nothing = Array.Empty<PadAction>();

    private bool _listening;
    private PadButtons _blocked, _down, _repeat;
    private double _repeatAt;

    /// <param name="held">the buttons down now (none when no pad is connected).</param>
    /// <param name="listen"><see cref="PadRules.Listen"/>.</param>
    /// <param name="t">a clock, in seconds.</param>
    public IReadOnlyList<PadAction> Step(PadButtons held, bool listen, double t)
    {
        if (!listen) { _listening = false; return Nothing; }
        held &= PadRules.Read;
        if (!_listening)
        {
            _listening = true;
            _blocked = held;
            _down = _repeat = PadButtons.None;
        }
        _blocked &= held;
        PadButtons now = held & ~_blocked, pressed = now & ~_down;
        _down = now;

        List<PadAction>? actions = null;
        foreach (var (button, action) in PadRules.Table)
        {
            if ((pressed & button) == 0) continue;
            (actions ??= new List<PadAction>()).Add(action);
            if ((button & PadRules.Dpad) == 0) continue;
            _repeat = button;
            _repeatAt = t + PadRules.RepeatDelaySeconds;
        }
        if ((now & _repeat) == 0) _repeat = PadButtons.None;
        else if ((pressed & _repeat) == 0 && t >= _repeatAt)
        {
            foreach (var (button, action) in PadRules.Table)
                if (button == _repeat) (actions ??= new List<PadAction>()).Add(action);
            _repeatAt = t + PadRules.RepeatEverySeconds;
        }
        return actions ?? (IReadOnlyList<PadAction>)Nothing;
    }
}
