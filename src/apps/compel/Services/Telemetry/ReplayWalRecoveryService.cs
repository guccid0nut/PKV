// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayWalRecoveryService.cs
// System: COMPEL Telemetry - Strategic Recommendation SR-04
// Subsystem: Background Replay Write-Ahead Logging (WAL) Autonomous Salvage & Ingestion
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using ASPIRE.Common.Hosting;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace COMPEL.Services.Telemetry;

/// <summary>
/// Autonomous Replay WAL Recovery Service (SR-04):
/// Scans the WAL journal directory on startup and after dedicated worker crashes,
/// validates sequential chunk headers and CRC32 checksums, isolates and discards torn writes,
/// and re-synthesizes valid combat event logs to trigger immediate Apache Parquet lakehouse export.
/// </summary>
public sealed class ReplayWalRecoveryService : ResilientBackgroundService
{
    private static readonly Regex WalFileNameRegex = new(@"match_(\d+)_port(\d+)\.wal$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MatchServerManagerOptions _options;
    private readonly MatchServerManagerSupervisor? _supervisor;
    private readonly PostMatchParquetIngestorService? _ingestor;
    private readonly ILogger<ReplayWalRecoveryService>? _logger;
    private readonly string _repoRoot;

    public ReplayWalRecoveryService(
        IOptions<MatchServerManagerOptions> options,
        MatchServerManagerSupervisor? supervisor = null,
        PostMatchParquetIngestorService? ingestor = null,
        ILogger<ReplayWalRecoveryService>? logger = null,
        string? repoRoot = null)
        : base(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReplayWalRecoveryService>.Instance, nameof(ReplayWalRecoveryService))
    {
        _options = options.Value;
        _supervisor = supervisor;
        _ingestor = ingestor;
        _logger = logger;
        _repoRoot = repoRoot ?? Directory.GetCurrentDirectory();
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.QueueArcadeMode)
        {
            _logger?.LogInformation("[ReplayWalRecovery] QueueArcadeMode=true; skipping Ranked WAL crash listener registration.");
            return Task.CompletedTask;
        }

        if (_supervisor != null)
        {
            _supervisor.WorkerCrashed += OnWorkerCrashedAsync;
            _logger?.LogInformation("[ReplayWalRecovery] Attached listener to supervisor.WorkerCrashed.");
        }
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.QueueArcadeMode)
        {
            return Task.CompletedTask;
        }

