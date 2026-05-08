using Microsoft.EntityFrameworkCore;
using StudyScheduler.Bot;
using StudyScheduler.Bot.Core;
using StudyScheduler.Bot.Core.Routing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<TelegramUpdateHandler>();
builder.Services.AddHostedService<BotWorker>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("StudySchedulerDatabase"));

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

host.Run();