// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http.Json;
using System.Text.Json;
using COMPEL.Endpoints;
using COMPEL.Serialisation;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace COMPEL.Tests;

public sealed class ControlPlaneEndpointsTests
{
    [Fact]
    public void ControlPlaneEndpoints_DedicatedWorkerArguments_IncludesPortAndVoicePortParameters()
    {
        MatchServerManagerOptions options = new()
        {
            UserName = "KONGOR",
            Password = "Password123!",
            Instances = 2,
            Gateway = "127.0.0.1",
            Location = "USE",
            ServerNamePrefix = "DONUT ARENA",
            UseProxy = false,
            PortRangeOffset = 0
        };
        PortPlan plan = new(options);
        DedicatedServerWorker worker = new(1, 11235, plan.LocalVoiceStart, 11235, plan.PublicVoiceStart, "DONUT ARENA #1");

        string[] args = DedicatedWorkerArguments.Build(
            worker,
            options,
            plan,
            "127.0.0.1",
            "127.0.0.1:55555");

        args.Should().Contain("-port");
        int portIdx = Array.IndexOf(args, "-port");
        portIdx.Should().BeGreaterThan(0);
        args[portIdx + 1].Should().Be("11235");

        args.Should().Contain("-voiceport");
        int voicePortIdx = Array.IndexOf(args, "-voiceport");
        voicePortIdx.Should().BeGreaterThan(0);
        args[voicePortIdx + 1].Should().Be("11735");

        args.Should().Contain("+Set");
        args.Should().Contain("svr_port");
        args.Should().Contain("svr_voicePort");
    }

    [Fact]
    public void ControlPlaneEndpoints_ControlPlaneJSONContext_SerializesDedicatedWorkerStatusArray()
    {
        DedicatedWorkerStatus[] workers =
        [
            new(1, 11235, 11435, 11235, 12345, true, 500000000, 476.84, 120.5, 0),
            new(2, 11236, 11436, 11236, 12346, true, 450000000, 429.15, 119.2, 0)
        ];

        string json = JsonSerializer.Serialize(workers, ControlPlaneJSONContext.Default.DedicatedWorkerStatusArray);
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("\"port\":11235");
        json.Should().Contain("\"port\":11236");
        json.Should().Contain("\"running\":true");

        DedicatedWorkerStatus[]? deserialized = JsonSerializer.Deserialize(json, ControlPlaneJSONContext.Default.DedicatedWorkerStatusArray);
        deserialized.Should().NotBeNull();
        deserialized!.Length.Should().Be(2);
        deserialized[0].Port.Should().Be(11235);
        deserialized[1].Port.Should().Be(11236);
    }

    [Fact]
    public void ControlPlaneEndpoints_ControlPlaneJSONContext_SerializesPingResponse()
    {
        PingResponse ping = new("COMPEL", "5.0.0", 1700000000000);
        string json = JsonSerializer.Serialize(ping, ControlPlaneJSONContext.Default.PingResponse);
        json.Should().Contain("\"application\":\"COMPEL\"");
        json.Should().Contain("\"version\":\"5.0.0\"");
    }

    [Fact]
    public void ControlPlaneEndpoints_ControlPlaneJSONContext_SerializesExecuteCommandModels()
    {
        ExecuteCommandRequest req = new("AddFakePlayer 1");
        string reqJson = JsonSerializer.Serialize(req, ControlPlaneJSONContext.Default.ExecuteCommandRequest);
        reqJson.Should().Contain("AddFakePlayer 1");

        ExecuteCommandResponse res = new(11235, "status", true, "stdin", "Command routed");
        string resJson = JsonSerializer.Serialize(res, ControlPlaneJSONContext.Default.ExecuteCommandResponse);
        resJson.Should().Contain("\"port\":11235");
        resJson.Should().Contain("\"channel\":\"stdin\"");
        resJson.Should().Contain("\"success\":true");
    }

    [Fact]
    public void ControlPlaneEndpoints_ControlPlaneJSONContext_SerializesDedicatedServerLogsResponse()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DedicatedServerLogLine[] logs =
        [
            new(now, "Starting game engine...", false, "server"),
            new(now.AddSeconds(1), "Hero spawned", false, "server")
        ];
        DedicatedServerLogsResponse response = new(11235, "DONUT ARENA #1", logs);

        string json = JsonSerializer.Serialize(response, ControlPlaneJSONContext.Default.DedicatedServerLogsResponse);
        json.Should().Contain("\"port\":11235");
        json.Should().Contain("\"serverName\":\"DONUT ARENA #1\"");
        json.Should().Contain("Hero spawned");

