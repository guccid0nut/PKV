// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpProtocolComplianceTests : IDisposable
{
    private readonly string _testTempDir;

    public McpProtocolComplianceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "kongor_phase4_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

    #region R1: Capabilities & Resource Subscriptions

    [Fact]
    public async Task Initialize_OnConstruction_AdvertisesResourceSubscribeCapability()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var root = doc.RootElement;
        root.GetProperty("result")
            .GetProperty("capabilities")
            .GetProperty("resources")
            .GetProperty("subscribe")
            .GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public async Task ServerDiscover_WhenInvoked_AdvertisesResourceSubscribeCapability()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"server/discover\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var root = doc.RootElement;
        root.GetProperty("result")
            .GetProperty("capabilities")
            .GetProperty("resources")
            .GetProperty("subscribe")
            .GetBoolean()
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("kongor://client/console")]
    [InlineData("kongor://client/telemetry")]
    [InlineData("kongor://client/errors")]
    [InlineData("kongor://logs/master-server")]
    [InlineData("kongor://logs/chat-server")]
    [InlineData("kongor://engine/vfs-manifest")]
    [InlineData("kongor://cluster/metrics")]
    [InlineData("kongor://replays/latest/summary")]
    [InlineData("kongor://servers/{port}/logs")]
    [InlineData("kongor://servers/11235/logs")]
    [InlineData("kongor://replays/12345/summary")]
    public async Task ResourcesSubscribe_ValidUri_TracksSubscriptionAndReturnsSuccess(string uri)
    {
        using McpServer server = new();
        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/subscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("result", out _).Should().BeTrue();

        server.IsSubscribed(uri).Should().BeTrue();
        server.Subscriptions.Should().Contain(uri);
    }

    [Fact]
    public async Task ResourcesSubscribe_DuplicateSubscription_IsIdempotent()
    {
        using McpServer server = new();
        string uri = "kongor://client/console";
        string req1 = $"{{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/subscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}";
        string req2 = $"{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"resources/subscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}";

        var resp1 = await server.ProcessMessageAsync(req1);
        var resp2 = await server.ProcessMessageAsync(req2);

        resp1.Should().NotBeNull();
        resp2.Should().NotBeNull();
        server.Subscriptions.Count(s => s.Equals(uri, StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task ResourcesSubscribe_InvalidUri_ReturnsInvalidParamsError()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://unknown/resource\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
        server.IsSubscribed("kongor://unknown/resource").Should().BeFalse();
    }

    [Fact]
    public async Task ResourcesSubscribe_MissingUri_ReturnsInvalidParamsError()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"resources/subscribe\",\"params\":{}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ResourcesUnsubscribe_SubscribedUri_RemovesSubscription()
    {
        using McpServer server = new();
        string uri = "kongor://client/console";

        await server.ProcessMessageAsync($"{{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"resources/subscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}");
        server.IsSubscribed(uri).Should().BeTrue();

        string? unResp = await server.ProcessMessageAsync($"{{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"resources/unsubscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}");

        unResp.Should().NotBeNull();
        using var doc = JsonDocument.Parse(unResp!);
        doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        server.IsSubscribed(uri).Should().BeFalse();
        server.Subscriptions.Should().NotContain(uri);
    }

    [Fact]
    public async Task ResourcesUnsubscribe_InvalidUri_ReturnsInvalidParamsError()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://not/valid\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ResourcesUnsubscribe_ValidUriNotSubscribed_ReturnsSuccessIdempotently()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://client/console\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        server.IsSubscribed("kongor://client/console").Should().BeFalse();
    }

    [Fact]
    public async Task NotifyResourceUpdatedAsync_WhenUpdated_DispatchesNotificationOnlyWhenSubscribed()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        // 1. Notify without subscription -> should NOT emit notification
        await server.NotifyResourceUpdatedAsync("kongor://client/console");
        server.SentNotifications.Should().BeEmpty();

        // 2. Subscribe
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://client/console\"}}");
        server.ClearSentNotifications();

        // 3. Notify with subscription -> should emit notifications/resources/updated
        await server.NotifyResourceUpdatedAsync("kongor://client/console");
        server.SentNotifications.Should().ContainSingle();

        string notificationJson = server.SentNotifications[0];
        using var doc = JsonDocument.Parse(notificationJson);
        doc.RootElement.GetProperty("method").GetString().Should().Be("notifications/resources/updated");
        doc.RootElement.GetProperty("params").GetProperty("uri").GetString().Should().Be("kongor://client/console");

        // 4. Unsubscribe
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://client/console\"}}");
        server.ClearSentNotifications();

        // 5. Notify after unsubscription -> should NOT emit
        await server.NotifyResourceUpdatedAsync("kongor://client/console");
        server.SentNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyResourceUpdatedAsync_DedicatedServerTemplate_NotifiesSubscribers()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        // Subscribe to template
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/{port}/logs\"}}");
        server.ClearSentNotifications();

        // Concrete server port update triggers template notification
        await server.NotifyResourceUpdatedAsync("kongor://servers/11235/logs");
        server.SentNotifications.Should().ContainSingle();
        server.SentNotifications[0].Should().Contain("notifications/resources/updated");
        server.SentNotifications[0].Should().Contain("kongor://servers/{port}/logs");
    }

    [Fact]
    public async Task ClientCommandRemediation_ErrorRecorded_TriggersResourceUpdatedWhenSubscribed()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        // Not subscribed -> record error -> no notification
        server.Registry.ClientCommandRemediationService.RecordAndRemediate("test_cmd", "test error", "TestContext");
        server.SentNotifications.Should().BeEmpty();

        // Subscribe to kongor://client/errors
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":15,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://client/errors\"}}");
        server.ClearSentNotifications();

        // Record error -> triggers update notification
        server.Registry.ClientCommandRemediationService.RecordAndRemediate("test_cmd_2", "test error 2", "TestContext");
        server.SentNotifications.Should().ContainSingle();
        server.SentNotifications[0].Should().Contain("notifications/resources/updated");
        server.SentNotifications[0].Should().Contain("kongor://client/errors");
    }

    [Fact]
    public async Task ResourcesList_WhenInvoked_IncludesClientTelemetry()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"resources/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        responseJson.Should().Contain("kongor://client/telemetry");
    }

    [Fact]
    public async Task ResourcesRead_ClientTelemetry_ReturnsStructuredJson()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://client/telemetry\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        var contents = doc.RootElement.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);

        var first = contents[0];
        first.GetProperty("uri").GetString().Should().Be("kongor://client/telemetry");
        first.GetProperty("mimeType").GetString().Should().Be("application/json");

        string text = first.GetProperty("text").GetString()!;
        using var textDoc = JsonDocument.Parse(text);
        textDoc.RootElement.TryGetProperty("gamePhase", out _).Should().BeTrue();
        textDoc.RootElement.TryGetProperty("hero", out _).Should().BeTrue();
        textDoc.RootElement.TryGetProperty("telemetry", out _).Should().BeTrue();
    }

    #endregion

    #region R2: Operation Progress Reporting

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ToolCall_KongorEngineCachePurge_WithMetaProgressToken_EmitsMultiStageProgress()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_engine_cache_purge\",\"arguments\":{\"action\":\"texture\"},\"_meta\":{\"progressToken\":\"token-purge-1\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("token-purge-1"));
        progressNotifs.Should().Contain(n => n.Contains("\"progress\":1") || n.Contains("\"progress\": 1"));
    }

    [Fact]
    public async Task ToolCall_KongorEngineCachePurge_WithArgumentsProgressToken_EmitsMultiStageProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_engine_cache_purge\",\"arguments\":{\"target\":\"all\",\"progressToken\":12345}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("12345"));
    }

    [Fact]
    public async Task ToolCall_KongorGameServerManage_WithMetaProgressToken_EmitsMultiStageProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_server_manage\",\"arguments\":{\"action\":\"launch\",\"port\":11250,\"map\":\"caldavar\"},\"_meta\":{\"progressToken\":\"token-srv-launch\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("token-srv-launch"));
    }

    [Fact]
    public async Task ToolCall_KongorMatchLaunch_WithArgumentsMetaProgressToken_EmitsMultiStageProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_match_launch\",\"arguments\":{\"map\":\"caldavar\",\"hero\":\"Hero_Pyromancer\",\"_meta\":{\"progressToken\":\"token-match-launch\"}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("token-match-launch"));
    }

    [Fact]
    public async Task ToolCall_WithoutProgressToken_DoesNotEmitProgressNotifications()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":24,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_db_fixture\",\"arguments\":{\"action\":\"status\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        server.SentNotifications.Should().BeEmpty();
    }

    #endregion

    #region R3: W3C Distributed Tracing Context Propagation

    [Fact]
    public async Task ProcessMessageAsync_WithW3CTraceparent_PropagatesTraceIdToLogger()
    {
        string logDir = Path.Combine(_testTempDir, "logs_tracing");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        string testTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        string testSpanId = "00f067aa0ba902b7";
        string traceparent = $"00-{testTraceId}-{testSpanId}-01";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_db_fixture\",\"arguments\":{{\"action\":\"status\"}},\"_meta\":{{\"traceparent\":\"{traceparent}\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string ndjsonContent = File.ReadAllText(ndjsonFiles[0]);
        ndjsonContent.Should().Contain(testTraceId);

        string[] mdFiles = Directory.GetFiles(logDir, "session_*.md");
        mdFiles.Should().NotBeEmpty();
        string mdContent = File.ReadAllText(mdFiles[0]);
        mdContent.Should().Contain(testTraceId);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithRootMetaTraceparent_PropagatesTraceId()
    {
        string logDir = Path.Combine(_testTempDir, "logs_root_tracing");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        string testTraceId = "1234567890abcdef1234567890abcdef";
        string testSpanId = "abcdef1234567890";
        string traceparent = $"00-{testTraceId}-{testSpanId}-01";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"ping\",\"_meta\":{{\"traceparent\":\"{traceparent}\"}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string ndjsonContent = File.ReadAllText(ndjsonFiles[0]);
        ndjsonContent.Should().Contain(testTraceId);
    }

    [Fact]
    public async Task ProcessMessageAsync_WithMalformedTraceparent_RestartsW3CTraceAndLogsValidTraceId()
    {
        string logDir = Path.Combine(_testTempDir, "logs_malformed_tracing");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        // Malformed traceparent: invalid non-hex characters and invalid length
        string malformedTraceparent = "00-invalid_non_hex_trace_id-bad_span-01";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":32,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_db_fixture\",\"arguments\":{{\"action\":\"status\"}},\"_meta\":{{\"traceparent\":\"{malformedTraceparent}\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string[] ndjsonLines = File.ReadAllLines(ndjsonFiles[0]);
        ndjsonLines.Should().NotBeEmpty();

        // Must contain a valid restarted 32-hex traceId (W3C restart per spec)
        using var ndDoc = JsonDocument.Parse(ndjsonLines[0]);
        string? restartedTraceId = ndDoc.RootElement.GetProperty("traceId").GetString();
        restartedTraceId.Should().NotBeNullOrWhiteSpace();
        restartedTraceId!.Length.Should().Be(32);
        restartedTraceId.Should().MatchRegex("^[0-9a-f]{32}$");
        restartedTraceId.Should().NotContain("invalid");
    }

    #endregion

    #region Adversarial & Boundary Stress Tests

    [Fact]
    public async Task ResourcesSubscribe_Concurrent50TasksThrashing_MaintainsIntegrityWithoutExceptions()
    {
        using McpServer server = new();

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            string uri = (i % 2 == 0) ? "kongor://client/console" : "kongor://servers/{port}/logs";
            string subJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{100 + i},\"method\":\"resources/subscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}";
            string unSubJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{200 + i},\"method\":\"resources/unsubscribe\",\"params\":{{\"uri\":\"{uri}\"}}}}";

            for (int r = 0; r < 5; r++)
            {
                await server.ProcessMessageAsync(subJson);
                server.IsSubscribed(uri).Should().BeTrue();
                await server.ProcessMessageAsync(unSubJson);
            }
        });

        await Task.WhenAll(tasks);
        server.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyResourceUpdatedAsync_BothTemplateAndConcreteSubscribed_NotifiesBothIndependently()
    {
        using McpServer server = new();

        // Subscribe to both template and concrete URI
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":40,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/{port}/logs\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/11235/logs\"}}");
        server.ClearSentNotifications();

        // Notify for concrete server 11235
        await server.NotifyResourceUpdatedAsync("kongor://servers/11235/logs");

        // Both subscriptions must receive notifications without suppression
        server.SentNotifications.Count.Should().Be(2);
        server.SentNotifications.Should().Contain(n => n.Contains("kongor://servers/11235/logs"));
        server.SentNotifications.Should().Contain(n => n.Contains("kongor://servers/{port}/logs"));
    }

    [Fact]
    public async Task ResourcesRead_ReplayMatchId_ReturnsStructuredJsonInsteadOfError()
    {
        using McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://replays/12345/summary\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(responseJson!);
        doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("result", out var result).Should().BeTrue();

        var contents = result.GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        var first = contents[0];
        first.GetProperty("uri").GetString().Should().Be("kongor://replays/12345/summary");
        first.GetProperty("mimeType").GetString().Should().Be("application/json");

        string text = first.GetProperty("text").GetString()!;
        using var textDoc = JsonDocument.Parse(text);
        textDoc.RootElement.GetProperty("matchId").GetString().Should().Be("12345");
    }

    [Fact]
    public async Task ToolCall_KongorEngineCachePurge_WithRootMetaProgressToken_EmitsMultiStageProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_engine_cache_purge\",\"arguments\":{\"target\":\"all\"},\"_meta\":{\"progressToken\":\"root-token-999\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("root-token-999"));
    }

    [Fact]
    public async Task ToolCall_KongorGameServerManage_OnLaunchFailure_EmitsTerminalProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        // Call launch with nonexistent server path
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":44,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_server_manage\",\"arguments\":{\"action\":\"launch\",\"customServerPath\":\"Z:\\\\nonexistent\\\\hon_x64.exe\"},\"_meta\":{\"progressToken\":\"srv-fail-tok\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        // Must contain initial progress and final terminal progress (1.0) on failure
        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("srv-fail-tok"));
        progressNotifs.Should().Contain(n => n.Contains("\"progress\":1") || n.Contains("\"progress\": 1"));

        // Must accurately reflect the failed launch in result
        responseJson.Should().Contain("errorMessage");
        responseJson.Should().Contain("running");
        responseJson.Should().Contain("error");
    }

    [Fact]
    public async Task ToolCall_WithCamelCaseTraceParent_PropagatesTraceId()
    {
        string logDir = Path.Combine(_testTempDir, "logs_camel_tracing");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        string testTraceId = "abcdef0123456789abcdef0123456789";
        string testSpanId = "fedcba9876543210";
        string traceparent = $"00-{testTraceId}-{testSpanId}-01";

        // Pass traceParent in camelCase inside _meta
        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":50,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_db_fixture\",\"arguments\":{{\"action\":\"status\"}},\"_meta\":{{\"traceParent\":\"{traceparent}\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string ndjsonContent = File.ReadAllText(ndjsonFiles[0]);
        ndjsonContent.Should().Contain(testTraceId);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ToolCall_WithSnakeCaseProgressToken_EmitsMultiStageProgress()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":51,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_engine_cache_purge\",\"arguments\":{\"action\":\"texture\"},\"_meta\":{\"progress_token\":\"tok-snake-99\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("tok-snake-99"));
    }

    [Fact]
    public async Task ToolCall_WithDecimalProgressToken_EmitsMultiStageProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":52,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_engine_cache_purge\",\"arguments\":{\"target\":\"all\"},\"_meta\":{\"progressToken\":100.5}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("100.5"));
    }

    [Fact]
    public async Task ToolCall_WithTracestate_PreservesTraceContext()
    {
        string logDir = Path.Combine(_testTempDir, "logs_tracestate");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        string testTraceId = "55555555555555555555555555555555";
        string testSpanId = "6666666666666666";
        string traceparent = $"00-{testTraceId}-{testSpanId}-01";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":53,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_db_fixture\",\"arguments\":{{\"action\":\"status\"}},\"_meta\":{{\"traceparent\":\"{traceparent}\",\"tracestate\":\"kongor=phase4\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string ndjsonContent = File.ReadAllText(ndjsonFiles[0]);
        ndjsonContent.Should().Contain(testTraceId);
    }

    [Fact]
    public async Task ResourcesSubscribe_IpcLogStreamReceived_DispatchesConsoleNotification()
    {
        var mockIpc = new Moq.Mock<IGameIpcBridge>();
        var registry = new McpToolRegistry(registerDefaults: false, gameIpcBridge: mockIpc.Object);
        using var server = new McpServer(toolRegistry: registry);

        // Subscribe to console log stream
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://client/console\"}}");
        server.ClearSentNotifications();

        // Raise log stream received event on IPC bridge
        mockIpc.Raise(m => m.OnLogStreamReceived += null, "[K2 Engine] Game client frame 1000 initialized.");

        server.SentNotifications.Should().ContainSingle();
        server.SentNotifications[0].Should().Contain("notifications/resources/updated");
        server.SentNotifications[0].Should().Contain("kongor://client/console");
    }

    [Fact]
    public async Task ResourcesSubscribe_IpcMessageReceived_DispatchesTelemetryNotification()
    {
        var mockIpc = new Moq.Mock<IGameIpcBridge>();
        var registry = new McpToolRegistry(registerDefaults: false, gameIpcBridge: mockIpc.Object);
        using var server = new McpServer(toolRegistry: registry);

        // Subscribe to client telemetry
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":61,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://client/telemetry\"}}");
        server.ClearSentNotifications();

        // Raise message received event on IPC bridge
        var dummyMsg = Kongor.Cli.Mcp.Services.IpcMessage.CreateString(Kongor.Cli.Mcp.Services.IpcOpCode.EngineStateResponse, "{\"gamePhase\": 2}");
        mockIpc.Raise(m => m.OnMessageReceived += null, dummyMsg);

        server.SentNotifications.Should().ContainSingle();
        server.SentNotifications[0].Should().Contain("notifications/resources/updated");
        server.SentNotifications[0].Should().Contain("kongor://client/telemetry");
    }

    [Fact]
    public async Task ResourcesSubscribe_MasterAndChatServerLogs_ValidUrisAndSubscribable()
    {
        using McpServer server = new();

        var sub1 = await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":62,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://logs/master-server\"}}");
        var sub2 = await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":63,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://logs/chat-server\"}}");

        sub1.Should().NotBeNull();
        sub2.Should().NotBeNull();
        server.IsSubscribed("kongor://logs/master-server").Should().BeTrue();
        server.IsSubscribed("kongor://logs/chat-server").Should().BeTrue();

        server.ClearSentNotifications();
        await server.NotifyResourceUpdatedAsync("kongor://logs/master-server");
        await server.NotifyResourceUpdatedAsync("kongor://logs/chat-server");

        server.SentNotifications.Count.Should().Be(2);
        server.SentNotifications.Should().Contain(n => n.Contains("kongor://logs/master-server"));
        server.SentNotifications.Should().Contain(n => n.Contains("kongor://logs/chat-server"));
    }

    [Fact]
    public async Task ResourcesSubscribe_ReplaysSummary_TemplateAndConcreteNotification()
    {
        using McpServer server = new();

        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":64,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://replays/{matchId}/summary\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":65,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://replays/latest/summary\"}}");

        server.ClearSentNotifications();
        await server.NotifyResourceUpdatedAsync("kongor://replays/9999/summary");

        // Should notify template kongor://replays/{matchId}/summary
        server.SentNotifications.Should().Contain(n => n.Contains("kongor://replays/{matchId}/summary"));
    }

    [Fact]
    public async Task ToolCall_KongorGameServerManage_OnInvalidServer_EmitsTerminalProgress()
    {
        using McpServer server = new();
        server.ClearSentNotifications();

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":66,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_server_manage\",\"arguments\":{\"action\":\"launch\",\"customServerPath\":\"Z:\\\\nonexistent\\\\hon_x64.exe\"},\"_meta\":{\"progressToken\":\"tok-db-err\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        var progressNotifs = server.SentNotifications
            .Where(n => n.Contains("notifications/progress"))
            .ToList();

        progressNotifs.Count.Should().BeGreaterOrEqualTo(2);
        progressNotifs.Should().OnlyContain(n => n.Contains("tok-db-err"));
        progressNotifs.Should().Contain(n => n.Contains("\"progress\":1") || n.Contains("\"progress\": 1"));
    }

    [Fact]
    public async Task ResourcesSubscribe_InvalidParamTypes_ReturnsInvalidParamsError()
    {
        using McpServer server = new();

        string[] invalidPayloads =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"resources/subscribe\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":102,\"method\":\"resources/subscribe\",\"params\":123}",
            "{\"jsonrpc\":\"2.0\",\"id\":103,\"method\":\"resources/subscribe\",\"params\":[\"kongor://client/console\"]}",
            "{\"jsonrpc\":\"2.0\",\"id\":104,\"method\":\"resources/subscribe\",\"params\":{\"uri\": 12345}}",
            "{\"jsonrpc\":\"2.0\",\"id\":105,\"method\":\"resources/subscribe\",\"params\":{\"uri\": true}}",
            "{\"jsonrpc\":\"2.0\",\"id\":106,\"method\":\"resources/subscribe\",\"params\":{\"uri\": {}}}"
        ];

        foreach (string payload in invalidPayloads)
        {
            string? resp = await server.ProcessMessageAsync(payload);
            resp.Should().NotBeNull();
            using var doc = JsonDocument.Parse(resp!);
            doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
            err.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams, because: $"Payload was {payload}");
        }
    }

    [Fact]
    public async Task ResourcesUnsubscribe_InvalidParamTypes_ReturnsInvalidParamsError()
    {
        using McpServer server = new();

        string[] invalidPayloads =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":111,\"method\":\"resources/unsubscribe\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":112,\"method\":\"resources/unsubscribe\",\"params\":456}",
            "{\"jsonrpc\":\"2.0\",\"id\":113,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\": 98765}}"
        ];

        foreach (string payload in invalidPayloads)
        {
            string? resp = await server.ProcessMessageAsync(payload);
            resp.Should().NotBeNull();
            using var doc = JsonDocument.Parse(resp!);
            doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
            err.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
        }
    }

    [Fact]
    public async Task ResourcesRead_InvalidParamTypes_ReturnsInvalidParamsError()
    {
        using McpServer server = new();

        string[] invalidPayloads =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":121,\"method\":\"resources/read\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":122,\"method\":\"resources/read\",\"params\":789}",
            "{\"jsonrpc\":\"2.0\",\"id\":123,\"method\":\"resources/read\",\"params\":{\"uri\": 555}}"
        ];

        foreach (string payload in invalidPayloads)
        {
            string? resp = await server.ProcessMessageAsync(payload);
            resp.Should().NotBeNull();
            using var doc = JsonDocument.Parse(resp!);
            doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
            err.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
        }
    }

    [Fact]
    public async Task ResourcesRead_TemplateUriDirectRead_ReturnsDedicatedServerAndReplaySummary()
    {
        using McpServer server = new();

        // Reading kongor://servers/{port}/logs directly
        string srvResp = await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":131,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://servers/{port}/logs\"}}") ?? "";
        using var srvDoc = JsonDocument.Parse(srvResp);
        srvDoc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        srvDoc.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("uri").GetString().Should().Be("kongor://servers/{port}/logs");

        // Reading kongor://replays/{matchId}/summary directly
        string repResp = await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":132,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://replays/{matchId}/summary\"}}") ?? "";
        using var repDoc = JsonDocument.Parse(repResp);
        repDoc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        repDoc.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("uri").GetString().Should().Be("kongor://replays/{matchId}/summary");
    }

    [Fact]
    public async Task ToolsCall_InvalidNameType_ReturnsInvalidParamsError()
    {
        using McpServer server = new();

        string req = "{\"jsonrpc\":\"2.0\",\"id\":141,\"method\":\"tools/call\",\"params\":{\"name\": 12345, \"arguments\": {}}}";
        string? resp = await server.ProcessMessageAsync(req);

        resp.Should().NotBeNull();
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task PromptsGet_InvalidNameType_ReturnsInvalidParamsError()
    {
        using McpServer server = new();

        string req = "{\"jsonrpc\":\"2.0\",\"id\":142,\"method\":\"prompts/get\",\"params\":{\"name\": 999}}";
        string? resp = await server.ProcessMessageAsync(req);

        resp.Should().NotBeNull();
        using var doc = JsonDocument.Parse(resp!);
        doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetProperty("code").GetInt32().Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ToolCall_WithSnakeCaseTraceState_PreservesTraceContext()
    {
        string logDir = Path.Combine(_testTempDir, "logs_snake_tracestate");
        Directory.CreateDirectory(logDir);

        var logger = new McpConversationLogger(logDir);
        using var server = new McpServer(conversationLogger: logger);

        string testTraceId = "88888888888888888888888888888888";
        string testSpanId = "9999999999999999";
        string traceparent = $"00-{testTraceId}-{testSpanId}-01";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":151,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_db_fixture\",\"arguments\":{{\"action\":\"status\"}},\"_meta\":{{\"traceparent\":\"{traceparent}\",\"trace_state\":\"congo=p4\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        await logger.FlushAsync();
        await logger.DisposeAsync();

        string[] ndjsonFiles = Directory.GetFiles(logDir, "mcp_traffic_*.ndjson");
        ndjsonFiles.Should().NotBeEmpty();

        string ndjsonContent = File.ReadAllText(ndjsonFiles[0]);
        ndjsonContent.Should().Contain(testTraceId);
    }

    [Fact]
    public async Task ResourcesSubscribe_MultipleServerPorts_SharesSingleWatcherAndRefCountsCleanly()
    {
        using McpServer server = new();

        // Subscribe to port 11235 and 11236
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":161,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/11235/logs\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":162,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/11236/logs\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":163,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"kongor://servers/{port}/logs\"}}");

        server.Subscriptions.Count.Should().Be(3);

        // Unsubscribe one port
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":164,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://servers/11235/logs\"}}");
        server.Subscriptions.Count.Should().Be(2);

        // Unsubscribe other ports
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":165,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://servers/11236/logs\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":166,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"kongor://servers/{port}/logs\"}}");
        server.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task SentNotifications_RollingCap_PreventsUnboundedMemoryGrowth()
    {
        using McpServer server = new();

        // Enqueue 1,200 notifications
        for (int i = 0; i < 1200; i++)
        {
            await server.SendNotificationAsync("notifications/test", new { index = i });
        }

        // Must be capped at 1000 items
        server.SentNotifications.Count.Should().BeLessOrEqualTo(1000);
    }

    #endregion
}
