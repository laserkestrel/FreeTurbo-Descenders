using System.Diagnostics;
using ANT_Managed_Library;

namespace DescendersBridge.Ant;

/// <summary>
/// FE-C over ANT+ using Garmin/Dynastream's managed library (ported from kcd2-turbo). Opens a Fitness Equipment
/// slave channel on the SHARED ANT device, paired to one specific trainer (the device number the user chose),
/// so it never latches onto a neighbour's trainer. The device and network key are owned by <see cref="AntSession"/>.
///
/// Channel parameters: device type 17, period 8192 (~4 Hz), RF frequency offset 57.
/// </summary>
public sealed class AntTrainerLink : ITrainerControl, IAsyncDisposable
{
    private const ushort ChannelPeriod = 8192;
    private const byte RfFrequency = 57;
    private const byte NetworkNumber = 0;
    private readonly byte ChannelNumber;
    private const uint Wait = 500;    // ms to wait for each config command's ACK
    private const uint AckWait = 500; // ms to wait for a page acknowledgement from the trainer

    // FE-C pages arrive at ~4 Hz. No page for this long = the link is gone,
    // whatever the event stream did or did not say (docs/failsafe.md P0 #2).
    private static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _txLock = new(1, 1);   // serialise page writes
    private readonly CancellationTokenSource _lifetime = new();

    // ANT message IDs (ANT_Response.responseID). Raw bytes to avoid enum churn.
    private const byte MsgBroadcastData = 0x4E;
    private const byte MsgAcknowledgedData = 0x4F;
    private const byte MsgBurstData = 0x50;
    private const byte MsgExtBroadcastData = 0x5D;
    private const byte MsgExtAcknowledgedData = 0x5E;
    private const byte MsgResponseEvent = 0x40;

    private readonly DeviceKey _key;
    private readonly DeviceTracker _tracker;
    private readonly bool _verbose;
    private readonly TxOutcomeWindow _txWindow = new();
    private readonly TxOutcomeWindow _rxWindow = new(
        TimeSpan.FromSeconds(10), failFraction: 0.30, minSamples: 12,
        reAlarmGap: TimeSpan.FromSeconds(15), subject: "FE-C messages");

    private long _lastDataTicks;   // Stopwatch timestamp of the last FE-C page received

    private readonly ANT_Device _device;   // shared; not disposed here
    private ANT_Channel? _channel;
    private int _paired;
    private int _everPaired;   // 1 once we've paired at least once → later pairs are "re-pairs"
    private int _disposed;     // 0 = live; the ANT native lib AVs on a double free too
    private int _releaseAttempted;   // 1 once ReleaseAsync has run — DisposeAsync then skips its own reset
    private long _commandsSent;
    private long _commandsDelivered;
    private double _lastGradeCommanded;   // the grade the trainer is holding, for the shutdown message
    private double _lastTargetPowerW = double.NaN;   // the acknowledged ERG target; NaN = none held

    public event Action<TrainerTelemetry>? TelemetryReceived;
    public event Action<string>? Status;

    /// <summary>
    /// True on pair, false when the link is lost (RX search timeout / go-to-search).
    /// Drive it into the same failsafe path as gradient staleness.
    /// </summary>
    public event Action<bool>? LinkStateChanged;

    public bool IsPaired => Volatile.Read(ref _paired) != 0;

    /// <summary>Resistance writes attempted vs acknowledged (design decision 7).</summary>
    public (long Sent, long Delivered) TxCounts =>
        (Interlocked.Read(ref _commandsSent), Interlocked.Read(ref _commandsDelivered));

