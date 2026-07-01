# StudyScheduler

A pocket CRM for private tutors, delivered as a **Telegram Mini App**. A tutor opens the app inside
Telegram to manage students, schedules and finances without juggling Excel, notes and their head.

This repository contains the **backend** (.NET 10 API + orchestration). The React Mini App frontend
lives separately.

## How this documentation is organised

The docs follow the [Diátaxis](https://diataxis.fr/) framework — four modes that serve distinct
needs. Jump to whichever fits what you're doing:

| Mode | Answers | Section |
|---|---|---|
| **Tutorial** | "I'm new — get me running." | [Getting started](#tutorial--getting-started) |
| **How-to guides** | "How do I do task X?" | [How-to guides](#how-to-guides) |
| **Reference** | "What are the facts?" | [Reference](#reference) |
| **Explanation** | "Why is it built this way?" | [Explanation](#explanation) |

Each project also has its own `README.md` with details specific to it.

---

## Tutorial — getting started

Goal: run the whole backend on your machine and see it respond. Requires **Docker** and the
**.NET 10 SDK**.

1. Give the app a Telegram bot token (from [@BotFather](https://t.me/BotFather)):

   ```bash
   dotnet user-secrets set "TelegramAuth:BotToken" "<token>" --project host/StudyScheduler.AppHost
   ```

2. Start everything via .NET Aspire — this spins up a SQL Server container, the API, and the Aspire
   dashboard:

   ```bash
   dotnet run --project host/StudyScheduler.AppHost
   ```

3. Open the **Aspire dashboard** (the URL is printed in the console) to watch the resources come up,
   then open the API's Scalar UI at `/scalar` to explore the endpoints.

That's it — you have the API talking to a real database locally. Next, read the
[How-to guides](#how-to-guides) for everyday tasks, or [Explanation](#explanation) for the why.

---

## How-to guides

### Add a feature

1. Create `src/StudyScheduler.API/Features/<Name>/` with:
   - `<Name>Module.cs` — `Add<Name>Feature(IServiceCollection)` (services/repository) and
     `Map<Name>Feature(IEndpointRouteBuilder)` (routes),
   - `Endpoints.cs` — the HTTP handlers (`internal static`),
   - request/response records.
2. Add one `builder.Services.Add<Name>Feature()` and one `app.Map<Name>Feature()` line to
   `Program.cs`.

Put the domain model (entities, repository interface) in `StudyScheduler.Domain`.

### Add a database migration

```bash
dotnet ef migrations add <Name> --project src/StudyScheduler.API --output-dir Core/Persistence/Migrations
```

Migrations are applied automatically on startup (`app.ApplyMigrations()`).

### Run the tests

```bash
dotnet test tests/StudyScheduler.Tests/StudyScheduler.Tests.csproj                        # unit — no Docker
dotnet test tests/StudyScheduler.IntegrationTests/StudyScheduler.IntegrationTests.csproj  # integration — needs Docker
```

> **Rider on Windows:** the Aspire integration tests hang under Rider's Aspire-plugin test runner
> (known JetBrains issue). Run them from the CLI, or disable the Aspire plugin.

### Deploy

Push to `main` — the GitHub Actions pipeline runs tests, then (only if they pass) publishes the API
and deploys to Azure App Service. See [CI/CD](#cicd) under Reference for the details.

---

## Reference

### Tech stack

| Layer | Technology |
|---|---|
| Runtime | C# / .NET 10, ASP.NET Core (Minimal APIs) |
| Persistence | EF Core → SQL Server / **Azure SQL** (serverless) |
| Orchestration | .NET Aspire (local dev + integration tests) |
| Auth | Telegram Mini App `initData` (HMAC-SHA256) |
| API docs | OpenAPI + Scalar (Development only) |
| Tests | xUnit (unit + Aspire integration) |
| CI/CD | GitHub Actions → Azure App Service (Linux) |

### Solution layout

```
StudyScheduler.slnx
├── src/
│   ├── StudyScheduler.API/          ASP.NET Core Web API (entry point)
│   └── StudyScheduler.Domain/       Domain model — no external dependencies
├── host/
│   ├── StudyScheduler.AppHost/      .NET Aspire orchestrator (SQL Server + API)
│   └── StudyScheduler.ServiceDefaults/  Shared OTel / health checks / resilience
└── tests/
    ├── StudyScheduler.Tests/            Unit tests (domain + auth validator)
    └── StudyScheduler.IntegrationTests/ End-to-end tests over a real SQL container
```

Each project's `README.md` documents it in detail.

### API endpoints

The endpoint reference is the **live OpenAPI document** — browse it via the Scalar UI at `/scalar`
(Development). It's generated from the code, so it never goes stale. All endpoints require a valid
`Authorization: tma <initData>` header; the tutor is derived from the token, never the request body.

### Configuration

Runtime configuration is read from standard .NET config (`appsettings`, environment variables,
user-secrets, or App Service settings). Environment variables use `__` for nested keys.

| Key | Notes |
|---|---|
| `TelegramAuth__BotToken` | **Required** — the app fails to start without it |
| `ConnectionStrings__Default` | SQL Server / Azure SQL connection string (App Service: a `Default` connection string of type `SQLAzure`) |
| `Cors__AllowedOrigins__0` | Frontend origin(s) allowed in production |

### CI/CD

`.github/workflows/main_studyscheduler.yml` runs one pipeline on push to `main`:

```
unit ─┐
      ├─► build (publishes ONLY the API project) ─► deploy (OIDC → Azure App Service)
integration ─┘
```

Deploy runs only after both test suites pass. Production is **Azure App Service (Linux)** + **Azure
SQL**. On Linux the App Service **Startup Command** must be `dotnet StudyScheduler.API.dll`
(Configuration → Stack settings), otherwise the startup script can't pick an entry point.

---

## Explanation

### Authentication — Telegram Mini App init data

Every request carries `Authorization: tma <initData>`. A custom `AuthenticationHandler` validates the
init data on **every** request (no server-side session): it verifies the HMAC-SHA256 signature over
the data-check-string, checks `auth_date` freshness, and projects the Telegram user into claims. This
suits a Mini App, where Telegram already signs fresh init data on each launch — so there's no login
flow or session to manage.

Resource ownership is scoped by the Telegram user id (`long`) from the authenticated principal. There
is deliberately **no separate `Account` entity**: the product is Telegram-only, so the Telegram id is
already a stable, unique identity, and a surrogate account would only add a lookup on every request.

### Persistence

EF Core on SQL Server. The DbContext is registered through the Aspire SQL client integration
(`AddSqlServerDbContext<AppDbContext>("Default")`, giving health checks + retries + telemetry), and
pending migrations are applied on startup. The connection string comes from configuration: a real SQL
Server container locally (via the AppHost) or Azure SQL in production. **Money is always `decimal`;
timestamps are UTC** — these are expensive to change once data exists, so they're fixed from the
start.

### Feature modules (vertical slices)

`Program.cs` is kept thin and declarative: cross-cutting infrastructure lives in `Core/*` extension
methods (`AddTelegramAuthentication`, `AddPersistence`, `AddMiniAppCors`, …), and each feature is a
self-contained slice under `Features/<Name>/` with a `<Name>Module` exposing `Add<Name>Feature` +
`Map<Name>Feature`. Adding a feature touches only its own folder plus two lines in `Program.cs`. This
keeps related code together and the composition root readable.

### Orchestration & testing with Aspire

.NET Aspire models the app topology (API + SQL Server) so a single command runs everything locally
with a dashboard. The same model powers integration tests: `Aspire.Hosting.Testing` boots the whole
app against a **real** SQL Server container, so tests exercise persistence, scoping and the real auth
pipeline end-to-end — not mocks. Unit tests stay Docker-free for a fast inner loop; integration tests
gate deployment in CI.

### Design principles

- **Thin `Program.cs`** — infrastructure in `Core/*` extensions, features as modules.
- **Scope everything by the authenticated tutor** — never trust ids from the request body.
- **Money is `decimal`, time is UTC.**
- **The server is the source of truth** — the client re-reads state, it doesn't compute policy.
