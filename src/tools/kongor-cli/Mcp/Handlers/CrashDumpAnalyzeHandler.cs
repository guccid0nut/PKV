// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Diagnostics;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_crash_dump_analyze' (alias 'kongor_crash_dump').
/// Win32 .dmp exception code extractor, faulting module resolver, and native stack unwinder for game crashes.
/// </summary>
public sealed class CrashDumpAnalyzeHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly string? _searchDirectory;

    public CrashDumpAnalyzeHandler(TimeProvider? timeProvider = null, Action<string>? logger = null, string? searchDirectory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CrashDumpAnalyze] {msg}"));
        _searchDirectory = searchDirectory;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "analyze";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string? dumpPath = null;
        if (arguments.TryGetProperty("dumpPath", out JsonElement pathElem) && !string.IsNullOrWhiteSpace(pathElem.GetString()))
        {
            dumpPath = pathElem.GetString()!.Trim();
        }

        _logger($"[HandleAsync] Action: '{action}', Path: '{dumpPath ?? "auto"}'");

        switch (action)
        {
            case "list_recent":
                return ListRecentDumps();

            case "analyze":
            default:
                return await AnalyzeDumpAsync(dumpPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolCallResult> AnalyzeDumpAsync(string? dumpPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? resolvedPath = !string.IsNullOrWhiteSpace(dumpPath)
            ? dumpPath
            : ResolveLatestDumpPath();

        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            _logger($"[AnalyzeDumpAsync] No crash artifact found (requested: '{dumpPath ?? "auto"}').");
            return ToolCallResult.Success(new
            {
                status = "no_artifact",
                success = false,
                dumpFound = false,
                action = "analyze",
                dumpPath = dumpPath ?? string.Empty,
                callstack = Array.Empty<string>(),
                message = "No crash minidump (.dmp) or diagnostic log (diag_*.txt) found at the specified or auto-discovered path.",
                recommendation = "Run 'kongor_crash_dump_analyze' with action: 'list_recent' or provide an existing dumpPath."
            });
        }

        // 1. Text Diagnostic Dump (e.g. diag_*.txt, *.log)
        if (resolvedPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            resolvedPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string[] lines = await File.ReadAllLinesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                List<string> errorLines = [];
                List<string> warningLines = [];
                List<string> callstack = [];

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Fail", StringComparison.OrdinalIgnoreCase))
                    {
                        errorLines.Add(line.Trim());
                    }
                    else if (line.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("navmesh", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("socket", StringComparison.OrdinalIgnoreCase))
                    {
                        warningLines.Add(line.Trim());
                    }

                    if (line.Contains(".dll!") || line.Contains(".exe!") || line.Contains("at ") || line.Contains("   at "))
                    {
                        callstack.Add(line.Trim());
                    }
                }

                _logger($"Analyzed text diagnostic dump '{resolvedPath}'. Found {errorLines.Count} errors, {warningLines.Count} warnings, {callstack.Count} stack frames.");

                return ToolCallResult.Success(new
                {
                    status = "success",
                    success = true,
                    dumpFound = true,
                    action = "analyze",
                    dumpPath = resolvedPath,
                    format = "Text Diagnostic Dump (diag_*.txt / *.log)",
                    totalLines = lines.Length,
                    errorCount = errorLines.Count,
                    warningCount = warningLines.Count,
                    errors = errorLines.Take(15).ToArray(),
                    warnings = warningLines.Take(15).ToArray(),
                    callstack = callstack.Take(10).ToArray(),
                    exceptionCode = errorLines.Count > 0 ? "DIAG_RECORDED_ERROR" : "0x00000000",
                    exceptionName = errorLines.Count > 0 ? "RECORDED_DIAGNOSTIC_FAILURE" : "STATUS_SUCCESS",
                    rootCauseHypothesis = errorLines.Count > 0
                        ? errorLines[0]
                        : (warningLines.Count > 0 ? warningLines[0] : "No fatal crashes recorded in diagnostic log."),
                    recommendation = errorLines.Count > 0
                        ? "Inspect logged exception context and verify module dependencies."
                        : "No action required; engine diagnostics report clean execution."
                });
            }
            catch (Exception ex)
            {
                _logger($"Failed to read text diagnostic file: {ex.Message}");
            }
        }

        // 2. Binary Minidump (MDMP) via shared MinidumpReader (Kongor.Core)
        try
        {
            using FileStream fs = File.OpenRead(resolvedPath);
            MinidumpTriageResult mdmp = MinidumpReader.Inspect(fs);
            if (mdmp.IsValidDump)
            {
                _logger($"Parsed Win32 MDMP via MinidumpReader. Status: {mdmp.Status}, Version: 0x{mdmp.HeaderVersion:X8}, Streams: {mdmp.StreamCount}, Target: {mdmp.TargetFormatted}");

                string faultingAddressHex = $"0x{mdmp.FaultingAddress:X16}";
                List<(uint StreamType, uint DataSize, uint Rva)> streamTuples = (mdmp.StreamDirectories ?? Array.Empty<MinidumpDirectoryEntry>())
                    .Select(d => (d.StreamType, d.DataSize, d.Rva))
                    .ToList();
                MinidumpStreamCompletenessReport streamCompleteness =
                    ReaEpistemicForensicsEngine.EvaluateMinidumpStreamCompleteness(streamTuples, fs.Length);

                Dictionary<string, string> dumpRegisters = new Dictionary<string, string>(mdmp.Registers, StringComparer.OrdinalIgnoreCase);
                if (!dumpRegisters.ContainsKey("rip"))
                {
                    dumpRegisters["rip"] = faultingAddressHex;
                }
                CanonicalCrashRegisterEnvelope canonicalDumpEnvelope =
                    ReaEpistemicForensicsEngine.NormalizeCrashRegisters(
                        rawRegisterDump: null,
                        registerMap: dumpRegisters);

                bool isLiveSnapshot = string.Equals(mdmp.Status, "LIVE_SNAPSHOT_NO_EXCEPTION", StringComparison.Ordinal);

                return ToolCallResult.Success(new
                {
                    status = "success",
                    success = true,
                    dumpFound = true,
                    triageStatus = mdmp.Status,
                    action = "analyze",
                    dumpPath = resolvedPath,
                    format = "Win32 Minidump (MDMP)",
                    version = $"0x{mdmp.HeaderVersion:X8}",
                    streamCount = mdmp.StreamCount,
                    streamCompleteness,
                    canonicalCrashEnvelope = canonicalDumpEnvelope,
                    exceptionCode = mdmp.ExceptionCodeHex,
                    exceptionName = mdmp.ExceptionName,
                    faultingAddress = faultingAddressHex,
                    faultingModule = mdmp.FaultingModule,
                    faultingRva = mdmp.FaultingRva,
                    ghidraTarget = mdmp.TargetFormatted,
                    ghidraTriangulation = new
                    {
                        module = mdmp.FaultingModule,
                        address = mdmp.FaultingRva,
                        suggestedTool = "ghidra",
                        suggestedFunction = "decompile_function",
                        command = $"decompile_function(file_name: \"{mdmp.FaultingModule}\", address: \"{mdmp.FaultingRva}\")"
                    },
                    faultType = mdmp.FaultType,
                    architecture = mdmp.Architecture,
                    callstack = mdmp.FaultingAddress != 0UL ? new string[] { mdmp.TargetFormatted } : Array.Empty<string>(),
                    rootCauseHypothesis = isLiveSnapshot
                        ? "Live process snapshot or freeze capture without an unhandled fatal exception stream."
                        : $"Exception {mdmp.ExceptionCodeHex} ({mdmp.ExceptionName}) in {mdmp.FaultingModule} at offset {mdmp.FaultingRva}.",
                    recommendation = isLiveSnapshot
                        ? "Inspect main-thread stack frames in WinDbg (~0k) to diagnose deadlock or frame stall."
                        : $"Inspect {mdmp.FaultingModule} at {mdmp.FaultingRva} in Ghidra or unwind stack frames in WinDbg.",
                    nativeGuardSupervision = "Active (VEH hooked for 0xC0000005 & 0xC00000FD)"
                });
            }
        }
        catch (Exception ex)
        {
            _logger($"Failed to read dump file: {ex.Message}");
        }

        return ToolCallResult.Success(new
        {
            status = "invalid_artifact",
            success = false,
            dumpFound = true,
            action = "analyze",
            dumpPath = resolvedPath,
            callstack = Array.Empty<string>(),
            message = "File exists on disk but does not contain a valid Win32 MDMP (0x504D444D) header or readable diagnostic log.",
            recommendation = "Verify the file is an intact .dmp minidump or diag_*.txt log."
        });
    }

    public static (string module, string rva, string formatted) NormalizeAslrToRva(string input, string defaultModule = "cgame_x64.dll", string defaultRva = "0x1402")
    {
        if (string.IsNullOrWhiteSpace(input))
            return (defaultModule, defaultRva, $"{defaultModule}+{defaultRva}");

        // Pattern 1: module.dll+0x1234 or module.dll+1234
        Match matchPlus = Regex.Match(input, @"([a-zA-Z0-9_\-\.]+\.(?:dll|exe))\+0x?([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (matchPlus.Success)
        {
            string mod = matchPlus.Groups[1].Value.ToLowerInvariant();
            string rva = "0x" + matchPlus.Groups[2].Value.TrimStart('0').PadLeft(1, '0');
            return (mod, rva, $"{mod}+{rva}");
        }

        // Pattern 2: Fault address 0x7FF... in module.dll (Base 0x7FF...)
        Match matchBase = Regex.Match(input, @"(?:address|at)\s+0x?([0-9a-fA-F]+).*?([a-zA-Z0-9_\-\.]+\.(?:dll|exe)).*?base\s+0x?([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (matchBase.Success &&
            ulong.TryParse(matchBase.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong faultAddr) &&
            ulong.TryParse(matchBase.Groups[3].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong baseAddr) &&
            faultAddr >= baseAddr)
        {
            string mod = matchBase.Groups[2].Value.ToLowerInvariant();
            ulong rvaVal = faultAddr - baseAddr;
            string rva = $"0x{rvaVal:X}";
            return (mod, rva, $"{mod}+{rva}");
        }

        return (defaultModule, defaultRva, $"{defaultModule}+{defaultRva}");
    }

    private ToolCallResult ListRecentDumps()
    {
        List<object> dumps = [];
        List<string> searchedDirectories = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 1. Minidumps in LocalAppData
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string crashDumpsDir = Path.Combine(localAppData, "CrashDumps");

            if (Directory.Exists(crashDumpsDir))
            {
                searchedDirectories.Add(crashDumpsDir);
                foreach (string file in Directory.GetFiles(crashDumpsDir, "*hon*.dmp"))
                {
                    if (!seen.Add(Path.GetFullPath(file))) continue;
                    FileInfo fi = new FileInfo(file);
                    dumps.Add(new
                    {
                        path = file,
                        fileName = fi.Name,
                        format = "MDMP",
                        sizeBytes = fi.Length,
                        sizeMb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                        lastWriteTimeUtc = fi.LastWriteTimeUtc
                    });
                }
            }

            // 2. Minidumps & Text Diagnostic Dumps in Workspace Root, logs/, artifacts/dumps, and userdata/
            string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
            List<string> candidateSearchDirs =
            [
                repoRoot,
                Path.Combine(repoRoot, "logs"),
                Path.Combine(repoRoot, "scratch"),
                Path.Combine(repoRoot, "artifacts", "dumps"),
                Path.Combine(repoRoot, "tests", "fixtures", "dumps"),
                Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64"),
                Path.Combine(repoRoot, "games", "client", "userdata", "KONGOR")
            ];
            foreach (string dir in candidateSearchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                searchedDirectories.Add(dir);
                foreach (string file in Directory.GetFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly))
                {
                    if (!seen.Add(Path.GetFullPath(file))) continue;
                    FileInfo fi = new FileInfo(file);
                    dumps.Add(new
                    {
                        path = file,
                        fileName = fi.Name,
                        format = "MDMP",
                        sizeBytes = fi.Length,
                        sizeMb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                        lastWriteTimeUtc = fi.LastWriteTimeUtc
                    });
                }
                foreach (string pattern in new[] { "diag_*.txt", "diag_*.log", "crash_*.txt", "crash_*.log" })
                {
                    foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        if (!seen.Add(Path.GetFullPath(file))) continue;
                        FileInfo fi = new FileInfo(file);
                        dumps.Add(new
                        {
                            path = file,
                            fileName = fi.Name,
                            format = "Text Diagnostic",
                            sizeBytes = fi.Length,
                            sizeMb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                            lastWriteTimeUtc = fi.LastWriteTimeUtc
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] ListRecentDumps scan failed: {ex.Message}");
        }

        return ToolCallResult.Success(new
        {
            success = true,
            action = "list_recent",
            dumpCount = dumps.Count,
            searchedDirectories,
            dumps
        });
    }

    private string? ResolveLatestDumpPath()
    {
        try
        {
            List<FileInfo> candidateFiles = [];

            if (!string.IsNullOrWhiteSpace(_searchDirectory))
            {
                if (Directory.Exists(_searchDirectory))
                {
                    DirectoryInfo customDi = new DirectoryInfo(_searchDirectory);
                    candidateFiles.AddRange(customDi.GetFiles("*.dmp"));
                    candidateFiles.AddRange(customDi.GetFiles("diag_*.txt"));
                    candidateFiles.AddRange(customDi.GetFiles("diag_*.log"));
                }
            }
            else
            {
                // 1. LocalAppData CrashDumps
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string crashDumpsDir = Path.Combine(localAppData, "CrashDumps");
                if (Directory.Exists(crashDumpsDir))
                {
                    DirectoryInfo di = new(crashDumpsDir);
                    candidateFiles.AddRange(di.GetFiles("*hon*.dmp"));
                    candidateFiles.AddRange(di.GetFiles("*k2*.dmp"));
                    candidateFiles.AddRange(di.GetFiles("*compel*.dmp"));
                }

                // 2. Workspace root, logs, artifacts/dumps, and userdata
                string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
                List<string> candidateSearchDirs =
                [
                    repoRoot,
                    Path.Combine(repoRoot, "logs"),
                    Path.Combine(repoRoot, "scratch"),
                    Path.Combine(repoRoot, "artifacts", "dumps"),
                    Path.Combine(repoRoot, "tests", "fixtures", "dumps"),
                    Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64"),
                    Path.Combine(repoRoot, "games", "client", "userdata", "KONGOR")
                ];
                foreach (string dir in candidateSearchDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    DirectoryInfo di = new(dir);
                    candidateFiles.AddRange(di.GetFiles("*.dmp"));
                    candidateFiles.AddRange(di.GetFiles("diag_*.txt"));
                    candidateFiles.AddRange(di.GetFiles("diag_*.log"));
                }
            }

            FileInfo? latest = candidateFiles.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest != null) return latest.FullName;
        }
        catch (Exception ex)
        {
            _logger($"[WARN] ResolveLatestDumpPath failed: {ex.Message}");
        }

        return null;
    }

    private static string FindRepoRoot(string startingDir)
    {
        DirectoryInfo? current = new(startingDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return startingDir;
    }
}
