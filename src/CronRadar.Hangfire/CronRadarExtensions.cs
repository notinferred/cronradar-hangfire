using System;
using System.Collections.Generic;
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
    /// Extension methods for simple CronRadar integration with Hangfire.
    /// </summary>
    public static class CronRadarExtensions
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Enables CronRadar monitoring for all Hangfire jobs automatically.
        /// Jobs are monitored using their actual recurring job IDs.
        /// Monitors are synced automatically as jobs are registered.
        /// Zero configuration required - just add this line!
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public static IGlobalConfiguration UseCronRadar(this IGlobalConfiguration configuration, string apiKey = null)
        {
            lock (_initLock)
            {
                if (_initialized) return configuration;
                _initialized = true;

                // Store API key in environment for other components to use
                if (!string.IsNullOrEmpty(apiKey))
                {
                    Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", apiKey, EnvironmentVariableTarget.Process);
                }

                var effectiveApiKey = apiKey ?? Environment.GetEnvironmentVariable("CRONRADAR_API_KEY");

                // Add filter for monitoring job execution
                GlobalJobFilters.Filters.Add(new GlobalCronRadarFilter(effectiveApiKey));

                // Add filter for syncing monitors when jobs are created
                GlobalJobFilters.Filters.Add(new CronRadarCreationFilter(effectiveApiKey));

                // Start background watcher to discover and sync jobs
                // Non-invasive: discovers jobs as they appear without blocking
                Task.Run(() =>
                {
                    var watcher = new CronRadarJobWatcher(effectiveApiKey);
                    watcher.WatchForJobs();
                });

                return configuration;
            }
        }

        /// <summary>
        /// Filter that syncs monitors when recurring jobs are created
        /// </summary>
        private class CronRadarCreationFilter : JobFilterAttribute, IClientFilter
        {
            private readonly CronRadarMonitorSync _monitorSync;

            public CronRadarCreationFilter(string apiKey = null)
            {
                _monitorSync = new CronRadarMonitorSync(apiKey);
            }

            public void OnCreating(CreatingContext context)
            {
                // Not needed
            }

            public void OnCreated(CreatedContext context)
            {
                // Check if this is a recurring job
                if (context.BackgroundJob?.Id != null)
                {
                    try
                    {
                        using (var connection = JobStorage.Current.GetConnection())
                        {
                            var recurringJobId = connection.GetJobParameter(
                                context.BackgroundJob.Id, "RecurringJobId");

                            if (!string.IsNullOrEmpty(recurringJobId))
                            {
                                // Get the recurring job details to find the cron expression
                                var recurringJobs = connection.GetRecurringJobs();
                                var normalizedId = CronRadarKeyNormalizer.Normalize(recurringJobId);

                                foreach (var job in recurringJobs)
                                {
                                    if (CronRadarKeyNormalizer.Normalize(job.Id) == normalizedId)
                                    {
                                        // Sync this monitor
                                        _monitorSync.SyncSingleMonitor(job.Id, job.Cron);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                                // Ignore errors during sync
                    }
                }
            }
        }

        /// <summary>
        /// Global filter that monitors all Hangfire jobs automatically.
        /// </summary>
        private class GlobalCronRadarFilter : JobFilterAttribute, IServerFilter
        {
            private readonly string _apiKey;

            public GlobalCronRadarFilter(string apiKey = null)
            {
                _apiKey = apiKey;
            }

            public void OnPerforming(PerformingContext context)
            {
                // Not needed
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context?.Exception != null || context.Canceled)
                    return;

                if (context.BackgroundJob?.Id == null)
                    return;

                // Try to get the recurring job ID from Hangfire storage
                string monitorKey = null;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        // Check if this job was triggered by a recurring job
                        var recurringJobId = connection.GetJobParameter(
                            context.BackgroundJob.Id, "RecurringJobId");

                        if (!string.IsNullOrEmpty(recurringJobId))
                        {
                            // Normalize the job ID to handle quoted values from Hangfire
                            monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId);
                        }
                    }
                }
                catch
                {
                    // If we can't get the recurring job ID, don't ping
                    // This prevents non-recurring jobs from creating monitors
                }

                // Only ping if we have a valid recurring job ID
                if (!string.IsNullOrEmpty(monitorKey))
                {
                    CronRadarClient.Ping(monitorKey, _apiKey);
                }
            }
        }

        /// <summary>
        /// Background task that watches for new recurring jobs and syncs them
        /// </summary>
        private class CronRadarJobWatcher
        {
            private readonly CronRadarMonitorSync _monitorSync;
            private readonly HashSet<string> _syncedJobs = new HashSet<string>();

            public CronRadarJobWatcher(string apiKey)
            {
                _monitorSync = new CronRadarMonitorSync(apiKey);
            }

            public async Task WatchForJobs()
            {
                // Initial delay to let application start properly
                await Task.Delay(TimeSpan.FromSeconds(1));

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

                                // Only sync if we haven't successfully synced this job before
                                if (!_syncedJobs.Contains(normalizedId))
                                {
                                    // Only mark as synced if sync actually succeeds
                                    if (_monitorSync.SyncSingleMonitor(job.Id, job.Cron))
                                    {
                                        _syncedJobs.Add(normalizedId);
                                    }
                                    // If sync fails, it will retry on next iteration
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors and continue watching
                    }

                    // Check again in 10 seconds (faster discovery, still non-invasive)
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }
    }
}