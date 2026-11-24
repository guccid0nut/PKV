// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Kongor.Core.Services.Native;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Individual binary symbol, Lua CFunction, or CVar extracted from a game DLL/EXE.
/// </summary>
public sealed record EngineSymbolEntry
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "Unknown"; // "Export", "LuaCFunction", "CVar", "ConsoleCommand", "StringLiteral"
    public string? Signature { get; init; }
    public string? DefaultValue { get; init; }
    public string? Flags { get; init; }
    public string? Description { get; init; }
    public uint? Rva { get; init; }
    public int? Ordinal { get; init; }
    public IReadOnlyList<string> ContextNeighbors { get; init; } = [];
}

/// <summary>
/// Comprehensive introspection snapshot of an engine binary.
/// </summary>
public sealed record EngineInspectReport
{
    public string Status { get; init; } = "success";
    public string TargetBinary { get; init; } = string.Empty;
    public string ResolvedFullPath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public string Architecture { get; init; } = "x64";
    public int TotalExportsCount { get; init; }
    public int TotalMatchedSymbols { get; init; }
    public IReadOnlyList<EngineSymbolEntry> Symbols { get; init; } = [];
    public string Timestamp { get; init; } = string.Empty;
}

/// <summary>
/// Interface for Engine Binary & CVar introspection.
/// </summary>
public interface IEngineInspectService
{
    Task<EngineInspectReport> InspectBinaryAsync(
        string target,
        string? query = null,
        string category = "all",
        int maxResults = 50,
        CancellationToken ct = default);
}

