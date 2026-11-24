// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayWalTests.cs
// System: COMPEL Unit Tests - Strategic Recommendation SR-04
// Subsystem: Background Replay Write-Ahead Logging (WAL) & Lakehouse Recovery
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Configuration;
using COMPEL.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class ReplayWalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _walDir;

    public ReplayWalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Kongor_WalTests_" + Guid.NewGuid().ToString("N"));
        _walDir = Path.Combine(_tempDir, "wal");
        Directory.CreateDirectory(_walDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task ReplayWalWriter_AppendsChunks_CalculatesCrc32AndFlushesToDisk()
    {
        // Arrange: 512-byte small chunks for rapid multi-chunk boundary testing
        int chunkSize = 512;
        await using ReplayWalWriter writer = new(
            matchId: 10482,
            port: 11235,
            walDirectory: _walDir,
            chunkSize: chunkSize,
            logger: NullLogger.Instance);

        // Act: write 100 log lines (~30 bytes each = ~3000 bytes => ~6 chunks)
        for (int i = 1; i <= 100; i++)
        {
            await writer.AppendLineAsync($"[CombatEvent] Tick:{i * 50} Hero:Hero_Pyromancer Damage:120 Target:Hero_Devourer");
        }

        await writer.CompleteAsync();

        // Assert
        File.Exists(writer.WalFilePath).Should().BeTrue();
        FileInfo fileInfo = new(writer.WalFilePath);
        fileInfo.Length.Should().BeGreaterThan(2500);

        ReplayWalBeacon beacon = writer.CurrentBeacon;
        beacon.Should().NotBeNull();
        beacon.MatchId.Should().Be(10482);
        beacon.Port.Should().Be(11235);
        beacon.Sequence.Should().BeGreaterThanOrEqualTo(5);
        beacon.FlushedBytes.Should().Be(fileInfo.Length);
        beacon.LastCrc32.Should().NotBe(0u);
        beacon.Status.Should().Be("COMPLETED");

        // Verify binary header on first chunk
        byte[] firstChunkHeader = new byte[ReplayWalChunkHeader.HeaderSizeBytes];
        await using (FileStream fs = File.OpenRead(writer.WalFilePath))
        {
            await fs.ReadExactlyAsync(firstChunkHeader);
        }

        ReplayWalChunkHeader.TryReadFrom(firstChunkHeader, out ReplayWalChunkHeader header).Should().BeTrue();
        header.IsValidMagic.Should().BeTrue();
        header.SequenceNumber.Should().Be(1);
        header.PayloadSizeBytes.Should().Be((uint)chunkSize);
        header.Crc32Checksum.Should().NotBe(0u);
    }

    [Fact]
    public void ReplayWalBeacon_FormatsQrStringAndTelemetryTokens_UnderOpticalLimit()
    {
        // Arrange
        ReplayWalBeacon beacon = new(
            MatchId: 99420,
            Port: 11235,
            Sequence: 42,
            FlushedBytes: 2752512,
            LastCrc32: 0xA8F3B10C,
            Status: "RECORDING");

        // Act
        string qr = beacon.ToQrString();
        string token = beacon.ToCompactTelemetryString();
        string webLink = beacon.ToWebLink();
        string markdown = beacon.ToTriageSnapshotMarkdown(
            map: "caldavar",
            hero: "Hero_Pyromancer",
            heroLevel: 6,
            team: "Legion",
            playerCoordinates: "Vector3(1450.2, 3210.8, 128.0)",
            fps: "144",
            ping: "12",
            clientBuild: "x64 Vulkan (Branch: main)",
            activeMods: "mod_ui_hd, mod_quick_cast",
            recentErrors: "None");

        // Assert: Verify compact payload (< 80 bytes) preventing QR code bloat on Ctrl+Shift+D HUD
        qr.Should().StartWith("kongor://wal?m=99420&p=11235&s=42&b=2752512&c=A8F3B10C&st=RECORDING");
        qr.Length.Should().BeLessThan(80);

        token.Should().Be("wal:99420:11235:42:2752512:A8F3B10C:RECORDING");
        token.Length.Should().BeLessThan(60);

        webLink.Should().Be("https://telemetry.kongor.online/match/99420?port=11235");

        markdown.Should().Contain("### 🐛 Kongor Triage Snapshot");
        markdown.Should().Contain("- **Match ID**: 99420 | **Port**: 11235 | **Map**: caldavar");
        markdown.Should().Contain("- **Hero**: `Hero_Pyromancer` (Level 6) | **Team**: Legion");
        markdown.Should().Contain("- **Position**: `Vector3(1450.2, 3210.8, 128.0)`");
        markdown.Should().Contain("- **Performance**: 144 FPS | Ping: 12 ms");
        markdown.Should().Contain("- **WAL State**: Seq #42");
        markdown.Should().Contain("- **Active Mod Stack**: mod_ui_hd, mod_quick_cast");
    }

    [Fact]
    public async Task ReplayWalRecoveryService_SalvagesCleanWalFile_AndGeneratesLog()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableReplayWal = true,
            ReplayWalDirectory = _walDir
        });

        var recovery = new ReplayWalRecoveryService(
            options: options,
            supervisor: null,
            ingestor: null,
            logger: NullLogger<ReplayWalRecoveryService>.Instance,
            repoRoot: _tempDir);

        string testLine = "[CombatEvent] Match:777 Tick:100 Event:Kill Source:1 Target:2\n";
        await using (ReplayWalWriter writer = new(777, 11236, _walDir, chunkSize: 256))
        {
            for (int i = 0; i < 20; i++)
            {
                await writer.AppendLineAsync(testLine);
            }
            await writer.CompleteAsync();
        }

        string walPath = Path.Combine(_walDir, "match_777_port11236.wal");

        // Act
        ReplayWalRecoveryResult result = await recovery.SalvageAndIngestAsync(walPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MatchId.Should().Be(777);
        result.Port.Should().Be(11236);
        result.ValidChunksSalvaged.Should().BeGreaterThan(0);
        result.TornBytesDiscarded.Should().Be(0);

        File.Exists(result.SalvagedLogPath).Should().BeTrue();
        string content = await File.ReadAllTextAsync(result.SalvagedLogPath);
        content.Should().Contain("[CombatEvent] Match:777");

        // Processed journal was renamed
        File.Exists(walPath).Should().BeFalse();
        File.Exists(walPath + ".processed").Should().BeTrue();
    }

    [Fact]
    public async Task ReplayWalRecoveryService_DetectsAndDiscardsTornWrite_AtFileTail()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableReplayWal = true,
            ReplayWalDirectory = _walDir
        });

        var recovery = new ReplayWalRecoveryService(
            options: options,
            supervisor: null,
            ingestor: null,
            logger: NullLogger<ReplayWalRecoveryService>.Instance,
            repoRoot: _tempDir);

        string walPath = Path.Combine(_walDir, "match_888_port11237.wal");
        await using (ReplayWalWriter writer = new(888, 11237, _walDir, chunkSize: 256))
        {
            for (int i = 0; i < 10; i++)
            {
                await writer.AppendLineAsync($"[CombatEvent] Clean line {i}");
            }
            await writer.FlushAsync();
        }

        // Simulate sudden hard power failure: append 45 bytes of a torn / half-written chunk at the end
        byte[] tornBytes = Encoding.UTF8.GetBytes("KWAL\x05\x00\x00\x00_torn_partial_data_abrupt_node_power_cut_!");
        await using (FileStream fs = File.Open(walPath, FileMode.Append, FileAccess.Write))
        {
            await fs.WriteAsync(tornBytes);
        }

        // Act
        ReplayWalRecoveryResult result = await recovery.SalvageAndIngestAsync(walPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MatchId.Should().Be(888);
        result.ValidChunksSalvaged.Should().BeGreaterThan(0);
        result.TornBytesDiscarded.Should().Be(tornBytes.Length);

        File.Exists(result.SalvagedLogPath).Should().BeTrue();
        string salvagedText = await File.ReadAllTextAsync(result.SalvagedLogPath);
        salvagedText.Should().Contain("[CombatEvent] Clean line");
        salvagedText.Should().NotContain("abrupt_node_power_cut");
    }

    [Fact]
    public async Task ReplayWalRecoveryService_DetectsCorruptedCrc32_AndStopsCleanly()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableReplayWal = true,
            ReplayWalDirectory = _walDir
        });

        var recovery = new ReplayWalRecoveryService(
            options: options,
            supervisor: null,
            ingestor: null,
            logger: NullLogger<ReplayWalRecoveryService>.Instance,
            repoRoot: _tempDir);

        string walPath = Path.Combine(_walDir, "match_999_port11238.wal");
        await using (ReplayWalWriter writer = new(999, 11238, _walDir, chunkSize: 256))
        {
            // Write 2 full chunks
            for (int i = 0; i < 30; i++)
            {
                await writer.AppendLineAsync($"[CombatEvent] Line {i}");
            }
            await writer.CompleteAsync();
        }

        // Corrupt a byte in the second chunk's payload
        byte[] fileBytes = await File.ReadAllBytesAsync(walPath);
        // Flip bits in the middle of second chunk
        int targetIndex = ReplayWalChunkHeader.HeaderSizeBytes + 256 + ReplayWalChunkHeader.HeaderSizeBytes + 10;
        if (targetIndex < fileBytes.Length)
        {
            fileBytes[targetIndex] ^= 0xFF;
            await File.WriteAllBytesAsync(walPath, fileBytes);
        }

        // Act
        ReplayWalRecoveryResult result = await recovery.SalvageAndIngestAsync(walPath);

        // Assert: First chunk was valid, second chunk had CRC mismatch and was discarded
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ValidChunksSalvaged.Should().Be(1);
        result.TornBytesDiscarded.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReplayWalWriter_TryEnqueueLine_PreservesStrictFifoOrderAndDrainsBeforeComplete()
    {
        // Protects: chronological combat log ordering and zero trailing event loss when TryEnqueueLine bursts precede CompleteAsync/FlushAsync.
        // Fails when: unawaited fire-and-forget tasks race on SemaphoreSlim or get dropped behind _isCompleted = true.
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableReplayWal = true,
            ReplayWalDirectory = _walDir
        });

        var recovery = new ReplayWalRecoveryService(
            options: options,
            supervisor: null,
            ingestor: null,
            logger: NullLogger<ReplayWalRecoveryService>.Instance,
            repoRoot: _tempDir);

        const int totalLines = 400;
        string walPath = Path.Combine(_walDir, "match_1001_port11239.wal");

        // Fault injection: 2ms async write delay forces real I/O suspension on every chunk boundary,
        // exposing SemaphoreSlim lock-stealing / barge-in reordering if a single-reader channel is not used.
        await using (ReplayWalWriter writer = new(
            matchId: 1001,
            port: 11239,
            walDirectory: _walDir,
            chunkSize: 256,
            logger: NullLogger.Instance,
            streamFactory: () => new FaultInjectingWalStream(
                new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                writeDelayMs: 2)))
        {
            for (int i = 1; i <= totalLines; i++)
            {
                string line = $"[CombatEvent] Seq:{i:D4} Payload:{new string('X', (i % 17) + 10)}";
                writer.TryEnqueueLine(line);
                if (i % 40 == 0)
                {
                    await Task.Delay(1);
                }
            }

            await writer.CompleteAsync();
        }

        ReplayWalRecoveryResult result = await recovery.SalvageAndIngestAsync(walPath);
        result.Success.Should().BeTrue();

        string[] salvagedLines = await File.ReadAllLinesAsync(result.SalvagedLogPath);
        salvagedLines.Should().HaveCount(totalLines);
        for (int i = 0; i < totalLines; i++)
        {
            salvagedLines[i].Should().StartWith($"[CombatEvent] Seq:{(i + 1):D4} ");
        }
    }

    [Fact]
    public async Task ReplayWalWriter_MidChunkTornWriteFaultInjection_LogsCleanlyAndSalvagesPrecedingChunks()
    {
        // Protects: fail-safe DisposeAsync error handling + ReplayWalRecoveryService torn-tail isolation on mid-write disk failure.
        // Fails when: DisposeAsync throws unhandled IOException or recovery fails to salvage committed chunks prior to a cut write.
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableReplayWal = true,
            ReplayWalDirectory = _walDir
        });

        var recovery = new ReplayWalRecoveryService(
            options: options,
            supervisor: null,
            ingestor: null,
            logger: NullLogger<ReplayWalRecoveryService>.Instance,
            repoRoot: _tempDir);

        string walPath = Path.Combine(_walDir, "match_1002_port11240.wal");

        // Cut the stream mid-write at byte 320 (after Chunk #1 [16B header + 256B payload = 272B], tearing Chunk #2 at 48 bytes)
        await using (ReplayWalWriter writer = new(
            matchId: 1002,
            port: 11240,
            walDirectory: _walDir,
            chunkSize: 256,
            logger: NullLogger.Instance,
            streamFactory: () => new FaultInjectingWalStream(
                new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                writeDelayMs: 0,
                cutWriteAfterBytes: 320)))
        {
            for (int i = 1; i <= 25; i++)
            {
                writer.TryEnqueueLine($"[CombatEvent] TornTest Line:{i:D3} Damage:250");
            }
            // DisposeAsync triggers CompleteAsync -> hits IOException at byte 320 -> logs warning instead of crashing
        }

        ReplayWalRecoveryResult result = await recovery.SalvageAndIngestAsync(walPath);
        result.Success.Should().BeTrue();
        result.ValidChunksSalvaged.Should().Be(1);
        result.TornBytesDiscarded.Should().Be(320 - (ReplayWalChunkHeader.HeaderSizeBytes + 256));
    }

    private sealed class FaultInjectingWalStream(
        Stream inner,
        int writeDelayMs = 0,
        long cutWriteAfterBytes = long.MaxValue) : Stream
    {
        private long _bytesWritten;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (writeDelayMs > 0)
            {
                await Task.Delay(writeDelayMs, cancellationToken).ConfigureAwait(false);
            }

            long remainingBudget = cutWriteAfterBytes - _bytesWritten;
            if (remainingBudget <= 0)
            {
                throw new IOException("FaultInjection: simulated mid-chunk disk write cut.");
            }

            if (buffer.Length > remainingBudget)
            {
                int partial = (int)remainingBudget;
                await inner.WriteAsync(buffer.Slice(0, partial), cancellationToken).ConfigureAwait(false);
                await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                _bytesWritten += partial;
                throw new IOException($"FaultInjection: simulated torn write after {partial}/{buffer.Length} bytes.");
            }

            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytesWritten += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("ReplayWalWriter uses asynchronous WriteAsync exclusively.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
