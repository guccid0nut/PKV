// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: K2UiContracts.cs
// Subsystem: ProjectKongor Client UI & K2 Scripting
// Description: Strongly-typed contracts, widget references, and interfaces
//              for compile-time safe K2 UI automation and IPC dispatch.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUS.Contracts.K2;

/// <summary>
/// Execution delegate for running a Lua script against the K2 game client engine.
/// </summary>
public delegate Task<string> K2LuaExecutor(string luaScript, int timeoutMs, CancellationToken ct);

/// <summary>
/// Strongly-typed reference to a specific K2 UI Widget within an Interface context.
/// Guarantees compile-time type safety and eliminates fragile raw string concatenation.
/// </summary>
public sealed class CWidgetRef
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName { get; }
    public string WidgetName { get; }

    public CWidgetRef(string interfaceName, string widgetName, K2LuaExecutor executor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetName);
        ArgumentNullException.ThrowIfNull(executor);

        InterfaceName = interfaceName;
        WidgetName = widgetName;
        _executor = executor;
    }

    /// <summary>
    /// Returns the canonical Lua expression referencing this widget via UIManager.
    /// Invariant: Always uses UIManager.GetInterface, never 'Windows'.
    /// </summary>
    public string ToLuaExpression()
        => $"UIManager.GetInterface('{InterfaceName}'):GetWidget('{WidgetName}')";

    /// <summary>
    /// Returns the canonical, safe Lua script to trigger an event on this widget.
    /// Invariant: DoEvent() takes NO string arguments.
    /// </summary>
    public string ToLuaDoEvent(int? eventIndex = null)
    {
        return eventIndex.HasValue
            ? $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.DoEventN then w:DoEventN({eventIndex.Value}) end end"
            : $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.DoEvent then w:DoEvent() end end";
    }

    /// <summary>
    /// Returns the canonical safe Lua script to update the widget's visibility.
    /// </summary>
    public string ToLuaSetVisible(bool visible)
        => $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.SetVisible then w:SetVisible({(visible ? "true" : "false")}) end end";

    /// <summary>
    /// Returns the canonical safe Lua script to update the widget's display text.
    /// </summary>
    public string ToLuaSetText(string text)
    {
        string escaped = (text ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
        return $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.SetText then w:SetText('{escaped}') end end";
    }

    /// <summary>
    /// Returns the canonical safe Lua script to update the widget's value.
    /// </summary>
    public string ToLuaSetValue(string value)
    {
        string escaped = (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
        return $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.SetValue then w:SetValue('{escaped}') end end";
    }

    /// <summary>
    /// Triggers the widget's primary XML event handler (onevent) with zero parameters.
    /// </summary>
    public Task<string> DoEventAsync(int timeoutMs = 3000, CancellationToken ct = default)
        => _executor(ToLuaDoEvent(), timeoutMs, ct);

    /// <summary>
    /// Triggers an indexed XML event handler (onevent0, onevent1, etc.) on this widget.
    /// </summary>
    public Task<string> DoEventNAsync(int index, int timeoutMs = 3000, CancellationToken ct = default)
        => _executor(ToLuaDoEvent(index), timeoutMs, ct);

    /// <summary>
    /// Shows or hides the widget.
    /// </summary>
    public Task<string> SetVisibleAsync(bool visible, int timeoutMs = 3000, CancellationToken ct = default)
        => _executor(ToLuaSetVisible(visible), timeoutMs, ct);

    /// <summary>
    /// Updates the display text for text-capable widgets (labels, buttons, textboxes).
    /// </summary>
    public Task<string> SetTextAsync(string text, int timeoutMs = 3000, CancellationToken ct = default)
        => _executor(ToLuaSetText(text), timeoutMs, ct);

    /// <summary>
    /// Updates the value for value-based widgets (comboboxes, textboxes, sliders).
    /// </summary>
    public Task<string> SetValueAsync(string value, int timeoutMs = 3000, CancellationToken ct = default)
        => _executor(ToLuaSetValue(value), timeoutMs, ct);

    /// <summary>
    /// Triggers the primary input action on this widget.
    /// </summary>
    public Task<string> TriggerAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        string script = $"local iface = UIManager.GetInterface('{InterfaceName}'); if iface then local w = iface:GetWidget('{WidgetName}'); if w and w.Trigger then w:Trigger() end end";
        return _executor(script, timeoutMs, ct);
    }
}

