// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Value-Weighted Testing Gate for MCP v2 Core Toolset Allowlist, Dynamic Toolset Expansion,
/// and OfficialMcpHttpHost Security Guardrails:
/// - protects: 18-tool "core" allowlist integrity (~82% token savings), dynamic runtime toolset expansion
///   via kongor_enable_toolset with list_changed notifications, and OfficialMcpHttpHost DNS-rebinding,
///   cross-origin, and constant-time Bearer token authentication firewalls.
/// - fails_when: Core allowlist drifts from the 18 canonical tools, kongor_enable_toolset fails to expose tools
///   or fire notifications/tools/list_changed, or OfficialMcpHttpHost admits malicious Host/Origin headers
///   or unauthenticated /mcp requests.
/// - seam: Real McpToolRegistry, real McpServer JSON-RPC pump, and real Kestrel HTTP pipeline with HttpClient.
/// </summary>
[Collection("McpHttpAuthEnvCollection")]
public sealed class McpV2CoreAndHttpSecurityTests
{
    private static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void CoreToolset_ReturnsExact18AllowlistedTools_AndCutsToolCountByAtLeast75Percent()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        IReadOnlyList<ToolDefinition> coreTools = registry.ListTools(toolsets: "core");
        IReadOnlyList<ToolDefinition> allTools = registry.ListTools(toolsets: "all");

        coreTools.Count.Should().Be(18, "core toolset must expose exactly the 18 allowlisted tools");

        string[] expected18 =
        [
            "kongor_game_client_manage",
            "kongor_client_eval_lua",
            "kongor_game_command",
            "kongor_client_practice",
            "kongor_reload_and_verify",
            "kongor_errors_since",
            "kongor_capture_window",
            "kongor_ui_control",
            "kongor_client_navigate",
            "kongor_k2_ui_inspect",
            "kongor_wait_for",
            "kongor_vfs_resolve",
            "kongor_vfs_locate",
            "kongor_sync_mirrors",
            "kongor_server_manage",
            "kongor_game_server_command",
            "kongor_health_check",
            "kongor_call_tool"
        ];

