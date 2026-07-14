using System;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, string?> _schedules = new();

        /// <summary>API key captured during initialization, used by the reconciler.</summary>
        internal static string? ApiKey => _apiKey;

        /// <summary>
        /// Registers the execution filter (start/complete/fail telemetry) exactly once
        /// and captures the API key. Shared by MonitorAll and AddCronRadarHangfire.
        /// </summary>
        internal static void EnsureExecutionFilter(string? apiKey)
        {
            lock (_initLock)
            {
                if (apiKey != null)
                {
                    _apiKey = apiKey; // Store for filter/reconciler (avoids env-var mutation)
                }

                if (_initialized)
                {
                    return;
                }

                _initialized = true;

                // Add single execution filter - handles lifecycle tracking (start/complete/fail)
                GlobalJobFilters.Filters.Add(new CronRadarExecutionFilter());
            }
        }

        /// <summary>
        /// Adds execution telemetry for all jobs (start/complete/fail); jobs with
        /// [SkipMonitor] are excluded. This does NOT pre-register monitors at startup —
        /// for dead-man coverage of jobs that have not run yet, use
        /// <c>services.AddCronRadarHangfire(apiKey)</c> which also reconciles the schedule.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        [Obsolete("Use services.AddCronRadarHangfire(apiKey) for startup + periodic reconcile (dead-man coverage). MonitorAll only wires execution telemetry.")]
        public static IGlobalConfiguration MonitorAll(this IGlobalConfiguration configuration, string? apiKey = null)
        {
            Console.WriteLine("[CronRadar.Hangfire] MonitorAll registers execution telemetry only. " +
                "For startup dead-man coverage, use services.AddCronRadarHangfire(apiKey).");
            EnsureExecutionFilter(apiKey);
            return configuration;
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
                var key = CronRadarKeyNormalizer.Normalize(recurringJobId);

                // O(1) cache hit
                if (_schedules.TryGetValue(key, out var cached))
                    return cached;

                // Cache miss - populate entire cache (happens once per app lifetime)
                try
                {
                    foreach (var job in connection.GetRecurringJobs())
                    {
                        _schedules.TryAdd(CronRadarKeyNormalizer.Normalize(job.Id), job.Cron);
                    }
                }
                catch (Exception ex)
                {
                    if (CronRadar.DebugLoggingEnabled)
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Cache populate error: {ex.Message}");
                }

                return _schedules.TryGetValue(key, out var schedule) ? schedule : null;
            }
        }
    }
}