/// <summary>
/// Strongly-typed contract for the Main Menu & Login UI Interface ('main').
/// </summary>
public sealed class MainUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "main";

    public CWidgetRef SysbarQuickStart { get; }
    public CWidgetRef MainMenuPanelSwitcher { get; }
    public CWidgetRef PreloginLower { get; }
    public CWidgetRef StoreContainer { get; }
    public CWidgetRef SystemBar { get; }
    public CWidgetRef BtnPlayNow { get; }
    public CWidgetRef SysbarLogged { get; }
    public CWidgetRef LobbyContainer { get; }
    public CWidgetRef CompendiumContainer { get; }

    public MainUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        SysbarQuickStart = new CWidgetRef("main", "sysbar_quickStart", executor);
        MainMenuPanelSwitcher = new CWidgetRef("main", "MainMenuPanelSwitcher", executor);
        PreloginLower = new CWidgetRef("main", "prelogin_lower", executor);
        StoreContainer = new CWidgetRef("main", "store_container2", executor);
        SystemBar = new CWidgetRef("main", "systembar", executor);
        BtnPlayNow = new CWidgetRef("main", "btnPlayNow", executor);
        SysbarLogged = new CWidgetRef("main", "sysbar_logged", executor);
        LobbyContainer = new CWidgetRef("main", "lobby_container", executor);
        CompendiumContainer = new CWidgetRef("main", "compendium", executor);
    }
}

/// <summary>
/// Strongly-typed contract for the In-Match Gameplay HUD Interface ('game').
/// </summary>
public sealed class GameUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "game";

    public CWidgetRef MiniMap { get; }
    public CWidgetRef SkillPanel { get; }
    public CWidgetRef InventoryPanel { get; }
    public CWidgetRef ChatInput { get; }
    public CWidgetRef HeroHealthBar { get; }
    public CWidgetRef HeroManaBar { get; }
    public CWidgetRef Scoreboard { get; }

    public GameUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        MiniMap = new CWidgetRef("game", "minimap", executor);
        SkillPanel = new CWidgetRef("game", "ability_panel", executor);
        InventoryPanel = new CWidgetRef("game", "inventory_panel", executor);
        ChatInput = new CWidgetRef("game", "game_chat_input", executor);
        HeroHealthBar = new CWidgetRef("game", "hero_health", executor);
        HeroManaBar = new CWidgetRef("game", "hero_mana", executor);
        Scoreboard = new CWidgetRef("game", "game_scoreboard", executor);
    }
}

/// <summary>
/// Strongly-typed contract for the Replay Studio & Playback Control Interface ('game_replay_control').
/// </summary>
public sealed class ReplayControlUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "game_replay_control";

    public CWidgetRef PlayPauseButton { get; }
    public CWidgetRef SpeedSlider { get; }
    public CWidgetRef Timeline { get; }
    public CWidgetRef CameraFollowToggle { get; }
    public CWidgetRef RewindButton { get; }
    public CWidgetRef FastForwardButton { get; }

    public ReplayControlUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        PlayPauseButton = new CWidgetRef("game_replay_control", "btn_play_pause", executor);
        SpeedSlider = new CWidgetRef("game_replay_control", "slider_speed", executor);
        Timeline = new CWidgetRef("game_replay_control", "slider_timeline", executor);
        CameraFollowToggle = new CWidgetRef("game_replay_control", "btn_camera_follow", executor);
        RewindButton = new CWidgetRef("game_replay_control", "btn_rewind", executor);
        FastForwardButton = new CWidgetRef("game_replay_control", "btn_fast_forward", executor);
    }
}

/// <summary>
/// Strongly-typed contract for the Match Spectator Interface ('game_spectator').
/// </summary>
public sealed class SpectatorUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "game_spectator";

    public CWidgetRef GoldGraph { get; }
    public CWidgetRef XpGraph { get; }
    public CWidgetRef StatsTable { get; }
    public CWidgetRef CasterPanel { get; }
    public CWidgetRef MinimapOverview { get; }

    public SpectatorUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        GoldGraph = new CWidgetRef("game_spectator", "graph_gold", executor);
        XpGraph = new CWidgetRef("game_spectator", "graph_xp", executor);
        StatsTable = new CWidgetRef("game_spectator", "table_player_stats", executor);
        CasterPanel = new CWidgetRef("game_spectator", "panel_caster", executor);
        MinimapOverview = new CWidgetRef("game_spectator", "spec_minimap", executor);
    }
}

