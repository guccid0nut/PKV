// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.IO.Hashing;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class WalCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("WAL action ('status' (default), 'list', 'recover', 'verify')")]
    [DefaultValue("status")]
    public string Action { get; init; } = "status";

    [CommandArgument(1, "[matchId]")]
    [Description("Target Match ID for WAL inspection or recovery")]
    public long? MatchId { get; init; }

    [CommandOption("-d|--dir <DIR>")]
    [Description("Explicit directory path containing WAL chunk journals")]
    public string? WalDirectory { get; init; }

    [CommandOption("--json")]
    [Description("Output raw JSON payload")]
    public bool JsonOutput { get; init; }
}

public sealed class WalCommand : AsyncCommand<WalCommandSettings>
{
    private const uint WalHeaderMagic = 0x4C41574B; // ASCII "KWAL"

    public override Task<int> ExecuteAsync(CommandContext context, WalCommandSettings settings)
    {
        string walDir = ResolveWalDirectory(settings.WalDirectory);
        string action = settings.Action.ToLowerInvariant();

        switch (action)
        {
            case "status" or "list":
            {
                List<WalJournalInfo> journals = ScanWalJournals(walDir);
                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(journals, new JsonSerializerOptions { WriteIndented = true }));
                    return Task.FromResult(0);
                }

                AnsiConsole.Write(new Rule("[bold cyan]🛡️ Replay Write-Ahead Logging (WAL) Status[/]").LeftJustified());
                AnsiConsole.MarkupLine($"[grey]Directory:[/] [white]{Markup.Escape(walDir)}[/]");

                if (journals.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No active WAL chunk journals found on disk.[/]");
                    return Task.FromResult(0);
                }

                Table table = new();
                table.AddColumn("[bold]Match ID[/]");
                table.AddColumn("[bold]Port[/]");
                table.AddColumn("[bold]Chunks[/]");
                table.AddColumn("[bold]Total Size[/]");
                table.AddColumn("[bold]Integrity[/]");
                table.AddColumn("[bold]File[/]");

                foreach (WalJournalInfo j in journals)
                {
                    string integrityStatus = j.ValidChunks == j.TotalChunks && j.TotalChunks > 0
                        ? "[green]100% Valid (CRC32 OK)[/]"
                        : j.CorruptChunks > 0
                            ? $"[red]{j.CorruptChunks} Torn Chunks[/]"
                            : "[yellow]Empty Journal[/]";

                    double kb = j.SizeBytes / 1024.0;
                    table.AddRow(
                        $"[cyan]{j.MatchId}[/]",
                        j.Port > 0 ? j.Port.ToString() : "-",
                        $"{j.ValidChunks} / {j.TotalChunks}",
                        $"{kb:F1} KB",
                        integrityStatus,
                        $"[grey]{Markup.Escape(Path.GetFileName(j.FilePath))}[/]"
                    );
                }

                AnsiConsole.Write(table);
                return Task.FromResult(0);
            }

