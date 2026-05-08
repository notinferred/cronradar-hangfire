using System;
using System.Reflection;
using Xunit;

namespace CronRadar.Hangfire.Tests;

public class SkipMonitorAttributeTests
{
    [Fact]
    public void SkipMonitorAttribute_CanBeAppliedToMethod()
    {
        var method = typeof(TestJobsWithSkip).GetMethod(nameof(TestJobsWithSkip.SkippedJob));
        var attribute = method?.GetCustomAttribute<SkipMonitorAttribute>();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void SkipMonitorAttribute_IsNotPresentOnRegularMethod()
    {
        var method = typeof(TestJobsWithSkip).GetMethod(nameof(TestJobsWithSkip.RegularJob));
        var attribute = method?.GetCustomAttribute<SkipMonitorAttribute>();

        Assert.Null(attribute);
    }

    [Fact]
    public void SkipMonitorAttribute_AllowMultipleIsFalse()
    {
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void SkipMonitorAttribute_InheritedIsFalse()
    {
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void SkipMonitorAttribute_TargetsMethodsOnly()
    {
        var attributeUsage = typeof(SkipMonitorAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Method, attributeUsage.ValidOn);
    }

    // Test helper class
    private class TestJobsWithSkip
    {
        [SkipMonitor]
        public void SkippedJob() { }

        public void RegularJob() { }
    }
}
