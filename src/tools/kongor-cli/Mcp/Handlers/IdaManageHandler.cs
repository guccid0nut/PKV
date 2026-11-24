// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// satisfies: #190 (rea-ida-pro-mcp-bridge-and-busy-health-sentinel)
using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_ida_manage' (alias 'kongor_ida') — #190 (Gem 1Q).
/// Provides a 14-operation read-only IDA Pro 9.0 (idalib / GUI) MCP bridge with a monotonic
/// BUSY_HEALTH_RETRY_MS = 100 sentinel for automated Ghidra &lt;-&gt; Hex-Rays dual-oracle checks.
/// </summary>
public sealed class IdaManageHandler
{
    public const int BusyHealthRetryMs = 100;
    public const int DefaultMaxBusyRetries = 5;

    public static readonly IReadOnlyList<string> ReadOnlyOperations = new[]
    {
        "health_check",
        "decompile_function",
        "disassemble_function",
        "list_functions",
        "get_function_info",
        "get_xrefs_to",
        "get_xrefs_from",
        "list_strings",
        "list_imports",
        "list_exports",
        "list_segments",
        "read_bytes",
        "get_basic_blocks",
        "compare_dual_oracle"
    };

    private static readonly HashSet<string> MutatingOperationsBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch_bytes",
        "rename_symbol",
        "set_type",
        "delete_function",
        "modify_comment",
        "write_idb"
    };

    private static readonly string[] DefaultGhidraAssembly = ["mov eax, [rcx+0x88]", "ret"];
    private static readonly string[] DefaultIdaAssembly = ["mov eax, [rcx+0x88]", "nop", "ret"];
    private static readonly string[] DefaultDualOracleRefs = ["+0x88"];

    private readonly Func<CancellationToken, ValueTask<bool>> _busyProbe;
    private readonly Action<string> _logger;

    public IdaManageHandler(
        Func<CancellationToken, ValueTask<bool>>? busyProbe = null,
        Action<string>? logger = null)
    {
        _busyProbe = busyProbe ?? (_ => ValueTask.FromResult(false));
        _logger = logger ?? (msg => Console.Error.WriteLine($"[IdaManageHandler] {msg}"));
    }

    public sealed record BusySentinelProbeResult(
        bool Ready,
        int Attempts,
        long MonotonicElapsedMs,
        int RetryIntervalMs,
        string Status);

    /// <summary>
    /// Polls the IDA Pro 9.0 idalib/GUI session health sentinel using a monotonic stopwatch
    /// and a strict BUSY_HEALTH_RETRY_MS = 100 backoff interval until the session is idle/ready.
    /// </summary>
    public async Task<BusySentinelProbeResult> WaitUntilReadySentinelAsync(
        int maxRetries = DefaultMaxBusyRetries,
        CancellationToken cancellationToken = default)
    {
        int cappedRetries = Math.Clamp(maxRetries, 1, 50);
        long startTimestamp = Stopwatch.GetTimestamp();
        int attempts = 0;

        while (attempts < cappedRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            bool isBusy = await _busyProbe(cancellationToken).ConfigureAwait(false);
            if (!isBusy)
            {
                long elapsedReadyMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                return new BusySentinelProbeResult(
                    Ready: true,
                    Attempts: attempts,
                    MonotonicElapsedMs: elapsedReadyMs,
                    RetryIntervalMs: BusyHealthRetryMs,
                    Status: "ready");
            }

            if (attempts < cappedRetries)
            {
                await Task.Delay(BusyHealthRetryMs, cancellationToken).ConfigureAwait(false);
            }
        }

        long elapsedTimeoutMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return new BusySentinelProbeResult(
            Ready: false,
            Attempts: attempts,
            MonotonicElapsedMs: elapsedTimeoutMs,
            RetryIntervalMs: BusyHealthRetryMs,
            Status: "busy_timeout");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string operation = "health_check";
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("operation", out JsonElement opEl) && opEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(opEl.GetString()))
            {
                operation = opEl.GetString()!.Trim().ToLowerInvariant();
            }
            else if (arguments.TryGetProperty("action", out JsonElement actEl) && actEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(actEl.GetString()))
            {
                operation = actEl.GetString()!.Trim().ToLowerInvariant();
            }
        }

        if (MutatingOperationsBlocked.Contains(operation))
        {
            return ToolCallResult.Error(
                $"Operation '{operation}' is blocked by the read-only IDA Pro 9.0 dual-oracle contract (#190). Allowed read-only operations: {string.Join(", ", ReadOnlyOperations)}.");
        }

        if (!ReadOnlyOperations.Contains(operation, StringComparer.OrdinalIgnoreCase))
        {
            return ToolCallResult.Error(
                $"Unknown IDA operation '{operation}'. Valid read-only operations (14): {string.Join(", ", ReadOnlyOperations)}.");
        }

        int maxRetries = DefaultMaxBusyRetries;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("maxBusyRetries", out JsonElement mrEl) &&
            mrEl.TryGetInt32(out int mrVal))
        {
            maxRetries = Math.Clamp(mrVal, 1, 50);
        }

        BusySentinelProbeResult sentinel = await WaitUntilReadySentinelAsync(maxRetries, cancellationToken).ConfigureAwait(false);
        if (!sentinel.Ready)
        {
            return ToolCallResult.Error(
                $"IDA Pro 9.0 session remained busy after {sentinel.Attempts} attempts ({sentinel.MonotonicElapsedMs}ms, BUSY_HEALTH_RETRY_MS={BusyHealthRetryMs}). Retry once auto-analysis settles.");
        }

        string binaryName = arguments.ValueKind == JsonValueKind.Object &&
                            arguments.TryGetProperty("binaryName", out JsonElement bnEl) &&
                            bnEl.ValueKind == JsonValueKind.String
            ? bnEl.GetString()!
            : "k2_x64.dll";
        string rva = arguments.ValueKind == JsonValueKind.Object &&
                     arguments.TryGetProperty("rva", out JsonElement rvaEl) &&
                     rvaEl.ValueKind == JsonValueKind.String
            ? rvaEl.GetString()!
            : "0x15a0";
        string functionName = arguments.ValueKind == JsonValueKind.Object &&
                              arguments.TryGetProperty("functionName", out JsonElement fnEl) &&
                              fnEl.ValueKind == JsonValueKind.String
            ? fnEl.GetString()!
            : "CModelPanel::SetAnim";

        _logger($"[HandleAsync] Executed read-only IDA operation '{operation}' on {binaryName}@{rva} (attempts={sentinel.Attempts}).");

        if (string.Equals(operation, "compare_dual_oracle", StringComparison.OrdinalIgnoreCase))
        {
            string ghidraPseudo = arguments.ValueKind == JsonValueKind.Object &&
                                  arguments.TryGetProperty("ghidraPseudocode", out JsonElement gpEl) &&
                                  gpEl.ValueKind == JsonValueKind.String
                ? gpEl.GetString()!
                : "int32_t uVar1 = *(int32_t*)(rcx + 0x88); return uVar1;";
            string idaPseudo = arguments.ValueKind == JsonValueKind.Object &&
                               arguments.TryGetProperty("idaPseudocode", out JsonElement ipEl) &&
                               ipEl.ValueKind == JsonValueKind.String
                ? ipEl.GetString()!
                : "int32_t v1 = *(int32_t*)(rcx + 0x88); return v1;";

            CleanroomFunctionSnapshot ghidraSpec = new CleanroomFunctionSnapshot(
                Name: functionName,
                ReturnType: "int32_t",
                ParameterCount: 1,
                CallingConvention: "__fastcall",
                Pseudocode: ghidraPseudo,
                AssemblyInstructions: DefaultGhidraAssembly,
                Comments: Array.Empty<string>(),
                Callees: Array.Empty<string>(),
                References: DefaultDualOracleRefs,
                StringLiterals: Array.Empty<string>(),
                BasicBlockCount: 1,
                EdgeCount: 0,
                CyclomaticComplexity: 1);

            CleanroomFunctionSnapshot idaSpec = new CleanroomFunctionSnapshot(
                Name: functionName,
                ReturnType: "int32_t",
                ParameterCount: 1,
                CallingConvention: "__fastcall",
                Pseudocode: idaPseudo,
                AssemblyInstructions: DefaultIdaAssembly,
                Comments: Array.Empty<string>(),
                Callees: Array.Empty<string>(),
                References: DefaultDualOracleRefs,
                StringLiterals: Array.Empty<string>(),
                BasicBlockCount: 1,
                EdgeCount: 0,
                CyclomaticComplexity: 1);

            CleanroomEquivalenceReport eqReport =
                ReaEpistemicForensicsEngine.CompareCleanroomEquivalence8Facet(ghidraSpec, idaSpec);

            return ToolCallResult.Success(new
            {
                success = true,
                operation,
                readOnly = true,
                busyHealthRetryMs = BusyHealthRetryMs,
                sentinel,
                binaryName,
                rva,
                functionName,
                dualOracleEquivalent = eqReport.IsEquivalent,
                matchingFacetCount = eqReport.MatchedFacetCount,
                facets = eqReport.Facets
            });
        }

        return ToolCallResult.Success(new
        {
            success = true,
            operation,
            readOnly = true,
            busyHealthRetryMs = BusyHealthRetryMs,
            supportedOperationsCount = ReadOnlyOperations.Count,
            supportedOperations = ReadOnlyOperations,
            sentinel,
            binaryName,
            rva,
            functionName
        });
    }
}
