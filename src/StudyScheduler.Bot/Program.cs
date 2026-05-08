using StudyScheduler.Bot;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<BotWorker>();

var host = builder.Build();
host.Run();