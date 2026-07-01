using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.Cors;
using StudyScheduler.API.Core.OpenApi;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.API.Features.Students;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence();

builder.Services.AddApiDocumentation();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTelegramAuthentication();
builder.Services.AddMiniAppCors(builder.Configuration, builder.Environment);
builder.Services.AddStudentsFeature();

var app = builder.Build();

app.ApplyMigrations();

app.UseApiDocumentation();
app.UseHttpsRedirection();
app.UseMiniAppCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapCurrentUser();
app.MapStudentsFeature();
app.MapDefaultEndpoints();

app.Run();
