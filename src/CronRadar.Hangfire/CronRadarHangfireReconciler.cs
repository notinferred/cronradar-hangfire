using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using CronRadar;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Declares Hangfire's recurring jobs to CronRadar (the authoritative set) so
    /// dead-man coverage exists before a job ever runs. Invoked once at startup and
    /// then periodically via a Hangfire recurring job. Separate from execution
    /// telemetry (the server filter) — this is declaration, not run reporting.
    /// </summary>
    public class CronRadarHangfireReconciler
    {
        private const string Source = "hangfire";
        private const string Version = "0.1.0";

        // Public so Hangfire's recurring job can invoke it via the job activator.
        public async Task Reconcile()
        {
            try
            {
                var monitors = new List<MonitorDefinition>();

                using (var connection = JobStorage.Current.GetConnection())
                {
                    foreach (var job in connection.GetRecurringJobs())
                    {
                        if (string.IsNullOrEmpty(job.Id) || string.IsNullOrEmpty(job.Cron))
                            continue;

                        // Respect [SkipMonitor] on the job method
                        var method = job.Job?.Method;
                        if (method != null && method.GetCustomAttribute<SkipMonitorAttribute>() != null)
                            continue;

                        monitors.Add(new MonitorDefinition
                        {
                            Key = CronRadarKeyNormalizer.Normalize(job.Id),
                            Schedule = job.Cron
                        });
                    }
                }

                await CronRadar.SyncMonitorsAsync(monitors, Source, Version, CronRadarExtensions.ApiKey)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never break the host
                if (CronRadar.DebugLoggingEnabled)
                {
                    Console.Error.WriteLine($"[CronRadar.Hangfire] Reconcile error: {ex.Message}");
                }
            }
        }
    }
}
