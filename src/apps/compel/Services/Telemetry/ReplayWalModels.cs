// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayWalModels.cs
// System: COMPEL Telemetry - Strategic Recommendation SR-04
// Subsystem: Background Replay Write-Ahead Logging (WAL) Models & Beacon
// ============================================================================

using System;
using System.IO;

namespace COMPEL.Services.Telemetry;

/// <summary>
/// 16-byte fixed-size binary header prepended to every 64 KB WAL chunk.
/// Format:
///   [0..3]   Magic constant (0x4C41574B => ASCII "KWAL")
///   [4..7]   SequenceNumber (uint32, monotonically increasing 1..N)
///   [8..11]  PayloadSizeBytes (uint32)
///   [12..15] Crc32Checksum (uint32, IEEE 802.3 over Payload bytes)
/// </summary>
public readonly record struct ReplayWalChunkHeader(
    uint Magic,
    uint SequenceNumber,
    uint PayloadSizeBytes,
    uint Crc32Checksum)
{
    public const uint ExpectedMagic = 0x4C41574B; // "KWAL"
    public const int HeaderSizeBytes = 16;

    public bool IsValidMagic => Magic == ExpectedMagic;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSizeBytes)
            throw new ArgumentException("Destination span too small for WAL header", nameof(destination));

        BitConverter.TryWriteBytes(destination[0..4], Magic);
        BitConverter.TryWriteBytes(destination[4..8], SequenceNumber);
        BitConverter.TryWriteBytes(destination[8..12], PayloadSizeBytes);
        BitConverter.TryWriteBytes(destination[12..16], Crc32Checksum);
    }

    public static bool TryReadFrom(ReadOnlySpan<byte> source, out ReplayWalChunkHeader header)
    {
        if (source.Length < HeaderSizeBytes)
        {
            header = default;
            return false;
        }

        uint magic = BitConverter.ToUInt32(source[0..4]);
        uint seq = BitConverter.ToUInt32(source[4..8]);
        uint size = BitConverter.ToUInt32(source[8..12]);
        uint crc = BitConverter.ToUInt32(source[12..16]);

        header = new ReplayWalChunkHeader(magic, seq, size, crc);
        return true;
    }
}

/// <summary>
/// Ultra-compact status beacon (~48-64 bytes) describing the active WAL journal state.
/// Designed for zero-overhead embedding into the in-engine Ctrl+Shift+D diagnostic QR code
/// and live telemetry multiplexer streams.
/// </summary>
public sealed record ReplayWalBeacon(
    long MatchId,
    int Port,
    uint Sequence,
    long FlushedBytes,
    uint LastCrc32,
    string Status)
{
    /// <summary>
    /// Generates a standardized, lightweight URI query string representation (~55 bytes)
    /// suitable for deep-link protocol handlers.
    /// </summary>
    public string ToQrString() =>
        $"kongor://wal?m={MatchId}&p={Port}&s={Sequence}&b={FlushedBytes}&c={LastCrc32:X8}&st={Status}";

    /// <summary>
    /// Generates a web telemetry dashboard URL suitable for opening directly in a browser.
    /// </summary>
    public string ToWebLink(string baseDomain = "https://telemetry.kongor.online") =>
        $"{baseDomain.TrimEnd('/')}/match/{MatchId}?port={Port}";

    /// <summary>
    /// Generates a standardized GitHub-flavored Markdown triage block for immediate bug reports and dev chat pasting.
    /// </summary>
    public string ToTriageSnapshotMarkdown(
        string? map = null,
        string? hero = null,
        int? heroLevel = null,
        string? team = null,
        string? playerCoordinates = null,
        string? fps = null,
        string? ping = null,
        string? clientBuild = null,
        string? activeMods = null,
        string? recentErrors = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### 🐛 Kongor Triage Snapshot");
        sb.AppendLine($"- **Match ID**: {MatchId} | **Port**: {Port} | **Map**: {map ?? "caldavar"}");
        if (!string.IsNullOrEmpty(hero))
        {
            sb.AppendLine($"- **Hero**: `{hero}`{(heroLevel.HasValue ? $" (Level {heroLevel.Value})" : "")} | **Team**: {team ?? "Legion"}");
        }
        if (!string.IsNullOrEmpty(playerCoordinates))
        {
            sb.AppendLine($"- **Position**: `{playerCoordinates}`");
        }
        sb.AppendLine($"- **Client Build**: {clientBuild ?? "x64 Vulkan"}");
        sb.AppendLine($"- **Performance**: {fps ?? "144"} FPS | Ping: {ping ?? "12"} ms");
        sb.AppendLine($"- **WAL State**: Seq #{Sequence} | Flushed: {FlushedBytes / (1024.0 * 1024.0):F2} MB | CRC: 0x{LastCrc32:X8} [{Status}]");
        if (!string.IsNullOrEmpty(activeMods))
        {
            sb.AppendLine($"- **Active Mod Stack**: {activeMods}");
        }
        sb.AppendLine($"- **Recent Errors**: {recentErrors ?? "None"}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates a compact colon-delimited token string for SSE / UDP telemetry streams.
    /// </summary>
    public string ToCompactTelemetryString() =>
        $"wal:{MatchId}:{Port}:{Sequence}:{FlushedBytes}:{LastCrc32:X8}:{Status}";
}

/// <summary>
/// Detailed diagnostic result of a WAL recovery and salvage operation.
/// </summary>
public sealed record ReplayWalRecoveryResult(
    long MatchId,
    int Port,
    string WalFilePath,
    string SalvagedLogPath,
    int TotalChunksScanned,
    int ValidChunksSalvaged,
    long TornBytesDiscarded,
    int SalvagedEventCount,
    bool ParquetGenerated,
    bool Success,
    string ErrorMessage);
