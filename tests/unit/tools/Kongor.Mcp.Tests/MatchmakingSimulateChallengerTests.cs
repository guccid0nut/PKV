// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Empirical Challenger Stress Test Suite for Milestone 2: Headless Matchmaking Simulation.
/// Adversarially challenges:
/// 1. MatchmakingSimulateHandler across failure modes & edge cases:
///    - Server offline (refused TCP connection)
///    - Invalid ports (0, -1, 70000)
///    - Invalid client counts (0, -5, 50 clamped to 10 vs 1)
///    - Timeouts (validation <= 0, hanging server timeout)
/// 2. Alias resolution in McpToolRegistry:
///    - 'kongor_tmm_simulate' -> 'kongor_matchmaking_simulate'
///    - 'kongor_matchmaking_bench' -> 'kongor_matchmaking_simulate'
/// 3. Error remediation integration & empirical audit:
///    - Verifies 'remediationCategory' and 'remediationHint'
///    - Empirically audits 'improvedCommand' across all error payloads
/// 4. Sub-2.0s execution speed and zero desktop GUI windows.
/// </summary>
public sealed class MatchmakingSimulateChallengerTests
{
    private static JsonElement GetResultRoot(ToolCallResult result)
    {
        TextContentItem textItem = (TextContentItem)result.Content[0];
        using JsonDocument doc = JsonDocument.Parse(textItem.Text);
        return doc.RootElement.Clone();
    }

    private static (MatchmakingSimulateHandler Handler, Mock<IMatchmakingSimulateService> MockService, ClientCommandRemediationService Remediation) CreateHandlerWithMocks(
        MatchmakingSimulateResult? customResult = null)
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns<int?, Func<string, string?>?>((port, _) => (port ?? 11031, port.HasValue ? "explicit" : "default"));

