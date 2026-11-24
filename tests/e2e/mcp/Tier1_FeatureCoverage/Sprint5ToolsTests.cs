// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class Sprint5ToolsTests : McpTestBase
{
    public Sprint5ToolsTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task CiTrigger_List_ReturnsValidOutput()
    {
        var args = new
        {
            action = "list",
            workflow = "build-and-test.yml"
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_ci_trigger", args);
        callResult.Should().NotBeNull();
        callResult.Content.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DbMigrate_List_ReturnsMigrationsSummary()
    {
        var args = new
        {
            action = "list"
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_db_migrate", args, timeout: TimeSpan.FromSeconds(60));
        callResult.Should().NotBeNull();
        callResult.Content.Should().NotBeEmpty();
        string text = callResult.Content[0].Text ?? string.Empty;
        text.Should().Contain("Database Migrations Summary");
    }

    [Fact]
    public async Task LogCorrelate_Errors_ReturnsStructuredReport()
    {
        var args = new
        {
            action = "errors",
            minutes = 30
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_log_correlate", args, timeout: TimeSpan.FromSeconds(60));
        callResult.Should().NotBeNull();
        callResult.Content.Should().NotBeEmpty();
        string text = callResult.Content[0].Text ?? string.Empty;
        text.Should().Contain("Recent Microservice Errors");
    }

    [Fact]
    public async Task CanaryDeploy_Status_ReturnsAgonesDeploymentStatus()
    {
        var args = new
        {
            action = "status"
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_deploy_canary", args, timeout: TimeSpan.FromSeconds(60));
        callResult.Should().NotBeNull();
        callResult.Content.Should().NotBeEmpty();
        string text = callResult.Content[0].Text ?? string.Empty;
        text.Should().Contain("Agones GameServer Deployment Status");
    }
}
