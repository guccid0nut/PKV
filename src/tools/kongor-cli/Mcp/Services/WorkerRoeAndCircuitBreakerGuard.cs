// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
///     In-process Rules of Engagement (ROE) guard for subagent role isolation (<c>KONGOR_WORKER_ROLE</c>)
///     and 3-Strike Consecutive Failure Circuit Breaker preventing blind retry loops against failing tools.
/// </summary>
public sealed class WorkerRoeAndCircuitBreakerGuard
{
    public const int DefaultStrikeThreshold = 3;

    private static readonly HashSet<string> LiveClientExclusiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "kongor_game_client_manage",
        "kongor_client_eval_lua",
        "kongor_client_practice",
        "kongor_client_navigate",
        "kongor_ui_control",
        "kongor_reload_and_verify",
        "kongor_game_command",
        "kongor_capture_window",
        "kongor_wait_for",
        "kongor_hot_reload",
        "kongor_sync_mirrors"
    };

    private sealed class StrikeState
    {
        public int ConsecutiveFailures;
        public string LastErrorSummary = string.Empty;
        public DateTimeOffset LastFailureUtc;
    }

    private readonly ConcurrentDictionary<string, StrikeState> _strikes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Optional explicit override for worker role (defaults to <c>KONGOR_WORKER_ROLE</c> environment variable).
    /// </summary>
    public string? WorkerRoleOverride { get; set; }

    /// <summary>
    ///     Enables the 3-strike circuit breaker. Defaults to true in production and false during xUnit test runs
    ///     unless explicitly enabled by a test verifying circuit-breaker behavior.
    /// </summary>
    public bool EnableThreeStrikeBreaker { get; set; } = !Handlers.GameServerManageHandler.IsTestExecution;

    public int StrikeThreshold { get; set; } = DefaultStrikeThreshold;

    public string? GetEffectiveWorkerRole()
    {
        string? role = WorkerRoleOverride ?? Environment.GetEnvironmentVariable("KONGOR_WORKER_ROLE");
        return string.IsNullOrWhiteSpace(role) ? null : role.Trim();
    }

    public static bool IsLiveClientExclusiveTool(string toolName)
        => !string.IsNullOrWhiteSpace(toolName) && LiveClientExclusiveTools.Contains(toolName);

    public ToolCallResult? EvaluatePreCall(string canonicalToolName, JsonElement arguments)
    {
        // 1. Subagent ROE Check: kongor-offline-worker MUST NOT mutate or probe the live hon_x64.exe client
        string? role = GetEffectiveWorkerRole();
        if (string.Equals(role, "kongor-offline-worker", StringComparison.OrdinalIgnoreCase) &&
            IsLiveClientExclusiveTool(canonicalToolName))
        {
            string msg =
                $"[WORKER-ROE] Tool '{canonicalToolName}' is reserved exclusively for the singleton 'kongor-game-worker'. " +
                "Current worker role is 'kongor-offline-worker' (static Ghidra/VFS/build/test only). " +
                "Queue this probe for 'kongor-game-worker' instead of invoking live client tools directly.";
            Console.Error.WriteLine(msg);
            return ToolCallResult.Json(new
            {
                status = "error",
                ok = false,
                tool = canonicalToolName,
                errorCode = "worker_roe_violation",
                workerRole = role,
                error = msg,
                remediation = "Return this probe in your risk-ordered live probe queue for kongor-game-worker."
            }, isError: true);
        }

        // 2. In-process 3-Strike Consecutive Failure Circuit Breaker
        if (!EnableThreeStrikeBreaker || StrikeThreshold <= 0)
        {
            return null;
        }

        bool forceRetry = arguments.ValueKind == JsonValueKind.Object &&
                          ((arguments.TryGetProperty("force_retry", out JsonElement frEl) ||
                            arguments.TryGetProperty("forceRetry", out frEl)) &&
                           (frEl.ValueKind == JsonValueKind.True ||
                            (frEl.ValueKind == JsonValueKind.String && bool.TryParse(frEl.GetString(), out bool b) && b)));
        if (forceRetry)
        {
            ResetTool(canonicalToolName);
            return null;
        }

        if (_strikes.TryGetValue(canonicalToolName, out StrikeState? state))
        {
            lock (state)
            {
                if (state.ConsecutiveFailures >= StrikeThreshold)
                {
                    string msg =
                        $"[3-STRIKE-BREAKER] Tool '{canonicalToolName}' tripped the 3-strike circuit breaker after " +
                        $"{state.ConsecutiveFailures} consecutive failures (last error: {state.LastErrorSummary}). " +
                        "Freeze blind retries, triage via logs/WinDbg/MCP issue KB, or pass force_retry=true after applying a root-cause fix.";
                    Console.Error.WriteLine(msg);
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        ok = false,
                        tool = canonicalToolName,
                        errorCode = "three_strike_circuit_breaker",
                        consecutiveFailures = state.ConsecutiveFailures,
                        lastError = state.LastErrorSummary,
                        error = msg,
                        remediation = $"Run: powershell -File scripts/kongor_mcp_issues.ps1 -Action search -Tool {canonicalToolName} (or pass force_retry=true after fixing root cause)."
                    }, isError: true);
                }
            }
        }

        return null;
    }

    public void RecordOutcome(string canonicalToolName, bool isError, string? errorSummary = null)
    {
        if (string.IsNullOrWhiteSpace(canonicalToolName))
        {
            return;
        }

        if (!isError)
        {
            ResetTool(canonicalToolName);
            return;
        }

        StrikeState state = _strikes.GetOrAdd(canonicalToolName, _ => new StrikeState());
        lock (state)
        {
            state.ConsecutiveFailures++;
            state.LastFailureUtc = TimeProvider.System.GetUtcNow();
            state.LastErrorSummary = string.IsNullOrWhiteSpace(errorSummary)
                ? "tool returned isError=true"
                : (errorSummary.Length <= 220 ? errorSummary : errorSummary[..220] + "...");
        }
    }

    public int GetStrikeCount(string canonicalToolName)
    {
        if (_strikes.TryGetValue(canonicalToolName, out StrikeState? state))
        {
            lock (state)
            {
                return state.ConsecutiveFailures;
            }
        }
        return 0;
    }

    public void ResetTool(string canonicalToolName)
    {
        _strikes.TryRemove(canonicalToolName, out _);
    }

    public void ResetAll()
    {
        _strikes.Clear();
    }
}
