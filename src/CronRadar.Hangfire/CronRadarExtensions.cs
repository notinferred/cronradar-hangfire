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
        /// Enables CronRadar monitoring for all Hangfire jobs automatically.
        /// Jobs are monitored using clean kebab-case keys.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public static IGlobalConfiguration UseCronRadar(this IGlobalConfiguration configuration, string apiKey = null)
        {
            GlobalJobFilters.Filters.Add(new GlobalCronRadarFilter(apiKey));
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

                if (context.BackgroundJob?.Job == null)
                    return;

                // Generate a clean monitor key from job type and method
                // Convert to kebab-case for cleaner keys
                var jobType = context.BackgroundJob.Job.Type.Name
                    .Replace("BackgroundJobs", "")
                    .Replace("Jobs", "")
                    .Replace("Job", "");
                var method = context.BackgroundJob.Job.Method.Name;
                var monitorKey = ToKebabCase($"{jobType}-{method}");

                // Ping CronRadar with the API key from configuration or env var
                CronRadarClient.Ping(monitorKey, _apiKey);
            }


            private string ToKebabCase(string input)
            {
                if (string.IsNullOrEmpty(input))
                    return input;

                // Simple kebab case conversion
                return System.Text.RegularExpressions.Regex
                    .Replace(input, "([a-z])([A-Z])", "$1-$2")
                    .ToLowerInvariant()
                    .Replace("_", "-")
                    .Replace("--", "-");
            }
        }
    }
}