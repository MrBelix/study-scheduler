var builder = DistributedApplication.CreateBuilder(args);

// SQL Server runs as a container (mcr.microsoft.com/mssql/server) with an auto-generated
// password. The database resource is named "Default" so the API receives it as the
// "ConnectionStrings:Default" connection string via WithReference.
var db = builder.AddSqlServer("sql")
    .AddDatabase("Default");

// Telegram bot token: real value comes from AppHost user-secrets locally; falls back to the
// fixed test token so integration tests can mint matching initData against the same token.
var botToken = builder.Configuration["TelegramAuth:BotToken"] ?? "123456:TEST-bot-token";

// Webhook secret enables POST /telegram/webhook; the fixed fallback lets integration tests
// call it. No webhook URL is set here, so nothing is registered with Telegram.
var webhookSecret = builder.Configuration["Notifications:WebhookSecret"] ?? "TEST-webhook-secret";

builder.AddProject<Projects.StudyScheduler_API>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithEnvironment("TelegramAuth__BotToken", botToken)
    .WithEnvironment("Notifications__WebhookSecret", webhookSecret);

builder.Build().Run();
