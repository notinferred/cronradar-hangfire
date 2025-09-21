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

        /// <summary>
        /// Creates a new CronRadar monitoring attribute.
        /// </summary>
        /// <param name="monitorKey">The unique key to identify this job in CronRadar</param>
        public CronRadarAttribute(string monitorKey)
        {
            _monitorKey = monitorKey;
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
                global::CronRadar.CronRadar.Ping(_monitorKey);
            }
        }
    }
}