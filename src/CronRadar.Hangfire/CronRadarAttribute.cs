using System;
using Hangfire.Common;
using Hangfire.Server;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Add [CronRadar("monitor-key")] to any Hangfire job to monitor it with CronRadar.
    /// DEPRECATED: Use MonitorAll() extension instead for automatic monitoring of all jobs.
    /// This attribute is kept for backward compatibility only.
    /// </summary>
    [Obsolete("Use MonitorAll() extension method for automatic monitoring. This attribute will be removed in a future version.", false)]
    public class CronRadarAttribute : JobFilterAttribute, IServerFilter
    {
        private readonly string _monitorKey;
        private readonly string? _apiKey;

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
            // Only monitor on successful completion
            if (context?.Exception == null && !context.Canceled)
            {
                // Pass API key directly to Monitor - no environment variable mutation needed
                CronRadar.Monitor(_monitorKey, schedule: null, apiKey: _apiKey);
            }
        }
    }
}