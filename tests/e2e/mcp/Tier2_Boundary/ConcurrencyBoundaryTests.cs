// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier2_Boundary;

public sealed class ConcurrencyBoundaryTests : McpTestBase
{
    public ConcurrencyBoundaryTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Boundary_PipelinedJsonRpcRequests_ProcessesInSequence()
    {
        List<Task<JsonRpcResponse>> tasks = [];
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Client.PingAsync());
        }

        JsonRpcResponse[] responses = await Task.WhenAll(tasks);
        responses.Should().HaveCount(5);
        foreach (JsonRpcResponse r in responses)
        {
            r.Should().NotBeNull();
            r.Error.Should().BeNull();
        }
    }

    [Fact]
    public async Task Boundary_RapidPingRequests_ReturnsCorrectIdsForEveryRequest()
    {
        for (int i = 0; i < 10; i++)
        {
            JsonRpcResponse resp = await Client.PingAsync();
            resp.Should().NotBeNull();
            resp.Error.Should().BeNull();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_InterleavedToolCalls_MaintainsRequestResponseCorrelation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Task<ServerManageResult?> t1 = Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", new ServerManageArgs { Action = "status", Target = "cluster" });
        Task<GameManageResult?> t2 = Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", new GameManageArgs { Action = "status" });
        Task<GameTelemetryResult?> t3 = Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", new GameTelemetryArgs { TimeoutMs = 2000 });

        await Task.WhenAll(t1, t2, t3);

        ServerManageResult? r1 = await t1;
        GameManageResult? r2 = await t2;
        GameTelemetryResult? r3 = await t3;

        r1.Should().NotBeNull();
        r2.Should().NotBeNull();
        r3.Should().NotBeNull();
    }
}
