// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TaoClientTests.cs
// System: Project Kongor MCP Tests - TaoClient Unit & Acceptance Verification
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.Contracts.Ipc;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.Tests;

public sealed class TaoClientTests
{
    private readonly ITestOutputHelper _output;

    public TaoClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TaoClient_DispatchAsync_WithFallbackBridge_SerializesAndParsesSuccess()
    {
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(b => b.IsConnected).Returns(true);
        mockBridge.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"status\":\"success\",\"data\":{\"activeInterface\":\"main\",\"widgetCount\":42}}");

        TaoClient client = new(mailboxBridge: null, fallbackBridge: mockBridge.Object, logger: msg => _output.WriteLine(msg));

        TaoResponse<JsonElement> resp = await client.DispatchAsync("inspect_ui", new { maxDepth = 4 });

        resp.Success.Should().BeTrue();
        resp.Action.Should().Be("inspect_ui");
        resp.Data.GetProperty("activeInterface").GetString().Should().Be("main");
        resp.Data.GetProperty("widgetCount").GetInt32().Should().Be(42);
        resp.ElapsedMs.Should().BeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public async Task TaoClient_DispatchAsync_WithErrorResult_ReturnsFailure()
    {
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(b => b.IsConnected).Returns(true);
        mockBridge.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"status\":\"error\",\"error\":\"Interface 'unknown_iface' not found\",\"traceback\":\"stack traceback:\\nline 1\"}");

        TaoClient client = new(mailboxBridge: null, fallbackBridge: mockBridge.Object, logger: msg => _output.WriteLine(msg));

        TaoResponse<JsonElement> resp = await client.DispatchAsync("inspect_ui", new { @interface = "unknown_iface" });

