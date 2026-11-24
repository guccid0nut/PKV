// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_client_eval_lua' (alias: 'client_eval_lua').
/// Provides sub-50ms programmatic control and hot-reloading over the live K2 game client via Named Pipe IPC.
/// Supports arbitrary Lua evaluation, hero cache reloading, catalog reloads, and UI interface refreshes.
/// </summary>
public sealed class ClientEvalLuaHandler
{
    public const long StaleHostFrameThresholdMs = 100;
    public const int DefaultIpcReadyGraceMs = 5000;
    public const int ColdStartClientAgeWindowSeconds = 25;

    private readonly IGameIpcBridge _ipcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly bool _enablePreSendEngineGate;
    private readonly Func<bool> _isClientBootingProvider;
    private readonly int _defaultIpcReadyGraceMs;

    public IGameIpcBridge IpcBridge => _ipcBridge;
    public bool EnablePreSendEngineGate => _enablePreSendEngineGate;

    public ClientEvalLuaHandler(
        TimeProvider? timeProvider = null,
        IGameIpcBridge? ipcBridge = null,
        Action<string>? logger = null,
        bool enablePreSendEngineGate = true,
        Func<bool>? isClientBootingProvider = null,
        int defaultIpcReadyGraceMs = DefaultIpcReadyGraceMs)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _enablePreSendEngineGate = enablePreSendEngineGate;
        _isClientBootingProvider = isClientBootingProvider ?? DefaultIsClientBooting;
        _defaultIpcReadyGraceMs = Math.Clamp(defaultIpcReadyGraceMs, 0, 15000);
    }

    private bool DefaultIsClientBooting()
    {
        if (GameServerManageHandler.IsTestExecution)
        {
            return false;
        }

        try
        {
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            Process[] procs = Process.GetProcessesByName("hon_x64");
            try
            {
                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        DateTimeOffset startedUtc = new(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
                        TimeSpan age = nowUtc - startedUtc;
                        if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(ColdStartClientAgeWindowSeconds))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"[ClientEvalLua] Cold-start age probe skipped for PID {p.Id}: {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ClientEvalLua] Process enumeration for cold-start probe skipped: {ex.Message}");
        }

        return false;
    }

    internal static string NormalizeRawLua(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        string rawLua = code.Trim();
        if (rawLua.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            rawLua = rawLua[7..].Trim();
        }

        // Unwrap outer console quotes only when the entire string is a single quoted wrapper
        if (IsSingleOuterQuotedString(rawLua, '"'))
        {
            rawLua = rawLua[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        else if (IsSingleOuterQuotedString(rawLua, '\''))
        {
            rawLua = rawLua[1..^1].Replace("\\'", "'").Replace("\\\\", "\\");
        }

        return rawLua;
    }

    internal static bool IsSingleOuterQuotedString(string text, char quote)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] != quote || text[^1] != quote)
        {
            return false;
        }

        bool escaped = false;
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == quote)
            {
                return false;
            }
        }

        return !escaped;
    }

    internal static string? ResolveRemediationHint(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        if (response.Contains("attempt to call global 'GetGamePhase'", StringComparison.OrdinalIgnoreCase))
        {
            return "GetGamePhase() does not exist in the global CScript VM. Use 'kongor_game_client_manage' (action: 'status') to inspect gamePhase (0..4) and isActiveMatch, or use 'Host.GetGamePhase()' / 'GetCvarInt(\"game_phase\")' in engine Lua.";
        }

        if (response.Contains("attempt to call global", StringComparison.OrdinalIgnoreCase))
        {
            return "K2 engine Lua functions are scoped. Common globals may reside under 'Host', 'Cvar', 'UIManager', or specific interface packages. Check docs/reference/mcp-issues/MCP-246 or inspect UI script packages.";
        }

        if (response.Contains("near '!'", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("near '!='", StringComparison.OrdinalIgnoreCase))
        {
            return "Lua 5.1 uses '~=' for inequality and 'not' for logical negation (not '!=' or '!'). Replace '!=' with '~='.";
        }

        if (response.Contains("near '/'", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("near '//'", StringComparison.OrdinalIgnoreCase))
        {
            return "Lua 5.1 uses '--' for comments and 'math.floor(a / b)' for integer division (no '//' operator or C-style comments).";
        }

        if (response.Contains("near 'continue'", StringComparison.OrdinalIgnoreCase))
        {
            return "Lua 5.1 does not have a 'continue' statement. Refactor loop body with an 'if not skip then ... end' guard.";
        }

        return null;
    }

    private static string? s_cachedLuacPath;

    private static string? ResolveLuacBinaryPath()
    {
        if (s_cachedLuacPath != null)
        {
            return s_cachedLuacPath.Length == 0 ? null : s_cachedLuacPath;
        }

        bool isWin = OperatingSystem.IsWindows();
        string exeName = isWin ? "luac51.exe" : "luac51";
        string ridDir = isWin
            ? "windows-x64"
            : OperatingSystem.IsMacOS()
                ? (RuntimeInformation.OSArchitecture == Architecture.X64 ? "osx-x64" : "osx-arm64")
                : "linux-x64";

        // 1. Check alongside the running binary first (self-contained / bin output folder)
        string baseDir = AppContext.BaseDirectory;
        string localRidCandidate = Path.Combine(baseDir, ridDir, exeName);
        if (File.Exists(localRidCandidate))
        {
            s_cachedLuacPath = localRidCandidate;
            return localRidCandidate;
        }

        string localDirectCandidate = Path.Combine(baseDir, exeName);
        if (File.Exists(localDirectCandidate))
        {
            s_cachedLuacPath = localDirectCandidate;
            return localDirectCandidate;
        }

        // 2. Walk up from BaseDirectory / CurrentDirectory to the true repository root (ProjectKongor.slnx or .git)
        string? repoRoot = null;
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), baseDir })
        {
            DirectoryInfo? dir = new(startPath);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                    Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            if (repoRoot != null)
            {
                break;
            }
        }

        repoRoot ??= Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(repoRoot, "native", "build", "luac51_build", "Release", exeName),
            Path.Combine(repoRoot, "native", "build", "luac51_build", exeName),
            Path.Combine(repoRoot, "native", "tools", "luac51", "build", "Release", exeName),
            Path.Combine(repoRoot, "artifacts", "native-binaries", ridDir, exeName),
            Path.Combine(repoRoot, ".tools", "bin", "lua", exeName)
        ];

        string? found = candidates.FirstOrDefault(File.Exists);
        s_cachedLuacPath = found ?? string.Empty;
        return found;
    }

    internal static async Task<(bool IsValid, int? Line, string? ErrorMessage)> ValidateLuaSyntaxPreSendAsync(
        string rawLua,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawLua))
        {
            return (true, null, null);
        }

        string? luacPath = ResolveLuacBinaryPath();
        if (string.IsNullOrEmpty(luacPath))
        {
            return (true, null, null);
        }

        var (stmtOk, stmtErr) = await RunLuacParseOnlyAsync(luacPath, rawLua, cancellationToken).ConfigureAwait(false);
        if (stmtOk)
        {
            return (true, null, null);
        }

        // Also accept single-expression snippets (e.g. "1 + 1" or "Host.GetGamePhase()") which compile cleanly when prefixed with "return "
        var (exprOk, _) = await RunLuacParseOnlyAsync(luacPath, "return " + rawLua, cancellationToken).ConfigureAwait(false);
        if (exprOk)
        {
            return (true, null, null);
        }

        int? parsedLine = null;
        if (!string.IsNullOrWhiteSpace(stmtErr))
        {
            var m = System.Text.RegularExpressions.Regex.Match(stmtErr, @":(\d+):\s*(.+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int ln))
            {
                parsedLine = ln;
                stmtErr = $"line {ln}: {m.Groups[2].Value.Trim()}";
            }
        }

        return (false, parsedLine, stmtErr ?? "Invalid Lua 5.1 syntax");
    }

    private static async Task<(bool Ok, string? Error)> RunLuacParseOnlyAsync(
        string luacPath,
        string source,
        CancellationToken cancellationToken)
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"kongor_eval_{Guid.NewGuid():N}.lua");
        try
        {
            await File.WriteAllTextAsync(tmpFile, source, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            using Process proc = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = luacPath,
                    Arguments = $"-p \"{tmpFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                return (true, null);
            }
            string combined = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return (false, combined);
        }
        catch
        {
            return (true, null);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
            }
            catch
            {
                // Best-effort temp cleanup
            }
        }
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "eval";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        int timeoutMs = 3000;
        if (arguments.TryGetProperty("timeout_ms", out JsonElement toEl) && toEl.TryGetInt32(out int parsedTo) && parsedTo > 0)
        {
            timeoutMs = parsedTo;
        }
        else if (arguments.TryGetProperty("timeoutMs", out JsonElement toEl2) && toEl2.TryGetInt32(out int parsedTo2) && parsedTo2 > 0)
        {
            timeoutMs = parsedTo2;
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // 0. Pre-flight Lua 5.1 syntax check before touching Named Pipe / Mailbox IPC
            if (action == "eval" &&
                arguments.ValueKind == JsonValueKind.Object &&
                (arguments.TryGetProperty("code", out JsonElement preCodeEl) ||
                 arguments.TryGetProperty("lua", out preCodeEl) ||
                 arguments.TryGetProperty("script", out preCodeEl)) &&
                preCodeEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(preCodeEl.GetString()))
            {
                string candidateCode = preCodeEl.GetString()!.Trim();
                bool skipSyntax = (arguments.TryGetProperty("skip_syntax_check", out JsonElement skipSynEl) && skipSynEl.ValueKind == JsonValueKind.True) ||
                                  candidateCode.StartsWith("Cmd ", StringComparison.OrdinalIgnoreCase) ||
                                  candidateCode.StartsWith("Trigger ", StringComparison.OrdinalIgnoreCase);
                if (!skipSyntax)
                {
                    string rawCandidate = NormalizeRawLua(candidateCode);
                    var (isValidLua, syntaxLine, syntaxError) = await ValidateLuaSyntaxPreSendAsync(rawCandidate, cancellationToken).ConfigureAwait(false);
                    if (!isValidLua)
                    {
                        sw.Stop();
                        string? hint = ResolveRemediationHint(syntaxError) ??
                                       "Fix the Lua 5.1 syntax error at the reported line before re-running kongor_client_eval_lua.";
                        string formattedErr = $"[LUA_SYNTAX_ERR] Pre-send luac51 check rejected invalid Lua ({syntaxError})";
                        _logger?.Invoke($"[ClientEvalLua] {formattedErr}");
                        return ToolCallResult.Json(new
                        {
                            status = "error",
                            success = false,
                            action = "eval",
                            code = candidateCode,
                            errorCode = "luaSyntaxError",
                            executed = false,
                            syntaxLine,
                            error = formattedErr,
                            result = formattedErr,
                            response = formattedErr,
                            remediationHint = hint,
                            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        }, isError: true);
                    }
                }
            }

            // 1. Ensure IPC connection is active (with bounded cold-start grace for MCP-215)
            if (!_ipcBridge.IsConnected)
            {
                string targetPipe = string.IsNullOrWhiteSpace(_ipcBridge.PipeName) ? "kongor-native-client" : _ipcBridge.PipeName;
                int? explicitIpcWaitMs = null;
                if ((arguments.TryGetProperty("wait_for_ipc_ms", out JsonElement wIpcEl) ||
                     arguments.TryGetProperty("waitForIpcMs", out wIpcEl)) &&
                    wIpcEl.TryGetInt32(out int parsedIpcWait) && parsedIpcWait >= 0)
                {
                    explicitIpcWaitMs = Math.Clamp(parsedIpcWait, 0, 15000);
                }

                bool booting = _isClientBootingProvider();
                int ipcWaitBudgetMs = explicitIpcWaitMs ?? (booting ? _defaultIpcReadyGraceMs : 0);
                Exception? lastConnEx = null;
                int attempts = 0;

                if (ipcWaitBudgetMs > 0)
                {
                    Stopwatch ipcSw = Stopwatch.StartNew();
                    _logger?.Invoke($"[ClientEvalLua] IPC Bridge disconnected while client is booting/grace requested; awaiting pipe '{targetPipe}' for up to {ipcWaitBudgetMs}ms (MCP-215)...");
                    while (!_ipcBridge.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        attempts++;
                        int remainingMs = Math.Max(50, ipcWaitBudgetMs - (int)ipcSw.ElapsedMilliseconds);
                        try
                        {
                            await _ipcBridge.ConnectAsync(timeoutMs: Math.Min(remainingMs, 500), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception connEx) when (connEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            lastConnEx = connEx;
                        }

                        if (_ipcBridge.IsConnected)
                        {
                            _logger?.Invoke($"[ClientEvalLua] IPC pipe '{targetPipe}' connected after {attempts} attempt(s) in {ipcSw.ElapsedMilliseconds}ms.");
                            break;
                        }

                        if (ipcSw.ElapsedMilliseconds >= ipcWaitBudgetMs)
                        {
                            break;
                        }

                        int delayMs = (int)Math.Min(150, Math.Max(1, ipcWaitBudgetMs - ipcSw.ElapsedMilliseconds));
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    attempts = 1;
                    _logger?.Invoke($"[ClientEvalLua] IPC Bridge disconnected. Attempting connection to {targetPipe}...");
                    try
                    {
                        await _ipcBridge.ConnectAsync(timeoutMs: Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception connEx)
                    {
                        lastConnEx = connEx;
                    }
                }

                if (!_ipcBridge.IsConnected)
                {
                    sw.Stop();
                    if (lastConnEx != null)
                    {
                        _logger?.Invoke($"[ClientEvalLua] Connection failed after {attempts} attempt(s): {lastConnEx.Message}");
                    }
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        success = false,
                        action,
                        errorCode = "clientOffline",
                        pipeName = targetPipe,
                        connectAttempts = attempts,
                        ipcGraceBudgetMs = ipcWaitBudgetMs,
                        error = $"K2 game client is not running or IPC pipe is offline (\\\\.\\pipe\\{targetPipe} / \\\\.\\pipe\\willowmaker-ipc). Launch the game via kongor_game_client_manage (action: 'launch') or Willowmaker to enable in-game hot-reloading and evaluation.",
                        details = lastConnEx?.Message,
                        executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }
            }

            return action switch
            {
                "eval" => await HandleEvalAsync(arguments, timeoutMs, sw, cancellationToken).ConfigureAwait(false),
                "reload_hero" => await HandleReloadHeroAsync(arguments, timeoutMs, sw, cancellationToken).ConfigureAwait(false),
                "reload_all_heroes" => await HandleReloadAllHeroesAsync(timeoutMs, sw, cancellationToken).ConfigureAwait(false),
                "reload_store" => await HandleReloadStoreAsync(timeoutMs, sw, cancellationToken).ConfigureAwait(false),
                "reload_ui" => await HandleReloadUiAsync(timeoutMs, sw, cancellationToken).ConfigureAwait(false),
                _ => ToolCallResult.Error($"Unknown action '{action}'. Valid actions: eval, reload_hero, reload_all_heroes, reload_store, reload_ui.")
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"[ClientEvalLua] Error executing '{action}': {ex.Message}");
            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                action,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private (string Channel, string? FallbackReason) ResolveChannelInfo(string? requestedChannel = null)
    {
        if (_ipcBridge is CompositeGameIpcBridge composite)
        {
            return (composite.LastChannel, composite.LastFallbackReason);
        }

        if (_ipcBridge is NexusMailboxIpcBridge ||
            string.Equals(_ipcBridge.PipeName, "nexus-mailbox-ipc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase))
        {
            return ("mailbox", string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase) ? "explicit_channel_override" : null);
        }

        return ("named_pipe", null);
    }

    private async Task<ToolCallResult> HandleEvalAsync(JsonElement arguments, int timeoutMs, Stopwatch sw, CancellationToken cancellationToken)
    {
        string? code = null;
        if ((arguments.TryGetProperty("code", out JsonElement cEl) ||
             arguments.TryGetProperty("lua", out cEl) ||
             arguments.TryGetProperty("script", out cEl)) &&
            !string.IsNullOrWhiteSpace(cEl.GetString()))
        {
            code = cEl.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return ToolCallResult.Error("Missing required parameter 'code' for action 'eval'.");
        }

        string? requestedChannel = null;
        if (arguments.TryGetProperty("channel", out JsonElement chEl) &&
            chEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(chEl.GetString()))
        {
            requestedChannel = chEl.GetString()!.Trim().ToLowerInvariant();
        }

        string response;
        double waitedForEngineMs = 0;
        if (code.StartsWith("Cmd ", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("Trigger ", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Invoke($"[ClientEvalLua] Dispatching console command: {code}");
            response = (_ipcBridge is CompositeGameIpcBridge compositeCmd
                ? await compositeCmd.ExecuteCommandOnChannelAsync(code, requestedChannel, timeoutMs, cancellationToken).ConfigureAwait(false)
                : await _ipcBridge.ExecuteCommandAsync(code, timeoutMs, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        }
        else
        {
            string rawLua = NormalizeRawLua(code);
            bool isReloadInterfaces = rawLua.Contains("ReloadInterfaces", StringComparison.OrdinalIgnoreCase);
            bool explicitOptIn =
                (arguments.TryGetProperty("wait_for_engine", out JsonElement wfeEl) && wfeEl.ValueKind == JsonValueKind.True) ||
                (arguments.TryGetProperty("waitForEngine", out JsonElement wfeEl2) && wfeEl2.ValueKind == JsonValueKind.True);
            bool explicitOptOut =
                (arguments.TryGetProperty("wait_for_engine", out JsonElement wfeOut) && wfeOut.ValueKind == JsonValueKind.False) ||
                (arguments.TryGetProperty("waitForEngine", out JsonElement wfeOut2) && wfeOut2.ValueKind == JsonValueKind.False);
            bool isMailboxExplicit = string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase);

            bool waitForEngine = !isMailboxExplicit && !explicitOptOut && (_enablePreSendEngineGate || explicitOptIn);
            int evalTimeoutMs = timeoutMs;
            if (waitForEngine)
            {
                waitedForEngineMs = await WaitIfMainThreadBusyAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                if (waitedForEngineMs > 0)
                {
                    evalTimeoutMs = Math.Max(50, timeoutMs - (int)Math.Ceiling(waitedForEngineMs));
                }
            }
            _logger?.Invoke($"[ClientEvalLua] Dispatching synchronous Lua evaluation: {rawLua}");
            string? luaRes = null;

            // Fast-path: Duplex Lua RPC (0x0080) with sub-millisecond QPC framing
            if (!isReloadInterfaces && requestedChannel is null or "named_pipe" or "duplex")
            {
                try
                {
                    DuplexLuaRpcResult duplexRes = await _ipcBridge.EvalLuaDuplexAsync(rawLua, (uint)evalTimeoutMs, cancellationToken).ConfigureAwait(false);
                    if (duplexRes.Success)
                    {
                        luaRes = duplexRes.ResultOrError;
                    }
                    else if (duplexRes.TimedOut)
                    {
                        sw.Stop();
                        var (timeoutChannel, timeoutFallbackReason) = ResolveChannelInfo(requestedChannel);
                        string timeoutMsg = $"Duplex Lua evaluation timed out after {evalTimeoutMs}ms ({duplexRes.ResultOrError})";
                        _logger?.Invoke($"[ClientEvalLua] {timeoutMsg}");
                        return ToolCallResult.Json(new
                        {
                            status = "error",
                            success = false,
                            action = "eval",
                            code,
                            channel = timeoutChannel,
                            fallbackReason = timeoutFallbackReason,
                            waitedForEngineMs = Math.Round(waitedForEngineMs, 2),
                            errorCode = "ipcPipeTimeout",
                            executed = (bool?)null,
                            error = timeoutMsg,
                            result = $"[ERROR] {timeoutMsg}",
                            response = $"[ERROR] {timeoutMsg}",
                            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        }, isError: true);
                    }
                    else if (duplexRes.TransportUnsupported || duplexRes.IsUninitializedDefault)
                    {
                        _logger?.Invoke($"[ClientEvalLua] Duplex 0x0080 transport unsupported ({duplexRes.ResultOrError}); proceeding to 0x000E path.");
                    }
                    else
                    {
                        luaRes = $"[LUA_ERR] {duplexRes.ResultOrError}";
                    }
                }
                catch (TimeoutException timeoutEx)
                {
                    sw.Stop();
                    var (timeoutChannel, timeoutFallbackReason) = ResolveChannelInfo(requestedChannel);
                    _logger?.Invoke($"[ClientEvalLua] Synchronous Lua evaluation timed out after {timeoutMs}ms: {timeoutEx.Message}");
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        success = false,
                        action = "eval",
                        code,
                        channel = timeoutChannel,
                        fallbackReason = timeoutFallbackReason,
                        waitedForEngineMs = Math.Round(waitedForEngineMs, 2),
                        errorCode = "ipcPipeTimeout",
                        executed = (bool?)null,
                        error = timeoutEx.Message,
                        result = $"[ERROR] {timeoutEx.Message}",
                        response = $"[ERROR] {timeoutEx.Message}",
                        executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[ClientEvalLua] Duplex 0x0080 fast-path fallback ({ex.Message}); proceeding to 0x000E path.");
                }
            }

            if (luaRes == null)
            {
                try
                {
                    luaRes = _ipcBridge is CompositeGameIpcBridge compositeLua
                        ? await compositeLua.ExecuteLuaOnChannelAsync(rawLua, requestedChannel, evalTimeoutMs, cancellationToken).ConfigureAwait(false)
                        : await _ipcBridge.ExecuteLuaAsync(rawLua, evalTimeoutMs, cancellationToken).ConfigureAwait(false);

                    if (!isReloadInterfaces &&
                        !string.IsNullOrEmpty(luaRes) &&
                        (luaRes.Contains("engine_busy", StringComparison.OrdinalIgnoreCase) ||
                         luaRes.Contains("Timed out waiting for K2 main thread Lua evaluation", StringComparison.OrdinalIgnoreCase)))
                    {
                        int remainingBudgetMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                        if (remainingBudgetMs >= 250)
                        {
                            int jitterDelayMs = Math.Min(Random.Shared.Next(35, 110), remainingBudgetMs - 100);
                            _logger?.Invoke($"[ClientEvalLua] K2 reported transient engine_busy; backing off {jitterDelayMs}ms with readiness probe before single retry...");
                            await Task.Delay(TimeSpan.FromMilliseconds(jitterDelayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                            waitedForEngineMs += jitterDelayMs;
                            waitedForEngineMs += await WaitIfMainThreadBusyAsync(Math.Max(100, timeoutMs - (int)sw.ElapsedMilliseconds), cancellationToken).ConfigureAwait(false);
                            int retryBudgetMs = Math.Max(100, timeoutMs - (int)sw.ElapsedMilliseconds);
                            luaRes = _ipcBridge is CompositeGameIpcBridge compositeRetry
                                ? await compositeRetry.ExecuteLuaOnChannelAsync(rawLua, requestedChannel, retryBudgetMs, cancellationToken).ConfigureAwait(false)
                                : await _ipcBridge.ExecuteLuaAsync(rawLua, retryBudgetMs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (TimeoutException timeoutEx) when (!isReloadInterfaces)
            {
                sw.Stop();
                var (timeoutChannel, timeoutFallbackReason) = ResolveChannelInfo(requestedChannel);
                _logger?.Invoke($"[ClientEvalLua] Synchronous Lua evaluation timed out after {timeoutMs}ms: {timeoutEx.Message}");
                return ToolCallResult.Json(new
                {
                    status = "error",
                    success = false,
                    action = "eval",
                    code,
                    channel = timeoutChannel,
                    fallbackReason = timeoutFallbackReason,
                    waitedForEngineMs = Math.Round(waitedForEngineMs, 2),
                    errorCode = "ipcPipeTimeout",
                    executed = (bool?)null,
                    error = timeoutEx.Message,
                    result = $"[ERROR] {timeoutEx.Message}",
                    response = $"[ERROR] {timeoutEx.Message}",
                    executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
            catch (Exception ex) when (isReloadInterfaces)
            {
                _logger?.Invoke($"[ClientEvalLua] ReloadInterfaces eval completed with transient pipe reset: {ex.Message}");
                luaRes = $"[OK] ReloadInterfaces dispatched ({ex.Message})";
            }
        }

            if (luaRes == null)
            {
                if (isReloadInterfaces)
                {
                    response = "[OK] ReloadInterfaces dispatched (UI state reloaded)";
                }
                else
                {
                    string sanitizedCode = rawLua
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n");
                    string wrappedLua = $"Script \"local _fn, _ce = loadstring(\\\"{sanitizedCode}\\\") if not _fn then Echo('[LUA_ERR] ' .. tostring(_ce)) else local _ok, _res = pcall(_fn) if not _ok then Echo('[LUA_ERR] ' .. tostring(_res)) else Echo('[LUA_OK] ' .. tostring(_res)) end end\"";
                    response = (_ipcBridge is CompositeGameIpcBridge compositeWrap
                        ? await compositeWrap.ExecuteCommandOnChannelAsync(wrappedLua, requestedChannel, evalTimeoutMs, cancellationToken).ConfigureAwait(false)
                        : await _ipcBridge.ExecuteCommandAsync(wrappedLua, evalTimeoutMs, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
                }
            }
            else
            {
                response = luaRes;
            }
        }

        sw.Stop();

        var (channel, fallbackReason) = ResolveChannelInfo(requestedChannel);
        bool isMainThreadDrainTimeout = response.Contains("Timed out waiting for K2 main thread Lua evaluation", StringComparison.OrdinalIgnoreCase);
        bool hasError = isMainThreadDrainTimeout ||
                        response.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                        response.Contains("[LUA_ERR]", StringComparison.OrdinalIgnoreCase);
        string? errorCode = isMainThreadDrainTimeout
            ? "mainThreadDrainTimeout"
            : (hasError ? "luaEvalError" : null);
        bool executed = !isMainThreadDrainTimeout;

        string? remediationHint = hasError ? ResolveRemediationHint(response) : null;

        return ToolCallResult.Json(new
        {
            status = hasError ? "error" : "success",
            success = !hasError,
            action = "eval",
            code,
            channel,
            fallbackReason,
            waitedForEngineMs = Math.Round(waitedForEngineMs, 2),
            errorCode,
            executed,
            result = response,
            response,
            remediationHint,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: hasError);
    }

    private async Task<double> WaitIfMainThreadBusyAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            var initialState = await _ipcBridge.GetEngineStateAsync(Math.Min(timeoutMs, 350), cancellationToken).ConfigureAwait(false);
            if (initialState == null)
            {
                return 0;
            }

            bool isStalled = initialState.MainThreadBusy ||
                             (initialState.MsSinceHostFrameAdvance.HasValue &&
                              initialState.MsSinceHostFrameAdvance.Value > StaleHostFrameThresholdMs);
            if (!isStalled)
            {
                return 0;
            }

            int maxWaitMs = Math.Max(0, timeoutMs - 50);
            if (maxWaitMs <= 0)
            {
                return 0;
            }

            Stopwatch waitSw = Stopwatch.StartNew();
            int initialFrame = initialState.HostFrame ?? 0;
            _logger?.Invoke(
                $"[ClientEvalLua] Pre-send gate: engine stalled (mainThreadBusy={initialState.MainThreadBusy}, " +
                $"msSinceHostFrameAdvance={initialState.MsSinceHostFrameAdvance?.ToString() ?? "null"}, hostFrame={initialFrame}). " +
                $"Waiting within {maxWaitMs}ms budget before sending Lua eval once...");

            while (waitSw.ElapsedMilliseconds < maxWaitMs && !cancellationToken.IsCancellationRequested)
            {
                int remainingWaitMs = (int)Math.Max(1, maxWaitMs - waitSw.ElapsedMilliseconds);
                int jitteredMs = Random.Shared.Next(25, 96);
                int sleepMs = Math.Min(jitteredMs, remainingWaitMs);
                await Task.Delay(TimeSpan.FromMilliseconds(sleepMs), _timeProvider, cancellationToken).ConfigureAwait(false);

                remainingWaitMs = (int)Math.Max(1, maxWaitMs - waitSw.ElapsedMilliseconds);
                var pollState = await _ipcBridge.GetEngineStateAsync(Math.Min(350, remainingWaitMs), cancellationToken).ConfigureAwait(false);
                if (pollState == null)
                {
                    break;
                }

                bool noLongerBusy = !pollState.MainThreadBusy;
                bool frameFresh = !pollState.MsSinceHostFrameAdvance.HasValue ||
                                  pollState.MsSinceHostFrameAdvance.Value <= StaleHostFrameThresholdMs;
                int currentFrame = pollState.HostFrame ?? 0;
                bool frameAdvanced = initialFrame <= 0 || currentFrame <= 0 || currentFrame > initialFrame;
                if (noLongerBusy && frameFresh && frameAdvanced)
                {
                    break;
                }
            }

            waitSw.Stop();
            return waitSw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger?.Invoke($"[ClientEvalLua] Pre-send engine state probe skipped: {ex.Message}");
            return 0;
        }
    }

    private async Task<ToolCallResult> HandleReloadHeroAsync(JsonElement arguments, int timeoutMs, Stopwatch sw, CancellationToken cancellationToken)
    {
        string hero = "Hero_Pyromancer";
        if ((arguments.TryGetProperty("hero", out JsonElement hEl) ||
             arguments.TryGetProperty("name", out hEl) ||
             arguments.TryGetProperty("heroName", out hEl)) &&
            !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            hero = hEl.GetString()!.Trim();
        }

        string cmd = $"Script \"if HeroDefinitionsCache and HeroDefinitionsCache.ReloadHero then " +
                     $"local _h = HeroDefinitionsCache.ReloadHero('{hero}') " +
                     $"Echo('[HERO_RELOADED] ' .. tostring(_h and _h.displayName or '{hero}')) " +
                     $"else dofile('/ui/scripts/newui/hero_definitions_cache.lua') " +
                     $"Echo('[CACHE_RELOADED] HeroDefinitionsCache re-executed') end\"";

        _logger?.Invoke($"[ClientEvalLua] Dispatching hero reload for '{hero}'...");
        string response = await _ipcBridge.ExecuteCommandAsync(cmd, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "reload_hero",
            hero,
            response,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleReloadAllHeroesAsync(int timeoutMs, Stopwatch sw, CancellationToken cancellationToken)
    {
        string cmd = "Script \"if HeroDefinitionsCache and HeroDefinitionsCache.LoadAllHeroes then " +
                     "local _c = HeroDefinitionsCache.LoadAllHeroes() " +
                     "Echo('[ALL_HEROES_RELOADED] Total: ' .. tostring(_c)) " +
                     "else dofile('/ui/scripts/newui/hero_definitions_cache.lua') end\"";

        _logger?.Invoke("[ClientEvalLua] Dispatching reload for all modular heroes...");
        string response = await _ipcBridge.ExecuteCommandAsync(cmd, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "reload_all_heroes",
            response,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleReloadStoreAsync(int timeoutMs, Stopwatch sw, CancellationToken cancellationToken)
    {
        string cmd = "Script \"if HoN_Store and HoN_Store.RefreshCache then " +
                     "HoN_Store:RefreshCache() " +
                     "Echo('[STORE_REFRESHED] HoN_Store refreshed') " +
                     "else dofile('/ui/scripts/store.lua') end\"";

        _logger?.Invoke("[ClientEvalLua] Dispatching store refresh...");
        string response = await _ipcBridge.ExecuteCommandAsync(cmd, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "reload_store",
            response,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleReloadUiAsync(int timeoutMs, Stopwatch sw, CancellationToken cancellationToken)
    {
        _logger?.Invoke("[ClientEvalLua] Executing ReloadInterfaces & nexus_bridge...");
        string resp1 = await _ipcBridge.ExecuteCommandAsync("ReloadInterfaces", timeoutMs, cancellationToken).ConfigureAwait(false);
        string resp2;
        try
        {
            resp2 = await _ipcBridge.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus_bridge.lua')", timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ClientEvalLua] Secondary nexus_bridge reload encountered transient error after ReloadInterfaces: {ex.Message}");
            resp2 = $"[WARN] {ex.Message}";
        }
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            action = "reload_ui",
            responses = new[] { resp1, resp2 },
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}
