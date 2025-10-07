using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Extension methods for CronRadar integration with Hangfire.
    /// Supports two modes: Monitor (selective opt-in) and MonitorAll (opt-out).
    /// </summary>
    public static class CronRadarExtensions
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new object();
        private static bool _selectiveMode = true; // true = Monitor, false = MonitorAll

        /// <summary>
        /// Enables selective monitoring - only jobs with [Monitor] attribute are tracked.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public static IGlobalConfiguration Monitor(this IGlobalConfiguration configuration, string? apiKey = null)
        {
            return InitializeMonitoring(configuration, apiKey, selectiveMode: true);
        }

        /// <summary>
        /// Enables monitoring for all jobs - only jobs with [SkipMonitor] attribute are excluded.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public static IGlobalConfiguration MonitorAll(this IGlobalConfiguration configuration, string? apiKey = null)
        {
            return InitializeMonitoring(configuration, apiKey, selectiveMode: false);
        }

        private static IGlobalConfiguration InitializeMonitoring(
            IGlobalConfiguration configuration,
            string? apiKey,
            bool selectiveMode)
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    Console.WriteLine("[CronRadar.Hangfire] Already initialized. Ignoring duplicate call.");
                    return configuration;
                }

                _initialized = true;
                _selectiveMode = selectiveMode;

                // Store API key in environment for base SDK to use
                if (!string.IsNullOrEmpty(apiKey))
                {
                    Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", apiKey, EnvironmentVariableTarget.Process);
                }

                var mode = selectiveMode ? "selective ([Monitor] opt-in)" : "monitorAll ([SkipMonitor] opt-out)";
                Console.WriteLine($"[CronRadar.Hangfire] Initializing in {mode} mode");

                // Add filter for monitoring job execution
                GlobalJobFilters.Filters.Add(new CronRadarExecutionFilter(selectiveMode));

                // Add filter for syncing monitors when jobs are created
                GlobalJobFilters.Filters.Add(new CronRadarRegistrationFilter(selectiveMode));

                // Start background watcher to discover and sync jobs
                Task.Run(() =>
                {
                    var watcher = new CronRadarJobWatcher(selectiveMode);
                    watcher.WatchForJobs();
                });

                return configuration;
            }
        }

        /// <summary>
        /// Filter that syncs monitors when recurring jobs are created.
        /// Checks for [Monitor] or [SkipMonitor] attributes based on mode.
        /// </summary>
        private class CronRadarRegistrationFilter : JobFilterAttribute, IClientFilter
        {
            private readonly bool _selectiveMode;

            public CronRadarRegistrationFilter(bool selectiveMode)
            {
                _selectiveMode = selectiveMode;
            }

            public void OnCreating(CreatingContext context)
            {
                // Not needed
            }

            public void OnCreated(CreatedContext context)
            {
                // Check if this is a recurring job
                if (context.BackgroundJob?.Id == null) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        var recurringJobId = connection.GetJobParameter(
                            context.BackgroundJob.Id, "RecurringJobId");

                        if (string.IsNullOrEmpty(recurringJobId)) return;

                        // Get the recurring job details
                        var recurringJobs = connection.GetRecurringJobs();
                        var normalizedId = CronRadarKeyNormalizer.Normalize(recurringJobId);

                        foreach (var job in recurringJobs)
                        {
                            if (CronRadarKeyNormalizer.Normalize(job.Id) != normalizedId) continue;

                            // Check if job should be monitored based on attributes
                            var method = context.BackgroundJob.Job?.Method;
                            if (method == null) continue;

                            if (!ShouldMonitorJob(method)) continue;

                            // Use base SDK's Sync() to pre-register
                            var jobName = GenerateReadableName(job.Id);
                            CronRadar.Sync(
                                normalizedId,
                                job.Cron,
                                source: "hangfire",
                                name: jobName
                            );
                            Console.WriteLine($"[CronRadar.Hangfire] Registered monitor: {normalizedId}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Ignore errors during sync - never break user's job registration
                    Console.WriteLine($"[CronRadar.Hangfire] Registration sync error: {ex.Message}");
                }
            }

            private bool ShouldMonitorJob(MethodInfo method)
            {
                if (_selectiveMode)
                {
                    // Selective: Only monitor if [Monitor] attribute present
                    return method.GetCustomAttribute<MonitorAttribute>() != null;
                }
                else
                {
                    // MonitorAll: Monitor unless [SkipMonitor] attribute present
                    return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
                }
            }
        }

        /// <summary>
        /// Filter that monitors job execution.
        /// Calls base SDK's Monitor() on successful completion.
        /// </summary>
        private class CronRadarExecutionFilter : JobFilterAttribute, IServerFilter
        {
            private readonly bool _selectiveMode;

            public CronRadarExecutionFilter(bool selectiveMode)
            {
                _selectiveMode = selectiveMode;
            }

            public void OnPerforming(PerformingContext context)
            {
                // Not needed
            }

            public void OnPerformed(PerformedContext context)
            {
                // Only monitor successful executions
                if (context?.Exception != null || context.Canceled) return;
                if (context.BackgroundJob?.Id == null) return;

                // Check if job should be monitored based on attributes
                var method = context.BackgroundJob.Job?.Method;
                if (method == null) return;

                if (!ShouldMonitorJob(method)) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        // Get the recurring job ID
                        var recurringJobId = connection.GetJobParameter(
                            context.BackgroundJob.Id, "RecurringJobId");

                        if (string.IsNullOrEmpty(recurringJobId)) return;

                        var monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId);

                        // Use base SDK's Monitor() to record execution
                        CronRadar.Monitor(monitorKey);
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    Console.WriteLine($"[CronRadar.Hangfire] Monitor error: {ex.Message}");
                }
            }

            private bool ShouldMonitorJob(MethodInfo method)
            {
                if (_selectiveMode)
                {
                    // Selective: Only monitor if [Monitor] attribute present
                    return method.GetCustomAttribute<MonitorAttribute>() != null;
                }
                else
                {
                    // MonitorAll: Monitor unless [SkipMonitor] attribute present
                    return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
                }
            }
        }

        /// <summary>
        /// Background task that watches for new recurring jobs and syncs them.
        /// Filters based on [Monitor] or [SkipMonitor] attributes.
        /// </summary>
        private class CronRadarJobWatcher
        {
            private readonly HashSet<string> _syncedJobs = new HashSet<string>();
            private readonly bool _selectiveMode;

            public CronRadarJobWatcher(bool selectiveMode)
            {
                _selectiveMode = selectiveMode;
            }

            public async Task WatchForJobs()
            {
                // Initial delay to let application start properly
                await Task.Delay(TimeSpan.FromSeconds(2));

                while (true)
                {
                    try
                    {
                        using (var connection = JobStorage.Current.GetConnection())
                        {
                            var recurringJobs = connection.GetRecurringJobs();

                            foreach (var job in recurringJobs)
                            {
                                var normalizedId = CronRadarKeyNormalizer.Normalize(job.Id);

                                // Skip if already synced
                                if (_syncedJobs.Contains(normalizedId)) continue;

                                try
                                {
                                    // Check if this job should be monitored
                                    if (!ShouldMonitorJob(job)) continue;

                                    // Use base SDK's Sync() to pre-register
                                    var jobName = GenerateReadableName(job.Id);
                                    CronRadar.Sync(
                                        normalizedId,
                                        job.Cron,
                                        source: "hangfire",
                                        name: jobName
                                    );

                                    _syncedJobs.Add(normalizedId);
                                    Console.WriteLine($"[CronRadar.Hangfire] Synced monitor: {normalizedId}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[CronRadar.Hangfire] Failed to sync {normalizedId}: {ex.Message}");
                                    // Don't add to synced list, will retry next iteration
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CronRadar.Hangfire] Watcher error: {ex.Message}");
                    }

                    // Check again in 10 seconds
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }

            private bool ShouldMonitorJob(RecurringJobDto job)
            {
                try
                {
                    // Get the job method to check for attributes
                    if (job.Job?.Method == null) return !_selectiveMode; // If unknown, default based on mode

                    if (_selectiveMode)
                    {
                        // Selective: Only monitor if [Monitor] attribute present
                        return job.Job.Method.GetCustomAttribute<MonitorAttribute>() != null;
                    }
                    else
                    {
                        // MonitorAll: Monitor unless [SkipMonitor] attribute present
                        return job.Job.Method.GetCustomAttribute<SkipMonitorAttribute>() == null;
                    }
                }
                catch
                {
                    // If we can't determine, default based on mode
                    return !_selectiveMode; // MonitorAll=true, Selective=false
                }
            }
        }

        /// <summary>
        /// Generate a human-readable name from a job ID
        /// </summary>
        private static string GenerateReadableName(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return jobId;

            // Convert kebab-case to title case
            // "check-overdue-pings" -> "Check Overdue Pings"
            return string.Join(" ", jobId.Split('-')
                .Select(word => word.Length > 0
                    ? char.ToUpper(word[0]) + word.Substring(1).ToLower()
                    : word));
        }
    }
}
