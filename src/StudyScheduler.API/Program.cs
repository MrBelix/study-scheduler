using System.Security.Claims;
using Scalar.AspNetCore;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Features.Students;
using StudyScheduler.Domain.Students;

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

// CORS for the Mini App client. Auth travels in the Authorization header (no
// cookies), so credentials aren't needed. In Development any origin is allowed
// to ease localhost/ngrok testing; production uses the configured allow-list.
const string CorsPolicy = "MiniApp";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (builder.Environment.IsDevelopment() && origins.Length == 0)
        policy.SetIsOriginAllowed(_ => true);
    else
        policy.WithOrigins(origins);
    policy.AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddSingleton<IStudentRepository, InMemoryStudentRepository>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors(CorsPolicy);

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

Endpoints.Map(app);

app.Run();
