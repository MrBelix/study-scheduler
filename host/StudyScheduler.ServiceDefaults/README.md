# StudyScheduler.ServiceDefaults

Shared **Aspire service defaults** — the cross-cutting host configuration every service in the
solution should apply. Referenced by `StudyScheduler.API`.

## What `AddServiceDefaults()` wires

- **OpenTelemetry** — logging, metrics and tracing (ASP.NET Core, HttpClient and runtime
  instrumentation). Exports via OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (e.g. the Aspire
  dashboard).
- **Health checks** — a default `self` liveness check. `MapDefaultEndpoints()` exposes `/health` and
  `/alive` (in Development).
- **Service discovery** and **HTTP resilience** (standard retry / circuit-breaker) on `HttpClient`.

## Usage (already done in the API)

```csharp
builder.AddServiceDefaults();   // in Program.cs, before Build()
...
app.MapDefaultEndpoints();      // after Build()
```

## Why it lives in `host/`

`AddServiceDefaults` extends `IHostApplicationBuilder` and sits in the `Microsoft.Extensions.Hosting`
namespace — it is **host-level infrastructure**, not application code. It is grouped with the AppHost
under `host/` for that reason. (It is still a normal library the API compiles against and ships with;
it is not itself a runnable host.)

This project is generated from the `aspire-servicedefaults` template; edit `Extensions.cs` to change
what the defaults do.
