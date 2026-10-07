using DescendersBridge.Gui;

namespace DescendersBridge;

/// <summary>
/// The bridge window is driven with the Xbox pad. The pure rules (<see cref="PadRules"/>, <see cref="PadReader"/>): a press
/// acts once, the D-pad repeats while held, the button -> action table, when the pad is listened to (the window in front and no game
/// connected; a button held when listening starts must be let go first), when the window asks for the foreground, the wrap of the tabs,
/// the question box, the hint line. The window itself, fed made-up pad readings, is in WindowDesignTests.
/// Run by `turbotrainer-bridge selftest`.
/// </summary>
public static class PadNavTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Xbox pad in the bridge window: presses, D-pad repeat, when it is listened to");
        const PadButtons None = PadButtons.None, A = PadButtons.A, B = PadButtons.B, LB = PadButtons.LB, RB = PadButtons.RB,
            Up = PadButtons.Up, Down = PadButtons.Down, Left = PadButtons.Left, Right = PadButtons.Right;
        static string Show(IEnumerable<PadAction> actions) => string.Join(",", actions);

        // ---- the buttons ----
        check(PadRules.FromXInput(0x1000) == A && PadRules.FromXInput(0x2000) == B && PadRules.FromXInput(0x0100) == LB && PadRules.FromXInput(0x0200) == RB
            && PadRules.FromXInput(0x0001) == Up && PadRules.FromXInput(0x0002) == Down && PadRules.FromXInput(0x0004) == Left && PadRules.FromXInput(0x0008) == Right,
            "XInput's button bits: A, B, LB, RB and the four D-pad directions");
        check(PadRules.FromXInput(0x4000 | 0x8000 | 0x0010 | 0x0020 | 0x0040 | 0x0080) == None && PadRules.FromXInput(0xFFFF) == PadRules.Read,
            "X, Y, Start, Back and the stick clicks are not read");
        var table = PadRules.Table.ToDictionary(e => e.Button, e => e.Action);
        check(table.Count == 8 && table[LB] == PadAction.TabPrevious && table[RB] == PadAction.TabNext && table[Up] == PadAction.Up && table[Down] == PadAction.Down
            && table[Left] == PadAction.Left && table[Right] == PadAction.Right && table[A] == PadAction.Select && table[B] == PadAction.Back,
            "the table: LB / RB = previous / next tab, the D-pad = up / down / left / right, A = select, B = back");
        check(PadRules.Command(PadAction.Up) == NavCommand.Up && PadRules.Command(PadAction.Down) == NavCommand.Down && PadRules.Command(PadAction.Left) == NavCommand.Previous
            && PadRules.Command(PadAction.Right) == NavCommand.Next && PadRules.Command(PadAction.Select) == NavCommand.Activate
            && PadRules.Command(PadAction.Back) is null && PadRules.Command(PadAction.TabNext) is null && PadRules.Command(PadAction.TabPrevious) is null,
            "the D-pad is the arrow keys and A is Enter (the same commands the keys give); LB / RB / B are no key");

        // ---- a press acts once ----
        var pad = new PadReader();
        check(pad.Step(None, true, 0).Count == 0, "nothing held: nothing happens");
        check(Show(pad.Step(A, true, 0.03)) == "Select", "A going down: select, once");
        int extra = 0;
        for (double t = 0.06; t < 3; t += 0.03) extra += pad.Step(A, true, t).Count;
        check(extra == 0, "A held for 3 s never repeats");
        check(pad.Step(None, true, 3.1).Count == 0 && Show(pad.Step(A, true, 3.2)) == "Select", "let go and pressed again: select again");
        pad = new PadReader();
        pad.Step(None, true, 0);
        int held = 0;
        foreach (var button in new[] { LB, RB, B })
        {
            held += pad.Step(button, true, 10).Count;
            for (double t = 10.03; t < 12; t += 0.03) held += pad.Step(button, true, t).Count;
            pad.Step(None, true, 12);
        }
        check(held == 3, $"LB, RB and B act once each and never repeat while held (got {held} actions)");
        pad = new PadReader();
        pad.Step(None, true, 0);
        check(Show(pad.Step(RB | A, true, 0.1)) == "TabNext,Select", "two buttons going down in the same reading both act");

        // ---- the D-pad repeats while held ----
        pad = new PadReader();
        pad.Step(None, true, 0);
        var times = new List<double>();
        for (int i = 1; i <= 100; i++)   // held for 1 s, read every 10 ms
        {
            double t = i * 0.01;
            foreach (var action in pad.Step(Right, true, t)) if (action == PadAction.Right) times.Add(t);
        }
        check(times.Count == 5 && Math.Abs(times[0] - 0.01) < 1e-9 && Math.Abs(times[1] - times[0] - PadRules.RepeatDelaySeconds) < 0.011
            && Enumerable.Range(2, 3).All(i => Math.Abs(times[i] - times[i - 1] - PadRules.RepeatEverySeconds) < 0.011),
            $"D-pad right held for 1 s: at once, again after about 400 ms, then every 150 ms (at {string.Join(", ", times.Select(t => t.ToString("0.00")))})");
        check(pad.Step(None, true, 1.02).Count == 0 && pad.Step(None, true, 2).Count == 0, "let go: the repeat stops");
        check(Show(pad.Step(Down, true, 2.1)) == "Down" && pad.Step(Down, true, 2.3).Count == 0 && Show(pad.Step(Down | Left, true, 2.35)) == "Left"
            && pad.Step(Down | Left, true, 2.6).Count == 0 && Show(pad.Step(Down | Left, true, 2.76)) == "Left",
            "a second direction pressed while one is held takes the repeat over, with its own 400 ms");
        check(PadRules.RepeatDelaySeconds == 0.4 && PadRules.RepeatEverySeconds == 0.15, "the repeat: 400 ms, then every 150 ms");

        // ---- when the pad is listened to ----
        check(PadRules.Listen(foreground: true, gameConnected: false) && !PadRules.Listen(true, true) && !PadRules.Listen(false, false) && !PadRules.Listen(false, true),
            "the pad is listened to only while the window is in front AND no game is connected");
        pad = new PadReader();
        int ignored = 0;
        foreach (var button in new[] { A, B, LB, RB, Up, Down, Left, Right })
        {
            ignored += pad.Step(button, false, 0).Count;
            for (double t = 0.03; t < 2; t += 0.03) ignored += pad.Step(button, false, t).Count;
            ignored += pad.Step(None, false, 2).Count;
        }
        check(ignored == 0, "not listened to (a game is connected, or another window is in front): no press and no held D-pad does anything");
        // riding: A is held in the game as it quits and the bridge starts listening
        pad = new PadReader();
        pad.Step(A, false, 0);
        int stray = pad.Step(A, true, 1).Count + pad.Step(A, true, 1.5).Count + pad.Step(A | Down, true, 1.6).Count(a => a == PadAction.Select);
        check(stray == 0, "a button already held when listening starts does not count while it stays held");
        check(pad.Step(None, true, 2).Count == 0 && Show(pad.Step(A, true, 2.1)) == "Select", "once it was let go, the next press counts");
        pad = new PadReader();
        check(pad.Step(Down, true, 0).Count == 0 && pad.Step(Down, true, 1).Count == 0 && pad.Step(Down, true, 2).Count == 0,
            "a D-pad direction held when listening starts does not move anything, and does not start repeating");
        check(Show(pad.Step(Down | RB, true, 2.1)) == "TabNext", "another button pressed meanwhile counts");
        pad = new PadReader();
        pad.Step(None, true, 0);
        pad.Step(Right, true, 0.1);
        pad.Step(Right, false, 0.2);   // the game connects while the D-pad is held
        check(pad.Step(Right, true, 5).Count == 0 && pad.Step(Right, true, 6).Count == 0,
            "listening stops and starts again with the same button still held: it has to be let go first");

        // ---- back to the window when the game goes ----
        check(PadRules.BringToFront(wasConnected: true, connected: false) && !PadRules.BringToFront(false, false) && !PadRules.BringToFront(false, true) && !PadRules.BringToFront(true, true),
            "the window asks for the foreground when the game goes from connected to not connected, and only then");

        // ---- tabs, the question box, the hint ----
        check(PadRules.NextTab(0, 6, 1) == 1 && PadRules.NextTab(5, 6, 1) == 0 && PadRules.NextTab(0, 6, -1) == 5 && PadRules.NextTab(3, 6, -1) == 2
            && PadRules.NextTab(0, 0, 1) == 0 && PadRules.NextTab(0, 1, 1) == 0, "LB / RB step one tab and wrap at both ends");
        check(PadRules.Question(PadAction.Left) == QuestionStep.FocusYes && PadRules.Question(PadAction.Right) == QuestionStep.FocusNo
            && PadRules.Question(PadAction.Select) == QuestionStep.PressFocused && PadRules.Question(PadAction.Back) == QuestionStep.No
            && new[] { PadAction.Up, PadAction.Down, PadAction.TabPrevious, PadAction.TabNext }.All(a => PadRules.Question(a) == QuestionStep.None),
            "a question box: D-pad left / right moves between Yes and No, A presses the focused one, B is No, nothing else does anything");
        check(NavRules.Hint(true, true, pad: true) == "A to start" && NavRules.Hint(true, true, pad: false) == "Enter or click to start"
            && NavRules.Hint(false, true, pad: true) == "" && NavRules.Hint(true, false, pad: true) == "",
            "the hint line says \"A to start\" when the pad was the last thing used, \"Enter or click to start\" otherwise");
    }
}
