namespace Api.DIA.Wallet;

public sealed record DebitOutcome(
    Guid EventId,
    Guid UserId,
    decimal Amount,
    string Status,
    decimal? BalanceAfter,
    bool DuplicateDelivery);

public sealed record WalletSnapshot(Guid UserId, decimal Balance, DateTime UpdatedAt);

public sealed record DebitRecord(
    Guid EventId,
    Guid UserId,
    decimal Amount,
    string Status,
    decimal? BalanceAfter,
    DateTime ProcessedAt);

public sealed record LedgerRecord(
    Guid Id,
    Guid EventId,
    Guid UserId,
    decimal Amount,
    string Direction,
    decimal BalanceAfter,
    DateTime CreatedAt);
