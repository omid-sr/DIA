using System.Text.Json;
using Dia.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Api.DIA.Messaging;

public sealed class WalletResultPublisher(IOptions<RabbitMqOptions> options, ILogger<WalletResultPublisher> logger)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(WalletDebitCompleted message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.EventId.ToString(),
            Type = nameof(WalletDebitCompleted)
        };

        await _sync.WaitAsync(cancellationToken);
        try
        {
            await EnsureChannelAsync(cancellationToken);
            await _channel!.BasicPublishAsync(
                WalletMessaging.Exchange,
                WalletMessaging.ResultRoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _sync.Release();
            _sync.Dispose();
        }
    }

    private async Task EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not { IsOpen: true })
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.Host,
                Port = settings.Port,
                VirtualHost = settings.VirtualHost,
                UserName = settings.User,
                Password = settings.Password,
                AutomaticRecoveryEnabled = false
            };
            _connection = await factory.CreateConnectionAsync($"api-dia-publisher-{Environment.MachineName}", cancellationToken);
        }

        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await WalletTopology.DeclareAsync(_channel, cancellationToken);
        logger.LogInformation("Result publisher connected to RabbitMQ at {Host}.", options.Value.Host);
    }
}
