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

        /// <summary>
        /// Enables monitoring for all jobs - only jobs with [SkipMonitor] attribute are excluded.
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

                // Store API key in environment for base SDK to use
                if (!string.IsNullOrEmpty(apiKey))
                {
                    Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", apiKey, EnvironmentVariableTarget.Process);
                }

                Console.WriteLine("[CronRadar.Hangfire] Initializing MonitorAll mode - use [SkipMonitor] to opt-out");

                // Add filter for monitoring job execution
                GlobalJobFilters.Filters.Add(new CronRadarExecutionFilter());

                // Add filter for syncing monitors when jobs are created
                GlobalJobFilters.Filters.Add(new CronRadarRegistrationFilter());

                // Start background watcher to discover and sync jobs
                Task.Run(() =>
                {
                    var watcher = new CronRadarJobWatcher();
                    watcher.WatchForJobs();
                });

                return configuration;
            }
        }

        /// <summary>
        /// Filter that syncs monitors when recurring jobs are created.
        /// Monitors all jobs unless [SkipMonitor] attribute is present.
        /// </summary>
        private class CronRadarRegistrationFilter : JobFilterAttribute, IClientFilter
        {
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

                            // Check if job should be monitored
                            var method = context.BackgroundJob.Job?.Method;
                            if (method == null) continue;

                            if (!ShouldMonitorJob(method)) continue;

                            // Use base SDK's SyncMonitor() to pre-register
                            var jobName = GenerateReadableName(job.Id);
                            CronRadar.SyncMonitor(
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
                // Monitor all jobs unless [SkipMonitor] attribute is present
                return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
            }
        }

        /// <summary>
        /// Filter that monitors job execution with lifecycle tracking.
        /// Calls StartJob on start, CompleteJob on success.
        /// </summary>
        private class CronRadarExecutionFilter : JobFilterAttribute, IServerFilter
        {
            public void OnPerforming(PerformingContext context)
            {
                // Signal job start for lifecycle tracking
                if (context?.BackgroundJob?.Id == null) return;

                // Check if job should be monitored
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

                        // Signal job start for lifecycle tracking
                        CronRadar.StartJob(monitorKey);
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    Console.WriteLine($"[CronRadar.Hangfire] StartJob error: {ex.Message}");
                }
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context?.BackgroundJob?.Id == null) return;

                // Check if job should be monitored
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

                        // Report success or failure
                        if (context.Exception == null && !context.Canceled)
                        {
                            // Signal job completion for lifecycle tracking
                            CronRadar.CompleteJob(monitorKey);
                        }
                        else
                        {
                            // Signal job failure for immediate alert
                            var message = context.Exception?.Message ?? "Job was canceled";
                            CronRadar.FailJob(monitorKey, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    Console.WriteLine($"[CronRadar.Hangfire] Lifecycle tracking error: {ex.Message}");
                }
            }

            private bool ShouldMonitorJob(MethodInfo method)
            {
                // Monitor all jobs unless [SkipMonitor] attribute is present
                return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
            }
        }

        /// <summary>
        /// Background task that watches for new recurring jobs and syncs them.
        /// Monitors all jobs unless [SkipMonitor] attribute is present.
        /// </summary>
        private class CronRadarJobWatcher
        {
            private readonly HashSet<string> _syncedJobs = new HashSet<string>();

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

                                    // Use base SDK's SyncMonitor() to pre-register
                                    var jobName = GenerateReadableName(job.Id);
                                    CronRadar.SyncMonitor(
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
                    if (job.Job?.Method == null) return true; // If unknown, monitor by default

                    // Monitor all jobs unless [SkipMonitor] attribute is present
                    return job.Job.Method.GetCustomAttribute<SkipMonitorAttribute>() == null;
                }
                catch
                {
                    // If we can't determine, monitor by default
                    return true;
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
