using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Internal HTTP client for the Hangfire extension.
    /// Extensions can accept API keys to integrate with framework configuration.
    /// </summary>
    internal static class CronRadarClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            BaseAddress = new Uri(
                Environment.GetEnvironmentVariable("CRONRADAR_BASE_URL")
                ?? "https://cronradar.com"
            ),
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Ping CronRadar with optional API key parameter (for framework integration).
        /// </summary>
        internal static void Ping(string monitorKey, string apiKey = null)
        {
            try
            {
                // Use provided API key or fall back to environment variable
                apiKey = apiKey ?? Environment.GetEnvironmentVariable("CRONRADAR_API_KEY") ?? "";

                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine($"[CronRadar.Hangfire] Warning: No API key provided and CRONRADAR_API_KEY not set. Monitor '{monitorKey}' will not be tracked.");
                    return;
                }

                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:"));
                var request = new HttpRequestMessage(HttpMethod.Post, $"/ping/{Uri.EscapeDataString(monitorKey)}");
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {auth}");
                _http.SendAsync(request).Wait(5000);
            }
            catch
            {
                // Never throw - protect Hangfire job execution
            }
        }
    }
}