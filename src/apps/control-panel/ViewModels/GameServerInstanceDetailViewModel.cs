// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class GameServerInstanceDetailViewModel : ObservableObject
{
    [GeneratedRegex(@"\^\d{3}|\^[a-zA-Z*]", RegexOptions.Compiled)]
    private static partial Regex K2ColorRegex();

    [GeneratedRegex(@"^(?:\[HON_PING_SHIM\]\s*|\[(?:[A-Za-z]{3}\s+\d{1,2}\s+)?\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\]\s*|(?:\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?\s*)|>?(?:Error|Warning|Sv|Server|Cl|Client|Resource|Net|Dev|Sys|VoiceServer|Chat|Master|HTTP):\s*)*", RegexOptions.IgnoreCase)]
    private static partial Regex LogPrefixRegex();

    private readonly HashSet<string> _seenMessageSignatures = [];
    private readonly List<string> _rawLogHistory = [];

    [ObservableProperty] private string _hostAddress = "127.0.0.1";
    [ObservableProperty] private int _controlPort = 11234;
    [ObservableProperty] private string _authToken = string.Empty;

    [ObservableProperty] private string _hostingEnvironment = "Local Windows Process";
    [ObservableProperty] private string _hostingBadgeText = "💻 LOCAL";
    [ObservableProperty] private string _hostingBadgeColor = "#10B981";
    [ObservableProperty] private string _hostingBadgeBackground = "#064E3B";
    [ObservableProperty] private bool _isContainer;
    [ObservableProperty] private bool _isLocal = true;

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private int _gamePort;
    [ObservableProperty] private int _workerPort;
    [ObservableProperty] private int _instanceNumber;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanServerName))]
    private string _instanceTag = "#1";
    [ObservableProperty] private string _tierCode = "L";

    partial void OnInstanceNumberChanged(int value)
    {
        if (string.IsNullOrEmpty(InstanceTag) || InstanceTag == "#0" || InstanceTag == "#1")
        {
            InstanceTag = $"#{TierCode}{value}";
        }
    }
    [ObservableProperty] private string _protocolMode = "Direct UDP";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanServerName))]
    private string _serverName = string.Empty;
    [ObservableProperty] private string _voiceEndpoint = string.Empty;
    [ObservableProperty] private int _voicePort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusShortText))]
    private string _serverStatus = "Available";
    [ObservableProperty] private string _statusDotColor = "#10B981";
    [ObservableProperty] private string _statusBadgeBackground = "#064E3B";
    [ObservableProperty] private string _statusColor = "#10B981";
    [ObservableProperty] private string _statusText = "Available (Ready)";
    [ObservableProperty] private string _statusIcon = "🟢";

    public string StatusShortText => ServerStatus switch
    {
        "Available" => "Ready",
        "Starting" => "Starting",
        "Restarting" => "Restarting",
        "Stopped" => "Stopped",
        "Crashed" => "Crashed",
        _ => ServerStatus
    };

    public string CleanServerName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ServerName)) return string.Empty;
            string name = ServerName.Trim();
            if (!string.IsNullOrWhiteSpace(InstanceTag) && name.EndsWith(InstanceTag, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^InstanceTag.Length].TrimEnd();
            }
            string tagWithoutHash = !string.IsNullOrWhiteSpace(InstanceTag) && InstanceTag.StartsWith('#')
                ? InstanceTag[1..]
                : string.Empty;
            if (!string.IsNullOrEmpty(tagWithoutHash) && name.EndsWith(tagWithoutHash, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^tagWithoutHash.Length].TrimEnd('#', ' ', '-');
            }
            return name;
        }
    }

    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private string _lastMessageSnippet = "No messages yet.";
    [ObservableProperty] private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorDisplay))]
    private bool _hasErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorDisplay))]
    private int _errorCount;

    public string ErrorDisplay => ErrorCount > 0 ? $"⚠️ {ErrorCount} Error{(ErrorCount > 1 ? "s" : "")}" : string.Empty;

    [ObservableProperty] private int? _processId;
    [ObservableProperty] private bool _desiredRunning = true;
    [ObservableProperty] private bool _isPermanentlyStopped;
    [ObservableProperty] private bool _isTabClosed;

    // --- Additive Live IPC Pipe Status (R5/R6 compatible with COMPEL.GUI) ---
    [ObservableProperty] private bool? _reportedIpcConnected;
    [ObservableProperty] private string? _reportedIpcPath;
    [ObservableProperty] private string? _reportedIpcTransport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeBadgeText))]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeToolTip))]
    private bool _isPipeConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeBadgeText))]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeToolTip))]
    private string _pipePathDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeBadgeText))]
    [NotifyPropertyChangedFor(nameof(ConnectedPipeToolTip))]
    private string _pipeTransportDisplay = string.Empty;

    public string ConnectedPipeBadgeText => !IsPipeConnected
        ? string.Empty
        : (!string.IsNullOrWhiteSpace(PipeTransportDisplay)
            ? $"Pipe Connected ({PipeTransportDisplay})"
            : "Pipe Connected");

    public string ConnectedPipeToolTip => !IsPipeConnected
        ? string.Empty
        : (!string.IsNullOrWhiteSpace(PipePathDisplay)
            ? $"{PipeTransportDisplay} • {PipePathDisplay}"
            : "Connected to Dedicated Server IPC");

    public void UpdateIpcPipeState(bool? reportedConnected, string? reportedPath, string? reportedTransport)
    {
        ReportedIpcConnected = reportedConnected;
        ReportedIpcPath = reportedPath;
        ReportedIpcTransport = reportedTransport;

        if (!IsRunning || reportedConnected == false)
        {
            IsPipeConnected = false;
            PipePathDisplay = string.Empty;
            PipeTransportDisplay = string.Empty;
            return;
        }

        int canonicalPort = WorkerPort > 0 ? WorkerPort : GamePort;
        if (reportedConnected == true && !string.IsNullOrWhiteSpace(reportedPath))
        {
            IsPipeConnected = true;
            PipePathDisplay = reportedPath;
            PipeTransportDisplay = !string.IsNullOrWhiteSpace(reportedTransport)
                ? reportedTransport!
                : (IsContainer ? "UnixSocket" : "NamedPipe");
            return;
        }

        // R6 zero-rebuild fallback: running container/local COMPEL worker on older schema without ipcConnected
        IsPipeConnected = true;
        PipeTransportDisplay = IsContainer ? "Container IPC" : "COMPEL IPC";
        PipePathDisplay = IsContainer
            ? $"/tmp/hon-server-native-{canonicalPort}.sock (Docker COMPEL)"
            : $@"\\.\pipe\hon-server-{canonicalPort}";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBackground))]
    [NotifyPropertyChangedFor(nameof(CardBorderBrush))]
    [NotifyPropertyChangedFor(nameof(CardBorderThickness))]
    private bool _isSelected;

    public string CardBackground => IsSelected ? "#1E293B" : "#0F172A";
    public string CardBorderBrush => IsSelected ? "#00F0FF" : "#1E293B";
    public string CardBorderThickness => IsSelected ? "3,1,1,1" : "1";

    [ObservableProperty] private ObservableCollection<GameServerChatMessageViewModel> _chatMessages = [];

    public void UpdateStatus(string status, bool isRunning)
    {
        IsRunning = isRunning;
        ServerStatus = status;

        if (string.Equals(status, "STARTING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "RESTARTING", StringComparison.OrdinalIgnoreCase))
        {
            ResetBootState(clearErrors: true);
        }
        else if (!isRunning || string.Equals(status, "CRASHED", StringComparison.OrdinalIgnoreCase))
        {
            ResetBootState(clearErrors: false);
        }

        string normalized = status.ToUpperInvariant();

        if (normalized is "CRASHED" or "FAULTED" or "FAILED")
        {
            StatusIcon = "⚠️";
            StatusText = "Crashed (Faulted)";
            StatusDotColor = "#EF4444";
            StatusColor = "#EF4444";
            StatusBadgeBackground = "#450A0A";
            return;
        }

        if (!isRunning)
        {
            StatusIcon = "🛑";
            StatusText = "Stopped / Inactive";
            StatusDotColor = "#64748B";
            StatusColor = "#94A3B8";
            StatusBadgeBackground = "#1E293B";
            return;
        }

        switch (normalized)
        {
            case "INMATCH":
            case "INGAME":
            case "MATCH":
                _isBootCompleted = true;
                _currentBootPhase = 5;
                StatusIcon = "⚔️";
                StatusText = "In Match (Active)";
                StatusDotColor = "#00F0FF";
                StatusColor = "#00F0FF";
                StatusBadgeBackground = "#083344";
                break;
            case "STARTING":
                StatusIcon = "🟡";
                StatusText = "Starting Up...";
                StatusDotColor = "#F59E0B";
                StatusColor = "#F59E0B";
                StatusBadgeBackground = "#451A03";
                break;
            case "RESTARTING":
            case "RECOVERING":
                StatusIcon = "🟡";
                StatusText = "Restarting...";
                StatusDotColor = "#F59E0B";
                StatusColor = "#F59E0B";
                StatusBadgeBackground = "#451A03";
                break;
            case "AVAILABLE":
            case "READY":
            default:
                _isBootCompleted = true;
                _currentBootPhase = 5;
                if (LastMessageSnippet == "No messages yet." || LastMessageSnippet.StartsWith("Starting"))
                {
                    LastMessageSnippet = "Server Ready";
                }
                StatusIcon = "🟢";
                StatusText = "Available (Ready)";
                StatusDotColor = "#10B981";
                StatusColor = "#10B981";
                StatusBadgeBackground = "#064E3B";
                break;
        }
    }

    private readonly List<(string Command, GameServerChatMessageViewModel Msg, DateTimeOffset Time)> _pendingOperatorMessages = [];

    public void AddOperatorMessage(string command, string channel = "stdin")
    {
        var msg = GameServerChatMessageViewModel.CreateOperatorMessage(command, channel);
        ChatMessages.Add(msg);
        _pendingOperatorMessages.Add((command.Trim(), msg, TimeProvider.System.GetUtcNow()));
        TrimMessagesIfExceeded(500);
        LastMessageSnippet = $"> {command}";
    }

    private bool _isBootCompleted;
    private int _bootNoiseLineCount;
    private int _currentBootPhase;
    private bool _hasConnectedToChatServer;

    public void ResetBootState(bool clearErrors = false)
    {
        _isBootCompleted = false;
        _bootNoiseLineCount = 0;
        _currentBootPhase = 0;
        _hasConnectedToChatServer = false;
        if (clearErrors)
        {
            HasErrors = false;
            ErrorCount = 0;
        }
    }

    public void AddSystemMessage(string text)
    {
        var msg = GameServerChatMessageViewModel.CreateSystemMessage(text);
        ChatMessages.Add(msg);
        TrimMessagesIfExceeded(500);
        LastMessageSnippet = text;
    }

    public static string StripLogPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return LogPrefixRegex().Replace(text.Trim(), string.Empty).Trim();
    }

    public static bool IsHarmlessStartupWarning(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string stripped = StripLogPrefix(text);

        // Explicit fatal errors must never be treated as harmless
        if (text.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WSAE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bind() failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bind failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Couldn't open port", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Segmentation fault", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SIGSEGV", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Assertion failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to initialize server", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Contains("[HON_PING_SHIM]", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains("FATAL", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("CRASH", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("INTERCEPTED INVALID CPacket::Write", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return stripped.Contains("application binding with cpu core", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("read-only value", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("read - only value", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("is a read-only value", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("is a read - only value", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("CFileManager", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Invalid file", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Invalid effect", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Cannot find", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Unknown element", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Hit end of file in ReadInt16", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("UpdateQuestsRequest", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("Failed to get a cookie", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("World var not found", StringComparison.OrdinalIgnoreCase) ||
               stripped.Contains("cg_worldAmbientSound", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("UpdateQuestsRequest", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Failed to get a cookie", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CFileManager", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Invalid file", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Invalid effect", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRealError(string text, bool isMarkedError = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 1. Benign/whitelisted startup notices are not treated as errors
        if (IsHarmlessStartupWarning(text)) return false;

        // 2. Explicitly whitelist benign [HON_PING_SHIM] stderr output so it never triggers red error cards
        if (text.Contains("[HON_PING_SHIM]", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("FATAL", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("CRASH", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("INTERCEPTED INVALID CPacket::Write", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3. Explicitly flagged as error by the DTO/channel
        if (isMarkedError) return true;

        // 4. Raw text checks (before prefix removal)
        string rawTrimmed = text.Trim();
        if (rawTrimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("Fatal Error", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("FATAL:", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("[Fatal]", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("CRASH:", StringComparison.OrdinalIgnoreCase) ||
            rawTrimmed.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. Stripped message checks
        string stripped = StripLogPrefix(text);
        if (stripped.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("Fatal Error", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("FATAL:", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("::Error:", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains(" Error: ", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Error reading", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Socket error", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Connection error", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Assertion failed", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Assert Failed", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Lua Error", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Unhandled Exception", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Crash Dump", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("WSAEADDRINUSE", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("bind failed", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("failed to bind", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Initialization Failed", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Failed to initialize", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Failed to load", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("corrupted", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("bad allocation", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("SIGSEGV", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Segmentation fault", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("attempt to call a nil value", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("attempt to index a nil value", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Access Violation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsStartupNoise(string text, bool isMarkedError = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // A real error can NEVER be classified as startup noise
        if (IsRealError(text, isMarkedError)) return false;

        // Benign/whitelisted startup warnings are always treated as startup noise
        if (IsHarmlessStartupWarning(text)) return true;

        string trimmed = StripLogPrefix(text);
        if (trimmed.Length == 0) return true;

        // Never filter out in-game chat, operator commands, acknowledgments, or engine cvars
        if (text.Contains("[ALL]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[TEAM]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ServerChat", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FakePlayer", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AddFakePlayer", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Echo", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("svr_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("host_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("con_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("sys_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("net_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("g_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("bot_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("sv_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("man_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Asterisk banners or separator lines
        if (trimmed.All(c => c == '*' || c == '-' || c == '=' || char.IsWhiteSpace(c)))
            return true;

        // HTTP chunk length hex indicators (e.g. "187", "0")
        if (trimmed.All(Uri.IsHexDigit))
            return true;

        if (trimmed.StartsWith("Executing \"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("K2 Engine start up", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("[UNICODE]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Build date:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Modded files are in use", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Loading StringTable", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("NetDriver Initialization", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Network Initialization", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Local IP #", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Host is:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("NIC #", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Using Windows Audio", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Using driver", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FMOD found", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Loaded FMOD", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Speaker mode set", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Loading ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Finished loading", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Host:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith("a:", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(":{")) ||
            trimmed.StartsWith("Created CSocket", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("[VoiceManager]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Voice quality settings", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("VoiceServer:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Initializing VoiceServer", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Sample Rate[", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("BitNum Per Sample[", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Record Quality[", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FFmpeg libraries successfully", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Registering cvar", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MemManager", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Vid_Init", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("GL_Init", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Direct3D", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Preloading", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Archive:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Mounted:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Dev: libxml2", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("StateStringChanged", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("CGameServer::StateStringChanged", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("SoundManager", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("CFileManager", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Mounting", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("CWorld", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("quadtree", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("libxml2", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("interface XML", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Loading interface", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("application binding with cpu core", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("handshake to chat server", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Sent heartbeat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Connected to chat server", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("game server status update", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Remote Configuration", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("is a read - only value", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("is a read-only value", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Invalid file type", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Invalid effect", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Cannot find clip", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Cannot find model", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Cannot find effect", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Requesting new session cookie", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Session cookie request failed", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Long server frame", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Server skipped", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Sleeping 15 seconds", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("[HON_PING_SHIM]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[HON_PING_SHIM]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsBootCompletionSignal(string text, bool hasConnectedToChatServer = false)
    {
        string stripped = StripLogPrefix(text);
        if (stripped.Contains("Remote Configuration Was Injected Successfully", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Entering Reconcile Loop", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Server is now open for connections", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Listening on port", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Started game server", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Game started", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Loaded map:", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Ready for connections", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Dedicated server initialized", StringComparison.OrdinalIgnoreCase) ||
            (hasConnectedToChatServer && stripped.Contains("StateStringChanged", StringComparison.OrdinalIgnoreCase)) ||
            stripped.Contains("Requesting new session cookie", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("Session cookie request failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public void IngestLogs(IEnumerable<string> logLines)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        IngestLogs(logLines.Select(line => new CompelDedicatedServerLogLineDto
        {
            Text = line,
            Timestamp = now,
            Source = "server",
            IsError = false
        }));
    }

    public void IngestLogs(IEnumerable<CompelDedicatedServerLogLineDto> incomingLogs)
    {
        bool addedAny = false;
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        _pendingOperatorMessages.RemoveAll(p => (now - p.Time).TotalSeconds > 30);

        if (_rawLogHistory.Count > 0 &&
            IsRunning &&
            (ServerStatus.Equals("InMatch", StringComparison.OrdinalIgnoreCase) ||
             ServerStatus.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            _isBootCompleted = true;
        }

        foreach (var log in incomingLogs)
        {
            _rawLogHistory.Add(log.Text);
            if (_rawLogHistory.Count > 3000)
            {
                _rawLogHistory.RemoveAt(0);
            }

            string cleanText = K2ColorRegex().Replace(log.Text, string.Empty);
            if (cleanText.StartsWith('>') && !cleanText.StartsWith("> "))
            {
                cleanText = cleanText[1..].TrimStart();
            }

            string trimmedClean = cleanText.Trim();
            if (trimmedClean.Length <= 4 && (trimmedClean.Equals("ype", StringComparison.OrdinalIgnoreCase) ||
                                             trimmedClean.Equals("ype]", StringComparison.OrdinalIgnoreCase) ||
                                             trimmedClean.Equals("fil", StringComparison.OrdinalIgnoreCase) ||
                                             trimmedClean.Equals("Error:", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (trimmedClean.EndsWith("Erro", StringComparison.OrdinalIgnoreCase) ||
                trimmedClean.EndsWith(".effect'", StringComparison.OrdinalIgnoreCase) ||
                trimmedClean.EndsWith(".clip'", StringComparison.OrdinalIgnoreCase) ||
                trimmedClean.EndsWith(".mdf'", StringComparison.OrdinalIgnoreCase) ||
                trimmedClean.EndsWith(".model'", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string sig = $"{log.Timestamp.ToUnixTimeMilliseconds()}_{log.Source}_{cleanText}";
            if (_seenMessageSignatures.Add(sig))
            {
                if (cleanText.Contains("stopped", StringComparison.OrdinalIgnoreCase) &&
                    cleanText.Contains("Server on Port", StringComparison.OrdinalIgnoreCase))
                {
                    ResetBootState();
                    if (!ServerStatus.Equals("Crashed", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateStatus("Stopped", isRunning: false);
                    }
                }

                if (cleanText.Contains("bind() failed", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.Contains("Couldn't open port", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.Contains("Failed to initialize server", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.Contains("WSAEADDRINUSE", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus("Crashed", isRunning: false);
                }

                if (cleanText.Contains("Connected to chat server", StringComparison.OrdinalIgnoreCase))
                {
                    _hasConnectedToChatServer = true;
                }

                bool isRealErr = IsRealError(cleanText, log.IsError);

                if (string.Equals(log.Source, "operator", StringComparison.OrdinalIgnoreCase) ||
                    cleanText.StartsWith("> ", StringComparison.Ordinal))
                {
                    string cmd = cleanText.StartsWith("> ") ? cleanText[2..].Trim() : cleanText.Trim();

                    int pendingIdx = _pendingOperatorMessages.FindIndex(p => string.Equals(p.Command, cmd, StringComparison.OrdinalIgnoreCase));
                    if (pendingIdx >= 0)
                    {
                        var pending = _pendingOperatorMessages[pendingIdx];
                        _pendingOperatorMessages.RemoveAt(pendingIdx);
                        pending.Msg.DeliveryChannel = "stdin (confirmed)";
                        LastMessageSnippet = $"> {cmd}";
                        if (!IsRunning || ServerStatus is "Stopped" or "Starting")
                        {
                            UpdateStatus("Available", isRunning: true);
                        }
                        continue;
                    }

                    ChatMessages.Add(GameServerChatMessageViewModel.CreateOperatorMessage(cmd, "stdin"));
                }
                else if (string.Equals(log.Source, "system", StringComparison.OrdinalIgnoreCase))
                {
                    ChatMessages.Add(GameServerChatMessageViewModel.CreateSystemMessage(cleanText, log.Timestamp));
                }
                else if (string.Equals(log.Source, "chat", StringComparison.OrdinalIgnoreCase) ||
                         cleanText.Contains("[ALL]", StringComparison.OrdinalIgnoreCase) ||
                         cleanText.Contains("[TEAM]", StringComparison.OrdinalIgnoreCase))
                {
                    bool isAll = !cleanText.Contains("[TEAM]", StringComparison.OrdinalIgnoreCase);
                    string sender = isAll ? "[ALL] Player" : "[TEAM] Player";
                    string chatBody = cleanText;

                    int tagIdx = cleanText.IndexOf(isAll ? "[ALL]" : "[TEAM]", StringComparison.OrdinalIgnoreCase);
                    if (tagIdx >= 0)
                    {
                        string afterTag = cleanText[(tagIdx + (isAll ? 5 : 6))..].Trim();
                        int colon = afterTag.IndexOf(':');
                        if (colon > 0)
                        {
                            sender = $"{(isAll ? "[ALL]" : "[TEAM]")} {afterTag[..colon].Trim()}";
                            chatBody = afterTag[(colon + 1)..].Trim();
                        }
                    }

                    ChatMessages.Add(GameServerChatMessageViewModel.CreatePlayerChat(sender, chatBody, isAll, log.Timestamp));
                    LastMessageSnippet = $"{sender}: {chatBody}";
                    addedAny = true;
                    continue;
                }
                else if (string.Equals(log.Source, "command-ack", StringComparison.OrdinalIgnoreCase) ||
                         cleanText.StartsWith("[Server]", StringComparison.OrdinalIgnoreCase))
                {
                    ChatMessages.Add(GameServerChatMessageViewModel.CreateServerLog(cleanText, isRealErr, log.Timestamp));
                    LastMessageSnippet = cleanText;
                    addedAny = true;
                    continue;
                }
                else
                {
                    if (isRealErr)
                    {
                        HasErrors = true;
                        ErrorCount++;
                    }

                    if (!IsRunning && (cleanText.StartsWith('>') || cleanText.Equals("ping", StringComparison.OrdinalIgnoreCase) || cleanText.Contains("svr_name is", StringComparison.OrdinalIgnoreCase)))
                    {
                        UpdateStatus("Available", isRunning: true);
                    }

                    // Filter noisy K2 startup logs and replace with progressive humorous milestones
                    if (!_isBootCompleted || !IsRunning)
                    {
                        if (IsBootCompletionSignal(cleanText, _hasConnectedToChatServer))
                        {
                            if (_currentBootPhase < 1) AddSystemMessage("🌀 Combobulating Legion & Hellbourne Leylines...");
                            if (_currentBootPhase < 2) AddSystemMessage("⚔️ Sharpening Kongor's cleaver & waking the Shopkeeper...");
                            if (_currentBootPhase < 3) AddSystemMessage("🔮 Calibrating Caldavar mana streams & rune spawns...");
                            if (_currentBootPhase < 4) AddSystemMessage("🍌 Stocking Kongor's stash with bananas & wards...");
                            _currentBootPhase = 5;
                            _isBootCompleted = true;
                            UpdateStatus("Available", isRunning: true);

                            if (HasErrors)
                            {
                                AddSystemMessage($"⚠️ Dedicated Game Server initialized with {ErrorCount} warning(s)/error(s). Review logs above.");
                                LastMessageSnippet = $"Ready ({ErrorCount} err)";
                            }
                            else
                            {
                                AddSystemMessage("✨ Dedicated Game Server initialized and ready for battle!");
                                LastMessageSnippet = "Server Ready";
                            }
                            addedAny = true;
                            continue;
                        }

                        if (IsStartupNoise(cleanText, log.IsError))
                        {
                            _bootNoiseLineCount++;

                            if (_currentBootPhase == 0)
                            {
                                _currentBootPhase = 1;
                                AddSystemMessage("🌀 Combobulating Legion & Hellbourne Leylines...");
                                addedAny = true;
                            }
                            else if (_currentBootPhase == 1 && _bootNoiseLineCount >= 2)
                            {
                                _currentBootPhase = 2;
                                AddSystemMessage("⚔️ Sharpening Kongor's cleaver & waking the Shopkeeper...");
                                addedAny = true;
                            }
                            else if (_currentBootPhase == 2 && _bootNoiseLineCount >= 4)
                            {
                                _currentBootPhase = 3;
                                AddSystemMessage("🔮 Calibrating Caldavar mana streams & rune spawns...");
                                addedAny = true;
                            }
                            else if (_currentBootPhase == 3 && _bootNoiseLineCount >= 6)
                            {
                                _currentBootPhase = 4;
                                AddSystemMessage("🍌 Stocking Kongor's stash with bananas & wards...");
                                addedAny = true;
                            }

                            continue;
                        }
                    }
                    else
                    {
                        // Post-boot: Filter out repetitive internal engine noise (heartbeats, StateStringChanged, status pings)
                        if (IsStartupNoise(cleanText, log.IsError))
                        {
                            continue;
                        }
                    }

                    ChatMessages.Add(GameServerChatMessageViewModel.CreateServerLog(cleanText, isRealErr, log.Timestamp));
                }

                LastMessageSnippet = isRealErr ? $"⚠️ {StripLogPrefix(cleanText)}" : cleanText;
                addedAny = true;
            }
        }

        if (addedAny)
        {
            TrimMessagesIfExceeded(500);
        }
    }

    public string GetLogsAsPlainText()
    {
        if (_rawLogHistory.Count > 0)
        {
            return string.Join(Environment.NewLine, _rawLogHistory);
        }

        StringBuilder sb = new();
        foreach (var msg in ChatMessages)
        {
            sb.AppendLine($"[{msg.FormattedTime}] [{msg.Sender}] {msg.Message}");
        }
        return sb.ToString();
    }

    public void ClearChat()
    {
        ChatMessages.Clear();
        _seenMessageSignatures.Clear();
        _pendingOperatorMessages.Clear();
        _rawLogHistory.Clear();
        ResetBootState(clearErrors: true);
        LastMessageSnippet = "Chat cleared.";
        UnreadCount = 0;
    }

    private void TrimMessagesIfExceeded(int max)
    {
        while (ChatMessages.Count > max)
        {
            ChatMessages.RemoveAt(0);
        }
    }
}
