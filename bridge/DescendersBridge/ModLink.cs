using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DescendersBridge;

/// <summary>
/// Every UDP socket of the bridge lives on 127.0.0.1. A sender made with <c>new UdpClient()</c> is bound by Windows
/// to 0.0.0.0 at its first Send, an address the network can reach, which is what Windows Firewall asks about.
/// </summary>
internal static class Loopback
{
    /// <summary>A socket for sending to the mod or to a bridge, bound to 127.0.0.1 on a port Windows picks.</summary>
    public static UdpClient Sender() => new(new IPEndPoint(IPAddress.Loopback, 0));
}

/// <summary>
/// The `status` message (docs/IPC.md): what the bridge is doing, in words the mod shows on its HUD ("Searching for trainer", ...).
/// Sent about once a second in every phase, including before the ANT+ stick opens, so it has its own socket rather than ModLink's.
/// </summary>
public static class ModStatus
{
    private static readonly UdpClient Tx = Loopback.Sender();
    private static readonly IPEndPoint ModEndpoint = new(IPAddress.Loopback, 9872);

    public static string LastState { get; private set; } = "";

    public static void Send(string state, string text)
    {
        if (state != LastState) Console.WriteLine($"[status] {state}: {text}");
        LastState = state;
        byte[] b = Encoding.UTF8.GetBytes("{\"type\":\"status\",\"state\":" + JsonSerializer.Serialize(state) + ",\"text\":" + JsonSerializer.Serialize(text) + "}");
        try { Tx.Send(b, b.Length, ModEndpoint); } catch { /* mod not listening: UDP, nothing to do */ }
    }
}

/// <summary>Latest state reported by the mod (docs/IPC.md telemetry). <paramref name="Riding"/> = player found and the game not paused
/// (telemetry `riding`; a mod that does not send it counts as riding whenever the player is found). <paramref name="RoadGradient"/> = telemetry
/// `fitness.roadGradient`, the raw road gradient as a fraction (0.13 = 13 %); the bridge scales it by trainerDifficulty.</summary>
public readonly record struct ModState(bool Seen, double AgeSeconds, bool PlayerFound, bool FitnessActive, double? RoadGradient, bool Riding = true);

/// <summary>
/// UDP link to the Descenders mod (docs/IPC.md): sends the `trainer` and `session` messages to 127.0.0.1:9872 and listens on 9871
/// for the mod's telemetry (keeping only the latest `fitness.roadGradient`, the raw road gradient as a fraction: 0.13 = 13 %, and `riding`), its `intent` messages (queued in order for the ride loop)
/// and its `hello` (which game is connected,; <see cref="SecondsSinceHeard"/> covers every packet, for <see cref="GamePresence"/>). Only one process can hold 9871.
/// </summary>
public sealed class ModLink : IDisposable
{
    private readonly UdpClient _tx = Loopback.Sender();
    private readonly UdpClient _sessionTx = Loopback.Sender();   // own socket, like `status` (docs/IPC.md)
    private readonly IPEndPoint _modEndpoint;
    private readonly UdpClient _rx;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private long _lastTicks;
    private bool _seen, _playerFound, _fitnessActive, _riding;
    private double? _grade;
    private readonly ConcurrentQueue<Intent> _intents = new();
    private readonly SemaphoreSlim _intentArrived = new(0);

    public long RxCount, RxBad, TxCount;
    private StreamWriter? _log;
    private readonly object _logGate = new();
    private string? _logPath;
    private Func<string>? _nextLogPath;
    private int _logEvery = 5;
    private long _telemetryCount;
    private long _heardTicks;          // any packet from the game (Stopwatch ticks), 0 = never
    private GameHello? _hello;
    private long _helloTicks;

