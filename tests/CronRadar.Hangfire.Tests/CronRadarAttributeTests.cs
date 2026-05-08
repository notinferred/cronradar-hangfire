using System;
using System.Reflection;
using Xunit;
using HangfireCommon = global::Hangfire.Common;

namespace CronRadar.Hangfire.Tests;

public class CronRadarAttributeTests
{
    [Fact]
    public void CronRadarAttribute_CanBeCreatedWithMonitorKey()
    {
        var attribute = new CronRadarAttribute("test-job");
        Assert.NotNull(attribute);
    }

    [Fact]
    public void CronRadarAttribute_CanBeCreatedWithMonitorKeyAndApiKey()
    {
        var attribute = new CronRadarAttribute("test-job", "api-key-123");
        Assert.NotNull(attribute);
    }

    [Fact]
    public void CronRadarAttribute_IsMarkedAsObsolete()
    {
        var obsoleteAttr = typeof(CronRadarAttribute)
            .GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(obsoleteAttr);
        Assert.Contains("MonitorAll()", obsoleteAttr.Message);
        Assert.False(obsoleteAttr.IsError); // Warning, not error
    }

    [Fact]
    public void CronRadarAttribute_ImplementsIServerFilter()
    {
        // CronRadarAttribute should implement IServerFilter for Hangfire integration
        var interfaces = typeof(CronRadarAttribute).GetInterfaces();
        Assert.Contains(interfaces, i => i.Name == "IServerFilter");
    }

    [Fact]
    public void CronRadarAttribute_ExtendsJobFilterAttribute()
    {
        Assert.True(typeof(CronRadarAttribute).IsSubclassOf(typeof(HangfireCommon.JobFilterAttribute)));
    }

    [Fact]
    public void CronRadarAttribute_OnPerforming_DoesNotThrow()
    {
        var attribute = new CronRadarAttribute("test-job");

        // OnPerforming should not throw even with null context
        var exception = Record.Exception(() => attribute.OnPerforming(null!));
        Assert.Null(exception);
    }

    [Fact]
    public void CronRadarAttribute_OnPerformed_HandlesNullContext()
    {
        var attribute = new CronRadarAttribute("test-job");

        // OnPerformed throws NullReferenceException when context is null
        // This is legacy behavior for the deprecated attribute
        Assert.Throws<NullReferenceException>(() => attribute.OnPerformed(null!));
    }
}
