using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Information about a recurring job to sync with CronRadar
    /// </summary>
    public class RecurringJobInfo
    {
        /// <summary>
        /// The unique ID of the job (e.g., "check-billing-periods")
        /// This will be used as the monitor key in CronRadar
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable name for the job (e.g., "Check Billing Periods")
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Cron schedule expression (e.g., "0 0 * * *")
        /// </summary>
        public string Schedule { get; set; } = "* * * * *";

        /// <summary>
        /// Grace period in seconds before marking the job as down
        /// </summary>
        public int? GracePeriod { get; set; }

        /// <summary>
        /// Optional description of what this job does
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// Sync functionality for Hangfire extensions to sync all jobs with CronRadar
    /// </summary>
    public static class CronRadarSync
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// Sync specified recurring jobs with CronRadar
        /// Since Hangfire doesn't provide easy access to enumerate recurring jobs,
        /// you can manually specify which jobs to sync
        /// Application ID is extracted from the API key (ck_app_{appId}_{secret})
        /// </summary>
        /// <param name="jobs">List of recurring jobs to sync</param>
        /// <param name="apiKey">Your CronRadar API key (or set CRONRADAR_API_KEY env var)</param>
        /// <param name="removeStale">Remove monitors that no longer exist in the job list</param>
        public static async Task SyncJobsAsync(
            List<RecurringJobInfo> jobs,
            string apiKey = null,
            bool removeStale = false)
        {
            try
            {
                // Get API key from environment if not provided
                apiKey = apiKey ?? Environment.GetEnvironmentVariable("CRONRADAR_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("[CronRadar.Hangfire] Warning: No API key provided for sync. Set CRONRADAR_API_KEY environment variable.");
                    return;
                }

                var baseUrl = Environment.GetEnvironmentVariable("CRONRADAR_BASE_URL") ?? "https://cronradar.com";

                // Convert job info to monitor format
                var monitors = jobs.Select(job => new
                {
                    key = job.Id,
                    name = job.Name ?? job.Id,
                    schedule = job.Schedule,
                    gracePeriod = job.GracePeriod ?? 60,
                    metadata = new
                    {
                        description = job.Description
                    }
                }).ToList();

                if (!monitors.Any())
                {
                    Console.WriteLine("[CronRadar.Hangfire] No recurring jobs provided to sync.");
                    return;
                }

                // Prepare sync request
                var syncRequest = new
                {
                    source = "hangfire",
                    version = "0.0.3",
                    monitors = monitors,
                    removeStale = removeStale
                };

                var json = JsonSerializer.Serialize(syncRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Make sync request (application ID is extracted from API key on server)
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync");
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:"))}");
                request.Content = content;

                var response = await _http.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<SyncResponse>(responseBody);

                    Console.WriteLine($"[CronRadar.Hangfire] Sync successful: " +
                        $"Created={result?.Summary?.Created ?? 0}, " +
                        $"Updated={result?.Summary?.Updated ?? 0}, " +
                        $"Removed={result?.Summary?.Removed ?? 0}");
                }
                else
                {
                    Console.WriteLine($"[CronRadar.Hangfire] Sync failed: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CronRadar.Hangfire] Error during sync: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy method - attempts to auto-discover Hangfire jobs
        /// Note: Auto-discovery is limited in Hangfire, consider using SyncJobsAsync with explicit job list
        /// </summary>
        public static async Task SyncRecurringJobsAsync(
            string apiKey = null,
            bool removeStale = true)
        {
            try
            {
                // Get API key from environment if not provided
                apiKey = apiKey ?? Environment.GetEnvironmentVariable("CRONRADAR_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("[CronRadar.Hangfire] Warning: No API key provided for sync. Set CRONRADAR_API_KEY environment variable.");
                    return;
                }

                var baseUrl = Environment.GetEnvironmentVariable("CRONRADAR_BASE_URL") ?? "https://cronradar.com";

                // Get all recurring jobs from Hangfire
                var monitors = GetRecurringJobMonitors();

                if (!monitors.Any())
                {
                    Console.WriteLine("[CronRadar.Hangfire] No recurring jobs found to sync.");
                    return;
                }

                // Prepare sync request
                var syncRequest = new
                {
                    source = "hangfire",
                    version = "0.0.2",
                    monitors = monitors,
                    removeStale = removeStale
                };

                var json = JsonSerializer.Serialize(syncRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Make sync request (application ID is extracted from API key on server)
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync");
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:"))}");
                request.Content = content;

                var response = await _http.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<SyncResponse>(responseBody);

                    Console.WriteLine($"[CronRadar.Hangfire] Sync successful: " +
                        $"Created={result?.Summary?.Created ?? 0}, " +
                        $"Updated={result?.Summary?.Updated ?? 0}, " +
                        $"Removed={result?.Summary?.Removed ?? 0}");
                }
                else
                {
                    Console.WriteLine($"[CronRadar.Hangfire] Sync failed: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CronRadar.Hangfire] Error during sync: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all recurring jobs from Hangfire and convert to monitor format
        /// </summary>
        private static List<object> GetRecurringJobMonitors()
        {
            var monitors = new List<object>();

            try
            {
                using (var connection = JobStorage.Current.GetConnection())
                {
                    var recurringJobs = connection.GetRecurringJobs();

                    foreach (var job in recurringJobs)
                    {
                        monitors.Add(new
                        {
                            key = job.Id, // Use the job ID as-is (e.g., "check-overdue-pings")
                            name = GenerateReadableName(job.Id),
                            schedule = job.Cron,
                            gracePeriod = 60 // Default grace period
                        });
                    }

                    Console.WriteLine($"[CronRadar.Hangfire] Discovered {monitors.Count} recurring jobs for sync");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CronRadar.Hangfire] Error reading recurring jobs: {ex.Message}");
            }

            return monitors;
        }

        /// <summary>
        /// Generate a human-readable name from a kebab-case job ID
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


        // Simple response classes for deserialization
        private class SyncResponse
        {
            public SyncSummary Summary { get; set; }
        }

        private class SyncSummary
        {
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Removed { get; set; }
        }
    }

    /// <summary>
    /// Extension method to add sync to Hangfire configuration
    /// </summary>
    public static class CronRadarSyncExtensions
    {
        /// <summary>
        /// Configure CronRadar with automatic job syncing
        /// Application ID is extracted from the API key (ck_app_{appId}_{secret})
        /// NOTE: This method is now deprecated. Use UseCronRadar() which includes auto-sync by default.
        /// </summary>
        [Obsolete("Use UseCronRadar() instead - it now includes auto-sync by default")]
        public static IGlobalConfiguration UseCronRadarWithSync(
            this IGlobalConfiguration configuration,
            string apiKey = null,
            bool syncOnStartup = true,
            bool removeStale = true)
        {
            // Just call the main UseCronRadar method which now includes auto-sync
            return configuration.UseCronRadar(apiKey, autoSync: syncOnStartup);
        }
    }
}