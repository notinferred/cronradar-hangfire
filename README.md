# CronRadar.Hangfire

Monitor all your Hangfire jobs with one line of code.

## Installation

```bash
dotnet add package CronRadar.Hangfire
```

## Quick Start

### Option 1: Monitor ALL Jobs (Recommended)

```csharp
// In Startup.cs or Program.cs
services.AddHangfire(configuration => configuration
    .UsePostgreSqlStorage(connectionString)
    .UseCronRadar()); // ← That's it! All jobs are now monitored
```

### Option 2: Monitor Specific Jobs

```csharp
public class EmailJobs
{
    [CronRadar("send-daily-digest")]
    public void SendDailyDigest()
    {
        // Your job logic here
    }
}

// Register normally
RecurringJob.AddOrUpdate<EmailJobs>(
    x => x.SendDailyDigest(),
    Cron.Daily
);
```

## How It Works

1. **Global Monitoring**: When you use `.UseCronRadar()`, all Hangfire jobs automatically ping CronRadar on successful completion
2. **Monitor Keys**: Jobs are identified by `ClassName.MethodName` (e.g., `EmailJobs.SendDailyDigest`)
3. **Attribute-Based**: Use `[CronRadar("custom-key")]` for custom monitor keys
4. **Zero Config**: No additional setup required beyond adding the extension

## Configuration

Set your CronRadar API key:

```bash
export CRONRADAR_API_KEY=ck_app_xxxxx_yyyyy
```

## Requirements

- .NET 6.0+ or .NET Standard 2.0+
- Hangfire 1.7.0+
- CronRadar base SDK 0.0.1+

Proprietary - © 2025 CronRadar. All rights reserved.