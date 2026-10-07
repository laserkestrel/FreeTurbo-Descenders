namespace DescendersBridge.Gui;

// The pure rules of the Workouts tab's left / right pickers: no window code here, so `selftest` drives them
// (tests\unit\WorkoutsTabTests.cs). The control that paints a picker is PickerRow (PickerControls.cs).

/// <summary>One entry of a left / right picker: its name, and the detail line shown under it when it is the chosen one.</summary>
public readonly record struct PickerEntry(string Title, string Detail = "");

/// <summary>
/// A picker that never opens a list: one entry is the chosen one, its neighbours are shown either side, left / right step one entry and
/// stop at the ends (no wrap). Any input source (keys, the mouse, later a gamepad or a remote) drives it through <see cref="Step"/>.
/// </summary>
public sealed class PickerModel
{
    private IReadOnlyList<PickerEntry> _entries = Array.Empty<PickerEntry>();

    public PickerModel(string label) { Label = label; }

    /// <summary>What is being picked ("Session"), for the line above the row.</summary>
    public string Label { get; }
    public int Count => _entries.Count;
    /// <summary>The chosen entry; -1 when there is nothing to choose.</summary>
    public int Index { get; private set; } = -1;

    public PickerEntry? Current => At(Index);
    /// <summary>The entry left of the chosen one; null at the first entry (that side is blank, its arrow hidden).</summary>
    public PickerEntry? Previous => Index > 0 ? At(Index - 1) : null;
    /// <summary>The entry right of the chosen one; null at the last entry.</summary>
    public PickerEntry? Next => Index >= 0 ? At(Index + 1) : null;
    public bool CanPrevious => Index > 0;
    public bool CanNext => Index >= 0 && Index < Count - 1;

    /// <summary>The line above the row: "Session · 8 of 24"; the label alone when there is nothing to choose.</summary>
    public string Position => Count == 0 ? Label : Label + " · " + (Index + 1) + " of " + Count;

    private PickerEntry? At(int i) => i >= 0 && i < _entries.Count ? _entries[i] : null;

    /// <summary>New entries, with <paramref name="selected"/> chosen (kept inside the list).</summary>
    public void Fill(IReadOnlyList<PickerEntry> entries, int selected)
    {
        _entries = entries;
        Index = entries.Count == 0 ? -1 : Math.Clamp(selected, 0, entries.Count - 1);
    }

    /// <summary>Move <paramref name="delta"/> entries (-1 = previous, +1 = next), stopping at the ends. False when nothing moved.</summary>
    public bool Step(int delta) => Select(Index + delta);

    /// <summary>Choose entry <paramref name="index"/> (kept inside the list). False when nothing moved.</summary>
    public bool Select(int index)
    {
        if (Count == 0) return false;
        int to = Math.Clamp(index, 0, Count - 1);
        if (to == Index) return false;
        Index = to;
        return true;
    }
}

/// <summary>Text helpers of the pickers.</summary>
public static class PickerText
{
    /// <summary><paramref name="text"/> in at most <paramref name="maxChars"/> characters: a longer one is cut and ends in "…".</summary>
    public static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        if (maxChars <= 0) return "";
        return text.Substring(0, maxChars - 1).TrimEnd() + "…";
    }
}

/// <summary>What a point of a picker row is over: the previous entry (its arrow and name), the chosen entry in the middle, the next entry.</summary>
public enum PickerHit { None, Previous, Centre, Next }

/// <summary>Where the parts of a picker row are, left to right (a click on the middle starts, a click either side only steps).</summary>
public static class PickerLayout
{
    /// <summary>The share of the row's width, arrows aside, each neighbour gets (the chosen entry wears a larger font, so it gets most of it).</summary>
    public const double NeighbourShare = 0.2;

    /// <summary>The width of one side of a row <paramref name="width"/> wide: the arrow and the neighbour's name.</summary>
    public static int Side(int width, int arrow) => arrow + Math.Max(0, (int)((width - 2 * arrow) * NeighbourShare));

    /// <summary>What is at <paramref name="x"/> in a row <paramref name="width"/> wide.</summary>
    public static PickerHit Hit(int x, int width, int arrow)
    {
        if (x < 0 || x >= width) return PickerHit.None;
        int side = Side(width, arrow);
        return x < side ? PickerHit.Previous : x >= width - side ? PickerHit.Next : PickerHit.Centre;
    }
}

/// <summary>
/// How a card of the Workouts tab shares the height its lines do not need (the effort graph no longer takes all of it, so it
/// is about half as tall as before; the picker rows get taller, and the rest is room between the lines).
/// </summary>
public static class CardRules
{
    /// <summary>The share of the spare height the effort graph takes, and the share the picker rows take between them.</summary>
    public const double GraphShare = 0.45, RowShare = 0.25;

    /// <summary>
    /// <paramref name="spare"/>: the card's height left once every line has its least height. Returns what the graph and each of the
    /// <paramref name="rows"/> picker rows (never more than <paramref name="rowMax"/>) grow by, and what each of the <paramref name="gaps"/>
    /// between the lines above the graph widens by. A card with nothing to spare changes nothing.
    /// </summary>
    public static (int Graph, int Row, int Gap) Share(int spare, int rows, int gaps, int rowMax)
    {
        if (spare <= 0) return (0, 0, 0);
        int graph = (int)Math.Round(spare * GraphShare);
        int row = rows > 0 ? Math.Max(0, Math.Min(rowMax, (int)(spare * RowShare) / rows)) : 0;
        int gap = gaps > 0 ? (spare - graph - row * rows) / gaps : 0;
        return (graph, row, gap);
    }
}

/// <summary>What an input source can ask of the Workouts tab: move the focus up / down, step the focused picker, start what its card offers.</summary>
public enum NavCommand { Up, Down, Previous, Next, Activate }

/// <summary>The up / down focus order of the Workouts tab (the picker rows, top to bottom; there are no Start buttons).</summary>
public static class NavRules
{
    /// <summary>How to start what a card offers, now that it has no button.</summary>
    public const string StartHint = "Enter or click to start";

    /// <summary>The line under a card: the hint while the focus is on one of its rows and there is something to start, else nothing.
    /// <paramref name="pad"/>: the Xbox pad was the last thing used, so it names the pad's button.</summary>
    public static string Hint(bool focused, bool canStart, bool pad = false) => focused && canStart ? pad ? PadRules.StartHint : StartHint : "";

    /// <summary>
    /// The stop the focus moves to from <paramref name="current"/> (-1 = none of them has it yet) going <paramref name="delta"/> (-1 up,
    /// +1 down): the nearest usable stop that way (an empty picker is skipped); <paramref name="current"/> when there is
    /// none (no wrap).
    /// </summary>
    public static int Move(int current, int delta, IReadOnlyList<bool> usable)
    {
        int step = Math.Sign(delta);
        if (step == 0) return current;
        for (int i = current + step; i >= 0 && i < usable.Count; i += step)
            if (usable[i]) return i;
        return current;
    }
}
