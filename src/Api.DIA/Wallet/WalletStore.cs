using Api.DIA.Data;
using Microsoft.Data.SqlClient;

namespace Api.DIA.Wallet;

public sealed class WalletStore(string connectionString)
{
    public async Task<bool> CanQueryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.Wallets', N'U') IS NULL THEN 0 ELSE 1 END;";
        var ready = await command.ExecuteScalarAsync(cancellationToken);
        return ready is int value && value == 1;
    }

    public async Task<WalletSnapshot?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT UserId, Balance, UpdatedAt
            FROM dbo.Wallets
            WHERE UserId = @UserId
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(SqlParams.Guid("@UserId", userId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WalletSnapshot(reader.GetGuid(0), reader.GetDecimal(1), reader.GetDateTime(2));
    }

    public async Task<IReadOnlyList<DebitRecord>> ListDebitsAsync(Guid userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EventId, UserId, Amount, Status, BalanceAfter, ProcessedAt
            FROM dbo.InboxMessages
            WHERE UserId = @UserId
              AND Status <> N'Processing'
            ORDER BY ProcessedAt DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(SqlParams.Guid("@UserId", userId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<DebitRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DebitRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetDecimal(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.GetDateTime(5)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<LedgerRecord>> ListLedgerAsync(Guid userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, EventId, UserId, Amount, Direction, BalanceAfter, CreatedAt
            FROM dbo.WalletLedger
            WHERE UserId = @UserId
            ORDER BY CreatedAt DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(SqlParams.Guid("@UserId", userId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<LedgerRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetDecimal(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetDateTime(6)));
        }

        return rows;
    }

    public async Task<WalletSnapshot> ResetBalanceAsync(Guid userId, decimal balance, CancellationToken cancellationToken)
    {
        if (balance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(balance), "Balance cannot be negative.");
        }

        const string sql = """
            MERGE dbo.Wallets WITH (HOLDLOCK) AS target
            USING (SELECT @UserId AS UserId) AS source
            ON target.UserId = source.UserId
            WHEN MATCHED THEN
                UPDATE SET Balance = @Balance, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (UserId, Balance, UpdatedAt)
                VALUES (@UserId, @Balance, SYSUTCDATETIME())
            OUTPUT INSERTED.UserId, INSERTED.Balance, INSERTED.UpdatedAt;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(SqlParams.Guid("@UserId", userId));
        command.Parameters.Add(SqlParams.Money("@Balance", balance));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Could not reset wallet {userId}.");
        }

        return new WalletSnapshot(reader.GetGuid(0), reader.GetDecimal(1), reader.GetDateTime(2));
    }
}
