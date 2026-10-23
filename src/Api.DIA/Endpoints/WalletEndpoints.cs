using Api.DIA.Hosting;
using Api.DIA.Wallet;
using Dia.Contracts;
using Microsoft.Extensions.Options;

namespace Api.DIA.Endpoints;

public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (SchemaReady ready) =>
        {
            if (!ready.IsReady)
            {
                return Results.Json(new { status = "starting", instance = Environment.MachineName }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { status = "healthy", instance = Environment.MachineName });
        })
        .WithName("Health")
        .WithTags("Health");

        var wallets = app.MapGroup("/api/wallets").WithTags("Wallets");

        wallets.MapGet("/{userId:guid}", async (Guid userId, WalletStore store, CancellationToken cancellationToken) =>
        {
            var wallet = await store.GetAsync(userId, cancellationToken);
            return wallet is null ? Results.NotFound(new { error = "Wallet was not found.", userId }) : Results.Ok(wallet);
        })
        .WithName("GetWallet");

        wallets.MapGet("/{userId:guid}/debits", async (Guid userId, WalletStore store, CancellationToken cancellationToken) =>
        {
            var debits = await store.ListDebitsAsync(userId, cancellationToken);
            return Results.Ok(debits);
        })
        .WithName("ListDebits");

        wallets.MapGet("/{userId:guid}/ledger", async (Guid userId, WalletStore store, CancellationToken cancellationToken) =>
        {
            var ledger = await store.ListLedgerAsync(userId, cancellationToken);
            return Results.Ok(ledger);
        })
        .WithName("ListLedger");

        wallets.MapPost("/{userId:guid}/debits", async (
            Guid userId,
            DebitBody body,
            WalletDebitService debits,
            CancellationToken cancellationToken) =>
        {
            if (body.EventId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "eventId is required." });
            }

            var outcome = await debits.HandleAsync(new WalletDebitRequested
            {
                EventId = body.EventId,
                UserId = userId,
                Amount = body.Amount,
                OccurredAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return ToHttp(outcome);
        })
        .WithName("DebitWallet");

        app.MapPost("/api/demo/wallets/{userId:guid}/reset", async (
            Guid userId,
            ResetBody body,
            WalletStore store,
            IOptions<DemoOptions> demo,
            CancellationToken cancellationToken) =>
        {
            if (!demo.Value.AllowReset)
            {
                return Results.Json(
                    new { error = "Demo reset is disabled. Set Demo:AllowReset to true to use it." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (body.Balance < 0)
            {
                return Results.BadRequest(new { error = "balance cannot be negative." });
            }

            var wallet = await store.ResetBalanceAsync(userId, body.Balance, cancellationToken);
            return Results.Ok(wallet);
        })
        .WithName("ResetDemoWallet")
        .WithTags("Demo");

        return app;
    }

    private static IResult ToHttp(DebitOutcome outcome)
    {
        var body = new
        {
            outcome.EventId,
            outcome.UserId,
            outcome.Amount,
            outcome.Status,
            outcome.BalanceAfter,
            outcome.DuplicateDelivery,
            handledBy = Environment.MachineName
        };

        return outcome.Status switch
        {
            DebitStatuses.Applied => Results.Ok(body),
            DebitStatuses.RejectedInsufficientFunds => Results.Conflict(body),
            DebitStatuses.RejectedWalletNotFound => Results.NotFound(body),
            DebitStatuses.RejectedInvalidAmount => Results.BadRequest(body),
            _ => Results.Json(body, statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    public sealed record DebitBody(Guid EventId, decimal Amount);

    public sealed record ResetBody(decimal Balance);
}