    /// <summary>
    /// Record the mod's telemetry (every <paramref name="telemetryEvery"/>th packet, one JSON line each) and every other packet (acks, intents, hello)
    /// for tuning afterwards. <paramref name="nextPath"/> names the file for a log reopened after the game was gone (<see cref="ReopenLog"/>);
    /// null = append to the same file.
    /// </summary>
    public void StartLog(string path, int telemetryEvery = 5, Func<string>? nextPath = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path.GetFullPath(path))!);
        lock (_logGate)
        {
            _log?.Dispose();
            _logEvery = Math.Max(1, telemetryEvery);
            _logPath = path; _nextLogPath = nextPath;
            _log = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    /// <summary>A ride log is being written.</summary>
    public bool LogOpen { get { lock (_logGate) return _log is not null; } }

    /// <summary>A log was started once, so <see cref="ReopenLog"/> can start the next one.</summary>
    public bool CanReopenLog { get { lock (_logGate) return _logPath is not null; } }

    /// <summary>Close the ride log (the game has been gone a while); the bridge keeps running.</summary>
    public void EndLog(string why)
    {
        string? path;
        lock (_logGate)
        {
            if (_log is null) return;
            _log.Dispose(); _log = null; path = _logPath;
        }
        Console.WriteLine($"Ride log ended ({why}): {path}");
    }

    /// <summary>Start logging again after <see cref="EndLog"/>: a new file (<c>nextPath</c> of <see cref="StartLog"/>), or the same one appended.</summary>
    public void ReopenLog()
    {
        string path;
        lock (_logGate)
        {
            if (_log is not null || _logPath is null) return;
            bool fresh = _nextLogPath is not null;
            path = _nextLogPath?.Invoke() ?? _logPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path.GetFullPath(path))!);
            _log = new StreamWriter(path, append: !fresh, new UTF8Encoding(false)) { AutoFlush = true };
            _logPath = path;
        }
        Console.WriteLine("A game is connected again: logging mod telemetry to " + path);
    }

    /// <summary>Seconds since any packet from the game (telemetry, hello, intent); +infinity = never.</summary>
    public double SecondsSinceHeard
    {
        get { long t = Interlocked.Read(ref _heardTicks); return t == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(t).TotalSeconds; }
    }

    /// <summary>The latest `hello` (which game is connected), null until one arrives.</summary>
    public GameHello? Hello { get { lock (_lock) return _hello; } }

    /// <summary>`{"type":"shutdown"}` arrived (`turbotrainer-bridge stop`, used by the dev scripts before installing: the game's
    /// bridge outlives the game). Set by `run --auto` only; the window and `run` / `run --sim` ignore it. Called on the receive thread.</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>Forget button presses queued while no ride was running (a shared link in `run --auto` listens from the start).</summary>
    public void DropIntents()
    {
        while (_intents.TryDequeue(out _)) { }
        while (_intentArrived.Wait(0)) { }
    }

    public ModLink(int telemetryPort = 9871, int commandPort = 9872)
    {
        _modEndpoint = new IPEndPoint(IPAddress.Loopback, commandPort);
        _rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, telemetryPort));   // throws if another bridge holds the port
        _ = Task.Run(ReceiveLoop);
    }

    public ModState State
    {
        get
        {
            lock (_lock)
                return new ModState(_seen, _seen ? Stopwatch.GetElapsedTime(_lastTicks).TotalSeconds : double.PositiveInfinity, _playerFound, _fitnessActive, _grade, _riding);
        }
    }

    /// <summary>The port it listens on (tests open one on port 0).</summary>
    internal int ListenPort => ((IPEndPoint)_rx.Client.LocalEndPoint!).Port;

    /// <summary>The oldest button press not yet handled.</summary>
    public bool TryTakeIntent(out Intent it) => _intents.TryDequeue(out it);

    /// <summary>True as soon as an intent arrives (it may already have been taken), false after <paramref name="timeout"/>.</summary>
    public Task<bool> WaitIntentAsync(TimeSpan timeout, CancellationToken ct) => _intentArrived.WaitAsync(timeout, ct);

    /// <summary>Send the trainer state. Omitted fields are omitted from the JSON (the mod treats absent as "no reading").</summary>
    public void SendTrainer(double powerW, double? cadenceRpm, int? heartRateBpm, bool connected)
    {
        var sb = new StringBuilder("{\"type\":\"trainer\",\"powerW\":");
        sb.Append(powerW.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        if (cadenceRpm.HasValue) sb.Append(",\"cadenceRpm\":").Append(cadenceRpm.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        if (heartRateBpm.HasValue) sb.Append(",\"heartRateBpm\":").Append(heartRateBpm.Value);
        sb.Append(",\"connected\":").Append(connected ? "true" : "false").Append('}');
        byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
        try { _tx.Send(b, b.Length, _modEndpoint); TxCount++; } catch { /* mod not listening: UDP, nothing to do */ }
    }

    /// <summary>Send the `session` message (<see cref="Session.Message"/>), latest-wins at the mod.</summary>
    public void SendSession(string json)
    {
        byte[] b = Encoding.UTF8.GetBytes(json);
        try { _sessionTx.Send(b, b.Length, _modEndpoint); } catch { /* mod not listening */ }
    }

    /// <summary>Reads one `intent` (docs/IPC.md); null when it has no name. Pure, for `selftest`.</summary>
    public static Intent? ParseIntent(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 } name) return null;
        double? value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        return new Intent(name, value);
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _rx.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                RxCount++;
                using var doc = JsonDocument.Parse(r.Buffer);
                var root = doc.RootElement;
                string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                lock (_logGate)
                    if (_log is { } lg && (type != "telemetry" || ++_telemetryCount % _logEvery == 0)) lg.WriteLine(Encoding.UTF8.GetString(r.Buffer));
                if (type is "telemetry" or "hello" or "intent") Interlocked.Exchange(ref _heardTicks, Stopwatch.GetTimestamp());

                if (type == "hello")
                {
                    if (GameHello.Parse(root) is not { } h) { RxBad++; continue; }
                    bool announce;
                    lock (_lock)
                    {
                        // log a new game / build, or the same one back after it was gone
                        announce = !h.SameAs(_hello) || Stopwatch.GetElapsedTime(_helloTicks).TotalSeconds > GamePresence.GoneSeconds;
                        _hello = h; _helloTicks = Stopwatch.GetTimestamp();
                    }
                    if (announce) Console.WriteLine("Game connected: " + h.Describe());
                    continue;
                }
                if (type == "shutdown")   // `turbotrainer-bridge stop`: only the headless `run --auto` acts on it
                {
                    if (ShutdownRequested is { } stop) stop();
                    else Console.WriteLine("Asked to stop (turbotrainer-bridge stop): ignored, only a bridge the game started stops on request");
                    continue;
                }
                if (type == "intent")
                {
                    if (ParseIntent(root) is { } it) { _intents.Enqueue(it); _intentArrived.Release(); } else RxBad++;
                    continue;
                }
                if (type != "telemetry") continue;

                bool player = root.TryGetProperty("playerFound", out var pf) && pf.ValueKind == JsonValueKind.True;
                bool riding = root.TryGetProperty("riding", out var rd) ? rd.ValueKind == JsonValueKind.True : player;
                bool active = false; double? grade = null;
                if (root.TryGetProperty("fitness", out var fit))
                {
                    active = fit.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                    if (fit.TryGetProperty("roadGradient", out var g) && g.ValueKind == JsonValueKind.Number) grade = g.GetDouble();
                }
                lock (_lock) { _seen = true; _lastTicks = Stopwatch.GetTimestamp(); _playerFound = player; _fitnessActive = active; _grade = grade; _riding = riding; }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (JsonException) { RxBad++; }
            catch (Exception) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _rx.Dispose();
        lock (_logGate) { _log?.Dispose(); _log = null; }
        _tx.Dispose();
        _sessionTx.Dispose();
        _intentArrived.Dispose();
    }
}
