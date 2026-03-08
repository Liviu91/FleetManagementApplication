using RabbitMQ.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MauiApp1.Services
{
    public sealed class RabbitMqService : IAsyncDisposable
    {
        readonly ConnectionFactory _factory;
        IConnection? _conn;
        IChannel? _ch;
        readonly SemaphoreSlim _gate = new(1, 1);

        const string Queue = "gps-log";

        public RabbitMqService()
        {
            _factory = new ConnectionFactory
            {
                HostName = "192.168.1.142",   // ← laptop IP / Rabbit host
                Port = 5672,
                UserName = "maui",                
                Password = "maui",      
            };
        }

        /* ---------- public API ---------- */

        public async Task PublishAsync(object payload, string routingKey = Queue)
        {
            await EnsureChannelAsync();

            var json = JsonSerializer.Serialize(payload);
            var body = Encoding.UTF8.GetBytes(json);

            await _gate.WaitAsync();
            try
            {
                await _ch!.BasicPublishAsync(
                    exchange: "",
                    routingKey: routingKey,
                    body: body);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ch != null) await _ch.CloseAsync();
            if (_conn != null) await _conn.CloseAsync();
            _gate.Dispose();
        }

        /* ---------- lazy async init ---------- */

        async Task EnsureChannelAsync()
        {
            if (_ch != null) return;

            await _gate.WaitAsync();
            try
            {
                if (_ch == null)
                {
                    _conn = await _factory.CreateConnectionAsync();
                    _ch = await _conn.CreateChannelAsync();

                    await _ch.QueueDeclareAsync(
                        queue: Queue,
                        durable: false,
                        exclusive: false,
                        autoDelete: false);

                    await _ch.QueueDeclareAsync(
                        queue: "obd-data",
                        durable: false,
                        exclusive: false,
                        autoDelete: false);
                }
            }
            finally { _gate.Release(); }
        }
    }
}
