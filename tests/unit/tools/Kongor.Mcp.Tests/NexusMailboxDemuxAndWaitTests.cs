// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Phase P0.3:
/// - NexusMailbox concurrent out-of-order execution without _sendLock starvation.
/// - ConsoleLogDemuxService thread-safe line demultiplexing.
/// - WaitForHandler SHM fast-path (<0.05 ms, zero Lua mailbox I/O).
/// - MCP-196 (timeout_ms parameter alias).
/// - MCP-198 (condition: "ipc_ready" native IPC connectivity).
/// - MCP-245 (condition: "lua" rejection of [ERROR], Error:, ERR_, Lua Error, false, nil, 0).
/// </summary>
public sealed class NexusMailboxDemuxAndWaitTests : IDisposable
{
    private readonly string _testMailboxDir;

    public NexusMailboxDemuxAndWaitTests()
    {
        _testMailboxDir = Path.Combine(Path.GetTempPath(), "nexus_demux_test_" + Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(_testMailboxDir))
        {
            Directory.CreateDirectory(_testMailboxDir);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testMailboxDir))
            {
                Directory.Delete(_testMailboxDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task NexusMailbox_TenConcurrentRequests_CompleteOutOfOrderWithoutSerializingOnSlowRequest()
    {
        // protects: Mailbox IPC concurrency; _sendLock must not be held across the response polling loop.
        // fails_when: _sendLock is held across the response loop, forcing all 10 requests to serialize sequentially behind request #0.
        // seam: NexusMailboxIpcBridge + ConsoleLogDemuxService
        string consoleLog = Path.Combine(_testMailboxDir, "console.log");
        await File.WriteAllTextAsync(consoleLog, "[12:00:00] Initial engine log\n", new UTF8Encoding(false));

        var mockSignalBridge = new Mock<IGameIpcBridge>();
        mockSignalBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockSignalBridge.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[OK]");

        await using var bridge = new NexusMailboxIpcBridge(
            mailboxDir: _testMailboxDir,
            signalBridge: mockSignalBridge.Object);
        bridge.ForceConnected = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completedReqs = new List<(int RequestIndex, DateTimeOffset CompletedAt)>();
        var completedLock = new object();
        var logFileLock = new object();

        // Background engine simulator:
        // Request #0 is slow (delays 250ms). Requests #1..#9 respond fast (within ~5ms) in reverse order.
        var simTask = Task.Run(async () =>
        {
            var processedIds = new HashSet<int>();
            while (!cts.IsCancellationRequested && processedIds.Count < 10)
            {
                string[] reqFiles;
                try
                {
                    reqFiles = Directory.GetFiles(_testMailboxDir, "req_*.lua");
                }
                catch
                {
                    await Task.Delay(5, cts.Token);
                    continue;
                }

                foreach (string file in reqFiles)
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("req_current.lua", StringComparison.OrdinalIgnoreCase)) continue;

                    string idPart = fileName.Replace("req_", "").Replace(".lua", "");
                    if (!int.TryParse(idPart, out int reqId) || processedIds.Contains(reqId)) continue;

                    // Read command to identify which request this is (using FileShare.ReadWrite)
                    string content = "";
                    for (int retry = 0; retry < 10; retry++)
                    {
                        try
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(fs, Encoding.UTF8);
                            content = await reader.ReadToEndAsync(cts.Token);
                            if (!string.IsNullOrWhiteSpace(content)) break;
                        }
                        catch
                        {
                            await Task.Delay(5, cts.Token);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(content)) continue;

                    processedIds.Add(reqId);

                    int cmdIdx = content.IndexOf("test_cmd_");
                    int reqIndex = -1;
                    if (cmdIdx >= 0)
                    {
                        int end = content.IndexOf('"', cmdIdx);
                        if (end > cmdIdx)
                        {
                            int.TryParse(content.AsSpan(cmdIdx + 9, end - cmdIdx - 9), out reqIndex);
                        }
                    }

                    // Dispatch response async so slow request #0 does not block fast requests
                    _ = Task.Run(async () =>
                    {
                        if (reqIndex == 0)
                        {
                            // Slow request delays 250ms
                            await Task.Delay(250, cts.Token);
                        }
                        else
                        {
                            // Fast requests respond almost immediately
                            await Task.Delay(10, cts.Token);
                        }

                        string line = $"[12:00:01] [NEXUS_MAILBOX_RES] {reqId} 1/1 {{\"id\":{reqId},\"type\":\"command\",\"success\":true,\"result\":\"Echo_test_cmd_{reqIndex}\",\"timestamp\":12345}}\n";
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            try
                            {
                                lock (logFileLock)
                                {
                                    using var logFs = new FileStream(consoleLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                    using var logWriter = new StreamWriter(logFs, new UTF8Encoding(false));
                                    logWriter.Write(line);
                                    logWriter.Flush();
                                }
                                break;
                            }
                            catch
                            {
                                await Task.Delay(5, cts.Token);
                            }
                        }
                    }, cts.Token);
                }

                await Task.Delay(5, cts.Token);
            }
        }, cts.Token);

        // Launch 10 concurrent requests simultaneously
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            string res = await bridge.ExecuteCommandAsync($"test_cmd_{i}", timeoutMs: 12000, cancellationToken: cts.Token);
            lock (completedLock)
            {
                completedReqs.Add((i, DateTimeOffset.UtcNow));
            }
            return (Index: i, Result: res);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        await simTask;

        // Verify all 10 completed successfully with expected results
        results.Should().HaveCount(10);
        foreach (var r in results)
        {
            r.Result.Should().Be($"Echo_test_cmd_{r.Index}");
        }

        // Prove out-of-order execution:
        // Because request #0 was delayed 250ms and requests #1..#9 were delayed only ~10ms,
        // multiple fast requests must have completed BEFORE request #0 completed!
        int indexOfSlowReqCompletion = completedReqs.FindIndex(c => c.RequestIndex == 0);
        indexOfSlowReqCompletion.Should().BeGreaterThan(0,
            "fast requests 1..9 must complete before slow request 0 finishes, proving non-serialized out-of-order concurrency!");
    }

