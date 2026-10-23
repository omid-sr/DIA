using System.Data;
using Api.DIA.Data;
using Dia.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Api.DIA.Wallet;

/// <summary>
/// Applies a wallet debit exactly once per event id, and never lets concurrent
/// debits drive the balance negative. The lock lives in SQL Server so every
/// API replica shares the same decision.
/// </summary>
public sealed class WalletDebitService(string connectionString, ILogger<WalletDebitService> logger)
{
    public async Task<DebitOutcome> HandleAsync(WalletDebitRequested request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EventId == Guid.Empty)
        {
            throw new ArgumentException("EventId is required.", nameof(request));
        }

        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId is required.", nameof(request));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var claimed = await TryClaimAsync(connection, transaction, request, cancellationToken);
        if (!claimed)
        {
            var existing = await ReadClaimAsync(connection, transaction, request.EventId, cancellationToken)
                ?? throw new InvalidOperationException($"Event {request.EventId} collided but the inbox row was not readable.");

            if (existing.Status == "Processing")
            {
                throw new InvalidOperationException($"Event {request.EventId} is still marked Processing.");
            }

            if (existing.UserId != request.UserId || existing.Amount != request.Amount)
            {
                logger.LogWarning(
                    "Event {EventId} was replayed with a different user or amount. Returning the original outcome.",
                    request.EventId);
            }

            await transaction.CommitAsync(cancellationToken);
            return new DebitOutcome(
                existing.EventId,
                existing.UserId,
                existing.Amount,
                existing.Status,
                existing.BalanceAfter,
                DuplicateDelivery: true);
        }

        var outcome = await DecideAsync(connection, transaction, request, cancellationToken);
        await CompleteClaimAsync(connection, transaction, outcome, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Debit {EventId} for user {UserId} amount {Amount} finished as {Status}. Balance after {Balance}. Instance {Instance}.",
            outcome.EventId,
            outcome.UserId,
            outcome.Amount,
            outcome.Status,
            outcome.BalanceAfter,
            Environment.MachineName);

        return outcome;
    }

    private static async Task<DebitOutcome> DecideAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WalletDebitRequested request,
        CancellationToken cancellationToken)
    {
        if (request.Amount <= 0)
        {
            return new DebitOutcome(
                request.EventId,
                request.UserId,
                request.Amount,
                DebitStatuses.RejectedInvalidAmount,
                BalanceAfter: null,
                DuplicateDelivery: false);
        }

        // UPDLOCK + HOLDLOCK serializes every replica that touches this wallet row.
        // The second debit blocks here until the first transaction commits, then
        // reads the balance that transaction left behind.
        var balance = await LockBalanceAsync(connection, transaction, request.UserId, cancellationToken);
        if (balance is null)
        {
            return new DebitOutcome(
                request.EventId,
                request.UserId,
                request.Amount,
                DebitStatuses.RejectedWalletNotFound,
                BalanceAfter: null,
                DuplicateDelivery: false);
        }

        if (balance.Value < request.Amount)
        {
            return new DebitOutcome(
                request.EventId,
                request.UserId,
                request.Amount,
                DebitStatuses.RejectedInsufficientFunds,
                balance,
                DuplicateDelivery: false);
        }

        var balanceAfter = await DebitAsync(connection, transaction, request, cancellationToken);
        await InsertLedgerAsync(connection, transaction, request, balanceAfter, cancellationToken);
        return new DebitOutcome(
            request.EventId,
            request.UserId,
            request.Amount,
            DebitStatuses.Applied,
            balanceAfter,
            DuplicateDelivery: false);
    }

    private static async Task<bool> TryClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WalletDebitRequested request,
        CancellationToken cancellationToken)
    {
        // The duplicate-key error is handled inside SQL so the transaction stays usable.
        // A concurrent delivery of the same EventId blocks on this insert until we commit,
        // then takes the CATCH path and replays the stored outcome.
        const string sql = """
            BEGIN TRY
                INSERT INTO dbo.InboxMessages (EventId, UserId, Amount, Status, BalanceAfter, ProcessedAt)
                VALUES (@EventId, @UserId, @Amount, N'Processing', NULL, SYSUTCDATETIME());
                SELECT CAST(1 AS bit);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() IN (2601, 2627)
                    SELECT CAST(0 AS bit);
                ELSE
                    THROW;
            END CATCH
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Guid("@EventId", request.EventId));
        command.Parameters.Add(SqlParams.Guid("@UserId", request.UserId));
        command.Parameters.Add(SqlParams.Money("@Amount", request.Amount));
        var claimed = await command.ExecuteScalarAsync(cancellationToken);
        return claimed is true;
    }

    private static async Task<StoredClaim?> ReadClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EventId, UserId, Amount, Status, BalanceAfter
            FROM dbo.InboxMessages
            WHERE EventId = @EventId
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Guid("@EventId", eventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredClaim(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetDecimal(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4));
    }

    private static async Task<decimal?> LockBalanceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Balance
            FROM dbo.Wallets WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
            WHERE UserId = @UserId
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Guid("@UserId", userId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (decimal)value;
    }

    private static async Task<decimal> DebitAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WalletDebitRequested request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Wallets
            SET Balance = Balance - @Amount,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.Balance
            WHERE UserId = @UserId
              AND Balance >= @Amount
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Guid("@UserId", request.UserId));
        command.Parameters.Add(SqlParams.Money("@Amount", request.Amount));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Wallet {request.UserId} changed while its row lock was held. The debit was rolled back.");
        }

        return (decimal)value;
    }

    private static async Task InsertLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WalletDebitRequested request,
        decimal balanceAfter,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.WalletLedger (Id, EventId, UserId, Amount, Direction, BalanceAfter, CreatedAt)
            VALUES (@Id, @EventId, @UserId, @Amount, N'Debit', @BalanceAfter, SYSUTCDATETIME())
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Guid("@Id", Guid.NewGuid()));
        command.Parameters.Add(SqlParams.Guid("@EventId", request.EventId));
        command.Parameters.Add(SqlParams.Guid("@UserId", request.UserId));
        command.Parameters.Add(SqlParams.Money("@Amount", request.Amount));
        command.Parameters.Add(SqlParams.Money("@BalanceAfter", balanceAfter));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DebitOutcome outcome,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.InboxMessages
            SET Status = @Status,
                BalanceAfter = @BalanceAfter,
                ProcessedAt = SYSUTCDATETIME()
            WHERE EventId = @EventId
              AND Status = N'Processing'
            """;

        await using var command = Create(connection, transaction, sql);
        command.Parameters.Add(SqlParams.Text("@Status", outcome.Status, 40));
        command.Parameters.Add(SqlParams.MoneyOrNull("@BalanceAfter", outcome.BalanceAfter));
        command.Parameters.Add(SqlParams.Guid("@EventId", outcome.EventId));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException($"Could not finalize inbox row for event {outcome.EventId}.");
        }
    }

    private static SqlCommand Create(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command;
    }

    private sealed record StoredClaim(
        Guid EventId,
        Guid UserId,
        decimal Amount,
        string Status,
        decimal? BalanceAfter);
}