        DedicatedServerLogsResponse? deserialized = JsonSerializer.Deserialize(json, ControlPlaneJSONContext.Default.DedicatedServerLogsResponse);
        deserialized.Should().NotBeNull();
        deserialized!.Logs.Count.Should().Be(2);
        deserialized.Logs[0].Text.Should().Be("Starting game engine...");
    }

    [Fact]
    public void ControlPlaneEndpoints_ControlPlaneJSONContext_SerializesStatusResponseWithWorkers()
    {
        PortAllocationResponse ports = new(11235, 11238, 11435, 11438, 11235, 11238, 11435, 11438, 11234);
        DedicatedWorkerStatus[] workers =
        [
            new(1, 11235, 11435, 11235, 1001, true, 400000000, 381.47, 50.0, 0),
            new(2, 11236, 11436, 11236, 1002, true, 410000000, 391.01, 50.0, 0),
            new(3, 11237, 11437, 11237, 1003, true, 420000000, 400.54, 50.0, 0),
            new(4, 11238, 11438, 11238, 1004, true, 430000000, 410.08, 50.0, 0)
        ];

        StatusResponse status = new(
            Application: "COMPEL",
            Version: "5.0.0",
            ServerNamePrefix: "DONUT ARENA",
            Location: "USE",
            ServerAddress: "127.0.0.1",
            Instances: 4,
            UseProxy: false,
            PortRangeOffset: 0,
            Ports: ports,
            DistributionVersion: null,
            SynchronisationState: "Disabled",
            ManagerRunning: true,
            ProxyRunning: false,
            PingResponderBound: true,
            ProxyFailedForwarderCount: 0,
            UptimeSeconds: 55.5,
            ActiveWorkers: 4,
            Workers: workers);

        string json = JsonSerializer.Serialize(status, ControlPlaneJSONContext.Default.StatusResponse);
        json.Should().Contain("\"instances\":4");
        json.Should().Contain("\"portRangeOffset\":0");
        json.Should().Contain("\"gameStart\":11235");
        json.Should().Contain("\"gameEnd\":11238");
        json.Should().Contain("\"pingPort\":11234");
        json.Should().Contain("\"activeWorkers\":4");
        json.Should().Contain("\"port\":11235");
        json.Should().Contain("\"port\":11238");
    }

    [Fact]
    public void ControlPlaneEndpoints_WhenExecuted_MapsAllUnifiedApiRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.Services.AddRouting();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default);
        });

        WebApplication app = builder.Build();
        app.MapControlPlaneEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        // Anonymous endpoints: both non-prefixed and /api/ prefixed
        endpoints.Should().Contain("/ping");
        endpoints.Should().Contain("/api/ping");
        endpoints.Should().Contain("/status");
        endpoints.Should().Contain("/api/status");
        endpoints.Should().Contain("/servers");
        endpoints.Should().Contain("/api/servers");
        endpoints.Should().Contain("/servers/{port:int}");
        endpoints.Should().Contain("/api/servers/{port:int}");

        // Management endpoints: both non-prefixed and /api/ prefixed
        endpoints.Should().Contain("/sync");
        endpoints.Should().Contain("/api/sync");
        endpoints.Should().Contain("/instances/start");
        endpoints.Should().Contain("/api/instances/start");
        endpoints.Should().Contain("/instances/stop");
        endpoints.Should().Contain("/api/instances/stop");
        endpoints.Should().Contain("/instances/restart");
        endpoints.Should().Contain("/api/instances/restart");
        endpoints.Should().Contain("/instances/{port:int}/restart");
        endpoints.Should().Contain("/api/instances/{port:int}/restart");
        endpoints.Should().Contain("/instances/{port:int}/start");
        endpoints.Should().Contain("/api/instances/{port:int}/start");
        endpoints.Should().Contain("/instances/{port:int}/stop");
        endpoints.Should().Contain("/api/instances/{port:int}/stop");
        endpoints.Should().Contain("/instances/{port:int}/logs");
        endpoints.Should().Contain("/api/instances/{port:int}/logs");
        endpoints.Should().Contain("/instances/{port:int}/logs/stream");
        endpoints.Should().Contain("/api/instances/{port:int}/logs/stream");
        endpoints.Should().Contain("/instances/{port:int}/command");
        endpoints.Should().Contain("/api/instances/{port:int}/command");
        endpoints.Should().Contain("/admin/server/{port:int}/rpc");
        endpoints.Should().Contain("/api/admin/server/{port:int}/rpc");
        endpoints.Should().Contain("/proxy/restart");
        endpoints.Should().Contain("/api/proxy/restart");
    }

    [Fact]
    public async Task ControlPlaneEndpoints_LiveHttpProbe_PingEndpoints()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default);
        });

        WebApplication app = builder.Build();
        app.MapControlPlaneEndpoints();
        await app.StartAsync();

        try
        {
            var serverAddresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
            string baseAddress = serverAddresses?.FirstOrDefault() ?? app.Urls.First();
            using HttpClient client = new() { BaseAddress = new Uri(baseAddress) };

            // Probe /ping
            HttpResponseMessage ping1 = await client.GetAsync("/ping");
            ping1.IsSuccessStatusCode.Should().BeTrue();
            PingResponse? res1 = await ping1.Content.ReadFromJsonAsync(ControlPlaneJSONContext.Default.PingResponse);
            res1.Should().NotBeNull();
            res1!.Application.Should().Be("COMPEL");

            // Probe /api/ping
            HttpResponseMessage ping2 = await client.GetAsync("/api/ping");
            ping2.IsSuccessStatusCode.Should().BeTrue();
            PingResponse? res2 = await ping2.Content.ReadFromJsonAsync(ControlPlaneJSONContext.Default.PingResponse);
            res2.Should().NotBeNull();
            res2!.Application.Should().Be("COMPEL");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
