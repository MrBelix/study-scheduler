# StudyScheduler.IntegrationTests

**End-to-end integration tests** over the real stack: the Aspire AppHost boots a **real SQL Server
container** plus the API, and requests go through the **real Telegram auth pipeline**. This verifies
behaviour that unit tests can't — persistence, scoping, and auth wired together.

**Requires Docker.**

## Layout

```
AppFixture.cs            Boots the Aspire app once, shared across the collection (IAsyncLifetime)
ApiClientExtensions.cs   GetAs(tutor, url) / PostAs(tutor, url, body) — attach the `tma` auth header
TelegramInitData.cs      Mints valid init data signed with the AppHost's test bot token
StudentsScopeTests.cs    A tutor cannot reach another tutor's students (404 / scoped lists)
```

## How it works

- `AppFixture` uses `Aspire.Hosting.Testing` to start the whole app **once per collection** (starting
  the SQL container is expensive) and exposes an authenticated-friendly `HttpClient`.
- The client talks to the API's HTTPS endpoint with certificate validation bypassed — the ASP.NET dev
  cert isn't trusted on CI (Linux) runners.
- Tests authenticate by minting init data signed with the same fixed test bot token the AppHost
  injects (`123456:TEST-bot-token`).
- **Use distinct tutor ids per test** — the database is shared across the collection.

## Run

```bash
dotnet test tests/StudyScheduler.IntegrationTests/StudyScheduler.IntegrationTests.csproj
```

> **Rider on Windows:** running these through Rider's Aspire-plugin test runner hangs (known JetBrains
> issue). Run from the CLI, or disable the Aspire plugin. In CI they run as a separate job on
> `ubuntu-latest`, gating deployment.
