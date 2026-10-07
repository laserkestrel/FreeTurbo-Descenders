using System.Runtime.InteropServices;

namespace DescendersBridge.Gui;

/// <summary>
/// The Xbox pad for the bridge window: XInput, the first connected of the four slots. Only the window reads it (the
/// headless bridge has none); what a press does is in PadNav.cs. No pad, or no XInput on this PC: no buttons down.
/// </summary>
public sealed class PadInput
{
    private const int Slots = 4;
    private const double RescanSeconds = 2;   // asking an empty slot is slow, so with no pad the slots are only looked at now and then

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState14(uint slot, out State state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState910(uint slot, out State state);

    private int _library;   // 0 = xinput1_4, 1 = xinput9_1_0 (every Windows has it), 2 = none
    private int _slot = -1;
    private double _scanAt = double.NegativeInfinity;

    private bool TryRead(int slot, out PadButtons held)
    {
        held = PadButtons.None;
        while (_library < 2)
        {
            try
            {
                State s;
                uint result = _library == 0 ? GetState14((uint)slot, out s) : GetState910((uint)slot, out s);
                if (result != 0) return false;
                held = PadRules.FromXInput(s.Buttons);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _library++;
                if (_library == 2) Console.WriteLine("Xbox pad: XInput is not on this PC, the window is used with mouse and keyboard only");
            }
        }
        return false;
    }

    /// <summary>The buttons held down now; none when no pad is connected.</summary>
    /// <param name="t">a clock, in seconds.</param>
    public PadButtons Read(double t)
    {
        if (_slot >= 0 && TryRead(_slot, out var held)) return held;
        _slot = -1;
        if (t < _scanAt) return PadButtons.None;
        _scanAt = t + RescanSeconds;
        for (int i = 0; i < Slots && _library < 2; i++)
        {
            if (!TryRead(i, out held)) continue;
            _slot = i;
            return held;
        }
        return PadButtons.None;
    }
}
