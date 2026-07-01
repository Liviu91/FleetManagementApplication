using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MauiApp1.Services
{
    /// <summary>
    /// Append-only, on-device diagnostic recorder for the OBD connect/monitor paths.
    ///
    /// THIS TYPE IS OBSERVABILITY ONLY — it never changes connect/reconnect behaviour. It exists so
    /// the intermittent "OBD not available" failure can be root-caused from captured evidence after a
    /// real test session, WITHOUT a debugger/logcat attached.
    ///
    /// Design:
    ///  - Every event is one compact JSON object on its own line (JSONL) appended to
    ///    <see cref="FilePath"/> under <see cref="FileSystem.AppDataDirectory"/>.
    ///  - Writes are decoupled from callers via an unbounded <see cref="Channel{T}"/>: callers only
    ///    serialize (cheap, microseconds) and enqueue (lock-free, non-blocking); a single background
    ///    task owns the file handle and flushes each line to disk immediately. So logging never blocks
    ///    the telemetry poll loop or the UI thread, and records survive a process kill.
    ///  - A small in-memory ring buffer keeps the last N human-readable trace lines (the raw ELM
    ///    dialogue, attempt outcomes, etc.). On a FAILURE episode the ring is flushed INTO the
    ///    episode_end record, so each failure carries the run-up that produced it.
    ///  - Correlation model: every line carries a short app <c>session</c> id, a monotonically
    ///    increasing <c>run</c> counter (one per connect episode), a per-route <c>route</c> id, and a
    ///    per-episode <c>corr</c> id. That lets a fresh session line up "the failures" against "the
    ///    good runs" purely from the exported file.
    ///
    /// Platform-agnostic on purpose (no <c>#if ANDROID</c>) so it can be injected into the non-Android
    /// <c>RoutesPage</c> constructor too and the export button works everywhere; the Android-specific
    /// snapshots are gathered by the caller and passed in as plain values.
    /// </summary>
    public sealed class ObdDiagnostics
    {
        private const int RingCapacity = 100;
        private const int MaxFieldLength = 800;

        // One queue item is either a JSON line to append, a flush request, or a reset request.
        private sealed class WriteItem
        {
            public string? Line;
            public TaskCompletionSource? Flush;
            public TaskCompletionSource? Reset;
        }

        private readonly Channel<WriteItem> _channel =
            Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _writerLoop;

        private readonly object _ringLock = new();
        private readonly Queue<string> _ring = new();

        private int _runCounter;

        /// <summary>Short id for this app process/run. Stamped on every line.</summary>
        public string SessionId { get; private set; }

        /// <summary>Absolute path of the JSONL diagnostics file on the device.</summary>
        public string FilePath { get; }

        /// <summary>Monotonic connect-episode counter (one per <c>BeginConnectEpisode</c>).</summary>
        public int RunCounter => Volatile.Read(ref _runCounter);

        public ObdDiagnostics()
        {
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            FilePath = Path.Combine(FileSystem.AppDataDirectory, "obd_diagnostics.jsonl");
            _writerLoop = Task.Run(WriterLoopAsync);

            // Anchor each app run so episodes can be grouped and ordered after export.
            Event("session_start", data: new
            {
                app_session = SessionId,
                file = FilePath,
                device = SafeDevice(() => $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}"),
                platform = SafeDevice(() => $"{DeviceInfo.Platform} {DeviceInfo.VersionString}"),
                app_version = SafeDevice(() => AppInfo.VersionString),
                local_offset = DateTimeOffset.Now.Offset.ToString()
            });
        }

        // ----------------------------------------------------------------------------------------
        // Correlation ids
        // ----------------------------------------------------------------------------------------

        /// <summary>A new short correlation id (8 hex chars).</summary>
        public static string NewCorrelationId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        // ----------------------------------------------------------------------------------------
        // Connect-episode lifecycle
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Opens a connect episode (initial connect OR a reconnect). Increments the run counter and
        /// records the pre-connect context (adapter/bonding state, prior-socket leak check, timings).
        /// </summary>
        public void BeginConnectEpisode(string corr, string? routeCorr, string trigger, int routeId, object? context)
        {
            int run = Interlocked.Increment(ref _runCounter);
            Trace("EPISODE", $"begin {trigger} run={run} corr={corr} route={routeId}");
            Event("episode_begin", corr: corr, route: routeCorr, run: run, data: Merge(new
            {
                trigger,
                route_id = routeId
            }, context));
        }

        /// <summary>Records the outcome of a single connect attempt (1..4).</summary>
        public void LogAttempt(string corr, string? routeCorr, object attempt)
            => Event("attempt", corr: corr, route: routeCorr, data: attempt);

        /// <summary>Records the ELM init handshake (ATZ/ATE0/ATL0/ATSP0 + VIN/battery) outcome.</summary>
        public void LogInit(string corr, string? routeCorr, string outcome, object init)
            => Event("init", corr: corr, route: routeCorr, outcome: outcome, data: init);

        /// <summary>
        /// Closes a connect episode. On any non-SUCCESS outcome the recent ring-buffer trace lines
        /// are attached so the failure carries its full run-up.
        /// </summary>
        public void EndConnectEpisode(string corr, string? routeCorr, string outcome, object summary)
        {
            object payload = summary;
            if (!string.Equals(outcome, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                payload = Merge(summary, new { recent = RingSnapshot() });

            Trace("EPISODE", $"end corr={corr} outcome={outcome}");
            Event("episode_end", corr: corr, route: routeCorr, outcome: outcome, data: payload);
        }

        // ----------------------------------------------------------------------------------------
        // Monitoring-phase events
        // ----------------------------------------------------------------------------------------

        /// <summary>A single poll cycle failed (no valid frame, or a transport exception).</summary>
        public void PollFailure(string? routeCorr, object data)
            => Event("poll_fail", route: routeCorr, outcome: "FAILURE", data: data);

        /// <summary>A free-form monitoring/lifecycle note (start/stop/reconnect/foreground service).</summary>
        public void MonitorNote(string note, string? routeCorr = null, object? data = null)
            => Event("monitor_note", route: routeCorr, data: Merge(new { note }, data));

        /// <summary>Records an export action so exports are visible in the timeline.</summary>
        public void LogExport(long fileSizeBytes)
            => Event("export", data: new { file = FilePath, size_bytes = fileSizeBytes });

        // ----------------------------------------------------------------------------------------
        // Ring buffer (human-readable trace, mirrored to Debug)
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Adds a short, timestamped line to the in-memory ring buffer (flushed into the next failure
        /// episode) and mirrors it to <see cref="Debug"/>. Cheap and allocation-light; safe to call
        /// from the poll loop.
        /// </summary>
        public void Trace(string tag, string message)
        {
            string line = $"{DateTime.UtcNow:HH:mm:ss.fff}Z [{tag}] {Truncate(message)}";
            lock (_ringLock)
            {
                _ring.Enqueue(line);
                while (_ring.Count > RingCapacity)
                    _ring.Dequeue();
            }
            Debug.WriteLine($"[{tag}] {message}");
        }

        private string[] RingSnapshot()
        {
            lock (_ringLock)
                return _ring.ToArray();
        }

        // ----------------------------------------------------------------------------------------
        // Export helpers
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Ensures every queued line has hit disk and returns the current file size. Safe to await on
        /// the UI thread before sharing/exporting the file.
        /// </summary>
        public async Task<long> FlushAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _channel.Writer.TryWrite(new WriteItem { Flush = tcs });
            try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* best-effort flush */ }

            try { return new FileInfo(FilePath).Length; }
            catch { return 0; }
        }

        /// <summary>
        /// Copies the current working log to a stable snapshot file (in the cache dir) and returns its
        /// path, or null if there is nothing to export. The snapshot is a SEPARATE file, so it can be
        /// shared safely even after <see cref="ResetAsync"/> truncates the working log.
        /// </summary>
        public async Task<string?> CreateExportSnapshotAsync()
        {
            await FlushAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= 0)
                    return null;
                var snapshot = Path.Combine(FileSystem.CacheDirectory, "obd_diagnostics_export.jsonl");
                File.Copy(FilePath, snapshot, overwrite: true);
                return snapshot;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ObdDiagnostics] snapshot failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Starts a FRESH working log: truncates the file, resets the run counter and ring buffer, gives
        /// the log a new session id, and writes a new session_start anchor. Used right after an export
        /// so each exported file contains only the routes captured since the previous export.
        /// </summary>
        public async Task ResetAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _channel.Writer.TryWrite(new WriteItem { Reset = tcs });
            try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* best-effort */ }

            Interlocked.Exchange(ref _runCounter, 0);
            lock (_ringLock) { _ring.Clear(); }
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);

            Event("session_start", data: new
            {
                app_session = SessionId,
                file = FilePath,
                note = "fresh log (previous log exported)",
                local_offset = DateTimeOffset.Now.Offset.ToString()
            });
        }

        // ----------------------------------------------------------------------------------------
        // Static formatting helpers (shared by callers)
        // ----------------------------------------------------------------------------------------

        /// <summary>Single-line, length-capped form of a raw ELM/text response for safe logging.</summary>
        public static string? San(string? s)
        {
            if (s == null) return null;
            return Truncate(s.Replace("\r", "\\r").Replace("\n", "\\n").Trim());
        }

        /// <summary>
        /// Flattens an exception (and its inner-exception chain) into a serializable shape. The chain
        /// is important on Android: the clone's Java "read failed ... read ret: -1" detail lives in the
        /// inner <c>Java.IO.IOException</c>.
        /// </summary>
        public static object? DescribeException(Exception? ex)
        {
            if (ex == null) return null;

            var chain = new List<object>();
            Exception? cur = ex.InnerException;
            int depth = 0;
            while (cur != null && depth++ < 6)
            {
                chain.Add(new { type = cur.GetType().FullName, message = Truncate(cur.Message) });
                cur = cur.InnerException;
            }

            return new
            {
                type = ex.GetType().FullName,
                message = Truncate(ex.Message),
                inner = chain,
                stack = Truncate(ex.StackTrace, 2000)
            };
        }

        // ----------------------------------------------------------------------------------------
        // Core write path
        // ----------------------------------------------------------------------------------------

        private void Event(string evt, string? corr = null, string? route = null, string? outcome = null,
                           int? run = null, object? data = null)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var o = new JObject
                {
                    ["ts_utc"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["ts_local"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    ["evt"] = evt,
                    ["session"] = SessionId
                };
                if (run.HasValue) o["run"] = run.Value;
                if (route != null) o["route"] = route;
                if (corr != null) o["corr"] = corr;
                if (outcome != null) o["outcome"] = outcome;

                if (data != null)
                {
                    foreach (var p in JObject.FromObject(data).Properties())
                        o[p.Name] = p.Value;
                }

                _channel.Writer.TryWrite(new WriteItem { Line = o.ToString(Formatting.None) });
            }
            catch (Exception ex)
            {
                // Diagnostics must never throw into the OBD paths.
                Debug.WriteLine($"[ObdDiagnostics] serialize failed: {ex.Message}");
            }
        }

        private async Task WriterLoopAsync()
        {
            StreamWriter? writer = null;
            try
            {
                writer = OpenWriter();

                await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (item.Line != null)
                    {
                        try { await writer.WriteLineAsync(item.Line).ConfigureAwait(false); }
                        catch (Exception ex) { Debug.WriteLine($"[ObdDiagnostics] write failed: {ex.Message}"); }
                    }

                    if (item.Flush != null)
                    {
                        try { await writer.FlushAsync().ConfigureAwait(false); } catch { }
                        item.Flush.TrySetResult();
                    }

                    if (item.Reset != null)
                    {
                        try
                        {
                            await writer.FlushAsync().ConfigureAwait(false);
                            writer.Dispose();
                            File.WriteAllText(FilePath, string.Empty);   // truncate the working log
                            writer = OpenWriter();
                        }
                        catch (Exception ex) { Debug.WriteLine($"[ObdDiagnostics] reset failed: {ex.Message}"); }
                        item.Reset.TrySetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ObdDiagnostics] writer loop ended: {ex.Message}");
            }
            finally
            {
                try { writer?.Dispose(); } catch { }
            }
        }

        private StreamWriter OpenWriter()
            => new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

        // ----------------------------------------------------------------------------------------
        // Small utilities
        // ----------------------------------------------------------------------------------------

        // Merges a base anonymous object with an optional extra object into a single JObject.
        private static JObject Merge(object @base, object? extra)
        {
            var o = JObject.FromObject(@base);
            if (extra != null)
            {
                foreach (var p in JObject.FromObject(extra).Properties())
                    o[p.Name] = p.Value;
            }
            return o;
        }

        private static string? Truncate(string? s, int max = MaxFieldLength)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…[+" + (s.Length - max) + "]";
        }

        private static string? SafeDevice(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
