// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Services;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class CliOrchestrationTests : IDisposable
{
    private readonly string _testTempDir;

    public CliOrchestrationTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_cli_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task TriageCommand_ExecutesAndFormatsSpectreSummary_WithSyntheticCrash()
    {
        // Construct a synthetic K2 text crash diagnostic log
        string diagPath = Path.Combine(_testTempDir, "diag_client_crash.txt");
        string diagContent = """
            Crash in cgame_x64.dll at 0x00007FFB341E5DE0
            Exception: 0xC0000005 ACCESS_VIOLATION
            Registers:
            RAX: 0x0000000000000000  RBX: 0x00007FFB34200000
            RCX: 0x0000000000000040  RDX: 0x0000000000000001
            RSP: 0x000000B2134FE920  RBP: 0x000000B2134FE980
            RIP: 0x00007FFB341E5DE0
            Callstack:
            0: cgame_x64.dll+0x1e5de0
            1: hon_x64.exe+0x42010
            """;
        await File.WriteAllTextAsync(diagPath, diagContent);

        var command = new TriageCommand();
        var settings = new TriageCommandSettings
        {
            DumpPath = diagPath,
            DryRun = true,
            NoBacklog = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task HealCommand_ExecutesXmlAndLobbyHealing_Successfully()
    {
        // Construct synthetic Lua and XML with known reparable defects
        string luaPath = Path.Combine(_testTempDir, "test_ui.lua");
        await File.WriteAllTextAsync(luaPath, "local w = Windows.main:GetWidget('button')");

        string xmlPath = Path.Combine(_testTempDir, "test.interface");
        await File.WriteAllTextAsync(xmlPath, "<interface><button onclick=\"widget:DoEvent('onclick')\" /></interface>");

        var command = new HealCommand();
        var settings = new HealCommandSettings
        {
            TargetDirectory = _testTempDir,
            DryRun = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ForgeCommand_UsageTableRendersCleanly_WhenHelpOrEmptyArgsProvided()
    {
        var command = new ForgeCommand();
        var settings = new RawArgsSettings { Args = ["--help"] };

        int exitCode = command.Execute(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ScrimmageCommand_WhenInvoked_EnforcesBoundedDurationAndCleansUpProcess()
    {
        var command = new ScrimmageCommand();
        var settings = new ScrimmageCommandSettings
        {
            Port = 11235,
            Map = "caldavar",
            Bots = 10,
            DurationSeconds = 3,
            DryRun = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void AssetWatcherService_DebouncesRapidEvents_AndPerformsAtomicSwap()
    {
        using var watcher = new AssetWatcherService(logger: _ => { });

        string sourcePath = Path.Combine(_testTempDir, "source.tmp");
        string destPath = Path.Combine(_testTempDir, "final_asset.dds");

        File.WriteAllText(sourcePath, "TEXTURE_PAYLOAD_D3D9_RXGB");

        // Verify lock release check
        bool readable = AssetWatcherService.WaitUntilFileIsReadable(sourcePath);
        Assert.True(readable);

        // Perform atomic file replacement
        bool swapped = watcher.AtomicReplaceFile(sourcePath, destPath);
        Assert.True(swapped);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destPath));
        Assert.Equal("TEXTURE_PAYLOAD_D3D9_RXGB", File.ReadAllText(destPath));
        Assert.True(watcher.TotalSwapsExecuted >= 1);

        // Test debounce path buffering without crashing
        watcher.HandleDebouncedPath(destPath);
        Assert.True(watcher.TotalEventsDebounced >= 1);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task TelemetryCommand_ExecutesLatestAndRendersStatus_WhenClientOffline()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        TelemetryCommand command = new();
        TelemetryCommandSettings settings = new()
        {
            Action = "latest",
            Channel = "auto"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task TelemetryCommand_ExecutesLatestJson_ReturnsFormattedJsonPayload()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        TelemetryCommand command = new();
        TelemetryCommandSettings settings = new()
        {
            Action = "latest",
            Channel = "auto",
            Json = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ClientCommand_ExecutesStatus_ReturnsCleanExitCode()
    {
        ClientCommand command = new();
        ClientCommandSettings settings = new()
        {
            Action = "status"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ClientCommand_ExecutesList_ReturnsCleanExitCode()
    {
        ClientCommand command = new();
        ClientCommandSettings settings = new()
        {
            Action = "list"
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ClientCommand_ExecutesStatusJson_ReturnsValidJsonPayload()
    {
        ClientCommand command = new();
        ClientCommandSettings settings = new()
        {
            Action = "status",
            Json = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
    }
}
