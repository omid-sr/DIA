using Api.DIA.Endpoints;
using Api.DIA.Hosting;
using Api.DIA.Messaging;
using Api.DIA.Wallet;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Wallet")
    ?? throw new InvalidOperationException("ConnectionStrings:Wallet is required.");

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.SectionName));
builder.Services.AddSingleton(new WalletStore(connectionString));
builder.Services.AddSingleton(sp => new WalletDebitService(
    connectionString,
    sp.GetRequiredService<ILogger<WalletDebitService>>()));
builder.Services.AddSingleton<SchemaReady>();
builder.Services.AddSingleton<WalletResultPublisher>();
builder.Services.AddHostedService<DatabaseStartup>();
builder.Services.AddHostedService<WalletDebitConsumer>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Api.DIA");
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        logger.LogError(feature?.Error, "Request failed.");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "The wallet service could not complete the request." });
    });
});

app.MapOpenApi();
app.MapWalletEndpoints();

app.Run();
