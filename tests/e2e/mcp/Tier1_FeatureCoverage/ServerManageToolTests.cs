// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class ServerManageToolTests : McpTestBase
{
    public ServerManageToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ServerManage_StatusCluster_ReturnsValidClusterState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ServerManageArgs args = new()
        {
            Action = "status",
            Target = "cluster"
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_server_manage", args);
        callResult.Should().NotBeNull();
        callResult.IsError.Should().BeFalse();

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().Be("success");
        result.Target.Should().Be("cluster");
        result.ClusterState.Should().NotBeNullOrWhiteSpace();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ServerManage_StatusAll_ReturnsAllMicroservices()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ServerManageArgs args = new()
        {
            Action = "status",
            Target = "all"
        };

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().Be("success");
        result.Services.Should().NotBeNull();
        result.Services!.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ServerManage_StartMasterServer_ReturnsSuccessState()
    {
        ServerManageArgs args = new()
        {
            Action = "start",
            Target = "master-server"
        };

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().BeOneOf("success", "error");
        result.Action.Should().Be("start");
        result.Target.Should().Be("master-server");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ServerManage_StopMasterServer_ReturnsStoppedState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ServerManageArgs args = new()
        {
            Action = "stop",
            Target = "master-server"
        };

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().BeOneOf("success", "error");
        result.Action.Should().Be("stop");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ServerManage_RestartTarget_ExecutesRestartWorkflow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ServerManageArgs args = new()
        {
            Action = "restart",
            Target = "chat-server"
        };

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Action.Should().Be("restart");
        result.Target.Should().Be("chat-server");
    }

    [Fact]
    public async Task ServerManage_WithGameServersFlag_ProbesCompelStatus()
    {
        ServerManageArgs args = new()
        {
            Action = "status",
            Target = "compel",
            WithGameServers = true
        };

        ServerManageResult? result = await Client.CallToolTypedAsync<ServerManageResult>("kongor_server_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().Be("success");
        result.Target.Should().Be("compel");
    }
}
