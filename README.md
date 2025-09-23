# CronRadar.Hangfire

Automatically monitors ALL your Hangfire jobs. Get alerts when jobs fail or don't run on schedule.

## Installation

```bash
dotnet add package CronRadar.Hangfire
```

## Setup

Add one line to your Hangfire configuration:

```csharp
services.AddHangfire(configuration => configuration
    .UsePostgreSqlStorage(connectionString)
    .UseCronRadar()); // Monitors ALL jobs automatically
```

That's it. Every Hangfire job now pings CronRadar on successful completion.

## Configuration

Set your API key:
```bash
export CRONRADAR_API_KEY=ck_app_xxxxx_yyyyy
```


## Custom Keys (Optional)

Want a specific monitor key? Use the attribute:

```csharp
[CronRadar("critical-backup")]
public void BackupDatabase() { }
```

## Requirements

- Hangfire 1.7.0+
- .NET 6.0+ or .NET Standard 2.0+

## License

Proprietary - © 2025 CronRadar. All rights reserved.