/// <summary>
/// Fast offline PE binary symbol, export table, Lua CFunction registration, and CVar introspector.
/// Inspects k2_x64.dll, cgame_x64.dll, game_shared_x64.dll, hon_x64.exe, etc.
/// in strictly read-only mode with in-memory caching.
/// </summary>
public sealed class EngineInspectService : IEngineInspectService
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CachedBinarySymbols> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedBinarySymbols(
        DateTime LastWriteTimeUtc,
        long FileSizeBytes,
        int TotalExports,
        List<EngineSymbolEntry> AllEntries);

    public EngineInspectService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EngineInspectReport> InspectBinaryAsync(
        string target,
        string? query = null,
        string category = "all",
        int maxResults = 50,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        string? resolvedPath = ResolveBinaryPath(target);
        if (resolvedPath == null || !File.Exists(resolvedPath))
        {
            return new EngineInspectReport
            {
                Status = "error",
                TargetBinary = target,
                Timestamp = _timeProvider.GetUtcNow().ToString("o"),
                Symbols =
                [
                    new EngineSymbolEntry
                    {
                        Name = target,
                        Type = "Error",
                        Description = $"Engine binary '{target}' could not be located in client or server directories."
                    }
                ]
            };
        }

        FileInfo fi = new(resolvedPath);
        CachedBinarySymbols cached = await GetOrLoadSymbolsAsync(resolvedPath, fi, ct).ConfigureAwait(false);

        // Filter symbols based on query and category
        IEnumerable<EngineSymbolEntry> filtered = cached.AllEntries;

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(s => s.Type.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            filtered = filtered.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (s.Description != null && s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (s.Signature != null && s.Signature.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        List<EngineSymbolEntry> matchedList = filtered.Take(Math.Max(1, maxResults)).ToList();

        return new EngineInspectReport
        {
            Status = "success",
            TargetBinary = Path.GetFileName(resolvedPath),
            ResolvedFullPath = resolvedPath,
            FileSizeBytes = cached.FileSizeBytes,
            Architecture = "x64",
            TotalExportsCount = cached.TotalExports,
            TotalMatchedSymbols = matchedList.Count,
            Symbols = matchedList,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    private async Task<CachedBinarySymbols> GetOrLoadSymbolsAsync(string filePath, FileInfo fi, CancellationToken ct)
    {
        if (_cache.TryGetValue(filePath, out CachedBinarySymbols? cached) && cached.LastWriteTimeUtc == fi.LastWriteTimeUtc)
        {
            return cached;
        }

        // Parse symbols on a background task
        CachedBinarySymbols parsed = await Task.Run(() => ParseBinarySymbols(filePath, fi), ct).ConfigureAwait(false);
        _cache[filePath] = parsed;
        return parsed;
    }

    private static CachedBinarySymbols ParseBinarySymbols(string filePath, FileInfo fi)
    {
        List<EngineSymbolEntry> entries = [];
        int totalExports = 0;

        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using PEReader peReader = new(fs);

        PEHeaders headers = peReader.PEHeaders;
        PEHeader? peHeader = headers.PEHeader;
        if (peHeader == null)
        {
            return new CachedBinarySymbols(fi.LastWriteTimeUtc, fi.Length, 0, entries);
        }

        // 1. Parse PE Export Directory
        DirectoryEntry exportDir = peHeader.ExportTableDirectory;
        if (exportDir.RelativeVirtualAddress != 0 && exportDir.Size > 0)
        {
            try
            {
                ParseExports(peReader, exportDir, entries, out totalExports);
            }
            catch
            {
                // Non-fatal if export table is obfuscated or non-standard
            }
        }

        // 2. Scan string tables in sections (.rdata, .data)
        ParseStringTables(peReader, entries);

        return new CachedBinarySymbols(fi.LastWriteTimeUtc, fi.Length, totalExports, entries);
    }

    private static void ParseExports(
        PEReader peReader,
        DirectoryEntry exportDir,
        List<EngineSymbolEntry> entries,
        out int totalExports)
    {
        totalExports = 0;
        int exportRva = exportDir.RelativeVirtualAddress;
        SectionHeader section = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
            exportRva >= s.VirtualAddress && exportRva < s.VirtualAddress + s.VirtualSize);

        if (section.VirtualAddress == 0) return;

        PEMemoryBlock sectionData = peReader.GetSectionData(section.VirtualAddress);
        BlobReader reader = sectionData.GetReader();

        int exportOffset = exportRva - section.VirtualAddress;
        if (exportOffset + 40 > reader.Length) return;

        reader.Offset = exportOffset;
        reader.ReadUInt32(); // Characteristics
        reader.ReadUInt32(); // TimeDateStamp
        reader.ReadUInt16(); // MajorVersion
        reader.ReadUInt16(); // MinorVersion
        int nameRva = (int)reader.ReadUInt32();
        int ordinalBase = (int)reader.ReadUInt32();
        int numFunctions = (int)reader.ReadUInt32();
        int numNames = (int)reader.ReadUInt32();
        int funcRva = (int)reader.ReadUInt32();
        int namesRva = (int)reader.ReadUInt32();
        int ordinalsRva = (int)reader.ReadUInt32();

        totalExports = numNames;

        if (numNames <= 0 || numNames > 20000) return;

        int namesOffset = namesRva - section.VirtualAddress;
        int ordinalsOffset = ordinalsRva - section.VirtualAddress;

        if (namesOffset < 0 || namesOffset >= sectionData.Length ||
            ordinalsOffset < 0 || ordinalsOffset >= sectionData.Length)
        {
            return;
        }

        for (int i = 0; i < Math.Min(numNames, 5000); i++)
        {
            int curNamePtrOffset = namesOffset + (i * 4);
            if (curNamePtrOffset + 4 > sectionData.Length) break;

            reader.Offset = curNamePtrOffset;
            int curNameRva = (int)reader.ReadUInt32();
            int curNameOffset = curNameRva - section.VirtualAddress;

            if (curNameOffset >= 0 && curNameOffset < sectionData.Length)
            {
                reader.Offset = curNameOffset;
                string exportName = ReadNullTerminatedAscii(ref reader);
                if (!string.IsNullOrWhiteSpace(exportName))
                {
                    reader.Offset = ordinalsOffset + (i * 2);
                    int ordinal = ordinalBase + reader.ReadUInt16();

                    entries.Add(new EngineSymbolEntry
                    {
                        Name = exportName,
                        Type = "Export",
                        Ordinal = ordinal,
                        Rva = (uint)curNameRva,
                        Description = $"PE exported binary symbol (Ordinal {ordinal})"
                    });
                }
            }
        }
    }

    private static string ReadNullTerminatedAscii(ref BlobReader reader)
    {
        List<byte> bytes = [];
        while (reader.RemainingBytes > 0)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void ParseStringTables(PEReader peReader, List<EngineSymbolEntry> entries)
    {
        HashSet<string> existingNames = new(entries.Select(e => e.Name), StringComparer.Ordinal);

        foreach (SectionHeader sec in peReader.PEHeaders.SectionHeaders)
        {
            string secName = sec.Name.Trim('\0', ' ');
            if (!secName.Equals(".rdata", StringComparison.OrdinalIgnoreCase) &&
                !secName.Equals(".data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PEMemoryBlock secData = peReader.GetSectionData(sec.VirtualAddress);
            byte[] bytes = secData.GetReader().ReadBytes(secData.Length);

            List<(int Offset, string Text)> extracted = [];
            int strStart = -1;

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b >= 32 && b <= 126)
                {
                    if (strStart == -1) strStart = i;
                }
                else if (b == 0)
                {
                    if (strStart != -1)
                    {
                        int len = i - strStart;
                        if (len >= 3 && len <= 100 &&
                            BinaryStringAndNumericScanner.TryConsumeValidUtf8Codepoint(bytes, strStart, out int _, out uint _))
                        {
                            string s = Encoding.ASCII.GetString(bytes, strStart, len);
                            extracted.Add((strStart, s));
                        }
                        strStart = -1;
                    }
                }
                else
                {
                    strStart = -1;
                }
            }

            // Identify symbols, Lua functions, CVars, and Commands
            for (int i = 0; i < extracted.Count; i++)
            {
                (int offset, string str) = extracted[i];
                if (existingNames.Contains(str)) continue;

                // 1. CVar Detection (starts with cvar_, cl_, cg_, svr_, vid_, sys_, gl_, d3d_)
                if (IsCVarPrefix(str))
                {
                    string? defVal = (i + 1 < extracted.Count && extracted[i + 1].Offset - (offset + str.Length) < 32)
                        ? extracted[i + 1].Text : null;
                    string? desc = (i + 2 < extracted.Count && extracted[i + 2].Offset - (offset + str.Length) < 96)
                        ? extracted[i + 2].Text : null;

                    entries.Add(new EngineSymbolEntry
                    {
                        Name = str,
                        Type = "CVar",
                        DefaultValue = defVal,
                        Description = desc ?? $"K2 Engine configuration variable ({str})",
                        Flags = "Configurable",
                        Rva = (uint)(sec.VirtualAddress + offset),
                        ContextNeighbors = GetNeighbors(extracted, i)
                    });
                    existingNames.Add(str);
                    continue;
                }

                // 2. Console Command Detection (starts with cmd_ or matches action verbs)
                if (str.StartsWith("cmd_", StringComparison.OrdinalIgnoreCase) ||
                    str.StartsWith("StartGame", StringComparison.OrdinalIgnoreCase) ||
                    str.StartsWith("Connect", StringComparison.OrdinalIgnoreCase) ||
                    str.StartsWith("AddFakePlayer", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new EngineSymbolEntry
                    {
                        Name = str,
                        Type = "ConsoleCommand",
                        Description = $"K2 Engine console command ({str})",
                        Rva = (uint)(sec.VirtualAddress + offset),
                        ContextNeighbors = GetNeighbors(extracted, i)
                    });
                    existingNames.Add(str);
                    continue;
                }

                // 3. Lua CFunction Detection (PascalCase identifiers surrounded by related function names)
                if (IsLikelyLuaFunctionName(str))
                {
                    IReadOnlyList<string> neighbors = GetNeighbors(extracted, i);
                    bool hasLuaNeighbors = neighbors.Any(n => IsLikelyLuaFunctionName(n));

                    if (hasLuaNeighbors)
                    {
                        string sig = InferSignature(str, neighbors);
                        entries.Add(new EngineSymbolEntry
                        {
                            Name = str,
                            Type = "LuaCFunction",
                            Signature = sig,
                            Description = $"K2 in-game Lua CFunction binding ({str})",
                            Rva = (uint)(sec.VirtualAddress + offset),
                            ContextNeighbors = neighbors
                        });
                        existingNames.Add(str);
                    }
                }
            }
        }
    }

    private static bool IsCVarPrefix(string s)
    {
        return s.StartsWith("cl_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("cg_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("svr_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("sys_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("vid_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("gl_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("d3d_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("sound_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("net_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("cam_", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("host_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyLuaFunctionName(string s)
    {
        if (s.Length < 4 || s.Length > 45) return false;
        if (s.Contains(' ') || s.Contains('.') || s.Contains('/') || s.Contains('\\')) return false;

        // PascalCase letters & digits: StartWatchingMatch, GetGameModeString, etc.
        return char.IsUpper(s[0]) && Regex.IsMatch(s, @"^[A-Z][a-zA-Z0-9_]+$");
    }

    private static IReadOnlyList<string> GetNeighbors(List<(int Offset, string Text)> extracted, int index)
    {
        List<string> neighbors = [];
        for (int i = Math.Max(0, index - 3); i <= Math.Min(extracted.Count - 1, index + 3); i++)
        {
            if (i != index) neighbors.Add(extracted[i].Text);
        }
        return neighbors;
    }

    private static string InferSignature(string funcName, IReadOnlyList<string> neighbors)
    {
        // Infer parameter patterns for common K2 routines
        if (funcName.Equals("StartWatchingMatch", StringComparison.OrdinalIgnoreCase))
        {
            return "string url, string timestamp, string urlbackup, string version";
        }
        if (funcName.StartsWith("ChatSpectate", StringComparison.OrdinalIgnoreCase))
        {
            return "int clientNumber, bool silent = false";
        }
        if (funcName.StartsWith("Get", StringComparison.OrdinalIgnoreCase) && funcName.EndsWith("Exp", StringComparison.OrdinalIgnoreCase))
        {
            return "void -> int exp";
        }
        if (funcName.StartsWith("Get", StringComparison.OrdinalIgnoreCase) && funcName.EndsWith("String", StringComparison.OrdinalIgnoreCase))
        {
            return "int id -> string result";
        }
        if (funcName.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
        {
            return "value val";
        }

        return "varargs ...";
    }

    private static string? ResolveBinaryPath(string target)
    {
        if (Path.IsPathRooted(target) && File.Exists(target)) return target;

        string root = FindProjectRoot();
        string[] candidateDirs =
        [
            Path.Combine(root, "games", "client"),
            Path.Combine(root, "games", "client", "game"),
            Path.Combine(root, "games", "client", "editor"),
            Path.Combine(root, "games", "client", "windows-x64"),
            Path.Combine(root, "games", "client", "windows-x64", "game"),
            Path.Combine(root, "games", "server"),
            Path.Combine(root, "games", "server", "game"),
            Path.Combine(root, "games", "server", "windows-x64"),
            Path.Combine(root, "games", "server", "windows-x64", "game"),
            Path.Combine(root, "games", "shared")
        ];

        string fileName = Path.GetFileName(target);

        foreach (string dir in candidateDirs)
        {
            string p = Path.Combine(dir, fileName);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static string FindProjectRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) || File.Exists(Path.Combine(current, "ProjectKongor.sln")))
            {
                return current;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "ProjectKongor.slnx")) || File.Exists(Path.Combine(baseDir, "ProjectKongor.sln")))
            {
                return baseDir;
            }
            string? parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
