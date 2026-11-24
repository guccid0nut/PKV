// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace ASPIRE.Tests.NEXUS.ControlPanel.GUI;

public sealed class GameServerFleetServiceTests
{
    [Fact]
    public async Task PingHostAsync_ReturnsSuccess_OnValidResponse()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        CompelPingResponseDto pingDto = new()
        {
            Application = "COMPEL",
            Version = "2.0.0",
            Timestamp = 1700000000000
        };

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.PathAndQuery == "/ping"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(pingDto))
            });

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        // Act
        (bool success, long latency, CompelPingResponseDto? resp) = await service.PingHostAsync("127.0.0.1", 8080);

        // Assert
        Assert.True(success);
        Assert.NotNull(resp);
        Assert.Equal("COMPEL", resp.Application);
        Assert.True(latency >= 0);
    }

    [Fact]
    public async Task GetHostStatusAsync_AttachesBearerToken_AndDeserializesStatus()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        CompelStatusResponseDto statusDto = new()
        {
            Application = "COMPEL",
            Version = "2.0.0",
            ServerNamePrefix = "TEST-ARENA",
            Location = "USE",
            Instances = 2,
            ManagerRunning = true,
            ProxyRunning = true,
            UptimeSeconds = 120.5
        };

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.PathAndQuery == "/status" &&
                    req.Headers.Authorization != null &&
                    req.Headers.Authorization.Parameter == "test_secret_token"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(statusDto))
            });

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        // Act
        (bool success, CompelStatusResponseDto? result, string? err) =
            await service.GetHostStatusAsync("127.0.0.1", 8080, "test_secret_token");

        // Assert
        Assert.True(success);
        Assert.Null(err);
        Assert.NotNull(result);
        Assert.True(result.ManagerRunning);
        Assert.Equal("TEST-ARENA", result.ServerNamePrefix);
    }

    [Fact]
    public async Task StartInstancesAsync_SendsPostRequest_AndReturnsSuccessMessage()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        CompelActionResponseDto actionDto = new()
        {
            Action = "start",
            Success = true,
            Message = "Start Requested"
        };

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.PathAndQuery == "/instances/start"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(actionDto))
            });

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        // Act
        (bool success, string msg) = await service.StartInstancesAsync("127.0.0.1", 8080, "test_token");

        // Assert
        Assert.True(success);
        Assert.Equal("Start Requested", msg);
    }

    [Fact]
    public async Task GetDiscoveredHostsAsync_WithoutSecretsManager_ReturnsSingleLocalHostEntry()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        // Act
        List<GameServerHostEntry> hosts = await service.GetDiscoveredHostsAsync();

        // Assert
        Assert.Single(hosts);
        Assert.Equal("Local Game Server (COMPEL)", hosts[0].Name);
        Assert.Equal("127.0.0.1", hosts[0].HostAddress);
        Assert.Equal(11234, hosts[0].Port);
        Assert.True(hosts[0].IsLocal);
    }

    [Fact]
    public async Task StreamServerLogsAsync_ParsesSseStream_YieldsLogDtos()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        string ssePayload =
            ": keepalive\n\n" +
            "data: {\"timestamp\":\"2026-09-25T00:00:00Z\",\"text\":\"Server boot complete\",\"isError\":false,\"source\":\"server\"}\n\n" +
            ": keepalive\n\n" +
            "data: {\"timestamp\":\"2026-09-25T00:00:01Z\",\"text\":\"Match started on Caldavar\",\"isError\":false,\"source\":\"server\"}\n\n";

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.PathAndQuery.Contains("/instances/11235/logs/stream")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(ssePayload, System.Text.Encoding.UTF8, "text/event-stream")
            });

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        // Act
        List<CompelDedicatedServerLogLineDto> received = [];
        await foreach (CompelDedicatedServerLogLineDto item in service.StreamServerLogsAsync("127.0.0.1", 11234, 11235))
        {
            received.Add(item);
        }

        // Assert
        Assert.Equal(2, received.Count);
        Assert.Equal("Server boot complete", received[0].Text);
        Assert.False(received[0].IsError);
        Assert.Equal("Match started on Caldavar", received[1].Text);
    }

    [Fact]
    public async Task PingHostAsync_WhenFailsRepeatedly_LogsOnceAndSuppressesDuplicates()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("No connection could be made because the target machine actively refused it."));

        List<string> logs = [];
        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(
            httpClient: client,
            logSink: (svc, msg, isErr) => logs.Add($"[{svc}] {msg}"));

        // Act: Ping 5 times consecutively while endpoint is offline
        for (int i = 0; i < 5; i++)
        {
            await service.PingHostAsync("127.0.0.1", 11234);
        }

        // Assert: First call logs unreachable with duplicate suppression notice, subsequent 4 calls are suppressed!
        Assert.Single(logs);
        Assert.Contains("unreachable (offline)", logs[0]);
        Assert.Contains("Suppressing duplicate polling logs", logs[0]);
    }

    [Fact]
    public async Task PingHostAsync_WhenRecoversFromOffline_LogsOnlineTransition()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        int callCount = 0;

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    throw new HttpRequestException("Target machine actively refused it.");
                }
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(JsonSerializer.Serialize(new CompelPingResponseDto
                    {
                        Application = "COMPEL",
                        Version = "2.0.0"
                    }))
                });
            });

        List<string> logs = [];
        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(
            httpClient: client,
            logSink: (svc, msg, isErr) => logs.Add($"[{svc}] {msg}"));

        // Act: 2 offline pings followed by 1 successful ping
        await service.PingHostAsync("127.0.0.1", 11234);
        await service.PingHostAsync("127.0.0.1", 11234);
        await service.PingHostAsync("127.0.0.1", 11234);

        // Assert: Exactly 2 logs: 1 for offline (with 1 suppressed), 1 for online recovery
        Assert.Equal(2, logs.Count);
        Assert.Contains("unreachable (offline)", logs[0]);
        Assert.Contains("ping succeeded", logs[1]);
        Assert.Contains("recovered after 1 offline polls", logs[1]);
    }

    [Fact]
    public async Task GetDiscoveredHostsAsync_WhenPortsClosed_SuppressesDiscoveryErrorSpam()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("No connection could be made because the target machine actively refused it."));

        List<string> logs = [];
        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(
            httpClient: client,
            logSink: (svc, msg, isErr) => logs.Add($"[{svc}] {msg}"));

        // Act: Run discovery on closed ports
        List<GameServerHostEntry> hosts = await service.GetDiscoveredHostsAsync();

        // Assert: Default host is returned
        Assert.Single(hosts);
        Assert.Equal(11234, hosts[0].Port);

        // Assert: NO speculative port spam (like 11334, 11244, 11434, 11254) in the logs!
        Assert.DoesNotContain(logs, l => l.Contains("11334"));
        Assert.DoesNotContain(logs, l => l.Contains("11244"));
        Assert.DoesNotContain(logs, l => l.Contains("11434"));
        Assert.DoesNotContain(logs, l => l.Contains("11254"));
    }

    [Fact]
    public async Task GetDiscoveredHostsAsync_WhenPort11484Responds_DiscoversQueueArcadeServerEntry()
    {
        // protects: Auto-discovery of local Queue Arcade COMPEL fleet on port 11484 inside the Game Servers tab.
        // fails_when: Running Queue Arcade on port 11484 is invisible in the Control Panel Game Servers fleet list.
        // seam: Real GameServerFleetService.GetDiscoveredHostsAsync against HTTP 11484 /ping responder.
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                if (req.RequestUri!.Port == 11484 && req.RequestUri.PathAndQuery == "/status")
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(JsonSerializer.Serialize(new CompelStatusResponseDto
                        {
                            OperatingSystem = "Windows",
                            Location = "USE",
                            UptimeSeconds = 42.0
                        }))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

        HttpClient client = new(mockHandler.Object);
        GameServerFleetService service = new(httpClient: client);

        List<GameServerHostEntry> hosts = await service.GetDiscoveredHostsAsync();

        Assert.Equal(2, hosts.Count);
        Assert.Contains(hosts, h => h.Port == 11234);
        Assert.Contains(hosts, h => h.Port == 11484 && h.Name.Contains("Queue Arcade", StringComparison.OrdinalIgnoreCase));
    }
}
