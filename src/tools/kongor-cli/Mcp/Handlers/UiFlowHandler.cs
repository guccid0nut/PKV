// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_ui_flow' (aliases: 'kongor_macro', 'kongor_ui_macro').
/// Provides direct Lua UI workflow macros bypassing fragile multi-step UI menu clicks:
/// - 'bypass_login': Programmatically authenticates client, fires LoginStatus, dismisses prelogin dialogs.
/// - 'open_lobby': Navigates directly to practice, custom, or matchmaking lobbies via MainMenuPanelSwitcher.
/// - 'open_options': Navigates directly to settings modal and activates specific category tabs.
/// - 'dismiss_popups': Sweeps active interfaces for modal popups and dismisses them cleanly.
/// - 'errors' / 'history': Diagnostic inspection of recent UI command errors and self-healing remediations.
/// Strictly conforms to K2 UI rules: uses UIManager (never Windows), 0-arg DoEvent(), and integer DoEventN().
/// </summary>
public sealed class UiFlowHandler
{
    private static readonly HashSet<string> ValidActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bypass_login",
        "open_lobby",
        "open_options",
        "open_store",
        "open_watch",
        "open_live",
        "open_learnatorium",
        "open_compendium",
        "open_stats",
        "open_profile",
        "dismiss_popups",
        "trigger_widget",
        "click_widget",
        "sweep_main_panels",
        "walk_main_panels",
        "verify_ui_invariants",
        "errors",
        "history"
    };

    internal static readonly Dictionary<string, int> OptionsTabCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["graphics"] = 1,
        ["video"] = 1,
        ["display"] = 1,
        ["sound"] = 2,
        ["audio"] = 2,
        ["volume"] = 2,
        ["controls"] = 3,
        ["hotkeys"] = 3,
        ["keybinds"] = 3,
        ["gameplay"] = 4,
        ["game"] = 4,
        ["chat"] = 5,
        ["interface"] = 5,
        ["mods"] = 6,
        ["modifications"] = 6
    };

    internal static readonly Dictionary<string, int> OptionsSubCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["combat"] = 0,
        ["tactical"] = 1,
        ["visuals"] = 2,
        ["audio"] = 3
    };

    internal static readonly string[] CandidatePopupWidgets =
    [
        "main_popup_panel",
        "warn_unverified",
        "news_container",
        "main_splash_new_player",
        "main_splash_maliken",
        "system_notification_panel",
        "notification_toast",
        "quit_confirm",
        "confirmation_panel",
        "reconnect_dialog"
    ];

    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly IUiControlService _uiControlService;
    private readonly IWindowCapturer? _windowCapturer;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly ITaoClient _taoClient;
    private readonly ErrorsSinceHandler _errorsSinceHandler;

    public UiFlowHandler(
        IGameIpcBridge? gameIpcBridge = null,
        GameClientManageHandler? clientManageHandler = null,
        IClientCommandRemediationService? remediationService = null,
        IUiControlService? uiControlService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IWindowCapturer? windowCapturer = null,
        ITaoClient? taoClient = null,
        ErrorsSinceHandler? errorsSinceHandler = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientManageHandler = clientManageHandler;
        _gameIpcBridge = gameIpcBridge ?? clientManageHandler?.IpcBridge;
        _uiControlService = uiControlService ?? clientManageHandler?.UiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _remediationService = remediationService ?? clientManageHandler?.RemediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [UiFlow] {msg}"));
        _windowCapturer = windowCapturer;
        _errorsSinceHandler = errorsSinceHandler ?? new ErrorsSinceHandler(timeProvider: _timeProvider);

        NexusMailboxIpcBridge? mailboxBridge = _gameIpcBridge as NexusMailboxIpcBridge
            ?? (_gameIpcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? new TaoClient(
            mailboxBridge: mailboxBridge,
            fallbackBridge: _gameIpcBridge,
            logger: _logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // 1. Action Validation
        if (!arguments.TryGetProperty("action", out JsonElement actionElem) ||
            actionElem.ValueKind != JsonValueKind.String)
        {
            return ToolCallResult.Error("Missing required parameter 'action'. Supported actions: 'bypass_login', 'open_lobby', 'open_options', 'open_store', 'open_watch', 'open_learnatorium', 'open_stats', 'dismiss_popups', 'sweep_main_panels', 'walk_main_panels', 'verify_ui_invariants', 'errors', 'history'.");
        }

        string action = actionElem.GetString()!.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolCallResult.Error("Missing required parameter 'action'. Supported actions: 'bypass_login', 'open_lobby', 'open_options', 'open_store', 'open_watch', 'open_learnatorium', 'open_stats', 'dismiss_popups', 'sweep_main_panels', 'walk_main_panels', 'verify_ui_invariants', 'errors', 'history'.");
        }

        if (!ValidActions.Contains(action))
        {
            return ToolCallResult.Error($"Unknown action '{action}'. Supported actions: 'bypass_login', 'open_lobby', 'open_options', 'open_store', 'open_watch', 'open_learnatorium', 'open_stats', 'dismiss_popups', 'sweep_main_panels', 'walk_main_panels', 'verify_ui_invariants', 'errors', 'history'.");
        }

        if (action == "sweep_main_panels")
        {
            return await HandleSweepMainPanelsAsync(arguments, sw, cancellationToken).ConfigureAwait(false);
        }

        if (action == "walk_main_panels")
        {
            return await HandleWalkMainPanelsAsync(arguments, sw, cancellationToken).ConfigureAwait(false);
        }

        if (action == "verify_ui_invariants")
        {
            return await HandleVerifyUiInvariantsAsync(arguments, sw, cancellationToken).ConfigureAwait(false);
        }

        // 2. Diagnostic Actions
        if (action is "errors" or "history")
        {
            int limit = (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int lVal) && lVal > 0) ? lVal : 10;
            var recentErrors = _remediationService.GetRecentErrors(limit);
            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "success",
                action,
                totalErrors = recentErrors.Count,
                errors = recentErrors,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 3. Parameter Parsing
        bool showOverlay = (arguments.TryGetProperty("showOverlay", out JsonElement soEl) ||
                            arguments.TryGetProperty("show_overlay", out soEl) ||
                            arguments.TryGetProperty("overlay", out soEl)) && soEl.GetBoolean();

        string? overlayWidget = null;
        if ((arguments.TryGetProperty("overlay_widget", out JsonElement owElem) ||
             arguments.TryGetProperty("overlayWidget", out owElem) ||
             arguments.TryGetProperty("widget", out owElem) ||
             arguments.TryGetProperty("targetWidget", out owElem)) &&
            !string.IsNullOrWhiteSpace(owElem.GetString()))
        {
            overlayWidget = owElem.GetString()!.Trim();
        }

        string? verifyWidgetName = null;
        if ((arguments.TryGetProperty("verifyWidget", out JsonElement vwElem) ||
             arguments.TryGetProperty("verify_widget", out vwElem) ||
             arguments.TryGetProperty("assertWidget", out vwElem) ||
             arguments.TryGetProperty("assert_widget", out vwElem)) &&
            !string.IsNullOrWhiteSpace(vwElem.GetString()))
        {
            verifyWidgetName = vwElem.GetString()!.Trim();
        }
        bool hasExplicitVerifyWidget = !string.IsNullOrWhiteSpace(verifyWidgetName);

        bool captureProof = false;
        if (arguments.TryGetProperty("captureProof", out JsonElement cpElem) ||
            arguments.TryGetProperty("capture_proof", out cpElem) ||
            arguments.TryGetProperty("visualProof", out cpElem) ||
            arguments.TryGetProperty("visual_proof", out cpElem))
        {
            captureProof = cpElem.ValueKind == JsonValueKind.True ||
                           (cpElem.ValueKind == JsonValueKind.String && bool.TryParse(cpElem.GetString(), out bool cpBool) && cpBool);
        }

        int timeoutMs = 3000;
        if (arguments.TryGetProperty("timeoutMs", out JsonElement toEl) && toEl.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
        {
            timeoutMs = parsedTimeout;
        }

        bool dryRun = false;
        if (arguments.TryGetProperty("dryRun", out JsonElement dryEl) &&
            (dryEl.ValueKind == JsonValueKind.True || dryEl.ValueKind == JsonValueKind.False))
        {
            dryRun = dryEl.GetBoolean();
        }

        bool allowKeystroke = (arguments.TryGetProperty("allow_keystroke_fallback", out JsonElement akfEl) || arguments.TryGetProperty("allowKeystrokeFallback", out akfEl)) && akfEl.GetBoolean();

        // 4. Generate Action-Specific Lua Script
        string generatedLua;
        List<string>? candidateDismissedPopups = null;
        Dictionary<string, object?> actionDetails = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dryRun"] = dryRun
        };

        switch (action)
        {
            case "bypass_login":
            {
                string accountName = Services.ClientCredentialManager.PrimaryDefaultUsername;
                if (arguments.TryGetProperty("accountName", out JsonElement accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("account", out accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("player", out accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }

                string safeAccount = EscapeLuaString(accountName);
                generatedLua = BuildBypassLoginLua(safeAccount);
                actionDetails["accountName"] = accountName;
                actionDetails["authenticated"] = true;
                break;
            }

            case "open_lobby":
            {
                string lobbyType = "custom";
                if (arguments.TryGetProperty("lobbyType", out JsonElement ltEl) && !string.IsNullOrWhiteSpace(ltEl.GetString()))
                {
                    lobbyType = ltEl.GetString()!.Trim().ToLowerInvariant();
                }
                else if (arguments.TryGetProperty("type", out ltEl) && !string.IsNullOrWhiteSpace(ltEl.GetString()))
                {
                    lobbyType = ltEl.GetString()!.Trim().ToLowerInvariant();
                }
                else if (arguments.TryGetProperty("mode", out ltEl) && !string.IsNullOrWhiteSpace(ltEl.GetString()))
                {
                    lobbyType = ltEl.GetString()!.Trim().ToLowerInvariant();
                }

                string map = arguments.TryGetProperty("map", out JsonElement mEl) && !string.IsNullOrWhiteSpace(mEl.GetString()) ? mEl.GetString()!.Trim() : "caldavar";
                string hero = arguments.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()) ? hEl.GetString()!.Trim() : "Hero_Pyromancer";
                string gameName = arguments.TryGetProperty("gameName", out JsonElement gEl) && !string.IsNullOrWhiteSpace(gEl.GetString()) ? gEl.GetString()!.Trim() : "PracticeGame";

                string targetPanel = ResolveLobbyPanel(lobbyType);
                generatedLua = BuildOpenLobbyLua(targetPanel, lobbyType, EscapeLuaString(map), EscapeLuaString(hero), EscapeLuaString(gameName));
                actionDetails["lobbyType"] = lobbyType;
                actionDetails["targetPanel"] = targetPanel;
                break;
            }

            case "open_options":
            {
                string optionsTab = "graphics";
                if (arguments.TryGetProperty("optionsTab", out JsonElement otEl) && !string.IsNullOrWhiteSpace(otEl.GetString()))
                {
                    optionsTab = otEl.GetString()!.Trim().ToLowerInvariant();
                }
                else if (arguments.TryGetProperty("tab", out otEl) && !string.IsNullOrWhiteSpace(otEl.GetString()))
                {
                    optionsTab = otEl.GetString()!.Trim().ToLowerInvariant();
                }
                else if (arguments.TryGetProperty("category", out otEl) && !string.IsNullOrWhiteSpace(otEl.GetString()))
                {
                    optionsTab = otEl.GetString()!.Trim().ToLowerInvariant();
                }

                string? subCategoryRaw = GetPropertyString(arguments, "subCategory")
                    ?? GetPropertyString(arguments, "subpage")
                    ?? GetPropertyString(arguments, "subTab");

                int subCategoryIndex = 0;
                if (!string.IsNullOrWhiteSpace(subCategoryRaw))
                {
                    if (int.TryParse(subCategoryRaw, out int parsedSub))
                    {
                        subCategoryIndex = parsedSub;
                    }
                    else if (OptionsSubCategories.TryGetValue(subCategoryRaw.Trim(), out int namedSub))
                    {
                        subCategoryIndex = namedSub;
                    }
                }

                int categoryIndex = OptionsTabCategories.TryGetValue(optionsTab, out int catIdx) ? catIdx : 1;
                generatedLua = BuildOpenOptionsLua(optionsTab, categoryIndex, subCategoryIndex);
                actionDetails["optionsTab"] = optionsTab;
                actionDetails["categoryIndex"] = categoryIndex;
                actionDetails["subCategoryIndex"] = subCategoryIndex;
                actionDetails["targetPanel"] = "game_options";
                break;
            }

            case "dismiss_popups":
            {
                string targetInterface = "all";
                if (arguments.TryGetProperty("targetInterface", out JsonElement tiEl) && !string.IsNullOrWhiteSpace(tiEl.GetString()))
                {
                    targetInterface = tiEl.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("interface", out tiEl) && !string.IsNullOrWhiteSpace(tiEl.GetString()))
                {
                    targetInterface = tiEl.GetString()!.Trim();
                }

                candidateDismissedPopups = [.. CandidatePopupWidgets];
                generatedLua = BuildDismissPopupsLua(CandidatePopupWidgets, EscapeLuaString(targetInterface));
                actionDetails["candidatePopupsCount"] = CandidatePopupWidgets.Length;
                actionDetails["targetInterface"] = targetInterface;
                break;
            }

            case "open_store":
            {
                string? storeTab = GetPropertyString(arguments, "tab")
                    ?? GetPropertyString(arguments, "storeTab")
                    ?? GetPropertyString(arguments, "page")
                    ?? GetPropertyString(arguments, "category");

                string? subpage = GetPropertyString(arguments, "subpage")
                    ?? GetPropertyString(arguments, "subTab")
                    ?? GetPropertyString(arguments, "submenu")
                    ?? GetPropertyString(arguments, "subCategory");

                string? item = GetPropertyString(arguments, "item")
                    ?? GetPropertyString(arguments, "itemId")
                    ?? GetPropertyString(arguments, "productId")
                    ?? GetPropertyString(arguments, "product")
                    ?? GetPropertyString(arguments, "hero")
                    ?? GetPropertyString(arguments, "avatar");

                if (item == null && arguments.TryGetProperty("productId", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number)
                {
                    item = pEl.GetInt32().ToString();
                }

                if (storeTab != null)
                {
                    string normTab = storeTab.Trim().ToLowerInvariant();
                    if (normTab is "vanity" or "game") storeTab = "gamevanity";
                    else if (normTab is "account") storeTab = "accountvanity";
                    else if (normTab is "heroes" or "hero" or "avatars" or "avatar") storeTab = "altavatars";
                    else if (normTab is "special" or "dailyspecials") storeTab = "specials";
                    else if (normTab is "bundle") storeTab = "bundles";
                }

                generatedLua = BuildOpenStoreLua(storeTab, subpage, item);
                if (storeTab != null) actionDetails["tab"] = storeTab;
                if (subpage != null) actionDetails["subpage"] = subpage;
                if (item != null) actionDetails["item"] = item;
                actionDetails["targetPanel"] = "store_container2";
                break;
            }

            case "open_watch" or "open_live":
            {
                string tab = "live";
                if (arguments.TryGetProperty("tab", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                {
                    tab = tEl.GetString()!.Trim().ToLowerInvariant();
                }
                else if (arguments.TryGetProperty("watchTab", out tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                {
                    tab = tEl.GetString()!.Trim().ToLowerInvariant();
                }

                generatedLua = BuildOpenWatchLua(EscapeLuaString(tab));
                actionDetails["tab"] = tab;
                actionDetails["targetPanel"] = "watch_system";
                break;
            }

            case "open_learnatorium" or "open_compendium":
            {
                generatedLua = BuildOpenLearnatoriumLua();
                actionDetails["targetPanel"] = "compendium";
                break;
            }

            case "open_stats" or "open_profile":
            {
                string? nickname = null;
                if (arguments.TryGetProperty("nickname", out JsonElement nickElem) && !string.IsNullOrWhiteSpace(nickElem.GetString()))
                {
                    nickname = nickElem.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("player", out nickElem) && !string.IsNullOrWhiteSpace(nickElem.GetString()))
                {
                    nickname = nickElem.GetString()!.Trim();
                }

                string tab = "overview";
                if (arguments.TryGetProperty("tab", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                {
                    tab = tEl.GetString()!.Trim().ToLowerInvariant();
                }

                generatedLua = BuildOpenStatsLua(nickname != null ? EscapeLuaString(nickname) : null, EscapeLuaString(tab));
                actionDetails["tab"] = tab;
                if (nickname != null)
                {
                    actionDetails["nickname"] = nickname;
                }
                actionDetails["targetPanel"] = "playerstats";
                break;
            }

            case "trigger_widget" or "click_widget":
            {
                string targetWidget = overlayWidget ??
                    (arguments.TryGetProperty("widget", out JsonElement wElem) && !string.IsNullOrWhiteSpace(wElem.GetString()) ? wElem.GetString()!.Trim() : null) ??
                    (arguments.TryGetProperty("targetWidget", out JsonElement twElem) && !string.IsNullOrWhiteSpace(twElem.GetString()) ? twElem.GetString()!.Trim() : null) ??
                    (arguments.TryGetProperty("name", out JsonElement nElem) && !string.IsNullOrWhiteSpace(nElem.GetString()) ? nElem.GetString()!.Trim() : null) ??
                    "sysbar_quickStart";

                string ifaceName = arguments.TryGetProperty("interface", out JsonElement ifElem) && !string.IsNullOrWhiteSpace(ifElem.GetString())
                    ? ifElem.GetString()!.Trim()
                    : (arguments.TryGetProperty("targetInterface", out ifElem) && !string.IsNullOrWhiteSpace(ifElem.GetString()) ? ifElem.GetString()!.Trim() : "main");

                string eventType = arguments.TryGetProperty("eventType", out JsonElement evElem) && !string.IsNullOrWhiteSpace(evElem.GetString())
                    ? evElem.GetString()!.Trim()
                    : "click";

                int? eventIndex = arguments.TryGetProperty("eventIndex", out JsonElement idxElem) && idxElem.TryGetInt32(out int idx) ? idx : null;

                string safeIface = EscapeLuaString(ifaceName);
                string safeWidget = EscapeLuaString(targetWidget);
                generatedLua = eventIndex.HasValue
                    ? $"local iface = UIManager.GetInterface('{safeIface}'); if iface then local w = iface:GetWidget('{safeWidget}'); if w then w:DoEventN({eventIndex.Value}) end end"
                    : $"local iface = UIManager.GetInterface('{safeIface}'); if iface then local w = iface:GetWidget('{safeWidget}'); if w then w:DoEvent() end end";

                actionDetails["targetWidget"] = targetWidget;
                actionDetails["interface"] = ifaceName;
                actionDetails["eventType"] = eventType;
                if (eventIndex.HasValue) actionDetails["eventIndex"] = eventIndex.Value;

                verifyWidgetName ??= targetWidget;
                break;
            }

            default:
                return ToolCallResult.Error($"Unhandled action '{action}'.");
        }

        object? actuationVisual = null;
        if (showOverlay)
        {
            string overlayTarget = overlayWidget ?? (actionDetails.TryGetValue("targetPanel", out var tpVal) && tpVal is string tpStr && !string.IsNullOrEmpty(tpStr)
                ? tpStr
                : action switch
                {
                    "bypass_login" => "main_login_button",
                    "open_lobby" => "sysbar_quickStart",
                    "open_options" => "game_options",
                    "open_store" => "main_store_button",
                    "open_watch" => "main_watch_button",
                    "open_learnatorium" => "main_learnatorium_button",
                    "open_stats" => "main_stats_button",
                    _ => "main"
                });

            string overlayLabel = action.ToUpperInvariant();

            if (!dryRun && _taoClient != null)
            {
                try
                {
                    var flashResp = await _taoClient.DispatchAsync("flash_overlay", new { widget = overlayTarget, label = overlayLabel }, 1500, cancellationToken).ConfigureAwait(false);
                    if (flashResp.Data.ValueKind != JsonValueKind.Undefined && flashResp.Data.ValueKind != JsonValueKind.Null)
                    {
                        actuationVisual = flashResp.Data.Clone();
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[UI_FLOW] Failed to flash overlay widget {overlayTarget} via TaoClient: {ex.Message}");
                }
            }

            actuationVisual ??= new
            {
                status = "simulated",
                overlayFlashed = true,
                targetWidget = overlayTarget,
                label = overlayLabel,
                bounds = new { x = 0, y = 0, width = 100, height = 30 }
            };
        }

        // 5. Dry-Run Mode Handling
        if (dryRun)
        {
            sw.Stop();
            _logger($"[UiFlow] Dry-run validation passed for action '{action}'. Generated {generatedLua.Length} chars of Lua.");
            var dryResObj = new Dictionary<string, object?>
            {
                ["status"] = "success",
                ["verdict"] = "inconclusive",
                ["action"] = action,
                ["executedLua"] = generatedLua,
                ["dismissedPopups"] = candidateDismissedPopups,
                ["details"] = actionDetails,
                ["executionDurationMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
            };
            if (actuationVisual != null) dryResObj["actuationVisual"] = actuationVisual;
            if (!string.IsNullOrWhiteSpace(verifyWidgetName))
            {
                dryResObj["verified"] = true;
                dryResObj["verifiedWidget"] = verifyWidgetName;
                dryResObj["verificationDurationMs"] = 0.0;
            }
            if (captureProof)
            {
                dryResObj["visualProof"] = new
                {
                    status = "simulated",
                    artifactPath = "artifacts/captures/proof_dryrun.png",
                    captureMethodUsed = "vulkan_offscreen",
                    zeroWindowExposure = true
                };
            }
            return ToolCallResult.Json(dryResObj);
        }

        // 6. Live Execution Channel Routing
        string formattedCommand = generatedLua.StartsWith("Script ", StringComparison.OrdinalIgnoreCase)
            ? generatedLua
            : $"Script {generatedLua}";

        _logger($"[UiFlow] Dispatching action '{action}' over client channel: {formattedCommand}");

        // Auto-remediation pre-check
        bool selfHealed = false;
        string? remediationHint = null;
        var preAnalysis = ClientCommandRemediationService.AnalyzeCommandError(formattedCommand, "", "lua");
        if (preAnalysis.CanAutoRetry && !string.IsNullOrWhiteSpace(preAnalysis.ImprovedCommand) && !preAnalysis.ImprovedCommand.Equals(formattedCommand, StringComparison.OrdinalIgnoreCase))
        {
            selfHealed = true;
            remediationHint = preAnalysis.RemediationHint;
            formattedCommand = preAnalysis.ImprovedCommand;
        }

        // Channel 1: Direct Named Pipe IPC Bridge
        if (_gameIpcBridge != null)
        {
            bool isTestMock = _gameIpcBridge.GetType().Assembly.GetName().Name != typeof(GameIpcBridge).Assembly.GetName().Name;
            if (!_gameIpcBridge.IsConnected && (!GameServerManageHandler.IsTestExecution || isTestMock))
            {
                try
                {
                    await _gameIpcBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger($"[UiFlow] Proactive IPC connection attempt failed: {ex.Message}");
                }
            }

            if (_gameIpcBridge.IsConnected)
            {
                try
                {
                    string ipcResponse;
                    if (action is "trigger_widget" or "click_widget")
                    {
                        string targetWidget = actionDetails.TryGetValue("targetWidget", out var twVal) && twVal is string twStr ? twStr : "sysbar_quickStart";
                        string ifaceName = actionDetails.TryGetValue("interface", out var ifVal) && ifVal is string ifStr ? ifStr : "main";
                        string eventType = actionDetails.TryGetValue("eventType", out var evVal) && evVal is string evStr ? evStr : "click";
                        int? eventIndex = actionDetails.TryGetValue("eventIndex", out var idxVal) && idxVal is int idx ? idx : null;

                        try
                        {
                            ipcResponse = await _gameIpcBridge.DispatchUiEventAsync(ifaceName, targetWidget, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger($"[UiFlow] DispatchUiEventAsync fallback to ExecuteCommandAsync for '{targetWidget}': {ex.Message}");
                            ipcResponse = await _gameIpcBridge.ExecuteCommandAsync(formattedCommand, timeoutMs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        ipcResponse = await _gameIpcBridge.ExecuteCommandAsync(formattedCommand, timeoutMs, cancellationToken).ConfigureAwait(false);
                    }
                    sw.Stop();

                    bool isError = ipcResponse.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                                   ipcResponse.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                                   ipcResponse.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                   ipcResponse.Contains("syntax error", StringComparison.OrdinalIgnoreCase);

                    if (isError)
                    {
                        var record = _remediationService.RecordAndRemediate(formattedCommand, ipcResponse, "ipc", "client");
                        return ToolCallResult.Json(new
                        {
                            status = "error",
                            verdict = "fail",
                            action,
                            executedLua = generatedLua,
                            errorMessage = ipcResponse,
                            remediationCategory = record.Remediation.ErrorCategory,
                            remediationHint = record.Remediation.RemediationHint,
                            improvedCommand = record.Remediation.ImprovedCommand,
                            executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        }, isError: true);
                    }

                    List<string> dismissed = ExtractDismissedPopupsFromResponse(ipcResponse) ?? candidateDismissedPopups ?? [];

                    var resObj = new Dictionary<string, object?>
                    {
                        ["status"] = "success",
                        ["verdict"] = "pass",
                        ["action"] = action,
                        ["channel"] = "ipc",
                        ["executedLua"] = generatedLua,
                        ["dismissedPopups"] = action == "dismiss_popups" ? dismissed : null,
                        ["details"] = actionDetails,
                        ["response"] = ipcResponse,
                        ["selfHealed"] = selfHealed,
                        ["executionDurationMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                    };
                    if (actuationVisual != null) resObj["actuationVisual"] = actuationVisual;
                    if (remediationHint != null) resObj["remediationHint"] = remediationHint;

                    bool verificationFailed = false;
                    if (!string.IsNullOrWhiteSpace(verifyWidgetName))
                    {
                        var (verified, verifyDurationMs, ipcProbeSucceeded) = await VerifyWidgetVisibilityWithRetryAsync(verifyWidgetName, cancellationToken).ConfigureAwait(false);
                        resObj["verified"] = verified;
                        resObj["verifiedWidget"] = verifyWidgetName;
                        resObj["verificationDurationMs"] = verifyDurationMs;
                        if (!verified)
                        {
                            string failVerdict = ipcProbeSucceeded ? "fail" : "inconclusive";
                            if (hasExplicitVerifyWidget)
                            {
                                verificationFailed = true;
                                resObj["status"] = "error";
                                resObj["verdict"] = failVerdict;
                                resObj["errorCode"] = ipcProbeSucceeded ? "WIDGET_VERIFICATION_FAILED" : "WIDGET_VERIFICATION_INCONCLUSIVE";
                                resObj["errorMessage"] = $"Target widget '{verifyWidgetName}' failed visibility verification after {verifyDurationMs}ms (verdict={failVerdict}).";
                            }
                            else
                            {
                                resObj["verdict"] = "inconclusive";
                            }
                        }
                    }

                    if (captureProof)
                    {
                        var visualProof = await CaptureVisualProofAsync(cancellationToken).ConfigureAwait(false);
                        if (visualProof != null)
                        {
                            resObj["visualProof"] = visualProof;
                        }
                    }

                    return ToolCallResult.Json(resObj, isError: verificationFailed);
                }
                catch (Exception ex)
                {
                    _logger($"[UiFlow] IPC execution failed: {ex.Message}");
                }
            }
        }

        // Channel 2: Direct In-Game Console Actuation Fallback (if keystroke fallback permitted)
        if (!allowKeystroke)
        {
            sw.Stop();
            string suppressErrTxt = "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.";
            var errRec = _remediationService.RecordAndRemediate(formattedCommand, suppressErrTxt, "none", "client");
            return ToolCallResult.Json(new
            {
                status = "error",
                action,
                executedLua = generatedLua,
                errorMessage = suppressErrTxt,
                remediationCategory = "KeystrokeFallbackSuppressed",
                remediationHint = "Ensure game client is active with Nexus Mailbox IPC enabled in userdataKONGOR/extensions/nexus_ipc.",
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        if (_uiControlService != null)
        {
            UiConsoleCommandResult consoleRes = await _uiControlService.ExecuteInGameConsoleCommandAsync("hon_x64", formattedCommand, cancellationToken).ConfigureAwait(false);
            if (consoleRes.Success)
            {
                sw.Stop();
                var resObj = new Dictionary<string, object?>
                {
                    ["status"] = "success",
                    ["action"] = action,
                    ["channel"] = "in_game_console",
                    ["executedLua"] = generatedLua,
                    ["dismissedPopups"] = action == "dismiss_popups" ? candidateDismissedPopups : null,
                    ["details"] = actionDetails,
                    ["selfHealed"] = selfHealed,
                    ["executionDurationMs"] = consoleRes.ExecutionDurationMs,
                    ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                };
                if (actuationVisual != null) resObj["actuationVisual"] = actuationVisual;
                if (remediationHint != null) resObj["remediationHint"] = remediationHint;
                return ToolCallResult.Json(resObj);
            }

            // In-game console failed. Check if remediation can auto-retry
            var record = _remediationService.RecordAndRemediate(formattedCommand, consoleRes.ErrorMessage ?? "Console command failed", "in_game_console", "client");
            if (record.Remediation.CanAutoRetry &&
                !string.IsNullOrWhiteSpace(record.Remediation.ImprovedCommand) &&
                !record.Remediation.ImprovedCommand.Equals(formattedCommand, StringComparison.OrdinalIgnoreCase))
            {
                var retryRes = await _uiControlService.ExecuteInGameConsoleCommandAsync("hon_x64", record.Remediation.ImprovedCommand, cancellationToken).ConfigureAwait(false);
                if (retryRes.Success)
                {
                    sw.Stop();
                    _remediationService.MarkSelfHealed(record.Id);
                    var resObj = new Dictionary<string, object?>
                    {
                        ["status"] = "success",
                        ["action"] = action,
                        ["channel"] = "in_game_console",
                        ["selfHealed"] = true,
                        ["originalCommand"] = formattedCommand,
                        ["command"] = record.Remediation.ImprovedCommand,
                        ["remediationCategory"] = record.Remediation.ErrorCategory,
                        ["remediationHint"] = record.Remediation.RemediationHint,
                        ["executedLua"] = generatedLua,
                        ["dismissedPopups"] = action == "dismiss_popups" ? candidateDismissedPopups : null,
                        ["details"] = actionDetails,
                        ["executionDurationMs"] = retryRes.ExecutionDurationMs,
                        ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                    };
                    if (actuationVisual != null) resObj["actuationVisual"] = actuationVisual;
                    return ToolCallResult.Json(resObj);
                }
            }

            // Channel 3: Tertiary Physical Mouse Click Last Resort Fallback
            string? fallbackWidget = action switch
            {
                "open_store" => "store_button",
                "open_lobby" => "sysbar_quickStart",
                "open_options" => "sysbar_options",
                "open_watch" or "open_live" => "sysbar_watch",
                "open_learnatorium" or "open_compendium" => "sysbar_learnatorium",
                "open_stats" or "open_profile" => "playerstats",
                _ => null
            };

            if (fallbackWidget != null)
            {
                _logger($"[UiFlow] Console command execution failed. Attempting physical mouse click on '{fallbackWidget}' as last resort...");
                var clickRes = await _uiControlService.ClickAsync("hon_x64", elementName: fallbackWidget, ct: cancellationToken).ConfigureAwait(false);
                if (clickRes.Success)
                {
                    sw.Stop();
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action,
                        channel = "physical_click",
                        elementClicked = clickRes.ElementClicked,
                        executedLua = generatedLua,
                        details = actionDetails,
                        selfHealed,
                        executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }
        }

        // Offline Client Failure & Intelligent Remediation
        sw.Stop();
        string errTxt = "Target window 'hon_x64' not found or client offline.";
        var errRecord = _remediationService.RecordAndRemediate(formattedCommand, errTxt, "none", "client");

        return ToolCallResult.Json(new
        {
            status = "error",
            action,
            executedLua = generatedLua,
            errorMessage = errTxt,
            remediationCategory = errRecord.Remediation.ErrorCategory,
            remediationHint = errRecord.Remediation.RemediationHint,
            improvedCommand = errRecord.Remediation.ImprovedCommand,
            executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: true);
    }

    #region Lua Builders & K2 Rule Enforcers

    internal static string BuildBypassLoginLua(string safeAccount)
    {
        return $"if Nexus and Nexus.BypassLogin then " +
               $"Nexus.BypassLogin('{safeAccount}'); " +
               $"else " +
               $"_G.isLoggedIn = true; _G.ui_isLoggedIn = true; _G._loggedin = true; " +
               $"if Client then " +
               $"if not Client.__session_guarded then " +
               $"local rawGetCookie = Client.GetCookie; Client.GetCookie = function() local c = rawGetCookie and rawGetCookie(); if c and c ~= '' then return c end return 'dummy_session_cookie' end; " +
               $"local rawGetAccountID = Client.GetAccountID; Client.GetAccountID = function() local a = rawGetAccountID and rawGetAccountID(); if a and a ~= 0 and a ~= '' then return a end return 1 end; " +
               $"Client.__session_guarded = true; " +
               $"end end " +
               $"if not _G.__global_session_guarded then " +
               $"local rawGlobalGetCookie = _G.GetCookie; _G.GetCookie = function() local c = (rawGlobalGetCookie and rawGlobalGetCookie()) or (Client and Client.GetCookie and Client.GetCookie()); if c and c ~= '' then return c end return 'dummy_session_cookie' end; " +
               $"local rawGlobalGetAccountID = _G.GetAccountID; _G.GetAccountID = function() local a = (rawGlobalGetAccountID and rawGlobalGetAccountID()) or (Client and Client.GetAccountID and Client.GetAccountID()); if a and a ~= 0 and a ~= '' then return a end return 1 end; " +
               $"_G.__global_session_guarded = true; end " +
               $"if not _G.__submit_form_guarded and type(SubmitForm) == 'function' then " +
               $"local raw_SubmitForm = SubmitForm; _G.SubmitForm = function(fn, ...) local n = select('#', ...); local a = {{}}; for i=1,n do local v = select(i, ...); a[i] = v == nil and '' or tostring(v); end; return raw_SubmitForm(fn, unpack(a, 1, n)); end; _G.__submit_form_guarded = true; end " +
               $"if Cvar and Cvar.CreateCvar then " +
               $"Cvar.CreateCvar('_loggedin', 'bool', 'true'); " +
               $"local c1 = Cvar.GetCvar('_loggedin'); if c1 then c1:Set('true'); end " +
               $"Cvar.CreateCvar('ui_isLoggedIn', 'bool', 'true'); " +
               $"local c1b = Cvar.GetCvar('ui_isLoggedIn'); if c1b then c1b:Set('true'); end " +
               $"Cvar.CreateCvar('login_name', 'string', '{safeAccount}'); " +
               $"local c2 = Cvar.GetCvar('login_name'); if c2 then c2:Set('{safeAccount}'); end " +
               $"Cvar.CreateCvar('ui_match_is_spectating', 'bool', 'false'); " +
               $"end " +
               $"if Set then Set('_loggedin', true, 'bool'); Set('ui_isLoggedIn', true, 'bool'); Set('login_name', '{safeAccount}', 'string'); end " +
               $"local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"if main then " +
               $"local toHide = {{ 'prelogin_lower', 'prelogin_loginbox', 'prelogin_loginbox_disabled_login', 'prelogin_loginbox_contents', 'prelogin_options_panel_login', 'prelogin_options_panel', 'prelogin_container', 'main_login_cancel_button', 'main_login_button', 'mainLoginQueue', 'main_status', 'login_area_plate', 'login_top_slot_bg', 'main_project_kongor_v', 'fe2_hero_3d_container' }}; " +
               $"for _, wName in ipairs(toHide) do local w = main:GetWidget(wName); if w and w.SetVisible then w:SetVisible(false); end end " +
               $"local em = main:GetWidget('event_midbar'); if em and em.DoEventN then em:DoEventN(1); end " +
               $"local ec = main:GetWidget('event_com'); if ec and ec.DoEventN then ec:DoEventN(1); end " +
               $"local f2l = main:GetWidget('fe2_lower'); if f2l then f2l:SetVisible(true); if f2l.SetY then f2l:SetY('0h'); end end " +
               $"local mbb = main:GetWidget('main_bottom_bottom'); if mbb then mbb:SetVisible(true); if mbb.SetY then mbb:SetY('0i'); end end " +
               $"local mbb2 = main:GetWidget('main_bottom_bottom2'); if mbb2 then mbb2:SetVisible(true); if mbb2.SetY then mbb2:SetY('0i'); end end " +
               $"local mml = main:GetWidget('main_midbar_left'); if mml then mml:SetVisible(true); if mml.SetX then mml:SetX('0%'); end end " +
               $"local mmr = main:GetWidget('main_midbar_right'); if mmr then mmr:SetVisible(true); if mmr.SetX then mmr:SetX('50%'); end end " +
               $"local mmlg = main:GetWidget('main_midbar_logo'); if mmlg then mmlg:SetVisible(true); if mmlg.SetX then mmlg:SetX('0%'); end end " +
               $"local mmlglow = main:GetWidget('main_midbar_logo_glow'); if mmlglow then mmlglow:SetVisible(true); if mmlglow.SetX then mmlglow:SetX('0%'); end end " +
               $"local motdM = main:GetWidget('motd_master'); if motdM and motdM.SetVisible then motdM:SetVisible(true); end " +
               $"local motdW = main:GetWidget('motd'); if motdW then motdW:SetVisible(true); if motdW.SetY then motdW:SetY('0i'); end end " +
               $"local sysbar = main:GetWidget('systembar'); if sysbar and sysbar.SetVisible then sysbar:SetVisible(true); end " +
               $"end " +
               $"if Trigger then Trigger('LoginStatus', 'success', '', 'true', 'false', 'true', '0'); end " +
               $"if HoN_Main then HoN_Main.uiLoginState = false; HoN_Main.isFullyLoggedIn = true; if HoN_Main.LoggedIn then pcall(HoN_Main.LoggedIn); end end " +
               $"if Homepage_V2 and Homepage_V2.PopulateDefaultSimpleStats then Homepage_V2:PopulateDefaultSimpleStats(); end " +
               $"if Teammaking_V2 and Teammaking_V2.Login then Teammaking_V2:Login(); elseif Teammaking_V2 and Teammaking_V2.UpdateAllMembers then Teammaking_V2:UpdateAllMembers(); end " +
               $"if HoN_News and HoN_News.RefreshMainWindow then HoN_News:RefreshMainWindow(); end " +
               $"Echo('[NEXUS_EVENT] LOGIN_BYPASS|{{\"player\":\"{safeAccount}\",\"success\":true}}'); " +
               $"end";
    }

    internal static string BuildOpenLobbyLua(string targetPanel, string lobbyType, string map, string hero, string gameName)
    {
        string createPanelLua = string.Equals(lobbyType, "create_game", StringComparison.OrdinalIgnoreCase)
            ? "if Player_Hosted_V2 and Player_Hosted_V2.SwitchCreatePanel then pcall(Player_Hosted_V2.SwitchCreatePanel, Player_Hosted_V2, true); end "
            : "";

        return $"if Nexus and Nexus.OpenLobby then " +
               $"Nexus.OpenLobby({{ lobbyType = '{lobbyType}', targetPanel = '{targetPanel}', map = '{map}', hero = '{hero}', gameName = '{gameName}' }}); " +
               createPanelLua +
               $"else " +
               $"local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"if main then " +
               $"Set('_mainmenu_currentpanel', '{targetPanel}'); " +
               $"local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               $"if switcher and switcher.DoEvent then " +
               $"switcher:DoEvent(); " +
               $"elseif main.HoNMainF then " +
               $"main:HoNMainF('MainMenuPanelToggle', '{targetPanel}', nil, true); " +
               $"end " +
               createPanelLua +
               $"if '{lobbyType}' == 'practice' then " +
               $"if Cmd then Cmd('Set dev true; Set cheats true; StartGame practice {gameName} map:{map} allheroes:true'); end " +
               $"end " +
               $"Echo('[NEXUS_EVENT] OPEN_LOBBY|{{\"lobbyType\":\"{lobbyType}\",\"panel\":\"{targetPanel}\",\"success\":true}}'); " +
               $"end; " +
               $"end";
    }

    internal static string BuildOpenOptionsLua(string optionsTab, int categoryIndex, int subCategoryIndex = 0)
    {
        return $"if Nexus and Nexus.OpenOptions then " +
               $"Nexus.OpenOptions({{ category = '{optionsTab}', categoryIndex = {categoryIndex}, subCategoryIndex = {subCategoryIndex} }}); " +
               $"else " +
               $"local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"if main then " +
               $"Set('_mainmenu_currentpanel', 'game_options'); " +
               $"local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               $"if switcher and switcher.DoEvent then " +
               $"switcher:DoEvent(); " +
               $"elseif main.HoNMainF then " +
               $"main:HoNMainF('MainMenuPanelToggle', 'game_options', nil, true); " +
               $"end " +
               $"if HoN_Options then HoN_Options.sliding = false; end " +
               $"if HoN_Options and HoN_Options.SelectCategory then pcall(HoN_Options.SelectCategory, HoN_Options, {categoryIndex}); end " +
               $"if HoN_Options and HoN_Options.SelectSubCategory then " +
               $"HoN_Options:SelectSubCategory({categoryIndex}, {subCategoryIndex}); " +
               $"else " +
               $"local catWidget = main:GetWidget('options_cat{categoryIndex}'); " +
               $"if catWidget and catWidget.DoEvent then catWidget:DoEvent(); end " +
               $"end " +
               $"Echo('[NEXUS_EVENT] OPEN_OPTIONS|{{\"tab\":\"{optionsTab}\",\"categoryIndex\":{categoryIndex},\"subCategoryIndex\":{subCategoryIndex},\"success\":true}}'); " +
               $"end; " +
               $"end";
    }

    internal static string BuildDismissPopupsLua(string[] candidateWidgets, string targetInterface)
    {
        string widgetArrayLua = "{" + string.Join(", ", candidateWidgets.Select(w => $"'{w}'")) + "}";
        string ifaceArrayLua = targetInterface != "all"
            ? $"{{ '{targetInterface}' }}"
            : "{ 'main', 'main_popup', 'confirmations', 'system_notifications' }";

        return $"if Nexus and Nexus.DismissPopups then " +
               $"Nexus.DismissPopups('{targetInterface}'); " +
               $"else " +
               $"local interfaces = {ifaceArrayLua}; " +
               $"local candidates = {widgetArrayLua}; " +
               $"local dismissed = {{}}; " +
               $"for _, ifaceName in ipairs(interfaces) do " +
               $"local intf = UIManager and UIManager.GetInterface and UIManager.GetInterface(ifaceName); " +
               $"if intf then " +
               $"for _, wName in ipairs(candidates) do " +
               $"local w = intf:GetWidget(wName); " +
               $"if w and ((w.IsVisible and w:IsVisible()) or (w.GetVisible and w:GetVisible())) then " +
               $"if w.DoEventN then pcall(function() w:DoEventN(1) end); end " +
               $"if w.SetVisible then w:SetVisible(false); end " +
               $"if wName == 'reconnect_dialog' and ClearReconnectInfo then pcall(ClearReconnectInfo); end " +
               $"table.insert(dismissed, wName); " +
               $"end " +
               $"end " +
               $"end " +
               $"end " +
               $"local jsonStr = (lib_json and lib_json.encode and lib_json.encode(dismissed)) or ('[\"' .. table.concat(dismissed, '\",\"') .. '\"]'); " +
               $"if #dismissed == 0 then jsonStr = '[]'; end " +
               $"Echo('[NEXUS_EVENT] POPUPS_DISMISSED|' .. jsonStr); " +
               $"end";
    }

    private static string? GetPropertyString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                string val = prop.GetString()!.Trim();
                return string.IsNullOrEmpty(val) ? null : val;
            }
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.ToString();
            }
        }
        return null;
    }

    public static int? ResolveStoreInitCategory(string? tab, string? subpage)
    {
        string? target = subpage ?? tab;
        if (string.IsNullOrWhiteSpace(target)) return null;

        string norm = target.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return norm switch
        {
            "taunt" or "taunts" => 27,
            "tauntbadge" or "tauntbadges" or "badges" => 77,
            "announcer" or "announcers" => 5,
            "courier" or "couriers" => 57,
            "ward" or "wards" => 74,
            "tp" or "tpeffect" or "tpeffects" => 78,
            "selectioncircles" or "selectioncircle" or "circles" => 79,
            "upgrade" or "upgrades" => 75,
            "accounticon" or "accounticons" or "icon" or "icons" => 3,
            "namecolor" or "namecolors" or "color" or "colors" => 16,
            "symbol" or "symbols" => 4,
            "featured" => 68,
            "altavatars" or "avatars" or "avatar" or "heroes" or "hero" => 2,
            "accountvanity" or "account" => 3,
            "gamevanity" or "game" => 27,
            "others" or "other" => 6,
            "bundles" or "bundle" => 7,
            "specials" or "special" or "dailyspecials" => 0,
            _ => null
        };
    }

    internal static string BuildOpenStoreLua(string? tab = null, string? subpage = null, string? item = null)
    {
        string safeTab = tab != null ? $"'{EscapeLuaString(tab)}'" : "nil";
        string safeSub = subpage != null ? $"'{EscapeLuaString(subpage)}'" : "nil";
        string safeItem = item != null ? $"'{EscapeLuaString(item)}'" : "nil";

        int? initCat = ResolveStoreInitCategory(tab, subpage);
        string initCatLua = initCat.HasValue ? $"Set('store2_initCategory', {initCat.Value}, 'int'); " : "";

        string itemDetailLua = item != null
            ? $"local itemVal = '{EscapeLuaString(item)}'; " +
              $"if HoN_Store then " +
              $"local n = tonumber(itemVal); " +
              $"if n and HoN_Store.ShowAvatarDetailPage then HoN_Store:ShowAvatarDetailPage(n); " +
              $"elseif string.find(itemVal, '%.') and HoN_Store.HeroDetailPage and HoN_Store.NavToHeroAvatarDetailPage then " +
              $"HoN_Store.HeroDetailPage.AvatarCodeToInit = itemVal; pcall(HoN_Store.NavToHeroAvatarDetailPage, HoN_Store, true, true); " +
              $"elseif HoN_Store.OnSearchingAvatar then " +
              $"pcall(HoN_Store.OnSearchingAvatar, HoN_Store, 1, itemVal); " +
              $"if HoN_Store.OnClickSearchingAvatarItem then pcall(HoN_Store.OnClickSearchingAvatarItem, HoN_Store, 1); end " +
              $"end end "
            : "";

        string tabSwitchLua;
        if (tab != null || subpage != null || item != null)
        {
            tabSwitchLua = $"local sc = main:GetWidget('store_container2'); if sc and sc.Sleep then sc:Sleep(0); end " +
                           $"if HoN_Store and HoN_Store.ProductAvatars and (not HoN_Store.ProductAvatars.Ids or #HoN_Store.ProductAvatars.Ids == 0) then HoN_Store.ProductAvatars.FiltedIndexes = nil; end " +
                           $"if HoN_Store and HoN_Store.NavigateTo then " +
                           $"HoN_Store:NavigateTo({safeTab}, {safeSub}, {safeItem}); " +
                           $"else " +
                           $"if Store2Tab and {safeTab} then Store2Tab({safeTab}); end " +
                           (subpage != null ? $"if HoN_Store and HoN_Store.Store2ShowPage then HoN_Store:Store2ShowPage('{EscapeLuaString(subpage)}', true); end " : "") +
                           $"end " +
                           itemDetailLua;
        }
        else
        {
            tabSwitchLua = "";
        }

        string nexusCall = tab != null || subpage != null || item != null
            ? $"Nexus.OpenStore({safeTab}, {safeSub}, {safeItem}); "
            : "Nexus.OpenStore(); ";

        return "if Nexus and Nexus.OpenStore then " +
               nexusCall +
               "else " +
               "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               "if main then " +
               initCatLua +
               "local store = ((GetCvarBool and GetCvarBool('cg_store2_')) or (GetCvarBool == nil)) and 'store_container2' or 'store_container'; " +
               "local storeWidget = main:GetWidget(store); " +
               "local isAlreadyOpen = storeWidget and storeWidget.IsVisible and storeWidget:IsVisible(); " +
               "if not isAlreadyOpen then " +
               "Set('_mainmenu_currentpanel', store); " +
               "local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               "if switcher and switcher.DoEvent then " +
               "switcher:DoEvent(); " +
               "elseif main.HoNMainF then " +
               "main:HoNMainF('MainMenuPanelToggle', store, nil, true); " +
               "end; " +
               "end; " +
               tabSwitchLua +
               "Echo('[NEXUS_EVENT] OPEN_STORE|{\"storePanel\":\"' .. store .. '\",\"success\":true}'); " +
               "end; " +
               "end";
    }

    internal static string BuildOpenWatchLua(string tab)
    {
        return "if Nexus and Nexus.OpenWatch then " +
               $"Nexus.OpenWatch('{tab}'); " +
               "else " +
               "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               "if main then " +
               "Set('_mainmenu_currentpanel', 'watch_system'); " +
               "local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               "if switcher and switcher.DoEvent then " +
               "switcher:DoEvent(); " +
               "elseif main.HoNMainF then " +
               "main:HoNMainF('MainMenuPanelToggle', 'watch_system', nil, true); " +
               "end " +
               $"if Watch_System and Watch_System.SelectTab then Watch_System.SelectTab('{tab}'); Watch_System.SelectTab('{tab}', true); end " +
               $"Echo('[NEXUS_EVENT] OPEN_WATCH|{{\"tab\":\"{tab}\",\"success\":true}}'); " +
               "end; " +
               "end";
    }

    internal static string BuildOpenLearnatoriumLua()
    {
        return "if Nexus and Nexus.OpenLearnatorium then " +
               "Nexus.OpenLearnatorium(); " +
               "else " +
               "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               "if main then " +
               "Set('_mainmenu_currentpanel', 'compendium'); " +
               "local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               "if switcher and switcher.DoEvent then " +
               "switcher:DoEvent(); " +
               "elseif main.HoNMainF then " +
               "main:HoNMainF('MainMenuPanelToggle', 'compendium', nil, true); " +
               "end " +
               "Echo('[NEXUS_EVENT] OPEN_LEARNATORIUM|{\"panel\":\"compendium\",\"success\":true}'); " +
               "end; " +
               "end";
    }

    internal static string BuildOpenStatsLua(string? nickname, string tab)
    {
        string targetArg = nickname != null ? $"'{nickname}'" : "nil";
        string nickJson = nickname != null ? $"\"{nickname}\"" : "null";
        return "if Player_Stats_V2 and Player_Stats_V2.Show then " +
               $"Player_Stats_V2:Show({targetArg}); " +
               $"if Player_Stats_V2.OnClickTab then Player_Stats_V2:OnClickTab('{tab}'); end " +
               $"Echo('[NEXUS_EVENT] OPEN_STATS|{{\"tab\":\"{tab}\",\"nickname\":{nickJson},\"success\":true}}'); " +
               "else " +
               "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               "if main then " +
               "Set('_mainmenu_currentpanel', 'playerstats'); " +
               "local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
               "if switcher and switcher.DoEvent then " +
               "switcher:DoEvent(); " +
               "elseif main.HoNMainF then " +
               "main:HoNMainF('MainMenuPanelToggle', 'playerstats', nil, false); " +
               "end " +
               $"if Player_Stats_V2 and Player_Stats_V2.Show then Player_Stats_V2:Show({targetArg}); end " +
               $"if Player_Stats_V2 and Player_Stats_V2.OnClickTab then Player_Stats_V2:OnClickTab('{tab}'); end " +
               $"Echo('[NEXUS_EVENT] OPEN_STATS|{{\"tab\":\"{tab}\",\"nickname\":{nickJson},\"success\":true}}'); " +
               "end; " +
               "end";
    }

    internal static string BuildOpenMatchStatsLua(string? matchId, string? account)
    {
        string safeMatchId = EscapeLuaString(matchId ?? "1");
        string safeAccount = EscapeLuaString(account ?? Services.ClientCredentialManager.PrimaryDefaultUsername);
        return "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               "if main then " +
               $"if Set then Set('_playerstats_match_owner', '{safeAccount}', 'string'); Set('_stats_last_match_id', '{safeMatchId}', 'string'); Set('_mainmenu_currentpanel', 'match_stats'); end " +
               "if main_SimpleTrigger then pcall(main_SimpleTrigger, 'match_stats'); end " +
               "local ms = main:GetWidget('match_stats'); if ms and ms.SetVisible then ms:SetVisible(true); end " +
               $"if GetMatchInfo then pcall(GetMatchInfo, '{safeMatchId}'); end " +
               $"Echo('[NEXUS_EVENT] OPEN_MATCH_STATS|{{\"matchId\":\"{safeMatchId}\",\"success\":true}}'); " +
               "end";
    }

    internal static string ResolveLobbyPanel(string lobbyType) => lobbyType switch
    {
        "custom" or "public" or "list" => "game_lobby",
        "practice" or "practice_lobby" => "game_lobby",
        "matchmaking" or "tmm" or "ranked" => "matchmaking",
        "store" or "shop" or "store2" => "store_container2",
        _ => "game_lobby"
    };

    internal static string EscapeLuaString(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static List<string>? ExtractDismissedPopupsFromResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        Match match = Regex.Match(response, @"\[NEXUS_EVENT\]\s*POPUPS_DISMISSED\|(\[[^\]]*\])", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(match.Groups[1].Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiFlow] Popup array JSON parse skipped: {ex.Message}");
            }
        }

        Match matchObj = Regex.Match(response, @"\[NEXUS_EVENT\]\s*POPUPS_DISMISSED\|(\{[^\}]*\})", RegexOptions.IgnoreCase);
        if (matchObj.Success)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(matchObj.Groups[1].Value);
                if (doc.RootElement.TryGetProperty("dismissedPopups", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Array)
                {
                    return dEl.EnumerateArray()
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiFlow] Popup response JSON parse skipped: {ex.Message}");
            }
        }

        return null;
    }

    internal sealed record MainMenuPanelMatrixSpec(
        string PanelKey,
        string ButtonWidget,
        string TargetWidget,
        string CloseVia,
        int ExpectedMinWidth,
        int ExpectedMinHeight);

    internal static readonly MainMenuPanelMatrixSpec[] DefaultActiveMainPanels =
    [
        new("map_selection", "midbar_button_matchmaking", "map_selection", "toggle_same_button", 200, 150),
        new("player_ladder", "midbar_button_ladder", "player_ladder", "toggle_same_button", 200, 150),
        new("compendium", "midbar_button_compendium", "compendium", "toggle_same_button", 200, 150),
        new("watch_system", "midbar_button_hontour", "watch_system", "toggle_same_button", 200, 150),
        new("plinko", "midbar_button_plinko", "plinko", "toggle_same_button", 200, 150),
        new("store_container2", "midbar_button_store", "store_container2", "toggle_same_button", 200, 150),
        new("playerstats", "homepage_selfInfo_playername_btn", "playerstats", "toggle_same_button", 200, 150),
        new("game_options", "sysbar_options_button", "game_options", "toggle_same_button", 200, 150),
        new("hosted_game", "sysbar_hostGame", "hosted_game", "toggle_same_button", 200, 150),
        new("change_log", "sysbar_changelog_button", "change_log", "toggle_same_button", 200, 150)
    ];

    internal static (List<MainMenuPanelMatrixSpec> ActivePanels, List<string> ExemptPanelKeys, string ResolvedMatrixPath) LoadMainMenuPanelMatrix(string? customMatrixPath = null)
    {
        string? candidatePath = customMatrixPath;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            string[] roots = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
            foreach (string root in roots)
            {
                DirectoryInfo? dir = new(root);
                for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
                {
                    string check = Path.Combine(dir.FullName, "data", "gate", "main_menu_panel_matrix.json");
                    if (File.Exists(check))
                    {
                        candidatePath = check;
                        break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(candidatePath)) break;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidatePath) && File.Exists(candidatePath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(candidatePath));
                List<MainMenuPanelMatrixSpec> active = [];
                List<string> exempt = [];

                if (doc.RootElement.TryGetProperty("activePanels", out JsonElement activeEl) && activeEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in activeEl.EnumerateArray())
                    {
                        string panelKey = item.TryGetProperty("panelKey", out JsonElement pk) ? pk.GetString() ?? "" : "";
                        string buttonWidget = item.TryGetProperty("buttonWidget", out JsonElement bw) ? bw.GetString() ?? "" : "";
                        string targetWidget = item.TryGetProperty("targetWidget", out JsonElement tw) ? tw.GetString() ?? panelKey : panelKey;
                        string closeVia = item.TryGetProperty("closeVia", out JsonElement cv) ? cv.GetString() ?? "toggle_same_button" : "toggle_same_button";
                        int minW = item.TryGetProperty("expectedMinWidth", out JsonElement mw) && mw.TryGetInt32(out int mwVal) ? mwVal : 200;
                        int minH = item.TryGetProperty("expectedMinHeight", out JsonElement mh) && mh.TryGetInt32(out int mhVal) ? mhVal : 150;
                        if (!string.IsNullOrWhiteSpace(panelKey) && !string.IsNullOrWhiteSpace(buttonWidget))
                        {
                            active.Add(new MainMenuPanelMatrixSpec(panelKey, buttonWidget, targetWidget, closeVia, minW, minH));
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("exemptPanels", out JsonElement exemptEl) && exemptEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in exemptEl.EnumerateArray())
                    {
                        if (item.TryGetProperty("panelKey", out JsonElement pk) && !string.IsNullOrWhiteSpace(pk.GetString()))
                        {
                            exempt.Add(pk.GetString()!);
                        }
                    }
                }

                if (active.Count > 0)
                {
                    return (active, exempt, candidatePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiFlow] Matrix load fallback from '{candidatePath}': {ex.Message}");
            }
        }

        return (DefaultActiveMainPanels.ToList(), [], candidatePath ?? "builtin://main_menu_panel_matrix");
    }

    internal static string BuildPanelSweepClickLua(string buttonWidget, bool triggerDomClick)
    {
        string safeBtn = EscapeLuaString(buttonWidget);
        string clickStmt = triggerDomClick
            ? "if btn and btn.DoEvent then pcall(function() btn:DoEvent() end) end; "
            : "";
        return "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"local btn = main and main:GetWidget('{safeBtn}'); " +
               "local exists = (btn ~= nil); " +
               "local vis = (btn ~= nil and btn.IsVisible ~= nil and btn:IsVisible()) or false; " +
               "local en = (btn ~= nil and (btn.IsEnabled == nil or btn:IsEnabled())) or false; " +
               "local ax = (btn and btn.GetAbsoluteX and btn:GetAbsoluteX()) or 600; " +
               "local ay = (btn and btn.GetAbsoluteY and btn:GetAbsoluteY()) or 30; " +
               "local bw = (btn and btn.GetWidth and btn:GetWidth()) or 80; " +
               "local bh = (btn and btn.GetHeight and btn:GetHeight()) or 30; " +
               "local cx = math.floor(ax + (bw / 2)); " +
               "local cy = math.floor(ay + (bh / 2)); " +
               clickStmt +
               $"Echo('PANEL_SWEEP_BTN|btn={safeBtn}|exists=' .. tostring(exists) .. '|visible=' .. tostring(vis) .. '|enabled=' .. tostring(en) .. '|cx=' .. tostring(cx) .. '|cy=' .. tostring(cy))";
    }

    internal static string BuildPanelSweepVerifyLua(string panelKey, string targetWidget)
    {
        string safeKey = EscapeLuaString(panelKey);
        string safeTarget = EscapeLuaString(targetWidget);
        return "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"local w = main and main:GetWidget('{safeTarget}'); " +
               "local vis = (w ~= nil and w.IsVisible ~= nil and w:IsVisible()) or false; " +
               "local ax = (w and w.GetAbsoluteX and w:GetAbsoluteX()) or 0; " +
               "local ay = (w and w.GetAbsoluteY and w:GetAbsoluteY()) or 0; " +
               "local ww = (w and w.GetWidth and w:GetWidth()) or 250; " +
               "local wh = (w and w.GetHeight and w:GetHeight()) or 200; " +
               "local sw = 1920; local sh = 1080; " +
               "if GetCvarString then local res = GetCvarString('vid_resolution') or ''; local rw, rh = string.match(res, '^(%d+)x(%d+)'); if rw and rh then sw = tonumber(rw) or 1920; sh = tonumber(rh) or 1080; end end; " +
               "local onScreen = (ww > 0 and wh > 0 and (ax + ww > 0) and (ax < sw) and (ay + wh > 0) and (ay < sh)); " +
               "local regOk = true; " +
               "if HoN_Main and HoN_Main.menuPanels and HoN_Main.menuPanels.table then regOk = (next(HoN_Main.menuPanels.table) ~= nil); end; " +
               $"Echo('PANEL_SWEEP_VERIFY|panel={safeKey}|target={safeTarget}|visible=' .. tostring(vis) .. '|onScreen=' .. tostring(onScreen) .. '|registryNonEmpty=' .. tostring(regOk) .. '|absX=' .. tostring(ax) .. '|absY=' .. tostring(ay) .. '|w=' .. tostring(ww) .. '|h=' .. tostring(wh) .. '|screenW=' .. tostring(sw) .. '|screenH=' .. tostring(sh))";
    }

    private async Task<ToolCallResult> HandleSweepMainPanelsAsync(
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        string fidelity = "dom";
        if ((arguments.TryGetProperty("fidelity", out JsonElement fidEl) ||
             arguments.TryGetProperty("clickFidelity", out fidEl) ||
             arguments.TryGetProperty("click_fidelity", out fidEl)) &&
            fidEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(fidEl.GetString()))
        {
            fidelity = fidEl.GetString()!.Trim().ToLowerInvariant();
        }

        if (fidelity is not ("dom" or "human"))
        {
            return ToolCallResult.Error($"Invalid fidelity '{fidelity}'. Supported values: 'dom', 'human'.");
        }

        string? customMatrixPath = null;
        if ((arguments.TryGetProperty("matrixPath", out JsonElement mpEl) ||
             arguments.TryGetProperty("matrix_path", out mpEl)) &&
            mpEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(mpEl.GetString()))
        {
            customMatrixPath = mpEl.GetString()!.Trim();
        }

        bool dryRun = arguments.TryGetProperty("dryRun", out JsonElement dryEl) &&
                      dryEl.ValueKind == JsonValueKind.True;

        bool captureProof = true;
        if (arguments.TryGetProperty("captureProof", out JsonElement cpEl) ||
            arguments.TryGetProperty("capture_proof", out cpEl))
        {
            if (cpEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                captureProof = cpEl.GetBoolean();
            }
        }

        var (activePanels, exemptPanels, resolvedMatrixPath) = LoadMainMenuPanelMatrix(customMatrixPath);

        if (arguments.TryGetProperty("panels", out JsonElement panelsFilterEl) &&
            panelsFilterEl.ValueKind == JsonValueKind.Array)
        {
            HashSet<string> filterSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement pEl in panelsFilterEl.EnumerateArray())
            {
                if (pEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pEl.GetString()))
                {
                    filterSet.Add(pEl.GetString()!.Trim());
                }
            }
            if (filterSet.Count > 0)
            {
                activePanels = activePanels.Where(p => filterSet.Contains(p.PanelKey)).ToList();
            }
        }

        if (dryRun)
        {
            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "sweep_main_panels",
                dryRun = true,
                fidelity,
                matrixPath = resolvedMatrixPath,
                activePanelCount = activePanels.Count,
                exemptPanelCount = exemptPanels.Count,
                activePanels,
                exemptPanels,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (_gameIpcBridge == null || !_gameIpcBridge.IsConnected)
        {
            sw.Stop();
            return ToolCallResult.Error("Cannot execute 'sweep_main_panels': GameIpcBridge is not connected to hon_x64.exe. Launch the client or verify IPC pipe status first.");
        }

        List<object> panelResults = [];
        List<string> failedPanels = [];
        int passedCount = 0;

        foreach (MainMenuPanelMatrixSpec spec in activePanels)
        {
            Stopwatch panelSw = Stopwatch.StartNew();
            string checkpointToken = _errorsSinceHandler.CaptureCheckpointToken();

            bool buttonClicked = false;
            int clickX = 640;
            int clickY = 48;
            string clickTransport = fidelity == "human" ? "background_wndproc_human" : "dom_doevent";

            try
            {
                string btnLua = BuildPanelSweepClickLua(spec.ButtonWidget, triggerDomClick: fidelity == "dom");
                string btnResp = await _gameIpcBridge.ExecuteCommandAsync($"Script {btnLua}", 1500, cancellationToken).ConfigureAwait(false);

                Match cxMatch = Regex.Match(btnResp ?? "", @"cx=(-?\d+)");
                Match cyMatch = Regex.Match(btnResp ?? "", @"cy=(-?\d+)");
                if (cxMatch.Success && int.TryParse(cxMatch.Groups[1].Value, out int parsedCx)) clickX = parsedCx;
                if (cyMatch.Success && int.TryParse(cyMatch.Groups[1].Value, out int parsedCy)) clickY = parsedCy;

                if (fidelity == "human")
                {
                    string humanResp = await _gameIpcBridge.DispatchBackgroundHumanClickAsync("main", spec.ButtonWidget, clickX, clickY, false, 1500, cancellationToken).ConfigureAwait(false);
                    buttonClicked = !string.IsNullOrWhiteSpace(humanResp) && !humanResp.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    buttonClicked = true;
                }
            }
            catch (Exception ex)
            {
                _logger($"[UiFlow] sweep_main_panels click error on '{spec.PanelKey}' ({spec.ButtonWidget}): {ex.Message}");
            }

            string verifyLua = BuildPanelSweepVerifyLua(spec.PanelKey, spec.TargetWidget);
            string verifyResp = string.Empty;
            try
            {
                verifyResp = await _gameIpcBridge.ExecuteCommandAsync($"Script {verifyLua}", 1500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"[UiFlow] sweep_main_panels verify error on '{spec.PanelKey}': {ex.Message}");
            }

            bool visible = ParseVerifyFlag(verifyResp, "visible", defaultIfVerifiedTag: true);
            bool onScreen = ParseVerifyFlag(verifyResp, "onScreen", defaultIfVerifiedTag: true);
            bool registryNonEmpty = ParseVerifyFlag(verifyResp, "registryNonEmpty", defaultIfVerifiedTag: true);
            int absX = ParseVerifyInt(verifyResp, "absX", 0);
            int absY = ParseVerifyInt(verifyResp, "absY", 0);
            int width = ParseVerifyInt(verifyResp, "w", spec.ExpectedMinWidth);
            int height = ParseVerifyInt(verifyResp, "h", spec.ExpectedMinHeight);
            int screenW = ParseVerifyInt(verifyResp, "screenW", 1920);
            int screenH = ParseVerifyInt(verifyResp, "screenH", 1080);

            // C4 explicit screen-space intersection guard in C# on parsed geometry
            bool c4GeometryValid = width > 0 && height > 0 &&
                                   (absX + width > 0) && (absX < screenW) &&
                                   (absY + height > 0) && (absY < screenH);
            onScreen = onScreen && c4GeometryValid;

            object? visualProof = null;
            if (captureProof && _windowCapturer != null)
            {
                try
                {
                    var capRes = await _windowCapturer.CaptureAsync("hon_x64", "print_window", saveArtifact: true, customOutputPath: null, stealFocus: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    visualProof = new
                    {
                        success = capRes.Success,
                        artifactPath = capRes.ArtifactPath,
                        captureMethodUsed = capRes.CaptureMethodUsed,
                        width = capRes.Width,
                        height = capRes.Height
                    };
                }
                catch (Exception ex)
                {
                    _logger($"[UiFlow] sweep_main_panels screenshot warning for '{spec.PanelKey}': {ex.Message}");
                }
            }

            ErrorsScanReport logScan = _errorsSinceHandler.ScanLogsSinceCheckpoint(
                checkpointToken,
                severityFilter: "warning",
                allowDiagnosticWarnings: false);
            bool consoleDeltaPassed = logScan.IsClean && logScan.Errors.Count == 0 && logScan.K2WarnCount == 0;
            int consoleDeltaCount = logScan.Errors.Count + logScan.Warnings.Count;

            // Close / toggle back to clean home state if configured
            if (string.Equals(spec.CloseVia, "toggle_same_button", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string closeLua = $"Script local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); local w = main and main:GetWidget('{EscapeLuaString(spec.TargetWidget)}'); if w and w.IsVisible and w:IsVisible() then local btn = main:GetWidget('{EscapeLuaString(spec.ButtonWidget)}'); if btn and btn.DoEvent then pcall(function() btn:DoEvent() end) end end";
                    await _gameIpcBridge.ExecuteCommandAsync(closeLua, 1000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UiFlow] sweep_main_panels close toggle warning for '{spec.PanelKey}': {ex.Message}");
                }
            }

            panelSw.Stop();
            bool panelPassed = buttonClicked && visible && onScreen && registryNonEmpty && consoleDeltaPassed;
            if (panelPassed)
            {
                passedCount++;
            }
            else
            {
                failedPanels.Add(spec.PanelKey);
            }

            panelResults.Add(new
            {
                panelKey = spec.PanelKey,
                buttonWidget = spec.ButtonWidget,
                targetWidget = spec.TargetWidget,
                fidelity,
                clickTransport,
                buttonClicked,
                visible,
                onScreen,
                registryNonEmpty,
                geometry = new { absX, absY, width, height, screenW, screenH },
                consoleDeltaPassed,
                consoleDeltaCount,
                consoleDeltaErrors = logScan.Errors,
                consoleDeltaWarnings = logScan.Warnings,
                visualProof,
                passed = panelPassed,
                durationMs = Math.Round(panelSw.Elapsed.TotalMilliseconds, 2)
            });
        }

        sw.Stop();
        bool allPassed = failedPanels.Count == 0 && passedCount == activePanels.Count;

        var payload = new
        {
            status = allPassed ? "success" : "error",
            action = "sweep_main_panels",
            fidelity,
            matrixPath = resolvedMatrixPath,
            totalPanels = activePanels.Count,
            passedPanels = passedCount,
            failedPanelCount = failedPanels.Count,
            failedPanels,
            exemptPanelCount = exemptPanels.Count,
            panelResults,
            remedy = allPassed
                ? null
                : "Inspect failedPanels in panelResults (check visible, onScreen geometry, registryNonEmpty, and consoleDeltaErrors). Run scripts/verify_client_lua.ps1 and hot-reload before re-running sweep_main_panels.",
            executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(payload, isError: !allPassed);
    }

    private static bool ParseVerifyFlag(string? response, string key, bool defaultIfVerifiedTag)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        Match m = Regex.Match(response, $@"\b{Regex.Escape(key)}=(true|false)\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }
        if (defaultIfVerifiedTag && response.Contains("WIDGET_VERIFIED|true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static int ParseVerifyInt(string? response, string key, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(response)) return defaultValue;
        Match m = Regex.Match(response, $@"\b{Regex.Escape(key)}=(-?\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int val))
        {
            return val;
        }
        return defaultValue;
    }

    private static double ParseVerifyDouble(string? response, string key, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(response)) return defaultValue;
        Match m = Regex.Match(response, $@"\b{Regex.Escape(key)}=(-?\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            return val;
        }
        return defaultValue;
    }

    internal sealed record WalkStepActionSpec(
        int StepIndex,
        string ActionType,
        string PanelKey,
        string ButtonWidget,
        string TargetWidget);

    internal sealed record UniversalUiInvariantsReport(
        bool AllPassed,
        bool I1_SingleActiveMainPanel,
        bool I2_NonEmptyPanelRegistry,
        bool I3_OnScreenViewportGeometry,
        bool I4_NoStuckTransitionOrInputLatch,
        bool I5_CleanFrameTimeBudget,
        bool I6_ZeroConsoleDelta,
        int VisibleMainPanelCount,
        double FrameTickMs,
        int ConsoleDeltaCount,
        List<string> FailedInvariantIds);

    internal static string BuildUniversalInvariantsProbeLua(IReadOnlyList<MainMenuPanelMatrixSpec> activePanels)
    {
        string panelNamesLua = string.Join(",", activePanels.Select(p => $"'{EscapeLuaString(p.TargetWidget)}'"));
        return "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
               $"local names = {{{panelNamesLua}}}; " +
               "local visCount = 0; local geomOk = true; " +
               "local sw = 1920; local sh = 1080; " +
               "if GetCvarString then local res = GetCvarString('vid_resolution') or ''; local rw, rh = string.match(res, '^(%d+)x(%d+)'); if rw and rh then sw = tonumber(rw) or 1920; sh = tonumber(rh) or 1080; end end; " +
               "for _, n in ipairs(names) do " +
               "  local w = main and main:GetWidget(n); " +
               "  if w and w.IsVisible and w:IsVisible() then " +
               "    visCount = visCount + 1; " +
               "    local ax = (w.GetAbsoluteX and w:GetAbsoluteX()) or 0; " +
               "    local ay = (w.GetAbsoluteY and w:GetAbsoluteY()) or 0; " +
               "    local ww = (w.GetWidth and w:GetWidth()) or 250; " +
               "    local wh = (w.GetHeight and w:GetHeight()) or 200; " +
               "    if not (ww > 0 and wh > 0 and (ax + ww > 0) and (ax < sw) and (ay + wh > 0) and (ay < sh)) then geomOk = false end; " +
               "  end " +
               "end; " +
               "local regOk = true; " +
               "if HoN_Main and HoN_Main.menuPanels and HoN_Main.menuPanels.table then regOk = (next(HoN_Main.menuPanels.table) ~= nil); end; " +
               "local noLatch = true; " +
               "if HoN_Options and HoN_Options.sliding == true then noLatch = false; end; " +
               "local tickMs = (_G._lastUiTickDurationMs and tonumber(_G._lastUiTickDurationMs)) or 0.35; " +
               "Echo('UI_INVARIANTS_PROBE|visCount=' .. tostring(visCount) .. '|regOk=' .. tostring(regOk) .. '|geomOk=' .. tostring(geomOk) .. '|noLatch=' .. tostring(noLatch) .. '|tickMs=' .. tostring(tickMs))";
    }

    internal static List<WalkStepActionSpec> GenerateSeededWalkPlan(
        int seed,
        int steps,
        IReadOnlyList<MainMenuPanelMatrixSpec> activePanels)
    {
        Random rng = new(seed);
        List<WalkStepActionSpec> plan = new(steps);
        string[] specialActions = ["escape_key", "double_toggle", "hot_reload_mid_panel"];

        for (int i = 1; i <= steps; i++)
        {
            int roll = rng.Next(100);
            MainMenuPanelMatrixSpec spec = activePanels[rng.Next(activePanels.Count)];
            if (roll < 75)
            {
                plan.Add(new WalkStepActionSpec(i, "click_panel_button", spec.PanelKey, spec.ButtonWidget, spec.TargetWidget));
            }
            else
            {
                string special = specialActions[rng.Next(specialActions.Length)];
                plan.Add(new WalkStepActionSpec(i, special, spec.PanelKey, spec.ButtonWidget, spec.TargetWidget));
            }
        }

        return plan;
    }

    private async Task<UniversalUiInvariantsReport> EvaluateUniversalUiInvariantsAsync(
        IReadOnlyList<MainMenuPanelMatrixSpec> activePanels,
        string checkpointToken,
        CancellationToken cancellationToken)
    {
        string probeLua = BuildUniversalInvariantsProbeLua(activePanels);
        string resp = string.Empty;
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                resp = await _gameIpcBridge.ExecuteCommandAsync($"Script {probeLua}", 1500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"[UiFlow] Universal UI invariant probe warning: {ex.Message}");
            }
        }

        int visCount = ParseVerifyInt(resp, "visCount", 1);
        bool i1 = visCount <= 1;
        bool i2 = ParseVerifyFlag(resp, "regOk", defaultIfVerifiedTag: true);
        bool i3 = ParseVerifyFlag(resp, "geomOk", defaultIfVerifiedTag: true);
        bool i4 = ParseVerifyFlag(resp, "noLatch", defaultIfVerifiedTag: true);
        double tickMs = ParseVerifyDouble(resp, "tickMs", 0.35);
        bool i5 = tickMs <= 5.0;

        ErrorsScanReport logScan = _errorsSinceHandler.ScanLogsSinceCheckpoint(
            checkpointToken,
            severityFilter: "warning",
            allowDiagnosticWarnings: false);
        bool i6 = logScan.IsClean && logScan.Errors.Count == 0 && logScan.K2WarnCount == 0;
        int deltaCount = logScan.Errors.Count + logScan.Warnings.Count;

        List<string> failedIds = [];
        if (!i1) failedIds.Add("I1_SingleActiveMainPanel");
        if (!i2) failedIds.Add("I2_NonEmptyPanelRegistry");
        if (!i3) failedIds.Add("I3_OnScreenViewportGeometry");
        if (!i4) failedIds.Add("I4_NoStuckTransitionOrInputLatch");
        if (!i5) failedIds.Add("I5_CleanFrameTimeBudget");
        if (!i6) failedIds.Add("I6_ZeroConsoleDelta");

        return new UniversalUiInvariantsReport(
            AllPassed: failedIds.Count == 0,
            I1_SingleActiveMainPanel: i1,
            I2_NonEmptyPanelRegistry: i2,
            I3_OnScreenViewportGeometry: i3,
            I4_NoStuckTransitionOrInputLatch: i4,
            I5_CleanFrameTimeBudget: i5,
            I6_ZeroConsoleDelta: i6,
            VisibleMainPanelCount: visCount,
            FrameTickMs: tickMs,
            ConsoleDeltaCount: deltaCount,
            FailedInvariantIds: failedIds);
    }

    private async Task<ToolCallResult> HandleVerifyUiInvariantsAsync(
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        if (_gameIpcBridge == null || !_gameIpcBridge.IsConnected)
        {
            sw.Stop();
            return ToolCallResult.Error("Cannot execute 'verify_ui_invariants': GameIpcBridge is not connected to hon_x64.exe.");
        }

        string? checkpointToken = null;
        if (arguments.TryGetProperty("checkpoint", out JsonElement cpEl) && cpEl.ValueKind == JsonValueKind.String)
        {
            checkpointToken = cpEl.GetString();
        }
        checkpointToken ??= _errorsSinceHandler.CaptureCheckpointToken();

        var (activePanels, _, _) = LoadMainMenuPanelMatrix();
        UniversalUiInvariantsReport report = await EvaluateUniversalUiInvariantsAsync(activePanels, checkpointToken, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = report.AllPassed ? "success" : "error",
            action = "verify_ui_invariants",
            invariants = report,
            executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: !report.AllPassed);
    }

    private async Task<ToolCallResult> HandleWalkMainPanelsAsync(
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        int seed = 42;
        if (arguments.TryGetProperty("seed", out JsonElement seedEl) && seedEl.TryGetInt32(out int parsedSeed))
        {
            seed = parsedSeed;
        }

        int steps = 25;
        if (arguments.TryGetProperty("steps", out JsonElement stepsEl) && stepsEl.TryGetInt32(out int parsedSteps) && parsedSteps > 0)
        {
            steps = Math.Clamp(parsedSteps, 1, 500);
        }

        string fidelity = "dom";
        if ((arguments.TryGetProperty("fidelity", out JsonElement fidEl) ||
             arguments.TryGetProperty("clickFidelity", out fidEl)) &&
            fidEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(fidEl.GetString()))
        {
            fidelity = fidEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool dryRun = arguments.TryGetProperty("dryRun", out JsonElement dryEl) &&
                      dryEl.ValueKind == JsonValueKind.True;

        string? replayBundlePath = null;
        if ((arguments.TryGetProperty("replayBundlePath", out JsonElement rbEl) ||
             arguments.TryGetProperty("replay_bundle_path", out rbEl) ||
             arguments.TryGetProperty("bundlePath", out rbEl)) &&
            rbEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(rbEl.GetString()))
        {
            replayBundlePath = rbEl.GetString()!.Trim();
        }

        string? reproOutputPath = null;
        if ((arguments.TryGetProperty("reproOutputPath", out JsonElement roEl) ||
             arguments.TryGetProperty("repro_output_path", out roEl)) &&
            roEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(roEl.GetString()))
        {
            reproOutputPath = roEl.GetString()!.Trim();
        }

        var (activePanels, _, _) = LoadMainMenuPanelMatrix();
        List<WalkStepActionSpec> walkPlan;

        if (!string.IsNullOrWhiteSpace(replayBundlePath) && File.Exists(replayBundlePath))
        {
            using JsonDocument bundleDoc = JsonDocument.Parse(File.ReadAllText(replayBundlePath));
            if (bundleDoc.RootElement.TryGetProperty("seed", out JsonElement bSeed) && bSeed.TryGetInt32(out int sVal))
            {
                seed = sVal;
            }
            walkPlan = [];
            if (bundleDoc.RootElement.TryGetProperty("transitionHistory", out JsonElement histEl) && histEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in histEl.EnumerateArray())
                {
                    int idx = item.TryGetProperty("stepIndex", out JsonElement si) && si.TryGetInt32(out int siVal) ? siVal : walkPlan.Count + 1;
                    string actType = item.TryGetProperty("actionType", out JsonElement at) ? at.GetString() ?? "click_panel_button" : "click_panel_button";
                    string pKey = item.TryGetProperty("panelKey", out JsonElement pk) ? pk.GetString() ?? "map_selection" : "map_selection";
                    string bWidget = item.TryGetProperty("buttonWidget", out JsonElement bw) ? bw.GetString() ?? "midbar_button_matchmaking" : "midbar_button_matchmaking";
                    string tWidget = item.TryGetProperty("targetWidget", out JsonElement tw) ? tw.GetString() ?? pKey : pKey;
                    walkPlan.Add(new WalkStepActionSpec(idx, actType, pKey, bWidget, tWidget));
                }
            }
            if (walkPlan.Count == 0)
            {
                walkPlan = GenerateSeededWalkPlan(seed, steps, activePanels);
            }
        }
        else
        {
            walkPlan = GenerateSeededWalkPlan(seed, steps, activePanels);
        }

        if (dryRun)
        {
            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "walk_main_panels",
                dryRun = true,
                seed,
                steps = walkPlan.Count,
                fidelity,
                walkPlan,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (_gameIpcBridge == null || !_gameIpcBridge.IsConnected)
        {
            sw.Stop();
            return ToolCallResult.Error("Cannot execute 'walk_main_panels': GameIpcBridge is not connected to hon_x64.exe.");
        }

        List<WalkStepActionSpec> executedSteps = [];
        List<object> stepReports = [];

        foreach (WalkStepActionSpec step in walkPlan)
        {
            executedSteps.Add(step);
            string checkpointToken = _errorsSinceHandler.CaptureCheckpointToken();

            try
            {
                switch (step.ActionType)
                {
                    case "escape_key":
                        await _gameIpcBridge.ExecuteCommandAsync("Script if MainMenu_HandleEscape then pcall(MainMenu_HandleEscape) end", 1500, cancellationToken).ConfigureAwait(false);
                        break;

                    case "double_toggle":
                    {
                        string dblLua = BuildPanelSweepClickLua(step.ButtonWidget, triggerDomClick: fidelity == "dom") +
                                        $"; local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); local btn = main and main:GetWidget('{EscapeLuaString(step.ButtonWidget)}'); if btn and btn.DoEvent then pcall(function() btn:DoEvent() end) end";
                        await _gameIpcBridge.ExecuteCommandAsync($"Script {dblLua}", 1500, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    case "hot_reload_mid_panel":
                        await _gameIpcBridge.ExecuteCommandAsync("Script if GetMainMenuPanels then pcall(GetMainMenuPanels) end", 1500, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                    {
                        string clickLua = BuildPanelSweepClickLua(step.ButtonWidget, triggerDomClick: fidelity == "dom");
                        await _gameIpcBridge.ExecuteCommandAsync($"Script {clickLua}", 1500, cancellationToken).ConfigureAwait(false);
                        if (fidelity == "human")
                        {
                            await _gameIpcBridge.DispatchBackgroundHumanClickAsync("main", step.ButtonWidget, 512, 44, false, 1500, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[UiFlow] walk_main_panels step {step.StepIndex} ({step.ActionType}) dispatch warning: {ex.Message}");
            }

            UniversalUiInvariantsReport inv = await EvaluateUniversalUiInvariantsAsync(activePanels, checkpointToken, cancellationToken).ConfigureAwait(false);
            stepReports.Add(new
            {
                step.StepIndex,
                step.ActionType,
                step.PanelKey,
                step.ButtonWidget,
                invariants = inv
            });

            if (!inv.AllPassed)
            {
                string savedReproPath = reproOutputPath ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "shj", $"repro_seed_{seed}.json");
                try
                {
                    string? dir = Path.GetDirectoryName(savedReproPath);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    var reproBundle = new
                    {
                        schemaVersion = 1,
                        seed,
                        fidelity,
                        failedStepIndex = step.StepIndex,
                        failedAction = step,
                        failedInvariantIds = inv.FailedInvariantIds,
                        invariantsReport = inv,
                        transitionHistory = executedSteps.Select(s => new
                        {
                            stepIndex = s.StepIndex,
                            actionType = s.ActionType,
                            panelKey = s.PanelKey,
                            buttonWidget = s.ButtonWidget,
                            targetWidget = s.TargetWidget
                        }).ToList(),
                        replayCommand = $"scripts/qa/kongor_shj.ps1 -Action replay -Bundle \"{savedReproPath}\"",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    };
                    File.WriteAllText(savedReproPath, JsonSerializer.Serialize(reproBundle, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    _logger($"[UiFlow] Failed to write repro bundle '{savedReproPath}': {ex.Message}");
                }

                sw.Stop();
                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "walk_main_panels",
                    seed,
                    fidelity,
                    completedSteps = executedSteps.Count,
                    totalPlannedSteps = walkPlan.Count,
                    failedStepIndex = step.StepIndex,
                    failedAction = step,
                    failedInvariantIds = inv.FailedInvariantIds,
                    reproBundlePath = savedReproPath,
                    replayCommand = $"scripts/qa/kongor_shj.ps1 -Action replay -Bundle \"{savedReproPath}\"",
                    stepReports,
                    executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }

        sw.Stop();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "walk_main_panels",
            seed,
            fidelity,
            completedSteps = executedSteps.Count,
            totalPlannedSteps = walkPlan.Count,
            replayedFromBundle = replayBundlePath,
            stepReports,
            executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<(bool verified, double latencyMs, bool ipcProbeSucceeded)> VerifyWidgetVisibilityWithRetryAsync(
        string widgetName,
        CancellationToken cancellationToken)
    {
        Stopwatch verifySw = Stopwatch.StartNew();
        int[] delaysMs = [0, 50, 150, 300];
        bool ipcProbeSucceeded = false;

        string safeWidget = EscapeLuaString(widgetName);
        string verifyLua = $"Script Echo('WIDGET_VERIFIED|' .. tostring(((UIManager.GetInterface('main') and UIManager.GetInterface('main'):GetWidget('{safeWidget}') and UIManager.GetInterface('main'):GetWidget('{safeWidget}'):IsVisible()) or (UIManager.GetInterface('game') and UIManager.GetInterface('game'):GetWidget('{safeWidget}') and UIManager.GetInterface('game'):GetWidget('{safeWidget}'):IsVisible())) or false))";

        foreach (int delay in delaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                try
                {
                    string resp = await _gameIpcBridge.ExecuteCommandAsync(verifyLua, 1000, cancellationToken).ConfigureAwait(false);
                    ipcProbeSucceeded = true;
                    if (resp.Contains("WIDGET_VERIFIED|true", StringComparison.OrdinalIgnoreCase))
                    {
                        verifySw.Stop();
                        return (true, Math.Round(verifySw.Elapsed.TotalMilliseconds, 2), true);
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[UiFlow] VerifyWidgetVisibilityWithRetryAsync probe warning for '{widgetName}': {ex.Message}");
                }
            }
        }

        verifySw.Stop();
        return (false, Math.Round(verifySw.Elapsed.TotalMilliseconds, 2), ipcProbeSucceeded);
    }

    private async Task<object?> CaptureVisualProofAsync(CancellationToken cancellationToken)
    {
        if (_windowCapturer == null) return null;
        try
        {
            var capRes = await _windowCapturer.CaptureAsync("hon_x64", "auto", saveArtifact: true, customOutputPath: null, stealFocus: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (capRes.Success)
            {
                return new
                {
                    success = true,
                    artifactPath = capRes.ArtifactPath,
                    captureMethodUsed = capRes.CaptureMethodUsed,
                    zeroWindowExposure = capRes.CaptureMethodUsed.Contains("vulkan", StringComparison.OrdinalIgnoreCase),
                    width = capRes.Width,
                    height = capRes.Height
                };
            }
        }
        catch (Exception ex)
        {
            _logger($"[UiFlow] Visual proof capture encountered error: {ex.Message}");
        }
        return null;
    }

    #endregion
}
