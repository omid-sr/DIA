using Api.DIA.Data;
using Api.DIA.Wallet;
using Dia.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace Api.DIA.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class WalletDebitConcurrencyTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Two_parallel_charges_apply_only_one_when_the_balance_covers_one()
    {
        var userId = await fixture.OpenWalletAsync(1100m);
        var results = await Task.WhenAll(
            fixture.DebitAsync(userId, 1000m),
            fixture.DebitAsync(userId, 1000m));

        Assert.Equal(1, results.Count(result => result.Status == DebitStatuses.Applied && !result.DuplicateDelivery));
        Assert.Equal(1, results.Count(result => result.Status == DebitStatuses.RejectedInsufficientFunds));
        Assert.Equal(100m, await fixture.BalanceAsync(userId));
    }

    [Fact]
    public async Task Fifteen_parallel_charges_stop_when_the_balance_is_spent()
    {
        var userId = await fixture.OpenWalletAsync(1100m);
        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => fixture.DebitAsync(userId, 100m)));

        Assert.Equal(11, results.Count(result => result.Status == DebitStatuses.Applied));
        Assert.Equal(4, results.Count(result => result.Status == DebitStatuses.RejectedInsufficientFunds));
        Assert.Equal(0m, await fixture.BalanceAsync(userId));
        Assert.Equal(11, (await fixture.Store.ListLedgerAsync(userId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task The_same_event_applied_twice_in_parallel_moves_the_balance_once()
    {
        var userId = await fixture.OpenWalletAsync(500m);
        var eventId = Guid.NewGuid();
        var results = await Task.WhenAll(
            fixture.Debits.HandleAsync(Request(eventId, userId, 100m), CancellationToken.None),
            fixture.Debits.HandleAsync(Request(eventId, userId, 100m), CancellationToken.None));

        Assert.Equal(2, results.Count(result => result.Status == DebitStatuses.Applied));
        Assert.Equal(1, results.Count(result => result.DuplicateDelivery));
        Assert.Equal(400m, await fixture.BalanceAsync(userId));
        Assert.Single(await fixture.Store.ListLedgerAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task Replaying_a_rejected_event_does_not_charge_later()
    {
        var userId = await fixture.OpenWalletAsync(1100m);
        var first = await fixture.DebitAsync(userId, 1000m);
        var second = await fixture.DebitAsync(userId, 1000m);
        var replay = await fixture.Debits.HandleAsync(
            new WalletDebitRequested
            {
                EventId = first.EventId,
                UserId = userId,
                Amount = 1000m,
                OccurredAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);

        Assert.Equal(DebitStatuses.Applied, first.Status);
        Assert.Equal(DebitStatuses.RejectedInsufficientFunds, second.Status);
        Assert.True(replay.DuplicateDelivery);
        Assert.Equal(DebitStatuses.Applied, replay.Status);
        Assert.Equal(100m, await fixture.BalanceAsync(userId));
    }

    [Fact]
    public async Task An_unknown_wallet_is_rejected_without_creating_a_ledger_row()
    {
        var outcome = await fixture.DebitAsync(Guid.NewGuid(), 1000m);

        Assert.Equal(DebitStatuses.RejectedWalletNotFound, outcome.Status);
        Assert.Null(outcome.BalanceAfter);
    }

    [Fact]
    public async Task A_non_positive_amount_is_rejected_and_stays_rejected()
    {
        var userId = await fixture.OpenWalletAsync(1100m);
        var eventId = Guid.NewGuid();
        var first = await fixture.Debits.HandleAsync(Request(eventId, userId, 0m), CancellationToken.None);
        var second = await fixture.Debits.HandleAsync(Request(eventId, userId, 0m), CancellationToken.None);

        Assert.Equal(DebitStatuses.RejectedInvalidAmount, first.Status);
        Assert.True(second.DuplicateDelivery);
        Assert.Equal(1100m, await fixture.BalanceAsync(userId));
    }

    private static WalletDebitRequested Request(Guid eventId, Guid userId, decimal amount) => new()
    {
        EventId = eventId,
        UserId = userId,
        Amount = amount,
        OccurredAt = DateTimeOffset.UtcNow
    };
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public WalletDebitService Debits { get; private set; } = null!;

    public WalletStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("Dia_Wallet_Str0ng!")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1536")
            .Build();
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();
        await WalletSchema.EnsureAsync(connectionString, Guid.NewGuid(), 0m, CancellationToken.None);
        Debits = new WalletDebitService(connectionString, NullLogger<WalletDebitService>.Instance);
        Store = new WalletStore(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<Guid> OpenWalletAsync(decimal balance)
    {
        var userId = Guid.NewGuid();
        await Store.ResetBalanceAsync(userId, balance, CancellationToken.None);
        return userId;
    }

    public Task<DebitOutcome> DebitAsync(Guid userId, decimal amount) =>
        Debits.HandleAsync(new WalletDebitRequested
        {
            EventId = Guid.NewGuid(),
            UserId = userId,
            Amount = amount,
            OccurredAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

    public async Task<decimal> BalanceAsync(Guid userId)
    {
        var wallet = await Store.GetAsync(userId, CancellationToken.None);
        return wallet?.Balance ?? throw new InvalidOperationException($"Wallet {userId} was not found.");
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
