using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Extension methods for simple CronRadar integration with Hangfire.
    /// </summary>
    public static class CronRadarExtensions
    {
        /// <summary>
        /// Enables CronRadar monitoring for all Hangfire jobs automatically.
        /// Jobs are monitored using their actual recurring job IDs.
        /// Auto-syncs recurring jobs at startup by default.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        /// <param name="autoSync">Whether to auto-sync recurring jobs at startup (default: true)</param>
        public static IGlobalConfiguration UseCronRadar(this IGlobalConfiguration configuration, string apiKey = null, bool autoSync = true)
        {
            GlobalJobFilters.Filters.Add(new GlobalCronRadarFilter(apiKey));

            // Auto-sync recurring jobs at startup
            if (autoSync)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Wait for Hangfire to initialize
                    await System.Threading.Tasks.Task.Delay(5000);
                    await CronRadarSync.SyncRecurringJobsAsync(apiKey, removeStale: true);
                });
            }

            return configuration;
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
                            monitorKey = recurringJobId; // Use the actual recurring job ID
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
    }
}