    /// <summary>
    /// Wall time since the last FE-C page arrived — the liveness watchdog's own
    /// input, surfaced so the drive loop's heartbeat can show it rather than
    /// keeping a second parallel clock. Before the first page this reads as time
    /// since <see cref="ConnectAsync"/>.
    /// </summary>
    public TimeSpan SinceLastData =>
        Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastDataTicks));

    public AntTrainerLink(ANT_Device device, byte channelNumber, DeviceKey key, DeviceTracker tracker, bool verbose = false)
    {
        _device = device;
        ChannelNumber = channelNumber;
        _key = key;
        _tracker = tracker;
        _verbose = verbose;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _channel = _device.getChannel(ChannelNumber);
        _channel.channelResponse += OnChannelResponse;

        Configure(_channel);

        if (!_channel.openChannel(Wait))
            throw new InvalidOperationException("openChannel failed.");

        _lastDataTicks = Stopwatch.GetTimestamp();
        _ = Task.Run(() => DataLivenessWatchdogAsync(_lifetime.Token));

        Status?.Invoke(
            $"channel {ChannelNumber} open — searching for trainer #{_key.DeviceNumber} (FE-C, {2400 + RfFrequency} MHz)…");

        return Task.CompletedTask;
    }

    private void Configure(ANT_Channel ch)
    {
        Check(ch.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00,
                               NetworkNumber, Wait), nameof(ch.assignChannel));

        // The exact device the user chose (number + transmission type), not a wildcard.
        Check(ch.setChannelID(_key.DeviceNumber, /*pairing*/ false,
                              _key.DeviceType, _key.TransmissionType, Wait),
              nameof(ch.setChannelID));

        Check(ch.setChannelFreq(RfFrequency, Wait), nameof(ch.setChannelFreq));
        Check(ch.setChannelPeriod(ChannelPeriod, Wait), nameof(ch.setChannelPeriod));

        // 255 = search forever. We never want the channel to give up mid-ride.
        Check(ch.setChannelSearchTimeout(255, Wait), nameof(ch.setChannelSearchTimeout));

        // The trainer is searched for before any sensor when it has to re-pair (sensor channels keep priority 0).
        // Not every stick has search priorities: a refusal is logged, not fatal.
        bool priority;
        try { priority = ch.setChannelSearchPriority(SensorSearchRules.TrainerSearchPriority, Wait); }
        catch { priority = false; }
        if (!priority) Status?.Invoke("search priority not set (this stick does not support it): sensor searches stay short instead");

        static void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException($"{what} failed.");
        }
    }

    private void OnChannelResponse(ANT_Response response)
    {
        try
        {
            switch (response.responseID)
            {
                case MsgBroadcastData:
                case MsgAcknowledgedData:
                case MsgBurstData:
                case MsgExtBroadcastData:
                case MsgExtAcknowledgedData:
                    HandleData(response);
                    break;

                case MsgResponseEvent:
                    HandleChannelEvent(response);
                    break;
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"response handler error: {ex.Message}");
        }
    }

    private void HandleData(ANT_Response response)
    {
        // Proof of life for the liveness watchdog — before the pair check, so a
        // fresh page can't be immediately re-flagged as lost.
        Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
        lock (_rxWindow) _rxWindow.Record(true, DateTime.UtcNow);

        // First broadcast from the trainer (this session or since a link loss) = paired.
        if (Interlocked.Exchange(ref _paired, 1) == 0)
        {
            bool rePair = Interlocked.Exchange(ref _everPaired, 1) != 0;
            Status?.Invoke(rePair
                ? "✅ trainer re-paired — resistance live"
                : "paired — receiving FE-C data.");
            LinkStateChanged?.Invoke(true);
        }

        byte[] payload = response.getDataPayload(); // 8 bytes; [0] = page number
        lock (_tracker) _tracker.Update(payload);
        var telemetry = FecParser.Parse(payload);
        if (telemetry is not null)
            TelemetryReceived?.Invoke(telemetry);
    }

    private void HandleChannelEvent(ANT_Response response)
    {
        // messageContents = [channel, msgId, eventCode, ...]
        var contents = response.messageContents;
        if (contents.Length < 3) return;

        byte code = contents[2];
        string name = AntChannelEvents.Name(code);

        switch ((AntChannelEvent)code)
        {
            // A missed 4 Hz message. Routine one at a time; a rising rate is the
            // leading indicator of a link about to go silent (docs/failsafe.md
            // P0 #4) — feed the window and warn on the trend.
            case AntChannelEvent.RxFail:
            {
                string? warn;
                lock (_rxWindow) warn = _rxWindow.Record(false, DateTime.UtcNow);
                if (warn is not null) Status?.Invoke($"⚠ {warn}");
                if (_verbose) Status?.Invoke($"channel: {name}");
                return;
            }

            // The outcome of our own acknowledged writes arrives synchronously as
            // the SendPageCore return code (0x05 ⟺ pass, 0x06 ⟺ fail). The async
            // event is redundant — quiet unless --verbose.
            case AntChannelEvent.NoError:
            case AntChannelEvent.Tx:
            case AntChannelEvent.TransferTxStart:
            case AntChannelEvent.TransferTxCompleted:
            case AntChannelEvent.TransferTxFailed:
            case AntChannelEvent.ChannelClosed:
            case AntChannelEvent.ChannelCollision:
                if (_verbose) Status?.Invoke($"channel: {name}");
                return;

            // The trainer link is gone. The faster of the two signals — but not
            // the reliable one; the liveness watchdog is (docs/failsafe.md P0 #2).
            case AntChannelEvent.RxSearchTimeout:
            case AntChannelEvent.RxFailGoToSearch:
                DeclareLinkLost(name);
                return;

            // We serialise writes with _txLock, so this should be impossible.
            case AntChannelEvent.TransferInProgress:
                Status?.Invoke($"⚠ {name} — a resistance write overlapped a previous one");
                return;

            // A command reached a closed/unassigned channel — our own lifecycle bug.
            case AntChannelEvent.ChannelInWrongState:
            case AntChannelEvent.ChannelNotOpened:
                Status?.Invoke($"⚠ {name} — command issued against a closed channel (lifecycle bug); not retried");
                return;

            default:
                Status?.Invoke($"channel event {name}");
                return;
        }
    }

    /// <summary>
    /// Drop <c>_paired</c> and route into the failsafe. Idempotent — whichever of
    /// the event hook or the liveness watchdog gets there first wins; the other
    /// is a no-op.
    /// </summary>
    private void DeclareLinkLost(string reason)
    {
        if (Interlocked.Exchange(ref _paired, 0) != 0)
        {
            Status?.Invoke($"⚠ trainer link lost ({reason}) — resistance 0%");
            LinkStateChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Watches for the <b>absence</b> of FE-C data rather than for an event that
    /// says the link died — the real failure mode, where nothing is announced at
    /// all (docs/failsafe.md P0 #2). No page 16/25 for <see cref="DataTimeout"/>
    /// while paired = gone.
    /// </summary>
    private async Task DataLivenessWatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (!IsPaired) continue;
            var since = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastDataTicks));
            if (since > DataTimeout)
                DeclareLinkLost($"no FE-C data for {since.TotalSeconds:0.0} s");
        }
    }

    // ─── FE-C control writes ───────────────────────────────────────────────

    public Task<bool> SendUserConfigAsync(RiderConfig rider, CancellationToken ct = default)
    {
        var page = FecControlPages.UserConfiguration(
            rider.RiderWeightKg, rider.BikeWeightKg, rider.WheelDiameterM, rider.GearRatio);
        return SendPageAsync(page, "user config (0x37)", ct);
    }

    public Task<bool> SendWindResistanceAsync(double windCoefficientKgPerM, CancellationToken ct = default)
    {
        var page = FecControlPages.WindResistance(windCoefficientKgPerM);
        return SendPageAsync(page, $"wind resistance (0x32) coeff {windCoefficientKgPerM:0.###} kg/m", ct);
    }

    public async Task<bool> SetTrackResistanceAsync(double gradePercent, double crr, CancellationToken ct = default)
    {
        var page = FecControlPages.TrackResistance(gradePercent, crr);
        bool ok = await SendPageAsync(page, $"track resistance (0x33) grade {gradePercent:0.0}%", ct)
            .ConfigureAwait(false);

        // Decision 7: what the trainer is *holding* is the last grade it
        // acknowledged, never the last one we tried to send. A failed flat write
        // during the failsafe must not read back as "released" (docs/failsafe.md
        // P0 #1 — this line lied at 21:38:50 on the 2026-08-31 run).
        if (ok) { Volatile.Write(ref _lastGradeCommanded, gradePercent); Volatile.Write(ref _lastTargetPowerW, double.NaN); }   // 0x33 ends ERG
        return ok;
    }

    /// <summary>Write an ERG target (page 0x31). True only when the trainer acknowledged; <see cref="LastTargetPowerW"/> then holds it.</summary>
    public async Task<bool> SetTargetPowerAsync(double watts, CancellationToken ct = default)
    {
        var page = FecControlPages.TargetPower(watts);
        bool ok = await SendPageAsync(page, $"target power (0x31) {FecControlPages.TargetPowerField.Sent(watts):0.##} W", ct).ConfigureAwait(false);
        if (ok) Volatile.Write(ref _lastTargetPowerW, watts);
        return ok;
    }

    /// <summary>The ERG target the trainer last acknowledged, NaN when none is held (a flat write, i.e. a released target, resets it).</summary>
    public double LastTargetPowerW => Volatile.Read(ref _lastTargetPowerW);

    public Task<bool> ResetResistanceAsync(CancellationToken ct = default) =>
        SetTrackResistanceAsync(0.0, double.NaN, ct);

    /// <summary>
    /// The last grade the trainer <b>acknowledged</b> — i.e. what it is actually
    /// holding. An unacknowledged write does not move this: a dropped flat write
    /// leaves it reading the last live grade, which is what the shutdown message
    /// needs to warn against (docs/failsafe.md P0 #1).
    /// </summary>
    public double LastGradeCommanded => Volatile.Read(ref _lastGradeCommanded);

    /// <summary>
    /// Shutdown-only. Retry a zero-grade write until it is acknowledged or
    /// <paramref name="budget"/> is spent — this is the one command worth several
    /// attempts (the process is ending and has nothing better to do). Returns a
    /// distinguishable outcome; the caller reports it honestly (docs/failsafe.md
    /// P0 #1). Choose the budget together with any outer <c>.Wait()</c> that
    /// bounds it — see DriveCli.
    /// </summary>
    public async Task<ReleaseResult> ReleaseAsync(TimeSpan budget, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _releaseAttempted, 1);
        double held = LastGradeCommanded;

        if (Volatile.Read(ref _disposed) != 0 || _channel is null || !IsPaired)
            return new(ReleaseOutcome.LinkAlreadyDead, held);

        var flat = FecControlPages.TrackResistance(0.0, double.NaN);
        var deadline = DateTime.UtcNow + budget;
        int attempts = 0;

        do
        {
            attempts++;
            int rc;
            await _txLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ch = _channel;
                if (ch is null) return new(ReleaseOutcome.LinkAlreadyDead, held);
                rc = await Task.Run(() => (int)ch.sendAcknowledgedData(flat, AckWait), ct).ConfigureAwait(false);
            }
            catch
            {
                // The send threw, not returned a code — the ANT device itself is
                // gone (dongle unplugged). Not "the trainer won't ack"; the link
                // is dead. Stop, don't busy-spin the budget away.
                return new(ReleaseOutcome.LinkAlreadyDead, held);
            }
            finally { _txLock.Release(); }

            Interlocked.Increment(ref _commandsSent);
            if (rc == 1)
            {
                Interlocked.Increment(ref _commandsDelivered);
                Volatile.Write(ref _lastGradeCommanded, 0.0);
                Volatile.Write(ref _lastTargetPowerW, double.NaN);
                return new(ReleaseOutcome.Released, held);
            }
            if (!IsPaired)
                return new(ReleaseOutcome.LinkAlreadyDead, held);

            // rc was Fail/Timeout — the mechanism works, the trainer just didn't
            // ack. Pace the retries; sendAcknowledgedData returns fast on Fail.
            try { await Task.Delay(150, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested);

        Status?.Invoke($"reset not acknowledged after {attempts} attempt(s) in {budget.TotalSeconds:0.0}s");
        return new(ReleaseOutcome.Failed, held);
    }

    /// <summary>
    /// Close and reopen the channel to restart the FE-C search after a link
    /// loss. The channel config is untouched, so this just resets the search;
    /// when the trainer answers, <see cref="HandleData"/> re-pairs and announces
    /// it. The drive loop calls this with exponential backoff — a delivery gap is
    /// not a death (docs/sampler-lifecycle.md), so it waits a grace period first.
    /// </summary>
    public async Task ReSearchAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var ch = _channel;
        if (ch is null) return;

        await _txLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;   // teardown won the race
            await Task.Run(() =>
            {
                ch.closeChannel(Wait);
                ch.openChannel(Wait);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // close/open threw — the ANT device is gone (dongle unplugged). A
            // channel restart can't help; the device has to be re-created.
            Status?.Invoke($"re-search failed: {ex.Message} — reconnect the ANT stick");
        }
        finally
        {
            _txLock.Release();
        }
    }

    /// <summary>Public write path — refuses once teardown has begun. Returns
    /// whether the trainer acknowledged the write (design decision 7).</summary>
    private Task<bool> SendPageAsync(byte[] page, string what, CancellationToken ct)
    {
        // Teardown first: after DisposeAsync the channel is gone and the tx lock
        // is disposed — a send here must fail quietly, not throw or touch native.
        if (Volatile.Read(ref _disposed) != 0 || _channel is null) return Task.FromResult(false);

        // P2 (docs/fec-protocol.md "Open"): nothing to send to while unpaired —
        // ReSearchAsync, with its own backoff, already owns re-acquisition. Skip
        // rather than attempt: sendAcknowledgedData's AckWait plus one retry is up
        // to ~1 s blocked *every drive-loop tick*, which is harmless today but
        // would collapse the gait controller's L3 double-tap spacing later (an
        // unplugged stick would make the horse gallop). Deliberately not moved to
        // a background thread — fire-and-forget would decouple the write from its
        // outcome, and decision 7 (sent vs acknowledged, LastGradeCommanded
        // meaning what the trainer actually holds) depends on that outcome
        // arriving synchronously.
        //
        // A skipped write is not attempted radio traffic, so it counts as neither
        // sent nor delivered: `_commandsSent` growing against a frozen
        // `_commandsDelivered` while unpaired would read as a degrading link
        // (P0 #4's job) rather than an absent one. Does not touch
        // `_lastGradeCommanded` either — same rule as a failed write; the trainer
        // still holds whatever it last acknowledged. `ReleaseAsync` and the
        // `DisposeAsync` fallback bypass this method entirely and keep their own
        // `IsPaired` checks, so the shutdown path is unaffected.
        if (!IsPaired) return Task.FromResult(false);

        return SendPageCore(page, what, ct);
    }

    /// <summary>The actual send. Used directly by <see cref="DisposeAsync"/>, which
    /// must be able to send the shutdown reset after <c>_disposed</c> is set.
    /// One immediate retry on failure — a single dropped ack on a 4 Hz link is
    /// normal. Records the sent/acknowledged counts (design decision 7). Returns
    /// true only if the trainer acknowledged.</summary>
    private async Task<bool> SendPageCore(byte[] page, string what, CancellationToken ct)
    {
        var ch = _channel;
        if (ch is null) return false;

        // AckWait (500 ms) is a real timeout: sendAcknowledgedData blocks until
        // the trainer acknowledges the transfer or the window expires. So rc == 1
        // means the trainer actually received the page, not merely that it was
        // queued. Comfortable at 1 Hz.
        await _txLock.WaitAsync(ct).ConfigureAwait(false);
        int rc;
        try
        {
            // 0=Fail 1=Pass 2=Timeout 3=Cancelled 4=InvalidParams; -1 = threw
            // (the ANT device is gone — dongle unplugged).
            rc = await Task.Run(() => (int)ch.sendAcknowledgedData(page, AckWait), ct).ConfigureAwait(false);

            // Retry exactly once, and only for a transient dropped ack — never
            // for 4 (our packing is wrong; retrying sends the same bad bytes).
            // The retry is inside the same _txLock, so it can't overlap another
            // transfer (0x1F TransferInProgress).
            if (ShouldRetry(rc))
                rc = await Task.Run(() => (int)ch.sendAcknowledgedData(page, AckWait), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { rc = -1; }
        finally
        {
            _txLock.Release();
        }

        bool delivered = rc == 1;
        Interlocked.Increment(ref _commandsSent);
        if (delivered) Interlocked.Increment(ref _commandsDelivered);

        if (delivered)
        {
            if (_verbose) Status?.Invoke($"{what} — TransferTxCompleted");
        }
        else if (rc == 4)
        {
            Status?.Invoke($"⚠ {what} — InvalidParams: bad page packing, this is a bug; not retried");
        }
        else if (rc == -1)
        {
            Status?.Invoke($"⚠ {what} — the ANT device threw (dongle unplugged?); write not sent");
        }
        else
        {
            Status?.Invoke($"⚠ {what} NOT DELIVERED ({RcName(rc)}) — trainer resistance unchanged");
        }

        string? warn;
        lock (_txWindow) warn = _txWindow.Record(delivered, DateTime.UtcNow);
        if (warn is not null) Status?.Invoke($"⚠ {warn}");

        return delivered;
    }

    /// <summary>Retry a resistance write only for a transient dropped ack (Fail / Timeout).</summary>
    internal static bool ShouldRetry(int rc) => rc is 0 or 2;

    private static string RcName(int rc) => rc switch
    {
        -1 => "device threw",
        0 => "TransferTxFailed",
        2 => "Timeout",
        3 => "Cancelled",
        4 => "InvalidParams",
        _ => $"rc {rc}",
    };

    // ─── shutdown ──────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // Win the race exactly once. A second entrant would close an already-freed
        // ANT channel — same uncatchable native AV as VirtualGamepad.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lifetime.Cancel();   // stop the liveness watchdog (touches no native state)

        // Failsafe (decision 5): leave the trainer flat, not mid-climb. If
        // ReleaseAsync already ran (the deterministic Ctrl+C path), it has done
        // this harder and reported the outcome — don't send again or re-log a
        // failure as news. This branch is the best-effort ProcessExit fallback.
        try
        {
            if (_channel is not null && IsPaired && Volatile.Read(ref _releaseAttempted) == 0)
            {
                await SendPageCore(FecControlPages.TrackResistance(0.0, double.NaN),
                    "reset resistance on shutdown", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }

        try
        {
            if (_channel is not null)
            {
                _channel.channelResponse -= OnChannelResponse;
                _channel.closeChannel(Wait);
                await Task.Delay(100).ConfigureAwait(false);
                _channel.Dispose();
            }
        }
        catch { /* best effort on shutdown */ }

        _txLock.Dispose();
        _lifetime.Dispose();
        _channel = null;
    }
}