        if (_supervisor != null)
        {
            _supervisor.WorkerCrashed -= OnWorkerCrashedAsync;
            _logger?.LogInformation("[ReplayWalRecovery] Detached listener from supervisor.WorkerCrashed.");
        }
        return base.StopAsync(cancellationToken);
    }

    private async Task OnWorkerCrashedAsync(DedicatedServerWorker worker, int exitCode)
    {
        if (!_options.EnableReplayWal || _options.QueueArcadeMode) return;

        try
        {
            string walPath = worker.WalWriter?.WalFilePath
                ?? Path.Combine(ResolveWalDirectory(), $"match_{worker.MatchId}_port{worker.Port}.wal");

            if (File.Exists(walPath))
            {
                _logger?.LogInformation(
                    "[ReplayWalRecovery] Dedicated Worker on Port {Port} crashed (ExitCode: {ExitCode}). Initiating autonomous WAL salvage for Match #{MatchId}...",
                    worker.Port, exitCode, worker.MatchId);

                await SalvageAndIngestAsync(walPath, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ReplayWalRecovery] Failed to salvage crash WAL for Port {Port}.", worker.Port);
        }
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableReplayWal || _options.QueueArcadeMode)
        {
            if (_options.QueueArcadeMode)
            {
                _logger?.LogInformation("[ReplayWalRecovery] QueueArcadeMode=true; skipping startup Ranked WAL journal scan.");
            }
            return;
        }

        try
        {
            _logger?.LogInformation("[ReplayWalRecovery] Scanning WAL directory for unfinalized journals on startup...");
            int recovered = await RecoverAllPendingJournalsAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger?.LogInformation("[ReplayWalRecovery] Startup recovery completed. Salvaged and ingested {Count} unfinalized match replays.", recovered);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ReplayWalRecovery] Error encountered during startup WAL journal recovery scan.");
        }
    }

    /// <summary>
    /// Scans the WAL directory and attempts to salvage all pending unfinalized .wal files.
    /// </summary>
    public async Task<int> RecoverAllPendingJournalsAsync(CancellationToken cancellationToken = default)
    {
        string walDir = ResolveWalDirectory();
        if (!Directory.Exists(walDir)) return 0;

        string[] walFiles = Directory.GetFiles(walDir, "*.wal", SearchOption.TopDirectoryOnly);
        int successfulRecoveries = 0;

        foreach (string file in walFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            ReplayWalRecoveryResult result = await SalvageAndIngestAsync(file, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.ValidChunksSalvaged > 0)
            {
                successfulRecoveries++;
            }
        }

        return successfulRecoveries;
    }

    /// <summary>
    /// Salvages a specific WAL file, writing clean chunks to a reconstructed log file
    /// and feeding it into PostMatchParquetIngestorService.
    /// </summary>
    public async Task<ReplayWalRecoveryResult> SalvageAndIngestAsync(
        string walFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(walFilePath))
        {
            return new ReplayWalRecoveryResult(
                MatchId: 0,
                Port: 0,
                WalFilePath: walFilePath,
                SalvagedLogPath: string.Empty,
                TotalChunksScanned: 0,
                ValidChunksSalvaged: 0,
                TornBytesDiscarded: 0,
                SalvagedEventCount: 0,
                ParquetGenerated: false,
                Success: false,
                ErrorMessage: "WAL file does not exist.");
        }

        // Parse match ID and port from filename (match_{matchId}_port{port}.wal)
        long matchId = 0;
        int port = 0;
        Match match = WalFileNameRegex.Match(Path.GetFileName(walFilePath));
        if (match.Success)
        {
            _ = long.TryParse(match.Groups[1].Value, out matchId);
            _ = int.TryParse(match.Groups[2].Value, out port);
        }

        string salvagedLogPath = Path.ChangeExtension(walFilePath, ".salvaged.clog");
        int totalChunks = 0;
        int validChunks = 0;
        long tornBytes = 0;
        uint expectedSeq = 1;

        try
        {
            await using (FileStream inStream = new(walFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (FileStream outStream = new(salvagedLogPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] headerBuffer = new byte[ReplayWalChunkHeader.HeaderSizeBytes];

                while (inStream.Position < inStream.Length)
                {
                    long chunkStartPos = inStream.Position;
                    int headerRead = await inStream.ReadAsync(headerBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (headerRead < ReplayWalChunkHeader.HeaderSizeBytes)
                    {
                        // Incomplete header => torn write at tail
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    if (!ReplayWalChunkHeader.TryReadFrom(headerBuffer, out ReplayWalChunkHeader header) || !header.IsValidMagic)
                    {
                        // Invalid magic constant => torn or corrupted block
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    if (header.SequenceNumber != expectedSeq)
                    {
                        // Out of sequence chunk
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    totalChunks++;

                    long remainingBytes = inStream.Length - inStream.Position;
                    if (remainingBytes < header.PayloadSizeBytes)
                    {
                        // Incomplete payload => torn write during unexpected power loss
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    byte[] payloadBuffer = new byte[header.PayloadSizeBytes];
                    int payloadRead = await inStream.ReadAsync(payloadBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (payloadRead < header.PayloadSizeBytes)
                    {
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    // Validate IEEE 802.3 CRC32 checksum
                    uint calculatedCrc = ReplayWalWriter.ComputeCrc32(payloadBuffer.AsSpan());
                    if (calculatedCrc != header.Crc32Checksum)
                    {
                        _logger?.LogWarning(
                            "[ReplayWalRecovery] CRC32 mismatch on Chunk #{Seq} for Match #{MatchId} (Expected 0x{Expected:X8}, Got 0x{Actual:X8}). Discarding tail.",
                            header.SequenceNumber, matchId, header.Crc32Checksum, calculatedCrc);
                        tornBytes += inStream.Length - chunkStartPos;
                        break;
                    }

                    // Chunk is 100% verified => write to salvaged log stream
                    await outStream.WriteAsync(payloadBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    validChunks++;
                    expectedSeq++;
                }

                await outStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            bool parquetGenerated = false;
            int eventCount = 0;

            if (validChunks > 0 && matchId > 0 && _ingestor != null)
            {
                // Ingest salvaged combat log into lakehouse Parquet export
                parquetGenerated = await _ingestor.IngestMatchAsync(matchId, salvagedLogPath, cancellationToken).ConfigureAwait(false);
            }

            // Rename WAL journal to .wal.processed to prevent duplicate ingestion loops
            string processedWalPath = walFilePath + ".processed";
            if (File.Exists(processedWalPath)) File.Delete(processedWalPath);
            File.Move(walFilePath, processedWalPath);

            _logger?.LogInformation(
                "[ReplayWalRecovery] Salvaged Match #{MatchId} on Port {Port}: {ValidChunks}/{TotalChunks} chunks valid ({TornBytes} torn bytes discarded). Parquet Export: {ParquetOk}.",
                matchId, port, validChunks, totalChunks, tornBytes, parquetGenerated);

            return new ReplayWalRecoveryResult(
                MatchId: matchId,
                Port: port,
                WalFilePath: walFilePath,
                SalvagedLogPath: salvagedLogPath,
                TotalChunksScanned: totalChunks,
                ValidChunksSalvaged: validChunks,
                TornBytesDiscarded: tornBytes,
                SalvagedEventCount: eventCount,
                ParquetGenerated: parquetGenerated,
                Success: true,
                ErrorMessage: string.Empty);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ReplayWalRecovery] Failed to salvage WAL journal at {Path}", walFilePath);

            return new ReplayWalRecoveryResult(
                MatchId: matchId,
                Port: port,
                WalFilePath: walFilePath,
                SalvagedLogPath: salvagedLogPath,
                TotalChunksScanned: totalChunks,
                ValidChunksSalvaged: validChunks,
                TornBytesDiscarded: tornBytes,
                SalvagedEventCount: 0,
                ParquetGenerated: false,
                Success: false,
                ErrorMessage: ex.Message);
        }
    }

    private string ResolveWalDirectory()
    {
        return Path.IsPathRooted(_options.ReplayWalDirectory)
            ? _options.ReplayWalDirectory
            : Path.Combine(_repoRoot, _options.ReplayWalDirectory);
    }
}
