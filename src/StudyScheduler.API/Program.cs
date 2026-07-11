using System.Text.Json.Serialization;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.Cors;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.OpenApi;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.API.Core.RateLimiting;
using StudyScheduler.API.Features.Profile;
using StudyScheduler.API.Features.Students;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence();

builder.Services.AddApiDocumentation();
builder.Services.AddSingleton(TimeProvider.System);

// Serialize enums as strings (e.g. StudentStatus → "Active") for a friendlier API contract.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddGlobalErrorHandling();
builder.Services.AddWriteRateLimiting(builder.Configuration);
builder.Services.AddTelegramAuthentication();
builder.Services.AddMiniAppCors(builder.Configuration, builder.Environment);
builder.Services.AddStudentsFeature();
builder.Services.AddProfileFeature();

var app = builder.Build();

app.ApplyMigrations();

app.UseGlobalErrorHandling();
app.UseApiDocumentation();
app.UseHttpsRedirection();
app.UseMiniAppCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStudentsFeature();
app.MapProfileFeature();
app.MapDefaultEndpoints();

app.Run();
