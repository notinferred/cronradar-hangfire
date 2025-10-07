using System;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Mark a Hangfire job to be monitored by CronRadar.
    /// Used with selective mode (.Monitor() setup).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class MonitorAttribute : Attribute
    {
        // Zero parameters for now - future: GracePeriod, Schedule override, Key, Name
    }
}
