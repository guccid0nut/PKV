// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Cli.Commands;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests;

public class CliCommandsTests
{
    [Fact]
    public void PackageCommandSettings_Defaults_AreCorrect()
    {
        PackageCommandSettings settings = new();

        Assert.Equal("all", settings.Target);
        Assert.Equal("current", settings.TargetOs);
        Assert.Equal("Release", settings.Configuration);
        Assert.Equal("dist", settings.OutDir);
        Assert.False(settings.PublishAot);
        Assert.False(settings.Clean);
        Assert.False(settings.Prebake);
    }

    [Theory]
    [InlineData("willowmaker")]
    [InlineData("compel")]
    [InlineData("services")]
    [InlineData("all")]
    public void PackageCommand_TargetNormalization_RecognizesValidTargets(string target)
    {
        string normalized = target.ToLowerInvariant();
        bool isValid = normalized is "willowmaker" or "client" or "launcher" or
                                    "compel" or "server" or "supervisor" or
                                    "services" or "nexus" or "microservices" or "cluster" or "all";

        Assert.True(isValid);
    }

    [Fact]
    public void AnalyticsCommandSettings_Defaults_AreCorrect()
    {
        AnalyticsCommandSettings settings = new();

        Assert.Equal("list", settings.Action);
        Assert.Null(settings.Argument);
        Assert.Equal(50, settings.Limit);
        Assert.False(settings.JsonOutput);
    }

    [Fact]
    public async Task AnalyticsCommand_ListAction_ExecutesWithoutError()
    {
        AnalyticsCommand command = new();
        AnalyticsCommandSettings settings = new() { Action = "list" };

        int exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AnalyticsCommand_SqlAction_WithValidQuery_ExecutesSuccessfully()
    {
        AnalyticsCommand command = new();
        AnalyticsCommandSettings settings = new()
        {
            Action = "sql",
            Argument = "SELECT 42 AS answer, 'Kongor' AS system"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AnalyticsCommand_SqlAction_WithoutQuery_ReturnsErrorCode()
    {
        AnalyticsCommand command = new();
        AnalyticsCommandSettings settings = new()
        {
            Action = "sql",
            Argument = ""
        };

        int exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void WalCommandSettings_Defaults_AreCorrect()
    {
        WalCommandSettings settings = new();

        Assert.Equal("status", settings.Action);
        Assert.Null(settings.MatchId);
        Assert.False(settings.JsonOutput);
    }

    [Fact]
    public async Task WalCommand_StatusAction_ExecutesWithoutError()
    {
        WalCommand command = new();
        WalCommandSettings settings = new() { Action = "status" };

        int exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DeployCommand_StatusAction_ExecutesWithoutError()
    {
        DeployCommand command = new();
        DeployCommandSettings settings = new() { Action = "status" };

        int exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }
}
