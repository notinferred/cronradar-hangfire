using System;
using System.Text.Json;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Normalizes monitor keys to ensure consistency across sync and ping operations.
    /// Handles various formats including JSON-serialized strings with quotes.
    /// </summary>
    public static class CronRadarKeyNormalizer
    {
        /// <summary>
        /// Normalizes a monitor key by removing quotes, spaces, and converting to lowercase.
        /// Handles JSON-serialized strings that may contain escaped quotes.
        /// </summary>
        /// <param name="key">The key to normalize</param>
        /// <returns>Normalized key in lowercase without quotes or spaces</returns>
        public static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return key;

            // Handle JSON-serialized strings (e.g., "\"check-overdue-pings\"")
            // This happens when Hangfire stores the RecurringJobId parameter
            if (key.StartsWith("\"") && key.EndsWith("\"") && key.Length > 2)
            {
                try
                {
                    // Deserialize to get the actual string value
                    key = JsonSerializer.Deserialize<string>(key);
                }
                catch
                {
                    // If deserialization fails, continue with manual cleanup
                }
            }

            // Remove all types of quotes and spaces
            // Convert to lowercase for consistent comparison
            return key
                .Trim('"', '\'', '`', ' ', '\t', '\r', '\n')
                .Replace("\\\"", "")
                .Replace("\"", "")
                .Replace("'", "")
                .Replace("`", "")
                .ToLowerInvariant();
        }
    }
}