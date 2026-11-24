// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TaoClient.cs
// System: Project Kongor TAO (Together as One) Companion Client & Dispatcher
// Policy: Strict Explicit Typing (NO 'var' in non-obvious types)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Static factory for creating <see cref="TaoResponse{T}"/> instances.
/// </summary>
public static class TaoResponse
{
    public static TaoResponse<T> Ok<T>(string action, T? data, double elapsedMs, JsonElement rawResult = default) =>
        new()
        {
            Success = true,
            Action = action,
            Data = data,
            ElapsedMs = elapsedMs,
            RawResult = rawResult
        };

    public static TaoResponse<T> Fail<T>(string action, string error, double elapsedMs, string? traceback = null, JsonElement rawResult = default) =>
        new()
        {
            Success = false,
            Action = action,
            Error = error,
            Traceback = traceback,
            ElapsedMs = elapsedMs,
            RawResult = rawResult
        };
}

/// <summary>
/// Structured result returned from all TAO in-engine actions and queries.
/// Captures success status, deserialized payload data, execution latency, and raw JSON element.
/// </summary>
/// <typeparam name="T">Target data type to deserialize.</typeparam>
public sealed class TaoResponse<T>
{
    public bool Success { get; init; }
    public string Action { get; init; } = string.Empty;
    public T? Data { get; init; }
    public string? Error { get; init; }
    public string? Traceback { get; init; }
    public double ElapsedMs { get; init; }
    public JsonElement RawResult { get; init; }
    public string Status => Success ? "success" : "error";
}

/// <summary>
/// Authoritative metadata DTO representing engine-supported TAO capabilities,
/// registered handler list, and file integrity hashes.
/// </summary>
public sealed class TaoCapabilitiesDto
{
    public string Version { get; init; } = "unknown";
    public string EngineBuild { get; init; } = "unknown";
    public string MailboxVersion { get; init; } = "unknown";
    public IReadOnlyList<string> Handlers { get; init; } = [];
    public string TaoLuaSha256 { get; init; } = string.Empty;
    public string HashedDiskPath { get; init; } = string.Empty;
    public bool? IsStale { get; init; }
    public double RoundTripLatencyMs { get; init; }
    public bool IsMailboxConnected { get; init; }
    public int GamePhase { get; init; }
    public string Author { get; init; } = "guccidonut";
    public string License { get; init; } = "AGPL-3.0-or-later";
    public string RepositoryUrl { get; init; } = "https://github.com/guccidonut/PKV";
}

/// <summary>
/// Unified client interface for dispatching structured TAO companion actions
/// directly into the K2 Lua environment with zero string concatenation and zero escaping errors.
/// </summary>
public interface ITaoClient
{
    bool IsConnected { get; }
    bool IsMailboxConnected { get; }

    Task<TaoResponse<T>> DispatchAsync<T>(
        string action,
        object? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default);

    Task<TaoResponse<JsonElement>> DispatchAsync(
        string action,
        object? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default);

