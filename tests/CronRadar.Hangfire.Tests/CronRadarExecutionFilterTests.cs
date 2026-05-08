using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Moq;
using Moq.Protected;
using Xunit;

namespace CronRadar.Hangfire.Tests;

/// <summary>
/// Tests for the CronRadarExecutionFilter behavior through the public MonitorAll API.
/// Note: Hangfire's PerformingContext and PerformedContext are not easily mockable
/// due to non-virtual properties, so we test what can be tested through the public API.
/// </summary>
public class CronRadarExecutionFilterTests : IDisposable
{
    private readonly string _originalApiKey;
    private readonly bool _originalDebug;

    public CronRadarExecutionFilterTests()
    {
        _originalApiKey = Environment.GetEnvironmentVariable("CRONRADAR_API_KEY") ?? "";
        _originalDebug = CronRadar.DebugLoggingEnabled;

        // Set up test environment
        Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", "test-api-key");
        CronRadar.DebugLoggingEnabled = false;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", _originalApiKey);
        CronRadar.DebugLoggingEnabled = _originalDebug;
        ResetHttpClient();
        ResetExtensionsState();
    }

    private void ResetHttpClient()
    {
        var field = typeof(CronRadar).GetField("_httpClient", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, null);
    }

    private void ResetExtensionsState()
    {
        // Reset the _initialized flag in CronRadarExtensions so tests are isolated
        var initField = typeof(CronRadarExtensions).GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic);
        initField?.SetValue(null, false);

        var apiKeyField = typeof(CronRadarExtensions).GetField("_apiKey", BindingFlags.Static | BindingFlags.NonPublic);
        apiKeyField?.SetValue(null, null);

