using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DescendersCyclingPOC
{
    /// <summary>Latest {"type":"trainer"} state message from the bridge (latest-wins, not queued).</summary>
    internal struct TrainerSample
    {
        public double PowerW, CadenceRpm, HeartRateBpm;
        public bool HasCadence;   // the message carried a cadence (absent = no reading, which is not 0 rpm)
        public bool Connected;
        public long RecvTicks;   // DateTime.UtcNow.Ticks when the mod received it
    }

    internal sealed class BridgeCommand
    {
        public string Name;
        public double? Value;
        public double? DurationSec;
        public string Raw;
    }

    /// <summary>
    /// Localhost UDP link to the trainer bridge. No game-thread work: two background threads.
    ///   TX: sends the latest telemetry JSON (and queued replies) to 127.0.0.1:TelemetryPort.
    ///   RX: listens on 127.0.0.1:CommandPort, parses JSON, queues commands for the game thread.
    /// UDP means the bridge can start/stop any time; nothing here connects, blocks or needs admin rights.
    /// Sockets are rebuilt with back-off after any error.
    /// </summary>
    internal sealed class BridgeLink
    {
        private readonly int _telemetryPort, _commandPort, _intervalMs;
        // Unity 2017's Mono has no ConcurrentQueue; plain queues guarded by _qLock.
        private readonly Queue<BridgeCommand> _commands = new Queue<BridgeCommand>();
        private readonly Queue<string> _outbox = new Queue<string>();
        private readonly object _qLock = new object();
        private readonly ManualResetEvent _stopEvt = new ManualResetEvent(false);
        private readonly AutoResetEvent _dataEvt = new AutoResetEvent(false);   // wakes TX when a message is queued
        private Thread _tx, _rx;
        private volatile bool _stop;
        private Socket _rxSock;

        public long TxCount, TxErrors, RxCount, RxBad;
        public volatile bool RxBound;
        private long _lastRxTicks;
        private TrainerSample _trainer;
        private bool _haveTrainer;

        /// <summary>Latest trainer state (false until the first message). Written by the RX thread, read by the game thread.</summary>
        public bool TryGetTrainer(out TrainerSample s)
        {
            Monitor.Enter(_qLock);
            try { s = _trainer; return _haveTrainer; }
            finally { Monitor.Exit(_qLock); }
        }

        private string _statusState = "", _statusText = "";
        private long _statusTicks;

        /// <summary>Latest {"type":"status"} from the bridge ("searching", "Searching for trainer"), if one arrived in the last <paramref name="maxAgeSec"/> s.</summary>
        public bool TryGetStatus(double maxAgeSec, out string state, out string text)
        {
            Monitor.Enter(_qLock);
            try
            {
                state = _statusState; text = _statusText;
                return _statusTicks != 0 && (DateTime.UtcNow.Ticks - _statusTicks) / (double)TimeSpan.TicksPerSecond <= maxAgeSec;
            }
            finally { Monitor.Exit(_qLock); }
        }

        private SessionView _session;
        private long _sessionTicks;

        /// <summary>Latest {"type":"session"} from the bridge, if one arrived in the last <paramref name="maxAgeSec"/> s. Latest-wins; the view is never mutated after parsing.</summary>
        public bool TryGetSession(double maxAgeSec, out SessionView view)
        {
            Monitor.Enter(_qLock);
            try
            {
                view = _session;
                return view != null && (DateTime.UtcNow.Ticks - _sessionTicks) / (double)TimeSpan.TicksPerSecond <= maxAgeSec;
            }
            finally { Monitor.Exit(_qLock); }
        }

        /// <summary>A button press for the bridge's session (docs/IPC.md `intent`), sent on the telemetry port.</summary>
        public void SendIntent(string name, double? value)
        {
            var w = new JsonWriter().Obj().Prop("type", "intent").Prop("name", name);
            if (value.HasValue) w.Prop("value", (float)value.Value);
            Reply(w.End().ToString());
        }

        private float _riderKg, _bikeKg, _ftpW, _cdA = float.NaN, _crr = float.NaN;
        private string _bikeType;
        private bool _haveRider;

        /// <summary>Latest {"type":"rider"} profile from the bridge (false until the first one). The game thread applies it (Config.ApplyBridgeRider).
        /// Bike fields absent from an older bridge: <paramref name="bikeType"/> null, <paramref name="cdA"/> / <paramref name="crr"/> NaN.</summary>
        public bool TryGetRider(out float riderKg, out float bikeKg, out float ftpW, out string bikeType, out float cdA, out float crr)
        {
            Monitor.Enter(_qLock);
            try { riderKg = _riderKg; bikeKg = _bikeKg; ftpW = _ftpW; bikeType = _bikeType; cdA = _cdA; crr = _crr; return _haveRider; }
            finally { Monitor.Exit(_qLock); }
        }

        private string _hello;
        private long _nextHelloTicks;

        /// <summary>The `hello` message (docs/IPC.md, <see cref="Hello"/>): sent on the telemetry port at once and then every
        /// <see cref="Hello.IntervalSeconds"/> s, so a bridge started after the game still learns which game is connected.</summary>
        public void SetHello(string json)
        {
            Monitor.Enter(_qLock); try { _hello = json; _nextHelloTicks = 0; } finally { Monitor.Exit(_qLock); }
            _dataEvt.Set();
        }

        private bool TakeHelloDue(out string json)
        {
            Monitor.Enter(_qLock);
            try
            {
                json = _hello;
                long now = DateTime.UtcNow.Ticks;
                if (json == null || now < _nextHelloTicks) return false;
                _nextHelloTicks = now + Hello.IntervalSeconds * TimeSpan.TicksPerSecond;
                return true;
            }
            finally { Monitor.Exit(_qLock); }
        }

        public BridgeLink(int telemetryPort, int commandPort, int hz)
        {
            _telemetryPort = telemetryPort; _commandPort = commandPort;
            _intervalMs = Math.Max(1, 1000 / Math.Max(1, hz));
        }

        /// <summary>Seconds since the last valid message arrived from the bridge (-1 = never).</summary>
        public double SecondsSinceLastRx
        {
            get
            {
                long t = Interlocked.Read(ref _lastRxTicks);
                return t == 0 ? -1 : (DateTime.UtcNow.Ticks - t) / (double)TimeSpan.TicksPerSecond;
            }
        }

        public void Start()
        {
            _tx = new Thread(TxLoop) { IsBackground = true, Name = "CyclingPOC-UDP-TX" };
            _rx = new Thread(RxLoop) { IsBackground = true, Name = "CyclingPOC-UDP-RX" };
            _tx.Start(); _rx.Start();
            Log.Info("Bridge: UDP started. Telemetry -> 127.0.0.1:" + _telemetryPort + " @ " + (1000 / _intervalMs) + " Hz; commands <- 127.0.0.1:" + _commandPort);
        }

        public void Stop()
        {
            if (_stop) return;
            _stop = true;
            _stopEvt.Set();
            try { var s = _rxSock; if (s != null) s.Close(); } catch (Exception) { /* unblocks the receive; nothing to report */ }
            try { if (_tx != null && !_tx.Join(1000)) Log.Warn("Bridge: TX thread did not stop in 1s"); } catch (Exception) { }
            try { if (_rx != null && !_rx.Join(1000)) Log.Warn("Bridge: RX thread did not stop in 1s"); } catch (Exception) { }
            Log.Info("Bridge: stopped (tx=" + TxCount + " txErr=" + TxErrors + " rx=" + RxCount + " rxBad=" + RxBad + ")");
        }

        // Telemetry is queued (not "latest wins") and sent exactly once per snapshot, in order.
        public void PublishTelemetry(string json) { Reply(json); }
        public void Reply(string json)
        {
            Monitor.Enter(_qLock);
            try { if (_outbox.Count < 200) _outbox.Enqueue(json); } finally { Monitor.Exit(_qLock); }
            _dataEvt.Set();
        }

        public bool TryDequeue(out BridgeCommand c)
        {
            Monitor.Enter(_qLock); try { if (_commands.Count > 0) { c = _commands.Dequeue(); return true; } } finally { Monitor.Exit(_qLock); }
            c = null; return false;
        }

        private bool TryDequeueReply(out string s)
        {
            Monitor.Enter(_qLock); try { if (_outbox.Count > 0) { s = _outbox.Dequeue(); return true; } } finally { Monitor.Exit(_qLock); }
            s = null; return false;
        }

        // ---------------------------------------------------------------- TX
        private void TxLoop()
        {
            Socket sock = null;
            var ep = new IPEndPoint(IPAddress.Loopback, _telemetryPort);
            int backoffMs = 500;
            bool wasFailing = false;
            while (!_stop)
            {
                try
                {
                    if (sock == null)
                    {
                        sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));   // not left to Windows, which binds an unbound sender to 0.0.0.0
                        sock.Blocking = true;
                        if (wasFailing) Log.Info("Bridge: TX socket recreated");
                    }
                    string msg;
                    if (TakeHelloDue(out msg)) Send(sock, ep, msg);
                    while (TryDequeueReply(out msg)) Send(sock, ep, msg);
                    wasFailing = false; backoffMs = 500;
                    if (WaitHandle.WaitAny(new WaitHandle[] { _stopEvt, _dataEvt }, 500) == 0) break;
                }
                catch (Exception ex)
                {
                    if (_stop) break;
                    Interlocked.Increment(ref TxErrors);
                    wasFailing = true;
                    Log.WarnLimited("txfail", "Bridge: TX error (" + ex.GetType().Name + ": " + ex.Message + "); recreating socket in " + backoffMs + " ms", 15f);
                    try { if (sock != null) sock.Close(); } catch (Exception) { }
                    sock = null;
                    if (_stopEvt.WaitOne(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 10000);
                }
            }
            try { if (sock != null) sock.Close(); } catch (Exception) { }
        }

        private void Send(Socket sock, EndPoint ep, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            sock.SendTo(b, ep);
            Interlocked.Increment(ref TxCount);
        }

        // ---------------------------------------------------------------- RX
        private void RxLoop()
        {
            int backoffMs = 1000;
            var buf = new byte[32768];   // a `session` message with 30 upcoming rows and 8 text events is a few kB
            while (!_stop)
            {
                Socket sock = null;
                try
                {
                    sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    try { sock.IOControl(-1744830452, new byte[] { 0 }, null); } catch (Exception) { /* SIO_UDP_CONNRESET: best effort */ }
                    sock.ExclusiveAddressUse = true;
                    sock.Bind(new IPEndPoint(IPAddress.Loopback, _commandPort));
                    sock.ReceiveTimeout = 500;
                    _rxSock = sock;
                    RxBound = true;
                    backoffMs = 1000;
                    Log.Info("Bridge: command socket bound to 127.0.0.1:" + _commandPort);

                    while (!_stop)
                    {
                        int n;
                        try { n = sock.Receive(buf); }
                        catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut) { continue; }
                        catch (SocketException se) when (se.SocketErrorCode == SocketError.MessageSize)
                        {
                            // one oversized datagram (truncated, dropped): not a broken socket, so no rebind (that left the menu stale for 1 s+)
                            Interlocked.Increment(ref RxBad);
                            Log.WarnLimited("rxsize", "Bridge: a message larger than " + buf.Length + " bytes was dropped", 15f);
                            continue;
                        }
                        Handle(Encoding.UTF8.GetString(buf, 0, n));
                    }
                }
                catch (Exception ex)
                {
                    if (_stop) break;
                    RxBound = false;
                    Log.WarnLimited("rxfail", "Bridge: RX error (" + ex.GetType().Name + ": " + ex.Message + "); rebinding in " + backoffMs + " ms", 15f);
                    if (_stopEvt.WaitOne(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 10000);
                }
                finally
                {
                    RxBound = false;
                    try { if (sock != null) sock.Close(); } catch (Exception) { }
                    _rxSock = null;
                }
            }
        }

        private void Handle(string text)
        {
            try
            {
                var root = JsonReader.Parse(text) as Dictionary<string, object>;
                if (root == null) throw new FormatException("top-level JSON value is not an object");
                object o;
                string msgType = root.TryGetValue("type", out o) ? o as string : null;
                if (msgType == "trainer")
                {
                    var ts = new TrainerSample { Connected = true, RecvTicks = DateTime.UtcNow.Ticks };
                    if (root.TryGetValue("powerW", out o) && o is double) ts.PowerW = (double)o; else throw new FormatException("trainer message needs numeric 'powerW'");
                    if (root.TryGetValue("cadenceRpm", out o) && o is double) { ts.CadenceRpm = (double)o; ts.HasCadence = true; }
                    if (root.TryGetValue("heartRateBpm", out o) && o is double) ts.HeartRateBpm = (double)o;
                    if (root.TryGetValue("connected", out o) && o is bool) ts.Connected = (bool)o;
                    Monitor.Enter(_qLock); try { _trainer = ts; _haveTrainer = true; } finally { Monitor.Exit(_qLock); }
                    Interlocked.Exchange(ref _lastRxTicks, ts.RecvTicks);
                    Interlocked.Increment(ref RxCount);
                    return;
                }
                if (msgType == "status")
                {
                    string st = root.TryGetValue("state", out o) ? o as string : null, tx = root.TryGetValue("text", out o) ? o as string : null;
                    Monitor.Enter(_qLock); try { _statusState = st ?? ""; _statusText = tx ?? ""; _statusTicks = DateTime.UtcNow.Ticks; } finally { Monitor.Exit(_qLock); }
                    Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref RxCount);
                    return;
                }
                if (msgType == "session")
                {
                    SessionView sv = SessionView.Parse(root);
                    Monitor.Enter(_qLock); try { _session = sv; _sessionTicks = DateTime.UtcNow.Ticks; } finally { Monitor.Exit(_qLock); }
                    Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref RxCount);
                    return;
                }
                if (msgType == "rider")
                {
                    double r, b, f;
                    if (!root.TryGetValue("riderWeightKg", out o) || !(o is double)) throw new FormatException("rider message needs numeric 'riderWeightKg'");
                    r = (double)o;
                    if (!root.TryGetValue("bikeWeightKg", out o) || !(o is double)) throw new FormatException("rider message needs numeric 'bikeWeightKg'");
                    b = (double)o;
                    if (!root.TryGetValue("ftpW", out o) || !(o is double)) throw new FormatException("rider message needs numeric 'ftpW'");
                    f = (double)o;
                    // bike preset: optional, an older bridge sends none (the mod then uses the road preset)
                    string bt = root.TryGetValue("bikeType", out o) ? o as string : null;
                    float cd = root.TryGetValue("cdA", out o) && o is double ? (float)(double)o : float.NaN;
                    float cr = root.TryGetValue("crr", out o) && o is double ? (float)(double)o : float.NaN;
                    Monitor.Enter(_qLock); try { _riderKg = (float)r; _bikeKg = (float)b; _ftpW = (float)f; _bikeType = bt; _cdA = cd; _crr = cr; _haveRider = true; } finally { Monitor.Exit(_qLock); }
                    Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref RxCount);
                    return;
                }
                var c = new BridgeCommand { Raw = text };
                if (root.TryGetValue("name", out o)) c.Name = o as string;
                if (root.TryGetValue("value", out o) && o is double) c.Value = (double)o;
                if (root.TryGetValue("durationSec", out o) && o is double) c.DurationSec = (double)o;
                string type = root.TryGetValue("type", out o) ? o as string : null;
                if (type != "command" || string.IsNullOrEmpty(c.Name)) throw new FormatException("expected {\"type\":\"command\",\"name\":...}");
                Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref RxCount);
                Monitor.Enter(_qLock); try { if (_commands.Count < 64) _commands.Enqueue(c); } finally { Monitor.Exit(_qLock); }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref RxBad);
                Log.WarnLimited("rxbad", "Bridge: ignoring bad message (" + ex.Message + "): " + Trunc(text, 120), 5f);
            }
        }

        private static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "..."; }
    }
}
