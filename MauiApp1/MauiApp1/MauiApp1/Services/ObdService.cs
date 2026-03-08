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

        private bool _isRunning;
        private string? _cachedVin;
        private string? _cachedFuelType;
        private string? _cachedBatteryVoltage;

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
            if (_bluetoothAdapter == null)
                throw new Exception("No Bluetooth adapter found");

            if (!_bluetoothAdapter.IsEnabled)
                throw new Exception("Bluetooth is not enabled");

            var pairedDevices = _bluetoothAdapter.BondedDevices;

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
                throw new Exception($"OBD device not found. Paired devices: {names}");
            }

            Debug.WriteLine($"[OBD] Connecting to: {_device.Name}");

            _socket = _device.CreateRfcommSocketToServiceRecord(SppUuid);
            await _socket.ConnectAsync();
            Debug.WriteLine($"Connected to classic Bluetooth device: {deviceName}");

            // Initialize ELM327
            await Task.Delay(500); // Give device time to settle
            await SendObdCommand("ATZ"); // Reset
            await Task.Delay(1000); // Wait for reset
            await SendObdCommand("ATE0"); // Echo off
            await Task.Delay(100);
            
            Debug.WriteLine("ELM327 initialized");

            // Read static values once
            _cachedVin = await ReadVin();
            _cachedFuelType = await ReadFuelType();
            _cachedBatteryVoltage = await ReadBatteryVoltage();
            Debug.WriteLine($"VIN: {_cachedVin}, FuelType: {_cachedFuelType}, Battery: {_cachedBatteryVoltage}");
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
                var bytes = ParseObdResponse(response);
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

        public async Task StartMonitoring()
        {
            if (_socket == null || !_socket.IsConnected)
                throw new Exception("OBD device not connected");

            _isRunning = true;
            int cycle = 0;

            // Cached slow-cycle values
            double throttle = 0, load = 0, intakeTemp = 0, maf = 0, mapVal = 0;
            double fuelPressure = 0, o2voltage = 0, lambda = 0, catalystTemp = 0, fuelLevel = 0;

            while (_isRunning)
            {
                try
                {
                    // Fast PIDs — every cycle (~2s)
                    var rpm = ParseTwoByteValue(await QueryParameter("010C"), 0x0C, 4.0);
                    var speed = ParseOneByteValue(await QueryParameter("010D"), 0x0D);
                    var temp = ParseOneByteOffset(await QueryParameter("0105"), 0x05, -40);

                    // Slow PIDs — every 5th cycle (~10s)
                    if (cycle % 5 == 0)
                    {
                        throttle = ParseOneBytePercent(await QueryParameter("0111"), 0x11);
                        load = ParseOneBytePercent(await QueryParameter("0104"), 0x04);
                        intakeTemp = ParseOneByteOffset(await QueryParameter("010F"), 0x0F, -40);
                        maf = ParseTwoByteValue(await QueryParameter("0110"), 0x10, 100.0);
                        mapVal = ParseOneByteValue(await QueryParameter("010B"), 0x0B);
                        fuelLevel = ParseOneBytePercent(await QueryParameter("012F"), 0x2F);
                    }

                    // Very slow PIDs — every 15th cycle (~30s)
                    if (cycle % 15 == 0)
                    {
                        fuelPressure = ParseTwoByteRaw(await QueryParameter("0123"), 0x23, 0.079);
                        o2voltage = ParseO2Voltage(await QueryParameter("0114"));
                        lambda = ParseLambda(await QueryParameter("0124"));
                        catalystTemp = ParseCatalystTemp(await QueryParameter("013C"));
                        _cachedBatteryVoltage = await ReadBatteryVoltage() ?? _cachedBatteryVoltage;
                    }

                    cycle++;

                    
                    
                    
                    
                    
                    
                    
                    CurrentRpm = rpm;
                    CurrentSpeed = speed;
                    CurrentTemperature = temp;
                    LastUpdate = DateTime.Now;

                    try
                    {
                        var carData = new CarDataDto
                        {
                            RouteId = CurrentRouteId,
                            RPM = rpm.ToString("F0"),
                            Speed = speed.ToString("F0"),
                            EngineCoolantTemperature = temp.ToString("F0"),
                            ThrottlePosition = throttle.ToString("F1"),
                            EngineLoad = load.ToString("F1"),
                            IntakeAirTemperature = intakeTemp.ToString("F0"),
                            MAF = maf.ToString("F2"),
                            MAP = mapVal.ToString("F0"),
                            FuelRailPressure = fuelPressure.ToString("F0"),
                            O2SensorVoltage = o2voltage.ToString("F3"),
                            LambdaValue = lambda.ToString("F3"),
                            CatalystTemperature = catalystTemp.ToString("F0"),
                            FuelLevel = fuelLevel.ToString("F1"),
                            FuelType = _cachedFuelType,
                            BatteryVoltage = _cachedBatteryVoltage,
                            VIN = _cachedVin,
                            Latitude = LastLatitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Longitude = LastLongitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Timestamp = DateTime.UtcNow
                        };
                        await _rabbitMqService.PublishAsync(carData, "obd-data");
                    }
                    catch (Exception pubEx)
                    {
                        Debug.WriteLine($"Error publishing OBD data: {pubEx.Message}");
                    }

                    Debug.WriteLine($"OBD - RPM:{rpm:F0} Spd:{speed:F0} Tmp:{temp:F0} Thr:{throttle:F1}% Load:{load:F1}%");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading OBD data: {ex.Message}");
                    await Task.Delay(3000); // Shorter retry wait
                }

                await Task.Delay(500); // Faster polling (adjustable)
            }
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Query failed for {command}: {ex.Message}");
                return "";
            }
        }

        public void StopMonitoring()
        {
            _isRunning = false;
            _socket?.Close();
            _socket = null!;
        }

        private async Task<string> SendObdCommand(string command, int timeoutMs = 2000)
        {
            if (_socket == null || !_socket.IsConnected)
                throw new Exception("Not connected to device");

            try
            {
                var inputStream = _socket.InputStream;
                var outputStream = _socket.OutputStream;

                if (inputStream == null || outputStream == null)
                    throw new Exception("Unable to get socket streams");

                var bytes = Encoding.ASCII.GetBytes(command + "\r");
                await outputStream.WriteAsync(bytes, 0, bytes.Length);
                await outputStream.FlushAsync();

                var responseBuilder = new StringBuilder();
                var startTime = DateTime.Now;
                byte[] buffer = new byte[1024];

                while (true)
                {
                    var bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length);
                    
                    if (bytesRead > 0)
                    {
                        string chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        responseBuilder.Append(chunk);

                        if (responseBuilder.ToString().Contains(">"))
                            break;
                    }

                    if ((DateTime.Now - startTime).TotalMilliseconds > timeoutMs)
                    {
                        Debug.WriteLine($"Timeout reading response for command: {command}");
                        break;
                    }
                }

                var result = responseBuilder.ToString();
                Debug.WriteLine($"CMD: {command} -> {result.Replace("\r", "\\r").Replace("\n", "\\n")}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending OBD command '{command}': {ex.Message}");
                throw;
            }
        }

        private byte[] ParseObdResponse(string response)
        {
            try
            {
                // Remove unwanted characters
                response = response.Replace("\r", "").Replace("\n", "")
                                   .Replace(">", "").Replace(" ", "").Trim();

                // Remove common ELM327 responses
                response = response.Replace("SEARCHING...", "")
                                   .Replace("NODATA", "")
                                   .Replace("OK", "");

                if (string.IsNullOrWhiteSpace(response) || response.Length < 4)
                {
                    Debug.WriteLine($"Invalid OBD response: '{response}'");
                    return new byte[0];
                }

                // Parse hex bytes (format: 410C1A2B -> 41 0C 1A 2B)
                var bytes = new List<byte>();
                for (int i = 0; i < response.Length; i += 2)
                {
                    if (i + 1 < response.Length)
                    {
                        string hex = response.Substring(i, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                        {
                            bytes.Add(b);
                        }
                    }
                }

                return bytes.ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error parsing OBD response '{response}': {ex.Message}");
                return new byte[0];
            }
        }

        // Generic parsers for OBD-II PIDs
        // Two-byte value with divisor: ((A*256)+B)/divisor — RPM(010C /4), MAF(0110 /100)
        private double ParseTwoByteValue(string raw, byte pid, double divisor)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == pid)
                return ((b[2] * 256) + b[3]) / divisor;
            return 0;
        }

        // One-byte direct value: A — Speed(010D), MAP(010B)
        private double ParseOneByteValue(string raw, byte pid)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2];
            return 0;
        }

        // One-byte with offset: A + offset — Coolant(0105 -40), IntakeAir(010F -40)
        private double ParseOneByteOffset(string raw, byte pid, int offset)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2] + offset;
            return 0;
        }

        // One-byte percentage: A * 100/255 — Throttle(0111), Load(0104), FuelLevel(012F)
        private double ParseOneBytePercent(string raw, byte pid)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == pid)
                return b[2] * 100.0 / 255.0;
            return 0;
        }

        // Two-byte raw with multiplier: ((A*256)+B)*multiplier — FuelRail(0123 *0.079)
        private double ParseTwoByteRaw(string raw, byte pid, double multiplier)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == pid)
                return ((b[2] * 256) + b[3]) * multiplier;
            return 0;
        }

        // O2 sensor voltage (0114): A/200 volts
        private double ParseO2Voltage(string raw)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 3 && b[0] == 0x41 && b[1] == 0x14)
                return b[2] / 200.0;
            return 0;
        }

        // Lambda/equivalence ratio (0124): ((A*256)+B)/32768
        private double ParseLambda(string raw)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == 0x24)
                return ((b[2] * 256) + b[3]) / 32768.0;
            return 0;
        }

        // Catalyst temperature (013C): ((A*256)+B)/10 - 40
        private double ParseCatalystTemp(string raw)
        {
            var b = ParseObdResponse(raw);
            if (b.Length >= 4 && b[0] == 0x41 && b[1] == 0x3C)
                return ((b[2] * 256) + b[3]) / 10.0 - 40;
            return 0;
        }
    }
}
#endif