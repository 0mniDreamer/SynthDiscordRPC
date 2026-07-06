using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SynthDiscordRPC.Discord
{
    /// <summary>
    /// The presence we want Discord to show. Null fields are omitted from the payload.
    /// </summary>
    public sealed class PresenceData
    {
        public string Details;          // top line   (e.g. song title)
        public string State;            // second line (e.g. "by Artist [Master]")
        public long? StartTimestampMs;  // unix ms; with no end set, Discord renders "elapsed"
        public long? EndTimestampMs;    // unix ms; when set, Discord renders a live countdown
        public string LargeImageKey;    // asset key OR a public https:// URL (e.g. cover art)
        public string LargeText;        // hover text on the large image
        public string SmallImageKey;    // corner overlay (e.g. game logo when cover art is shown)
        public string SmallText;        // hover text on the small image

        public PresenceData Clone() => (PresenceData)MemberwiseClone();
    }

    /// <summary>
    /// Minimal Discord Rich Presence client speaking the local IPC protocol directly
    /// over a named pipe (discord-ipc-0 .. discord-ipc-9). No Discord Game SDK, no
    /// native DLLs, no game/Unity references — pure .NET 6 BCL.
    ///
    /// Threading model:
    ///   - SetPresence()/ClearPresence() may be called from ANY thread (they only
    ///     touch a lock-guarded "desired state" slot and signal the worker).
    ///   - A single background worker thread owns connect/handshake/writes.
    ///   - A reader task drains Discord's responses and answers PINGs.
    ///   - Pipe death (Discord closed/restarted) => automatic reconnect with backoff,
    ///     and the current desired presence is re-sent on reconnect.
    /// </summary>
    public sealed class DiscordIpcClient : IDisposable
    {
        // Frame opcodes (Discord IPC protocol)
        private const int OpHandshake = 0;
        private const int OpFrame     = 1;
        private const int OpClose     = 2;
        private const int OpPing      = 3;
        private const int OpPong      = 4;

        private const int ReconnectDelayMs = 5000;

        // Discord allows ~5 presence updates per 20s; we cap at 4 in a sliding window
        // so a song-start burst (initial presence + cover-art follow-up) is immediate.
        private const int RateWindowMs = 20000;
        private const int RateMaxWrites = 4;

        private readonly string _clientId;
        private readonly Action<string> _log;
        private readonly Action<string> _logWarn;
        private readonly bool _debug;

        private readonly object _stateLock = new object();
        private PresenceData _desired;     // latest requested presence (null = clear)
        private bool _dirty;               // desired differs from what Discord last got
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private readonly object _writeLock = new object(); // writer thread + PONG replies share the pipe
        private NamedPipeClientStream _pipe;
        private Thread _worker;
        private volatile bool _disposed;
        private volatile bool _readerFailed;
        private readonly Queue<long> _writeTicks = new Queue<long>();

        public bool IsConnected { get; private set; }

        public DiscordIpcClient(string clientId, Action<string> log, Action<string> logWarn, bool debug)
        {
            _clientId = clientId;
            _log = log ?? (_ => { });
            _logWarn = logWarn ?? (_ => { });
            _debug = debug;
        }

        public void Start()
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop)
            {
                Name = "SynthDiscordRPC-IPC",
                IsBackground = true
            };
            _worker.Start();
        }

        /// <summary>Request a presence update. Thread-safe; last write wins.</summary>
        public void SetPresence(PresenceData presence)
        {
            lock (_stateLock)
            {
                _desired = presence;
                _dirty = true;
            }
            _signal.Set();
        }

        /// <summary>Request the presence be cleared. Thread-safe.</summary>
        public void ClearPresence() => SetPresence(null);

        // ------------------------------------------------------------------
        // Worker
        // ------------------------------------------------------------------

        private void WorkerLoop()
        {
            while (!_disposed)
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = TryConnectAnyPipe();
                    if (pipe == null)
                    {
                        if (_debug) _log("Discord not detected (no IPC pipe). Retrying in 5s.");
                        SleepInterruptible(ReconnectDelayMs);
                        continue;
                    }

                    _pipe = pipe;
                    SendHandshake(pipe);

                    _readerFailed = false;
                    var readerTask = Task.Run(() => ReaderLoop(pipe));

                    IsConnected = true;
                    _log("Connected to Discord.");

                    // Force a (re)send of whatever presence is currently desired.
                    lock (_stateLock) { _dirty = true; }
                    _writeTicks.Clear();

                    // Write loop: wake on signal or every second to re-check state.
                    while (!_disposed && !_readerFailed)
                    {
                        _signal.WaitOne(1000);
                        if (_disposed || _readerFailed) break;

                        PresenceData toSend = null;
                        bool send = false;
                        lock (_stateLock)
                        {
                            if (_dirty && CanWriteNow())
                            {
                                // The slot always holds the LATEST state; if the window
                                // is full we leave dirty set and re-check next wake.
                                toSend = _desired;
                                _dirty = false;
                                send = true;
                            }
                        }

                        if (send)
                        {
                            WriteFrame(pipe, OpFrame, BuildSetActivity(toSend));
                            _writeTicks.Enqueue(Environment.TickCount64);
                            if (_debug) _log(toSend == null ? "Presence cleared." : $"Presence set: {toSend.Details} / {toSend.State}");
                        }
                    }

                    IsConnected = false;
                    try { pipe.Dispose(); } catch { }
                    try { readerTask.Wait(1000); } catch { }

                    if (!_disposed)
                    {
                        _logWarn("Lost connection to Discord; will reconnect.");
                        SleepInterruptible(ReconnectDelayMs);
                    }
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    try { pipe?.Dispose(); } catch { }
                    if (!_disposed)
                    {
                        if (_debug) _logWarn($"IPC worker error: {ex.Message}");
                        SleepInterruptible(ReconnectDelayMs);
                    }
                }
            }

            IsConnected = false;
        }

        /// <summary>Sliding-window rate check; only called from the worker thread.</summary>
        private bool CanWriteNow()
        {
            long now = Environment.TickCount64;
            while (_writeTicks.Count > 0 && now - _writeTicks.Peek() > RateWindowMs)
                _writeTicks.Dequeue();
            return _writeTicks.Count < RateMaxWrites;
        }

        private void SleepInterruptible(int ms)
        {
            // Sleep in small slices so Dispose() doesn't hang the thread join.
            int waited = 0;
            while (!_disposed && waited < ms)
            {
                Thread.Sleep(100);
                waited += 100;
            }
        }

        private NamedPipeClientStream TryConnectAnyPipe()
        {
            for (int i = 0; i <= 9; i++)
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(
                        ".", $"discord-ipc-{i}",
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(500);
                    return pipe;
                }
                catch
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }
            return null;
        }

        private void SendHandshake(NamedPipeClientStream pipe)
        {
            string payload = "{\"v\":1,\"client_id\":" + Json.Str(_clientId) + "}";
            WriteFrame(pipe, OpHandshake, payload);
        }

        // ------------------------------------------------------------------
        // Reader — drains responses, answers PINGs, detects pipe death
        // ------------------------------------------------------------------

        private void ReaderLoop(NamedPipeClientStream pipe)
        {
            try
            {
                var header = new byte[8];
                while (!_disposed && pipe.IsConnected)
                {
                    if (!ReadExactly(pipe, header, 8)) break;

                    int op = BitConverter.ToInt32(header, 0);
                    int len = BitConverter.ToInt32(header, 4);
                    if (len < 0 || len > 1024 * 1024) break; // insane length => corrupt stream

                    var body = new byte[len];
                    if (!ReadExactly(pipe, body, len)) break;

                    if (op == OpPing)
                    {
                        // Echo payload back as PONG to keep the connection alive.
                        WriteFrame(pipe, OpPong, Encoding.UTF8.GetString(body));
                    }
                    else if (op == OpClose)
                    {
                        if (_debug) _log("Discord sent CLOSE: " + Encoding.UTF8.GetString(body));
                        break;
                    }
                    else if (_debug && op == OpFrame)
                    {
                        string text = Encoding.UTF8.GetString(body);
                        // Only surface errors; READY / SET_ACTIVITY echoes are noise.
                        if (text.Contains("\"evt\":\"ERROR\""))
                            _logWarn("Discord IPC error response: " + text);
                    }
                }
            }
            catch
            {
                // fall through — treated as disconnect
            }
            finally
            {
                _readerFailed = true;
                _signal.Set(); // wake the writer so it notices
            }
        }

        private static bool ReadExactly(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Frame + payload construction
        // ------------------------------------------------------------------

        private void WriteFrame(NamedPipeClientStream pipe, int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);          // little-endian on all our targets
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);

            lock (_writeLock)
            {
                pipe.Write(frame, 0, frame.Length);
                pipe.Flush();
            }
        }

        private string BuildSetActivity(PresenceData p)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":")
              .Append(Environment.ProcessId)
              .Append(",\"activity\":");

            if (p == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('{');
                bool first = true;

                string details = Sanitize(p.Details);
                if (details != null)
                {
                    sb.Append("\"details\":").Append(Json.Str(details));
                    first = false;
                }

                string state = Sanitize(p.State);
                if (state != null)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"state\":").Append(Json.Str(state));
                    first = false;
                }

                if (p.StartTimestampMs.HasValue || p.EndTimestampMs.HasValue)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"timestamps\":{");
                    if (p.StartTimestampMs.HasValue)
                        sb.Append("\"start\":").Append(p.StartTimestampMs.Value);
                    if (p.EndTimestampMs.HasValue)
                    {
                        if (p.StartTimestampMs.HasValue) sb.Append(',');
                        sb.Append("\"end\":").Append(p.EndTimestampMs.Value);
                    }
                    sb.Append('}');
                    first = false;
                }

                bool hasLarge = !string.IsNullOrEmpty(p.LargeImageKey);
                bool hasSmall = !string.IsNullOrEmpty(p.SmallImageKey);
                if (hasLarge || hasSmall)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"assets\":{");
                    bool assetFirst = true;
                    if (hasLarge)
                    {
                        sb.Append("\"large_image\":").Append(Json.Str(p.LargeImageKey));
                        string largeText = Sanitize(p.LargeText);
                        if (largeText != null)
                            sb.Append(",\"large_text\":").Append(Json.Str(largeText));
                        assetFirst = false;
                    }
                    if (hasSmall)
                    {
                        if (!assetFirst) sb.Append(',');
                        sb.Append("\"small_image\":").Append(Json.Str(p.SmallImageKey));
                        string smallText = Sanitize(p.SmallText);
                        if (smallText != null)
                            sb.Append(",\"small_text\":").Append(Json.Str(smallText));
                    }
                    sb.Append('}');
                }

                sb.Append('}');
            }

            sb.Append("},\"nonce\":").Append(Json.Str(Guid.NewGuid().ToString())).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Discord requires string fields to be 2..128 chars.
        /// Returns null (omit field) for empty; pads 1-char strings; truncates long ones.
        /// </summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Length == 1) return s + " ";
            if (s.Length > 128) return s.Substring(0, 128);
            return s;
        }

        // ------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;

            // Best effort: clear the presence before dropping the pipe.
            try
            {
                var pipe = _pipe;
                if (pipe != null && pipe.IsConnected)
                    WriteFrame(pipe, OpFrame, BuildSetActivity(null));
            }
            catch { }

            _disposed = true;
            _signal.Set();
            try { _pipe?.Dispose(); } catch { }
            try { _worker?.Join(1500); } catch { }
            _signal.Dispose();
        }
    }
}
