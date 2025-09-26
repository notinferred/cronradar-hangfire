using Hangfire.Common;
using Hangfire.Server;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Add [CronRadar("monitor-key")] to any Hangfire job to monitor it with CronRadar.
    /// </summary>
    public class CronRadarAttribute : JobFilterAttribute, IServerFilter
    {
        private readonly string _monitorKey;
        private readonly string _apiKey;

        /// <summary>
        /// Creates a new CronRadar monitoring attribute.
        /// </summary>
        /// <param name="monitorKey">The unique key to identify this job in CronRadar</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        public CronRadarAttribute(string monitorKey, string apiKey = null)
        {
            _monitorKey = monitorKey;
            _apiKey = apiKey;
        }

        public void OnPerforming(PerformingContext context)
        {
            // Not needed - we only care about completion
        }

        public void OnPerformed(PerformedContext context)
        {
            // Only ping on successful completion
            if (context?.Exception == null && !context.Canceled)
            {
                // Store API key if provided for this specific monitor
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    var currentKey = System.Environment.GetEnvironmentVariable("CRONRADAR_API_KEY");
                    System.Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", _apiKey, System.EnvironmentVariableTarget.Process);
                    CronRadar.Ping(_monitorKey);
                    // Restore original key
                    System.Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", currentKey, System.EnvironmentVariableTarget.Process);
                }
                else
                {
                    CronRadar.Ping(_monitorKey);
                }
            }
        }
    }
}