using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Api.DIA.Data;

public static partial class WalletSchema
{
    public static async Task EnsureAsync(
        string connectionString,
        Guid demoUserId,
        decimal openingBalance,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "DiaWallet" : builder.InitialCatalog;
        if (!DatabaseName().IsMatch(database))
        {
            throw new InvalidOperationException($"Database name '{database}' is not allowed.");
        }

        var master = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            TrustServerCertificate = true
        };

        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        builder.InitialCatalog = database;
        builder.TrustServerCertificate = true;

        await using var wallet = new SqlConnection(builder.ConnectionString);
        await wallet.OpenAsync(cancellationToken);
        await using (var command = wallet.CreateCommand())
        {
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var seed = wallet.CreateCommand();
        seed.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.Wallets WHERE UserId = @UserId)
                INSERT INTO dbo.Wallets (UserId, Balance, UpdatedAt)
                VALUES (@UserId, @Balance, SYSUTCDATETIME());
            """;
        seed.Parameters.Add(SqlParams.Guid("@UserId", demoUserId));
        seed.Parameters.Add(SqlParams.Money("@Balance", openingBalance));
        try
        {
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2627 or 2601)
        {
            // Another replica inserted the demo wallet between the existence check and the insert.
        }
    }

    private const string SchemaSql = """
        IF OBJECT_ID(N'dbo.Wallets', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Wallets
            (
                UserId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Wallets PRIMARY KEY,
                Balance DECIMAL(19, 4) NOT NULL,
                UpdatedAt DATETIME2(3) NOT NULL,
                CONSTRAINT CK_Wallets_Balance_NonNegative CHECK (Balance >= 0)
            );
        END;

        IF OBJECT_ID(N'dbo.InboxMessages', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.InboxMessages
            (
                EventId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InboxMessages PRIMARY KEY,
                UserId UNIQUEIDENTIFIER NOT NULL,
                Amount DECIMAL(19, 4) NOT NULL,
                Status NVARCHAR(40) NOT NULL,
                BalanceAfter DECIMAL(19, 4) NULL,
                ProcessedAt DATETIME2(3) NOT NULL
            );

            CREATE INDEX IX_InboxMessages_UserId ON dbo.InboxMessages (UserId, ProcessedAt DESC);
        END;

        IF OBJECT_ID(N'dbo.WalletLedger', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.WalletLedger
            (
                Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_WalletLedger PRIMARY KEY,
                EventId UNIQUEIDENTIFIER NOT NULL,
                UserId UNIQUEIDENTIFIER NOT NULL,
                Amount DECIMAL(19, 4) NOT NULL,
                Direction NVARCHAR(8) NOT NULL,
                BalanceAfter DECIMAL(19, 4) NOT NULL,
                CreatedAt DATETIME2(3) NOT NULL,
                CONSTRAINT UQ_WalletLedger_EventId UNIQUE (EventId)
            );

            CREATE INDEX IX_WalletLedger_UserId ON dbo.WalletLedger (UserId, CreatedAt DESC);
        END;
        """;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex DatabaseName();
}
