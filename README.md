# CronRadar for Hangfire

[![NuGet](https://img.shields.io/nuget/v/CronRadar.Hangfire.svg)](https://www.nuget.org/packages/CronRadar.Hangfire/)

Monitor Hangfire recurring jobs automatically: synchronize the recurring-job registry, record every execution lifecycle, and alert when a job fails or misses its schedule.

## Install

```bash
dotnet add package CronRadar.Hangfire
```

## Setup

Set the application API key in your deployment environment:

```bash
CRONRADAR_API_KEY=ck_app_xxxxxxxxxxxxxxxxxxxx
```

Get the key from the application settings in [CronRadar](https://app.cronradar.com). Do not commit it to source control.

## Quickstart

Call `AddCronRadarHangfire()` after `AddHangfire()`:

```csharp
using CronRadar.Hangfire;

builder.Services.AddHangfire(config =>
{
    config.UseSqlServerStorage("connection-string");
});

builder.Services.AddCronRadarHangfire();
```

At startup, the integration reads Hangfire's recurring-job registry, declares each job and cron schedule to CronRadar, and installs an execution filter that reports start, completion, and failure. The registry is reconciled every 15 minutes by default.

## Annotations

All recurring jobs are monitored unless their method has `[SkipMonitor]`:

```csharp
using CronRadar.Hangfire;

public class MaintenanceJobs
{
    [SkipMonitor]
    public void RebuildDevelopmentFixtures()
    {
        Console.WriteLine("Rebuilding development fixtures");
    }
}
```

## Lifecycle

Lifecycle events are emitted by the global Hangfire server filter. No monitoring call belongs inside the job method:

- Before execution: `start`
- Normal completion: `complete`
- Terminal Hangfire failure: `fail` with a bounded message, after automatic retries are exhausted

One-time background jobs are ignored because they have no recurring-job schedule. Use the base [`CronRadar`](https://www.nuget.org/packages/CronRadar) SDK for manually instrumented work.

### Outcome checks

Report up to five bounded numeric or boolean results from a recurring job. The execution filter attaches them to that job's existing completion event:

```csharp
public void ImportCustomers()
{
    var processed = customerImporter.Run();
    CronRadarOutcome.Report("records_processed", processed);
    CronRadarOutcome.Report("successful", true);
}
```

Define expectations such as `records_processed >= 1` or `successful is true` in the automation settings. Do not report job arguments, payloads, database records, logs, secrets, or personal data. Invalid outcome calls are ignored and never change Hangfire job behavior.

## Reference

```csharp
public static IServiceCollection AddCronRadarHangfire(
    this IServiceCollection services,
    string? apiKey = null,
    int reconcileIntervalMinutes = 15);
```

| Parameter | Type | Default | Purpose |
|---|---|---|---|
| `apiKey` | `string?` | `CRONRADAR_API_KEY` | Application API key. |
| `reconcileIntervalMinutes` | `int` | `15` | Registry reconciliation interval, clamped to 1–59 minutes. |

`AddCronRadarHangfire()` returns the same `IServiceCollection` for chaining and does not throw for CronRadar network or synchronization failures.

`[SkipMonitor]` is the supported per-method opt-out and never throws. `MonitorAll()` and `[CronRadar(...)]` remain obsolete compatibility APIs; do not use them in new installations. `CronRadarHangfireReconciler.Reconcile()` is public only so Hangfire's job activator can invoke the package's internal reconciliation job—application code should not call it directly.

## Configuration

| Environment variable | Required | Default | Purpose |
|---|---|---|---|
| `CRONRADAR_API_KEY` | yes | — | Application API key if not passed directly. |
| `CRONRADAR_BASE_URL` | no | `https://cron.life` | Override the ingestion endpoint. |
| `CRONRADAR_DEBUG` | no | `false` | Log monitoring requests and caught errors. |

Pass a key directly only when your configuration system already protects it:

```csharp
builder.Services.AddCronRadarHangfire(apiKey: configuration["CronRadar:ApiKey"]);
```

Change the reconcile interval when the default does not fit your deployment:

```csharp
builder.Services.AddCronRadarHangfire(reconcileIntervalMinutes: 10);
```

## Error handling

CronRadar observes Hangfire; it does not change Hangfire behavior.

- Network failures, timeouts, API errors, and registry reconciliation errors are caught.
- Monitoring HTTP calls time out after five seconds by default.
- A user-job exception is still reported to Hangfire and follows the application's normal retry policy.
- Intermediate failed attempts do not open a CronRadar incident; only Hangfire's final `Failed` state is reported.
- Lifecycle events carry the Hangfire background-job ID and retry count, so concurrent recurring executions and retries remain distinct in run history.
- When debug logging is enabled, caught monitoring errors are written to standard error.

## Troubleshooting

### Jobs do not appear after startup

Confirm the host starts Hangfire and registers recurring jobs before the CronRadar hosted service runs. Enable `CRONRADAR_DEBUG=true` and verify `CRONRADAR_API_KEY` belongs to the intended application.

### One job is missing

Only Hangfire recurring jobs are discovered. Confirm the job appears in Hangfire's recurring-jobs registry and its method does not have `[SkipMonitor]`.

### Schedules changed but the dashboard has not

Wait for the next reconciliation interval or restart the host to trigger an immediate registry sync.

### CronRadar reports 401 Unauthorized

Confirm `CRONRADAR_API_KEY` is active and belongs to the intended CronRadar application. Use a separate application and key for each deployment environment so staging cannot update production monitors.

### Reconciliation is rate-limited or CronRadar is unavailable

Keep the default 15-minute reconciliation interval. The package writes bounded guidance to standard error, Hangfire continues normally, and the next reconciliation retries automatically.

### Jobs still run during a CronRadar outage

That is expected. Monitoring failures are caught so CronRadar cannot block the job it observes.

## Complete recurring-job example

This example registers a daily recurring job. It assumes the application already has Hangfire and a storage provider configured.

```csharp
using CronRadar.Hangfire;
using Hangfire;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHangfire(config =>
{
    config.UseSqlServerStorage(
        builder.Configuration.GetConnectionString("Hangfire"));
});
builder.Services.AddHangfireServer();
builder.Services.AddCronRadarHangfire();

var app = builder.Build();
app.UseHangfireDashboard();

RecurringJob.AddOrUpdate<InvoiceJobs>(
    "send-overdue-invoice-reminders",
    jobs => jobs.SendOverdueReminders(),
    "0 9 * * *");

app.Run();

public class InvoiceJobs
{
    public void SendOverdueReminders()
    {
        Console.WriteLine("Sending overdue invoice reminders");
    }
}
```

After startup, `send-overdue-invoice-reminders` appears in the CronRadar application with the declared `0 9 * * *` schedule. Each Hangfire execution reports start and either completion or failure.

## Verification

Verify monitoring before relying on it for a critical job:

1. Start the application with `CRONRADAR_API_KEY` set.
2. Confirm the recurring job appears in the intended CronRadar application.
3. Trigger the job once from Hangfire and confirm CronRadar records a successful execution.
4. In a non-production test job, throw an exception and confirm Hangfire still records the failure.
5. Confirm CronRadar records the failed execution and the configured alert destination receives the incident.
6. Remove the test failure and confirm the next successful execution recovers the monitor.

If step 2 fails, enable `CRONRADAR_DEBUG=true` before changing job code. Discovery and synchronization happen outside the job method.

## Migrating from `MonitorAll()`

Older releases documented this Hangfire configuration extension:

```csharp
services.AddHangfire(config => config.MonitorAll());
```

`MonitorAll()` now remains only as an obsolete compatibility entrypoint for execution telemetry. It does not provide startup and periodic registry reconciliation. Replace it with:

```csharp
services.AddHangfire(config =>
{
    config.UseSqlServerStorage("connection-string");
});
services.AddCronRadarHangfire();
```

Run the old and new deployment through one normal recurring-job execution, confirm the inventory, then remove the obsolete call. Do not register both entrypoints permanently.

## Schedule synchronization behavior

- Startup reconciliation declares the authoritative set of current Hangfire recurring jobs.
- Periodic reconciliation catches jobs added after startup and schedule changes made while the host is running.
- `[SkipMonitor]` jobs are omitted from the declared set and execution telemetry.
- A successful reconciliation is authoritative: removed jobs are paused immediately rather than billing or alerting indefinitely. Reappearing jobs resume automatically unless the customer paused them.
- The package's own `cronradar:reconcile` recurring job is internal integration work and is not a customer automation.

Reconciliation declares configuration only. Execution signals still come from the Hangfire server filter when a recurring job runs.

## Deployment environments

Create a separate CronRadar application and API key for production, staging, and development. Keep the recurring-job IDs stable within each environment and never reuse a production key in a non-production host. Environment isolation is an application boundary, so identical Hangfire job IDs remain independent.

## Links

- [CronRadar documentation](https://docs.cronradar.com/sdks)
- [Ingestion OpenAPI contract](https://docs.cronradar.com/openapi.json)
- [NuGet package](https://www.nuget.org/packages/CronRadar.Hangfire)
- [Hangfire monitoring overview](https://cronradar.com/hangfire)
- [Security and reliability](https://cronradar.com/security)

## License

© 2026 [CronRadar](https://cronradar.com) — see [LICENSE](./LICENSE).
