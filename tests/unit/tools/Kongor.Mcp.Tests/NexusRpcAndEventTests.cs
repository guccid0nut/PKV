// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NexusRpcAndEventTests
{
    [Fact]
    public async Task NexusEventCoordinator_ExecuteRpcAsync_Success()
    {
        NexusEventCoordinator? coordinator = null;
        coordinator = new NexusEventCoordinator(async (cmd, timeout, ct) =>
        {
            cmd.Should().Contain("Nexus.RPC(1, 'match.start_practice'");
            // Simulate in-engine response
            await Task.Yield();
            coordinator!.HandleLogStream("[NEXUS_RPC_RES] {\"id\": 1, \"method\": \"match.start_practice\", \"success\": true, \"result\": {\"started\": true}}");
            return "ok";
        });

        JsonElement result = await coordinator.ExecuteRpcAsync<JsonElement>("match.start_practice", new { hero = "Hero_Pyromancer" }, 15000);

        result.GetProperty("started").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NexusEventCoordinator_ExecuteRpcAsync_Error_ThrowsInvalidOperationException()
    {
        NexusEventCoordinator? coordinator = null;
        coordinator = new NexusEventCoordinator(async (cmd, timeout, ct) =>
        {
            await Task.Yield();
            coordinator!.HandleLogStream("[NEXUS_RPC_RES] {\"id\": 1, \"method\": \"bad_method\", \"success\": false, \"error\": \"Method not found: bad_method\"}");
            return "ok";
        });

        Func<Task> act = async () => await coordinator.ExecuteRpcAsync<JsonElement>("bad_method", null, 15000);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Method not found: bad_method*");
    }

    [Fact]
    public async Task NexusEventCoordinator_WaitForEventAsync_ResolvesWhenEventReceived()
    {
        NexusEventCoordinator coordinator = new((cmd, timeout, ct) => Task.FromResult("ok"));

        Task<JsonElement> waitTask = coordinator.WaitForEventAsync("MATCH_READY", 15000);

        coordinator.HandleLogStream("[NEXUS_EVENT] MATCH_READY|{\"hero\": \"Hero_Pyromancer\", \"ready\": true}");

        JsonElement payload = await waitTask;
        payload.GetProperty("ready").GetBoolean().Should().BeTrue();
        payload.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
    }

    [Fact]
    public async Task NexusEventCoordinator_WaitForEventAsync_TimesOutWhenNoEvent()
    {
        NexusEventCoordinator coordinator = new((cmd, timeout, ct) => Task.FromResult("ok"));

        Func<Task> act = async () => await coordinator.WaitForEventAsync("NON_EXISTENT_EVENT", 50);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task NexusEventCoordinator_Reset_FailsPendingTasksWithIOException()
    {
        NexusEventCoordinator coordinator = new((cmd, timeout, ct) => Task.FromResult("ok"));

        Task<JsonElement> waitTask = coordinator.WaitForEventAsync("MATCH_READY", 5000);

        coordinator.Reset();

        Func<Task> act = async () => await waitTask;
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public void CrashDumpAnalyzeHandler_NormalizeAslrToRva_CalculatesRva()
    {
        (string mod1, string rva1, string formatted1) = CrashDumpAnalyzeHandler.NormalizeAslrToRva("k2_x64.dll+0x3A20");
        mod1.Should().Be("k2_x64.dll");
        rva1.Should().Be("0x3A20");
        formatted1.Should().Be("k2_x64.dll+0x3A20");

        (string mod2, string rva2, string formatted2) = CrashDumpAnalyzeHandler.NormalizeAslrToRva("Fault address: 0x00007FFB33201402 in cgame_x64.dll (Base: 0x00007FFB33200000)");
        mod2.Should().Be("cgame_x64.dll");
        rva2.Should().Be("0x1402");
        formatted2.Should().Be("cgame_x64.dll+0x1402");
    }

    [Fact]
    public async Task CrashDumpAnalyzeHandler_Analyze_ContainsGhidraTriangulation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_nexus_dump_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dumpPath = Path.Combine(tempDir, "sample.dmp");
        try
        {
            using (FileStream fs = File.Create(dumpPath))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(0x504D444DU); // 'MDMP'
                writer.Write(0x0000A793U); // Version
                writer.Write(1U);          // StreamCount = 1
                writer.Write(32U);         // StreamDirectoryRva = 32
                writer.Write(0U);
                writer.Write(0U);
                writer.Write(0UL);

                writer.Write(6U);
                writer.Write(48U);
                writer.Write(44U);

                writer.Write(100U);
                writer.Write(0U);
                writer.Write(0xC0000005U);
                writer.Write(0U);
                writer.Write(0UL);
                writer.Write(0x00007FF700001402UL);
                writer.Write(2U);
                writer.Write(0U);
                writer.Write(0UL);
                writer.Write(0x18UL);
            }

            CrashDumpAnalyzeHandler handler = new CrashDumpAnalyzeHandler(searchDirectory: tempDir);
            using JsonDocument argDoc = JsonDocument.Parse($$"""{"action": "analyze", "dumpPath": "{{dumpPath.Replace("\\", "/")}}"}""");
            JsonElement args = argDoc.RootElement;

            ToolCallResult result = await handler.HandleAsync(args);

            result.IsError.Should().BeFalse();
            using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
            JsonElement root = doc.RootElement;

            root.TryGetProperty("ghidraTriangulation", out JsonElement ghidraElem).Should().BeTrue();
            ghidraElem.GetProperty("suggestedTool").GetString().Should().Be("ghidra");
            ghidraElem.GetProperty("suggestedFunction").GetString().Should().Be("decompile_function");
            ghidraElem.GetProperty("command").GetString().Should().Contain("decompile_function");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string GetJsonText(ToolCallResult result) => result.FirstText;

    [Fact]
    public void MatchLaunchHandler_BuildMatchLaunchArguments_IncludesVirtualHeadlessPositioning()
    {
        IReadOnlyList<string> args = MatchLaunchHandler.BuildMatchLaunchArguments(
            map: "caldavar",
            hero: "Hero_Pyromancer",
            mode: "practice",
            bots: 0,
            cheats: true,
            headless: true,
            resolution: "1280x720",
            fullscreen: false,
            maxFps: 120,
            masterServer: "127.0.0.1:55555");

        string joined = string.Join(" ", args);
        joined.Should().Contain("+Set vid_pos -32000 -32000");
        joined.Should().Contain("+Set sound_disable true");
    }
}
