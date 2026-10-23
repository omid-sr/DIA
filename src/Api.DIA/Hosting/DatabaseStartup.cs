using Api.DIA.Data;
using Microsoft.Extensions.Options;

namespace Api.DIA.Hosting;

public sealed class DatabaseStartup(
    IConfiguration configuration,
    IOptions<DemoOptions> demo,
    SchemaReady ready,
    ILogger<DatabaseStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Wallet")
            ?? throw new InvalidOperationException("ConnectionStrings:Wallet is required.");
        var delay = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WalletSchema.EnsureAsync(
                    connectionString,
                    demo.Value.UserId,
                    demo.Value.InitialBalance,
                    stoppingToken);
                ready.SetReady();
                logger.LogInformation(
                    "Wallet database is ready. Demo user {UserId} is created with {Balance} when the row is missing.",
                    demo.Value.UserId,
                    demo.Value.InitialBalance);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SQL Server is not ready. Retrying in {DelaySeconds}s.", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }
}
