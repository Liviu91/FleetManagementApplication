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

        // Diagnostics (observability only — never alters connect/reconnect behaviour).
        private readonly ObdDiagnostics _diag;
        private string? _routeCorr;             // correlation id shared across one route's OBD activity
        private DateTime? _lastConnectOkUtc;    // last successful connect (inter-route timing)
        private DateTime? _lastStopUtc;         // last StopMonitoring (inter-route timing)

        // Connection / loop resilience state
        private const int ConnectTimeoutMs = 8000;     // hard cap so a wedged adapter can't block a connect forever
        private string _preferredDeviceName = "OBDII";
        private Task? _monitorTask;
        private CancellationTokenSource? _sessionCts;
        private ObdConnectionManager? _manager;

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

        public ObdService(RabbitMqService rabbitMqService, ObdDiagnostics diagnostics)
        {
            _rabbitMqService = rabbitMqService;
            _diag = diagnostics;
#pragma warning disable CS0618 // Type or member is obsolete
            _bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
#pragma warning restore CS0618 // Type or member is obsolete
        }

        public async Task Connect(string deviceName = "OBDII")
        {
            _preferredDeviceName = deviceName;
            _routeCorr = ObdDiagnostics.NewCorrelationId();   // groups this route's OBD activity in the log
            await ConnectInternalAsync();
        }

        // Robust connect: resolve the paired adapter, cancel discovery, and retry a few times.
        // Later attempts use the reflection createRfcommSocket(1) fallback — the well-known
        // workaround for ELM327 clones that fail the secure-UUID socket with
        // "read failed, socket might closed or timeout, read ret: -1".
        private async Task ConnectInternalAsync(CancellationToken ct = default, int maxAttempts = 4,
                                                string trigger = "initial-connect")
        {
            // --- Diagnostics: open an episode and snapshot the PRE-connect context. Observability only;
            //     none of the calls below change the connect/retry/fallback behaviour.
            string episodeCorr = ObdDiagnostics.NewCorrelationId();
            var episodeSw = Stopwatch.StartNew();
            bool priorSocketNonNull = _socket != null;            // catches a socket leaked from a prior route
            bool priorSocketConnected = false;
            try { priorSocketConnected = _socket?.IsConnected == true; } catch { }
            var nowUtc = DateTime.UtcNow;

            _diag.BeginConnectEpisode(episodeCorr, _routeCorr, trigger, CurrentRouteId, new
            {
                prior_socket_non_null = priorSocketNonNull,
                prior_socket_connected = priorSocketConnected,
                secs_since_last_connect_ok = _lastConnectOkUtc.HasValue
                    ? (double?)(nowUtc - _lastConnectOkUtc.Value).TotalSeconds : null,
                secs_since_last_stop = _lastStopUtc.HasValue
                    ? (double?)(nowUtc - _lastStopUtc.Value).TotalSeconds : null,
                max_attempts = maxAttempts,
                adapter = SnapshotAdapterState(),
                lifecycle = SnapshotLifecycle()
            });

            string outcome = "FAILURE";
            string summary = "";
            try
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

                    string socketType = attempt < 3 ? "secure-uuid" : "reflection";
                    string phase = "connect";
                    var attemptSw = Stopwatch.StartNew();
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
                            _diag.LogAttempt(episodeCorr, _routeCorr, new
                            {
                                attempt,
                                socket_type = socketType,
                                phase = "connect",
                                result = "connected",
                                duration_ms = attemptSw.ElapsedMilliseconds
                            });

                            phase = "initialize";
                            await InitializeElmAsync(episodeCorr);

                            _diag.LogAttempt(episodeCorr, _routeCorr, new
                            {
                                attempt,
                                socket_type = socketType,
                                phase = "initialize",
                                result = "success",
                                duration_ms = attemptSw.ElapsedMilliseconds
                            });

                            outcome = "SUCCESS";
                            _lastConnectOkUtc = DateTime.UtcNow;
                            return;
                        }

                        // ConnectAsync returned but the socket reports not-connected — log the oddity.
                        _diag.LogAttempt(episodeCorr, _routeCorr, new
                        {
                            attempt,
                            socket_type = socketType,
                            phase = "connect",
                            result = "not-connected",
                            duration_ms = attemptSw.ElapsedMilliseconds
                        });
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _diag.LogAttempt(episodeCorr, _routeCorr, new
                        {
                            attempt,
                            socket_type = socketType,
                            phase,
                            result = "cancelled",
                            duration_ms = attemptSw.ElapsedMilliseconds
                        });
                        SafeCloseSocket();                   // session ending — leave nothing half-open
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        _diag.LogAttempt(episodeCorr, _routeCorr, new
                        {
                            attempt,
                            socket_type = socketType,
                            phase,                            // "connect" or "initialize" — where it died
                            result = ex is TimeoutException ? "timeout" : "exception",
                            duration_ms = attemptSw.ElapsedMilliseconds,
                            error = ObdDiagnostics.DescribeException(ex)
                        });
                        SafeCloseSocket();                   // release the failed/stuck socket immediately
                        Debug.WriteLine($"[OBD] Connect attempt {attempt} failed: {ex.Message}");
                    }
                }

                SafeCloseSocket();
                throw new ObdConnectionException(
                    $"Failed to connect to OBD after {maxAttempts} attempts: {last?.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = "CANCELLED";
                summary = "connect cancelled (route ended)";
                throw;
            }
            catch (Exception ex)
            {
                summary = ex.Message;
                throw;
            }
            finally
            {
                _diag.EndConnectEpisode(episodeCorr, _routeCorr, outcome, new
                {
                    trigger,
                    summary,
                    total_ms = episodeSw.ElapsedMilliseconds,
                    reconnect_count = _manager?.ReconnectCount,
                    consecutive_poll_failures = _manager?.ConsecutivePollFailures,
                    manager_last_error = _manager?.LastError,
                    adapter_after = SnapshotAdapterState()
                });
            }
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

        // Plain-value snapshot of the adapter / bonding / target-device state for diagnostics. Never throws.
        private object SnapshotAdapterState()
        {
            try
            {
                var bonded = new List<string>();
                try
                {
                    var devices = _bluetoothAdapter?.BondedDevices;
                    if (devices != null)
                        foreach (var d in devices)
                            bonded.Add($"{d.Name}|{d.Address}|{d.BondState}");
                }
                catch { /* bonded list unavailable — leave empty */ }

                return new
                {
                    adapter_present = _bluetoothAdapter != null,
                    enabled = _bluetoothAdapter?.IsEnabled,
                    discovering = _bluetoothAdapter?.IsDiscovering,
                    state = _bluetoothAdapter?.State.ToString(),
                    device_name = _device?.Name,
                    device_address = _device?.Address,
                    device_bond_state = _device?.BondState.ToString(),
                    bonded_count = bonded.Count,
                    bonded
                };
            }
            catch (Exception ex)
            {
                return new { snapshot_error = ex.Message };
            }
        }

        // Cheap app-lifecycle hints relevant to the wedged-adapter failure (foreground service + wake lock).
        private static object SnapshotLifecycle()
        {
            try
            {
                return new
                {
                    fg_service_running = global::MauiApp1.LocationForegroundService.IsRunning,
                    wake_lock_held = global::MauiApp1.LocationForegroundService.WakeLockHeld,
                    fg_service_route = global::MauiApp1.LocationForegroundService.CurrentRouteId
                };
            }
            catch (Exception ex)
            {
                return new { lifecycle_error = ex.Message };
            }
        }

        private async Task InitializeElmAsync(string? corr = null)
        {
            // Capture the raw ELM handshake so an init-time failure (vs a connect-time failure) is
            // distinguishable from the exported log. Behaviour is unchanged: on any error we still throw.
            string? atz = null, ate0 = null, atl0 = null, atsp0 = null;
            try
            {
                await Task.Delay(500);              // let the adapter settle
                atz = await SendObdCommand("ATZ");  // reset
                await Task.Delay(1000);
                ate0 = await SendObdCommand("ATE0");// echo off
                await Task.Delay(100);
                atl0 = await SendObdCommand("ATL0");// linefeeds off
                await Task.Delay(100);
                atsp0 = await SendObdCommand("ATSP0");// auto protocol — re-establishes the bus after an engine restart
                await Task.Delay(100);
                Debug.WriteLine("[OBD] ELM327 initialized");

                // Static values — refresh on (re)connect but keep the previous value if a read fails.
                _cachedVin = await ReadVin() ?? _cachedVin;
                _cachedFuelType = await ReadFuelType() ?? _cachedFuelType;
                _cachedBatteryVoltage = await ReadBatteryVoltage() ?? _cachedBatteryVoltage;
                Debug.WriteLine($"[OBD] VIN:{_cachedVin} Fuel:{_cachedFuelType} Batt:{_cachedBatteryVoltage}");

                _diag.LogInit(corr ?? _routeCorr ?? "?", _routeCorr, "SUCCESS", new
                {
                    atz = ObdDiagnostics.San(atz),
                    ate0 = ObdDiagnostics.San(ate0),
                    atl0 = ObdDiagnostics.San(atl0),
                    atsp0 = ObdDiagnostics.San(atsp0),
                    vin = _cachedVin,
                    fuel_type = _cachedFuelType,
                    battery_voltage = _cachedBatteryVoltage
                });
            }
            catch (Exception ex)
            {
                _diag.LogInit(corr ?? _routeCorr ?? "?", _routeCorr, "FAILURE", new
                {
                    atz = ObdDiagnostics.San(atz),
                    ate0 = ObdDiagnostics.San(ate0),
                    atl0 = ObdDiagnostics.San(atl0),
                    atsp0 = ObdDiagnostics.San(atsp0),
                    vin = _cachedVin,
                    battery_voltage = _cachedBatteryVoltage,
                    error = ObdDiagnostics.DescribeException(ex)
                });
                throw;
            }
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
            // Defensive: tear down any previous session so a route always starts from a clean slate.
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();

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

            _diag.MonitorNote("monitor_start", _routeCorr, new { route_id = CurrentRouteId });

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
                _diag.PollFailure(_routeCorr, new
                {
                    reason = "no-valid-rpm-frame",
                    command = "010C",
                    raw = ObdDiagnostics.San(rpmRaw),
                    reconnect_count = _manager?.ReconnectCount,
                    consecutive_poll_failures = _manager?.ConsecutivePollFailures,
                    manager_last_error = _manager?.LastError
                });
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
                    Timestamp = DateTime.UtcNow
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
            catch (ObdConnectionException ex)
            {
                _diag.PollFailure(_routeCorr, new
                {
                    reason = "transport-exception",
                    command,
                    error = ObdDiagnostics.DescribeException(ex)
                });
                throw;   // transport down -> bubble up so the monitor loop reconnects
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Query failed for {command}: {ex.Message}");
                return "";
            }
        }

        public async Task StopMonitoring()
        {
            _diag.MonitorNote("monitor_stop", _routeCorr, new
            {
                route_id = CurrentRouteId,
                reconnect_count = _manager?.ReconnectCount,
                consecutive_poll_failures = _manager?.ConsecutivePollFailures,
                manager_last_error = _manager?.LastError
            });

            // 1) Tell the loop to stop and unblock any in-flight blocking read so it returns promptly.
            _sessionCts?.Cancel();
            SafeCloseSocket();

            // 2) Wait for the loop to actually finish. The manager's finally then closes the socket
            //    exactly once, so the next route starts from a guaranteed-clean state.
            var task = _monitorTask;
            if (task != null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { Debug.WriteLine($"[OBD] Stop wait: {ex.Message}"); }
            }

            // 3) Belt-and-suspenders close + reset cached telemetry/session state.
            SafeCloseSocket();
            _monitorTask = null;
            _manager = null;
            _sessionCts?.Dispose();
            _sessionCts = null;

            CurrentRpm = 0;
            CurrentSpeed = 0;
            CurrentTemperature = 0;

            _lastStopUtc = DateTime.UtcNow;   // inter-route timing anchor for the next connect episode
            _routeCorr = null;
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
                    {
                        _diag.Trace("CMD", $"TIMEOUT reading response for: {command}");
                        break;
                    }
                }

                var result = responseBuilder.ToString();
                _diag.Trace("CMD", $"{command} -> {ObdDiagnostics.San(result)}");
                return result;
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