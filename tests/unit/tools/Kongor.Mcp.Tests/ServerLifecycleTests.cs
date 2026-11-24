// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ServerLifecycleTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixedTime;
        public FakeTimeProvider(DateTimeOffset fixedTime) => _fixedTime = fixedTime;
        public override DateTimeOffset GetUtcNow() => _fixedTime;
    }

    [Fact]
    public async Task ServerManageHandler_StatusCluster_ReturnsValidStructure()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var mockService = new Mock<ILifecycleService>();
        mockService.Setup(s => s.GetStatusAsync("cluster", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult
            {
                Status = "success",
                Action = "status",
                Target = "cluster",
                ClusterState = "Running",
                DashboardUrl = "https://localhost:5550",
                Services = new Dictionary<string, ServiceStatusInfo>
                {
                    ["master-server"] = new() { Port = 55555, State = "Running", ProcessId = 1234, Responding = true },
                    ["chat-server"] = new() { Port = 11031, State = "Running", ProcessId = 1235, Responding = true }
                },
                Timestamp = fixedTime.ToString("o")
            });

        var handler = new ServerManageHandler(mockService.Object, timeProvider);

        var args = JsonElement.Parse("{\"action\":\"status\",\"target\":\"cluster\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();

        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
        root.GetProperty("target").GetString().Should().Be("cluster");
        root.GetProperty("clusterState").GetString().Should().Be("Running");
        root.GetProperty("dashboardUrl").GetString().Should().Be("https://localhost:5550");
        root.GetProperty("services").GetProperty("master-server").GetProperty("port").GetInt32().Should().Be(55555);
        root.GetProperty("services").GetProperty("chat-server").GetProperty("port").GetInt32().Should().Be(11031);
        root.GetProperty("timestamp").GetString().Should().Be(fixedTime.ToString("o"));
    }

    [Fact]
    public async Task ServerManageHandler_StatusAll_ReturnsAllKnownServices()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var lifecycle = new LifecycleService(timeProvider);
        var handler = new ServerManageHandler(lifecycle, timeProvider);

        var args = JsonElement.Parse("{\"action\":\"status\",\"target\":\"all\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        var services = root.GetProperty("services");
        services.TryGetProperty("master-server", out _).Should().BeTrue();
        services.TryGetProperty("chat-server", out _).Should().BeTrue();
        services.TryGetProperty("web-portal-api", out _).Should().BeTrue();
        services.TryGetProperty("web-portal-ui", out _).Should().BeTrue();
        services.TryGetProperty("compel", out _).Should().BeTrue();
        services.TryGetProperty("redis", out _).Should().BeTrue();
        services.TryGetProperty("pyroscope", out _).Should().BeTrue();
        services.TryGetProperty("aspire-host", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ServerManageHandler_StartMasterServer_DispatchesToService()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var mockService = new Mock<ILifecycleService>();
        mockService.Setup(s => s.StartAsync("master-server", false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult
            {
                Status = "success",
                Action = "start",
                Target = "master-server",
                ClusterState = "Running",
                Services = new Dictionary<string, ServiceStatusInfo>
                {
                    ["master-server"] = new() { Port = 55555, State = "Running", ProcessId = 5555 }
                },
                Timestamp = fixedTime.ToString("o")
            });

        var handler = new ServerManageHandler(mockService.Object, timeProvider);
        var args = JsonElement.Parse("{\"action\":\"start\",\"target\":\"master-server\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("start");
        root.GetProperty("target").GetString().Should().Be("master-server");
        mockService.Verify(s => s.StartAsync("master-server", false, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ServerManageHandler_StopTarget_DispatchesToService()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var mockService = new Mock<ILifecycleService>();
        mockService.Setup(s => s.StopAsync("chat-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult
            {
                Status = "success",
                Action = "stop",
                Target = "chat-server",
                ClusterState = "Stopped",
                Services = new Dictionary<string, ServiceStatusInfo>
                {
                    ["chat-server"] = new() { Port = 11031, State = "Stopped", ProcessId = 0 }
                },
                Timestamp = fixedTime.ToString("o")
            });

        var handler = new ServerManageHandler(mockService.Object, timeProvider);
        var args = JsonElement.Parse("{\"action\":\"stop\",\"target\":\"chat-server\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("stop");
        root.GetProperty("target").GetString().Should().Be("chat-server");
        mockService.Verify(s => s.StopAsync("chat-server", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ServerManageHandler_RestartTarget_DispatchesToService()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var mockService = new Mock<ILifecycleService>();
        mockService.Setup(s => s.RestartAsync("cluster", true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult
            {
                Status = "success",
                Action = "restart",
                Target = "cluster",
                ClusterState = "Running",
                Services = new Dictionary<string, ServiceStatusInfo>
                {
                    ["master-server"] = new() { Port = 55555, State = "Running" },
                    ["compel"] = new() { Port = 11234, State = "Running" }
                },
                Timestamp = fixedTime.ToString("o")
            });

        var handler = new ServerManageHandler(mockService.Object, timeProvider);
        var args = JsonElement.Parse("{\"action\":\"restart\",\"target\":\"cluster\",\"withGameServers\":true}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("restart");
        mockService.Verify(s => s.RestartAsync("cluster", true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ServerManageHandler_LogsAction_RetrievesLogsAndStatus()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var mockService = new Mock<ILifecycleService>();
        mockService.Setup(s => s.GetRecentLogsAsync("master-server", 25, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServiceLogEntry>
            {
                new()
                {
                    Timestamp = fixedTime,
                    ServiceName = "master-server",
                    Message = "SRP Authentication Initialized",
                    Level = "INFO",
                    IsError = false
                }
            });

        mockService.Setup(s => s.GetStatusAsync("master-server", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult
            {
                Status = "success",
                Action = "status",
                Target = "master-server",
                ClusterState = "Running",
                Services = new Dictionary<string, ServiceStatusInfo>
                {
                    ["master-server"] = new() { Port = 55555, State = "Running" }
                },
                Timestamp = fixedTime.ToString("o")
            });

        var handler = new ServerManageHandler(mockService.Object, timeProvider);
        var args = JsonElement.Parse("{\"action\":\"logs\",\"target\":\"master-server\",\"maxLines\":25}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("action").GetString().Should().Be("logs");
        root.GetProperty("target").GetString().Should().Be("master-server");
        root.GetProperty("recentLogs").GetArrayLength().Should().Be(1);
        root.GetProperty("recentLogs")[0].GetProperty("message").GetString().Should().Be("SRP Authentication Initialized");
    }

    [Fact]
    public async Task ServerManageHandler_MissingAction_DefaultsToStatus()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var lifecycle = new LifecycleService(timeProvider);
        var handler = new ServerManageHandler(lifecycle, timeProvider);

        var args = JsonElement.Parse("{\"target\":\"cluster\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
    }

    [Fact]
    public async Task ServerManageHandler_InvalidTarget_HandlesGracefully()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var lifecycle = new LifecycleService(timeProvider);
        var handler = new ServerManageHandler(lifecycle, timeProvider);

        var args = JsonElement.Parse("{\"action\":\"status\",\"target\":\"invalid_target_xyz\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("target").GetString().Should().Be("invalid_target_xyz");
    }

    [Fact]
    public void LifecycleService_GetRegisteredEndpoints_ContainsAllMicroservices()
    {
        var lifecycle = new LifecycleService();
        var endpoints = lifecycle.GetRegisteredEndpoints();

        endpoints.Should().ContainKey("master-server");
        endpoints["master-server"].PrimaryPort.Should().Be(55555);

        endpoints.Should().ContainKey("chat-server");
        endpoints["chat-server"].PrimaryPort.Should().Be(11031);
        endpoints["chat-server"].AdditionalPorts.Should().Contain(11032);

        endpoints.Should().ContainKey("web-portal-api");
        endpoints["web-portal-api"].PrimaryPort.Should().Be(5200);

        endpoints.Should().ContainKey("web-portal-ui");
        endpoints["web-portal-ui"].PrimaryPort.Should().Be(5266);

        endpoints.Should().ContainKey("compel");
        endpoints["compel"].PrimaryPort.Should().Be(11234);

        endpoints.Should().ContainKey("redis");
        endpoints["redis"].PrimaryPort.Should().Be(28298);

        endpoints.Should().ContainKey("pyroscope");
        endpoints["pyroscope"].PrimaryPort.Should().Be(4040);

        endpoints.Should().ContainKey("aspire-host");
        endpoints["aspire-host"].PrimaryPort.Should().Be(5550);
    }

    [Fact]
    public void LifecycleService_ActiveListeningPorts_QueriesSystemWithoutCrashing()
    {
        var lifecycle = new LifecycleService();
        HashSet<int> ports = lifecycle.GetActiveListeningPorts();
        ports.Should().NotBeNull();
    }

    [Fact]
    public async Task LifecycleService_ProbePortAsync_NonExistentPort_ReturnsFalseQuickly()
    {
        var lifecycle = new LifecycleService();
        // Port 59999 should not be actively listening in test runner
        bool isOpen = await lifecycle.ProbePortAsync("127.0.0.1", 59999, timeoutMs: 100);
        isOpen.Should().BeFalse();
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_ServerManage_ExecutesSuccessfully()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var registry = new McpToolRegistry(registerDefaults: true, timeProvider: timeProvider);

        var args = JsonElement.Parse("{\"action\":\"status\",\"target\":\"cluster\"}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_server_manage", args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();

        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("status");
        root.GetProperty("clusterState").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("services").Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task McpToolRegistry_ExecuteToolAsync_ServerManage_RoutesToCompelInstances()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var registry = new McpToolRegistry(registerDefaults: true, timeProvider: timeProvider);

        var args = JsonElement.Parse("{\"action\":\"instances\",\"target\":\"compel\"}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_server_manage", args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();

        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task McpToolRegistry_ExecuteToolAsync_ServerManage_RoutesToGameServerDiscover()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));
        var registry = new McpToolRegistry(registerDefaults: true, timeProvider: timeProvider);

        var args = JsonElement.Parse("{\"action\":\"discover\",\"target\":\"dedicated\"}");
        ToolCallResult result = await registry.ExecuteToolAsync("kongor_server_manage", args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();

        string json = ((TextContentItem)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("status", out _).Should().BeTrue();
    }
}