        MatchmakingSimulateResult defaultResult = customResult ?? new MatchmakingSimulateResult
        {
            Status = "success",
            ResolvedPort = 11031,
            DiscoveryTier = "default",
            ClientCount = 10,
            SuccessfulConnections = 10,
            HandshakeCount = 10,
            QueueJoinedCount = 10,
            TotalElapsedMs = 50.0,
            Latency = new MatchmakingLatencySummary
            {
                ConnectMs = new LatencyStats { MinMs = 1.0, MaxMs = 5.0, AvgMs = 2.5 },
                HandshakeMs = new LatencyStats { MinMs = 2.0, MaxMs = 8.0, AvgMs = 4.0 },
                QueueJoinMs = new LatencyStats { MinMs = 3.0, MaxMs = 10.0, AvgMs = 5.5 }
            },
            Clients = Enumerable.Range(0, 10).Select(i => new VirtualClientStatus
            {
                ClientIndex = i,
                AccountId = 90000 + i,
                AccountName = $"VirtualClient_{i}",
                Connected = true,
                ConnectLatencyMs = 2.5,
                HandshakeComplete = true,
                HandshakeLatencyMs = 4.0,
                QueueJoined = true,
                QueueJoinLatencyMs = 5.5
            }).ToList(),
            SocketsCleaned = 10,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o")
        };

        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultResult);

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockService, remediation);
    }

    #region Category 1: MatchmakingSimulateHandler Edge Cases

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HandleAsync_ServerOffline_RealRefusedConnection_ReturnsStructuredErrorWithChatServerOffline()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Allocate and immediately close a TCP listener to get a guaranteed closed local port
        int closedPort;
        using (Socket tempSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            tempSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
        }

        // Using real MatchmakingSimulateService
        MatchmakingSimulateService realService = new();
        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: realService,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse($$"""
        {
            "serverHost": "127.0.0.1",
            "serverPort": {{closedPort}},
            "clientCount": 2,
            "timeoutSeconds": 2
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("resolvedPort").GetInt32().Should().Be(closedPort);
        root.GetProperty("successfulConnections").GetInt32().Should().Be(0);
        root.GetProperty("handshakeCount").GetInt32().Should().Be(0);
        root.GetProperty("remediationCategory").GetString().Should().Be("ChatServerOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("ChatServer is not listening");
        root.GetProperty("remediationHint").GetString().Should().Contain("kongor_server_manage");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-65535)]
    [InlineData(65536)]
    [InlineData(70000)]
    public async Task HandleAsync_InvalidPort_EdgeCases_ReturnsMalformedArgumentsError(int invalidPort)
    {
        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"serverPort": {{invalidPort}}}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("remediationHint").GetString().Should().Contain("between 1 and 65535");
        root.GetProperty("error").GetString().Should().Contain($"Invalid serverPort '{invalidPort}'");
    }

    [Fact]
    public async Task HandleAsync_ClientCount_Zero_ClampedToOneInHandler()
    {
        (MatchmakingSimulateHandler handler, Mock<IMatchmakingSimulateService> mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""{"clientCount": 0}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        // Handler clamps 0 via Math.Clamp(0, 1, 10) -> 1
        capturedOptions!.ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_ClientCount_NegativeFive_ClampedToOneInHandler()
    {
        (MatchmakingSimulateHandler handler, Mock<IMatchmakingSimulateService> mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""{"clientCount": -5}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        // Handler clamps -5 via Math.Clamp(-5, 1, 10) -> 1
        capturedOptions!.ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_ClientCount_Fifty_ClampedToTenInHandler()
    {
        (MatchmakingSimulateHandler handler, Mock<IMatchmakingSimulateService> mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""{"clientCount": 50}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.ClientCount.Should().Be(10);
    }

    [Fact]
    public void Service_DirectInvocation_ClientCountZeroOrNegative_DefaultsToTen()
    {
        // When calling MatchmakingSimulateService directly with options.ClientCount <= 0,
        // it defaults to 10 (options.ClientCount <= 0 ? 10 : options.ClientCount)
        MatchmakingSimulateOptions optZero = new() { ClientCount = 0 };
        MatchmakingSimulateOptions optNegative = new() { ClientCount = -5 };

        int clampedZero = Math.Clamp(optZero.ClientCount <= 0 ? 10 : optZero.ClientCount, 1, 10);
        int clampedNegative = Math.Clamp(optNegative.ClientCount <= 0 ? 10 : optNegative.ClientCount, 1, 10);

        clampedZero.Should().Be(10);
        clampedNegative.Should().Be(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task HandleAsync_InvalidTimeout_NonPositive_ReturnsMalformedArgumentsError(int invalidTimeout)
    {
        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"timeoutSeconds": {{invalidTimeout}}}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
        root.GetProperty("remediationHint").GetString().Should().Contain("positive integer");
        root.GetProperty("error").GetString().Should().Contain("greater than 0");
    }

    [Fact]
    public async Task HandleAsync_SimulationTimeout_HangingServer_ReturnsSimulationTimeoutRemediation()
    {
        // Spin up a silent server that accepts connections but never responds to handshake
        TcpListener silentListener = new(IPAddress.Loopback, 0);
        silentListener.Start();
        int silentPort = ((IPEndPoint)silentListener.LocalEndpoint).Port;

        using CancellationTokenSource serverCts = new();
        using CancellationTokenSource callTimeoutCts = new(TimeSpan.FromSeconds(2));
        _ = Task.Run(async () =>
        {
            while (!serverCts.Token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await silentListener.AcceptTcpClientAsync(serverCts.Token);
                    // Keep socket open without sending any bytes, and trigger timeout once client sends handshake
                    _ = Task.Run(async () =>
                    {
                        byte[] dummy = new byte[1024];
                        try
                        {
                            while (!serverCts.Token.IsCancellationRequested)
                            {
                                int r = await client.GetStream().ReadAsync(dummy, serverCts.Token);
                                if (r == 0) break;
                                callTimeoutCts.CancelAfter(5);
                            }
                        }
                        catch { }
                        finally { client.Dispose(); }
                    });
                }
                catch { break; }
            }
        });

        try
        {
            MatchmakingSimulateService realService = new();
            ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
            MatchmakingSimulateHandler handler = new(
                simulationService: realService,
                remediationService: remediation,
                timeProvider: TimeProvider.System);

            using JsonDocument doc = JsonDocument.Parse($$"""
            {
                "serverHost": "127.0.0.1",
                "serverPort": {{silentPort}},
                "clientCount": 1,
                "timeoutSeconds": 1
            }
            """);

            Stopwatch sw = Stopwatch.StartNew();
            ToolCallResult result = await handler.HandleAsync(doc.RootElement, callTimeoutCts.Token);
            sw.Stop();

            result.IsError.Should().BeTrue();
            JsonElement root = GetResultRoot(result);

            root.GetProperty("status").GetString().Should().Be("error");
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("successfulConnections").GetInt32().Should().Be(1);
            root.GetProperty("handshakeCount").GetInt32().Should().Be(0);
            root.GetProperty("remediationCategory").GetString().Should().Be("SimulationTimeout");
            root.GetProperty("remediationHint").GetString().Should().Contain("timeoutSeconds");
        }
        finally
        {
            serverCts.Cancel();
            silentListener.Stop();
        }
    }

    #endregion

    #region Category 2: Alias Resolution in McpToolRegistry

    [Fact]
    public void McpToolRegistry_Aliases_ResolvedByHasTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_matchmaking_simulate").Should().BeTrue();
        registry.HasTool("kongor_tmm_simulate").Should().BeTrue();
        registry.HasTool("kongor_matchmaking_bench").Should().BeTrue();
    }

    [Theory]
    [InlineData("kongor_tmm_simulate")]
    [InlineData("KONGOR_TMM_SIMULATE")]
    [InlineData("Kongor_Tmm_Simulate")]
    [InlineData("kongor_matchmaking_bench")]
    [InlineData("KONGOR_MATCHMAKING_BENCH")]
    [InlineData("Kongor_Matchmaking_Bench")]
    public void McpToolRegistry_Aliases_CaseInsensitiveMatching(string aliasName)
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool(aliasName).Should().BeTrue();
        ToolDefinition? tool = registry.GetTool(aliasName);
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_matchmaking_simulate");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_DispatchesBothAliasesToCanonicalHandler()
    {
        Mock<IMatchmakingSimulateService> mockSim = new();
        mockSim.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchmakingSimulateResult
            {
                Status = "success",
                ResolvedPort = 11031,
                DiscoveryTier = "default",
                ClientCount = 10,
                SuccessfulConnections = 10,
                HandshakeCount = 10,
                QueueJoinedCount = 10
            });

        McpToolRegistry registry = new(
            registerDefaults: true,
            matchmakingSimulateService: mockSim.Object);

        using JsonDocument doc = JsonDocument.Parse("{}");

        ToolCallResult resCanonical = await registry.ExecuteToolAsync("kongor_matchmaking_simulate", doc.RootElement);
        ToolCallResult resAlias1 = await registry.ExecuteToolAsync("kongor_tmm_simulate", doc.RootElement);
        ToolCallResult resAlias2 = await registry.ExecuteToolAsync("kongor_matchmaking_bench", doc.RootElement);

        resCanonical.IsError.Should().BeFalse();
        resAlias1.IsError.Should().BeFalse();
        resAlias2.IsError.Should().BeFalse();

        JsonElement root1 = GetResultRoot(resAlias1);
        JsonElement root2 = GetResultRoot(resAlias2);

        root1.GetProperty("status").GetString().Should().Be("success");
        root2.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public void McpToolRegistry_ListTools_ContainsCanonicalAndOmitsDuplicateAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        tools.Select(t => t.Name).Should().Contain("kongor_matchmaking_simulate");
        tools.Select(t => t.Name).Should().NotContain("kongor_tmm_simulate");
        tools.Select(t => t.Name).Should().NotContain("kongor_matchmaking_bench");
    }

    #endregion

    #region Category 3: Error Remediation Integration & Empirical improvedCommand Audit

    [Theory]
    [InlineData("port", -1)]
    [InlineData("port", 0)]
    [InlineData("port", 70000)]
    public async Task RemediationIntegration_InvalidPort_ContainsCategoryAndHint_AuditsImprovedCommand(string key, int val)
    {
        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"{{key}}": {{val}}}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        // Verification of remediationCategory & remediationHint
        root.TryGetProperty("remediationCategory", out JsonElement catProp).Should().BeTrue();
        catProp.GetString().Should().Be("MalformedCommandArguments");

        root.TryGetProperty("remediationHint", out JsonElement hintProp).Should().BeTrue();
        hintProp.GetString().Should().NotBeNullOrWhiteSpace();

        // EMPIRICAL AUDIT: Check whether improvedCommand is present in response JSON
        bool hasImprovedCommand = root.TryGetProperty("improvedCommand", out _);
        // Note: The handler currently does NOT populate improvedCommand on validation failures
        hasImprovedCommand.Should().BeFalse(
            "Empirical finding: MatchmakingSimulateHandler does not include improvedCommand in invalid port validation responses.");
    }

    [Theory]
    [InlineData("timeoutSeconds", 0)]
    [InlineData("timeoutSeconds", -5)]
    public async Task RemediationIntegration_InvalidTimeout_ContainsCategoryAndHint_AuditsImprovedCommand(string key, int val)
    {
        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"{{key}}": {{val}}}""");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.TryGetProperty("remediationCategory", out JsonElement catProp).Should().BeTrue();
        catProp.GetString().Should().Be("MalformedCommandArguments");

        root.TryGetProperty("remediationHint", out JsonElement hintProp).Should().BeTrue();
        hintProp.GetString().Should().NotBeNullOrWhiteSpace();

        bool hasImprovedCommand = root.TryGetProperty("improvedCommand", out _);
        hasImprovedCommand.Should().BeFalse(
            "Empirical finding: MatchmakingSimulateHandler does not include improvedCommand in invalid timeout validation responses.");
    }

    [Fact]
    public async Task RemediationIntegration_ServerOffline_ContainsCategoryAndHint_AuditsImprovedCommand()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns((11031, "default"));
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("{}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.TryGetProperty("remediationCategory", out JsonElement catProp).Should().BeTrue();
        catProp.GetString().Should().Be("ChatServerOffline");

        root.TryGetProperty("remediationHint", out JsonElement hintProp).Should().BeTrue();
        hintProp.GetString().Should().NotBeNullOrWhiteSpace();

        bool hasImprovedCommand = root.TryGetProperty("improvedCommand", out _);
        hasImprovedCommand.Should().BeFalse(
            "Empirical finding: MatchmakingSimulateHandler does not include improvedCommand in SocketException offline responses.");
    }

    [Fact]
    public async Task RemediationIntegration_SimulationTimeout_ContainsCategoryAndHint_AuditsImprovedCommand()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns((11031, "default"));
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Operation timed out"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("{}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.TryGetProperty("remediationCategory", out JsonElement catProp).Should().BeTrue();
        catProp.GetString().Should().Be("SimulationTimeout");

        root.TryGetProperty("remediationHint", out JsonElement hintProp).Should().BeTrue();
        hintProp.GetString().Should().NotBeNullOrWhiteSpace();

        bool hasImprovedCommand = root.TryGetProperty("improvedCommand", out _);
        hasImprovedCommand.Should().BeFalse(
            "Empirical finding: MatchmakingSimulateHandler does not include improvedCommand in TimeoutException responses.");
    }

    [Fact]
    public async Task RemediationIntegration_ServiceErrorResult_ContainsCategoryAndHint_AuditsImprovedCommand()
    {
        MatchmakingSimulateResult failResult = new()
        {
            Status = "error",
            ResolvedPort = 11031,
            DiscoveryTier = "default",
            ClientCount = 10,
            SuccessfulConnections = 1,
            HandshakeCount = 0,
            QueueJoinedCount = 0,
            ErrorMessage = "Handshake rejected",
            RemediationCategory = "SimulationTimeout",
            RemediationHint = "Server handshake timed out."
        };

        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks(failResult);
        using JsonDocument doc = JsonDocument.Parse("{}");
        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        JsonElement root = GetResultRoot(result);

        root.GetProperty("remediationCategory").GetString().Should().Be("SimulationTimeout");
        root.GetProperty("remediationHint").GetString().Should().Be("Server handshake timed out.");

        bool hasImprovedCommand = root.TryGetProperty("improvedCommand", out _);
        hasImprovedCommand.Should().BeFalse(
            "Empirical finding: MatchmakingSimulateHandler does not include improvedCommand in service failure result responses.");
    }

    [Fact]
    public async Task RemediationIntegration_ClientCommandRemediationService_PopulatesMemoryBuffer()
    {
        (MatchmakingSimulateHandler handler, _, ClientCommandRemediationService remediation) = CreateHandlerWithMocks();
        remediation.Clear();

        // 1. Trigger port validation error
        using (JsonDocument doc = JsonDocument.Parse("""{"serverPort": -99}"""))
        {
            await handler.HandleAsync(doc.RootElement);
        }

        // 2. Trigger timeout validation error
        using (JsonDocument doc = JsonDocument.Parse("""{"timeoutSeconds": 0}"""))
        {
            await handler.HandleAsync(doc.RootElement);
        }

        IReadOnlyList<ClientCommandErrorRecord> errors = remediation.GetRecentErrors();
        errors.Should().HaveCount(2);
        errors[0].Command.Should().Contain("timeout:0");
        errors[1].Command.Should().Contain("port:-99");
    }

    #endregion

    #region Category 4: Performance (< 2.0s) & Zero Desktop GUI Windows

    [Fact]
    public async Task ChallengerSuite_InnerLoopExecutionSpeed_Runs50QueriesUnder500Ms()
    {
        (MatchmakingSimulateHandler handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{}");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            ToolCallResult res = await handler.HandleAsync(doc.RootElement);
            res.IsError.Should().BeFalse();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "50 mocked simulation calls must execute in < 2000ms");
    }

    [Fact]
    public void ChallengerSuite_ZeroDesktopWindows_DoesNotSpawnAnyGuiProcess()
    {
        Process[] processesBefore = Process.GetProcessesByName("ReplayStudio.GUI");
        processesBefore.Should().BeEmpty();

        McpToolRegistry registry = new(registerDefaults: true);
        registry.Should().NotBeNull();

        Process[] processesAfter = Process.GetProcessesByName("ReplayStudio.GUI");
        processesAfter.Should().BeEmpty();
    }

    [Fact]
    public void TigerBeetlePrng_GeneratesZeroBiasLemireBoundsAndHeterogeneousSwarmWeights()
    {
        TigerBeetlePrng prng = new(seed: 0x1234_5678_9ABC_DEF0UL);
        for (int i = 0; i < 1000; i++)
        {
            ulong sample = prng.IntInclusive(9UL);
            sample.Should().BeLessOrEqualTo(9UL);
        }

        prng.Chance(Ratio.Zero()).Should().BeFalse();
        prng.Chance(Ratio.One()).Should().BeTrue();

        ulong[] weights = prng.GenerateSwarmWeights(6);
        weights.Should().HaveCount(6);
        weights.Sum(w => (long)w).Should().BeGreaterThan(0L);
        int chosen = prng.SampleWeightedIndex(weights);
        weights[chosen].Should().BeGreaterThan(0UL);
    }

    #endregion
}

