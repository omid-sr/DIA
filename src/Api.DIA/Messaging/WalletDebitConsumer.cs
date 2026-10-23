using System.Text.Json;
using Api.DIA.Hosting;
using Api.DIA.Wallet;
using Dia.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Api.DIA.Messaging;

public sealed class WalletDebitConsumer(
    IOptions<RabbitMqOptions> options,
    SchemaReady schemaReady,
    WalletDebitService debits,
    WalletResultPublisher results,
    ILogger<WalletDebitConsumer> logger) : BackgroundService
{
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schemaReady.WaitAsync(stoppingToken);
        var delay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Wallet consumer disconnected. Reconnecting in {DelaySeconds}s.", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var prefetch = settings.PrefetchCount == 0 ? (ushort)1 : settings.PrefetchCount;
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.User,
            Password = settings.Password,
            AutomaticRecoveryEnabled = false
        };

        await using var connection = await factory.CreateConnectionAsync(
            $"api-dia-consumer-{Environment.MachineName}",
            stoppingToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            outstandingPublisherConfirmationsRateLimiter: null,
            consumerDispatchConcurrency: prefetch);
        await using var channel = await connection.CreateChannelAsync(channelOptions, stoppingToken);
        await WalletTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetch, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(
            queue: WalletMessaging.DebitQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "Instance {Instance} is consuming {Queue} with prefetch {Prefetch}.",
            Environment.MachineName,
            WalletMessaging.DebitQueue,
            prefetch);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, args) =>
        {
            logger.LogWarning("RabbitMQ connection closed: {Reply}", args.ReplyText);
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, stoppingToken));
        stoppingToken.ThrowIfCancellationRequested();
        throw new IOException("RabbitMQ connection closed.");
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var payload = delivery.Body.ToArray();
        var deliveryTag = delivery.DeliveryTag;

        try
        {
            var request = JsonSerializer.Deserialize<WalletDebitRequested>(payload, ContractJson.Options)
                ?? throw new JsonException("Debit payload was empty.");

            logger.LogInformation(
                "Instance {Instance} received debit {EventId} for user {UserId} amount {Amount}.",
                Environment.MachineName,
                request.EventId,
                request.UserId,
                request.Amount);

            var outcome = await debits.HandleAsync(request, stoppingToken);
            await results.PublishAsync(ToCompleted(outcome), stoppingToken);
            await AckAsync(channel, deliveryTag, stoppingToken);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Dead-lettering a debit message that is not valid JSON.");
            await NackAsync(channel, deliveryTag, requeue: false, stoppingToken);
        }
        catch (ArgumentException exception)
        {
            logger.LogError(exception, "Dead-lettering a debit message that failed validation.");
            await NackAsync(channel, deliveryTag, requeue: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await NackAsync(channel, deliveryTag, requeue: true, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Debit handling failed. The message will be requeued.");
            await NackAsync(channel, deliveryTag, requeue: true, stoppingToken);
        }
    }

    private async Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken cancellationToken)
    {
        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task NackAsync(IChannel channel, ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
    {
        try
        {
            await _channelLock.WaitAsync(cancellationToken);
            try
            {
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken);
            }
            finally
            {
                _channelLock.Release();
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not nack delivery {DeliveryTag}.", deliveryTag);
        }
    }

    private static WalletDebitCompleted ToCompleted(DebitOutcome outcome) => new()
    {
        EventId = outcome.EventId,
        UserId = outcome.UserId,
        Amount = outcome.Amount,
        Status = outcome.Status,
        BalanceAfter = outcome.BalanceAfter,
        DuplicateDelivery = outcome.DuplicateDelivery,
        HandledBy = Environment.MachineName,
        CompletedAt = DateTimeOffset.UtcNow
    };
}
