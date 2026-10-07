namespace DescendersBridge.Ant;

/// <summary>
/// Parses the ANT+ network key from whatever punctuation the human pasted.
///
/// The adopter site presents it as a C array literal — <c>{0xB9, 0xA5, ...}</c> —
/// which is awkward as config. This accepts that, plus dashes, spaces and bare
/// hex. See <c>docs/fec-protocol.md</c>.
///
/// Two traps, both guarded here:
///   * It is a <b>byte sequence, not an integer</b> — never byte-swapped.
///   * It must be exactly 8 bytes. 7 or 9 means a transcription slip, and the
///     downstream failure (a channel that opens fine then receives nothing) is
///     indistinguishable from a driver fault. Fail loudly here instead.
/// </summary>
public static class AntNetworkKey
{
    public static byte[] Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new FormatException("ANT network key is empty. Fill in config.json or use a build with the key built in.");

        // Strip "0x" / "0X" first so the surviving '0' is not mistaken for data.
        var cleaned = s.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        var hex = new string(cleaned.Where(Uri.IsHexDigit).ToArray());

        if (hex.Length != 16)
        {
            throw new FormatException(
                $"ANT network key must be 8 bytes (16 hex digits); got {hex.Length / 2} "
                + $"byte(s) from \"{s}\". A transcription slip is the usual cause.");
        }

        if (hex == "PASTE_ADOPTER_KEY_HERE")   // unreachable (not hex) but explicit
            throw new FormatException("ANT network key placeholder not filled in.");

        return Convert.FromHexString(hex);
    }
}
