// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesLifecycleServiceTests.cs
// System: COMPEL.Tests - Dedicated Game Server Supervisor Unit Tests
// Subsystem: Agones Cloud-Native Lifecycle Integration Tests
// ============================================================================

using System.Net;
using System.Text;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace COMPEL.Tests;

public sealed class AgonesLifecycleServiceTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _recordedRequests = [];
        public IReadOnlyList<HttpRequestMessage> RecordedRequests => _recordedRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _recordedRequests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public void Constructor_WhenDisabled_SetsIsEnabledFalse()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "false"
            })
            .Build();

        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config);

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void Constructor_WhenEnabled_ParsesPortAndBaseUrl()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config);

        Assert.True(service.IsEnabled);
        Assert.Equal("http://localhost:9358", service.AgonesBaseUrl);
    }

    [Fact]
    public async Task SendAgonesCommandAsync_WhenSuccess_ReturnsTrueAndPostsJson()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler mockHandler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using HttpClient httpClient = new(mockHandler);
        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient);

        bool result = await service.SendAgonesCommandAsync("ready");

        Assert.True(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("http://localhost:9358/ready", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task SendAgonesCommandAsync_WhenHttpError_ReturnsFalseGracefully()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true"
            })
            .Build();

        MockHttpMessageHandler mockHandler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using HttpClient httpClient = new(mockHandler);

        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient);

        bool result = await service.SendAgonesCommandAsync("health");

        Assert.False(result);
    }

    private static DedicatedServerWorker CreateMockWorker(int index, int port, string state = "Available")
    {
        return new DedicatedServerWorker(index, port, port + 200, port + 10000, port + 10200, $"MockServer_{port}")
        {
            IsRunningOverride = true,
            CurrentState = state
        };
    }

    [Fact]
    public async Task OnMatchEndedAsync_WhenEnabled_DispatchesShutdownCommand()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler mockHandler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using HttpClient httpClient = new(mockHandler);
        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient);

        await service.OnMatchEndedAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("http://localhost:9358/shutdown", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task OnMatchEndedAsync_WhenSiblingWorkersInMatch_DefersShutdown()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler mockHandler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        DedicatedServerWorker worker1 = CreateMockWorker(1, 11235, "Available");
        DedicatedServerWorker worker2 = CreateMockWorker(2, 11236, "InMatch");

        using HttpClient httpClient = new(mockHandler);
        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient,
            workersResolver: () => [worker1, worker2]);

        await service.OnMatchEndedAsync(worker1);

        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task OnMatchEndedAsync_WhenAllSiblingMatchesConcluded_DispatchesShutdown()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler mockHandler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        DedicatedServerWorker worker1 = CreateMockWorker(1, 11235, "Available");
        DedicatedServerWorker worker2 = CreateMockWorker(2, 11236, "Available");

        using HttpClient httpClient = new(mockHandler);
        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient,
            workersResolver: () => [worker1, worker2]);

        await service.OnMatchEndedAsync(worker1);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("http://localhost:9358/shutdown", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public void DedicatedServerWorker_IsInMatch_ReflectsMatchStateAccurately()
    {
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "Available");
        Assert.False(worker.IsInMatch);

        worker.CurrentState = "InMatch";
        Assert.True(worker.IsInMatch);

        // When draining during an active match, IsInMatch remains true even though CurrentState reports Draining
        worker.IsDraining = true;
        Assert.Equal("Draining", worker.CurrentState);
        Assert.True(worker.IsInMatch);

        // When match concludes
        worker.CurrentState = "Available";
        Assert.False(worker.IsInMatch);

        // When worker process stops
        worker.CurrentState = "InMatch";
        worker.IsRunningOverride = false;
        Assert.False(worker.IsInMatch);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ExitsImmediately()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "false"
            })
            .Build();

        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await service.StopAsync(cts.Token);

        Assert.False(service.HasSentReady);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnabled_SendsReadyAndMarksState()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGONES_ENABLED"] = "true",
                ["AGONES_SDK_HTTP_PORT"] = "9358"
            })
            .Build();

        List<string> dispatchedEndpoints = [];
        MockHttpMessageHandler mockHandler = new(req =>
        {
            dispatchedEndpoints.Add(req.RequestUri?.AbsolutePath ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using HttpClient httpClient = new(mockHandler);
        AgonesLifecycleService service = new(
            supervisor: null,
            logger: NullLogger<AgonesLifecycleService>.Instance,
            configuration: config,
            httpClient: httpClient);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        try
        {
            await service.StartAsync(cts.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!service.HasSentReady && sw.ElapsedMilliseconds < 1500)
            {
                await Task.Delay(25, CancellationToken.None);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(service.HasSentReady);
        Assert.Contains("/ready", dispatchedEndpoints);
    }
}
