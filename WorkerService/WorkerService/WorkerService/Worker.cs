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
        //private readonly IRepository<CarData> _carDataRepository;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private ConnectionFactory _factory;
        private IConnection _connection;
        private IChannel _channel;
        private AsyncEventingBasicConsumer _consumer;

        public Worker(ILogger<Worker> logger, IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            //_carDataRepository = carDataRepository;
            _serviceScopeFactory = serviceScopeFactory;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _factory = new ConnectionFactory { HostName = "localhost" };

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

                        await repository.AddAsync(entry);
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
                            Latitude = json.Latitude,
                            Longitude = json.Longitude,
                            Timestamp = json.Timestamp
                        };

                        await repository.AddAsync(entry);
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
    }
}
