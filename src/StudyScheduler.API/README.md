# StudyScheduler.API

The **web host** and application entry point — an ASP.NET Core (.NET 10) Minimal API. Kept thin: it
wires infrastructure and maps feature modules, nothing more.

## `Program.cs`

Declarative and short. It composes the app from extension methods:

```csharp
builder.AddServiceDefaults();          // Aspire: OTel, health, resilience
builder.AddPersistence();              // EF Core / SQL Server
builder.Services.AddTelegramAuthentication();
builder.Services.AddMiniAppCors(...);
builder.Services.AddStudentsFeature(); // one line per feature
...
app.MapStudentsFeature();              // one line per feature
```

## Layout

```
Core/                       Cross-cutting infrastructure (Add…/Use…/Map… extensions)
  Authentication/           Telegram Mini App auth scheme, init-data validator, claims
  Persistence/              AppDbContext, EF config, EF repository, Migrations/
  Cors/                     Mini App CORS policy
  OpenApi/                  OpenAPI + Scalar wiring (Development only)
Features/                   Vertical slices
  Students/                 StudentsModule, Endpoints, request/response DTOs, EF repo binding
```

## Authentication

Requests carry `Authorization: tma <initData>`. `TelegramAuthenticationHandler` validates the init
data on every request (HMAC-SHA256 over the data-check-string + `auth_date` freshness) and projects
the Telegram user into claims. The tutor id (`long`) is read from the principal — never from the
request body. Use `ClaimsPrincipalExtensions.GetTelegramId()` as the single access point.

## Persistence

`AddPersistence()` registers `AppDbContext` via the Aspire SQL client integration
(`AddSqlServerDbContext<AppDbContext>("Default")`); `app.ApplyMigrations()` applies pending migrations
on startup. Connection string comes from configuration (`ConnectionStrings:Default`) — a local
container via the AppHost, or Azure SQL in production.

Add a migration:

```bash
dotnet ef migrations add <Name> --project src/StudyScheduler.API --output-dir Core/Persistence/Migrations
```

## Adding a feature

Create `Features/<Name>/` with:

- `<Name>Module.cs` — `Add<Name>Feature(IServiceCollection)` (register services/repository) and
  `Map<Name>Feature(IEndpointRouteBuilder)` (map routes),
- `Endpoints.cs` — the HTTP handlers (`internal static`),
- request/response records.

Then add one `builder.Services.Add<Name>Feature()` and one `app.Map<Name>Feature()` line to
`Program.cs`. Don't put feature registrations or route tables directly in `Program.cs`.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET`   | `/students`      | List the current tutor's students |
| `GET`   | `/students/{id}` | One student (scoped; foreign id → 404) |
| `POST`  | `/students`      | Create a student |
| `PATCH` | `/students/{id}` | Update / archive a student |

API docs (Development only): Scalar UI at `/scalar`.
