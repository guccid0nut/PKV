// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using COMPEL.Services.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace COMPEL.Tests;

public sealed class GameServerLogWatcherServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDiskWatcherDisabled_ExitsGracefullyWithoutPolling()
    {
        // protects: EnableDiskLogWatcher=false fast-exit contract preventing redundant disk polling when stdout pipes are active.
        // fails_when: ExecuteAsync enters PeriodicTimer polling loop (blocking ExecuteTask prior to StopAsync), or logs/scans .clog files when disabled.
        // seam: GameServerLogWatcherService HostedService lifecycle + real temp RuntimeArtefactsPath filesystem boundary.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"compel_watcher_disabled_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            MatchServerManagerOptions options = new()
            {
                Gateway = "localhost",
                RuntimeArtefactsPath = tempRoot,
                EnableDiskLogWatcher = false
            };
            IOptions<MatchServerManagerOptions> optionsWrapper = Options.Create(options);

            ArtefactsLocator artefactsLocator = new(optionsWrapper);
            string logsDir = Path.Combine(artefactsLocator.ArtefactsDirectory, "game", "logs");
            string seededClogPath = string.Empty;
            if (artefactsLocator.RuntimeArtefactsPathApplies)
            {
                Directory.CreateDirectory(logsDir);
                seededClogPath = Path.Combine(logsDir, "Slave1001_M9999_console.clog");
                await File.WriteAllBytesAsync(seededClogPath, Encoding.Unicode.GetBytes("[ALL] Player1: test line\r\n"));
            }

            AddressResolver addressResolver = new(optionsWrapper, NullLogger<AddressResolver>.Instance);
            await using CompelTelemetryRelayService telemetryRelay = new(
                addressResolver,
                NullLogger<CompelTelemetryRelayService>.Instance,
                options: optionsWrapper);

            CapturingLogger<GameServerLogWatcherService> logger = new();
            using GameServerLogWatcherService service = new(
                artefactsLocator,
                telemetryRelay,
                logger,
                optionsWrapper);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

            await service.StartAsync(cts.Token);

            Assert.NotNull(service.ExecuteTask);
            // In .NET 10+, BackgroundService.StartAsync dispatches ExecuteAsync via Task.Run;
            // awaiting ExecuteTask BEFORE StopAsync proves ExecuteAsync exited on its own without entering the PeriodicTimer loop.
            await service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(
                service.ExecuteTask.IsCompletedSuccessfully,
                "ExecuteAsync must exit cleanly without entering the PeriodicTimer polling loop when EnableDiskLogWatcher is false.");

            await service.StopAsync(cts.Token);

            Assert.Contains(
                logger.Messages,
                msg => msg.Contains("EnableDiskLogWatcher=false", StringComparison.Ordinal));
            Assert.DoesNotContain(
                logger.Messages,
                msg => msg.Contains("Starting live game server log tailing service", StringComparison.Ordinal));

            if (artefactsLocator.RuntimeArtefactsPathApplies)
            {
                string[] remainingFiles = Directory.GetFiles(logsDir);
                Assert.Single(remainingFiles);
                Assert.Equal(seededClogPath, remainingFiles[0]);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DetectStreamEncoding_ReturnsUnicode_ForUtf16LeWithoutBom()
    {
        // protects: Issue #3B K2 .clog UTF-16LE without BOM detection invariant.
        // fails_when: Raw .clog bytes are misdetected as UTF-8, causing null-interleaved garbled strings.
        // seam: MatchServerManagerSupervisor.DetectStreamEncoding stream probe.
        byte[] utf16LeBytes = [(byte)'H', 0x00, (byte)'o', 0x00];
        using MemoryStream ms = new(utf16LeBytes);

        Encoding detected = MatchServerManagerSupervisor.DetectStreamEncoding(ms);
        Assert.Equal(Encoding.Unicode, detected);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void DetectStreamEncoding_ReturnsUtf8_ForStandardAsciiAndUtf8()
    {
        // protects: Standard server.log UTF-8 encoding invariant.
        // fails_when: UTF-8 log files are incorrectly parsed as 16-bit unicode.
        // seam: MatchServerManagerSupervisor.DetectStreamEncoding stream probe.
        byte[] utf8Bytes = [(byte)'H', (byte)'o', (byte)'N', (byte)'\n'];
        using MemoryStream ms = new(utf8Bytes);

        Encoding detected = MatchServerManagerSupervisor.DetectStreamEncoding(ms);
        Assert.Equal(Encoding.UTF8, detected);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void CalculateFileScore_MatchesSlave1000Offset_ForTargetWorker()
    {
        // protects: Issue #3B K2 Slave1000+ offset scoring for multi-slave supervisor discovery.
        // fails_when: Slave1002_ console logs fail to correlate with instance index 2 or correlate with wrong worker.
        // seam: MatchServerManagerSupervisor.CalculateFileScore scoring heuristic.
        DedicatedServerWorker worker2 = new(
            instanceIndex: 2,
            port: 11236,
            voicePort: 11736,
            publicGamePort: 11236,
            publicVoicePort: 11736,
            serverName: "DONUT ARENA #2");

        FileInfo targetLog = new("Slave1002_M1033_console.clog");
        FileInfo otherLog = new("Slave1001_M1033_console.clog");

        int targetScore = MatchServerManagerSupervisor.CalculateFileScore(worker2, targetLog);
        int otherScore = MatchServerManagerSupervisor.CalculateFileScore(worker2, otherLog);

        Assert.True(targetScore > 100, $"Expected targetScore > 100 but was {targetScore}");
        Assert.Equal(-10000, otherScore);
    }

    [Fact]
    public void DedicatedServerWorker_TryRecordRecentChatMessage_DeduplicatesWithin15Seconds()
    {
        // protects: Issue #3B Chat message deduplication between stream pipe and .clog tailers.
        // fails_when: Repeated chat lines in quick succession bypass deduplication.
        // seam: DedicatedServerWorker.TryRecordRecentChatMessage sliding window cache.
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11735,
            publicGamePort: 11235,
            publicVoicePort: 11735,
            serverName: "DONUT ARENA #1");

        string chat = "[ALL] Player1: gg well played";

        bool firstSeen = worker.TryRecordRecentChatMessage(chat);
        bool duplicateSeen = worker.TryRecordRecentChatMessage(chat);
        bool differentSeen = worker.TryRecordRecentChatMessage("[TEAM] Player1: need ward");

        Assert.True(firstSeen);
        Assert.False(duplicateSeen);
        Assert.True(differentSeen);
    }

    [Fact]
    public void CalculateFileScore_MatchesTierPartitionedWorker1002_WithoutSelfRejection()
    {
        // protects: Tier-partitioned worker InstanceIndex (1001+, 2001+) Slave log correlation on Windows/Docker.
        // fails_when: CalculateFileScore treats other=2 -> otherOffset=1002 as a foreign worker when worker.InstanceIndex is 1002, returning -10000.
        // seam: MatchServerManagerSupervisor.CalculateFileScore real scoring boundary.
        DedicatedServerWorker worker1002 = new(
            instanceIndex: 1002,
            port: 11236,
            voicePort: 11436,
            publicGamePort: 11236,
            publicVoicePort: 11436,
            serverName: "KONGOR ARENA #1002");

        DedicatedServerWorker worker1001 = new(
            instanceIndex: 1001,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 11235,
            publicVoicePort: 11435,
            serverName: "KONGOR ARENA #1001");

        FileInfo targetLog = new("Slave1002_M1108_console.clog");
        FileInfo localSlotLog = new("Slave2_2026_11_07.log");
        FileInfo foreignLog = new("Slave1001_M1108_console.clog");
        FileInfo unindexedMinusOneLog = new("Slave-1_2026_11_07.log");

        Assert.True(MatchServerManagerSupervisor.CalculateFileScore(worker1002, targetLog) > 100);
        Assert.True(MatchServerManagerSupervisor.CalculateFileScore(worker1002, localSlotLog) > 100);
        Assert.Equal(-10000, MatchServerManagerSupervisor.CalculateFileScore(worker1002, foreignLog));
        // K2 writes Slave-1_*.log when m_iSlave == -1 (before svr_slave applies); must never be claimed as Slave #1!
        Assert.Equal(-10000, MatchServerManagerSupervisor.CalculateFileScore(worker1001, unindexedMinusOneLog));
        Assert.Equal(-10000, MatchServerManagerSupervisor.CalculateFileScore(worker1002, unindexedMinusOneLog));
    }

    [Fact]
    public void BuildCandidateLogDirectories_IncludesUserdataServerLogsAndSiblingServerTree()
    {
        // protects: Windows K2 dedicated server .clog discovery under userdata/Heroes of Newerth x64/server/logs and sibling ../server layout.
        // fails_when: ResolveActiveLogFileForWorker omits userdata/Heroes of Newerth x64/server/logs or sibling games/server when InstallationDirectory is games/hon server.
        // seam: MatchServerManagerSupervisor.BuildCandidateLogDirectories + real temp directory tree.
        string tempGames = Path.Combine(Path.GetTempPath(), $"compel_logdirs_{Guid.NewGuid():N}");
        string honServerDir = Path.Combine(tempGames, "hon server");
        string siblingServerLogs = Path.Combine(tempGames, "server", "userdata", "Heroes of Newerth x64", "server", "logs");
        string directServerLogs = Path.Combine(honServerDir, "userdata", "Heroes of Newerth x64", "server", "logs");
        Directory.CreateDirectory(honServerDir);
        Directory.CreateDirectory(siblingServerLogs);
        Directory.CreateDirectory(directServerLogs);

        try
        {
            IReadOnlyList<string> dirs = MatchServerManagerSupervisor.BuildCandidateLogDirectories(honServerDir, artefactsDirectory: null);
            Assert.Contains(dirs, d => string.Equals(Path.GetFullPath(d), Path.GetFullPath(directServerLogs), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(dirs, d => string.Equals(Path.GetFullPath(d), Path.GetFullPath(siblingServerLogs), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempGames))
            {
                Directory.Delete(tempGames, recursive: true);
            }
        }
    }

    [Fact]
    public void EmptyLobbyReaper_TracksZeroJoinLobbyAndReapsAfterConfiguredThreshold()
    {
        // protects: Automatic reaping of 0/10 player-hosted lobbies where 0 clients connect within EmptyLobbyReapSeconds, without reaping occupied lobbies or idle warm workers.
        // fails_when: StartGame/LoadWorld lobby open fails to arm LobbyOpenedAtUtc, idle workers reap prematurely, or 0-client stale lobbies remain open past EmptyLobbyReapSeconds.
        // seam: MatchServerManagerSupervisor.DispatchStreamLine + ShouldReapEmptyLobby real state machine.
        IOptions<MatchServerManagerOptions> opts = Options.Create(new MatchServerManagerOptions
        {
            EmptyLobbyReapSeconds = 60
        });
        using MatchServerManagerSupervisor supervisor = new(opts, NullLogger<MatchServerManagerSupervisor>.Instance);
        DedicatedServerWorker worker = new(1002, 11236, 11436, 11236, 11436, "KONGOR ARENA #1002")
        {
            CurrentState = "Available"
        };

        DateTimeOffset t0 = TimeProvider.System.GetUtcNow();
        // Idle warm worker before any lobby opens must never be reaped
        Assert.False(supervisor.ShouldReapEmptyLobby(worker, t0.AddMinutes(10), ignoreProcessRunningForTest: true));

        // Player creates custom lobby ("LinuxTexSoccer", MatchID 1108) with 0 connected players
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(11236, "[Nov 7 13:52:54] [13:52:53] StartGame - MatchID [1108]", IsError: false));
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(11236, "CURL (data out): session=abc&num_conn=0&map=soccer&c_state=2&mname=LinuxTexSoccer&new=1&match_id=1108", IsError: false));

        Assert.Equal(1108, worker.MatchId);
        Assert.Equal("LinuxTexSoccer", worker.MatchName);
        Assert.NotNull(worker.LobbyOpenedAtUtc);

        DateTimeOffset openedAt = worker.LobbyOpenedAtUtc!.Value;
        Assert.False(supervisor.ShouldReapEmptyLobby(worker, openedAt.AddSeconds(30), ignoreProcessRunningForTest: true));
        Assert.True(supervisor.ShouldReapEmptyLobby(worker, openedAt.AddSeconds(61), ignoreProcessRunningForTest: true));

        // Once a player connects, zero-join empty-lobby reaper must not fire while occupied
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(11236, "AddClient: client num: 0", IsError: false));
        Assert.Equal(1, worker.ConnectedClients);
        Assert.False(supervisor.ShouldReapEmptyLobby(worker, openedAt.AddSeconds(120), ignoreProcessRunningForTest: true));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
        }
    }
}
