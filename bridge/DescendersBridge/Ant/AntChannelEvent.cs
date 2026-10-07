namespace DescendersBridge.Ant;

/// <summary>
/// ANT channel event / response codes (the third byte of a 0x40 message). Full
/// table with meanings in <c>docs/fec-protocol.md</c> → "Channel event codes".
/// The pair that matters is <see cref="TransferTxCompleted"/> (0x05, our write
/// landed) vs <see cref="TransferTxFailed"/> (0x06, it was silently discarded) —
/// one hex character apart, opposite meanings.
/// </summary>
public enum AntChannelEvent : byte
{
    NoError = 0x00,               // command accepted
    RxSearchTimeout = 0x01,       // search gave up — trainer never answered
    RxFail = 0x02,               // one expected message missed
    Tx = 0x03,                    // a broadcast went out (master channels)
    TransferRxFailed = 0x04,      // an inbound burst/ack failed
    TransferTxCompleted = 0x05,   // the acknowledged write reached the trainer
    TransferTxFailed = 0x06,      // the write did NOT land — resistance unchanged
    ChannelClosed = 0x07,
    RxFailGoToSearch = 0x08,      // link lost, dropping back to search
    ChannelCollision = 0x09,
    TransferTxStart = 0x0A,
    ChannelInWrongState = 0x15,   // command issued against a closed/unassigned channel
    ChannelNotOpened = 0x16,
    TransferInProgress = 0x1F,    // a previous ack write hasn't finished — this one was rejected
}

public static class AntChannelEvents
{
    /// <summary>
    /// Human name for a code. Known codes → the enum name; unknown codes → the
    /// hex, so the raw value is never lost.
    /// </summary>
    public static string Name(byte code) =>
        Enum.IsDefined(typeof(AntChannelEvent), code)
            ? ((AntChannelEvent)code).ToString()
            : $"UNKNOWN(0x{code:X2})";
}