        var schedulesField = typeof(CronRadarExtensions).GetField("_schedules", BindingFlags.Static | BindingFlags.NonPublic);
        var schedules = schedulesField?.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<string, string?>;
        schedules?.Clear();
    }

    private IServerFilter? GetLastExecutionFilter()
    {
        return GlobalJobFilters.Filters
            .Where(f => f.Instance.GetType().Name == "CronRadarExecutionFilter")
            .LastOrDefault()?.Instance as IServerFilter;
    }

    #region MonitorAll Tests

    [Fact]
    public void MonitorAll_AddsFilterToGlobalFilters()
    {
        // Arrange
        ResetExtensionsState();
        var initialCount = GlobalJobFilters.Filters
            .Count(f => f.Instance.GetType().Name == "CronRadarExecutionFilter");

        // Act
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        // Assert
        var newCount = GlobalJobFilters.Filters
            .Count(f => f.Instance.GetType().Name == "CronRadarExecutionFilter");

        Assert.Equal(initialCount + 1, newCount);
    }

    [Fact]
    public void MonitorAll_IgnoresDuplicateCalls()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();

        // Act - call MonitorAll twice
        mockConfig.Object.MonitorAll();
        var countAfterFirst = GlobalJobFilters.Filters
            .Count(f => f.Instance.GetType().Name == "CronRadarExecutionFilter");

        mockConfig.Object.MonitorAll();
        var countAfterSecond = GlobalJobFilters.Filters
            .Count(f => f.Instance.GetType().Name == "CronRadarExecutionFilter");

        // Assert - should not add duplicate filter
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public void MonitorAll_ReturnsConfigurationForChaining()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();

        // Act
        var result = mockConfig.Object.MonitorAll();

        // Assert
        Assert.Same(mockConfig.Object, result);
    }

    [Fact]
    public void MonitorAll_StoresApiKeyInternally()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();

        // Act
        mockConfig.Object.MonitorAll("custom-api-key-123");

        // Assert - verify the API key was stored via reflection
        var apiKeyField = typeof(CronRadarExtensions).GetField("_apiKey", BindingFlags.Static | BindingFlags.NonPublic);
        var storedApiKey = apiKeyField?.GetValue(null) as string;
        Assert.Equal("custom-api-key-123", storedApiKey);
    }

    [Fact]
    public void MonitorAll_SetsInitializedFlag()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();

        // Act
        mockConfig.Object.MonitorAll();

        // Assert
        var initField = typeof(CronRadarExtensions).GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic);
        var isInitialized = (bool)(initField?.GetValue(null) ?? false);
        Assert.True(isInitialized);
    }

    [Fact]
    public void MonitorAll_WithoutApiKey_UsesEnvironmentVariable()
    {
        // Arrange
        ResetExtensionsState();
        Environment.SetEnvironmentVariable("CRONRADAR_API_KEY", "env-api-key");
        var mockConfig = new Mock<IGlobalConfiguration>();

        // Act
        mockConfig.Object.MonitorAll();

        // Assert - API key field should be null (will use env var at runtime)
        var apiKeyField = typeof(CronRadarExtensions).GetField("_apiKey", BindingFlags.Static | BindingFlags.NonPublic);
        var storedApiKey = apiKeyField?.GetValue(null) as string;
        Assert.Null(storedApiKey);
    }

    #endregion

    #region Filter Registration Tests

    [Fact]
    public void ExecutionFilter_ImplementsIServerFilter()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        // Act
        var filter = GetLastExecutionFilter();

        // Assert
        Assert.NotNull(filter);
        Assert.IsAssignableFrom<IServerFilter>(filter);
    }

    [Fact]
    public void ExecutionFilter_ExtendsJobFilterAttribute()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        // Act
        var filter = GlobalJobFilters.Filters
            .Where(f => f.Instance.GetType().Name == "CronRadarExecutionFilter")
            .LastOrDefault()?.Instance;

        // Assert
        Assert.NotNull(filter);
        Assert.IsAssignableFrom<JobFilterAttribute>(filter);
    }

    [Fact]
    public void ExecutionFilter_HasBothFilterMethods()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        // Act
        var filter = GetLastExecutionFilter();

        // Assert - verify both methods exist
        Assert.NotNull(filter);
        var filterType = filter.GetType();
        var onPerformingMethod = filterType.GetMethod("OnPerforming");
        var onPerformedMethod = filterType.GetMethod("OnPerformed");
        Assert.NotNull(onPerformingMethod);
        Assert.NotNull(onPerformedMethod);
    }

    #endregion

    #region Null Context Handling Tests

    [Fact]
    public void OnPerforming_WithNullContext_DoesNotThrow()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        var filter = GetLastExecutionFilter();
        Assert.NotNull(filter);

        // Act & Assert - should not throw
        var exception = Record.Exception(() => filter!.OnPerforming(null!));
        Assert.Null(exception);
    }

    [Fact]
    public void OnPerformed_WithNullContext_DoesNotThrow()
    {
        // Arrange
        ResetExtensionsState();
        var mockConfig = new Mock<IGlobalConfiguration>();
        mockConfig.Object.MonitorAll();

        var filter = GetLastExecutionFilter();
        Assert.NotNull(filter);

        // Act & Assert - should not throw
        var exception = Record.Exception(() => filter!.OnPerformed(null!));
        Assert.Null(exception);
    }

    #endregion

    #region SkipMonitor Attribute Tests

    [Fact]
    public void SkipMonitorAttribute_TargetsMethod()
    {
        // Arrange & Act
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        // Assert
        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Method, attributeUsage.ValidOn);
    }

    [Fact]
    public void SkipMonitorAttribute_AllowMultipleIsFalse()
    {
        // Arrange & Act
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        // Assert
        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void SkipMonitorAttribute_InheritedIsFalse()
    {
        // Arrange & Act
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        // Assert
        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void SkipMonitorAttribute_CanBeAppliedToMethod()
    {
        // Arrange
        var method = typeof(TestJobWithSkipMonitor).GetMethod(nameof(TestJobWithSkipMonitor.Execute));

        // Act
        var attribute = method?.GetCustomAttribute<SkipMonitorAttribute>();

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void SkipMonitorAttribute_IsNotPresentOnRegularMethod()
    {
        // Arrange
        var method = typeof(TestJob).GetMethod(nameof(TestJob.Execute));

        // Act
        var attribute = method?.GetCustomAttribute<SkipMonitorAttribute>();

        // Assert
        Assert.Null(attribute);
    }

    #endregion

    #region Schedule Cache Tests

    [Fact]
    public void ScheduleCache_InitializedAsConcurrentDictionary()
    {
        // Arrange
        ResetExtensionsState();

        // Act
        var schedulesField = typeof(CronRadarExtensions).GetField("_schedules", BindingFlags.Static | BindingFlags.NonPublic);
        var schedules = schedulesField?.GetValue(null);

        // Assert
        Assert.NotNull(schedules);
        Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, string?>>(schedules);
    }

    [Fact]
    public void ScheduleCache_IsEmptyInitially()
    {
        // Arrange
        ResetExtensionsState();

        // Act
        var schedulesField = typeof(CronRadarExtensions).GetField("_schedules", BindingFlags.Static | BindingFlags.NonPublic);
        var schedules = schedulesField?.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<string, string?>;

        // Assert
        Assert.NotNull(schedules);
        Assert.Empty(schedules);
    }

    #endregion

    #region Test Helper Classes

    public class TestJob
    {
        public void Execute()
        {
            // Test job method
        }
    }

    public class TestJobWithSkipMonitor
    {
        [SkipMonitor]
        public void Execute()
        {
            // Test job method with SkipMonitor attribute
        }
    }

    #endregion
}
