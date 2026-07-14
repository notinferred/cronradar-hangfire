using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// DI entrypoint for CronRadar + Hangfire. Wires execution telemetry AND continuous
    /// reconcile (startup + periodic) so every recurring job is declared to CronRadar
    /// before it ever runs — closing the dead-man gap for jobs that fire infrequently.
    /// </summary>
    public static class CronRadarServiceCollectionExtensions
    {
        /// <summary>
        /// Enables CronRadar monitoring for all Hangfire recurring jobs. Use [SkipMonitor]
        /// to exclude a job. Registers:
        ///  - the execution filter (start/complete/fail telemetry);
        ///  - a startup + periodic reconcile of the recurring-jobs schedule.
        /// </summary>
        /// <param name="services">The service collection (call after AddHangfire).</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var).</param>
        /// <param name="reconcileIntervalMinutes">How often to re-declare the schedule (heartbeat). 1..59.</param>
        public static IServiceCollection AddCronRadarHangfire(
            this IServiceCollection services,
            string? apiKey = null,
            int reconcileIntervalMinutes = 15)
        {
            // Telemetry filter (shared static state with MonitorAll; idempotent)
            CronRadarExtensions.EnsureExecutionFilter(apiKey);

            services.AddHostedService(_ => new CronRadarHangfireHostedService(reconcileIntervalMinutes));

            return services;
        }
    }

    /// <summary>
    /// Runs after the host starts (recurring jobs registered in Program.cs are already in
    /// storage): declares them immediately, then schedules a recurring reconcile.
    /// </summary>
    internal sealed class CronRadarHangfireHostedService : IHostedService
    {
        private readonly string _cron;

        public CronRadarHangfireHostedService(int reconcileIntervalMinutes)
        {
            var minutes = reconcileIntervalMinutes < 1 ? 1 : (reconcileIntervalMinutes > 59 ? 59 : reconcileIntervalMinutes);
            _cron = $"*/{minutes} * * * *";
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Periodic reconcile (heartbeat + catches jobs registered after startup)
                RecurringJob.AddOrUpdate<CronRadarHangfireReconciler>(
                    "cronradar:reconcile",
                    x => x.Reconcile(),
                    _cron);

                // Immediate reconcile so coverage exists at t=0, not after the first tick
                await new CronRadarHangfireReconciler().Reconcile().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (CronRadar.DebugLoggingEnabled)
                {
                    Console.Error.WriteLine($"[CronRadar.Hangfire] Startup reconcile error: {ex.Message}");
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