    Task<TaoCapabilitiesDto> GetCapabilitiesAsync(
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production implementation of <see cref="ITaoClient"/>.
/// Prioritizes high-speed non-allocating Nexus Mailbox IPC, with seamless fallback
/// to game IPC bridges when running headless or in test suites.
/// </summary>
public sealed class TaoClient : ITaoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly NexusMailboxIpcBridge? _mailboxBridge;
    private readonly IGameIpcBridge? _fallbackBridge;
    private readonly Action<string> _logger;

    public ScrcpyAtomicPipeInterrupter PipeInterrupter { get; } = new();
    public bool IsMailboxConnected => _mailboxBridge != null && _mailboxBridge.IsConnected;
    public bool IsConnected => IsMailboxConnected || (_fallbackBridge?.IsConnected ?? false);

    public TaoClient(
        NexusMailboxIpcBridge? mailboxBridge = null,
        IGameIpcBridge? fallbackBridge = null,
        Action<string>? logger = null)
    {
        _mailboxBridge = mailboxBridge;
        _fallbackBridge = fallbackBridge;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[TaoClient] {msg}"));
    }

    public void InterruptPendingDispatches()
    {
        PipeInterrupter.Interrupt();
    }

    public Task<TaoResponse<JsonElement>> DispatchAsync(
        string action,
        object? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        return DispatchAsync<JsonElement>(action, payload, timeoutMs, cancellationToken);
    }

    public async Task<TaoResponse<T>> DispatchAsync<T>(
        string action,
        object? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Stopwatch sw = Stopwatch.StartNew();

        if (PipeInterrupter.IsInterrupted)
        {
            sw.Stop();
            return TaoResponse.Fail<T>(action, "TaoClient pipe interrupter is active; dispatch aborted.", sw.Elapsed.TotalMilliseconds);
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, PipeInterrupter.Token);
        CancellationToken effectiveToken = linkedCts.Token;

        // 1. Primary path: High-speed zero-escaping Nexus Mailbox IPC
        if (_mailboxBridge != null && _mailboxBridge.IsConnected)
        {
            try
            {
                NexusMailboxIpcBridge.MailboxResponseRecord mbRes = await _mailboxBridge.DispatchTaoRawAsync(action, payload, timeoutMs, effectiveToken).ConfigureAwait(false);
                sw.Stop();
                double elapsedMs = sw.Elapsed.TotalMilliseconds;

                if (!mbRes.Success)
                {
                    return TaoResponse.Fail<T>(action, mbRes.Error ?? "Mailbox request failed.", elapsedMs, rawResult: mbRes.Result);
                }

                return ParseTaoResult<T>(action, mbRes.Result, elapsedMs);
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Mailbox dispatch for '{action}' threw: {ex.Message}. Attempting fallback...");
            }
        }

        // 2. Fallback path: Execute via IGameIpcBridge synchronous 1-frame Lua evaluation (EvalLuaSync 0x000E)
        if (_fallbackBridge != null && _fallbackBridge.IsConnected)
        {
            try
            {
                string payloadJson = payload != null ? JsonSerializer.Serialize(payload, JsonOptions) : "{}";
                string safePayload = payloadJson.Replace("\\", "\\\\").Replace("'", "\\'");
                string luaExpr = $"return TAO and TAO.Dispatch and TAO.Dispatch('{action}', '{safePayload}') or '{{\"status\":\"error\",\"error\":\"TAO not loaded\"}}'";

                string? rawOutput = await _fallbackBridge.ExecuteLuaAsync(luaExpr, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawOutput))
                {
                    string luaCommand = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('{action}', '{safePayload}') else Echo('[TAO_ERROR] TAO not loaded') end";
                    rawOutput = await _fallbackBridge.ExecuteCommandAsync(luaCommand, timeoutMs, cancellationToken).ConfigureAwait(false);
                }

                sw.Stop();
                double elapsedMs = sw.Elapsed.TotalMilliseconds;

                if (string.IsNullOrWhiteSpace(rawOutput))
                {
                    return TaoResponse.Fail<T>(action, "Empty response from fallback bridge.", elapsedMs);
                }

                // If output contains JSON document
                int jsonStart = rawOutput.IndexOf('{');
                int jsonEnd = rawOutput.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    string jsonSubstring = rawOutput.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    using JsonDocument doc = JsonDocument.Parse(jsonSubstring);
                    return ParseTaoResult<T>(action, doc.RootElement.Clone(), elapsedMs);
                }

                return TaoResponse.Fail<T>(action, $"Unexpected non-JSON response: {rawOutput}", elapsedMs);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return TaoResponse.Fail<T>(action, $"Fallback bridge exception: {ex.Message}", sw.Elapsed.TotalMilliseconds);
            }
        }

