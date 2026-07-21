using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using CronRadar;

namespace CronRadar.Hangfire
{
    /// <summary>
    /// Extension methods for CronRadar integration with Hangfire.
    /// Monitors all jobs by default - use [SkipMonitor] attribute to opt-out.
    /// </summary>
    public static class CronRadarExtensions
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new object();
        private static string? _apiKey = null;
        private static bool _missingKeyReported;
        private static readonly ConcurrentDictionary<string, string> _recurringExecutions = new();

        /// <summary>API key captured during initialization, used by the reconciler.</summary>
        internal static string? ApiKey => _apiKey;

        /// <summary>
        /// Registers the execution filter (start/complete/fail telemetry) exactly once
        /// and captures the API key. Shared by MonitorAll and AddCronRadarHangfire.
        /// </summary>
        internal static void EnsureExecutionFilter(string? apiKey)
        {
            lock (_initLock)
            {
                if (apiKey != null)
                {
                    _apiKey = apiKey; // Store for filter/reconciler (avoids env-var mutation)
                }

                if (_initialized)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_apiKey) &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CronRadarConstants.ApiKeyEnvironmentVariable)) &&
                    !_missingKeyReported)
                {
                    _missingKeyReported = true;
                    Console.Error.WriteLine("[CronRadar.Hangfire] CRONRADAR_API_KEY is not set. Create an application-specific key in CronRadar, add it to the deployed Hangfire process, and restart the host. Hangfire will continue normally.");
                }

                _initialized = true;

                // Add single execution filter - handles lifecycle tracking (start/complete/fail)
                GlobalJobFilters.Filters.Add(new CronRadarExecutionFilter());
            }
        }

        /// <summary>
        /// Adds execution telemetry for all jobs (start/complete/fail); jobs with
        /// [SkipMonitor] are excluded. This does NOT pre-register monitors at startup —
        /// for dead-man coverage of jobs that have not run yet, use
        /// <c>services.AddCronRadarHangfire(apiKey)</c> which also reconciles the schedule.
        /// </summary>
        /// <param name="configuration">Hangfire global configuration</param>
        /// <param name="apiKey">Optional API key (defaults to CRONRADAR_API_KEY env var)</param>
        [Obsolete("Use services.AddCronRadarHangfire(apiKey) for startup + periodic reconcile (dead-man coverage). MonitorAll only wires execution telemetry.")]
        public static IGlobalConfiguration MonitorAll(this IGlobalConfiguration configuration, string? apiKey = null)
        {
            Console.WriteLine("[CronRadar.Hangfire] MonitorAll registers execution telemetry only. " +
                "For startup dead-man coverage, use services.AddCronRadarHangfire(apiKey).");
            EnsureExecutionFilter(apiKey);
            return configuration;
        }

        /// <summary>
        /// Filter that monitors job execution with lifecycle tracking.
        /// Auto-registers monitors on first execution via API's self-healing.
        /// Calls StartJob (with schedule for auto-registration), CompleteJob, or FailJob.
        /// </summary>
        private class CronRadarExecutionFilter : JobFilterAttribute, IServerFilter, IElectStateFilter
        {
            public CronRadarExecutionFilter()
            {
                // AutomaticRetryAttribute uses order 20. Observe the candidate
                // after it has converted retryable failures to ScheduledState.
                Order = 1000;
            }

            public void OnPerforming(PerformingContext context)
            {
                if (context?.BackgroundJob?.Id == null) return;

                var method = context.BackgroundJob.Job?.Method;
                if (method == null || !ShouldMonitorJob(method)) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        var recurringJobId = ReadRecurringJobId(connection, context.BackgroundJob.Id);
                        if (string.IsNullOrEmpty(recurringJobId))
                        {
                            if (CronRadar.DebugLoggingEnabled)
                            {
                                Console.Error.WriteLine($"[CronRadar.Hangfire] Job {context.BackgroundJob.Id} has no RecurringJobId - skipping monitoring (one-time job?)");
                            }
                            return;
                        }

                        var monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId!);
                        _recurringExecutions[context.BackgroundJob.Id] = recurringJobId!;

                        // Read the current schedule on every execution. Recurring-job
                        // schedules can change while a host is running; a permanent
                        // cache would recreate a deleted monitor with stale metadata.
                        var schedule = GetJobSchedule(connection, recurringJobId);

                        // Signal job start - API will auto-register if monitor doesn't exist (self-healing)
                        var attempt = context.GetJobParameter<int>("RetryCount");
                        CronRadar.StartJob(monitorKey, schedule, _apiKey, context.BackgroundJob.Id, attempt);
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    if (CronRadar.DebugLoggingEnabled)
                    {
                        Console.Error.WriteLine($"[CronRadar.Hangfire] StartJob error: {ex.Message}");
                    }
                }
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context?.BackgroundJob?.Id == null) return;

                var method = context.BackgroundJob.Job?.Method;
                if (method == null || !ShouldMonitorJob(method)) return;

                try
                {
                    using (var connection = JobStorage.Current.GetConnection())
                    {
                        var recurringJobId = ReadRecurringJobId(connection, context.BackgroundJob.Id);
                        if (string.IsNullOrEmpty(recurringJobId))
                        {
                            if (CronRadar.DebugLoggingEnabled)
                            {
                                Console.Error.WriteLine($"[CronRadar.Hangfire] Job {context.BackgroundJob.Id} has no RecurringJobId - skipping monitoring (one-time job?)");
                            }
                            return;
                        }

                        var monitorKey = CronRadarKeyNormalizer.Normalize(recurringJobId!);

                        // A successful attempt is terminal immediately. Failures are
                        // reported from OnStateElection only after AutomaticRetry has
                        // elected the final Failed state; intermediate attempts remain
                        // Hangfire's concern and do not create false CronRadar incidents.
                        if (context.Exception == null && !context.Canceled)
                        {
                            var attempt = context.GetJobParameter<int>("RetryCount");
                            CronRadar.CompleteJob(monitorKey, _apiKey, context.BackgroundJob.Id, attempt);
                            _recurringExecutions.TryRemove(context.BackgroundJob.Id, out _);
                        }
                        else if (context.Canceled)
                        {
                            // Hangfire may requeue cancellation during shutdown. Do
                            // not report a false failure and do not retain stale state.
                            _recurringExecutions.TryRemove(context.BackgroundJob.Id, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never break user's job
                    if (CronRadar.DebugLoggingEnabled)
                    {
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Lifecycle tracking error: {ex.Message}");
                    }
                }
            }

            public void OnStateElection(ElectStateContext context)
            {
                if (context?.CandidateState is not FailedState failedState) return;

                try
                {
                    var backgroundJob = context.BackgroundJob;
                    var method = backgroundJob?.Job?.Method;
                    if (method == null || !ShouldMonitorJob(method))
                    {
                        if (CronRadar.DebugLoggingEnabled)
                            Console.Error.WriteLine("[CronRadar.Hangfire] Final failure has no monitorable method.");
                        return;
                    }

                    _recurringExecutions.TryGetValue(backgroundJob!.Id, out var recurringJobId);
                    recurringJobId ??= ReadRecurringJobId(context.Connection, backgroundJob.Id);
                    if (string.IsNullOrEmpty(recurringJobId))
                    {
                        if (CronRadar.DebugLoggingEnabled)
                            Console.Error.WriteLine($"[CronRadar.Hangfire] Final failure {backgroundJob.Id} has no RecurringJobId.");
                        return;
                    }

                    if (CronRadar.DebugLoggingEnabled)
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Reporting final failure for {recurringJobId}.");

                    CronRadar.FailJob(
                        CronRadarKeyNormalizer.Normalize(recurringJobId!),
                        failedState.Exception?.Message ?? failedState.Reason ?? "Hangfire job failed after retries were exhausted",
                        _apiKey,
                        backgroundJob.Id);
                    _recurringExecutions.TryRemove(backgroundJob.Id, out _);
                }
                catch (Exception ex)
                {
                    if (CronRadar.DebugLoggingEnabled)
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Final failure reporting error: {ex.Message}");
                }
            }

            private bool ShouldMonitorJob(MethodInfo method)
            {
                // Monitor all jobs unless [SkipMonitor] attribute is present
                return method.GetCustomAttribute<SkipMonitorAttribute>() == null;
            }

            private string? GetJobSchedule(IStorageConnection connection, string recurringJobId)
            {
                try
                {
                    return connection.GetRecurringJobs()
                        .FirstOrDefault(job => string.Equals(job.Id, recurringJobId, StringComparison.Ordinal))
                        ?.Cron;
                }
                catch (Exception ex)
                {
                    if (CronRadar.DebugLoggingEnabled)
                        Console.Error.WriteLine($"[CronRadar.Hangfire] Schedule lookup error: {ex.Message}");
                }
                return null;
            }

            private static string? ReadRecurringJobId(IStorageConnection connection, string backgroundJobId)
            {
                var raw = connection.GetJobParameter(backgroundJobId, "RecurringJobId");
                if (string.IsNullOrWhiteSpace(raw)) return null;
                if (raw.Length >= 2 && raw[0] == '"')
                {
                    try { return JsonSerializer.Deserialize<string>(raw); }
                    catch { return raw.Trim('"'); }
                }
                return raw;
            }
        }
    }
}
