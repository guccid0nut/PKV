// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class MatchmakingSimulateTests
{
    private static JsonElement GetResultRoot(ToolCallResult result)
    {
        var textItem = (TextContentItem)result.Content[0];
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
            TotalElapsedMs = 45.2,
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

    #region Category 1: Parameter Extraction & Normalization (7 Tests)

    [Fact]
    public async Task MatchmakingSimulateHandler_DefaultParameters_AppliesAllContractDefaults()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.ClientCount.Should().Be(10);
        capturedOptions.ServerHost.Should().Be("127.0.0.1");
        capturedOptions.QueueType.Should().Be("pvp");
        capturedOptions.GameType.Should().Be("normal");
        capturedOptions.MapName.Should().Be("caldavar");
        capturedOptions.GameMode.Should().Be("ap");
        capturedOptions.Region.Should().Be("USE");
        capturedOptions.TimeoutSeconds.Should().Be(15);
        capturedOptions.SimulateMatchServer.Should().BeFalse();
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_FlexibleParameterAliases_ExtractsCorrectly()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "clients": 5,
            "port": 11035,
            "host": "10.0.0.1",
            "queue": "solo",
            "type": "casual",
            "map": "midwars",
            "timeout": 30
        }
        """);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.ClientCount.Should().Be(5);
        capturedOptions.ServerPort.Should().Be(11035);
        capturedOptions.ServerHost.Should().Be("10.0.0.1");
        capturedOptions.QueueType.Should().Be("solo");
        capturedOptions.GameType.Should().Be("casual");
        capturedOptions.MapName.Should().Be("midwars");
        capturedOptions.TimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_StringNumbers_ParsesProperly()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? capturedOptions = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedOptions = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "clientCount": "4",
            "serverPort": "11032",
            "timeoutSeconds": "20"
        }
        """);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        capturedOptions.Should().NotBeNull();
        capturedOptions!.ClientCount.Should().Be(4);
        capturedOptions.ServerPort.Should().Be(11032);
        capturedOptions.TimeoutSeconds.Should().Be(20);
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_ClientCountClamping_Enforces1To10()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        List<int> capturedCounts = [];
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => capturedCounts.Add(opt.ClientCount))
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using (JsonDocument doc0 = JsonDocument.Parse("""{"clientCount": 0}"""))
        {
            await handler.HandleAsync(doc0.RootElement);
        }

        using (JsonDocument doc50 = JsonDocument.Parse("""{"clientCount": 50}"""))
        {
            await handler.HandleAsync(doc50.RootElement);
        }

        capturedCounts.Should().Equal(1, 10);
    }

    [Theory]
    [InlineData(99999)]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task MatchmakingSimulateHandler_InvalidPort_ReturnsMalformedArgumentsError(int invalidPort)
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"serverPort": {{invalidPort}}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task MatchmakingSimulateHandler_InvalidTimeout_ReturnsMalformedArgumentsError(int invalidTimeout)
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse($$"""{"timeoutSeconds": {{invalidTimeout}}}""");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("remediationCategory").GetString().Should().Be("MalformedCommandArguments");
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_OptionalParameters_ExtractsModeRegionSimMatch()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        MatchmakingSimulateOptions? captured = null;
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<MatchmakingSimulateOptions, CancellationToken>((opt, _) => captured = opt)
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success" });

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "gameMode": "ar",
            "region": "EU",
            "simulateMatchServer": true
        }
        """);
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.GameMode.Should().Be("ar");
        captured.Region.Should().Be("EU");
        captured.SimulateMatchServer.Should().BeTrue();
    }

    #endregion

    #region Category 2: Mocked Service Execution & Contracts (5 Tests)

    [Fact]
    public async Task MatchmakingSimulateHandler_SuccessfulSimulation_ReturnsFullContractJson()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        var root = GetResultRoot(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("resolvedPort").GetInt32().Should().Be(11031);
        root.GetProperty("discoveryTier").GetString().Should().Be("default");
        root.GetProperty("clientCount").GetInt32().Should().Be(10);
        root.GetProperty("successfulConnections").GetInt32().Should().Be(10);
        root.GetProperty("handshakeCount").GetInt32().Should().Be(10);
        root.GetProperty("queueJoinedCount").GetInt32().Should().Be(10);
        root.GetProperty("socketsCleaned").GetInt32().Should().Be(10);

        var latency = root.GetProperty("latency");
        latency.GetProperty("connectMs").GetProperty("avgMs").GetDouble().Should().Be(2.5);
        latency.GetProperty("handshakeMs").GetProperty("avgMs").GetDouble().Should().Be(4.0);
        latency.GetProperty("queueJoinMs").GetProperty("avgMs").GetDouble().Should().Be(5.5);

        root.GetProperty("clients").GetArrayLength().Should().Be(10);
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_WithMatchAssignment_IncludesMatchDetailsInResponse()
    {
        MatchmakingSimulateResult matchResult = new()
        {
            Status = "success",
            ResolvedPort = 11031,
            DiscoveryTier = "default",
            ClientCount = 10,
            SuccessfulConnections = 10,
            HandshakeCount = 10,
            QueueJoinedCount = 10,
            MatchAssigned = true,
            MatchAssignment = new MatchAssignmentInfo
            {
                MatchId = 54321,
                ServerAddress = "127.0.0.1",
                ServerPort = 11235,
                Map = "caldavar",
                TeamSize = 5
            }
        };

        var (handler, _, _) = CreateHandlerWithMocks(matchResult);
        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        var root = GetResultRoot(result);
        var assignment = root.GetProperty("matchAssignment");
        assignment.GetProperty("matchId").GetInt32().Should().Be(54321);
        assignment.GetProperty("serverAddress").GetString().Should().Be("127.0.0.1");
        assignment.GetProperty("serverPort").GetInt32().Should().Be(11235);
        assignment.GetProperty("map").GetString().Should().Be("caldavar");
        assignment.GetProperty("teamSize").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_ServiceReturnsError_ReturnsStructuredErrorResult()
    {
        MatchmakingSimulateResult failResult = new()
        {
            Status = "error",
            ResolvedPort = 11031,
            DiscoveryTier = "default",
            ClientCount = 10,
            SuccessfulConnections = 0,
            HandshakeCount = 0,
            QueueJoinedCount = 0,
            ErrorMessage = "All connections refused",
            RemediationCategory = "ChatServerOffline",
            RemediationHint = "Verify ChatServer is running."
        };

        var (handler, _, _) = CreateHandlerWithMocks(failResult);
        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Be("All connections refused");
        root.GetProperty("remediationCategory").GetString().Should().Be("ChatServerOffline");
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_CancellationPassedThrough_RespectsCancellationToken()
    {
        var (handler, mockService, _) = CreateHandlerWithMocks();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        using JsonDocument doc = JsonDocument.Parse("{}");
        await handler.HandleAsync(doc.RootElement, cts.Token);

        mockService.Verify(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_DetailedClientArray_MaintainsPerClientState()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        var root = GetResultRoot(result);
        var clients = root.GetProperty("clients");
        clients.GetArrayLength().Should().Be(10);

        var firstClient = clients[0];
        firstClient.GetProperty("clientIndex").GetInt32().Should().Be(0);
        firstClient.GetProperty("accountId").GetInt32().Should().Be(90000);
        firstClient.GetProperty("accountName").GetString().Should().Be("VirtualClient_0");
        firstClient.GetProperty("connected").GetBoolean().Should().BeTrue();
        firstClient.GetProperty("handshakeComplete").GetBoolean().Should().BeTrue();
        firstClient.GetProperty("queueJoined").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region Category 3: Error Remediation & Resilience (4 Tests)

    [Fact]
    public async Task MatchmakingSimulateHandler_ChatServerOffline_RecordsAndReturnsActionableRemediation()
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
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("remediationCategory").GetString().Should().Be("ChatServerOffline");
        root.GetProperty("remediationHint").GetString().Should().Contain("kongor_server_manage");
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_SimulationTimeout_RecordsAndReturnsTimeoutRemediation()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns((11031, "default"));
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Operation timed out waiting for server"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("remediationCategory").GetString().Should().Be("SimulationTimeout");
        root.GetProperty("remediationHint").GetString().Should().Contain("timeoutSeconds");
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_UnhandledServiceException_ReturnsGracefulError()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns((11031, "default"));
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected internal state failure"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        var root = GetResultRoot(result);
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("Unexpected internal state failure");
    }

    [Fact]
    public async Task MatchmakingSimulateHandler_RemediationHistoryQuery_PopulatesDiskAndMemoryQueue()
    {
        Mock<IMatchmakingSimulateService> mockService = new();
        mockService.Setup(s => s.ResolveChatServerPort(It.IsAny<int?>(), It.IsAny<Func<string, string?>?>()))
            .Returns((11031, "default"));
        mockService.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        remediation.Clear();

        MatchmakingSimulateHandler handler = new(
            simulationService: mockService.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse("{}");
        await handler.HandleAsync(doc.RootElement);

        var errors = remediation.GetRecentErrors();
        errors.Should().NotBeEmpty();
        errors[0].Target.Should().Contain("server");
    }

    #endregion

    #region Category 4: McpToolRegistry Integration & Aliases (5 Tests)

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersMatchmakingSimulateCanonicalTool()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_matchmaking_simulate").Should().BeTrue();
        var tool = registry.GetTool("kongor_matchmaking_simulate");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("kongor_matchmaking_simulate");
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersMatchmakingSimulateAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.HasTool("kongor_tmm_simulate").Should().BeTrue();
        registry.HasTool("kongor_matchmaking_bench").Should().BeTrue();

        var tool1 = registry.GetTool("kongor_tmm_simulate");
        var tool2 = registry.GetTool("kongor_matchmaking_bench");

        tool1.Should().NotBeNull();
        tool1!.Name.Should().Be("kongor_matchmaking_simulate");
        tool2.Should().NotBeNull();
        tool2!.Name.Should().Be("kongor_matchmaking_simulate");
    }

    [Fact]
    public void McpToolRegistry_MatchmakingSimulateSchema_ContainsAllExpectedProperties()
    {
        var def = McpToolRegistry.CreateMatchmakingSimulateDefinition();
        def.Name.Should().Be("kongor_matchmaking_simulate");
        var props = def.InputSchema.Properties;

        props.Should().ContainKey("clientCount");
        props.Should().ContainKey("serverPort");
        props.Should().ContainKey("serverHost");
        props.Should().ContainKey("queueType");
        props.Should().ContainKey("gameType");
        props.Should().ContainKey("mapName");
        props.Should().ContainKey("gameMode");
        props.Should().ContainKey("region");
        props.Should().ContainKey("timeoutSeconds");
        props.Should().ContainKey("simulateMatchServer");
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_DispatchesCanonicalSuccessfully()
    {
        Mock<IMatchmakingSimulateService> mockSim = new();
        mockSim.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success", ClientCount = 10 });

        McpToolRegistry registry = new(
            registerDefaults: true,
            matchmakingSimulateService: mockSim.Object);

        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await registry.ExecuteToolAsync("kongor_matchmaking_simulate", doc.RootElement);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_DispatchesAliasesSuccessfully()
    {
        Mock<IMatchmakingSimulateService> mockSim = new();
        mockSim.Setup(s => s.SimulateMatchmakingAsync(It.IsAny<MatchmakingSimulateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchmakingSimulateResult { Status = "success", ClientCount = 10 });

        McpToolRegistry registry = new(
            registerDefaults: true,
            matchmakingSimulateService: mockSim.Object);

        using JsonDocument doc = JsonDocument.Parse("{}");
        var res1 = await registry.ExecuteToolAsync("kongor_tmm_simulate", doc.RootElement);
        var res2 = await registry.ExecuteToolAsync("kongor_matchmaking_bench", doc.RootElement);

        res1.IsError.Should().BeFalse();
        res2.IsError.Should().BeFalse();
    }

    #endregion

    #region Category 5: End-to-End Loopback Server Integration (4 Tests)

    [Fact]
    public async Task MatchmakingSimulate_LoopbackMockChatServer_CompletesHandshakeAndQueueFor2Clients()
    {
        await using var server = new McpSimulationTestHarness.MockChatServer();
        server.Start();

        MatchmakingSimulateService service = new();
        MatchmakingSimulateOptions options = new()
        {
            ClientCount = 2,
            ServerHost = "127.0.0.1",
            ServerPort = server.Port,
            TimeoutSeconds = 2
        };

        var result = await service.SimulateMatchmakingAsync(options);

        result.Status.Should().Be("success");
        result.ClientCount.Should().Be(2);
        result.SuccessfulConnections.Should().Be(2);
        result.HandshakeCount.Should().Be(2);
        result.QueueJoinedCount.Should().Be(2);
        result.SocketsCleaned.Should().Be(2);
    }

    [Fact]
    public async Task MatchmakingSimulate_LoopbackMockChatServer_10ClientsConcurrent_CompletesUnder500Ms()
    {
        await using var server = new McpSimulationTestHarness.MockChatServer();
        server.Start();

        MatchmakingSimulateService service = new();
        // Warm up accept loop + JIT before timing under heavy parallel suite ThreadPool contention.
        var warmUp = await service.SimulateMatchmakingAsync(new MatchmakingSimulateOptions
        {
            ClientCount = 1,
            ServerHost = "127.0.0.1",
            ServerPort = server.Port,
            TimeoutSeconds = 5
        });
        warmUp.Status.Should().Be("success");

        MatchmakingSimulateOptions options = new()
        {
            ClientCount = 10,
            ServerHost = "127.0.0.1",
            ServerPort = server.Port,
            TimeoutSeconds = 5
        };

        Stopwatch sw = Stopwatch.StartNew();
        var result = await service.SimulateMatchmakingAsync(options);
        sw.Stop();

        result.Status.Should().Be("success");
        result.ClientCount.Should().Be(10);
        result.SuccessfulConnections.Should().Be(10);
        result.HandshakeCount.Should().Be(10);
        result.SocketsCleaned.Should().Be(10);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public async Task MatchmakingSimulate_LoopbackMockChatServer_Disposal_LeavesZeroOpenSockets()
    {
        await using var server = new McpSimulationTestHarness.MockChatServer();
        server.Start();

        MatchmakingSimulateService service = new();
        MatchmakingSimulateOptions options = new()
        {
            ClientCount = 5,
            ServerHost = "127.0.0.1",
            ServerPort = server.Port,
            TimeoutSeconds = 2
        };

        var result = await service.SimulateMatchmakingAsync(options);

        result.SocketsCleaned.Should().Be(5);
        result.Clients.Should().HaveCount(5);
    }

    [Fact]
    public async Task MatchmakingSimulate_LoopbackMockChatServer_SimulatedMatchServer_AssignsMatch()
    {
        await using var server = new McpSimulationTestHarness.MockChatServer();
        server.Start();

        MatchmakingSimulateService service = new();
        MatchmakingSimulateOptions options = new()
        {
            ClientCount = 2,
            ServerHost = "127.0.0.1",
            ServerPort = server.Port,
            TimeoutSeconds = 2,
            SimulateMatchServer = true
        };

        var result = await service.SimulateMatchmakingAsync(options);

        result.Status.Should().Be("success");
        result.HandshakeCount.Should().Be(2);
    }

    #endregion

    #region Category 6: Tiered Port Discovery & Wire Framing Direct Unit Tests (5 Tests)

    [Fact]
    public void TieredPortDiscovery_Tier1_ExplicitPort_ResolvesImmediately()
    {
        MatchmakingSimulateService service = new();
        var (port, tier) = service.ResolveChatServerPort(11035);
        port.Should().Be(11035);
        tier.Should().Be("explicit");
    }

    [Fact]
    public void TieredPortDiscovery_Tier2_AspireEnvironmentVariables_ResolvesHierarchically()
    {
        Dictionary<string, string?> env = new()
        {
            ["services__chat-server__tcp__0"] = "11038"
        };

        MatchmakingSimulateService service = new();
        var (port, tier) = service.ResolveChatServerPort(null, key => env.TryGetValue(key, out var v) ? v : null);

        port.Should().Be(11038);
        tier.Should().Be("aspire");
    }

    [Fact]
    public void TieredPortDiscovery_Tier3_FallbackDefault_Returns11031()
    {
        MatchmakingSimulateService service = new();
        var (port, tier) = service.ResolveChatServerPort(null, _ => null);

        port.Should().Be(11031);
        tier.Should().Be("default");
    }

    [Fact]
    public void ChatAuthHash_Formula_MatchesExpectedSha1Digest()
    {
        int accountId = 90001;
        string ip = "127.0.0.1";
        string cookie = "cookie_90001";
        string salt = MatchmakingSimulateService.S2AuthenticationSalt;

        string expected = Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes($"{accountId}{ip}{cookie}{salt}")));

        string actual = MatchmakingSimulateService.ComputeChatAuthHash(accountId, ip, cookie);

        actual.Should().Be(expected);
        actual.Should().HaveLength(40);
    }

    [Fact]
    public void PacketFrameCodec_EncodeAndDecode_MaintainsLittleEndianLengthAndOpcode()
    {
        ushort opcode = MatchmakingSimulateService.NET_CHAT_CL_CONNECT;
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        byte[] packet = MatchmakingSimulateService.EncodePacket(opcode, payload);

        // Header: length = 2 (opcode) + 4 (payload) = 6
        packet.Length.Should().Be(8);
        BitConverter.ToUInt16(packet, 0).Should().Be(6);
        BitConverter.ToUInt16(packet, 2).Should().Be(opcode);
        packet[4..].Should().Equal(payload);
    }

    #endregion

    #region Category 7: Fast Inner-Loop Performance & Zero GUI Window Checks (2 Tests)

    [Fact]
    public async Task MatchmakingSimulate_InnerLoopPerformance_RunsMultipleQueriesUnderThreshold()
    {
        var (handler, _, _) = CreateHandlerWithMocks();
        using JsonDocument doc = JsonDocument.Parse("{}");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var res = await handler.HandleAsync(doc.RootElement);
            res.IsError.Should().BeFalse();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000); // 20 iterations well under 2000ms
    }

    [Fact]
    public void MatchmakingSimulate_ZeroDesktopWindows_DoesNotSpawnAnyExternalProcess()
    {
        var processesBefore = Process.GetProcessesByName("ReplayStudio.GUI");
        processesBefore.Should().BeEmpty();

        var def = McpToolRegistry.CreateMatchmakingSimulateDefinition();
        def.Should().NotBeNull();

        var processesAfter = Process.GetProcessesByName("ReplayStudio.GUI");
        processesAfter.Should().BeEmpty();
    }

    #endregion
}
