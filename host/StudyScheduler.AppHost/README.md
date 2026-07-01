# StudyScheduler.AppHost

The **.NET Aspire orchestrator** for local development and integration tests. It describes the
application topology and starts everything with a single command.

## What it provisions

- A **SQL Server** container (`AddSqlServer("sql").AddDatabase("Default")`).
- The **API** project, wired to that database (`WithReference(db)`), waiting for it to be healthy.

The database resource is named `Default`, so the API receives it as the `ConnectionStrings:Default`
connection string automatically. The AppHost also injects `TelegramAuth__BotToken` into the API.

## Run it

```bash
dotnet run --project host/StudyScheduler.AppHost
```

This starts the SQL Server container, the API, and the **Aspire dashboard** (logs, traces, health,
connection strings in one place). Requires Docker.

Provide the real bot token via AppHost user-secrets (a fixed test token is used as a fallback so the
integration tests can mint matching init data):

```bash
dotnet user-secrets set "TelegramAuth:BotToken" "<token>" --project host/StudyScheduler.AppHost
```

## Notes

- This is **dev/test orchestration only** — production runs the API directly on Azure App Service with
  Azure SQL, not this AppHost.
- Stop it gracefully (Ctrl+C / Stop) — force-killing can leave an orphaned host holding the dashboard
  port. Clean up with:
  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'AppHost|Aspire.Dashboard' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```
