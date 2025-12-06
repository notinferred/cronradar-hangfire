using Hangfire;
using Hangfire.Common;
using Hangfire.Server;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Extension methods for simple CronRadar integration with Hangfire.
    /// </summary>
    public static class CronRadarExtensions
    {
        /// <summary>
        /// Enables CronRadar monitoring for all Hangfire jobs using a simple naming convention.
        /// Jobs are monitored using their Type.Method as the monitor key.
        /// </summary>
        public static IGlobalConfiguration UseCronRadar(this IGlobalConfiguration configuration)
        {
            GlobalJobFilters.Filters.Add(new GlobalCronRadarFilter());
            return configuration;
        }

        /// <summary>
        /// Global filter that monitors all Hangfire jobs automatically.
        /// </summary>
        private class GlobalCronRadarFilter : JobFilterAttribute, IServerFilter
        {
            public void OnPerforming(PerformingContext context)
            {
                // Not needed
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context?.Exception != null || context.Canceled)
                    return;

                if (context.BackgroundJob?.Job == null)
                    return;

                // Use job type and method name as monitor key
                var jobType = context.BackgroundJob.Job.Type.Name;
                var method = context.BackgroundJob.Job.Method.Name;
                var monitorKey = $"{jobType}.{method}";

                // Ping CronRadar with the generated monitor key
                global::CronRadar.CronRadar.Ping(monitorKey);
            }
        }
    }
}