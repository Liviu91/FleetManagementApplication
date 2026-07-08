using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using MauiApp1.Model;

#if ANDROID
using Android.Bluetooth;
using Java.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MauiApp1.Services
{
    public class ObdService
    {
        private readonly RabbitMqService _rabbitMqService;
        private BluetoothAdapter? _bluetoothAdapter;
        private BluetoothSocket? _socket;
        private BluetoothDevice? _device;
        private readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB");

        private string? _cachedVin;
        private string? _cachedFuelType;
        private string? _cachedBatteryVoltage;

        // Connection / loop resilience state
        private const int ConnectTimeoutMs = 8000;     // hard cap so a wedged adapter can't block a connect forever
        private string _preferredDeviceName = "OBDII";
        private Task? _monitorTask;
        private CancellationTokenSource? _sessionCts;
        private ObdConnectionManager? _manager;

        // Serializes the public connect/teardown lifecycle so a fast Finish->Start (or a double Start)
        // can never run two connect loops over the same _socket at once. Without it a route's 4-attempt
        // connect could still be looping when the next route's Start began, and the two would clobber
        // each other's socket.
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

        // Per-session telemetry cache (slow PIDs persist between poll cycles)
        private int _cycle;
        private double _throttle, _load, _intakeTemp, _maf, _mapVal;
        private double _fuelPressure, _o2voltage, _lambda, _catalystTemp, _fuelLevel;

        // Public properties to expose current OBD values
        public double CurrentRpm { get; private set; }
        public double CurrentSpeed { get; private set; }
        public double CurrentTemperature { get; private set; }
        public DateTime LastUpdate { get; private set; }
        public int CurrentRouteId { get; set; }
        public double LastLatitude { get; set; }
        public double LastLongitude { get; set; }

        public ObdService(RabbitMqService rabbitMqService)
        {
            _rabbitMqService = rabbitMqService;
#pragma warning disable CS0618 // Type or member is obsolete
            _bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
#pragma warning restore CS0618 // Type or member is obsolete
        }

        public async Task Connect(string deviceName = "OBDII")
        {
            // Promptly signal any previous in-flight session to abort so it releases the gate quickly,
            // then serialize: only ONE connect/teardown runs at a time. This removes the connect race
            // where two ConnectInternalAsync loops could fight over _socket.
            CancelCurrentSession();
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await TeardownSessionAsync().ConfigureAwait(false);   // finish unwinding the previous session

                _preferredDeviceName = deviceName;
                _sessionCts = new CancellationTokenSource();
                await ConnectInternalAsync(_sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        // Robust connect: resolve the paired adapter, cancel discovery, and retry a few times.
        // Later attempts use the reflection createRfcommSocket(1) fallback — the well-known
        // workaround for ELM327 clones that fail the secure-UUID socket with
        // "read failed, socket might closed or timeout, read ret: -1".
        private async Task ConnectInternalAsync(CancellationToken ct = default, int maxAttempts = 4,
                                                string trigger = "initial-connect")
        {
            if (_bluetoothAdapter == null)
                throw new ObdConnectionException("No Bluetooth adapter found");
            if (!_bluetoothAdapter.IsEnabled)
                throw new ObdConnectionException("Bluetooth is not enabled");

            ResolveDevice(_preferredDeviceName);

            // Discovery is heavyweight and breaks RFCOMM connects — always stop it first.
            if (_bluetoothAdapter.IsDiscovering)
                _bluetoothAdapter.CancelDiscovery();

            Exception? last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();       // route ended — abandon the connect, no orphan socket
                SafeCloseSocket();                       // never reuse a half-open socket
                await Task.Delay(attempt == 1 ? 200 : 600, ct);

                try
                {
                    if (_bluetoothAdapter.IsDiscovering)
                        _bluetoothAdapter.CancelDiscovery();

                    _socket = attempt < 3
                        ? _device!.CreateRfcommSocketToServiceRecord(SppUuid)   // preferred
                        : CreateReflectionSocket(_device!);                     // fallback

                    // Hard timeout: a wedged clone can otherwise block ConnectAsync indefinitely,
                    // which is what produced the "Bluetooth blinks several times then hangs" symptom.
                    await _socket!.ConnectAsync().WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), ct);

                    if (_socket.IsConnected)
                    {
                        Debug.WriteLine($"[OBD] Connected to {_device!.Name} on attempt {attempt}");
                        await InitializeElmAsync();
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    SafeCloseSocket();                   // session ending — leave nothing half-open
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    SafeCloseSocket();                   // release the failed/stuck socket immediately
                    Debug.WriteLine($"[OBD] Connect attempt {attempt} failed: {ex.Message}");
                }
            }

            SafeCloseSocket();
            throw new ObdConnectionException(
                $"Failed to connect to OBD after {maxAttempts} attempts: {last?.Message}");
        }

        private void ResolveDevice(string deviceName)
        {
            var pairedDevices = _bluetoothAdapter!.BondedDevices;

            // Try exact name first, then partial match on common OBD adapter names
            _device = pairedDevices?.FirstOrDefault(d => d.Name == deviceName)
                   ?? pairedDevices?.FirstOrDefault(d => d.Name != null &&
                        (d.Name.Contains("OBD", StringComparison.OrdinalIgnoreCase) ||
                         d.Name.Contains("ELM", StringComparison.OrdinalIgnoreCase) ||
                         d.Name.Contains("V-LINK", StringComparison.OrdinalIgnoreCase) ||
                         d.Name.Contains("VEEPEAK", StringComparison.OrdinalIgnoreCase)));

            if (_device == null)
            {
                var names = pairedDevices != null
                    ? string.Join(", ", pairedDevices.Where(d => d.Name != null).Select(d => d.Name))
                    : "none";
                throw new ObdConnectionException($"OBD device not found. Paired devices: {names}");
            }
        }

        // BluetoothDevice.createRfcommSocket(int channel) is a hidden API; invoke via reflection.
        private BluetoothSocket CreateReflectionSocket(BluetoothDevice device)
        {
            var intType = Java.Lang.Integer.Type!;
            var method = device.Class.GetMethod("createRfcommSocket", new[] { intType });
            var socket = method!.Invoke(device, Java.Lang.Integer.ValueOf(1));
            return (BluetoothSocket)socket!;
        }

        private async Task InitializeElmAsync()
        {
            await Task.Delay(500);              // let the adapter settle
            await SendObdCommand("ATZ");        // reset
            await Task.Delay(1000);
            await SendObdCommand("ATE0");       // echo off
            await Task.Delay(100);
            await SendObdCommand("ATL0");       // linefeeds off
            await Task.Delay(100);
            await SendObdCommand("ATSP0");      // auto protocol — re-establishes the bus after an engine restart
            await Task.Delay(100);
            Debug.WriteLine("[OBD] ELM327 initialized");

            // Static values — refresh on (re)connect but keep the previous value if a read fails.
            _cachedVin = await ReadVin() ?? _cachedVin;
            _cachedFuelType = await ReadFuelType() ?? _cachedFuelType;
            _cachedBatteryVoltage = await ReadBatteryVoltage() ?? _cachedBatteryVoltage;
            Debug.WriteLine($"[OBD] VIN:{_cachedVin} Fuel:{_cachedFuelType} Batt:{_cachedBatteryVoltage}");
        }

        private void SafeCloseSocket()
        {
            try { _socket?.Close(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _socket = null;
        }

        private async Task<string?> ReadVin()
        {
            try
            {
                var response = await SendObdCommand("0902", 5000);
                Debug.WriteLine($"[VIN RAW] {response.Replace("\r", "\\r").Replace("\n", "\\n")}");

                // Clean up response
                var clean = response.Replace("\r", " ").Replace("\n", " ").Replace(">", "").Trim();
                clean = System.Text.RegularExpressions.Regex.Replace(clean, @"SEARCHING\.\.\.", "");
                clean = clean.Replace("NO DATA", "");

                // Split into lines/segments by "49 02" headers
                // The response can be: "49 02 01 57 46 30 ... 49 02 02 58 58 ..."
                // Each "49 02 XX" = header (mode response + PID + line number)
                // After header: VIN data bytes in hex

                // Collect all hex bytes
                var parts = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var vinBytes = new List<byte>();
                int i = 0;
                while (i < parts.Length)
                {
                    // Look for "49" "02" pattern (header start)
                    if (i + 2 < parts.Length && parts[i] == "49" && parts[i + 1] == "02")
                    {
                        i += 3; // Skip "49 02 XX" (mode + pid + line number)
                        // Collect data bytes until next header or end
                        while (i < parts.Length && parts[i] != "49")
                        {
                            if (byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
                                vinBytes.Add(b);
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                // Convert to ASCII
                if (vinBytes.Count >= 17)
                {
                    var vin = Encoding.ASCII.GetString(vinBytes.ToArray()).Trim('\0').Trim();
                    // Take last 17 chars if longer
                    if (vin.Length >= 17)
                        vin = vin.Substring(vin.Length - 17);
                    Debug.WriteLine($"[VIN DECODED] {vin}");
                    return vin.Length == 17 ? vin : null;
                }

                // Fallback: try to extract any 17-char alphanumeric sequence
                var allText = Encoding.ASCII.GetString(vinBytes.ToArray()).Trim('\0').Trim();
                Debug.WriteLine($"[VIN FALLBACK] bytes={vinBytes.Count} text='{allText}'");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VIN ERROR] {ex.Message}");
                return null;
            }
        }

        private async Task<string?> ReadFuelType()
        {
            try
            {
                var response = await QueryParameter("0151");
                var bytes = ObdProtocol.ParseResponse(response);
                if (bytes.Length >= 3 && bytes[0] == 0x41 && bytes[1] == 0x51)
                {
                    return bytes[2] switch
                    {
                        0x01 => "Gasoline",
                        0x02 => "Methanol",
                        0x03 => "Ethanol",
                        0x04 => "Diesel",
                        0x05 => "LPG",
                        0x06 => "CNG",
                        0x07 => "Propane",
                        0x08 => "Electric",
                        0x09 => "Bifuel (Gasoline)",
                        0x0A => "Bifuel (Methanol)",
                        0x0B => "Bifuel (Ethanol)",
                        0x0C => "Bifuel (LPG)",
                        0x0D => "Bifuel (CNG)",
                        0x0E => "Bifuel (Propane)",
                        0x0F => "Bifuel (Electric)",
                        _ => $"Unknown ({bytes[2]:X2})"
                    };
                }
                return null;
            }
            catch { return null; }
        }

        private async Task<string?> ReadBatteryVoltage()
        {
            try
            {
                var response = await SendObdCommand("ATRV");
                var clean = response.Replace("\r", "").Replace("\n", "").Replace(">", "").Trim();
                return clean.Length > 0 ? clean : null;
            }
            catch { return null; }
        }

        public Task StartMonitoring()
        {
            // Reuse the session CTS established by Connect so the connect, the poll/reconnect loop and
            // teardown all share ONE cancellation scope. (The old code recreated it here, which orphaned
            // the just-established connection and is why a Finish during connect could not cancel it.)
            _sessionCts ??= new CancellationTokenSource();

            // Reset the per-session telemetry cache.
            _cycle = 0;
            _throttle = _load = _intakeTemp = _maf = _mapVal = 0;
            _fuelPressure = _o2voltage = _lambda = _catalystTemp = _fuelLevel = 0;

            // The manager owns the poll/reconnect loop and GUARANTEES the socket is closed exactly
            // once when the session ends — even if Finish races a reconnect. That single teardown
            // guarantee is what stops the open-socket leak from accumulating across routes and
            // eventually wedging the adapter ("Socket not available"). See ObdConnectionManager
            // and its unit tests in WebApplication1.Tests/ObdConnectionManagerTests.cs.
            _manager = new ObdConnectionManager(
                reconnectAsync: ct => ConnectInternalAsync(ct, trigger: "reconnect"),
                pollOnceAsync: PollOnceAsync,
                closeTransport: SafeCloseSocket,
                policy: new ReconnectPolicy(),
                pollDelayMs: 500);

            var token = _sessionCts.Token;
            var task = Task.Run(() => _manager.RunAsync(token));
            _monitorTask = task;
            return task;
        }

        // One telemetry poll cycle, driven by ObdConnectionManager. Returns true when a valid frame
        // was read and published. A transport failure bubbles up as ObdConnectionException (from
        // QueryParameter) so the manager counts it and reconnects; the slow-PID cache lives in fields
        // so values persist between cycles.
        private async Task<bool> PollOnceAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // RPM is read every cycle and doubles as the connection-health probe:
            // a valid 41 0C frame means the link AND the vehicle bus are alive.
            var rpmRaw = await QueryParameter("010C");
            bool valid = ObdProtocol.TryParseTwoByteValue(rpmRaw, 0x0C, 4.0, out double rpm);
            if (!valid)
            {
                Debug.WriteLine($"[OBD] No valid frame (raw='{rpmRaw?.Trim()}')");
                return false;
            }

            double speed = ObdProtocol.ParseOneByteValue(await QueryParameter("010D"), 0x0D);
            double temp = ObdProtocol.ParseOneByteOffset(await QueryParameter("0105"), 0x05, -40);

            // Slow PIDs — every 5th cycle (~10s)
            if (_cycle % 5 == 0)
            {
                _throttle = ObdProtocol.ParseOneBytePercent(await QueryParameter("0111"), 0x11);
                _load = ObdProtocol.ParseOneBytePercent(await QueryParameter("0104"), 0x04);
                _intakeTemp = ObdProtocol.ParseOneByteOffset(await QueryParameter("010F"), 0x0F, -40);
                _maf = ObdProtocol.ParseTwoByteValue(await QueryParameter("0110"), 0x10, 100.0);
                _mapVal = ObdProtocol.ParseOneByteValue(await QueryParameter("010B"), 0x0B);
                _fuelLevel = ObdProtocol.ParseOneBytePercent(await QueryParameter("012F"), 0x2F);
            }

            // Very slow PIDs — every 15th cycle (~30s)
            if (_cycle % 15 == 0)
            {
                _fuelPressure = ObdProtocol.ParseTwoByteRaw(await QueryParameter("0123"), 0x23, 0.079);
                _o2voltage = ObdProtocol.ParseO2Voltage(await QueryParameter("0114"));
                _lambda = ObdProtocol.ParseLambda(await QueryParameter("0124"));
                _catalystTemp = ObdProtocol.ParseCatalystTemp(await QueryParameter("013C"));
                _cachedBatteryVoltage = await ReadBatteryVoltage() ?? _cachedBatteryVoltage;
            }

            _cycle++;

            CurrentRpm = rpm;
            CurrentSpeed = speed;
            CurrentTemperature = temp;
            LastUpdate = DateTime.Now;

            try
            {
                // Only attach coordinates once GPS has produced a real fix. Until then
                // LastLatitude/LastLongitude are still 0,0; persisting and drawing that
                // would put a bogus point in the Gulf of Guinea and distort the route.
                bool hasGpsFix = LastLatitude != 0 && LastLongitude != 0;
                var carData = new CarDataDto
                {
                    RouteId = CurrentRouteId,
                    RPM = rpm.ToString("F0"),
                    Speed = speed.ToString("F0"),
                    EngineCoolantTemperature = temp.ToString("F0"),
                    ThrottlePosition = _throttle.ToString("F1"),
                    EngineLoad = _load.ToString("F1"),
                    IntakeAirTemperature = _intakeTemp.ToString("F0"),
                    MAF = _maf.ToString("F2"),
                    MAP = _mapVal.ToString("F0"),
                    FuelRailPressure = _fuelPressure.ToString("F0"),
                    O2SensorVoltage = _o2voltage.ToString("F3"),
                    LambdaValue = _lambda.ToString("F3"),
                    CatalystTemperature = _catalystTemp.ToString("F0"),
                    FuelLevel = _fuelLevel.ToString("F1"),
                    FuelType = _cachedFuelType,
                    BatteryVoltage = _cachedBatteryVoltage,
                    VIN = _cachedVin,
                    Latitude = hasGpsFix ? LastLatitude.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    Longitude = hasGpsFix ? LastLongitude.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    Timestamp = DateTime.Now
                };
                await _rabbitMqService.PublishAsync(carData, "obd-data");
            }
            catch (Exception pubEx)
            {
                Debug.WriteLine($"Error publishing OBD data: {pubEx.Message}");
            }

            Debug.WriteLine($"OBD - RPM:{rpm:F0} Spd:{speed:F0} Tmp:{temp:F0} Thr:{_throttle:F1}% Load:{_load:F1}%");
            return true;
        }

        private async Task<Location?> GetCurrentLocation()
        {
            try
            {
                var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(5));
                return await Geolocation.GetLocationAsync(request);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting location: {ex.Message}");
                return null;
            }
        }

        private async Task ClearInputBuffer()
        {
            if (_socket?.InputStream == null) return;
            

            try
            {
                byte[] buffer = new byte[1024];
                // Just do a quick read with timeout to flush buffer
                var readTask = _socket.InputStream.ReadAsync(buffer, 0, buffer.Length);
                var timeoutTask = Task.Delay(100);
                await Task.WhenAny(readTask, timeoutTask);
            }
            catch
            {
                // Ignore errors during flush
            }
        }

        private async Task<string> QueryParameter(string command)
        {
            try
            {
                return await SendObdCommand(command);
            }
            catch (ObdConnectionException)
            {
                throw;   // transport down -> bubble up so the monitor loop reconnects
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Query failed for {command}: {ex.Message}");
                return "";
            }
        }

        // Signals the current session (in-flight connect AND/OR the poll loop) to abort, and unblocks
        // any in-flight blocking read/connect by closing the socket. Safe to call when nothing runs.
        private void CancelCurrentSession()
        {
            var cts = _sessionCts;
            try { cts?.Cancel(); } catch { /* already disposed */ }
            SafeCloseSocket();
        }

        // Waits for the monitor loop (if any) to unwind after cancellation, then disposes the session
        // and resets the transport/manager state so the next connect starts from a clean slate.
        private async Task TeardownSessionAsync()
        {
            var task = _monitorTask;
            if (task != null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[OBD] Teardown wait: {ex.Message}"); }
            }

            SafeCloseSocket();
            _monitorTask = null;
            _manager = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
        }

        public async Task StopMonitoring()
        {
            // Cancel FIRST (outside the gate) so an in-flight connect/poll aborts immediately and the
            // lifecycle gate is released promptly; then serialize the actual teardown.
            CancelCurrentSession();

            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Nothing to stop -> skip the bookkeeping. This collapses the burst of redundant Finish
                // taps into a single teardown.
                if (_monitorTask == null && _manager == null && _sessionCts == null)
                    return;

                await TeardownSessionAsync().ConfigureAwait(false);

                CurrentRpm = 0;
                CurrentSpeed = 0;
                CurrentTemperature = 0;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task<string> SendObdCommand(string command, int timeoutMs = 2000)
        {
            var socket = _socket;
            if (socket == null || !socket.IsConnected)
                throw new ObdConnectionException("Not connected to device");

            try
            {
                var inputStream = socket.InputStream;
                var outputStream = socket.OutputStream;

                if (inputStream == null || outputStream == null)
                    throw new ObdConnectionException("Unable to get socket streams");

                var bytes = Encoding.ASCII.GetBytes(command + "\r");
                await outputStream.WriteAsync(bytes, 0, bytes.Length);
                await outputStream.FlushAsync();

                var responseBuilder = new StringBuilder();
                var startTime = DateTime.Now;
                byte[] buffer = new byte[1024];

                while (true)
                {
                    var bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead < 0)
                        throw new ObdConnectionException("Socket stream closed (read returned -1)");

                    if (bytesRead > 0)
                    {
                        string chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        responseBuilder.Append(chunk);

                        if (responseBuilder.ToString().Contains(">"))
                            break;
                    }

                    if ((DateTime.Now - startTime).TotalMilliseconds > timeoutMs)
                        break;
                }

                return responseBuilder.ToString();
            }
            catch (ObdConnectionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Any write/read failure here means the Bluetooth transport is down
                // (covers both System.IO.IOException and Java.IO.IOException) -> reconnect.
                throw new ObdConnectionException($"I/O error on '{command}': {ex.Message}", ex);
            }
        }
    }
}
#endif