        var coreNames = coreTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string toolName in expected18)
        {
            coreNames.Should().Contain(toolName, $"core toolset must contain '{toolName}'");
        }

        allTools.Count.Should().BeGreaterThan(50, "default registry has comprehensive suite of tools");
        double reductionFraction = 1.0 - ((double)coreTools.Count / allTools.Count);
        reductionFraction.Should().BeGreaterThanOrEqualTo(0.75, "core toolset must reduce advertised tools by >= 75% vs 'all'");
    }

    [Fact]
    public async Task EnableToolset_DynamicallyEnablesDomains_UpdatesListTools_AndFiresEvent()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.SetDefaultToolsets("core");

        registry.ListTools().Count.Should().Be(18, "defaults to 18 core tools when core is configured");

        bool eventFired = false;
        registry.ToolListChanged += () => eventFired = true;

        using JsonDocument argsDoc = JsonDocument.Parse("{\"domain\":\"ui\"}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_enable_toolset", argsDoc.RootElement);

        result.IsError.Should().BeFalse();
        eventFired.Should().BeTrue("ToolListChanged must be triggered when toolset is enabled");

        IReadOnlyList<ToolDefinition> afterUiTools = registry.ListTools();
        afterUiTools.Count.Should().BeGreaterThan(18, "enabling UI toolset expands visible tools");
        afterUiTools.Should().Contain(t => t.Name == "kongor_ui_render_view" || t.Name == "kongor_ui_flow");

        // Enable all tools
        using JsonDocument allArgsDoc = JsonDocument.Parse("{\"domain\":\"all\"}");
        ToolCallResult resultAll = await registry.ExecuteToolAsync("kongor_enable_toolset", allArgsDoc.RootElement);

        resultAll.IsError.Should().BeFalse();
        IReadOnlyList<ToolDefinition> afterAllTools = registry.ListTools();
        afterAllTools.Count.Should().Be(registry.ListTools(toolsets: "all").Count, "enabling 'all' exposes all registered tools");
    }

    [Fact]
    public void McpServer_EmitsToolsListChangedNotification_WhenToolsetsEnabled()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.SetDefaultToolsets("core");

        using McpServer server = new(registry, TextWriter.Null);
        server.ClearSentNotifications();

        registry.EnableToolsets(["ui"]);

        server.SentNotifications.Should().Contain(n => n.Contains("notifications/tools/list_changed"),
            "McpServer must emit notifications/tools/list_changed when toolsets are enabled");
    }

    [Fact]
    public async Task OfficialMcpHttpHost_SecurityMiddleware_BlocksNonLoopbackHost_BlocksCrossOrigin_AndEnforcesBearerAuth()
    {
        int port = GetAvailablePort();
        string testToken = "synthetic-auth-token-" + Guid.NewGuid().ToString("N")[..12];

        McpToolRegistry registry = new(registerDefaults: true);
        using McpServer server = new(registry, TextWriter.Null);

        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(
            server,
            "127.0.0.1",
            port,
            requiredBearerToken: testToken,
            requireAuth: true,
            allowRemoteHttp: false);

        await app.StartAsync();

        try
        {
            using SocketsHttpHandler handler = new();
            using HttpClient client = new(handler);

            // 1. Non-loopback Host header -> 403 Forbidden
            using HttpRequestMessage hostReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            hostReq.Headers.Host = "evil.com";
            using HttpResponseMessage hostResp = await client.SendAsync(hostReq);
            hostResp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Host evil.com must be blocked by DNS-rebinding guard");

            // 2. Cross-origin Origin header -> 403 Forbidden
            using HttpRequestMessage originReq = new(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            originReq.Headers.Add("Origin", "https://attacker.invalid");
            using HttpResponseMessage originResp = await client.SendAsync(originReq);
            originResp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Cross-origin Origin header must be blocked");

            // 3. /mcp request with missing Bearer token -> 401 Unauthorized
            using HttpRequestMessage mcpNoAuthReq = new(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
            using HttpResponseMessage mcpNoAuthResp = await client.SendAsync(mcpNoAuthReq);
            mcpNoAuthResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "Missing Bearer token on /mcp must return 401");

            // 4. /mcp request with wrong Bearer token -> 401 Unauthorized
            using HttpRequestMessage mcpWrongAuthReq = new(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
            mcpWrongAuthReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token");
            using HttpResponseMessage mcpWrongAuthResp = await client.SendAsync(mcpWrongAuthReq);
            mcpWrongAuthResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "Invalid Bearer token on /mcp must return 401");

            // 5. /mcp request with valid Bearer token -> Accepted (not 401/403)
            using HttpRequestMessage mcpValidAuthReq = new(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
            mcpValidAuthReq.Headers.Add("Authorization", $"Bearer {testToken}");
            mcpValidAuthReq.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage mcpValidAuthResp = await client.SendAsync(mcpValidAuthReq);
            mcpValidAuthResp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            mcpValidAuthResp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

            // 6. /healthz from loopback without bearer token -> 200 OK
            using HttpResponseMessage healthResp = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            healthResp.StatusCode.Should().Be(HttpStatusCode.OK, "/healthz must remain accessible on loopback without bearer token");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CallToolMetaBridge_SupportsDiscovery_DualArgParsing_BlocksRecursion_EnforcesReadOnly_AndSingleBillsInnerTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.SetDefaultToolsets("core");

        // 1. Discovery ('search') returns matching non-core tools without mutating global toolset visibility
        using JsonDocument searchArgs = JsonDocument.Parse("{\"action\":\"search\",\"query\":\"k2_lint\"}");
        ToolCallResult searchRes = await registry.ExecuteToolAsync("kongor_call_tool", searchArgs.RootElement);
        searchRes.IsError.Should().BeFalse();
        searchRes.FirstText.Should().Contain("kongor_k2_lint");
        registry.ListTools().Count.Should().Be(18, "stateless discovery must not mutate process-wide visible tools");

        // 2. Describe ('describe') returns complete inputSchema & exampleBridgeCall
        using JsonDocument descArgs = JsonDocument.Parse("{\"action\":\"describe\",\"tool_name\":\"kongor_rate_limit_status\"}");
        ToolCallResult descRes = await registry.ExecuteToolAsync("kongor_call_tool", descArgs.RootElement);
        descRes.IsError.Should().BeFalse();
        descRes.FirstText.Should().Contain("exampleBridgeCall");

        // 3. Bridged call via arguments_json string AND single-billing verification (SLO 1 protection) on a clean registry
        McpToolRegistry billingRegistry = new(registerDefaults: true);
        using JsonDocument callJsonStrArgs = JsonDocument.Parse(
            "{\"action\":\"call\",\"tool_name\":\"kongor_rate_limit_status\",\"arguments_json\":\"{}\"}");
        ToolCallResult bridgedRes = await billingRegistry.ExecuteToolAsync("kongor_call_tool", callJsonStrArgs.RootElement);
        bridgedRes.IsError.Should().BeFalse();

        FirstCallScoreboardSnapshot after = billingRegistry.GetFirstCallScoreboard();
        after.FirstCallOutcomesByTool.Should().ContainKey("kongor_rate_limit_status");
        after.FirstCallOutcomesByTool.Should().NotContainKey("kongor_call_tool",
            "bridged action='call' must attribute outcome solely to the inner tool and never double-count kongor_call_tool");
        after.TotalToolCalls.Should().Be(1,
            "1 bridged call must record exactly 1 outcome (the inner tool) with zero outer bridge double-billing");

        // 4. Postel's Law fallback: raw JSON object 'arguments' also succeeds
        using JsonDocument callObjArgs = JsonDocument.Parse(
            "{\"tool_name\":\"kongor_rate_limit_status\",\"arguments\":{}}");
        ToolCallResult objBridgedRes = await registry.ExecuteToolAsync("kongor_call_tool", callObjArgs.RootElement);
        objBridgedRes.IsError.Should().BeFalse();

        // 5. Recursion guard blocks direct and aliased self-invocation
        using JsonDocument recurseArgs = JsonDocument.Parse(
            "{\"action\":\"call\",\"tool_name\":\"kongor_call_tool\",\"arguments_json\":\"{}\"}");
        ToolCallResult recurseRes = await registry.ExecuteToolAsync("kongor_call_tool", recurseArgs.RootElement);
        recurseRes.IsError.Should().BeTrue();
        recurseRes.FirstText.Should().Contain("Recursion blocked");

        using JsonDocument aliasRecurseArgs = JsonDocument.Parse(
            "{\"action\":\"call\",\"tool_name\":\"kongor_bridge\",\"arguments_json\":\"{}\"}");
        ToolCallResult aliasRecurseRes = await registry.ExecuteToolAsync("kongor_call_tool", aliasRecurseArgs.RootElement);
        aliasRecurseRes.IsError.Should().BeTrue();

        // 6. Read-only mode blocks mutating inner tools through kongor_call_tool while allowing read-only inner tools
        using McpServer roServer = new(registry, TextWriter.Null, readOnlyMode: true);
        string mutatingReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 701,
            method = "tools/call",
            @params = new
            {
                name = "kongor_call_tool",
                arguments = new
                {
                    action = "call",
                    tool_name = "kongor_db_migrate",
                    arguments_json = "{\"action\":\"apply\"}"
                }
            }
        });
        string? mutatingResp = await roServer.ProcessMessageAsync(mutatingReq);
        mutatingResp.Should().NotBeNull();
        mutatingResp!.Should().Contain("--read-only");

        string readOnlyReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 702,
            method = "tools/call",
            @params = new
            {
                name = "kongor_call_tool",
                arguments = new
                {
                    action = "call",
                    tool_name = "kongor_rate_limit_status",
                    arguments_json = "{}"
                }
            }
        });
        string? readOnlyResp = await roServer.ProcessMessageAsync(readOnlyReq);
        readOnlyResp.Should().NotBeNull();
        readOnlyResp!.Should().NotContain("--read-only");
    }
}
