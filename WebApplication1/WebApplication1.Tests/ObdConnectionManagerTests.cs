using System;
using System.Threading;
using System.Threading.Tasks;
using MauiApp1.Services;
using Xunit;

namespace WebApplication1.Tests
{
    /// <summary>
    /// Hardware-free reproduction of the OBD Bluetooth stability problem.
    ///
    /// The real failure ("Socket not available" after ~4-5 routes, needing a physical replug)
    /// was caused by the monitor loop leaking an open RFCOMM socket whenever a route finished
    /// while the loop was reconnecting. These tests drive <see cref="ObdConnectionManager"/> with
    /// a fake transport that counts opens/closes, so the leak can be reproduced and the fix
    /// (a guaranteed single close on every exit path) can be asserted with no Bluetooth adapter.
    /// </summary>
    public class ObdConnectionManagerTests
    {
        /// <summary>A stand-in for the Bluetooth socket that records its open/close lifecycle.</summary>
        private sealed class FakeTransport
        {
            private readonly object _lock = new();
            public int OpenCount { get; private set; }
            public int CloseCount { get; private set; }
            public bool IsOpen { get; private set; }

            public void Open()
            {
                lock (_lock) { IsOpen = true; OpenCount++; }
            }

            public void Close()
            {
                lock (_lock) { IsOpen = false; CloseCount++; }
            }

            // Mirrors ObdService.ConnectInternalAsync: drop the stale socket, then open a new one.
            public void Reconnect()
            {
                lock (_lock)
                {
                    if (IsOpen) CloseCount++;
                    IsOpen = true;
                    OpenCount++;
                }
            }
        }

        // Fast, deterministic policy: reconnect after a single failure with a zero-second backoff.
        private static ReconnectPolicy FastPolicy() => new ReconnectPolicy(1, new[] { 0 });

        [Fact]
        public async Task Polls_until_cancelled_then_closes_transport_exactly_once()
        {
            var t = new FakeTransport();
            t.Open();
            int polls = 0;
            using var cts = new CancellationTokenSource();

            var mgr = new ObdConnectionManager(
                reconnectAsync: _ => { t.Reconnect(); return Task.CompletedTask; },
                pollOnceAsync: _ =>
                {
                    if (Interlocked.Increment(ref polls) >= 5) cts.Cancel();
                    return Task.FromResult(true);
                },
                closeTransport: t.Close,
                policy: FastPolicy(),
                pollDelayMs: 0);

            await mgr.RunAsync(cts.Token);

            Assert.False(t.IsOpen);
            Assert.Equal(1, t.CloseCount);   // closed once, at teardown
            Assert.Equal(0, mgr.ReconnectCount);
        }

        [Fact]
        public async Task Reconnects_after_failures()
        {
            var t = new FakeTransport();
            t.Open();
            int polls = 0;
            using var cts = new CancellationTokenSource();

            var mgr = new ObdConnectionManager(
                reconnectAsync: _ => { t.Reconnect(); return Task.CompletedTask; },
                pollOnceAsync: _ =>
                {
                    int n = Interlocked.Increment(ref polls);
                    if (n >= 4) cts.Cancel();
                    return Task.FromResult(n > 2);   // first two reads fail -> force a reconnect
                },
                closeTransport: t.Close,
                policy: FastPolicy(),
                pollDelayMs: 0);

            await mgr.RunAsync(cts.Token);

            Assert.True(mgr.ReconnectCount >= 1);
            Assert.False(t.IsOpen);   // still closed cleanly at the end
        }

        /// <summary>
        /// The exact failure that wedged the adapter: the user taps Finish while the loop is
        /// inside a reconnect. The new socket finishes connecting AFTER the cancel — teardown
        /// must still close it so nothing is left open.
        /// </summary>
        [Fact]
        public async Task Cancelling_during_reconnect_never_leaves_transport_open()
        {
            var t = new FakeTransport();
            t.Open();
            using var cts = new CancellationTokenSource();
            var reconnectStarted = new TaskCompletionSource();
            var allowReconnect = new TaskCompletionSource();

            var mgr = new ObdConnectionManager(
                reconnectAsync: async _ =>
                {
                    reconnectStarted.TrySetResult();
                    await allowReconnect.Task;   // hold the reconnect open until the test releases it
                    t.Reconnect();               // a brand-new socket comes up AFTER Finish
                },
                pollOnceAsync: _ => Task.FromResult(false),   // always fail -> force a reconnect
                closeTransport: t.Close,
                policy: FastPolicy(),
                pollDelayMs: 0);

            var run = mgr.RunAsync(cts.Token);

            await reconnectStarted.Task;   // we are now inside the reconnect
            cts.Cancel();                  // user taps "Finish" mid-reconnect
            allowReconnect.SetResult();    // the new socket finishes connecting

            await run;

            Assert.False(t.IsOpen);        // would be true (leak) with the old loop
            Assert.True(t.CloseCount >= 1);
        }

        [Fact]
        public async Task Each_session_closes_transport_exactly_once_across_many_routes()
        {
            for (int route = 0; route < 5; route++)
            {
                var t = new FakeTransport();
                t.Open();
                int polls = 0;
                using var cts = new CancellationTokenSource();

                var mgr = new ObdConnectionManager(
                    reconnectAsync: _ => { t.Reconnect(); return Task.CompletedTask; },
                    pollOnceAsync: _ =>
                    {
                        if (Interlocked.Increment(ref polls) >= 3) cts.Cancel();
                        return Task.FromResult(true);
                    },
                    closeTransport: t.Close,
                    policy: FastPolicy(),
                    pollDelayMs: 0);

                await mgr.RunAsync(cts.Token);

                Assert.Equal(1, t.CloseCount);   // no socket leaks between routes
                Assert.False(t.IsOpen);
            }
        }

        [Fact]
        public async Task Transport_exception_triggers_reconnect_and_does_not_crash()
        {
            var t = new FakeTransport();
            t.Open();
            int polls = 0;
            using var cts = new CancellationTokenSource();

            var mgr = new ObdConnectionManager(
                reconnectAsync: _ => { t.Reconnect(); return Task.CompletedTask; },
                pollOnceAsync: _ =>
                {
                    int n = Interlocked.Increment(ref polls);
                    if (n == 1) throw new ObdConnectionException("read failed, socket might closed or timeout, read ret: -1");
                    cts.Cancel();
                    return Task.FromResult(true);
                },
                closeTransport: t.Close,
                policy: FastPolicy(),
                pollDelayMs: 0);

            await mgr.RunAsync(cts.Token);   // must not throw out of the loop

            Assert.True(mgr.ReconnectCount >= 1);
            Assert.False(t.IsOpen);
        }
    }
}
