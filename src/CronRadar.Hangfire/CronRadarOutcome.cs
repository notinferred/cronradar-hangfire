using System;
using System.Collections.Generic;
using System.Threading;
using Hangfire.Server;

namespace CronRadar.Hangfire
{
    /// <summary>Reports bounded numeric or boolean results for the current Hangfire execution.</summary>
    public static class CronRadarOutcome
    {
        private const string ContextKey = "CronRadar.OutcomeValues";
        private static readonly AsyncLocal<Dictionary<string, object>?> Current = new();

        public static void Report(string key, bool value) => Add(key, value);
        public static void Report(string key, int value) => Add(key, value);
        public static void Report(string key, long value) => Add(key, value);
        public static void Report(string key, decimal value) => Add(key, value);
        public static void Report(string key, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) Add(key, value);
        }

        private static void Add(string key, object value)
        {
            try
            {
                var values = Current.Value;
                if (values == null || values.Count >= 5 && !values.ContainsKey(key)) return;
                if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]*$")) return;
                values[key] = value;
            }
            catch { /* Monitoring must never affect the job. */ }
        }

        internal static void Begin(PerformingContext context)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            context.Items[ContextKey] = values;
            Current.Value = values;
        }

        internal static IReadOnlyDictionary<string, object> End(PerformedContext context)
        {
            try
            {
                return context.Items.TryGetValue(ContextKey, out var value) && value is Dictionary<string, object> values
                    ? values : new Dictionary<string, object>();
            }
            finally { Current.Value = null; }
        }
    }
}
