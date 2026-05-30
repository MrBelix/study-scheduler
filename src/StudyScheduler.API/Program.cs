using System.Security.Claims;
using Scalar.AspNetCore;
using StudyScheduler.API.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<TelegramSecuritySchemeTransformer>();
});

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<TelegramAuthOptions>()
    .Bind(builder.Configuration.GetSection("TelegramAuth"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BotToken), "TelegramAuth:BotToken is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<TelegramInitDataValidator>();

builder.Services.AddAuthentication(TelegramAuthOptions.Scheme)
    .AddScheme<TelegramAuthOptions, TelegramAuthenticationHandler>(
        TelegramAuthOptions.Scheme, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    Id = user.FindFirstValue(ClaimTypes.NameIdentifier),
    Username = user.FindFirstValue(TelegramClaimTypes.Username),
    FirstName = user.FindFirstValue(ClaimTypes.GivenName),
    LastName = user.FindFirstValue(ClaimTypes.Surname),
}))
.RequireAuthorization();

app.Run();
