// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Structured remediation recommendations and self-healing analysis for a failed client command.
/// </summary>
public sealed record ClientCommandRemediation
{
    public string ErrorCategory { get; init; } = "GenericError";
    public string RemediationHint { get; init; } = string.Empty;
    public string? ImprovedCommand { get; init; }
    public bool CanAutoRetry { get; init; }
}

/// <summary>
/// Persistent record of a failed game client command and its remediation details.
/// </summary>
public sealed record ClientCommandErrorRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Target { get; init; } = "client";
    public string Channel { get; init; } = "unknown";
    public string RawErrorResponse { get; init; } = string.Empty;
    public ClientCommandRemediation Remediation { get; init; } = new();
    public bool SelfHealed { get; init; }
}

/// <summary>
/// Service interface for collecting client command failures and deriving automated improvements/remediations.
/// </summary>
public interface IClientCommandRemediationService
{
    bool IsEnabled { get; set; }
    event Action<ClientCommandErrorRecord>? ErrorRecorded;
    ClientCommandErrorRecord RecordAndRemediate(string command, string errorResponse, string channel, string target = "client");
    void MarkSelfHealed(string recordId);
    IReadOnlyList<ClientCommandErrorRecord> GetRecentErrors(int limit = 10);
    void Clear();
    Task FlushAsync();
}

