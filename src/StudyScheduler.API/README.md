# StudyScheduler.API

The **web host** and application entry point — an ASP.NET Core (.NET 10) Minimal API. Kept thin: it
wires infrastructure and maps feature modules, nothing more.

## `Program.cs`

Declarative and short. It composes the app from extension methods:

```csharp
builder.AddServiceDefaults();          // Aspire: OTel, health, resilience
builder.AddPersistence();              // EF Core / PostgreSQL
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
  Tenancy/                  Scoped tutor context: global query filters + insert stamping
  Cors/                     Mini App CORS policy
  OpenApi/                  OpenAPI + Scalar wiring (Development only)
Features/                   Vertical slices
  Students/                 StudentsModule, Endpoints, request/response DTOs, EF repo binding
```

## Authentication

Requests carry `Authorization: tma <initData>`. `TelegramAuthenticationHandler` validates the init
data on every request (HMAC-SHA256 over the data-check-string; no `auth_date` TTL — initData never
refreshes in a running WebView) and projects the Telegram user into claims. The tutor id (`long`) is read from the principal — never from the
request body. Use `ClaimsPrincipalExtensions.GetTelegramId()` as the single access point.

## Tenancy

Ownership is enforced in one place, not in every query. `ITutorContext` (scoped) holds the tutor of
the current scope; `UseTenancy()` fills it from the already-validated principal right after
`UseAuthentication()`. `AppDbContext` applies a global query filter on that value to every
tutor-owned table (`Students`, `Lessons`, `LessonSeries`, `TutorProfiles`) and stamps it on insert.
Stamping only ever FILLS an owner in: a row that already names this tutor passes untouched, and one
that names another tutor is refused with an `InvalidOperationException` instead of being written
across the boundary the filters draw.

It fails closed: a scope with no tutor reads NO rows, because the filters then compare against the
sentinel `0` — an id no row can wear, since every tutor-owned table carries a `CHECK ("…Id" > 0)`
constraint. Work without an authenticated caller therefore
has to say which tenant it means through `ITutorScope.SetForBackground()` — the nightly generator and
the notification poller walk tenants one at a time, and the anonymous Telegram webhook takes its
tenant from the update payload (after a constant-time check of the shared secret header — that gate
is what authorizes the payload to name a tenant at all). `SetForBackground` throws inside an
authenticated request: an incoming call can never talk its way into another tenant. Nor can a scope's
tutor move once established: `SetFromAuthentication` is idempotent for the same id and throws for a
different one. The only queries allowed to span tenants
say so in their name (`...AcrossAllTutorsAsync`) and are the only users of `IgnoreQueryFilters`.

Because the filter owns scoping, ownership is not threaded through the code: repository methods,
`LessonService`, the readers and the endpoints take no `tutorTelegramId`, and a cross-tenant id
simply reads as missing (404 / "not found" validation) instead of being compared by hand. The tutor
id is still read in three places, and only there: the log lines of `LessonOverlapChecker` and
`ReportDashboardService`, `PUT /profile` when it creates the profile that is KEYED by it, and the
rate limiter's per-user partition (from the principal's claim).

## Persistence

`AddPersistence()` registers `AppDbContext` with `AddDbContext` and then `EnrichNpgsqlDbContext`
(health check, retries, telemetry) — NOT Aspire's own `AddNpgsqlDbContext`, which
pools the context, and a pooled `DbContext` may not take constructor dependencies (`AppDbContext` needs the scoped
`ITutorContext`). `app.ApplyMigrations()` applies pending migrations on startup. Connection string comes from configuration (`ConnectionStrings:Default`) — a local
PostgreSQL container via the AppHost, or the Dokploy database service in production.

Instants are `DateTimeOffset` mapped to `timestamp with time zone` and forced to a zero offset by
`UtcTimestampConversion` (a model-wide convention in `AppDbContext.ConfigureConventions`), because
Npgsql refuses to write a non-zero offset to `timestamptz`. Wall-clock times are `TimeOnly` +
an IANA zone id, and dates are `DateOnly` — so no column needs `timestamp without time zone`.

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
| `GET`   | `/students/{id}/debts` | The student's unpaid completed lessons + totals |

API docs (Development only): Scalar UI at `/scalar`.
