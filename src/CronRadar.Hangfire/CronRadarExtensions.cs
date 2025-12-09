using System;
using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using CronRadar;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Extension methods for CronRadar integration with Hangfire.
    /// Monitors all jobs by default - use [SkipMonitor] attribute to opt-out.
    /// </summary>
    public static class CronRadarExtensions
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new object();
        private static string? _apiKey = null;

        /// <summary>
        /// Enables monitoring for all jobs - only jobs with [SkipMonitor] attribute are excluded.
        /// Leverages API's auto-registration - no background workers or complex sync logic needed.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public static IGlobalConfiguration MonitorAll(this IGlobalConfiguration configuration, string? apiKey = null)
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    Console.WriteLine("[CronRadar.Hangfire] Already initialized. Ignoring duplicate call.");
                    return configuration;
                }

                _initialized = true;
                _apiKey = apiKey; // Store API key for filter to use (avoids environment variable mutation)

                Console.WriteLine("[CronRadar.Hangfire] Initializing MonitorAll mode - use [SkipMonitor] to opt-out");

                // Add single execution filter - handles lifecycle tracking + auto-registration on first ping
                GlobalJobFilters.Filters.Add(new CronRadarExecutionFilter());

                return configuration;
            }
        }

        /// <summary>
        /// Filter that monitors job execution with lifecycle tracking.
        /// Auto-registers monitors on first execution via API's self-healing.
        /// Calls StartJob (with schedule for auto-registration), CompleteJob, or FailJob.
        /// </summary>
        private class CronRadarExecutionFilter : JobFilterAttribute, IServerFilter
        {
            public void OnPerforming(PerformingContext context)
            {
                if (context?.BackgroundJob?.Id == null) return;

                var method = context.BackgroundJob.Job?.Method;
                if (method == null || !ShouldMonitorJob(method)) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        var recurringJobId = connection.GetJobParameter(context.BackgroundJob.Id, "RecurringJobId");
                        if (string.IsNullOrEmpty(recurringJobId))
                        {
                            if (CronRadar.DebugLoggingEnabled)
                            {
                                Console.Error.WriteLine($"[CronRadar.Hangfire] Job {context.BackgroundJob.Id} has no RecurringJobId - skipping monitoring (one-time job?)");
                            }
                            return;
                        }

                        var monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId);

                        // Get schedule for auto-registration
                        var schedule = GetJobSchedule(connection, recurringJobId);

                        // Signal job start - API will auto-register if monitor doesn't exist (self-healing)
                        CronRadar.StartJob(monitorKey, schedule, _apiKey);
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    if (CronRadar.DebugLoggingEnabled)
                    {
                        Console.Error.WriteLine($"[CronRadar.Hangfire] StartJob error: {ex.Message}");
                    }
                }
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context?.BackgroundJob?.Id == null) return;

                var method = context.BackgroundJob.Job?.Method;
                if (method == null || !ShouldMonitorJob(method)) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        var recurringJobId = connection.GetJobParameter(context.BackgroundJob.Id, "RecurringJobId");
                        if (string.IsNullOrEmpty(recurringJobId))
                        {
                            if (CronRadar.DebugLoggingEnabled)
                            {
                                Console.Error.WriteLine($"[CronRadar.Hangfire] Job {context.BackgroundJob.Id} has no RecurringJobId - skipping monitoring (one-time job?)");
                            }
                            return;
                        }

                        var monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId);

                        // Report success or failure
                        if (context.Exception == null && !context.Canceled)
                        {
                            CronRadar.CompleteJob(monitorKey, _apiKey);
                        }
                        else
                        {
                            var message = context.Exception?.Message ?? "Job was canceled";
                            CronRadar.FailJob(monitorKey, message, _apiKey);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    if (CronRadar.DebugLoggingEnabled)
                    {
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Lifecycle tracking error: {ex.Message}");
                    }
                }
            }

            private bool ShouldMonitorJob(MethodInfo method)
            {
                // Monitor all jobs unless [SkipMonitor] attribute is present
                return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
            }

            private string? GetJobSchedule(IStorageConnection connection, string recurringJobId)
            {
                try
                {
                    var recurringJobs = connection.GetRecurringJobs();
                    var normalizedId = CronRadarKeyNormalizer.Normalize(recurringJobId);

                    foreach (var job in recurringJobs)
                    {
                        if (CronRadarKeyNormalizer.Normalize(job.Id) == normalizedId)
                        {
                            return job.Cron;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log exception but don't fail - API will handle missing schedule
                    if (CronRadar.DebugLoggingEnabled)
                    {
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Failed to get schedule for job '{recurringJobId}': {ex.Message}");
                    }
                }
                return null;
            }
        }
    }
}
