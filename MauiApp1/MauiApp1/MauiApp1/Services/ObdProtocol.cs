using System;
using System.Collections.Generic;
using System.Globalization;

namespace MauiApp1.Services
{
    /// <summary>
    /// Platform-agnostic OBD-II / ELM327 protocol helpers and connection-resilience policy.
    /// Contains NO Android dependencies so it can be unit-tested on any .NET runtime and shared
    /// with the Android <see cref="ObdService"/>.
    /// </summary>
    public static class ObdProtocol
    {
        /// <summary>
        /// Cleans an ELM327 text response and parses the hex payload into bytes.
        /// Strips CR/LF/prompt, "SEARCHING...", "NO DATA", "OK", spaces.
        /// </summary>
        public static byte[] ParseResponse(string? response)
        {
            if (string.IsNullOrEmpty(response))
                return Array.Empty<byte>();

            response = response.Replace("\r", "").Replace("\n", "")
                               .Replace(">", "").Replace(" ", "").Trim();

            response = response.Replace("SEARCHING...", "")
                               .Replace("NODATA", "")
                               .Replace("UNABLETOCONNECT", "")
                               .Replace("STOPPED", "")
                               .Replace("BUSINIT", "")
                               .Replace("ERROR", "")
                               .Replace("OK", "");

            if (string.IsNullOrWhiteSpace(response) || response.Length < 4)
                return Array.Empty<byte>();

            var bytes = new List<byte>();
            for (int i = 0; i + 1 < response.Length; i += 2)
            {
                string hex = response.Substring(i, 2);
                if (byte.TryParse(hex, NumberStyles.HexNumber, null, out byte b))
                    bytes.Add(b);
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// True when the parsed response is a positive Mode-01 reply (0x41) for the given PID
        /// with at least <paramref name="minDataBytes"/> data bytes after the header.
        /// </summary>
        public static bool HasValidFrame(string? raw, byte pid, int minDataBytes = 1)
        {
            var b = ParseResponse(raw);
            return b.Length >= 2 + minDataBytes && b[0] == 0x41 && b[1] == pid;
        }

        // ((A*256)+B)/divisor — RPM(010C /4), MAF(0110 /100)
        public static bool TryParseTwoByteValue(string? raw, byte pid, double divisor, out double value)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == pid)
            {
                value = ((b[2] * 256) + b[3]) / divisor;
                return true;
            }
            value = 0;
            return false;
        }

        public static double ParseTwoByteValue(string? raw, byte pid, double divisor)
            => TryParseTwoByteValue(raw, pid, divisor, out var v) ? v : 0;

        // One-byte direct value: A — Speed(010D), MAP(010B)
        public static double ParseOneByteValue(string? raw, byte pid)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2];
            return 0;
        }

        // One-byte with offset: A + offset — Coolant(0105 -40), IntakeAir(010F -40)
        public static double ParseOneByteOffset(string? raw, byte pid, int offset)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2] + offset;
            return 0;
        }

        // One-byte percentage: A * 100/255 — Throttle(0111), Load(0104), FuelLevel(012F)
        public static double ParseOneBytePercent(string? raw, byte pid)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2] * 100.0 / 255.0;
            return 0;
        }

        // Two-byte raw with multiplier: ((A*256)+B)*multiplier — FuelRail(0123 *0.079)
        public static double ParseTwoByteRaw(string? raw, byte pid, double multiplier)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == pid)
                return ((b[2] * 256) + b[3]) * multiplier;
            return 0;
        }

        // O2 sensor voltage (0114): A/200 volts
        public static double ParseO2Voltage(string? raw)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == 0x14)
                return b[2] / 200.0;
            return 0;
        }

        // Lambda/equivalence ratio (0124): ((A*256)+B)/32768
        public static double ParseLambda(string? raw)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == 0x24)
                return ((b[2] * 256) + b[3]) / 32768.0;
            return 0;
        }

        // Catalyst temperature (013C): ((A*256)+B)/10 - 40
        public static double ParseCatalystTemp(string? raw)
        {
            var b = ParseResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == 0x3C)
                return ((b[2] * 256) + b[3]) / 10.0 - 40;
            return 0;
        }
    }

    /// <summary>
    /// Raised when the OBD transport (Bluetooth socket) is unusable and the caller should
    /// trigger a reconnect rather than treat the result as "no data".
    /// </summary>
    public class ObdConnectionException : Exception
    {
        public ObdConnectionException(string message) : base(message) { }
        public ObdConnectionException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Tracks consecutive read failures and decides when to reconnect and how long to wait,
    /// using a capped backoff ladder. Pure logic so the engine start-stop reconnect behaviour
    /// can be unit-tested without any hardware.
    /// </summary>
    public class ReconnectPolicy
    {
        private readonly int _failuresBeforeReconnect;
        private readonly int[] _backoffSecondsLadder;
        private int _consecutiveFailures;
        private int _reconnectAttempts;

        public ReconnectPolicy(int failuresBeforeReconnect = 3, int[]? backoffSecondsLadder = null)
        {
            if (failuresBeforeReconnect < 1) failuresBeforeReconnect = 1;
            _failuresBeforeReconnect = failuresBeforeReconnect;
            _backoffSecondsLadder = (backoffSecondsLadder is { Length: > 0 })
                ? backoffSecondsLadder
                : new[] { 1, 2, 5, 10, 15 };
        }

        public int ConsecutiveFailures => _consecutiveFailures;
        public int ReconnectAttempts => _reconnectAttempts;

        /// <summary>A valid reading arrived: clear the failure and reconnect-attempt counters.</summary>
        public void RecordSuccess()
        {
            _consecutiveFailures = 0;
            _reconnectAttempts = 0;
        }

        /// <summary>An invalid/empty read or transport error occurred.</summary>
        public void RecordFailure() => _consecutiveFailures++;

        /// <summary>True once failures reach the reconnect threshold.</summary>
        public bool ShouldReconnect => _consecutiveFailures >= _failuresBeforeReconnect;

        /// <summary>
        /// Returns the wait before the next reconnect attempt and advances the ladder.
        /// The delay is capped at the last ladder value so it keeps retrying forever
        /// (e.g. while waiting for the engine/ignition to come back).
        /// </summary>
        public TimeSpan NextBackoff()
        {
            int idx = Math.Min(_reconnectAttempts, _backoffSecondsLadder.Length - 1);
            _reconnectAttempts++;
            return TimeSpan.FromSeconds(_backoffSecondsLadder[idx]);
        }
    }
}
