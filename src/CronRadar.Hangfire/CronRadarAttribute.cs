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
                CronRadarClient.Ping(_monitorKey, _apiKey);
            }
        }
    }
}