/// <summary>
/// Strongly-typed contract for the Player Stats & Match History Interface ('player_stats_v2' / 'profile').
/// </summary>
public sealed class ProfileStatsUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "player_stats_v2";

    public CWidgetRef MatchHistoryList { get; }
    public CWidgetRef StatsOverviewPanel { get; }
    public CWidgetRef RankedRatingLabel { get; }
    public CWidgetRef HeroPerformanceGrid { get; }
    public CWidgetRef MatchDetailContainer { get; }
    public CWidgetRef BtnRefreshStats { get; }

    public ProfileStatsUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        MatchHistoryList = new CWidgetRef("player_stats_v2", "match_history_list", executor);
        StatsOverviewPanel = new CWidgetRef("player_stats_v2", "stats_overview_panel", executor);
        RankedRatingLabel = new CWidgetRef("player_stats_v2", "ranked_mmr_label", executor);
        HeroPerformanceGrid = new CWidgetRef("player_stats_v2", "hero_stats_grid", executor);
        MatchDetailContainer = new CWidgetRef("player_stats_v2", "match_detail_panel", executor);
        BtnRefreshStats = new CWidgetRef("player_stats_v2", "btn_refresh_stats", executor);
    }
}

/// <summary>
/// Strongly-typed contract for the Learnatorium & Compendium Interface ('learnatorium').
/// </summary>
public sealed class LearnatoriumUiInterface
{
    private readonly K2LuaExecutor _executor;

    public string InterfaceName => "learnatorium";

    public CWidgetRef HeroRosterGrid { get; }
    public CWidgetRef HeroDetailsPanel { get; }
    public CWidgetRef ItemsGuideGrid { get; }
    public CWidgetRef MechanicsTutorialList { get; }
    public CWidgetRef VideoPlayerContainer { get; }
    public CWidgetRef BtnPracticeHero { get; }

    public LearnatoriumUiInterface(K2LuaExecutor executor)
    {
        _executor = executor;
        HeroRosterGrid = new CWidgetRef("learnatorium", "learnatorium_hero_grid", executor);
        HeroDetailsPanel = new CWidgetRef("learnatorium", "learnatorium_hero_details", executor);
        ItemsGuideGrid = new CWidgetRef("learnatorium", "learnatorium_items_grid", executor);
        MechanicsTutorialList = new CWidgetRef("learnatorium", "learnatorium_tutorials_list", executor);
        VideoPlayerContainer = new CWidgetRef("learnatorium", "learnatorium_video_player", executor);
        BtnPracticeHero = new CWidgetRef("learnatorium", "btn_practice_hero", executor);
    }
}

/// <summary>
/// Root entry point aggregating all typed K2 UI interfaces under a unified, discoverable facade.
/// </summary>
public sealed class K2UiContractsRoot
{
    private readonly K2LuaExecutor _executor;

    public MainUiInterface Main { get; }
    public GameUiInterface Game { get; }
    public ReplayControlUiInterface ReplayControl { get; }
    public SpectatorUiInterface Spectator { get; }
    public ProfileStatsUiInterface ProfileStats { get; }
    public LearnatoriumUiInterface Learnatorium { get; }

    public K2UiContractsRoot(K2LuaExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Main = new MainUiInterface(_executor);
        Game = new GameUiInterface(_executor);
        ReplayControl = new ReplayControlUiInterface(_executor);
        Spectator = new SpectatorUiInterface(_executor);
        ProfileStats = new ProfileStatsUiInterface(_executor);
        Learnatorium = new LearnatoriumUiInterface(_executor);
    }

    /// <summary>
    /// Create a dynamic typed widget reference for any custom or modded widget.
    /// </summary>
    public CWidgetRef Widget(string interfaceName, string widgetName)
        => new(interfaceName, widgetName, _executor);
}
