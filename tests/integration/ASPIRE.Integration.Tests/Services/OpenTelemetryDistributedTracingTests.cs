// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Telemetry;

namespace ASPIRE.Tests.Services;

public sealed class OpenTelemetryDistributedTracingTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _startedActivities = [];

    public OpenTelemetryDistributedTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NexusTracing.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _startedActivities.Add
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public void OpenTelemetryDistributedTracing_StartCommandActivity_EmitsExpectedTagsAndAttributes()
    {
        // Act
        using Activity? activity = NexusTracing.StartCommandActivity("ChannelMessage", 0x0200, 1001);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("ChatCommand/ChannelMessage", activity.DisplayName);
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("tcp_binary_protocol", activity.GetTagItem("rpc.system"));
        Assert.Equal("ChatServer", activity.GetTagItem("rpc.service"));
        Assert.Equal("ChannelMessage", activity.GetTagItem("rpc.method"));
        Assert.Equal("ChannelMessage", activity.GetTagItem("nexus.command_name"));
        Assert.Equal("0x0200", activity.GetTagItem("nexus.command_code"));
        Assert.Equal(1001, activity.GetTagItem("nexus.account_id"));
    }

    [Fact]
    public void OpenTelemetryDistributedTracing_StartPhpActivity_EmitsMasterServerTags()
    {
        // Act
        using Activity? activity = NexusTracing.StartPhpActivity("auth", 4050);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("PhpProtocol/auth", activity.DisplayName);
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("php_wire_protocol", activity.GetTagItem("rpc.system"));
        Assert.Equal("MasterServer", activity.GetTagItem("rpc.service"));
        Assert.Equal("auth", activity.GetTagItem("rpc.method"));
        Assert.Equal("auth", activity.GetTagItem("nexus.php_action"));
        Assert.Equal(4050, activity.GetTagItem("nexus.account_id"));
    }

    [Fact]
    public void OpenTelemetryDistributedTracing_StartRedisActivity_EmitsRedisCacheTierTags()
    {
        // Act
        using Activity? activity = NexusTracing.StartRedisActivity("GET", "account:profile:1001");

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("Redis/GET", activity.DisplayName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("redis", activity.GetTagItem("db.system"));
        Assert.Equal("GET", activity.GetTagItem("db.operation"));
        Assert.Equal("account:profile:1001", activity.GetTagItem("db.redis.key"));
        Assert.Equal("L2_Redis", activity.GetTagItem("nexus.cache_tier"));
    }

    [Fact]
    public void OpenTelemetryDistributedTracing_StartDatabaseActivity_EmitsDatabaseTags()
    {
        // Act
        using Activity? activity = NexusTracing.StartDatabaseActivity("SELECT", "accounts");

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("Database/SELECT accounts", activity.DisplayName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("postgresql", activity.GetTagItem("db.system"));
        Assert.Equal("merrick_db", activity.GetTagItem("db.name"));
        Assert.Equal("SELECT", activity.GetTagItem("db.operation"));
        Assert.Equal("accounts", activity.GetTagItem("db.sql.table"));
    }

    [Fact]
    public void W3CTraceContext_InjectAndExtract_PropagatesTraceparentAcrossCarriers()
    {
        // Arrange
        ActivitySource testSource = new("Test.Source");
        using ActivityListener testListener = new()
        {
            ShouldListenTo = source => source.Name == "Test.Source",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(testListener);

        using Activity? rootActivity = testSource.StartActivity("RootRequest", ActivityKind.Server);
        Assert.NotNull(rootActivity);

        Dictionary<string, string> carrier = [];

        // Act - Inject
        NexusTracing.InjectTraceContext(rootActivity.Context, carrier);

        // Assert injection has traceparent
        Assert.True(carrier.ContainsKey("traceparent"));
        string traceParent = carrier["traceparent"];
        Assert.False(string.IsNullOrWhiteSpace(traceParent));

        // Act - Extract in simulated downstream service
        ActivityContext extractedContext = NexusTracing.ExtractTraceContext(carrier);

        // Assert extracted context matches root trace ID
        Assert.NotEqual(default, extractedContext);
        Assert.Equal(rootActivity.TraceId, extractedContext.TraceId);
        Assert.Equal(rootActivity.SpanId, extractedContext.SpanId);
    }

    [Fact]
    public void OpenTelemetryDistributedTracing_RecordException_SetsErrorStatusAndExceptionEvents()
    {
        // Arrange
        using Activity? activity = NexusTracing.StartPhpActivity("get_account_stats");
        Assert.NotNull(activity);
        InvalidOperationException testEx = new("Simulated Redis timeout error");

        // Act
        NexusTracing.RecordException(activity, testEx);

        // Assert
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Simulated Redis timeout error", activity.StatusDescription);
        Assert.Contains(activity.Events, ev => ev.Name == "exception");
    }
}
