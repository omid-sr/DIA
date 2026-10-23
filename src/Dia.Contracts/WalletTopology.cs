using RabbitMQ.Client;

namespace Dia.Contracts;

public static class WalletTopology
{
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: WalletMessaging.Exchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WalletMessaging.DebitDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        // Poison messages (bad JSON, invalid payload) are dead-lettered instead of looping forever.
        var debitArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = WalletMessaging.DebitDeadLetterQueue
        };

        await channel.QueueDeclareAsync(
            queue: WalletMessaging.DebitQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: debitArguments,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: WalletMessaging.DebitQueue,
            exchange: WalletMessaging.Exchange,
            routingKey: WalletMessaging.DebitRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WalletMessaging.ResultQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: WalletMessaging.ResultQueue,
            exchange: WalletMessaging.Exchange,
            routingKey: WalletMessaging.ResultRoutingKey,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
    }
}
