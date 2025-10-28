# CronRadar for Hangfire

Monitor Hangfire recurring jobs automatically.

## Installation

```bash
dotnet add package CronRadar.Hangfire
```

[NuGet](https://nuget.org/packages/CronRadar.Hangfire)

## Quick Start

Monitor all jobs by default - use `[SkipMonitor]` to opt-out:

```csharp
services.AddHangfire(config =>
    config.MonitorAll()
);

public void DailyBackup()
{
    // Monitored automatically
}

[SkipMonitor]
public void InternalTask()
{
    // Opted out
}
```

## Configuration

Set environment variable:
```bash
export CRONRADAR_API_KEY=ck_app_YOUR_KEY
```

## Links

- 📦 [NuGet](https://nuget.org/packages/CronRadar.Hangfire)
- 🐛 [GitHub Issues](https://github.com/cronradar/cronradar/issues)
- 📚 [Documentation](https://cronradar.com/docs/sdks/hangfire)
- ✉️ support@cronradar.com

## License

Proprietary - © 2025 CronRadar. All rights reserved.

See [LICENSE](./LICENSE) for details. This SDK may only be used with the CronRadar monitoring service.
