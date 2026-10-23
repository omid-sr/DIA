namespace Dia.Contracts;

public static class DemoWallet
{
    public static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const decimal OpeningBalance = 1100m;
    public const decimal DebitAmount = 1000m;
}

public static class DebitStatuses
{
    public const string Applied = nameof(Applied);
    public const string RejectedInsufficientFunds = nameof(RejectedInsufficientFunds);
    public const string RejectedWalletNotFound = nameof(RejectedWalletNotFound);
    public const string RejectedInvalidAmount = nameof(RejectedInvalidAmount);
}

public static class WalletMessaging
{
    public const string Exchange = "dia.wallet";
    public const string DebitQueue = "wallet.debit.requested";
    public const string DebitRoutingKey = "wallet.debit.requested";
    public const string DebitDeadLetterQueue = "wallet.debit.requested.dlq";
    public const string ResultQueue = "wallet.debit.results";
    public const string ResultRoutingKey = "wallet.debit.results";
}

/// <summary>
/// A request to take <see cref="Amount"/> out of a user's wallet.
/// <see cref="EventId"/> is the idempotency key: a redelivery of the same id is never applied twice.
/// </summary>
public sealed record WalletDebitRequested
{
    public required Guid EventId { get; init; }
    public required Guid UserId { get; init; }
    public required decimal Amount { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record WalletDebitCompleted
{
    public required Guid EventId { get; init; }
    public required Guid UserId { get; init; }
    public required decimal Amount { get; init; }
    public required string Status { get; init; }
    public decimal? BalanceAfter { get; init; }
    public bool DuplicateDelivery { get; init; }
    public required string HandledBy { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}
