using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Dia.Contracts;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var rabbit = configuration.GetSection("RabbitMq").Get<RabbitSettings>() ?? new RabbitSettings();
var apiBase = configuration["WalletApi:BaseUrl"] ?? "http://localhost:8080";
var userId = configuration.GetValue("Demo:UserId", DemoWallet.UserId);
var amount = configuration.GetValue("Demo:Amount", DemoWallet.DebitAmount);
var opening = configuration.GetValue("Demo:OpeningBalance", DemoWallet.OpeningBalance);

Console.WriteLine($"Wallet API: {apiBase}");
Console.WriteLine($"RabbitMQ: {rabbit.Host}:{rabbit.Port}");
Console.WriteLine($"User {userId} opens with {opening:0.00}. Two different charges of {amount:0.00} will be published.");

using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(10) };
await WaitForApiAsync(http);

var reset = await http.PostAsJsonAsync($"/api/demo/wallets/{userId}/reset", new { balance = opening });
reset.EnsureSuccessStatusCode();
Console.WriteLine($"Reset wallet to {opening:0.00}.");

var first = new WalletDebitRequested
{
    EventId = Guid.NewGuid(),
    UserId = userId,
    Amount = amount,
    OccurredAt = DateTimeOffset.UtcNow
};
var second = new WalletDebitRequested
{
    EventId = Guid.NewGuid(),
    UserId = userId,
    Amount = amount,
    OccurredAt = DateTimeOffset.UtcNow
};

var results = new ConcurrentBag<WalletDebitCompleted>();
await using var connection = await ConnectAsync(rabbit);
await using var publisher = await connection.CreateChannelAsync();
await using var subscriber = await connection.CreateChannelAsync();
await WalletTopology.DeclareAsync(publisher, CancellationToken.None);

var consumer = new AsyncEventingBasicConsumer(subscriber);
consumer.ReceivedAsync += async (_, delivery) =>
{
    var copy = delivery.Body.ToArray();
    var completed = JsonSerializer.Deserialize<WalletDebitCompleted>(copy, ContractJson.Options);
    if (completed is not null && (completed.EventId == first.EventId || completed.EventId == second.EventId))
    {
        results.Add(completed);
    }

    await subscriber.BasicAckAsync(delivery.DeliveryTag, multiple: false);
};
await subscriber.BasicConsumeAsync(WalletMessaging.ResultQueue, autoAck: false, consumer);

await PublishAsync(publisher, first);
await PublishAsync(publisher, second);
await PublishAsync(publisher, first);
Console.WriteLine($"Published {first.EventId}");
Console.WriteLine($"Published {second.EventId}");
Console.WriteLine($"Republished {first.EventId} so a duplicate delivery cannot charge the wallet again.");

var debits = await WaitForDebitsAsync(http, userId, [first.EventId, second.EventId]);
var wallet = await http.GetFromJsonAsync<WalletView>($"/api/wallets/{userId}")
    ?? throw new InvalidOperationException("Wallet response was empty.");
var ledger = await http.GetFromJsonAsync<List<LedgerView>>($"/api/wallets/{userId}/ledger") ?? [];

Console.WriteLine();
Console.WriteLine("Broker results:");
if (results.IsEmpty)
{
    Console.WriteLine("  (none observed before the HTTP check finished)");
}

foreach (var result in results.OrderBy(item => item.CompletedAt))
{
    Console.WriteLine(
        $"  {result.EventId} {result.Status} duplicate={result.DuplicateDelivery} balanceAfter={result.BalanceAfter} handledBy={result.HandledBy}");
}

Console.WriteLine();
Console.WriteLine("Stored outcomes:");
foreach (var debit in debits.Where(item => item.EventId == first.EventId || item.EventId == second.EventId))
{
    Console.WriteLine($"  {debit.EventId} {debit.Status} balanceAfter={debit.BalanceAfter}");
}

var relevantLedger = ledger.Where(item => item.EventId == first.EventId || item.EventId == second.EventId).ToArray();
var applied = debits.Count(item =>
    (item.EventId == first.EventId || item.EventId == second.EventId) && item.Status == DebitStatuses.Applied);
var rejected = debits.Count(item =>
    (item.EventId == first.EventId || item.EventId == second.EventId) && item.Status == DebitStatuses.RejectedInsufficientFunds);

Console.WriteLine();
Console.WriteLine($"Final balance: {wallet.Balance:0.00}");
Console.WriteLine($"Applied charges: {applied}");
Console.WriteLine($"Rejected charges: {rejected}");
Console.WriteLine($"Ledger rows for these events: {relevantLedger.Length}");

var expectedBalance = opening - amount;
var passed = wallet.Balance == expectedBalance && applied == 1 && rejected == 1 && relevantLedger.Length == 1;
Console.WriteLine(passed
    ? "PASS: one charge was applied and the duplicate delivery did not move the balance."
    : "FAIL: the wallet did not end with exactly one applied charge.");
return passed ? 0 : 1;

static async Task WaitForApiAsync(HttpClient http)
{
    for (var attempt = 1; attempt <= 60; attempt++)
    {
        try
        {
            using var response = await http.GetAsync("/health");
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API is healthy ({await response.Content.ReadAsStringAsync()}).");
                return;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    throw new TimeoutException("The wallet API did not become healthy.");
}

static async Task<List<DebitView>> WaitForDebitsAsync(HttpClient http, Guid userId, Guid[] eventIds)
{
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        var debits = await http.GetFromJsonAsync<List<DebitView>>($"/api/wallets/{userId}/debits") ?? [];
        if (eventIds.All(id => debits.Any(debit => debit.EventId == id)))
        {
            return debits;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException("The wallet API did not record both charges in time.");
}

static async Task<IConnection> ConnectAsync(RabbitSettings settings)
{
    var factory = new ConnectionFactory
    {
        HostName = settings.Host,
        Port = settings.Port,
        VirtualHost = settings.VirtualHost,
        UserName = settings.User,
        Password = settings.Password
    };

    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            return await factory.CreateConnectionAsync("dia-producer");
        }
        catch (Exception) when (attempt < 30)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    return await factory.CreateConnectionAsync("dia-producer");
}

static async Task PublishAsync(IChannel channel, WalletDebitRequested request)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options);
    var properties = new BasicProperties
    {
        ContentType = "application/json",
        DeliveryMode = DeliveryModes.Persistent,
        MessageId = request.EventId.ToString(),
        Type = nameof(WalletDebitRequested)
    };

    await channel.BasicPublishAsync(
        WalletMessaging.Exchange,
        WalletMessaging.DebitRoutingKey,
        mandatory: false,
        basicProperties: properties,
        body: body);
}

internal sealed class RabbitSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

internal sealed record WalletView(Guid UserId, decimal Balance);

internal sealed record DebitView(Guid EventId, string Status, decimal? BalanceAfter);

internal sealed record LedgerView(Guid EventId, decimal Amount);
