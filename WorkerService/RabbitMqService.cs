using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;

namespace WorkerService
{
    public class RabbitMqService
    {
        private readonly ILogger<RabbitMqService> _logger;
        private IConnection _connection;
        private IChannel _channel;

        public RabbitMqService(ILogger<RabbitMqService> logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            var factory = new ConnectionFactory { HostName = "localhost" };

            _connection = await factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(queue: "liviu", durable: false, exclusive: false, autoDelete: false, arguments: null);

            _logger.LogInformation("RabbitMQ connection and channel initialized.");
        }

        public async Task ConsumeAsync(Func<string, Task> handleMessage, CancellationToken stoppingToken)
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                _logger.LogInformation($"Received message from RabbitMQ: {message}");

                if (handleMessage != null)
                {
                    await handleMessage(message);
                }
            };

            await _channel.BasicConsumeAsync(queue: "liviu", autoAck: true, consumer: consumer);

            _logger.LogInformation("RabbitMQ consuming started.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel != null)
            {
                await _channel.CloseAsync();
                await _channel.DisposeAsync();
            }

            if (_connection != null)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }

            _logger.LogInformation("RabbitMQ connection and channel disposed.");
        }
    }
}