        resp.Success.Should().BeFalse();
        resp.Error.Should().Contain("Interface 'unknown_iface' not found");
        resp.Traceback.Should().Contain("stack traceback");
    }

    [Fact]
    public async Task TaoClient_GetCapabilitiesAsync_ReturnsKnownHandlersAndSha256()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        TaoClient client = new(mailboxBridge: null, fallbackBridge: null, logger: msg => _output.WriteLine(msg));

        TaoCapabilitiesDto caps = await client.GetCapabilitiesAsync();

        caps.Should().NotBeNull();
        caps.Handlers.Should().Contain("inspect_ui");
        caps.Handlers.Should().Contain("navigate");
        caps.Handlers.Should().Contain("apply_fixture");
        caps.Handlers.Should().Contain("set_timescale");
        caps.TaoLuaSha256.Should().NotBeNullOrWhiteSpace();
        caps.TaoLuaSha256.Should().NotBe("sha256:unknown");
    }

    [Fact]
    public async Task TaoCapabilitiesHandler_HandleAsync_ReturnsToolCallResult()
    {
        TaoClient client = new(mailboxBridge: null, fallbackBridge: null, logger: msg => _output.WriteLine(msg));
        TaoCapabilitiesHandler handler = new(client);

        JsonDocument emptyArgs = JsonDocument.Parse("{}");
        ToolCallResult result = await handler.HandleAsync(emptyArgs.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        string json = ((TextContentItem)result.Content[0]).Text;
        json.Should().Contain("inspect_ui");
        json.Should().Contain("taoLuaSha256");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LiveTaoClient_DispatchAsync_InspectUi_LiveClientRoundTrip()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            _output.WriteLine("SKIPPED: Gated behind KONGOR_RUN_MANUAL_TESTS.");
            return;
        }

        Process[] procs = Process.GetProcessesByName("hon_x64");
        if (!procs.Any(p => !p.HasExited))
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        NexusMailboxIpcBridge mailboxBridge = new(
            mailboxDir: null,
            logger: msg => _output.WriteLine(msg),
            timeProvider: null,
            authKey: null,
            signalBridge: null)
        {
            ForceConnected = true
        };

        TaoClient client = new(mailboxBridge: mailboxBridge, fallbackBridge: null, logger: msg => _output.WriteLine(msg));

        TaoResponse<JsonElement> resp = await client.DispatchAsync("inspect_ui", new
        {
            @interface = "main",
            maxDepth = 3,
            includeHidden = false
        }, timeoutMs: 5000);

        _output.WriteLine($"[TAO] inspect_ui success: {resp.Success}, elapsed: {resp.ElapsedMs:F2}ms, error: {resp.Error}");

        resp.Success.Should().BeTrue("TAO inspect_ui must execute successfully through live mailbox");
        resp.ElapsedMs.Should().BeGreaterThan(0.0);
        resp.ElapsedMs.Should().BeLessThan(2000.0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LiveTaoClient_GetCapabilities_LiveClientReturnsLiveHandlers()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            _output.WriteLine("SKIPPED: Gated behind KONGOR_RUN_MANUAL_TESTS.");
            return;
        }

        Process[] procs = Process.GetProcessesByName("hon_x64");
        if (!procs.Any(p => !p.HasExited))
        {
            _output.WriteLine("SKIPPED: Live game client hon_x64 is not running.");
            return;
        }

        NexusMailboxIpcBridge mailboxBridge = new(
            mailboxDir: null,
            logger: msg => _output.WriteLine(msg),
            timeProvider: null,
            authKey: null,
            signalBridge: null)
        {
            ForceConnected = true
        };

        TaoClient client = new(mailboxBridge: mailboxBridge, fallbackBridge: null, logger: msg => _output.WriteLine(msg));

        TaoCapabilitiesDto caps = await client.GetCapabilitiesAsync(timeoutMs: 5000);

        _output.WriteLine($"[TAO] Capabilities retrieved. Version: {caps.Version}, Latency: {caps.RoundTripLatencyMs:F2}ms, Handlers: {caps.Handlers.Count}");

        caps.Version.Should().NotBe("offline");
        caps.Handlers.Should().Contain("inspect_ui");
        caps.Handlers.Should().Contain("get_capabilities");
        caps.TaoLuaSha256.Should().NotBeNullOrWhiteSpace();
        caps.HashedDiskPath.Should().NotBeNullOrWhiteSpace();
        _output.WriteLine($"[TAO] EngineBuild: {caps.EngineBuild}, DiskHash: {caps.TaoLuaSha256}, Path: {caps.HashedDiskPath}, IsStale: {caps.IsStale}");
    }

    [Fact]
    public async Task TaoClient_GetCapabilities_Offline_PopulatesHashAndHashedPath()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        TaoClient client = new(mailboxBridge: null, fallbackBridge: null, logger: msg => _output.WriteLine(msg));

        TaoCapabilitiesDto caps = await client.GetCapabilitiesAsync(timeoutMs: 500);

        caps.Version.Should().Be("offline");
        caps.EngineBuild.Should().Be("offline");
        caps.TaoLuaSha256.Should().NotBeNullOrWhiteSpace();
        caps.HashedDiskPath.Should().NotBe("unknown");
        caps.IsStale.Should().BeNull();
    }

    [Fact]
    public void McpToolRegistry_TaoCapabilities_ToolAndAliasesRegistered()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_tao_capabilities").Should().BeTrue();
        registry.HasTool("kongor_tao_caps").Should().BeTrue();
        registry.HasTool("tao_capabilities").Should().BeTrue();

        ToolDefinition? def = registry.GetTool("kongor_tao_capabilities");
        def.Should().NotBeNull();
        def!.Name.Should().Be("kongor_tao_capabilities");
        def.Description.Should().Contain("TAO");
        def.InputSchema.Should().NotBeNull();
    }

    [Fact]
    public async Task McpToolRegistry_TaoCapabilities_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        McpToolRegistry registry = new(registerDefaults: true);

        using var argsDoc = JsonDocument.Parse("{\"timeoutMs\": 500}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_tao_capabilities", argsDoc.RootElement);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        using var respDoc = JsonDocument.Parse(jsonText);
        respDoc.RootElement.TryGetProperty("taoLuaSha256", out JsonElement shaEl).Should().BeTrue();
        shaEl.GetString().Should().NotBeNullOrWhiteSpace();
        respDoc.RootElement.TryGetProperty("hashedDiskPath", out JsonElement pathEl).Should().BeTrue();
        pathEl.GetString().Should().NotBe("unknown");
    }
}
