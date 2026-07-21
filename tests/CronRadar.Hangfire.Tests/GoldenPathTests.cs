using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CronRadar.Hangfire.Tests;

[CollectionDefinition("Hangfire golden path", DisableParallelization = true)]
public sealed class HangfireGoldenPathCollection
{
}

[Collection("Hangfire golden path")]
public class GoldenPathTests
{
    [Fact(Timeout = 30_000)]
    public async Task RealHost_DiscoversAndRunsRecurringJob_WithoutDashboardSetup()
    {
        ExampleJobs.Executions = 0;
        ExampleJobs.RetryAttempts = 0;
        await using var endpoint = await RecordingEndpoint.StartAsync();
        Environment.SetEnvironmentVariable("CRONRADAR_BASE_URL", endpoint.BaseUrl);
        Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", "ck_app_test_environment_secret");
        Environment.SetEnvironmentVariable("CRONRADAR_DEBUG", "true");
        global::CronRadar.CronRadar.DebugLoggingEnabled = true;

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHangfire(configuration => configuration.UseMemoryStorage());
                services.AddHangfireServer(options => options.WorkerCount = 1);
                services.AddCronRadarHangfire(reconcileIntervalMinutes: 15);
            })
            .Build();

        JobStorage.Current = host.Services.GetRequiredService<JobStorage>();
        RecurringJob.AddOrUpdate<ExampleJobs>(
            "send-daily-report",
            jobs => jobs.SendDailyReport(),
            "0 9 * * *");
        RecurringJob.AddOrUpdate<ExampleJobs>(
            "internal-maintenance",
            jobs => jobs.InternalMaintenance(),
            "0 3 * * *");
        RecurringJob.AddOrUpdate<ExampleJobs>(
            "retry-safe-report",
            jobs => jobs.RetrySafeReport(),
            "0 10 * * *");
        RecurringJob.AddOrUpdate<ExampleJobs>(
            "terminal-failure",
            jobs => jobs.TerminalFailure(),
            "0 11 * * *");

        await host.StartAsync();

        var sync = await endpoint.WaitForAsync(request => request.Path == "/api/sync");
        Assert.Contains("send-daily-report", sync.Body);
        Assert.Contains("0 9 * * *", sync.Body);
        Assert.DoesNotContain("internal-maintenance", sync.Body);
        Assert.DoesNotContain("cronradar:reconcile", sync.Body);

        var recurringJobs = new RecurringJobManager(JobStorage.Current);
        recurringJobs.TriggerJob("send-daily-report");
        var start = await endpoint.WaitForAsync(request => request.Path.Contains("send-daily-report") && request.Path.Contains("/start?", StringComparison.Ordinal));
        var complete = await endpoint.WaitForAsync(request => request.Path.Contains("send-daily-report") && request.Path.Contains("/complete?", StringComparison.Ordinal));
        Assert.Equal(QueryValue(start.Path, "run_id"), QueryValue(complete.Path, "run_id"));
        Assert.False(string.IsNullOrWhiteSpace(QueryValue(start.Path, "run_id")));
        Assert.Contains("records_processed", complete.Body);
        Assert.Contains("42", complete.Body);

        Assert.Equal(1, ExampleJobs.Executions);

        recurringJobs.TriggerJob("retry-safe-report");
        var retryComplete = await endpoint.WaitForAsync(request => request.Path.Contains("retry-safe-report") && request.Path.Contains("/complete?"));
        Assert.Equal(2, ExampleJobs.RetryAttempts);
        Assert.False(endpoint.Any(request => request.Path.Contains("retry-safe-report") && request.Path.Contains("/fail")));
        Assert.Equal("1", QueryValue(retryComplete.Path, "attempt"));

        recurringJobs.TriggerJob("terminal-failure");
        await endpoint.WaitForAsync(request => request.Path.Contains("terminal-failure") && request.Path.Contains("/fail"));

        endpoint.StopAccepting();
        var outageJobId = recurringJobs.TriggerJob("send-daily-report");
        await WaitForJobStateAsync(outageJobId, "Succeeded");
        Assert.Equal(2, ExampleJobs.Executions);
        await host.StopAsync();
    }

    private static async Task WaitForJobStateAsync(string backgroundJobId, string expectedState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            using var connection = JobStorage.Current.GetConnection();
            if (connection.GetJobData(backgroundJobId)?.State == expectedState) return;
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException($"Hangfire job {backgroundJobId} did not reach {expectedState}.");
    }

    private static string? QueryValue(string path, string key)
    {
        var query = new Uri("http://localhost" + path).Query.TrimStart('?');
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .FirstOrDefault();
    }

    public sealed class ExampleJobs
    {
        public static int Executions;
        public static int RetryAttempts;

        public void SendDailyReport()
        {
            Interlocked.Increment(ref Executions);
            CronRadarOutcome.Report("records_processed", 42);
            CronRadarOutcome.Report("successful", true);
        }

        [SkipMonitor]
        public void InternalMaintenance() => throw new InvalidOperationException("Must never run in this test");

        [AutomaticRetry(Attempts = 1, DelaysInSeconds = new int[] { 0 })]
        public void RetrySafeReport()
        {
            if (Interlocked.Increment(ref RetryAttempts) == 1)
                throw new InvalidOperationException("transient");
        }

        [AutomaticRetry(Attempts = 0)]
        public void TerminalFailure() => throw new InvalidOperationException("terminal");
    }

    private sealed record RecordedRequest(string Path, string Body);

    private sealed class RecordingEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private readonly Task _server;

        private RecordingEndpoint(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _server = ServeAsync();
        }

        public string BaseUrl { get; }

        public static Task<RecordingEndpoint> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new RecordingEndpoint(listener));
        }

        public async Task<RecordedRequest> WaitForAsync(Func<RecordedRequest, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                var match = _requests.FirstOrDefault(predicate);
                if (match != null) return match;
                await Task.Delay(25, timeout.Token);
            }
            throw new TimeoutException("Expected CronRadar request was not received. Paths: " +
                string.Join(", ", _requests.Select(request => request.Path)));
        }

        public bool Any(Func<RecordedRequest, bool> predicate) => _requests.Any(predicate);

        public void StopAccepting()
        {
            if (_stopping.IsCancellationRequested) return;
            _stopping.Cancel();
            _listener.Stop();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                    var requestLine = await reader.ReadLineAsync(_stopping.Token) ?? string.Empty;
                    var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? string.Empty;
                    var contentLength = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stopping.Token)))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                    }

                    var buffer = new char[contentLength];
                    var read = 0;
                    while (read < contentLength)
                        read += await reader.ReadAsync(buffer.AsMemory(read, contentLength - read), _stopping.Token);
                    _requests.Enqueue(new RecordedRequest(path, new string(buffer)));

                    var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stopping.Token);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            StopAccepting();
            try { await _server; } catch (OperationCanceledException) { }
            _stopping.Dispose();
        }
    }
}
