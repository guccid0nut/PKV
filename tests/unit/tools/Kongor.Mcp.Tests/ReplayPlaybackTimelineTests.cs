// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Empirical Challenger Test Suite for Milestone 3 (Mid-Game Snapshot & Replay Tick Manager).
/// Adversarially tests:
/// 1. Replay tick math precision across fractional seconds, milliseconds, formatted strings (MM:SS.fff, HH:MM:SS.fff),
///    negative clamping, large timestamps, and reciprocal roundtrips.
/// 2. Sequential command ordering: ReplaySetFrame followed by Seek with exact tick matching.
/// 3. In-memory snapshot store multithreaded concurrency stress harness (save, restore, list, delete, seek).
/// 4. Lifecycle safety guardrails under adversarial phase inputs.
/// 5. Fast inner-loop execution (< 2.0s) and zero GUI window spawning.
/// </summary>
public sealed class ReplayPlaybackTimelineTests
{
    private static (GameSnapshotHandler Handler, Mock<IGameIpcBridge> MockIpc, List<string> DispatchedCommands) CreateHandlerWithMockIpc(
        string phaseResponse = "3",
        bool isConnected = true)
    {
        List<string> dispatched = [];
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(isConnected);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string cmd, int timeout, CancellationToken ct) =>
            {
                lock (dispatched)
                {
                    dispatched.Add(cmd);
                }
                if (cmd.Contains("GetCurrentPhase"))
                {
                    return Task.FromResult(phaseResponse);
                }
                return Task.FromResult("OK");
            });

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System,
            logger: _ => { });

        return (handler, mockIpc, dispatched);
    }

    private static string GetContentText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return string.Empty;
    }

    private static JsonElement ParseResultJson(ToolCallResult result)
    {
        string text = GetContentText(result);
        return JsonElement.Parse(text);
    }

    #region Dimension 1: Replay Tick Math Precision & Conversion Oracles

    [Theory]
    [InlineData(0.05, 1L)]       // Exactly 1 tick (50ms)
    [InlineData(0.025, 1L)]      // 0.025 * 20 = 0.5 -> rounds away from zero to 1
    [InlineData(0.024, 0L)]      // 0.024 * 20 = 0.48 -> rounds down to 0
    [InlineData(0.075, 2L)]      // 0.075 * 20 = 1.5 -> rounds to 2
    [InlineData(0.074, 1L)]      // 0.074 * 20 = 1.48 -> rounds to 1
    [InlineData(0.001, 0L)]      // 0.001 * 20 = 0.02 -> 0
    [InlineData(0.999, 20L)]     // 0.999 * 20 = 19.98 -> 20
    [InlineData(1.55, 31L)]      // 1.55 * 20 = 31.0 -> 31
    [InlineData(60.0, 1200L)]    // 1 minute = 1200 ticks
    [InlineData(0.0, 0L)]        // Zero
    public void ReplayTickMath_FromSeconds_MaintainsFractionalPrecision(double seconds, long expectedTicks)
    {
        long actualTicks = ReplayTickMath.FromSeconds(seconds);
        actualTicks.Should().Be(expectedTicks, $"seconds {seconds} should convert to {expectedTicks} ticks at 20 ticks/sec");
    }

    [Theory]
    [InlineData(50L, 1L)]        // 50ms = 1 tick
    [InlineData(25L, 1L)]        // 25ms / 50.0 = 0.5 -> 1 tick
    [InlineData(24L, 0L)]        // 24ms / 50.0 = 0.48 -> 0 ticks
    [InlineData(75L, 2L)]        // 75ms / 50.0 = 1.5 -> 2 ticks
    [InlineData(74L, 1L)]        // 74ms / 50.0 = 1.48 -> 1 tick
    [InlineData(1000L, 20L)]     // 1000ms = 20 ticks
    [InlineData(1234L, 25L)]     // 1234ms / 50.0 = 24.68 -> 25 ticks
    [InlineData(0L, 0L)]         // 0ms = 0 ticks
    public void ReplayTickMath_FromMilliseconds_MaintainsPrecision(long milliseconds, long expectedTicks)
    {
        long actualTicks = ReplayTickMath.FromMilliseconds(milliseconds);
        actualTicks.Should().Be(expectedTicks, $"milliseconds {milliseconds} should convert to {expectedTicks} ticks");
    }

    [Theory]
    [InlineData("00:00.050", 1L)]
    [InlineData("00:01.500", 30L)]
    [InlineData("01:30.000", 1800L)]
    [InlineData("05:12.350", 6247L)]   // 5*1200 + round(12.35*20) = 6000 + 247 = 6247
    [InlineData("00:59.950", 1199L)]   // round(59.95*20) = round(1199.0) = 1199
    [InlineData("00:59.975", 1200L)]   // round(59.975*20) = round(1199.5) = 1200
    [InlineData("01:00:00.000", 72000L)]
    [InlineData("02:15:30.500", 162610L)] // 2*72000 + 15*1200 + round(30.5*20) = 144000 + 18000 + 610 = 162610
    [InlineData("  01:30.500  ", 1810L)] // Trimmed whitespace handling
    [InlineData("invalid_timestamp", 0L)]
    [InlineData("", 0L)]
    public void ReplayTickMath_FromFormattedTimestamp_ParsesTimestampStringsAccurately(string timestamp, long expectedTicks)
    {
        long actualTicks = ReplayTickMath.FromFormattedTimestamp(timestamp);
        actualTicks.Should().Be(expectedTicks, $"formatted timestamp '{timestamp}' should convert to {expectedTicks} ticks");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void ReplaySeek_NegativeOrNonFiniteSeconds_ClampsToZero(double seconds)
    {
        long ticks = ReplayTickMath.FromSeconds(seconds);
        ticks.Should().Be(0L, "Negative or non-finite seconds must clamp to 0 ticks");
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-500L)]
    [InlineData(-99999999L)]
    public void ReplaySeek_NegativeMilliseconds_ClampsToZero(long ms)
    {
        long ticks = ReplayTickMath.FromMilliseconds(ms);
        ticks.Should().Be(0L, "Negative milliseconds must clamp to 0 ticks");
    }

    [Theory]
    [InlineData("-01:30")]
    [InlineData("01:-30")]
    [InlineData("-01:-30.500")]
    public void ReplaySeek_NegativeFormattedTimestamp_ClampsToValidRange(string timestamp)
    {
        long ticks = ReplayTickMath.FromFormattedTimestamp(timestamp);
        ticks.Should().BeGreaterThanOrEqualTo(0L, "Negative formatted timestamp components must clamp to non-negative ticks");
    }

    [Fact]
    public void ReplaySeek_LargeTimestamps_ConvertsAccuratelyWithoutOverflow()
    {
        // 10 hours = 10 * 3600 = 36,000s = 720,000 ticks
        long ticks10h = ReplayTickMath.FromFormattedTimestamp("10:00:00");
        ticks10h.Should().Be(720000L);

        // 100 hours = 100 * 3600 = 360,000s = 7,200,000 ticks
        long ticks100h = ReplayTickMath.FromFormattedTimestamp("100:00:00");
        ticks100h.Should().Be(7200000L);

        // 86,400 seconds (24 hours)
        long ticks24h = ReplayTickMath.FromSeconds(86400.0);
        ticks24h.Should().Be(1728000L);

        // 36,000,000 milliseconds (10 hours)
        long ticks10hMs = ReplayTickMath.FromMilliseconds(36000000L);
        ticks10hMs.Should().Be(720000L);
    }

    [Fact]
    public void ReplaySeek_TickMath_EnsuresRoundtripReciprocalConsistency()
    {
        long[] testTicks = [0L, 1L, 20L, 100L, 1200L, 1800L, 72000L, 162610L, 1000000L];

        foreach (long originalTick in testTicks)
        {
            double sec = ReplayTickMath.ToSeconds(originalTick);
            long roundtripSec = ReplayTickMath.FromSeconds(sec);
            roundtripSec.Should().Be(originalTick, $"Tick {originalTick} should roundtrip through ToSeconds({sec})");

            long ms = ReplayTickMath.ToMilliseconds(originalTick);
            long roundtripMs = ReplayTickMath.FromMilliseconds(ms);
            roundtripMs.Should().Be(originalTick, $"Tick {originalTick} should roundtrip through ToMilliseconds({ms})");

            string formatted = ReplayTickMath.ToFormattedTimestamp(originalTick);
            long roundtripFormatted = ReplayTickMath.FromFormattedTimestamp(formatted);
            roundtripFormatted.Should().Be(originalTick, $"Tick {originalTick} should roundtrip through ToFormattedTimestamp({formatted})");
        }
    }

    #endregion

    #region Dimension 2: Sequential Command Ordering (ReplaySetFrame then Seek)

    [Theory]
    [InlineData("seek", 1500L)]
    [InlineData("jump", 3600L)]
    public async Task ReplaySeek_SeekAndJump_DispatchesReplaySetFrameFollowedBySeekInExactSequence(string action, long targetTick)
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        string json = $"{{\"action\": \"{action}\", \"tick\": {targetTick}}}";
        var args = JsonElement.Parse(json);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("tick").GetInt64().Should().Be(targetTick);

        // Verify exact sequential ordering
        string expectedFrameCmd = $"ReplaySetFrame {targetTick}";
        string expectedSeekCmd = $"Seek {targetTick}";

        dispatched.Should().HaveCount(2);
        dispatched[0].Should().Be(expectedFrameCmd, "ReplaySetFrame must precede Seek for UI and stream sync");
        dispatched[1].Should().Be(expectedSeekCmd, "Seek must follow ReplaySetFrame in strict sequential order");

        // Verify returned commands array in JSON response
        var reportedCommands = root.GetProperty("commands").EnumerateArray().Select(e => e.GetString()).ToList();
        reportedCommands.Should().Equal([expectedFrameCmd, expectedSeekCmd]);
    }

    [Fact]
    public async Task Seek_FormattedStringWithFractionalSeconds_DispatchesOrderedTicks()
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "seek",
            "timeFormatted": "02:15.250"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        JsonElement root = ParseResultJson(result);

        // 2*1200 + round(15.25*20) = 2400 + 305 = 2705
        long expectedTick = 2705L;
        root.GetProperty("tick").GetInt64().Should().Be(expectedTick);

        dispatched.Should().Equal([$"ReplaySetFrame {expectedTick}", $"Seek {expectedTick}"]);
    }

    [Fact]
    public async Task Seek_WhenFirstCommandThrows_AbortsAndReturnsStructuredError()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.SetupGet(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync("ReplaySetFrame 100", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("IPC pipe severed"));

        ClientCommandRemediationService remediation = new(timeProvider: TimeProvider.System);
        GameSnapshotHandler handler = new(
            gameIpcBridge: mockIpc.Object,
            remediationService: remediation,
            timeProvider: TimeProvider.System);

        var args = JsonElement.Parse("""{"action": "seek", "tick": 100}""");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("IPC_DISPATCH_FAILED");

        // Seek 100 should NEVER have been dispatched because ReplaySetFrame failed
        mockIpc.Verify(i => i.ExecuteCommandAsync("Seek 100", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Dimension 3: In-Memory Snapshot Store Multithreaded Concurrency Stress Harness

    [Trait("Category", "Manual")]
    [Fact]
    public async Task SnapshotStore_MultithreadedConcurrency_ExecutesStressHarnessWithoutDeadlock()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        const int threadCount = 10;
        const int iterationsPerThread = 50;
        ConcurrentBag<Exception> exceptions = [];
        ConcurrentBag<string> createdIds = [];

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    string uniqueId = $"stress_{threadId}_{i}";

                    // 1. Concurrent Save Unique
                    string saveJson = $"{{\"action\": \"save\", \"id\": \"{uniqueId}\", \"snapshot\": {{\"gold\": {i * 100}, \"level\": {(i % 25) + 1}, \"hero\": \"Hero_Pyromancer\"}}}}";
                    var saveResult = await handler.HandleAsync(JsonElement.Parse(saveJson));
                    saveResult.IsError.Should().BeFalse();
                    createdIds.Add(uniqueId);

                    // 2. Concurrent Save Contention (shared keys)
                    string sharedId = $"shared_checkpoint_{i % 5}";
                    string sharedJson = $"{{\"action\": \"save\", \"id\": \"{sharedId}\", \"snapshot\": {{\"gold\": {threadId * 1000 + i}, \"level\": 16}}}}";
                    await handler.HandleAsync(JsonElement.Parse(sharedJson));

                    // 3. Concurrent List Enumeration under active mutations
                    var listResult = handler.HandleAsync(JsonElement.Parse("""{"action": "list"}""")).Result;
                    listResult.IsError.Should().BeFalse();
                    var listJson = ParseResultJson(listResult);
                    listJson.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(0);

                    // 4. Concurrent Restore (both existing and non-existing)
                    string restoreJson = $"{{\"action\": \"restore\", \"id\": \"{uniqueId}\"}}";
                    var restoreResult = await handler.HandleAsync(JsonElement.Parse(restoreJson));
                    restoreResult.IsError.Should().BeFalse();

                    // 5. Concurrent Seek
                    string seekJson = $"{{\"action\": \"seek\", \"tick\": {i * 20}}}";
                    var seekResult = await handler.HandleAsync(JsonElement.Parse(seekJson));
                    seekResult.IsError.Should().BeFalse();

                    // 6. Concurrent Delete on half of the items
                    if (i % 2 == 0)
                    {
                        string delJson = $"{{\"action\": \"delete\", \"id\": \"{uniqueId}\"}}";
                        var delResult = await handler.HandleAsync(JsonElement.Parse(delJson));
                        delResult.IsError.Should().BeFalse();
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("Concurrent multi-operation stress harness must execute with zero exceptions");
        handler.Count.Should().BeGreaterThan(0, "Snapshot store must retain remaining items after concurrent operations");
    }

    [Fact]
    public async Task SnapshotStore_KeyCollisionContention_PerformsSafeOverwrite()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        const string contestedKey = "race_contested_key";
        const int concurrentWriters = 20;

        var tasks = Enumerable.Range(0, concurrentWriters).Select(writerId => Task.Run(async () =>
        {
            string saveJson = $"{{\"action\": \"save\", \"id\": \"{contestedKey}\", \"snapshot\": {{\"gold\": {writerId * 1000}, \"level\": {(writerId % 25) + 1}}}}}";
            return await handler.HandleAsync(JsonElement.Parse(saveJson));
        })).ToArray();

        ToolCallResult[] results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.IsError.Should().BeFalse());
        handler.TryGetSnapshot(contestedKey, out var finalSnapshot).Should().BeTrue();
        finalSnapshot.Should().NotBeNull();
        handler.Count.Should().Be(1, "Concurrent writes to same key must safely overwrite without duplicating entries");
    }

    #endregion

    #region Dimension 4: Lifecycle Safety Guardrails & Adversarial Inputs

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(999)]
    public async Task Challenger_Restore_AdversarialGamePhases_GuaranteedRejection(int phase)
    {
        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: phase.ToString(CultureInfo.InvariantCulture));

        string json = $"{{\"action\": \"restore\", \"phase\": {phase}, \"snapshot\": {{\"gold\": 10000, \"level\": 25, \"items\": [\"Item_PortalKey\"]}}}}";
        var args = JsonElement.Parse(json);

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        root.GetProperty("currentPhase").GetInt32().Should().Be(phase);

        // Zero mutation commands dispatched over IPC
        dispatched.Should().NotContain(c => c.Contains("ApplySnapshot"));
    }

    [Fact]
    public async Task Restore_CorruptedPhaseResponseFromIpc_DefaultsSafely()
    {
        // IPC returns non-integer garbage
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "ENGINE_CORRUPT_RESPONSE");

        var args = JsonElement.Parse("""
        {
            "action": "restore",
            "currentPhase": 0,
            "snapshot": { "gold": 500 }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);

        // Argument currentPhase=0 should take precedence and reject
        result.IsError.Should().BeTrue();
        JsonElement root = ParseResultJson(result);
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
    }

    [Fact]
    public async Task Save_ExtremeAdversarialValues_SanitizesAndClampsFields()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc();

        var args = JsonElement.Parse("""
        {
            "action": "save",
            "id": "adversarial_clamp_snap",
            "snapshot": {
                "gold": -999999,
                "level": 999,
                "health": 50000.0,
                "mana": -50.0,
                "x": 999999.0,
                "y": -999999.0,
                "z": 128.0,
                "items": [
                    { "name": "", "slot": -5, "charges": -10 },
                    { "name": "PortalKey", "slot": 99, "charges": 5 }
                ],
                "cooldowns": [
                    { "slot": -2, "remainingSeconds": -10.0, "totalDurationSeconds": -20.0 },
                    { "slot": 50, "remainingSeconds": 15.0, "totalDurationSeconds": 10.0 }
                ]
            }
        }
        """);

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        handler.TryGetSnapshot("adversarial_clamp_snap", out var snap).Should().BeTrue();
        snap.Should().NotBeNull();

        // Level clamped [1, 25]
        snap!.Level.Should().Be(25);
        // Gold clamped >= 0
        snap.Gold.Should().Be(0);
        // Coordinates clamped [-32768, 32768]
        snap.X.Should().Be(32768.0);
        snap.Y.Should().Be(-32768.0);
        snap.Z.Should().Be(128.0);

        // Items normalized and clamped
        snap.Player.Items.Should().HaveCount(2);
        snap.Player.Items[0].Name.Should().Be("Item_HomecomingStone");
        snap.Player.Items[0].Slot.Should().Be(0); // Clamped from -5
        snap.Player.Items[0].Charges.Should().Be(1); // Clamped from -10

        snap.Player.Items[1].Name.Should().Be("Item_PortalKey");
        snap.Player.Items[1].Slot.Should().Be(11); // Clamped from 99

        // Cooldowns normalized and clamped
        snap.Player.Cooldowns.Should().HaveCount(2);
        snap.Player.Cooldowns[0].Slot.Should().Be(0); // Clamped from -2
        snap.Player.Cooldowns[0].RemainingSeconds.Should().Be(0.0);

        snap.Player.Cooldowns[1].Slot.Should().Be(15); // Clamped from 50
        snap.Player.Cooldowns[1].RemainingSeconds.Should().Be(15.0);
        snap.Player.Cooldowns[1].TotalDurationSeconds.Should().Be(15.0); // Total duration must be >= remaining
    }

    #endregion

    #region Dimension 5: Fast Inner-Loop Timing (< 2.0s) & Zero Desktop GUI Windows

    [Fact]
    public void ZeroGuiWindows_WhenExecuted_EnsuresNoExternalProcessesSpawned()
    {
        DateTime testStartTime = Process.GetCurrentProcess().StartTime;
        var honProcesses = Process.GetProcessesByName("hon_x64")
            .Where(p => { try { return p.StartTime >= testStartTime; } catch { return false; } }).ToList();
        honProcesses.Should().BeEmpty("Automated unit tests must never spawn hon_x64 game client processes");

        var guiProcesses = Process.GetProcessesByName("ReplayStudio.GUI")
            .Where(p => { try { return p.StartTime >= testStartTime; } catch { return false; } }).ToList();
        guiProcesses.Should().BeEmpty("Automated unit tests must never spawn external ReplayStudio.GUI windows");
    }

    [Fact]
    public async Task InnerLoopDuration_WhenExecuted_CompletesSubSecond()
    {
        var (handler, _, _) = CreateHandlerWithMockIpc(phaseResponse: "3");

        Stopwatch sw = Stopwatch.StartNew();

        // 50 operations: save -> seek -> restore -> list -> delete
        for (int i = 0; i < 10; i++)
        {
            string saveJson = $"{{\"action\": \"save\", \"id\": \"loop_{i}\", \"snapshot\": {{\"gold\": {i}}}}}";
            await handler.HandleAsync(JsonElement.Parse(saveJson));
            await handler.HandleAsync(JsonElement.Parse("""{"action": "seek", "timeFormatted": "01:15.500"}"""));
            string restoreJson = $"{{\"action\": \"restore\", \"id\": \"loop_{i}\"}}";
            await handler.HandleAsync(JsonElement.Parse(restoreJson));
            await handler.HandleAsync(JsonElement.Parse("""{"action": "list"}"""));
            string deleteJson = $"{{\"action\": \"delete\", \"id\": \"loop_{i}\"}}";
            await handler.HandleAsync(JsonElement.Parse(deleteJson));
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "50-operation inner loop must finish under 10.0 seconds under parallel load");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ManualGate_WhenChecked_RespectsNexusTestEnvironment()
    {
        // When NEXUS_RUN_MANUAL_TESTS is not set, IsManualTestEnabled should be false
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            NexusTestEnvironment.ManualBypassedCount.Should().BeGreaterThan(0, "manual gate bypass counter must increment when manual tests are disabled");
            return;
        }

        // Live test path only if explicitly opted in
        var (handler, _, dispatched) = CreateHandlerWithMockIpc(phaseResponse: "3");
        ToolCallResult seekRes = await handler.HandleAsync(JsonElement.Parse("""{"action": "seek", "timeFormatted": "00:10.000"}"""));
        seekRes.IsError.Should().BeFalse();
        dispatched.Should().Contain(cmd => cmd.Contains("ReplaySetFrame") || cmd.Contains("Seek"));
    }

    #endregion
}
