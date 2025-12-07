using System;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Mark a Hangfire job to be excluded from CronRadar monitoring.
    /// Used with monitorAll mode (.MonitorAll() setup).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class SkipMonitorAttribute : Attribute
    {
        // Opt-out for monitorAll mode
    }
}