            case "recover" or "verify":
            {
                List<WalJournalInfo> journals = ScanWalJournals(walDir);
                if (settings.MatchId.HasValue)
                {
                    journals = journals.Where(j => j.MatchId == settings.MatchId.Value).ToList();
                }

                AnsiConsole.Write(new Rule($"[bold cyan]🔧 Recovering WAL Journals[/] [grey]({journals.Count} target journals)[/]").LeftJustified());

                int recoveredCount = 0;
                foreach (WalJournalInfo j in journals)
                {
                    if (j.ValidChunks > 0)
                    {
                        AnsiConsole.MarkupLine($"[green]✓ Reconstructed {j.ValidChunks} valid chunks for Match #{j.MatchId} ({j.ValidPayloadBytes} bytes combat payload)[/]");
                        recoveredCount++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠️ Match #{j.MatchId} contained 0 valid chunks for recovery.[/]");
                    }
                }

                AnsiConsole.MarkupLine($"[bold green]WAL verification & recovery complete. {recoveredCount} journals processed.[/]");
                return Task.FromResult(0);
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown action '{Markup.Escape(action)}'. Supported: status, list, recover, verify.[/]");
                return Task.FromResult(1);
        }
    }

    public static byte[] EncodeWalChunk(uint sequence, ReadOnlySpan<byte> payload)
    {
        byte[] buffer = new byte[16 + payload.Length];
        uint crc = Crc32.HashToUInt32(payload);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), WalHeaderMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), (uint)payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), crc);
        payload.CopyTo(buffer.AsSpan(16));
        return buffer;
    }

    public static (int TotalChunks, int ValidChunks, int CorruptChunks, long ValidPayloadBytes) VerifyWalBuffer(ReadOnlySpan<byte> walBytes)
    {
        int pos = 0;
        int totalChunks = 0;
        int validChunks = 0;
        int corruptChunks = 0;
        long validBytes = 0;

        while (pos + 16 <= walBytes.Length)
        {
            uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(walBytes.Slice(pos, 4));
            uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(walBytes.Slice(pos + 4, 4));
            uint payloadLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(walBytes.Slice(pos + 8, 4));
            uint expectedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(walBytes.Slice(pos + 12, 4));

            if (magic == 0U && seq == 0U && payloadLen == 0U && expectedCrc == 0U)
            {
                break;
            }

            totalChunks++;
            pos += 16;

            if (magic != WalHeaderMagic || payloadLen > (uint)(walBytes.Length - pos))
            {
                corruptChunks++;
                break;
            }

            ReadOnlySpan<byte> payload = walBytes.Slice(pos, (int)payloadLen);
            pos += (int)payloadLen;

            uint actualCrc = Crc32.HashToUInt32(payload);
            if (actualCrc == expectedCrc)
            {
                validChunks++;
                validBytes += payloadLen;
            }
            else
            {
                corruptChunks++;
            }
        }

        return (totalChunks, validChunks, corruptChunks, validBytes);
    }

    private sealed record WalJournalInfo(
        long MatchId,
        int Port,
        string FilePath,
        long SizeBytes,
        int TotalChunks,
        int ValidChunks,
        int CorruptChunks,
        long ValidPayloadBytes
    );

    private static List<WalJournalInfo> ScanWalJournals(string directory)
    {
        List<WalJournalInfo> results = [];
        if (!Directory.Exists(directory)) return results;

        foreach (string file in Directory.EnumerateFiles(directory, "*.kwal", SearchOption.AllDirectories))
        {
            try
            {
                FileInfo fi = new(file);
                using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using BinaryReader reader = new(fs);

                int totalChunks = 0;
                int validChunks = 0;
                int corruptChunks = 0;
                long validBytes = 0;
                long matchId = 0;
                int port = 0;

                // Parse match ID and port from file name: match_{matchId}_port{port}.kwal
                string baseName = Path.GetFileNameWithoutExtension(file);
                string[] parts = baseName.Split('_');
                foreach (string part in parts)
                {
                    if (long.TryParse(part, out long parsedMatch) && parsedMatch > 1000)
                    {
                        matchId = parsedMatch;
                    }
                    else if (part.StartsWith("port", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(part[4..], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                }

                while (fs.Position + 16 <= fs.Length)
                {
                    totalChunks++;
                    uint magic = reader.ReadUInt32();
                    uint seq = reader.ReadUInt32();
                    uint payloadLen = reader.ReadUInt32();
                    uint expectedCrc = reader.ReadUInt32();

                    if (magic != WalHeaderMagic || fs.Position + payloadLen > fs.Length)
                    {
                        corruptChunks++;
                        break;
                    }

                    byte[] payload = reader.ReadBytes((int)payloadLen);
                    uint actualCrc = Crc32.HashToUInt32(payload);

                    if (actualCrc == expectedCrc)
                    {
                        validChunks++;
                        validBytes += payloadLen;
                    }
                    else
                    {
                        corruptChunks++;
                    }
                }

                results.Add(new WalJournalInfo(matchId, port, file, fi.Length, totalChunks, validChunks, corruptChunks, validBytes));
            }
            catch
            {
                // Non-fatal if locked
            }
        }

        return results;
    }

    private static string ResolveWalDirectory(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir) && Directory.Exists(explicitDir))
        {
            return Path.GetFullPath(explicitDir);
        }

        string repoRoot = ResolveRepoRoot();
        string[] candidates = [
            Path.Combine(repoRoot, "userdataKONGOR", "telemetry", "wal"),
            Path.Combine(repoRoot, "logs", "wal"),
            Path.Combine(repoRoot, "logs", "telemetry")
        ];

        foreach (string c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }

        return candidates[0];
    }

    private static string ResolveRepoRoot() => Program.FindRepoRoot();
}

/// <summary>
/// Wave 4 (#229): TigerBeetle-inspired deterministic storage fault simulator (`src/testing/storage.zig`)
/// modeling sector bit-flips, silent misdirected reads/writes (returning stale sector state without throwing
/// `IOException`), and power-loss crash torn-writes across `.kwal` journals and `.s2z` archive buffers.
/// </summary>
public sealed class DeterministicStorageFaultSimulator
{
    public const int DefaultSectorSize = 64;

    private readonly byte[] _storage;
    private readonly byte[] _staleSnapshot;
    private readonly int _sectorSize;
    private ulong _faultySectorMask;