/// <summary>
/// Implementation of client command error collection and intelligent remediation analysis.
/// Analyzes failure responses (nil global indexing, event signatures, widget names, missing script prefixes,
/// offline processes, and K2 syntax errors), generates improved commands for automatic self-healing retry,
/// and stores error records to disk ('logs/MCP Conversation History/client_command_errors.ndjson').
/// </summary>
public sealed class ClientCommandRemediationService : IClientCommandRemediationService, IAsyncDisposable, IDisposable
{
    public bool IsEnabled { get; set; } = true;
    public event Action<ClientCommandErrorRecord>? ErrorRecorded;
    private readonly ConcurrentQueue<ClientCommandErrorRecord> _recentErrors = new();
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly string _logFilePath;
    private readonly Channel<ClientCommandErrorRecord> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processQueueTask;
    private int _pendingWrites;
    private const int MaxInMemoryErrors = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public ClientCommandRemediationService(string? logDirectory = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        string? envVar = Environment.GetEnvironmentVariable("KONGOR_ENABLE_SELF_HEALING");
        IsEnabled = string.IsNullOrWhiteSpace(envVar) ||
                    (!envVar.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                     !envVar.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                     !envVar.Equals("no", StringComparison.OrdinalIgnoreCase));

        string baseDir = logDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "MCP Conversation History");
        _logFilePath = Path.Combine(baseDir, "client_command_errors.ndjson");

        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] Failed to create log directory '{baseDir}': {ex.Message}");
        }

        _channel = Channel.CreateUnbounded<ClientCommandErrorRecord>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        _processQueueTask = Task.Run(ProcessDiskQueueAsync);
    }

    public ClientCommandErrorRecord RecordAndRemediate(string command, string errorResponse, string channel, string target = "client")
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        ClientCommandRemediation remediation = AnalyzeCommandError(command, errorResponse, channel, target);
        if (!IsEnabled)
        {
            remediation = remediation with { CanAutoRetry = false, ImprovedCommand = null };
        }

        ClientCommandErrorRecord record = new()
        {
            Timestamp = now,
            Command = command,
            Target = target,
            Channel = channel,
            RawErrorResponse = errorResponse,
            Remediation = remediation,
            SelfHealed = false
        };

        lock (_lock)
        {
            _recentErrors.Enqueue(record);
            while (_recentErrors.Count > MaxInMemoryErrors)
            {
                _recentErrors.TryDequeue(out _);
            }
        }

        Interlocked.Increment(ref _pendingWrites);
        _channel.Writer.TryWrite(record);

        try
        {
            ErrorRecorded?.Invoke(record);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] ErrorRecorded subscriber failed: {ex.Message}");
        }

        return record;
    }

    public void MarkSelfHealed(string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return;

        ClientCommandErrorRecord? healedRecord = null;
        lock (_lock)
        {
            ClientCommandErrorRecord[] snapshot = _recentErrors.ToArray();
            _recentErrors.Clear();
            foreach (ClientCommandErrorRecord r in snapshot)
            {
                if (r.Id == recordId)
                {
                    ClientCommandErrorRecord healed = r with { SelfHealed = true };
                    _recentErrors.Enqueue(healed);
                    healedRecord = healed;
                }
                else
                {
                    _recentErrors.Enqueue(r);
                }
            }
        }

        if (healedRecord != null)
        {
            Interlocked.Increment(ref _pendingWrites);
            _channel.Writer.TryWrite(healedRecord);
        }
    }

    public IReadOnlyList<ClientCommandErrorRecord> GetRecentErrors(int limit = 10)
    {
        int count = Math.Clamp(limit, 1, MaxInMemoryErrors);
        lock (_lock)
        {
            return [.. _recentErrors.Reverse().Take(count)];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _recentErrors.Clear();
        }
    }

    /// <summary>
    /// Analyzes the command string, error response, and execution channel to determine root cause and improved command.
    /// </summary>
    public static ClientCommandRemediation AnalyzeCommandError(string command, string errorResponse, string channel, string? target = null)
    {
        string cmd = command ?? string.Empty;
        string err = errorResponse ?? string.Empty;

        string improved = cmd;
        List<string> categories = [];
        List<string> hints = [];
        bool canAutoRetry = false;

        // 0a. Dedicated Server Worker Offline / Docker Container Target (MCP-088, MCP-004)
        if (target != null && target.StartsWith("server", StringComparison.OrdinalIgnoreCase) &&
            (err.Contains("No running dedicated game server", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("target_offline", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("Failed to execute command on server port", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("Timed out", StringComparison.OrdinalIgnoreCase) ||
             channel == "none"))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "ServerOffline",
                RemediationHint = "The target dedicated game server worker is offline on the host Win32 named pipe. Launch a host worker via 'kongor_game_server_manage(action=\"start\")', or if targeting Docker-hosted workers (ports 11335..11336), dispatch commands via 'docker exec compel-dedicated-server ...' or COMPEL IPC.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 0b. Client Process Offline / No Channel Available (MCP-003, MCP-210)
        if (channel == "none" ||
            err.Contains("not currently running", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Target window 'hon_x64' not found", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("is not reachable", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "ClientOffline",
                RemediationHint = "The player game client (hon_x64.exe) is not running or has no active window handle. Launch it first with 'kongor_game_client_manage action:launch' or direct map loader 'kongor_match_launch'.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 1. K2 Global 'Windows' Nil Indexing
        if (improved.Contains("Windows.", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("global 'Windows'", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Windows is nil", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bWindows\.main\b", "UIManager.GetInterface('main')", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"\bWindows\.", "UIManager.GetInterface('main'):", RegexOptions.IgnoreCase);
            categories.Add("NilGlobalReference");
            hints.Add("In K2 Lua, the global table 'Windows' is not defined. The root interface manager is 'UIManager'. Use UIManager.GetInterface('main'):GetWidget('widget_name') or GetWidget('widget_name') within interface scope.");
            canAutoRetry = true;
        }

        // 1b. K2 Global 'interface' Nil Indexing (MCP-002)
        if (Regex.IsMatch(improved, @"\binterface\s*:\s*UICmd", RegexOptions.IgnoreCase) ||
            err.Contains("global 'interface'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\binterface\s*:\s*UICmd\s*\(\s*(['""][^'""]*['""])\s*\)", "Cmd($1)", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"\binterface\s*:\s*", "UIManager.GetInterface('main'):", RegexOptions.IgnoreCase);
            categories.Add("InvalidGlobalReference");
            hints.Add("In K2 Lua, 'interface' is only exposed inside XML interface script tags, not in global root context. Use 'Cmd(...)' to dispatch engine UI commands, or 'UIManager.GetInterface('main')'.");
            canAutoRetry = true;
        }

        // 1c. K2 Global 'LuaTrigger' Nil Indexing (MCP-005)
        if (Regex.IsMatch(improved, @"\bLuaTrigger\s*\.\s*GetTrigger", RegexOptions.IgnoreCase) ||
            err.Contains("global 'LuaTrigger'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bLuaTrigger\s*\.\s*GetTrigger\b", "GetTrigger", RegexOptions.IgnoreCase);
            categories.Add("InvalidTriggerApi");
            hints.Add("In K2 Lua, 'LuaTrigger' is not a global object. In-engine triggers are retrieved via the global function 'GetTrigger(name)'.");
            canAutoRetry = true;
        }

        // 1d. Sandboxed Lua 'io' / 'os' / 'debug' Libraries (MCP-001, MCP-019, MCP-029)
        if (Regex.IsMatch(improved, @"\b(io\.(?:open|write|read|close|lines|flush)|os\.(?:execute|remove|rename|getenv)|debug\.(?:getupvalue|setupvalue|getinfo|traceback|getlocal|setlocal))\b", RegexOptions.IgnoreCase) ||
            err.Contains("global 'io'", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("global 'os'", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("global 'debug'", StringComparison.OrdinalIgnoreCase))
        {
            categories.Add("LuaSandboxRestriction");
            hints.Add("The K2 game engine embeds a sandboxed Lua environment where standard 'io', 'os', and 'debug' libraries are stripped. Use Echo(...) or IPC return values from 'kongor_game_lua' for script telemetry, or use agent file tools / PowerShell for host filesystem operations.");
            canAutoRetry = false;
        }

        // 1e. Colon Method Existence Guard Syntax Error (MCP-078)
        // In Lua 5.1, 'widget:Method and' is a syntax error because colon invocation requires an argument list.
        if (Regex.IsMatch(improved, @":([a-zA-Z0-9_]+)\s+and\b") ||
            (err.Contains("syntax error", StringComparison.OrdinalIgnoreCase) && improved.Contains(":GetNumChildren and", StringComparison.OrdinalIgnoreCase)))
        {
            improved = Regex.Replace(improved, @":([a-zA-Z0-9_]+)\s+and\b", ".$1 and");
            categories.Add("LuaMethodCheckSyntaxError");
            hints.Add("In Lua, checking method existence via colon syntax (e.g. 'widget:Method and') is a syntax error because colon requires argument parentheses. Use table dot access 'widget.Method and' to test method existence before calling.");
            canAutoRetry = true;
        }

        // 1f. CInterface Non-Existent GetRootWidget() Method (MCP-079)
        if (Regex.IsMatch(improved, @"[:\.]GetRootWidget\s*\(\s*\)", RegexOptions.IgnoreCase) ||
            err.Contains("attempt to call method 'GetRootWidget'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"[:\.]GetRootWidget\s*\(\s*\)\s*:\s*GetWidget", ":GetWidget", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"[:\.]GetRootWidget\s*\(\s*\)", ":GetWidget('main')", RegexOptions.IgnoreCase);
            categories.Add("NonExistentWidgetMethod");
            hints.Add("In K2 Lua, CInterface does not provide a 'GetRootWidget()' method. Query widgets directly via 'interface:GetWidget(name)' or 'UIManager.GetInterface('main'):GetWidget(name)'.");
            canAutoRetry = true;
        }

        // 1g. Nil Widget Dereference on GetNumChildren / UI methods (MCP-071, MCP-045)
        if (err.Contains("attempt to call method 'GetNumChildren' (a nil value)", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("attempt to call method 'GetWidget' on a nil value", StringComparison.OrdinalIgnoreCase))
        {
            categories.Add("NilWidgetDereference");
            hints.Add("The target widget or interface returned nil. In K2 Lua, GetWidget(name) returns nil if the widget does not exist in the active interface. Verify widget name using 'kongor_k2_ui_inspect' or wrap lookups with 'if widget ~= nil then ... end'.");
            canAutoRetry = false;
        }

        // 1h. practice_button Non-Existent Widget (MCP-002, MCP-071)
        if (improved.Contains("practice_button", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bpractice_button\b", "sysbar_quickStart", RegexOptions.IgnoreCase);
            categories.Add("WidgetNotFound");
            hints.Add("The widget 'practice_button' does not exist in the K2 system bar. The native quick practice button is 'sysbar_quickStart'. Alternatively, bypass UI clicking with 'kongor_match_launch' or console command 'StartGame practice PracticeGame map:caldavar allheroes:true'.");
            canAutoRetry = true;
        }

        // 1i. Unscoped Global GetWidget Call (MCP-002, MCP-071, MCP-079)
        if ((Regex.IsMatch(improved, @"(?<![\.:\w])GetWidget\s*\(") && !improved.Contains("UIManager.", StringComparison.OrdinalIgnoreCase) && !improved.Contains("interface:", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrEmpty(err) || channel.Equals("lua", StringComparison.OrdinalIgnoreCase) || err.Contains("global 'GetWidget'", StringComparison.OrdinalIgnoreCase))) ||
            err.Contains("attempt to call global 'GetWidget'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"(?<![\.:\w])GetWidget\s*\(", "UIManager.GetInterface('main'):GetWidget(");
            categories.Add("UnscopedGetWidget");
            hints.Add("In K2 engine Lua, GetWidget(...) is not a global CScript function. Target the widget via 'UIManager.GetInterface(\\'main\\'):GetWidget(...)', 'interface:GetWidget(...)', or within an XML script tag scope.");
            canAutoRetry = true;
        }

        // 1j. HoN_Region Global Access vs main_v2 / regions.lua Scoping
        if (improved.Contains("HoN_Region", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("global 'HoN_Region'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bHoN_Region\b", "UIManager.GetInterface('main')", RegexOptions.IgnoreCase);
            categories.Add("RegionScopeMismatch");
            hints.Add("In K2 UI, region selection state is scoped inside 'regions.lua' / 'main_v2' rather than a raw global 'HoN_Region'. Query region widgets via 'UIManager.GetInterface(\\'main\\'):GetWidget(\\'region_select_container\\')' or inspect 'main_v2'.");
            canAutoRetry = true;
        }

        // 1j. UICmd Quoting & Nested Quote Corruption
        if (Regex.IsMatch(improved, @"\bUICmd\s*\(\s*""[^""]*""[^""]*""\s*\)") ||
            (improved.Contains("UICmd(", StringComparison.OrdinalIgnoreCase) && err.Contains("unexpected symbol", StringComparison.OrdinalIgnoreCase)))
        {
            improved = Regex.Replace(improved, @"\binterface\s*:\s*UICmd", "Cmd", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"\bUICmd", "Cmd", RegexOptions.IgnoreCase);
            categories.Add("InvalidUICmdQuoting");
            hints.Add("In K2 Lua, use Cmd('...') instead of interface:UICmd('...') and alternate quotes (e.g. Cmd(\"Set dev true\")) to prevent string literal corruption.");
            canAutoRetry = true;
        }

        // 1k. Global GetGamePhase Nil Indexing (MCP-246)
        if (Regex.IsMatch(improved, @"\bGetGamePhase\s*\(\s*\)") ||
            err.Contains("attempt to call global 'GetGamePhase'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bGetGamePhase\s*\(\s*\)", "Host.GetGamePhase()");
            categories.Add("NilGlobalGetGamePhase");
            hints.Add("GetGamePhase() does not exist in the global CScript VM. Use 'Host.GetGamePhase()', 'GetCvarInt(\"game_phase\")' in Lua, or query 'kongor_game_client_manage(action: \\'status\\')' via MCP.");
            canAutoRetry = true;
        }

        // 1l. Testing.GetEntityStatus Returns Numeric Enum, Not Table (MCP-243)
        if (improved.Contains("Testing.GetEntityStatus", StringComparison.OrdinalIgnoreCase) &&
            ((err.Contains("attempt to index local", StringComparison.OrdinalIgnoreCase) && err.Contains("a number value", StringComparison.OrdinalIgnoreCase)) ||
             Regex.IsMatch(improved, @"Testing\.GetEntityStatus\([^\)]+\)\s*\.\s*(?:health|status|name|team)")))
        {
            categories.Add("ScalarEntityStatusIndexing");
            hints.Add("Testing.GetEntityStatus(entityIdx) returns a numeric enum (1=Active, 2=Corpse, 0=Invalid), not a Lua table. Compare the status code directly as a number or inspect Testing.GetEntityInfo(entityIdx) for structured fields.");
            canAutoRetry = false;
        }

        // 1m. Testing.SpawnUnit Localized Hero Name Mismatch (MCP-244)
        if (improved.Contains("Testing.SpawnUnit", StringComparison.OrdinalIgnoreCase) &&
            (err.Contains("Entity not found", StringComparison.OrdinalIgnoreCase) ||
             improved.Contains("Hero_Glacius", StringComparison.OrdinalIgnoreCase) ||
             improved.Contains("Hero_Pebbles", StringComparison.OrdinalIgnoreCase) ||
             improved.Contains("Hero_Swiftblade", StringComparison.OrdinalIgnoreCase)))
        {
            improved = Regex.Replace(improved, @"\bHero_Glacius\b", "Hero_Frosty", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"\bHero_Pebbles\b", "Hero_Rocky", RegexOptions.IgnoreCase);
            improved = Regex.Replace(improved, @"\bHero_Swiftblade\b", "Hero_Hiro", RegexOptions.IgnoreCase);
            categories.Add("InvalidHeroEntityName");
            hints.Add("K2 legacy heroes use internal engine entity codenames (e.g. Hero_Frosty instead of Hero_Glacius, Hero_Rocky instead of Hero_Pebbles). Always call Testing.Precache(entityName) before Testing.SpawnUnit.");
            canAutoRetry = true;
        }

        // 1n. pcall(loadstring( Spin-Wait Timeout in version.dll (MCP-231)
        if (improved.Contains("pcall(loadstring(", StringComparison.OrdinalIgnoreCase) ||
            (err.Contains("ExecuteCommand", StringComparison.OrdinalIgnoreCase) && err.Contains("timed out after 3000ms", StringComparison.OrdinalIgnoreCase) && improved.Contains("loadstring")))
        {
            categories.Add("PcallLoadstringSpinwait");
            hints.Add("Submitting 'pcall(loadstring(' via 0x0003 triggers a legacy 3,000ms native spin-wait in version.dll on unmapped KongorSharedMem. Use synchronous 0x000E (EvalLuaSync) or dispatch direct Lua statements without nested pcall(loadstring().");
            canAutoRetry = false;
        }

        // 1o. Fire-and-Forget Script ACK vs EvalLuaSync Return Value (MCP-136)
        if (err.Contains("{\"status\": \"error\", \"success\": false, \"response\": \"{\\\"success\\\":true", StringComparison.OrdinalIgnoreCase) ||
            (err.Contains("ExecuteCommand", StringComparison.OrdinalIgnoreCase) && improved.StartsWith("Script ", StringComparison.OrdinalIgnoreCase)))
        {
            categories.Add("AsyncPipeAckMismatch");
            hints.Add("Fire-and-forget Script '...' dispatches return an immediate submission ACK JSON instead of synchronous evaluation return value. Use kongor_client_eval_lua with action: 'eval' (0x000E EvalLuaSync) for synchronous 1-frame return values.");
            canAutoRetry = false;
        }

        // 2. DoEvent with String Argument (e.g. DoEvent('onclick'))
        if (Regex.IsMatch(improved, @":DoEvent\s*\(\s*['""][^'""]+['""]\s*\)", RegexOptions.IgnoreCase) ||
            err.Contains("number expected, got string", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("bad argument #1 to 'DoEvent'", StringComparison.OrdinalIgnoreCase))
        {
            improved = Regex.Replace(improved, @":DoEvent\s*\(\s*['""][^'""]+['""]\s*\)", ":DoEvent()", RegexOptions.IgnoreCase);
            categories.Add("InvalidEventSignature");
            hints.Add("In K2, widget:DoEvent() takes no parameters (triggers the XML 'onevent' script) or widget:DoEventN(index) takes an integer (triggers 'onevent0', 'onevent1'). To trigger an action callback by name, use 'Call <widget> <event>' or 'SetCallback'.");
            canAutoRetry = true;
        }

        // 4. Malformed StartGame Grammar (e.g. StartGame practice caldavar)
        if (improved.StartsWith("StartGame", StringComparison.OrdinalIgnoreCase) &&
            !improved.Contains("map:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = improved.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string mode = parts.Length > 1 ? parts[1] : "practice";
            string map = parts.Length > 2 && !parts[2].Contains(':') ? parts[2] : "caldavar";
            improved = $"StartGame {mode} PracticeGame map:{map} allheroes:true";
            categories.Add("MalformedCommandArguments");
            hints.Add("StartGame syntax requires session/game name before map options: 'StartGame <mode> <gameName> map:<mapName> allheroes:true'. Missing the session name causes K2 to interpret the map name as the game title.");
            canAutoRetry = true;
        }

        // 4b. StartGame practice lacking offline svr_login false guard on login/auth timeout
        if ((err.Contains("login", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("masterserver", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("auth", StringComparison.OrdinalIgnoreCase)) &&
            improved.Contains("StartGame practice", StringComparison.OrdinalIgnoreCase) &&
            !improved.Contains("svr_login false", StringComparison.OrdinalIgnoreCase))
        {
            improved = "Set svr_login false; Set svr_broadcastLocal false; Set ui_quickStart true; Set sv_precacheEntities false; " + improved;
            categories.Add("PracticeLoginHang");
            hints.Add("Practice match commands must disable master server login ('Set svr_login false; Set svr_broadcastLocal false; Set ui_quickStart true;') to prevent a 30-second remote authentication timeout and unhandled host server teardown.");
            canAutoRetry = true;
        }

        // 4c. Malformed CreateGame Grammar on dedicated server (e.g. CreateGame caldavar)
        if (improved.StartsWith("CreateGame", StringComparison.OrdinalIgnoreCase) &&
            !improved.Contains("map:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = improved.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string world = parts.Length > 1 ? parts[1] : "caldavar";
            improved = $"CreateGame {world} map:{world} allheroes:true";
            categories.Add("MalformedCommandArguments");
            hints.Add("CreateGame syntax requires map options: 'CreateGame <world> map:<world> allheroes:true'. Missing 'map:<world>' causes CGameServer::LoadWorld to fail.");
            canAutoRetry = true;
        }

        // 4d. StartGame dispatched to dedicated server (where CreateGame is required)
        if (target != null && target.StartsWith("server", StringComparison.OrdinalIgnoreCase) &&
            improved.StartsWith("StartGame", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = improved.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string map = "caldavar";
            foreach (string part in parts)
            {
                if (part.StartsWith("map:", StringComparison.OrdinalIgnoreCase))
                {
                    map = part.Substring("map:".Length);
                }
                else if (!part.Equals("StartGame", StringComparison.OrdinalIgnoreCase) &&
                         !part.Equals("practice", StringComparison.OrdinalIgnoreCase) &&
                         !part.Contains(':') &&
                         !part.Equals("PracticeGame", StringComparison.OrdinalIgnoreCase) &&
                         !part.Equals("GameName", StringComparison.OrdinalIgnoreCase))
                {
                    map = part;
                }
            }
            improved = $"CreateGame {map} map:{map} allheroes:true";
            categories.Add("DedicatedServerCreateGameRequired");
            hints.Add("Dedicated servers (CHostServer) load matches via 'CreateGame <world> map:<world> allheroes:true' instead of 'StartGame'. 'StartGame' on dedicated server attempts manager slave process spawning.");
            canAutoRetry = true;
        }

        // 5. Malformed ReloadInterface / ReloadInterfaces
        if (Regex.IsMatch(improved, @"\bReloadInterfaces\b", RegexOptions.IgnoreCase) ||
            improved.Trim().Equals("ReloadInterface", StringComparison.OrdinalIgnoreCase))
        {
            improved = "Set ui_reloadInterfaces true";
            categories.Add("MalformedCommandArguments");
            hints.Add("In the K2 engine, triggering interface reloads via 'Set ui_reloadInterfaces true' defers execution safely to the frame boundary in CUIManager::Frame, preventing session desync, HUD drops, and tear-down crashes.");
            canAutoRetry = true;
        }

        // 6. Missing 'Script' or 'Cmd' Prefix on Raw Lua
        if (err.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) &&
            (improved.StartsWith("GetWidget", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("UIManager", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("println", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("echo", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("local ", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("function", StringComparison.OrdinalIgnoreCase) ||
             improved.StartsWith("Trigger(", StringComparison.OrdinalIgnoreCase)))
        {
            improved = $"Script {improved}";
            categories.Add("MissingScriptPrefix");
            hints.Add("In the K2 in-game console, raw Lua code must be prefixed with 'Script ' (e.g. 'Script println(\"hello\")'). The console engine treated the statement as a native CVar/Command.");
            canAutoRetry = true;
        }

        // 6. Read-Only CVar
        if (err.Contains("is read only", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("read-only", StringComparison.OrdinalIgnoreCase))
        {
            improved = $"Set dev true; {improved}";
            categories.Add("ReadOnlyCvar");
            hints.Add("This CVar is protected or read-only during active matches. Enable developer mode first via 'Set dev true; Set cheats true' or specify on command-line startup.");
            canAutoRetry = true;
        }

        // 7. Store Container Mismatch (cg_store2_ enables store_container2 in modern client builds)
        if (Regex.IsMatch(improved, @"\bstore_container\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(improved, @"\bstore_container2\b", RegexOptions.IgnoreCase))
        {
            improved = Regex.Replace(improved, @"\bstore_container\b", "store_container2", RegexOptions.IgnoreCase);
            categories.Add("StoreContainerMismatch");
            hints.Add("In modern K2 client builds with 'cg_store2_' enabled, the store panel container is 'store_container2' rather than legacy 'store_container'. Target 'store_container2' or evaluate dynamically via GetCvarBool('cg_store2_').");
            canAutoRetry = true;
        }

        if (categories.Count > 0)
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = categories[0],
                RemediationHint = string.Join(" ", hints),
                ImprovedCommand = improved != cmd ? improved : null,
                CanAutoRetry = canAutoRetry
            };
        }

        // 7a. Dedicated Server Worker Offline / Docker Container Target (MCP-088, MCP-004)
        if (target != null && target.StartsWith("server", StringComparison.OrdinalIgnoreCase) &&
            (err.Contains("No running dedicated game server", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("target_offline", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("Failed to execute command on server port", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("Timed out", StringComparison.OrdinalIgnoreCase) ||
             channel == "none"))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "ServerOffline",
                RemediationHint = "The target dedicated game server worker is offline on the host Win32 named pipe. Launch a host worker via 'kongor_game_server_manage(action=\"start\")', or if targeting Docker-hosted workers (ports 11335..11336), dispatch commands via 'docker exec compel-dedicated-server ...' or COMPEL IPC.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 7b. Client Process Offline / No Channel Available (MCP-003, MCP-210)
        if (channel == "none" ||
            err.Contains("not currently running", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Target window 'hon_x64' not found", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("is not reachable", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "ClientOffline",
                RemediationHint = "The player game client (hon_x64.exe) is not running or has no active window handle. Launch it first with 'kongor_game_client_manage action:launch' or direct map loader 'kongor_match_launch'.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 8. Lua Syntax Errors
        if (err.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("unexpected symbol", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("unfinished string", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "LuaSyntaxError",
                RemediationHint = "The K2 Lua compiler encountered a syntax error in the provided script. Verify that strings are properly terminated, brackets are balanced, and statements use valid Lua 5.1 grammar.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 9. Unknown Console Command
        if (err.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("not a valid cvar or command", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientCommandRemediation
            {
                ErrorCategory = "UnknownCommand",
                RemediationHint = $"The K2 engine does not recognize '{cmd}' as a valid command or CVar. Use 'CmdList' or 'CvarList' in the console to inspect available symbols, or use 'kongor_game_lua' for script execution.",
                ImprovedCommand = null,
                CanAutoRetry = false
            };
        }

        // 10. Default / Unclassified Error
        return new ClientCommandRemediation
        {
            ErrorCategory = "ExecutionError",
            RemediationHint = $"Command failed in channel '{channel}': {err}. Verify client engine state, console log, and active match phase.",
            ImprovedCommand = null,
            CanAutoRetry = false
        };
    }

    private async Task ProcessDiskQueueAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                List<string> lines = [];
                while (_channel.Reader.TryRead(out ClientCommandErrorRecord? item))
                {
                    lines.Add(JsonSerializer.Serialize(item, JsonOpts));
                }

                if (lines.Count > 0)
                {
                    try
                    {
                        await File.AppendAllLinesAsync(_logFilePath, lines, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] Disk persistence skipped: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Add(ref _pendingWrites, -lines.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] Queue shutdown cancelled cleanly: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] Queue processor stopped: {ex.Message}");
        }
    }

    public async Task FlushAsync()
    {
        while (Volatile.Read(ref _pendingWrites) > 0)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wave 4 (#230): Generates a deterministic, reversible 128-bit correlation ID from a monotonic sequence
    /// using <see cref="Kongor.Cli.Mcp.Handlers.Reversible128BitIdPermutator"/>.
    /// </summary>
    public static UInt128 CreateDeterministicCorrelationId(
        ulong sequence,
        Kongor.Cli.Mcp.Handlers.IdOrder order = Kongor.Cli.Mcp.Handlers.IdOrder.Permuted,
        ulong seed = 0xAA55_AA55_55AA_55AAUL)
    {
        Kongor.Cli.Mcp.Handlers.Reversible128BitIdPermutator permutator =
            Kongor.Cli.Mcp.Handlers.Reversible128BitIdPermutator.FromSeed(order, seed);
        return permutator.Encode((UInt128)sequence);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _processQueueTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientCommandRemediationService] DisposeAsync drain warning: {ex.Message}");
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public void Dispose()
    {
#pragma warning disable RS0030 // Justification: Synchronous IDisposable bridge to DisposeAsync
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore RS0030
    }
}

