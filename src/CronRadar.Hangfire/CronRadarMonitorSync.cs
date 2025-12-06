using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Handles per-monitor syncing to CronRadar API
    /// Each monitor is synced individually as it's registered
    /// </summary>
    public class CronRadarMonitorSync
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Track which monitors have been synced (per-monitor idempotency)
        private static readonly HashSet<string> _syncedMonitors = new HashSet<string>();
        private static readonly object _syncLock = new object();

        private readonly string _apiKey;

        public CronRadarMonitorSync(string apiKey = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("CRONRADAR_API_KEY");
        }

        /// <summary>
        /// Sync a single monitor to CronRadar
        /// Called immediately when a job is registered with Hangfire
        /// </summary>
        public async Task<bool> SyncSingleMonitorAsync(string recurringJobId, string cronExpression, string jobName = null)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                Console.WriteLine("[CronRadar] No API key configured, skipping monitor sync");
                return false;
            }

            if (string.IsNullOrEmpty(recurringJobId))
                return false;

            // Normalize the key
            var normalizedKey = CronRadarKeyNormalizer.Normalize(recurringJobId);

            // Check if already synced (per-monitor idempotency)
            lock (_syncLock)
            {
                if (!_syncedMonitors.Add(normalizedKey))
                {
                    // Already synced this monitor
                    return true;
                }
            }

            try
            {
                var baseUrl = Environment.GetEnvironmentVariable("CRONRADAR_BASE_URL") ?? "https://cronradar.com";

                // Prepare single monitor sync request
                var syncRequest = new
                {
                    source = "hangfire",
                    version = "0.0.6",
                    monitors = new[]
                    {
                        new
                        {
                            key = normalizedKey,
                            name = jobName ?? GenerateReadableName(recurringJobId),
                            schedule = cronExpression,
                            gracePeriod = 60
                        }
                    }
                };

                var json = JsonSerializer.Serialize(syncRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync");
                request.Headers.TryAddWithoutValidation("Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_apiKey}:"))}");
                request.Content = content;

                var response = await _http.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[CronRadar] Monitor '{normalizedKey}' synced successfully");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[CronRadar] Failed to sync monitor '{normalizedKey}': {response.StatusCode}");

                    // Remove from synced set to allow retry
                    lock (_syncLock)
                    {
                        _syncedMonitors.Remove(normalizedKey);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CronRadar] Error syncing monitor '{normalizedKey}': {ex.Message}");

                // Remove from synced set to allow retry
                lock (_syncLock)
                {
                    _syncedMonitors.Remove(normalizedKey);
                }
                return false;
            }
        }

        /// <summary>
        /// Sync a single monitor synchronously (for use in decorator pattern)
        /// </summary>
        public void SyncSingleMonitor(string recurringJobId, string cronExpression, string jobName = null)
        {
            // Fire and forget - don't block job registration
            Task.Run(() => SyncSingleMonitorAsync(recurringJobId, cronExpression, jobName));
        }

        /// <summary>
        /// Generate a human-readable name from a job ID
        /// </summary>
        private static string GenerateReadableName(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return jobId;

            // Convert kebab-case to title case
            // "check-overdue-pings" -> "Check Overdue Pings"
            return string.Join(" ", jobId.Split('-')
                .Select(word => word.Length > 0
                    ? char.ToUpper(word[0]) + word.Substring(1).ToLower()
                    : word));
        }

        /// <summary>
        /// Clear synced monitors cache (useful for testing)
        /// </summary>
        public static void ClearSyncedMonitors()
        {
            lock (_syncLock)
            {
                _syncedMonitors.Clear();
            }
        }
    }
}