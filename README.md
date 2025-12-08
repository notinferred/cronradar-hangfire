# CronRadar for Hangfire

[![NuGet](https://img.shields.io/nuget/v/CronRadar.Hangfire.svg)](https://www.nuget.org/packages/CronRadar.Hangfire/)

**Monitor all Hangfire jobs automatically.**

## Installation

```bash
dotnet add package CronRadar.Hangfire
```

## Quick Start

```csharp
// Set API key via environment variable
// CRONRADAR_API_KEY=ck_app_xxxxx

services.AddHangfire(config =>
{
    config
        .UseSqlServerStorage("connection-string")
        .MonitorAll();  // 👈 That's it!
});
```

All recurring jobs are now monitored automatically.

## Opt-Out Specific Jobs

```csharp
[SkipMonitor]
public void InternalTask()
{
    // This job won't be monitored
}
```

## Configuration

**API Key** (required):
```bash
CRONRADAR_API_KEY=ck_app_xxxxx
```

Or pass directly:
```csharp
config.MonitorAll(apiKey: "ck_app_xxxxx");
```

**Debug logging** (optional):
```bash
CRONRADAR_DEBUG=true
```

## How It Works

1. Detects all Hangfire recurring jobs
2. Auto-registers monitors on first execution
3. Tracks start/complete/fail for every run
4. Never breaks your jobs (all exceptions caught)

## Links

- [Documentation](https://docs.cronradar.com)
- [NuGet](https://www.nuget.org/packages/CronRadar.Hangfire)

**See also:** [CronRadar .NET SDK](https://www.nuget.org/packages/CronRadar)

## License

© 2025 [CronRadar](https://cronradar.com) - See [LICENSE](./LICENSE)
