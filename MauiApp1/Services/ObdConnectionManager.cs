using System;
using System.Threading;
using System.Threading.Tasks;

namespace MauiApp1.Services
{
    /// <summary>
    /// Platform-agnostic engine that owns the OBD telemetry session lifecycle:
    /// poll the adapter, and on failure reconnect with a capped backoff. Crucially, it
    /// GUARANTEES the transport (Bluetooth socket) is closed exactly once when the session
    /// ends — whether that is a normal cancel (route finished), an unrecoverable error, or a
    /// cancel that lands in the middle of a reconnect.
    ///
    /// Pulling this loop out of the Android <see cref="ObdService"/> is what makes the
    /// "socket leaks across routes" failure reproducible and verifiable with a fake transport,
    /// without any Bluetooth hardware. The original loop exited without closing the socket when
    /// a Finish raced a reconnect, leaving an orphaned open RFCOMM channel that accumulated over
    /// several routes until the clone adapter refused all connections ("Socket not available").
    /// </summary>
    public sealed class ObdConnectionManager
    {
        private readonly Func<CancellationToken, Task> _reconnectAsync;
        private readonly Func<CancellationToken, Task<bool>> _pollOnceAsync;
        private readonly Action _closeTransport;
        private readonly ReconnectPolicy _policy;
        private readonly int _pollDelayMs;

        public ObdConnectionManager(
            Func<CancellationToken, Task> reconnectAsync,
            Func<CancellationToken, Task<bool>> pollOnceAsync,
            Action closeTransport,
            ReconnectPolicy? policy = null,
            int pollDelayMs = 500)
        {
            _reconnectAsync = reconnectAsync ?? throw new ArgumentNullException(nameof(reconnectAsync));
            _pollOnceAsync = pollOnceAsync ?? throw new ArgumentNullException(nameof(pollOnceAsync));
            _closeTransport = closeTransport ?? throw new ArgumentNullException(nameof(closeTransport));
            _policy = policy ?? new ReconnectPolicy();
            _pollDelayMs = pollDelayMs < 0 ? 0 : pollDelayMs;
        }

        /// <summary>Last transport/reconnect error message, surfaced for diagnostics/UI.</summary>
        public string? LastError { get; private set; }

        /// <summary>How many times this session successfully reconnected the transport.</summary>
        public int ReconnectCount { get; private set; }

        /// <summary>
        /// Current consecutive poll-failure count from the reconnect policy. Read-only passthrough
        /// exposed purely for diagnostics — does not affect the loop's behaviour.
        /// </summary>
        public int ConsecutivePollFailures => _policy.ConsecutiveFailures;

        /// <summary>
        /// Runs the poll/reconnect loop until <paramref name="ct"/> is cancelled. The transport
        /// is always closed exactly once before the returned task completes, even if cancellation
        /// lands while a reconnect is in flight.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool valid;
                    try
                    {
                        valid = await _pollOnceAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;   // session ending – stop polling
                    }
                    catch (ObdConnectionException ex)
                    {
                        LastError = ex.Message;
                        valid = false;   // transport down -> count it and reconnect below
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        valid = false;
                    }

                    if (valid)
                    {
                        _policy.RecordSuccess();
                        if (!await DelayAsync(_pollDelayMs, ct).ConfigureAwait(false))
                            break;
                        continue;
                    }

                    _policy.RecordFailure();
                    if (_policy.ShouldReconnect)
                    {
                        var backoff = _policy.NextBackoff();
                        if (!await DelayAsync((int)backoff.TotalMilliseconds, ct).ConfigureAwait(false))
                            break;

                        try
                        {
                            await _reconnectAsync(ct).ConfigureAwait(false);
                            _policy.RecordSuccess();
                            ReconnectCount++;
                            LastError = null;
                        }
                        catch (OperationCanceledException)
                        {
                            break;   // Finish landed mid-reconnect – the finally below still closes
                        }
                        catch (Exception ex)
                        {
                            LastError = ex.Message;   // keep retrying on the next loop
                        }
                    }
                    else if (!await DelayAsync(_pollDelayMs, ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                // The single source of truth for releasing the socket. Because EVERY exit path
                // funnels through here, a Finish that races a reconnect can never leave an
                // orphaned open RFCOMM channel behind. Close must never throw out of teardown.
                try { _closeTransport(); } catch { /* ignore – teardown is best-effort */ }
            }
        }

        // Returns false when the wait was cancelled (caller should stop), true otherwise.
        private static async Task<bool> DelayAsync(int ms, CancellationToken ct)
        {
            if (ms <= 0)
                return !ct.IsCancellationRequested;

            try
            {
                await Task.Delay(ms, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
