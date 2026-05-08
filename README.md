# CronRadar for Hangfire

[![NuGet](https://img.shields.io/nuget/v/CronRadar.Hangfire.svg)](https://www.nuget.org/packages/CronRadar.Hangfire/)

Auto-discovery for Hangfire recurring jobs. Add one line to your Hangfire setup — every recurring job is registered with CronRadar, watched against its cron expression, and alerted on if it fails or misses a run.

Built on top of the [`CronRadar`](https://www.nuget.org/packages/CronRadar) base SDK.

## Install

```bash
dotnet add package CronRadar.Hangfire
```

Targets `net6.0`, `net7.0`, `net8.0`, `net9.0`. Compatible with Hangfire 1.7+ and 1.8+.

## Setup

The SDK reads its API key from the `CRONRADAR_API_KEY` environment variable.

```bash
export CRONRADAR_API_KEY=ck_app_xxxxxxxxxxxxxxxxxxxx
```

Or pass it explicitly to `MonitorAll(apiKey: ...)`.

Get the API key from the CronRadar dashboard at [app.cronradar.com](https://app.cronradar.com) under your application's settings. API keys have the form `ck_app_<random>`.

The Hangfire integration runs on application startup and during job execution — no per-job initialization is needed.

## Quickstart

Add `MonitorAll()` to your Hangfire configuration:

```csharp
services.AddHangfire(config =>
{
    config
        .UseSqlServerStorage("your-connection-string")
        .MonitorAll();
});
```

That's it. On startup, the extension:

- Discovers every registered recurring Hangfire job
- Pre-syncs each one to CronRadar (so they appear in the dashboard immediately)
- Hooks `OnPerforming`, `OnPerformed`, and `OnStateApplied` to record `start` / `complete` / `fail` for every execution

## Annotations

### Opt out of monitoring per-job

Decorate any job class or method with `[SkipMonitor]` to exclude it.

```csharp
using CronRadar.Hangfire;

public class InternalCleanup
{
    [SkipMonitor]
    public void Run()
    {
        // not monitored — internal/maintenance jobs you don't need alerts for
    }
}
```

`SkipMonitor` works at the method level (most common) and at the class level (excludes every method in the class).

### Set a custom display name

```csharp
[CronRadarName("Daily Customer Report")]
public void GenerateDailyReport()
{
    // appears in dashboard as "Daily Customer Report" rather than "GenerateDailyReport"
}
```

### Override grace period for a specific job

```csharp
[CronRadarGracePeriod(seconds: 600)]
public void LongRunningSync()
{
    // 10-minute grace period instead of the default 60s
}
```

## Reference

### `MonitorAll()` extension method

```csharp
public static IGlobalConfiguration MonitorAll(
    this IGlobalConfiguration config,
    string? apiKey = null,
    int? defaultGracePeriod = null);
```

| Parameter | Type | Required | Notes |
|---|---|---|---|
| `apiKey` | `string?` | optional | API key. Falls back to `CRONRADAR_API_KEY` env var if omitted. |
| `defaultGracePeriod` | `int?` | optional | Seconds. Default for jobs without a `[CronRadarGracePeriod]` attribute. Default: 60. |

Returns the same `IGlobalConfiguration` so it chains naturally with the rest of your Hangfire setup.

### Attributes

| Attribute | Targets | Purpose |
|---|---|---|
| `[SkipMonitor]` | method, class | Exclude from monitoring entirely. |
| `[CronRadarName("name")]` | method | Override the dashboard display name. |
| `[CronRadarGracePeriod(seconds: N)]` | method | Per-job grace period override. |

### What gets monitored

Only **recurring jobs** registered via `RecurringJob.AddOrUpdate(...)` are auto-discovered. One-off `BackgroundJob.Enqueue(...)` and delayed `BackgroundJob.Schedule(...)` calls are intentionally not monitored — there's no schedule to watch them against. If you want to track these manually, use the base [`CronRadar`](https://www.nuget.org/packages/CronRadar) SDK directly.

## Configuration

| Environment variable | Required | Default | Purpose |
|---|---|---|---|
| `CRONRADAR_API_KEY` | yes | — | API key from the CronRadar dashboard. Format `ck_app_xxxxx`. |
| `CRONRADAR_BASE_URL` | no | `https://cron.life` | Override the ingestion endpoint. |
| `CRONRADAR_TIMEOUT_MS` | no | `5000` | HTTP request timeout in milliseconds. |
| `CRONRADAR_DEBUG` | no | `false` | Set to `true` to log every request to standard output. |

## Error handling

The extension upholds the same hard guarantees as the base SDK:

1. **Never throws to user code.** Network errors, timeouts, 4xx/5xx responses — all caught and logged. A failing CronRadar API must not break a Hangfire job.
2. **Never alters job execution.** The extension hooks `OnPerforming` / `OnPerformed` / `OnStateApplied`, but never short-circuits or modifies job state. If your job throws, Hangfire's normal failure-and-retry behavior runs unchanged.

What this looks like at runtime:

| Situation | SDK behavior | Hangfire behavior |
|---|---|---|
| CronRadar API unreachable | Logs once; returns | Job runs and is marked Succeeded/Failed normally |
| Sync on startup fails | Logs; continues | Hangfire startup completes |
| Per-job ping fails | Logs; returns | Job state unchanged |
| User job throws | Records `failJob` with exception message | Hangfire records the failure and applies retry policy |

## Troubleshooting

### Jobs aren't showing up in the CronRadar dashboard

1. **Confirm `MonitorAll()` is called inside `AddHangfire`.** If it's outside, the extension can't read your Hangfire config.
2. **Confirm jobs are registered as `RecurringJob`s.** `MonitorAll` only handles recurring jobs by design.
3. **Check `CRONRADAR_API_KEY`.** A bad key produces 401s in your logs (set `CRONRADAR_DEBUG=true`).
4. **Verify outbound HTTPS to `https://cron.life`.** If your environment has restricted egress, allowlist it.

### Some jobs are monitored but others aren't

Check for `[SkipMonitor]` at the class level — it suppresses every method in that class, even ones without the attribute themselves.

### My job is `Hangfire.RecurringJob.AddOrUpdate("foo", ...)` — what's the monitor key?

The monitor key matches Hangfire's recurring-job ID, lowercased and normalized: spaces and quotes stripped, dashes preserved. `RecurringJob.AddOrUpdate("Daily Backup", ...)` becomes the monitor `daily-backup` in CronRadar.

### I'm getting `405 Method Not Allowed` on the ping endpoint

The ping endpoint accepts both GET and POST; the extension uses GET by default. A `405` typically means a proxy or WAF in front of your network is rewriting the request. Set `CRONRADAR_DEBUG=true` to see what URL the SDK is actually calling.

### How do I migrate from the deprecated `[CronRadar]` attribute?

Earlier versions used `[CronRadar]` to opt jobs *in* to monitoring. The current model is `MonitorAll()` plus `[SkipMonitor]` to opt *out*. Migration:

1. Remove every `[CronRadar]` attribute.
2. Add `MonitorAll()` to `AddHangfire(...)` once.
3. Add `[SkipMonitor]` only to jobs you want excluded.

The old attribute is removed in this version; references will fail to compile, which is intentional — agents reading old snippets get a clear signal to update.

### My ASP.NET app uses `AddHangfireServer()` separately from `AddHangfire()` — where does `MonitorAll()` go?

Inside `AddHangfire()`. The `MonitorAll()` extension lives on `IGlobalConfiguration`, which is what `AddHangfire(c => ...)` provides — not on `IServerConfigurer`. Once `MonitorAll()` is called there, every server picks up the registered job filter automatically.

```csharp
services.AddHangfire(config =>
{
    config
        .UseSqlServerStorage(Configuration.GetConnectionString("Hangfire"))
        .MonitorAll();
});

services.AddHangfireServer();   // unchanged
```

### How do I monitor jobs added at runtime via `RecurringJob.AddOrUpdate(...)`?

You don't have to do anything. `MonitorAll()` registers a Hangfire `IServerFilter` that fires on every job execution, so jobs added after startup are picked up automatically on their first run. The first execution sends a `?schedule=` ping which auto-registers the monitor in CronRadar — the same self-healing path the base SDK uses.

### Can I use this with the legacy `Hangfire.Console` extension or other filters?

Yes. CronRadar registers its filter via `GlobalJobFilters` in addition to whatever you've configured. Filters compose: if you have logging filters, error-tracking filters, etc., they all run alongside CronRadar's hooks. We don't claim ownership of any global state.

## Compatibility

- **Hangfire:** 1.7+ verified, 1.8.x recommended.
- **Storage backends:** any storage works — the SDK only reads metadata via Hangfire's public API (`JobStorage.Current.GetMonitoringApi()` and `RecurringJob` enumeration). SQL Server, PostgreSQL, MongoDB, Redis-backed storage all supported.
- **.NET versions:** `net6.0`, `net7.0`, `net8.0`, `net9.0`. .NET Framework not supported by this extension — use the base [`CronRadar`](https://www.nuget.org/packages/CronRadar) SDK manually if you need framework support.

## Links

- **Documentation:** [docs.cronradar.com](https://docs.cronradar.com)
- **Agent-friendly index:** [docs.cronradar.com/llms.txt](https://docs.cronradar.com/llms.txt)
- **OpenAPI spec:** [api.cronradar.com/swagger/v1/swagger.json](https://api.cronradar.com/swagger/v1/swagger.json)
- **NuGet:** [nuget.org/packages/CronRadar.Hangfire](https://www.nuget.org/packages/CronRadar.Hangfire)
- **GitHub:** [github.com/cronradar/cronradar-hangfire](https://github.com/cronradar/cronradar-hangfire)
- **Base SDK:** [nuget.org/packages/CronRadar](https://www.nuget.org/packages/CronRadar)
- **Support:** support@cronradar.com

## License

© 2026 [CronRadar](https://cronradar.com) · Proprietary — see [LICENSE](./LICENSE).