    [Fact]
    public async Task WaitForHandler_IpcReady_MCP198_SatisfiesImmediatelyWithoutTouchingLuaMailbox()
    {
        // protects: MCP-198; 'condition: "ipc_ready"' verifies Named Pipe/SHM connectivity immediately without Lua round-trip.
        // fails_when: 'ipc_ready' is rejected or dispatches redundant Lua mailbox evaluation.
        // seam: WaitForHandler + IGameIpcBridge
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.SetupGet(x => x.PipeName).Returns("kongor-native-client");
        mockBridge.Setup(x => x.PingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(120L); // 120 us ping

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);

        using var argsDoc = JsonDocument.Parse("""
        {
            "condition": "ipc_ready",
            "timeoutMs": 2000
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);
        result.IsError.Should().BeFalse(result.FirstText);

        using var outDoc = JsonDocument.Parse(result.FirstText);
        outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        outDoc.RootElement.GetProperty("condition").GetString().Should().Be("ipc_ready");

        // Verify Ping was called but ExecuteLuaAsync was NEVER called
        mockBridge.Verify(x => x.PingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        mockBridge.Verify(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForHandler_TimeoutMsAlias_MCP196_HonorsTimeoutMsAndTimeoutSnakeCase()
    {
        // protects: MCP-196; 'timeout_ms' parameter alias parsed cleanly alongside 'timeoutMs'.
        // fails_when: 'timeout_ms' is ignored, falling back to default timeout.
        // seam: WaitForHandler.ResolveTimeoutMs + HandleAsync
        using var snakeCaseDoc = JsonDocument.Parse("""{"timeout_ms": 14200}""");
        WaitForHandler.ResolveTimeoutMs(snakeCaseDoc.RootElement, "engine_ready").Should().Be(14200);

        using var camelCaseDoc = JsonDocument.Parse("""{"timeoutMs": 18500}""");
        WaitForHandler.ResolveTimeoutMs(camelCaseDoc.RootElement, "engine_ready").Should().Be(18500);

        using var maxWaitDoc = JsonDocument.Parse("""{"max_wait_ms": 9900}""");
        WaitForHandler.ResolveTimeoutMs(maxWaitDoc.RootElement, "engine_ready").Should().Be(9900);

        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.PingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(50L);

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);
        using var execDoc = JsonDocument.Parse("""{"condition": "ipc_ready", "timeout_ms": 4500}""");
        var result = await handler.HandleAsync(execDoc.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var outDoc = JsonDocument.Parse(result.FirstText);
        outDoc.RootElement.GetProperty("timeoutMs").GetInt32().Should().Be(4500);
    }

    [Fact]
    public async Task WaitForHandler_ShmFastPath_SatisfiesGamePhaseMapLoadedAndActiveMatchDirectly()
    {
        // protects: Zero-lock fast-path satisfying game_phase, map_loaded, and active_match from SHM/GetEngineStateAsync with zero Lua I/O.
        // fails_when: WaitForHandler attempts Lua mailbox dispatch for predicates queryable directly from EngineState.
        // seam: WaitForHandler + IGameIpcBridge.GetEngineStateAsync
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                HostFrame = 500,
                GamePhase = 5,
                CanonicalGamePhase = 5,
                GamePhaseName = "In-Game",
                CurrentMap = "caldavar",
                IsActiveMatch = true,
                ActiveMatchId = 123456
            });

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);

        // 1. game_phase with integer target
        using (var doc = JsonDocument.Parse("""{"condition": "game_phase", "target": "5", "timeoutMs": 1000}"""))
        {
            var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
            res.IsError.Should().BeFalse(res.FirstText);
            using var outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        }

        // 2. map_loaded with map target
        using (var doc = JsonDocument.Parse("""{"condition": "map_loaded", "target": "caldavar", "timeoutMs": 1000}"""))
        {
            var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
            res.IsError.Should().BeFalse(res.FirstText);
            using var outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        }

        // 3. active_match
        using (var doc = JsonDocument.Parse("""{"condition": "active_match", "timeoutMs": 1000}"""))
        {
            var res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
            res.IsError.Should().BeFalse(res.FirstText);
            using var outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        }

        // Verify zero Lua mailbox dispatches occurred
        mockBridge.Verify(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForHandler_ShmFastPath_FrameAdvance_TracksDeltaAcrossPolls()
    {
        // protects: 'frame_advance' predicate evaluates frame delta directly from SHM HostFrame.
        // fails_when: 'frame_advance' ignores frame advances or requires Lua round-trip.
        // seam: WaitForHandler + IGameIpcBridge.GetEngineStateAsync
        int callCount = 0;
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new EngineStateDto
                {
                    HostFrame = 100 + callCount // 101 on poll 1, 102 on poll 2
                };
            });

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);
        using var doc = JsonDocument.Parse("""{"condition": "frame_advance", "target": "1", "intervalMs": 25, "timeoutMs": 1000}""");

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        result.IsError.Should().BeFalse(result.FirstText);

        using var outDoc = JsonDocument.Parse(result.FirstText);
        outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        mockBridge.Verify(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForHandler_FrameStallOptimization_SkipsRedundantLuaPollingWhenFrameUnchanged()
    {
        // protects: Lua/UI polling throttle when engine main thread is stalled or busy loading .s2z.
        // fails_when: WaitForHandler blindly bombards the client with redundant Lua mailbox polls while the main thread frame has not advanced.
        // seam: WaitForHandler + IGameIpcBridge
        int luaExecutionCount = 0;
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                HostFrame = 200, // HostFrame remains 200 (stalled / loading)
                MainThreadBusy = false
            });
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                luaExecutionCount++;
                return "false"; // Condition not yet satisfied
            });

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);
        using var doc = JsonDocument.Parse("""
        {
            "condition": "lua",
            "target": "SomeModule.IsReady()",
            "intervalMs": 25,
            "timeoutMs": 150
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        result.IsError.Should().BeTrue(); // Times out because lua returns false

        // In 150ms with 25ms interval, without optimization there would be ~6 Lua executions.
        // With frame stall optimization, after poll 1 observes HostFrame 200, polls 2..N skip Lua execution!
        luaExecutionCount.Should().Be(1,
            "redundant Lua calls must be skipped when HostFrame has not advanced between poll ticks!");
    }

    [Theory]
    [InlineData("[ERROR] attempt to index global 'NonExistentGlobal' (a nil value)", false)]
    [InlineData("Error: attempt to index a nil value", false)]
    [InlineData("ERR_INVALID_DISPATCH_CALL", false)]
    [InlineData("Lua Error: unexpected symbol near ')'", false)]
    [InlineData("[LUA_ERR] Chunk execution failed", false)]
    [InlineData("[SECURITY ERROR] Blocked unauthorized Lua execution", false)]
    [InlineData("[OK] Command queued to K2 main thread ring", false)]
    [InlineData("{\"success\":false,\"error\":\"Failed\"}", false)]
    [InlineData("false", false)]
    [InlineData("nil", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    [InlineData("ready", true)]
    [InlineData("1", true)]
    public async Task WaitForHandler_ConditionLua_MCP245_RejectsErrorPrefixesAndNonTruthyStrings(
        string luaResponse,
        bool expectedSatisfied)
    {
        // protects: MCP-245; condition: "lua" must treat [ERROR], Error:, ERR_, Lua Error, false, nil, 0, and queued ACKs as satisfied: false.
        // fails_when: non-empty error strings like "[ERROR] ..." or "Error: ..." are evaluated as truthy satisfied.
        // seam: WaitForHandler + IGameIpcBridge
        var mockBridge = new Mock<IGameIpcBridge>();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(luaResponse);

        var handler = new WaitForHandler(taoClient: null, gameIpcBridge: mockBridge.Object);
        using var doc = JsonDocument.Parse("""
        {
            "condition": "lua",
            "target": "SomeGlobal.Flag",
            "timeoutMs": 200
        }
        """);

        var result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        using var outDoc = JsonDocument.Parse(result.FirstText);
        bool actualSatisfied = outDoc.RootElement.GetProperty("satisfied").GetBoolean();

        actualSatisfied.Should().Be(expectedSatisfied,
            $"Lua response '{luaResponse}' should result in satisfied: {expectedSatisfied}");

        if (!expectedSatisfied && (luaResponse.StartsWith("[ERROR]") || luaResponse.StartsWith("Error:") || luaResponse.StartsWith("ERR_") || luaResponse.StartsWith("Lua Error")))
        {
            var observed = outDoc.RootElement.GetProperty("observedState");
            observed.GetProperty("isEvalError").GetBoolean().Should().BeTrue();
        }
    }
}
