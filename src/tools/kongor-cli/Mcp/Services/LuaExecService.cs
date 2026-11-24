// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Domain service interface for executing Lua code strings against game client and server Named Pipe IPC channels.
/// </summary>
public interface ILuaExecService
{
    Task<string> ExecuteClientLuaStringAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<string> ExecuteServerLuaStringAsync(int port, string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default);
    string EscapeLuaString(string input);
    bool TryDetectSyntaxError(string output, out string errorCategory, out string remediationHint);
}

/// <summary>
/// Handles dispatching Lua commands to client and dedicated server over Named Pipe IPC,
/// with retry, self-healing syntax remediation, timeout handling, and output capture.
/// </summary>
public sealed class LuaExecService : ILuaExecService
{
    private readonly IGameIpcBridge _clientIpcBridge;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly Action<string> _logger;

    public LuaExecService(
        IGameIpcBridge? clientIpcBridge = null,
        IClientCommandRemediationService? remediationService = null,
        Action<string>? logger = null)
    {
        _clientIpcBridge = clientIpcBridge ?? new GameIpcBridge("hon-client-ipc");
        _remediationService = remediationService ?? new ClientCommandRemediationService();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[LuaExecService] {msg}"));
    }

    public async Task<string> ExecuteClientLuaStringAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        _logger($"[ClientLua] Executing: {luaCode} (timeout={timeoutMs}ms)");
        string wrapped = luaCode.Trim();
        if (!wrapped.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            wrapped = $"Script {wrapped}";
        }

        try
        {
            await _clientIpcBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            string output = await _clientIpcBridge.ExecuteCommandAsync(wrapped, timeoutMs, cancellationToken).ConfigureAwait(false);
            _logger($"[ClientLua] Output: {output}");
            return output;
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Client Lua IPC failed: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> ExecuteServerLuaStringAsync(int port, string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        _logger($"[ServerLua:{port}] Executing: {luaCode} (timeout={timeoutMs}ms)");
        string wrapped = luaCode.Trim();
        if (!wrapped.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            wrapped = $"Script {wrapped}";
        }

        try
        {
            await using var serverBridge = new GameIpcBridge($"hon-server-{port}");
            await serverBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            string output = await serverBridge.ExecuteCommandAsync(wrapped, timeoutMs, cancellationToken).ConfigureAwait(false);
            _logger($"[ServerLua:{port}] Output: {output}");
            return output;
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Server Lua IPC on port {port} failed: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    public string EscapeLuaString(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    public bool TryDetectSyntaxError(string output, out string errorCategory, out string remediationHint)
    {
        errorCategory = "unknown";
        remediationHint = string.Empty;

        if (string.IsNullOrWhiteSpace(output)) return false;

        bool isError = output.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("bad argument", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("is read only", StringComparison.OrdinalIgnoreCase);

        if (isError)
        {
            var record = _remediationService.RecordAndRemediate(output, output, "lua_exec", "engine");
            errorCategory = record.Remediation.ErrorCategory;
            remediationHint = record.Remediation.RemediationHint;
            return true;
        }

        return false;
    }
}
