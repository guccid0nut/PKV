// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Adversarial challenger empirical test suite for Milestone 1: UI & Lua Hot-Reloading (kongor_hot_reload).
/// Formally challenges sliding debounce windowing under concurrent bursts, spaced burst isolation,
/// IPC command sequencing and fault injection, UTF-8 BOM sanitation, and inner-loop performance.
/// </summary>
public sealed class HotReloadAdversarialChallengerTests
{
    #region Challenge 1: Rapid Concurrent Event Burst (50 events in 10ms)

    [Fact]
    public async Task HotReloadDebounce_RapidConcurrentEventBurst_ConsolidatesToExactlyOneReload()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        ConcurrentQueue<(string Command, DateTimeOffset Time)> commandLog = new();

        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => commandLog.Enqueue((cmd, TimeProvider.System.GetUtcNow())))
            .ReturnsAsync("OK: Success");

        using HotReloadService service = new(mockIpc.Object);

        TaskCompletionSource<HotReloadResultDto> reloadTcs = new();
        service.OnReloadCompleted += res => reloadTcs.TrySetResult(res);

        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 300);

        const int eventCount = 50;
        Stopwatch burstStopwatch = Stopwatch.StartNew();

        // Concurrently dispatch 50 file change notifications across multiple threads
        Parallel.For(0, eventCount, new ParallelOptions { MaxDegreeOfParallelism = 10 }, i =>
        {
            service.NotifyFileChanged($"games/shared/ui/button_spec_{i}.interface");
        });

        burstStopwatch.Stop();
        burstStopwatch.ElapsedMilliseconds.Should().BeLessThan(2000, "50 concurrent events should be queued within 2000ms");

        // Await the sliding debounce window completion
        HotReloadResultDto result = await reloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(5.0));

        result.Success.Should().BeTrue();

        // Exactly one consolidated reload dispatch for all 50 concurrent events
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockIpc.Verify(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        service.TotalReloads.Should().Be(1, "Exactly one reload must be counted");
        service.TotalFileEvents.Should().Be(50, "All 50 concurrent file events must be captured");
        service.LastChangedFiles.Should().HaveCount(50, "All 50 unique modified file paths must be collected in the batch");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task HotReloadDebounce_ContinuousBurst_FiresOnlyAfterQuietPeriod()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        TaskCompletionSource<HotReloadResultDto> reloadTcs = new();
        service.OnReloadCompleted += res => reloadTcs.TrySetResult(res);

        // 800ms debounce window
        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: 800);

        // Fire 10 events spaced by 25ms
        // A true sliding window MUST NOT fire prematurely while new events keep arriving
        for (int i = 0; i < 10; i++)
        {
            service.NotifyFileChanged($"games/shared/ui/widget_stream_{i}.xml");
            await Task.Delay(25);
        }

        // Wait for debounce window to settle after the final event
        HotReloadResultDto result = await reloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(5.0));

        result.Success.Should().BeTrue();
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        service.TotalReloads.Should().Be(1);
        service.TotalFileEvents.Should().Be(10);
    }

    #endregion

    #region Challenge 2: Spaced Bursts Separated by > 150ms Trigger Independent Reloads

    [Fact]
    public async Task HotReloadDebounce_SpacedBursts_TriggerIndependentReloads()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        // Use 60ms debounce for fast deterministic multi-burst testing
        const int testDebounceMs = 60;
        service.StartWatching(customPaths: ["games/shared/ui/"], debounceMs: testDebounceMs);

        const int burstCount = 3;
        List<HotReloadResultDto> results = [];

        for (int burst = 1; burst <= burstCount; burst++)
        {
            TaskCompletionSource<HotReloadResultDto> burstTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<HotReloadResultDto> handler = res => burstTcs.TrySetResult(res);
            service.OnReloadCompleted += handler;

            // Fire 5 file events in this burst
            for (int i = 0; i < 5; i++)
            {
                service.NotifyFileChanged($"games/shared/ui/burst_{burst}_file_{i}.interface");
            }

            var res = await burstTcs.Task.WaitAsync(TimeSpan.FromSeconds(10.0));
            results.Add(res);
            service.OnReloadCompleted -= handler;

            // Separation pause between bursts: 90ms > testDebounceMs (60ms)
            if (burst < burstCount)
            {
                await Task.Delay(90);
            }
        }

        results.Should().HaveCount(burstCount);
        results.All(r => r.Success).Should().BeTrue();

        // Exactly 3 distinct reloads dispatched to the IPC bridge
        mockIpc.Verify(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        mockIpc.Verify(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        service.TotalReloads.Should().Be(3);
        service.TotalFileEvents.Should().Be(15);
    }

    #endregion

    #region Challenge 3: IGameIpcBridge Command Ordering & Sequence Verification

    [Fact]
    public async Task IpcCommandSequencing_StrictOrdering_ExecutesReloadInterfacesThenNexusBridgeDofile()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        List<string> orderedCommands = [];

        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => orderedCommands.Add(cmd))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        var result = await service.TriggerReloadAsync(["games/shared/ui/menu.interface"]);

        result.Success.Should().BeTrue();
        result.DispatchedCommands.Should().HaveCount(2);
        result.DispatchedCommands[0].Should().Be("ReloadInterfaces");
        result.DispatchedCommands[1].Should().Be("Script dofile('/ui/scripts/nexus_bridge.lua')");

        orderedCommands.Should().Equal("ReloadInterfaces", "Script dofile('/ui/scripts/nexus_bridge.lua')");
    }

    [Fact]
    public async Task IpcCommandSequencing_WhenFirstCommandFails_DoesNotExecuteSecondCommand()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        List<string> orderedCommands = [];

        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => orderedCommands.Add(cmd))
            .ThrowsAsync(new IOException("Pipe disconnected"));

        mockIpc.Setup(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => orderedCommands.Add(cmd))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        var result = await service.TriggerReloadAsync(["games/shared/ui/menu.interface"]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Pipe disconnected");
        orderedCommands.Should().Equal("ReloadInterfaces");
        mockIpc.Verify(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IpcCommandSequencing_WhenSecondCommandFails_CapturesFailureGracefully()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        List<string> orderedCommands = [];

        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReloadInterfaces", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => orderedCommands.Add(cmd))
            .ReturnsAsync("OK");

        mockIpc.Setup(i => i.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => orderedCommands.Add(cmd))
            .ThrowsAsync(new InvalidOperationException("Lua syntax error in nexus_bridge.lua"));

        using HotReloadService service = new(mockIpc.Object);
        var result = await service.TriggerReloadAsync(["games/shared/scripts/nexus_bridge.lua"]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Lua syntax error in nexus_bridge.lua");
        orderedCommands.Should().Equal("ReloadInterfaces", "Script dofile('/ui/scripts/nexus_bridge.lua')");
        result.CommandResponses.Should().ContainSingle().Which.Should().Be("OK");
    }

    #endregion

    #region Challenge 4: Edge Cases — BOM Stripping, Ignored Files, and Concurrency Hazards

    [Fact]
    public async Task HotReloadSanitization_Utf8BomPresent_StripsBomFromXmlAndInterfaceFiles()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        string tempFile = Path.Combine(Path.GetTempPath(), $"HotReloadBomTest_{Guid.NewGuid():N}.interface");
        try
        {
            // Write file starting with UTF-8 BOM bytes 0xEF, 0xBB, 0xBF
            byte[] bom = [0xEF, 0xBB, 0xBF];
            byte[] content = System.Text.Encoding.UTF8.GetBytes("<interface name=\"test\"></interface>");
            byte[] fileWithBom = [.. bom, .. content];
            await File.WriteAllBytesAsync(tempFile, fileWithBom);

            using HotReloadService service = new(mockIpc.Object);
            service.NotifyFileChanged(tempFile);

            // Verify BOM has been stripped from disk
            byte[] readBack = await File.ReadAllBytesAsync(tempFile);
            readBack.Should().Equal(content, "UTF-8 BOM header (0xEF, 0xBB, 0xBF) must be cleanly removed");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void HotReloadFilter_SpecialFilesModified_IgnoresWithoutTriggeringDebounce()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        string[] ignoredPaths =
        [
            "games/shared/ui/.hotreload",
            "games/shared/ui/menu.interface.tmp",
            "games/shared/ui/menu.interface.bak",
            "games/shared/ui/~lock.xml",
            "games/shared/ui/.git/index",
            "games/shared/ui/change.log",
            "games/shared/ui/traffic.ndjson"
        ];

        foreach (string path in ignoredPaths)
        {
            service.NotifyFileChanged(path);
        }

        service.TotalFileEvents.Should().Be(0, "Ignored files must not increment TotalFileEvents");
        service.GetStatus().LastChangedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task HotReloadConcurrency_ManualTriggerAndDebounce_SerializeCleanlyWithoutDeadlock()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(25); // Simulate in-engine processing time
                return "OK";
            });

        using HotReloadService service = new(mockIpc.Object);

        // Trigger manual reload concurrently while firing file changes
        var task1 = service.TriggerReloadAsync(["games/shared/ui/manual.xml"]);
        service.NotifyFileChanged("games/shared/ui/auto.xml");
        var task2 = service.TriggerReloadAsync(["games/shared/ui/manual2.xml"]);

        HotReloadResultDto[] results = await Task.WhenAll(task1, task2);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeTrue();
    }

    [Fact]
    public async Task HotReloadDebounce_InvalidOrNegativeValue_SafelyDefaultsTo150()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        using HotReloadService service = new(mockIpc.Object);

        service.StartWatching(debounceMs: -10);
        service.DebounceMs.Should().Be(150);

        service.StartWatching(debounceMs: 0);
        service.DebounceMs.Should().Be(150);

        HotReloadHandler handler = new(service);
        using var req = JsonDocument.Parse("""{ "action": "watch", "debounceMs": -50 }""");
        var res = await handler.HandleAsync(req.RootElement);
        res.IsError.Should().BeFalse();
        service.DebounceMs.Should().Be(150);
    }

    [Fact]
    public async Task HotReloadAction_SynonymsAndCaseInsensitivity_ResolveCorrectly()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        // "START" synonym for "watch"
        using var startReq = JsonDocument.Parse("""{ "action": "START" }""");
        var startRes = await handler.HandleAsync(startReq.RootElement);
        startRes.IsError.Should().BeFalse();
        service.IsWatching.Should().BeTrue();

        // "RELOAD" synonym for "trigger"
        using var reloadReq = JsonDocument.Parse("""{ "action": "RELOAD" }""");
        var reloadRes = await handler.HandleAsync(reloadReq.RootElement);
        reloadRes.IsError.Should().BeFalse();

        // "STOP" synonym for "unwatch"
        using var stopReq = JsonDocument.Parse("""{ "action": "STOP" }""");
        var stopRes = await handler.HandleAsync(stopReq.RootElement);
        stopRes.IsError.Should().BeFalse();
        service.IsWatching.Should().BeFalse();
    }

    #endregion

    #region Challenge 5: Performance Threshold (< 2.0s Execution) & Zero Desktop GUI Windows

    [Fact]
    public async Task HotReloadThroughput_HighThroughputInnerLoop_ExecutesUnder2SecondsAndSpawnsZeroGuiWindows()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        using HotReloadService service = new(mockIpc.Object);
        HotReloadHandler handler = new(service);

        Stopwatch sw = Stopwatch.StartNew();

        // Execute 50 MCP queries in sequence
        for (int i = 0; i < 50; i++)
        {
            using var req = JsonDocument.Parse("""{ "action": "status" }""");
            var result = await handler.HandleAsync(req.RootElement);
            result.IsError.Should().BeFalse();
        }

        sw.Stop();

        // Fast inner loop requirement: 50 MCP queries must complete in < 2.0s
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "50 inner-loop queries must execute well under the 2.0s threshold");

        // Zero external GUI process spawning
        Process[] guiProcesses = Process.GetProcessesByName("ReplayStudio.GUI");
        guiProcesses.Should().BeEmpty("Automated test suites must never spawn external desktop GUI windows");
    }

    #endregion
}