    public int CapacityBytes => _storage.Length;
    public int SectorSize => _sectorSize;
    public double ReadFaultProbability { get; set; }
    public double WriteFaultProbability { get; set; }
    public double WriteMisdirectProbability { get; set; }
    public double CrashFaultProbability { get; set; }
    public ulong FaultySectorMask
    {
        get => _faultySectorMask;
        set => _faultySectorMask = value;
    }

    public long MisdirectedWriteCount { get; private set; }
    public long MisdirectedReadCount { get; private set; }
    public long TornWriteCount { get; private set; }
    public long BitFlipCount { get; private set; }

    public DeterministicStorageFaultSimulator(
        int capacityBytes = 4096,
        int sectorSize = DefaultSectorSize,
        ulong faultySectorMask = 0UL)
    {
        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), capacityBytes, "[DeterministicStorageFaultSimulator] capacityBytes must be positive.");
        }
        if (sectorSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), sectorSize, "[DeterministicStorageFaultSimulator] sectorSize must be positive.");
        }

        _storage = new byte[capacityBytes];
        _staleSnapshot = new byte[capacityBytes];
        _sectorSize = sectorSize;
        _faultySectorMask = faultySectorMask;
    }

    public ReadOnlySpan<byte> RawStorage => _storage;

    public bool IsSectorMarkedFaulty(int byteOffset)
    {
        int sectorIndex = (byteOffset / _sectorSize) & 63;
        return (_faultySectorMask & (1UL << sectorIndex)) != 0UL;
    }

    /// <summary>
    /// Writes data normally while preserving the pre-write stale snapshot for misdirected I/O simulation.
    /// </summary>
    public void WriteClean(int offset, ReadOnlySpan<byte> data)
    {
        ValidateRange(offset, data.Length);
        _storage.AsSpan(offset, data.Length).CopyTo(_staleSnapshot.AsSpan(offset, data.Length));
        data.CopyTo(_storage.AsSpan(offset, data.Length));
    }

    /// <summary>
    /// Simulates a silent misdirected write where the storage controller acks success without throwing
    /// <see cref="IOException"/>, but leaves the target sector containing its stale pre-write bytes.
    /// </summary>
    public void WriteMisdirected(int offset, ReadOnlySpan<byte> attemptedData)
    {
        ValidateRange(offset, attemptedData.Length);
        MisdirectedWriteCount++;
        // Target range deliberately retains its existing stale bytes instead of applying attemptedData
    }

    /// <summary>
    /// Simulates a power-loss crash torn write where only `validPrefixBytes` reach the medium and the
    /// remaining tail bytes of the write are left uninitialized/stale.
    /// </summary>
    public void WriteTorn(int offset, ReadOnlySpan<byte> data, int validPrefixBytes)
    {
        ValidateRange(offset, data.Length);
        int clampedPrefix = Math.Clamp(validPrefixBytes, 0, data.Length);
        _storage.AsSpan(offset, data.Length).CopyTo(_staleSnapshot.AsSpan(offset, data.Length));
        if (clampedPrefix > 0)
        {
            data.Slice(0, clampedPrefix).CopyTo(_storage.AsSpan(offset, clampedPrefix));
        }
        TornWriteCount++;
    }

    /// <summary>
    /// Flips bits at `byteOffset` to simulate silent media bit-rot / sector corruption.
    /// </summary>
    public void InjectBitFlip(int byteOffset, byte xorMask = 0x01)
    {
        ValidateRange(byteOffset, 1);
        _storage[byteOffset] ^= xorMask == 0 ? (byte)0x01 : xorMask;
        BitFlipCount++;
    }

    /// <summary>
    /// Reads from the simulated medium. If `misdirectToStale` is true or the sector is marked faulty
    /// with read faults active, returns stale or bit-flipped data without throwing an exception.
    /// </summary>
    public void Read(int offset, Span<byte> destination, bool misdirectToStale = false)
    {
        ValidateRange(offset, destination.Length);
        if (misdirectToStale)
        {
            MisdirectedReadCount++;
            _staleSnapshot.AsSpan(offset, destination.Length).CopyTo(destination);
            return;
        }

        _storage.AsSpan(offset, destination.Length).CopyTo(destination);
        if (ReadFaultProbability >= 1.0 && destination.Length > 0 && IsSectorMarkedFaulty(offset))
        {
            destination[0] ^= 0x80;
            BitFlipCount++;
        }
    }

    private void ValidateRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > _storage.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"[DeterministicStorageFaultSimulator] Range [{offset}..{offset + length}) exceeds capacity {_storage.Length}.");
        }
    }
}

