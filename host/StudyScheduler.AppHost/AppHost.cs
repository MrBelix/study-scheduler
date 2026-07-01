var builder = DistributedApplication.CreateBuilder(args);

// SQL Server runs as a container (mcr.microsoft.com/mssql/server) with an auto-generated
// password. The database resource is named "Default" so the API receives it as the
// "ConnectionStrings:Default" connection string via WithReference.
var db = builder.AddSqlServer("sql")
    .AddDatabase("Default");

// Telegram bot token: real value comes from AppHost user-secrets locally; falls back to the
// fixed test token so integration tests can mint matching initData against the same token.
var botToken = builder.Configuration["TelegramAuth:BotToken"] ?? "123456:TEST-bot-token";

builder.AddProject<Projects.StudyScheduler_API>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithEnvironment("TelegramAuth__BotToken", botToken);

builder.Build().Run();
