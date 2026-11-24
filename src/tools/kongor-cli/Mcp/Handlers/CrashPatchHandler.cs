// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_crash_patch' (alias: 'crash_patch').
/// Machine Code Crash Patcher: Synthesizes ABI-safe x64 detours, performs non-destructive
/// byte-level dry-run verifications, and applies idempotent hot-patches with .bak safety.
/// </summary>
public sealed class CrashPatchHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _workspaceRoot;

    public CrashPatchHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[CrashPatch] {msg}"));
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "synthesize";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string targetModule = "k2_x64.dll";
        if ((arguments.TryGetProperty("target_module", out JsonElement tmEl) ||
             arguments.TryGetProperty("targetModule", out tmEl) ||
             arguments.TryGetProperty("module", out tmEl)) &&
            !string.IsNullOrWhiteSpace(tmEl.GetString()))
        {
            targetModule = tmEl.GetString()!.Trim();
        }

        string faultingRva = "0x0";
        if ((arguments.TryGetProperty("faulting_rva", out JsonElement frEl) ||
             arguments.TryGetProperty("faultingRva", out frEl) ||
             arguments.TryGetProperty("rva", out frEl) ||
             arguments.TryGetProperty("address", out frEl)) &&
            !string.IsNullOrWhiteSpace(frEl.GetString()))
        {
            faultingRva = frEl.GetString()!.Trim();
        }

        string exceptionCode = "0xC0000005";
        if ((arguments.TryGetProperty("exception_code", out JsonElement ecEl) ||
             arguments.TryGetProperty("exceptionCode", out ecEl)) &&
            !string.IsNullOrWhiteSpace(ecEl.GetString()))
        {
            exceptionCode = ecEl.GetString()!.Trim();
        }

        string? originalBytesHex = null;
        if ((arguments.TryGetProperty("original_bytes_hex", out JsonElement obEl) ||
             arguments.TryGetProperty("originalBytesHex", out obEl)) &&
            !string.IsNullOrWhiteSpace(obEl.GetString()))
        {
            originalBytesHex = obEl.GetString()!.Trim();
        }

        string? detourBytesHex = null;
        if ((arguments.TryGetProperty("detour_bytes_hex", out JsonElement dbEl) ||
             arguments.TryGetProperty("detourBytesHex", out dbEl) ||
             arguments.TryGetProperty("patched_bytes_hex", out dbEl) ||
             arguments.TryGetProperty("patchedBytesHex", out dbEl)) &&
            !string.IsNullOrWhiteSpace(dbEl.GetString()))
        {
            detourBytesHex = dbEl.GetString()!.Trim();
        }

        string? customBinaryPath = null;
        if ((arguments.TryGetProperty("binary_path", out JsonElement bpEl) ||
             arguments.TryGetProperty("binaryPath", out bpEl) ||
             arguments.TryGetProperty("path", out bpEl)) &&
            !string.IsNullOrWhiteSpace(bpEl.GetString()))
        {
            customBinaryPath = bpEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Executing crash patch action '{action}' on module '{targetModule}' (RVA: '{faultingRva}', Exception: '{exceptionCode}')");

        switch (action)
        {
            case "synthesize":
            {
                SynthesizedDetour detour = AbiSafeDetourSynthesizer.Synthesize(targetModule, faultingRva, exceptionCode);

                sw.Stop();
                List<ToolHandoff> handoffs = new List<ToolHandoff>
                {
                    ToolHandoff.Create(
                        server: "kongor",
                        tool: "kongor_crash_patch",
                        reason: $"Dry-run verify binary bytes for {targetModule} at {faultingRva}",
                        arguments: new
                        {
                            action = "dry_run",
                            target_module = targetModule,
                            faulting_rva = faultingRva,
                            original_bytes_hex = detour.OriginalBytesHex,
                            detour_bytes_hex = detour.DetourBytesHex
                        })
                };

                object synthData = new
                {
                    action = "synthesize",
                    targetModule = targetModule,
                    faultingRva = faultingRva,
                    exceptionCode = exceptionCode,
                    strategy = detour.Strategy,
                    disassemblyInstruction = detour.DisassemblyInstruction,
                    originalBytesHex = detour.OriginalBytesHex,
                    detourBytesHex = detour.DetourBytesHex,
                    safetyVerification = detour.SafetyVerification,
                    preservesNonVolatileRegisters = detour.PreservesNonVolatileRegisters,
                    maintains16ByteStackAlignment = detour.Maintains16ByteStackAlignment,
                    hasShadowStore = detour.HasShadowStore,
                    canLiveInject = detour.CanLiveInject
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: synthData,
                    isError: false,
                    handoffs: handoffs);
            }

            case "dry_run":
            case "verify":
            {
                SynthesizedDetour detour = AbiSafeDetourSynthesizer.Synthesize(targetModule, faultingRva, exceptionCode);
                string expectedOriginal = originalBytesHex ?? detour.OriginalBytesHex;
                string expectedDetour = detourBytesHex ?? detour.DetourBytesHex;

                string resolvedBinary = ResolveBinaryPath(customBinaryPath, targetModule);
                long parsedRva = ParseOffset(faultingRva);

                bool fileExists = File.Exists(resolvedBinary);
                bool alreadyPatched = false;
                bool bytesMatch = false;
                bool lengthSafe = false;
                bool rvaMapped = false;
                long fileOffset = parsedRva;
                string actualBytesHex = string.Empty;
                string verdict;

                if (!fileExists)
                {
                    verdict = $"Binary file not found on disk at '{resolvedBinary}'. Run with a valid binary_path.";
                }
                else if (!PeImportAddressTableService.TryResolveFileOffsetFromRva(
                             resolvedBinary,
                             parsedRva,
                             out fileOffset,
                             out bool _,
                             out string? rvaError))
                {
                    verdict = $"Safety Abort: {rvaError}";
                }
                else
                {
                    rvaMapped = true;
                    byte[] origBytes = ParseHexBytes(expectedOriginal);
                    byte[] detBytes = ParseHexBytes(expectedDetour);
                    lengthSafe = detBytes.Length <= origBytes.Length;
                    int checkLength = Math.Max(origBytes.Length, detBytes.Length);

                    byte[] actual = ReadFileBytes(resolvedBinary, fileOffset, checkLength);
                    actualBytesHex = BitConverter.ToString(actual).Replace("-", " ");

                    if (actual.Length >= detBytes.Length && actual.AsSpan().StartsWith(detBytes))
                    {
                        alreadyPatched = true;
                        verdict = $"Binary is already patched with detour bytes at file offset 0x{fileOffset:X}.";
                    }
                    else if (!lengthSafe)
                    {
                        bytesMatch = actual.Length >= origBytes.Length && actual.AsSpan().StartsWith(origBytes);
                        verdict = $"Safety Abort: detour payload ({detBytes.Length} bytes) exceeds verified original signature ({origBytes.Length} bytes); refusing to overwrite {detBytes.Length - origBytes.Length} unverified trailing instruction bytes.";
                    }
                    else if (actual.Length >= origBytes.Length && actual.AsSpan().StartsWith(origBytes))
                    {
                        bytesMatch = true;
                        verdict = $"Verified: original bytes match expected signature '{expectedOriginal}' ({origBytes.Length} >= {detBytes.Length} bytes) at file offset 0x{fileOffset:X}. Ready to apply detour safely.";
                    }
                    else
                    {
                        verdict = $"Safety Abort: actual bytes '{actualBytesHex}' at file offset 0x{fileOffset:X} do not match expected original '{expectedOriginal}'. Aborting to prevent corrupting unrelated instructions.";
                    }
                }

                sw.Stop();

                bool safeToApply = rvaMapped && bytesMatch && lengthSafe && !alreadyPatched;
                List<ToolHandoff> handoffs = new List<ToolHandoff>();
                if (safeToApply)
                {
                    handoffs.Add(ToolHandoff.Create(
                        server: "kongor",
                        tool: "kongor_crash_patch",
                        reason: $"Apply verified detour patch to {targetModule} at file offset 0x{fileOffset:X}",
                        arguments: new
                        {
                            action = "apply",
                            binary_path = resolvedBinary,
                            faulting_rva = faultingRva,
                            original_bytes_hex = expectedOriginal,
                            detour_bytes_hex = expectedDetour
                        },
                        path: resolvedBinary));
                }

                object dryRunData = new
                {
                    action = "dry_run",
                    targetModule = targetModule,
                    resolvedBinaryPath = resolvedBinary,
                    fileOffsetHex = $"0x{Math.Max(0L, fileOffset):X}",
                    fileExists = fileExists,
                    alreadyPatched = alreadyPatched,
                    bytesMatch = bytesMatch,
                    lengthSafe = lengthSafe,
                    detourFitsInPlace = lengthSafe,
                    expectedOriginalHex = expectedOriginal,
                    expectedDetourHex = expectedDetour,
                    actualBytesHex = actualBytesHex,
                    safetyVerdict = verdict,
                    safeToApply = safeToApply
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: (safeToApply || alreadyPatched) ? "success" : "verification_failed",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: dryRunData,
                    isError: !safeToApply && !alreadyPatched,
                    handoffs: handoffs);
            }

            case "apply":
            {
                SynthesizedDetour detour = AbiSafeDetourSynthesizer.Synthesize(targetModule, faultingRva, exceptionCode);
                string expectedOriginal = originalBytesHex ?? detour.OriginalBytesHex;
                string expectedDetour = detourBytesHex ?? detour.DetourBytesHex;

                string resolvedBinary = ResolveBinaryPath(customBinaryPath, targetModule);
                long parsedRva = ParseOffset(faultingRva);

                if (!File.Exists(resolvedBinary))
                {
                    return ToolCallResult.Error($"Cannot apply patch: binary '{resolvedBinary}' not found. Pass a valid binary_path.");
                }

                if (!PeImportAddressTableService.TryResolveFileOffsetFromRva(
                        resolvedBinary,
                        parsedRva,
                        out long fileOffset,
                        out bool _,
                        out string? rvaError))
                {
                    return ToolCallResult.Error($"Patch safety violation: {rvaError}");
                }

                byte[] origBytes = ParseHexBytes(expectedOriginal);
                byte[] detBytes = ParseHexBytes(expectedDetour);

                // 1. Pre-flight verification
                int readLen = Math.Max(origBytes.Length, detBytes.Length);
                byte[] current = ReadFileBytes(resolvedBinary, fileOffset, readLen);
                if (current.Length >= detBytes.Length && current.AsSpan().StartsWith(detBytes))
                {
                    sw.Stop();
                    object alreadyData = new
                    {
                        action = "apply",
                        status = "already_patched",
                        message = $"Binary '{resolvedBinary}' is already patched at file offset 0x{fileOffset:X}.",
                        targetModule = targetModule
                    };
                    return ExecutionTruthEnvelope.CreateJsonResult("success", ExecutionMode.OfflineDirect, null, null, alreadyData);
                }

                if (detBytes.Length > origBytes.Length)
                {
                    return ToolCallResult.Error(
                        $"Patch safety violation: detour payload ({detBytes.Length} bytes) exceeds verified original signature ({origBytes.Length} bytes). " +
                        $"Refusing to overwrite {detBytes.Length - origBytes.Length} unverified trailing bytes at file offset 0x{fileOffset:X}.");
                }

                if (current.Length < origBytes.Length || !current.AsSpan().StartsWith(origBytes))
                {
                    string actual = BitConverter.ToString(current).Replace("-", " ");
                    return ToolCallResult.Error($"Patch safety violation: actual bytes '{actual}' at file offset 0x{fileOffset:X} do not match expected original '{expectedOriginal}'.");
                }

                // 2. Create .bak backup file
                string bakPath = resolvedBinary + ".bak";
                if (!File.Exists(bakPath))
                {
                    File.Copy(resolvedBinary, bakPath);
                    _logger?.Invoke($"Created backup '{bakPath}'");
                }

                // 3. Write patch bytes
                using (FileStream fs = new FileStream(resolvedBinary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    fs.Seek(fileOffset, SeekOrigin.Begin);
                    fs.Write(detBytes, 0, detBytes.Length);
                    fs.Flush();
                }

                _logger?.Invoke($"Successfully wrote {detBytes.Length} detour bytes to '{resolvedBinary}' at 0x{fileOffset:X}");

                sw.Stop();

                List<ToolHandoff> handoffs = new List<ToolHandoff>
                {
                    ToolHandoff.Create(
                        server: "kongor",
                        tool: "kongor_crash_patch",
                        reason: $"Revert hot-patch on {targetModule} restoring from .bak",
                        arguments: new { action = "revert", binary_path = resolvedBinary },
                        path: resolvedBinary),
                    ToolHandoff.Create(
                        server: "kongor",
                        tool: "kongor_match_launch",
                        reason: "Run headless verification match to confirm zero-crash remediation",
                        arguments: new { mode = "practice", map = "caldavar", hero = "Hero_Pyromancer", window_mode = "offscreen" })
                };

                object applyData = new
                {
                    action = "apply",
                    patched = true,
                    targetModule = targetModule,
                    binaryPath = resolvedBinary,
                    fileOffsetHex = $"0x{fileOffset:X}",
                    bytesWritten = detBytes.Length,
                    detourBytesHex = expectedDetour,
                    backupPath = bakPath,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: applyData,
                    isError: false,
                    handoffs: handoffs);
            }

            case "revert":
            case "rollback":
            {
                string resolvedBinary = ResolveBinaryPath(customBinaryPath, targetModule);
                string bakPath = resolvedBinary + ".bak";

                if (!File.Exists(bakPath))
                {
                    return ToolCallResult.Error($"Cannot revert: backup file '{bakPath}' not found.");
                }

                File.Copy(bakPath, resolvedBinary, overwrite: true);
                _logger?.Invoke($"Restored original binary from '{bakPath}' to '{resolvedBinary}'");

                sw.Stop();
                object revertData = new
                {
                    action = "revert",
                    reverted = true,
                    targetModule = targetModule,
                    restoredPath = resolvedBinary,
                    backupPath = bakPath,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: revertData);
            }

            default:
                return ToolCallResult.Error($"Unknown action '{action}'. Valid actions: synthesize, dry_run, apply, revert.");
        }
    }

    private string ResolveBinaryPath(string? customPath, string module)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        string cleanModule = Path.GetFileName(module);
        string[] searchDirs =
        [
            Path.Combine(_workspaceRoot, "games", "client"),
            Path.Combine(_workspaceRoot, "games", "client", "win-x64"),
            Path.Combine(_workspaceRoot, "games", "client", "windows-x64"),
            Path.Combine(_workspaceRoot, "bin")
        ];

        foreach (string dir in searchDirs)
        {
            string candidate = Path.Combine(dir, cleanModule);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.Combine(_workspaceRoot, "games", "client", cleanModule);
    }

    private static long ParseOffset(string rva)
    {
        string clean = rva.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[2..];
        }

        if (long.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out long parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static byte[] ParseHexBytes(string hexString)
    {
        string clean = hexString.Replace(" ", string.Empty).Replace("-", string.Empty);
        byte[] bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static byte[] ReadFileBytes(string filePath, long offset, int length)
    {
        if (!File.Exists(filePath)) return [];

        using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset >= fs.Length) return [];

        fs.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[Math.Min(length, (int)(fs.Length - offset))];
        fs.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }
}
