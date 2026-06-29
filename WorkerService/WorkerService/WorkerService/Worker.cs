using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using WorkerService.Models;
using WorkerService.Repository;
using System.Text;
using static System.Formats.Asn1.AsnWriter;
using Azure;
using System.Threading.Channels;
using System.Text.Json;
using System.Globalization;

namespace WorkerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IConfiguration _configuration;
        private ConnectionFactory _factory;
        private IConnection _connection;
        private IChannel _channel;
        private AsyncEventingBasicConsumer _consumer;

        public Worker(ILogger<Worker> logger, IServiceScopeFactory serviceScopeFactory, IConfiguration configuration)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _configuration = configuration;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var rabbitUri = _configuration["RabbitMQ:Uri"] ?? "amqp://guest:guest@localhost:5672/";
            _factory = new ConnectionFactory { Uri = new Uri(rabbitUri) };

            int retries = 0;
            const int maxRetries = 10;
            while (true)
            {
                try
                {
                    _connection = await _factory.CreateConnectionAsync(cancellationToken);
                    break;
                }
                catch (Exception ex) when (retries < maxRetries)
                {
                    retries++;
                    _logger.LogWarning(ex, "[RabbitMQ] Connection failed (attempt {Attempt}/{Max}). Retrying in 5s...", retries, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }

            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await _channel.QueueDeclareAsync(queue: "gps-log", durable: false, exclusive: false, autoDelete: false,
                arguments: null);

            await _channel.QueueDeclareAsync(queue: "obd-data", durable: false, exclusive: false, autoDelete: false,
                arguments: null);

            // GPS consumer
            _consumer = new AsyncEventingBasicConsumer(_channel);

            _consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var json = JsonSerializer.Deserialize<RouteGpsLogEntry>(message);

                    if (json == null || json.RouteId <= 0)
                    {
                        _logger.LogWarning("[RabbitMQ] Skipping GPS message with missing RouteId: {message}", message);
                        return;
                    }

                    // A real GPS fix is never exactly 0,0 (that point sits in the Gulf of Guinea);
                    // such a value means the phone had no fix yet, so drop it instead of poisoning
                    // the route polyline with an out-of-range point.
                    if (json.Latitude == 0 && json.Longitude == 0)
                    {
                        _logger.LogWarning("[RabbitMQ] Skipping GPS message with no fix (0,0): {message}", message);
                        return;
                    }

                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var repository = scope.ServiceProvider.GetRequiredService<IRepository<CarData>>();
                        var entry = new CarData
                        {
                            RouteId = json.RouteId,
                            Longitude = json.Longitude.ToString(CultureInfo.InvariantCulture),
                            Latitude = json.Latitude.ToString(CultureInfo.InvariantCulture),
                            Timestamp = json.Timestamp,
                            RPM = json.RPM,
                            Speed = json.Speed,
                            EngineCoolantTemperature = json.EngineCoolantTemperature
                        };

                        await AddWithRetryAsync(repository, entry);
                    }

                    _logger.LogInformation($"[RabbitMQ] Saved GPS message: {message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitMQ] Error processing GPS message");
                }
            };

            await _channel.BasicConsumeAsync(queue: "gps-log", autoAck: true, consumer: _consumer);

            // OBD consumer
            var obdConsumer = new AsyncEventingBasicConsumer(_channel);

            obdConsumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var json = JsonSerializer.Deserialize<ObdDataEntry>(message);

                    if (json == null || json.RouteId <= 0)
                    {
                        _logger.LogWarning("[RabbitMQ] Skipping OBD message with missing RouteId: {message}", message);
                        return;
                    }

                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var repository = scope.ServiceProvider.GetRequiredService<IRepository<CarData>>();
                        var entry = new CarData
                        {
                            RouteId = json.RouteId,
                            RPM = json.RPM,
                            Speed = json.Speed,
                            EngineCoolantTemperature = json.EngineCoolantTemperature,
                            ThrottlePosition = json.ThrottlePosition,
                            EngineLoad = json.EngineLoad,
                            IntakeAirTemperature = json.IntakeAirTemperature,
                            MAF = json.MAF,
                            MAP = json.MAP,
                            FuelRailPressure = json.FuelRailPressure,
                            O2SensorVoltage = json.O2SensorVoltage,
                            LambdaValue = json.LambdaValue,
                            CatalystTemperature = json.CatalystTemperature,
                            VIN = json.VIN,
                            FuelType = json.FuelType,
                            FuelLevel = json.FuelLevel,
                            BatteryVoltage = json.BatteryVoltage,
                            // Treat missing or 0,0 coordinates as "no fix" so the row carries
                            // telemetry only and is never drawn at an invalid location.
                            Latitude = NormalizeCoordinate(json.Latitude),
                            Longitude = NormalizeCoordinate(json.Longitude),
                            Timestamp = json.Timestamp
                        };

                        await AddWithRetryAsync(repository, entry);
                    }

                    _logger.LogInformation($"[RabbitMQ] Saved OBD message: {message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitMQ] Error processing OBD message");
                }
            };

            await _channel.BasicConsumeAsync(queue: "obd-data", autoAck: true, consumer: obdConsumer);

            _logger.LogInformation("[RabbitMQ] Consumer started.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);
            }
        }

        // Returns the coordinate string only when it is a real, non-zero value; otherwise null.
        // Keeps invalid 0,0 fixes (or empty strings) out of the map without losing the telemetry.
        private static string? NormalizeCoordinate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d == 0)
                return null;
            return value;
        }

        // Persist one row, retrying a few times on transient database errors so an intermittent
        // DB hiccup does not silently drop telemetry (messages are auto-acked on receipt).
        private async Task AddWithRetryAsync(IRepository<CarData> repository, CarData entry)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await repository.AddAsync(entry);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(ex, "[RabbitMQ] DB write failed (attempt {Attempt}/{Max}); retrying...", attempt, maxAttempts);
                    await Task.Delay(200 * attempt);
                }
            }
        }
    }
}
