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

2. Start everything via .NET Aspire — this spins up a PostgreSQL container, the API, and the Aspire
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

### Integrate the frontend

The React Mini App (separate repo, `StudySchedulerClient`) calls this API with an
`Authorization: tma <initData>` header, scoped to the current tutor. The endpoint reference is the
OpenAPI document (Scalar UI at `/scalar`); the client-side integration contract is documented in that
repo. To allow the frontend in production, set `Cors__AllowedOrigins__0` to its origin.

### Deploy

Push to `main` — GitHub Actions runs the test suites, and on green triggers **Dokploy**, which
builds `Dockerfile` from the repo root and restarts the container. See [Deployment](#deployment)
under Reference for the required environment variables.

---

## Reference

### Tech stack

| Layer | Technology |
|---|---|
| Runtime | C# / .NET 10, ASP.NET Core (Minimal APIs) |
| Persistence | EF Core → **PostgreSQL** (Npgsql) |
| Orchestration | .NET Aspire (local dev + integration tests) |
| Auth | Telegram Mini App `initData` (HMAC-SHA256) |
| API docs | OpenAPI + Scalar (Development only) |
| Tests | xUnit (unit + Aspire integration) |
| CI/CD | GitHub Actions (tests) + Dokploy (Docker build & deploy) |

### Solution layout

```
StudyScheduler.slnx
├── src/
│   ├── StudyScheduler.API/          ASP.NET Core Web API (entry point)
│   └── StudyScheduler.Domain/       Domain model — no external dependencies
├── host/
│   ├── StudyScheduler.AppHost/      .NET Aspire orchestrator (PostgreSQL + API)
│   └── StudyScheduler.ServiceDefaults/  Shared OTel / health checks / resilience
└── tests/
    ├── StudyScheduler.Tests/            Unit tests (domain + auth validator)
    └── StudyScheduler.IntegrationTests/ End-to-end tests over a real PostgreSQL container
```

Each project's `README.md` documents it in detail.

### API endpoints

The endpoint reference is the **live OpenAPI document** — browse it via the Scalar UI at `/scalar`
(Development). It's generated from the code, so it never goes stale. All endpoints require a valid
`Authorization: tma <initData>` header; the tutor is derived from the token, never the request body.

### Configuration

Runtime configuration is read from standard .NET config (`appsettings`, environment variables,
user-secrets). Environment variables use `__` for nested keys. **No secret is committed** —
`appsettings.json` holds only non-sensitive defaults.

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings__Default` | **yes** | PostgreSQL connection string, e.g. `Host=studyscheduler-db;Port=5432;Database=studyscheduler;Username=studyscheduler;Password=<password>` |
| `TelegramAuth__BotToken` | **yes** | Bot token from [@BotFather](https://t.me/BotFather); the app refuses to start without it |
| `TelegramAuth__MaxAuthAge` | no | `d.hh:mm:ss`, default `1.00:00:00` |
| `Cors__AllowedOrigins__0` | **yes in production** | Mini App origin, e.g. `https://app.example.org`. Add `__1`, `__2`, … for more. Left empty in production, **no** cross-origin request is allowed |
| `Notifications__WebhookUrl` | no | Public HTTPS URL Telegram POSTs updates to, e.g. `https://api.example.org/telegram/webhook`. Empty means poller-only mode, nothing registered with Telegram |
| `Notifications__WebhookSecret` | with `WebhookUrl` | Shared secret echoed back in `X-Telegram-Bot-Api-Secret-Token`; the endpoint 404s without it. Self-invented, not issued by Telegram — generate with `openssl rand -hex 32` (allowed: 1–256 chars of `A-Za-z0-9_-`) |
| `Notifications__PollIntervalMinutes` | no | Default `1` |
| `Notifications__FollowUpLookbackMinutes` | no | Default `60` |
| `RateLimiting__Write__PermitLimit` | no | Default `60` |
| `RateLimiting__Write__WindowSeconds` | no | Default `60` |
| `ASPNETCORE_ENVIRONMENT` | no | Defaults to `Production` |
| `ASPNETCORE_URLS` | no | **Baked into the image** as `http://+:8080` — don't override |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | Ships traces/metrics/logs to an OTLP collector; unset means no exporter |

### Deployment

Production is **self-hosted via [Dokploy](https://dokploy.com/)**. On push to `main` GitHub Actions
runs the test suites; only when both pass does the `deploy` job call the Dokploy API
(`application.deploy`), and Dokploy builds `Dockerfile` (build context = this repo root) and
restarts the container:

```
push to main ─► GitHub Actions: unit + integration tests
                     └─► deploy job (self-hosted runner) ─► Dokploy API: application.deploy
                                                                 └─► docker build -f Dockerfile .
                                                                          └─► container (plain HTTP :8080)
                                                                                   └─► Traefik terminates TLS
```

Dokploy's own push-webhook autodeploy must stay **off** for this app — otherwise pushes would
deploy without waiting for tests. The deploy job runs on a self-hosted runner on the same server
(it calls `http://localhost:3000`) and needs two repo secrets: `DOKPLOY_TOKEN` (API key) and
`DOKPLOY_APP_ID` (the application id in Dokploy).

- The container serves **plain HTTP on 8080** (`ASPNETCORE_URLS` is baked into the image, `EXPOSE
  8080` is declared). Traefik in front of it terminates TLS — do **not** add a certificate to the
  container.
- **PostgreSQL** runs as a separate Dokploy service on the same internal Docker network; reach it by
  its service hostname (e.g. `studyscheduler-db:5432`), never over the public internet.
- **Migrations apply themselves on startup** (`app.ApplyMigrations()`), so a deploy that adds a
  migration needs no extra step. This assumes a single instance — scale out only after moving to an
  explicit `dotnet ef database update` step.
- **Health probes**, mapped in every environment:

  | Route | Meaning |
  |---|---|
  | `GET /alive` | Liveness — the process is responsive. Point the uptime monitor here. |
  | `GET /health` | Readiness — includes the database connection, so it fails while PostgreSQL is unreachable. |

- The startup log line `Cannot load library libgssapi_krb5.so.2` is **benign**: Npgsql probes for
  Kerberos support, which the runtime image doesn't ship and this deployment doesn't use.

#### CI

`.github/workflows/main_studyscheduler.yml` runs the unit and integration suites on every push and
PR to `main`; on a green push to `main` its `deploy` job triggers the Dokploy build (see
[Deployment](#deployment)).

---

## Explanation

### Authentication — Telegram Mini App init data

Every request carries `Authorization: tma <initData>`. A custom `AuthenticationHandler` validates the
init data on **every** request (no server-side session): it verifies the HMAC-SHA256 signature over
the data-check-string and projects the Telegram user into claims. There is no `auth_date` TTL —
initData never refreshes inside a running WebView, so an expiry check would lock out long-lived sessions. This
suits a Mini App, where Telegram already signs fresh init data on each launch — so there's no login
flow or session to manage.

Resource ownership is scoped by the Telegram user id (`long`) from the authenticated principal. There
is deliberately **no separate `Account` entity**: the product is Telegram-only, so the Telegram id is
already a stable, unique identity, and a surrogate account would only add a lookup on every request.

### Persistence

EF Core on PostgreSQL. The DbContext is registered through the Aspire Npgsql client integration
(`AddNpgsqlDbContext<AppDbContext>("Default")`, giving health checks + retries + telemetry), and
pending migrations are applied on startup. The connection string comes from configuration: a real
PostgreSQL container locally (via the AppHost) or the Dokploy database service in production.
**Money is always `decimal`; timestamps are UTC** — these are expensive to change once data exists,
so they're fixed from the start.

Time mapping is explicit, because PostgreSQL is strict about it. Every instant is a
`DateTimeOffset` stored as `timestamp with time zone`, normalized to a zero-offset UTC value by a
model-wide value conversion (`UtcTimestampConversion`) — Npgsql rejects a non-zero offset, and a
client may legitimately send one. There are deliberately **no** local wall-clock `DateTime` columns:
a series' wall clock is a `TimeOnly` plus its IANA zone id (`time without time zone` + `varchar`),
and occurrence/series dates are `DateOnly` (`date`).

### Feature modules (vertical slices)

`Program.cs` is kept thin and declarative: cross-cutting infrastructure lives in `Core/*` extension
methods (`AddTelegramAuthentication`, `AddPersistence`, `AddMiniAppCors`, …), and each feature is a
self-contained slice under `Features/<Name>/` with a `<Name>Module` exposing `Add<Name>Feature` +
`Map<Name>Feature`. Adding a feature touches only its own folder plus two lines in `Program.cs`. This
keeps related code together and the composition root readable.

### Orchestration & testing with Aspire

.NET Aspire models the app topology (API + PostgreSQL) so a single command runs everything locally
with a dashboard. The same model powers integration tests: `Aspire.Hosting.Testing` boots the whole
app against a **real** PostgreSQL container — the same engine production runs — so tests exercise
persistence, scoping and the real auth pipeline end-to-end, not mocks. Unit tests stay Docker-free
for a fast inner loop.

### Design principles

- **Thin `Program.cs`** — infrastructure in `Core/*` extensions, features as modules.
- **Scope everything by the authenticated tutor** — never trust ids from the request body.
- **Money is `decimal`, time is UTC.**
- **The server is the source of truth** — the client re-reads state, it doesn't compute policy.