        sw.Stop();
        return TaoResponse.Fail<T>(action, "No active IPC bridge connected to game client.", sw.Elapsed.TotalMilliseconds);
    }

    private TaoResponse<T> ParseTaoResult<T>(string action, JsonElement resultEl, double elapsedMs)
    {
        if (resultEl.ValueKind == JsonValueKind.Object)
        {
            // Case A: Result is { status: "success", data: ... }
            if (resultEl.TryGetProperty("status", out JsonElement statusEl))
            {
                string? status = statusEl.GetString();
                if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    if (resultEl.TryGetProperty("data", out JsonElement dataEl))
                    {
                        return DeserializeData<T>(action, dataEl, elapsedMs, resultEl);
                    }
                    return TaoResponse.Ok<T>(action, default, elapsedMs, resultEl);
                }

                string err = resultEl.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "Unknown TAO error" : "TAO error";
                string? trace = resultEl.TryGetProperty("traceback", out JsonElement tbEl) ? tbEl.GetString() : null;
                return TaoResponse.Fail<T>(action, err, elapsedMs, trace, resultEl);
            }

            // Case B: Direct object returned
            return DeserializeData<T>(action, resultEl, elapsedMs, resultEl);
        }

        return DeserializeData<T>(action, resultEl, elapsedMs, resultEl);
    }

    private static TaoResponse<T> DeserializeData<T>(string action, JsonElement dataEl, double elapsedMs, JsonElement rawResult)
    {
        try
        {
            if (typeof(T) == typeof(JsonElement))
            {
                return TaoResponse.Ok<T>(action, (T)(object)dataEl.Clone(), elapsedMs, rawResult);
            }

            if (typeof(T) == typeof(string))
            {
                string text = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() ?? string.Empty : dataEl.GetRawText();
                return TaoResponse.Ok<T>(action, (T)(object)text, elapsedMs, rawResult);
            }

            if (dataEl.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return TaoResponse.Ok<T>(action, default, elapsedMs, rawResult);
            }

            T? deserialized = JsonSerializer.Deserialize<T>(dataEl.GetRawText(), JsonOptions);
            return TaoResponse.Ok<T>(action, deserialized, elapsedMs, rawResult);
        }
        catch (Exception ex)
        {
            return TaoResponse.Fail<T>(action, $"Failed to deserialize TAO payload to {typeof(T).Name}: {ex.Message}", elapsedMs, rawResult: rawResult);
        }
    }

    public async Task<TaoCapabilitiesDto> GetCapabilitiesAsync(
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        (string taoHash, string hashedPath) = ComputeTaoLuaSha256(_logger);
        bool isConnected = _mailboxBridge != null && _mailboxBridge.IsConnected;

        if (!isConnected && (_fallbackBridge == null || !_fallbackBridge.IsConnected))
        {
            return new TaoCapabilitiesDto
            {
                Version = "offline",
                EngineBuild = "offline",
                MailboxVersion = "offline",
                Handlers = GetStaticKnownHandlers(),
                TaoLuaSha256 = taoHash,
                HashedDiskPath = hashedPath,
                IsStale = null,
                RoundTripLatencyMs = 0.0,
                IsMailboxConnected = false,
                GamePhase = 0
            };
        }

        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            TaoResponse<JsonElement> resp = await DispatchAsync("get_capabilities", null, timeoutMs, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (resp.Success && resp.Data.ValueKind == JsonValueKind.Object)
            {
                string ver = resp.Data.TryGetProperty("version", out JsonElement vEl) ? vEl.GetString() ?? "unknown" : "unknown";
                string engineBuild = resp.Data.TryGetProperty("build", out JsonElement bEl) ? bEl.GetString() ?? "unknown" : "unknown";
                int phase = resp.Data.TryGetProperty("gamePhase", out JsonElement pEl) && pEl.TryGetInt32(out int p) ? p : 0;
                List<string> handlers = [];
                if (resp.Data.TryGetProperty("handlers", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in hEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            handlers.Add(item.GetString()!);
                        }
                    }
                }

                if (handlers.Count == 0)
                {
                    handlers = [.. GetStaticKnownHandlers()];
                }

                bool isStale = !string.IsNullOrWhiteSpace(engineBuild)
                    && !string.Equals(engineBuild, "dev", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(engineBuild, "unknown", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(engineBuild, taoHash, StringComparison.OrdinalIgnoreCase);

                return new TaoCapabilitiesDto
                {
                    Version = ver,
                    EngineBuild = engineBuild,
                    MailboxVersion = _mailboxBridge != null ? "3.0.0" : "fallback",
                    Handlers = handlers,
                    TaoLuaSha256 = taoHash,
                    HashedDiskPath = hashedPath,
                    IsStale = isStale,
                    RoundTripLatencyMs = resp.ElapsedMs > 0 ? resp.ElapsedMs : sw.Elapsed.TotalMilliseconds,
                    IsMailboxConnected = isConnected,
                    GamePhase = phase
                };
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Querying live TAO capabilities failed: {ex.Message}");
        }

        return new TaoCapabilitiesDto
        {
            Version = "2.2.0",
            EngineBuild = "unknown",
            MailboxVersion = isConnected ? "3.0.0" : "offline",
            Handlers = GetStaticKnownHandlers(),
            TaoLuaSha256 = taoHash,
            HashedDiskPath = hashedPath,
            IsStale = null,
            RoundTripLatencyMs = 0.0,
            IsMailboxConnected = isConnected,
            GamePhase = 0
        };
    }

    private static IReadOnlyList<string> GetStaticKnownHandlers() =>
    [
        "inspect_ui",
        "protect_queues",
        "navigate",
        "inspect_transition",
        "apply_fixture",
        "set_timescale",
        "step_frames",
        "pause",
        "bot_choreography",
        "relay_error",
        "flash_overlay",
        "get_world_state",
        "step_simulation",
        "switch_hero",
        "inject_fixture",
        "get_capabilities"
    ];

    private static (string hash, string path) ComputeTaoLuaSha256(Action<string>? logger = null)
    {
        string? envRepoRoot = Environment.GetEnvironmentVariable("KONGOR_REPO_ROOT");
        string repoRoot = !string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot)
            ? envRepoRoot
            : NexusMailboxIpcBridge.FindRepoRoot(AppContext.BaseDirectory);

        string currentDir = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory;

        string[] candidatePaths =
        [
            Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "tao.lua"),
            Path.Combine(currentDir, "games", "client", "override", "ui", "scripts", "tao.lua"),
            Path.Combine(baseDir, "games", "client", "override", "ui", "scripts", "tao.lua"),
            Path.Combine(repoRoot, "games", "shared", "ui", "scripts", "tao.lua"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "scripts", "tao.lua"),
            Path.Combine(currentDir, "override", "ui", "scripts", "tao.lua"),
            Path.Combine(repoRoot, "ui", "scripts", "tao.lua")
        ];

        foreach (string path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    List<string> filteredLines = new List<string>(lines.Length);
                    foreach (string line in lines)
                    {
                        if (!line.TrimStart().StartsWith("TAO.BUILD", StringComparison.OrdinalIgnoreCase))
                        {
                            filteredLines.Add(line);
                        }
                    }
                    string normalizedText = string.Join("\n", filteredLines);
                    byte[] bytes = Encoding.UTF8.GetBytes(normalizedText);
                    byte[] hash = SHA256.HashData(bytes);
                    string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                    return (hashHex, path);
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[WARN] Failed to read or hash candidate path '{path}': {ex.Message}");
                }
            }
        }

        return ("sha256:unknown", "unknown");
    }
}
