// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Services;
using MediatR;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Delegate signature for MCP Tool Handlers.
/// </summary>
public delegate Task<ToolCallResult> ToolHandlerDelegate(JsonElement arguments, CancellationToken cancellationToken);

/// <summary>
/// Tool entry within the registry.
/// </summary>
public sealed record ToolRegistration(ToolDefinition Definition, ToolHandlerDelegate Handler, McpToolCategory Category = McpToolCategory.Default);

internal sealed class ToolCallOutcomeStats
{
    public int TotalCalls;
    public int TotalErrors;
    public int FirstCallRecorded;
    public bool FirstCallSucceeded;
}

internal sealed record FirstCallScoreboardSnapshot(
    int DistinctToolsCalled,
    int FirstCallSuccesses,
    int FirstCallFailures,
    double FirstCallSuccessRate,
    int TotalToolCalls,
    int TotalToolErrors,
    IReadOnlyDictionary<string, bool> FirstCallOutcomesByTool);

internal sealed record McpIssueKbEntry(
    string Id,
    string Tool,
    string Category,
    string Title,
    string Status,
    int Occurrences,
    string PreventionRule);

/// <summary>
/// Central registry for MCP tools with built-in schemas for all ProjectKongor tools.
/// </summary>
public sealed partial class McpToolRegistry
{
    private readonly IMcpRateLimiter _rateLimiter;
    partial void InitializeGameLifecycleHandlers(TimeProvider timeProvider);
    partial void RegisterGameLifecycleTools();
    partial void InitializeLuaUiHandlers(TimeProvider timeProvider);
    partial void RegisterLuaUiTools();
    partial void InitializeVfsAssetsHandlers(TimeProvider timeProvider);
    partial void RegisterVfsAssetsTools();
    partial void InitializeForensicsHandlers(TimeProvider timeProvider);
    partial void RegisterForensicsTools();
    partial void InitializeDedicatedServerHandlers(TimeProvider timeProvider);
    partial void RegisterDedicatedServerTools();
    partial void InitializeControlFabricHandlers(TimeProvider timeProvider);
    partial void RegisterControlFabricTools();
    private readonly ConcurrentDictionary<string, ToolRegistration> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Hidden aliases registered via <see cref="RegisterAlias" />: alias name -> canonical tool name.
    ///     Aliases stay callable (HasTool / GetTool / ExecuteToolAsync) but are NOT advertised by <see cref="ListTools" />,
    ///     so agents see one tool per capability instead of 2-4 identical copies.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _isBundledMode;
    private int _bundledSkippedCount;
    public bool IsBundledMode => _isBundledMode;
    public bool IncludeSidecars { get; set; }
    public WorkerRoeAndCircuitBreakerGuard WorkerRoeGuard { get; } = new();

    public static readonly IReadOnlySet<string> CoreToolsetAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "kongor_game_client_manage",
        "kongor_client_eval_lua",
        "kongor_game_command",
        "kongor_client_practice",
        "kongor_reload_and_verify",
        "kongor_errors_since",
        "kongor_capture_window",
        "kongor_ui_control",
        "kongor_client_navigate",
        "kongor_k2_ui_inspect",
        "kongor_wait_for",
        "kongor_vfs_resolve",
        "kongor_vfs_locate",
        "kongor_sync_mirrors",
        "kongor_server_manage",
        "kongor_game_server_command",
        "kongor_health_check",
        "kongor_call_tool"
    };

    public static readonly HashSet<string> BundledSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "kongor_health_check",
        "kongor_errors_since",
        "kongor_enable_toolset",
        "kongor_call_tool",
        "kongor_vfs_resolve",
        "kongor_vfs_locate",
        "kongor_get_client_logs",
        "kongor_log_correlate",
        "kongor_rate_limit_status",
        "kongor_diagnostics",
        "kongor_tao_capabilities",
        "kongor_perf_window",
        "kongor_k2_ui_inspect",
        "kongor_asset_inspect",
        "kongor_k2_lint",
        "kongor_engine_inspect",
        "kongor_vfs_mount_graph",
        "kongor_texture_preview",
        "kongor_material_inspect",
        "kongor_validate_previews",
        "kongor_inspect_model_mdf",
        "kongor_inspect_hero_pool",
        "kongor_vfs_completeness_audit",
        "kongor_server_manage",
        "kongor_compel_manage",
        "kongor_willowmaker_manage",
        "kongor_game_client_manage",
        "kongor_game_server_manage",
        "kongor_capture_window",
        "kongor_client_eval_lua",
        "kongor_ui_control",
        "kongor_db_pool_diagnostics"
    };

    public static readonly HashSet<string> QuarantinedSimulatedTools = new(StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> BundledUnsafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "kongor_db_migrate", "kongor_db_fixture",
        "kongor_app_eval"
    };
    private readonly TimeProvider _timeProvider;
    private readonly IMediator? _mediator;
    private readonly ILifecycleService _lifecycleService;
    private readonly ServerManageHandler _serverManageHandler;
    private readonly GameClientManageHandler _gameClientManageHandler;
    private readonly WillowmakerManageHandler _willowmakerManageHandler;
    private readonly CompelManageHandler _compelManageHandler;
    private readonly GameServerManageHandler _gameServerManageHandler;
    private readonly IGameLauncherService _gameLauncherService;
    private readonly IGameIpcBridge _gameIpcBridge;
    private readonly GameManageHandler _gameManageHandler;
    private readonly GameCommandHandler _gameCommandHandler;
    private readonly GameTelemetryHandler _gameTelemetryHandler;
    private readonly IWindowCapturer _windowCapturer;
    private readonly IHeadlessUiRenderer _headlessUiRenderer;
    private readonly IUiControlService _uiControlService;
    private readonly CaptureWindowHandler _captureWindowHandler;
    private readonly UiRenderViewHandler _uiRenderViewHandler;
    private readonly UiControlHandler _uiControlHandler;
    private readonly IK2UiInspectService _k2UiInspectService;
    private readonly K2UiInspectHandler _k2UiInspectHandler;
    private readonly MatchLaunchHandler _matchLaunchHandler;
    private readonly IAssetInspectService _assetInspectService;
    private readonly AssetInspectHandler _assetInspectHandler;
    private readonly IEngineCacheService _engineCacheService;
    private readonly EngineCachePurgeHandler _engineCachePurgeHandler;
    private readonly IClusterDiagnosticsService _clusterDiagnosticsService;
    private readonly ClusterDiagnosticsHandler _clusterDiagnosticsHandler;
    private readonly GameLuaHandler _gameLuaHandler;
    private readonly IClientCommandRemediationService _clientCommandRemediationService;
    private readonly MatchStateHandler _matchStateHandler;
    private readonly GameActionHandler _gameActionHandler;
    private readonly ReplayInspectHandler _replayInspectHandler;
    private readonly ReplayPlaybackHandler _replayPlaybackHandler;
    private readonly DbFixtureHandler _dbFixtureHandler;
    private readonly VfsResolveHandler _vfsResolveHandler;
    private readonly ModBuildHandler _modBuildHandler;
    private readonly AudioPreviewHandler _audioPreviewHandler;
    private readonly IK2LintService _k2LintService;
    private readonly K2LintHandler _k2LintHandler;
    private readonly IEngineInspectService _engineInspectService;
    private readonly EngineInspectHandler _engineInspectHandler;
    private readonly IHotReloadService _hotReloadService;
    private readonly HotReloadHandler _hotReloadHandler;
    private readonly IMatchmakingSimulateService _matchmakingSimulateService;
    private readonly MatchmakingSimulateHandler _matchmakingSimulateHandler;
    private readonly GameSnapshotHandler _gameSnapshotHandler;
    private readonly ScrimmageLaunchHandler _scrimmageLaunchHandler;
    private readonly UiFlowHandler _uiFlowHandler;
    private readonly TelemetryStreamHandler _telemetryStreamHandler;
    private readonly DbMigrationTestHandler _dbMigrationTestHandler;
    private readonly MemoryLeakDetectHandler _memoryLeakDetectHandler;
    private readonly CrashDumpAnalyzeHandler _crashDumpAnalyzeHandler;
    private readonly BotChoreographyHandler _botChoreographyHandler;
    private readonly NetworkImpairSimulateHandler _networkImpairSimulateHandler;
    private readonly CacheCoherenceVerifyHandler _cacheCoherenceVerifyHandler;
    private readonly UiVisualDiffHandler _uiVisualDiffHandler;
    private readonly NativeManageHandler _nativeManageHandler;
    private readonly VulkanManageHandler _vulkanManageHandler;
    private readonly ClientNavigateHandler _clientNavigateHandler;
    private readonly ClientPracticeHandler _clientPracticeHandler;
    private readonly VectorSearchHandler _vectorSearchHandler;
    private readonly ITaoClient _taoClient;
    private readonly TaoCapabilitiesHandler _taoCapabilitiesHandler;
    private readonly ErrorsSinceHandler _errorsSinceHandler;
    private readonly HealthCheckHandler _healthCheckHandler;
    private readonly WaitForHandler _waitForHandler;
    private readonly AssertUiHandler _assertUiHandler;
    private readonly ReloadAndVerifyHandler _reloadAndVerifyHandler;
    private readonly PerfWindowHandler _perfWindowHandler;
    private readonly EnableToolsetHandler _enableToolsetHandler;
    private readonly CallToolHandler _callToolHandler;
    private readonly bool _hasInjectedMockCollaborators;
    private readonly HashSet<string> _runtimeEnabledToolsets = new(StringComparer.OrdinalIgnoreCase);
    private string? _configuredDefaultToolsets;
    private readonly object _toolsetLock = new();

    public event Action? ToolListChanged;

    public McpToolRegistry(
        bool registerDefaults = true,
        TimeProvider? timeProvider = null,
        ILifecycleService? lifecycleService = null,
        IGameLauncherService? gameLauncherService = null,
        IGameIpcBridge? gameIpcBridge = null,
        IWindowCapturer? windowCapturer = null,
        IHeadlessUiRenderer? headlessUiRenderer = null,
        IUiControlService? uiControlService = null,
        GameClientManageHandler? gameClientManageHandler = null,
        WillowmakerManageHandler? willowmakerManageHandler = null,
        CompelManageHandler? compelManageHandler = null,
        GameServerManageHandler? gameServerManageHandler = null,
        GameLuaHandler? gameLuaHandler = null,
        IK2UiInspectService? k2UiInspectService = null,
        K2UiInspectHandler? k2UiInspectHandler = null,
        MatchLaunchHandler? matchLaunchHandler = null,
        IAssetInspectService? assetInspectService = null,
        AssetInspectHandler? assetInspectHandler = null,
        IEngineCacheService? engineCacheService = null,
        EngineCachePurgeHandler? engineCachePurgeHandler = null,
        IClusterDiagnosticsService? clusterDiagnosticsService = null,
        ClusterDiagnosticsHandler? clusterDiagnosticsHandler = null,
        IClientCommandRemediationService? clientCommandRemediationService = null,
        IHotReloadService? hotReloadService = null,
        HotReloadHandler? hotReloadHandler = null,
        IMatchmakingSimulateService? matchmakingSimulateService = null,
        MatchmakingSimulateHandler? matchmakingSimulateHandler = null,
        GameSnapshotHandler? gameSnapshotHandler = null,
        ScrimmageLaunchHandler? scrimmageLaunchHandler = null,
        UiFlowHandler? uiFlowHandler = null,
        TelemetryStreamHandler? telemetryStreamHandler = null,
        DbMigrationTestHandler? dbMigrationTestHandler = null,
        MemoryLeakDetectHandler? memoryLeakDetectHandler = null,
        CrashDumpAnalyzeHandler? crashDumpAnalyzeHandler = null,
        BotChoreographyHandler? botChoreographyHandler = null,
        NetworkImpairSimulateHandler? networkImpairSimulateHandler = null,
        CacheCoherenceVerifyHandler? cacheCoherenceVerifyHandler = null,
        UiVisualDiffHandler? uiVisualDiffHandler = null,
        NativeManageHandler? nativeManageHandler = null,
        VulkanManageHandler? vulkanManageHandler = null,
        ClientNavigateHandler? clientNavigateHandler = null,
        ClientPracticeHandler? clientPracticeHandler = null,
        VectorSearchHandler? vectorSearchHandler = null,
        GameActionHandler? gameActionHandler = null,
        AuditStatsHandler? auditStatsHandler = null,
        ITaoClient? taoClient = null,
        TaoCapabilitiesHandler? taoCapabilitiesHandler = null,
        ErrorsSinceHandler? errorsSinceHandler = null,
        HealthCheckHandler? healthCheckHandler = null,
        WaitForHandler? waitForHandler = null,
        AssertUiHandler? assertUiHandler = null,
        ReloadAndVerifyHandler? reloadAndVerifyHandler = null,
        PerfWindowHandler? perfWindowHandler = null,
        IMediator? mediator = null,
        IMcpRateLimiter? rateLimiter = null,
        string? bundleProbeDirectory = null)
    {
        _isBundledMode = Environment.GetEnvironmentVariable("KONGOR_MCP_DEV_TOOLS") != "1"
            && Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out _, bundleProbeDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mediator = mediator;
        _hasInjectedMockCollaborators = gameLauncherService != null
            || windowCapturer != null
            || headlessUiRenderer != null
            || uiControlService != null
            || gameLuaHandler != null
            || k2UiInspectService != null
            || k2UiInspectHandler != null
            || uiFlowHandler != null
            || clientNavigateHandler != null
            || assertUiHandler != null;
        _rateLimiter = rateLimiter ??
            (Environment.GetEnvironmentVariable("KONGOR_MCP_RATE_LIMIT_DISABLED") == "1"
                ? new McpRateLimiterNoOp()
                : new McpRateLimiter(Console.Error));
        if (auditStatsHandler != null) _auditStatsHandler = auditStatsHandler;
        _clientCommandRemediationService = clientCommandRemediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _lifecycleService = lifecycleService ?? new LifecycleService(_timeProvider);
        _willowmakerManageHandler = willowmakerManageHandler ?? new WillowmakerManageHandler(_timeProvider);
        _compelManageHandler = compelManageHandler ?? new CompelManageHandler(_lifecycleService, timeProvider: _timeProvider);
        _gameServerManageHandler = gameServerManageHandler ?? new GameServerManageHandler(_timeProvider);
        _serverManageHandler = new ServerManageHandler(
            _lifecycleService,
            _timeProvider,
            compelManageHandler: _compelManageHandler,
            gameServerManageHandler: _gameServerManageHandler);
        _gameLauncherService = gameLauncherService ?? new GameLauncherService();
        NativeClientIpcBridge nativeBridge = new NativeClientIpcBridge();
        _gameIpcBridge = gameIpcBridge ?? new CompositeGameIpcBridge(
            nativeBridge,
            new McpServerIpcBridge(),
            new NexusMailboxIpcBridge(timeProvider: _timeProvider, signalBridge: nativeBridge));
        NexusMailboxIpcBridge? mailboxBridge = _gameIpcBridge as NexusMailboxIpcBridge
            ?? (_gameIpcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? new TaoClient(mailboxBridge: mailboxBridge, fallbackBridge: _gameIpcBridge);
        _taoCapabilitiesHandler = taoCapabilitiesHandler ?? new TaoCapabilitiesHandler(_taoClient);
        _uiControlService = uiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _gameClientManageHandler = gameClientManageHandler ?? new GameClientManageHandler(
            _gameIpcBridge,
            _uiControlService,
            _clientCommandRemediationService,
            _timeProvider);
        _gameManageHandler = gameLauncherService != null
            ? new GameManageHandler(_gameLauncherService, _timeProvider)
            : new GameManageHandler(_gameClientManageHandler, _willowmakerManageHandler, _timeProvider);
        _gameCommandHandler = new GameCommandHandler(
            _gameIpcBridge,
            _gameServerManageHandler,
            _gameClientManageHandler,
            _uiControlService,
            _clientCommandRemediationService,
            _gameServerManageHandler.PortRegistry,
            _timeProvider);
        _windowCapturer = windowCapturer ?? new WindowCapturer(timeProvider: _timeProvider);
        _gameLuaHandler = gameLuaHandler ?? new GameLuaHandler(
            _gameClientManageHandler,
            _gameServerManageHandler,
            _uiControlService,
            _clientCommandRemediationService,
            _timeProvider,
            gameIpcBridge: _gameIpcBridge,
            portRegistry: _gameServerManageHandler.PortRegistry,
            windowCapturer: _windowCapturer,
            taoClient: _taoClient);
        _gameTelemetryHandler = new GameTelemetryHandler(_gameIpcBridge, _timeProvider, _gameClientManageHandler);
        _headlessUiRenderer = headlessUiRenderer ?? new HeadlessUiRenderer(timeProvider: _timeProvider);
        _captureWindowHandler = new CaptureWindowHandler(_windowCapturer, _timeProvider, ipcBridge: _gameIpcBridge);
        _uiRenderViewHandler = new UiRenderViewHandler(_headlessUiRenderer, _timeProvider);
        _k2UiInspectService = k2UiInspectService ?? new K2UiInspectService(_gameIpcBridge, _timeProvider, taoClient: _taoClient);
        _k2UiInspectHandler = k2UiInspectHandler ?? new K2UiInspectHandler(_k2UiInspectService, _timeProvider);
        _matchLaunchHandler = matchLaunchHandler ?? new MatchLaunchHandler(
            _gameIpcBridge,
            clientManageHandler: _gameClientManageHandler,
            uiControlService: _uiControlService,
            timeProvider: _timeProvider);
        _assetInspectService = assetInspectService ?? new AssetInspectService();
        _assetInspectHandler = assetInspectHandler ?? new AssetInspectHandler(_assetInspectService, _timeProvider);
        _engineCacheService = engineCacheService ?? new EngineCacheService(_gameIpcBridge);
        _engineCachePurgeHandler = engineCachePurgeHandler ?? new EngineCachePurgeHandler(_engineCacheService, _gameIpcBridge, _timeProvider);
        _clusterDiagnosticsService = clusterDiagnosticsService ?? new ClusterDiagnosticsService(msg => Console.Error.WriteLine(msg));
        _clusterDiagnosticsHandler = clusterDiagnosticsHandler ?? new ClusterDiagnosticsHandler(_clusterDiagnosticsService, _timeProvider);
        _matchStateHandler = new MatchStateHandler(
            _gameIpcBridge,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            new StateReconciliationService(_timeProvider),
            _timeProvider);
        _gameActionHandler = gameActionHandler ?? new GameActionHandler(
            _gameIpcBridge,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _clientCommandRemediationService,
            _timeProvider);
        _replayInspectHandler = new ReplayInspectHandler(
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _gameIpcBridge,
            _timeProvider);
        _replayPlaybackHandler = new ReplayPlaybackHandler(
            _gameIpcBridge,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _gameClientManageHandler,
            _timeProvider);
        _dbFixtureHandler = new DbFixtureHandler(_timeProvider);
        _vfsResolveHandler = new VfsResolveHandler(_timeProvider);
        _modBuildHandler = new ModBuildHandler(_gameIpcBridge, _timeProvider);
        _audioPreviewHandler = new AudioPreviewHandler(
            _gameIpcBridge,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _timeProvider);
        _k2LintService = new K2LintService(_timeProvider);
        _k2LintHandler = new K2LintHandler(_k2LintService, _timeProvider);
        _engineInspectService = new EngineInspectService(_timeProvider);
        _engineInspectHandler = new EngineInspectHandler(_engineInspectService, _timeProvider);
        _hotReloadService = hotReloadService ?? new HotReloadService(_gameIpcBridge, _timeProvider);
        _hotReloadHandler = hotReloadHandler ?? new HotReloadHandler(
            _hotReloadService,
            _gameIpcBridge,
            _clientCommandRemediationService,
            _timeProvider);
        _matchmakingSimulateService = matchmakingSimulateService ?? new MatchmakingSimulateService(timeProvider: _timeProvider);
        _matchmakingSimulateHandler = matchmakingSimulateHandler ?? new MatchmakingSimulateHandler(
            simulationService: _matchmakingSimulateService,
            remediationService: _clientCommandRemediationService,
            timeProvider: _timeProvider);
        _gameSnapshotHandler = gameSnapshotHandler ?? new GameSnapshotHandler(
            _gameIpcBridge,
            _gameLuaHandler,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            new StateReconciliationService(_timeProvider),
            _clientCommandRemediationService,
            _timeProvider);
        _scrimmageLaunchHandler = scrimmageLaunchHandler ?? new ScrimmageLaunchHandler(
            _gameServerManageHandler,
            _gameClientManageHandler,
            _matchStateHandler,
            _gameIpcBridge,
            _clientCommandRemediationService,
            _timeProvider,
            portRegistry: _gameServerManageHandler.PortRegistry);
        _uiFlowHandler = uiFlowHandler ?? new UiFlowHandler(
            _gameIpcBridge,
            _gameClientManageHandler,
            _clientCommandRemediationService,
            _uiControlService,
            _timeProvider,
            windowCapturer: _windowCapturer,
            taoClient: _taoClient);
        _telemetryStreamHandler = telemetryStreamHandler ?? new TelemetryStreamHandler(_timeProvider);
        _dbMigrationTestHandler = dbMigrationTestHandler ?? new DbMigrationTestHandler(_timeProvider);
        _memoryLeakDetectHandler = memoryLeakDetectHandler ?? new MemoryLeakDetectHandler(_timeProvider);
        _crashDumpAnalyzeHandler = crashDumpAnalyzeHandler ?? new CrashDumpAnalyzeHandler(_timeProvider);
        _botChoreographyHandler = botChoreographyHandler ?? new BotChoreographyHandler(
            _gameIpcBridge,
            _gameClientManageHandler,
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _clientCommandRemediationService,
            timeProvider: _timeProvider,
            taoClient: _taoClient);
        _networkImpairSimulateHandler = networkImpairSimulateHandler ?? new NetworkImpairSimulateHandler(
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _gameIpcBridge,
            _timeProvider);
        _cacheCoherenceVerifyHandler = cacheCoherenceVerifyHandler ?? new CacheCoherenceVerifyHandler(_timeProvider);
        _uiVisualDiffHandler = uiVisualDiffHandler ?? new UiVisualDiffHandler(_timeProvider);
        _nativeManageHandler = nativeManageHandler ?? new NativeManageHandler(_gameIpcBridge);
        _vulkanManageHandler = vulkanManageHandler ?? new VulkanManageHandler(_engineCacheService);
        _clientNavigateHandler = clientNavigateHandler ?? new ClientNavigateHandler(
            _gameIpcBridge,
            _gameClientManageHandler,
            _lifecycleService,
            _clusterDiagnosticsService,
            _k2UiInspectService,
            _windowCapturer,
            _uiControlService,
            _clientCommandRemediationService,
            _timeProvider);
        _clientPracticeHandler = clientPracticeHandler ?? new ClientPracticeHandler(
            _gameIpcBridge,
            _gameClientManageHandler,
            _lifecycleService,
            _clusterDiagnosticsService,
            _windowCapturer,
            _uiControlService,
            _clientCommandRemediationService,
            _timeProvider);
        _vectorSearchHandler = vectorSearchHandler ?? new VectorSearchHandler(timeProvider: _timeProvider);
        _errorsSinceHandler = errorsSinceHandler ?? new ErrorsSinceHandler(timeProvider: _timeProvider);
        _healthCheckHandler = healthCheckHandler ?? new HealthCheckHandler(
            _taoClient,
            _taoCapabilitiesHandler,
            _errorsSinceHandler,
            _gameClientManageHandler.ProcessRegistry,
            timeProvider: _timeProvider,
            logger: msg => Console.Error.WriteLine(msg),
            gameIpcBridge: _gameIpcBridge);
        _waitForHandler = waitForHandler ?? new WaitForHandler(
            _taoClient,
            _gameIpcBridge,
            _gameClientManageHandler.ProcessRegistry,
            timeProvider: _timeProvider);
        _assertUiHandler = assertUiHandler ?? new AssertUiHandler(_taoClient, timeProvider: _timeProvider);
        _reloadAndVerifyHandler = reloadAndVerifyHandler ?? new ReloadAndVerifyHandler(
            _hotReloadHandler,
            _taoCapabilitiesHandler,
            _healthCheckHandler,
            _errorsSinceHandler,
            _taoClient,
            timeProvider: _timeProvider);
        _perfWindowHandler = perfWindowHandler ?? new PerfWindowHandler(
            _taoClient,
            _gameClientManageHandler,
            timeProvider: _timeProvider);
        _uiControlHandler = new UiControlHandler(
            _uiControlService,
            _gameIpcBridge,
            _taoClient,
            _waitForHandler,
            _errorsSinceHandler,
            _timeProvider);
        _enableToolsetHandler = new EnableToolsetHandler(this, _timeProvider);
        _callToolHandler = new CallToolHandler(this, _timeProvider);
        OutcomeLogFilePath = Environment.GetEnvironmentVariable("KONGOR_MCP_OUTCOME_LOG_PATH")
            ?? Environment.GetEnvironmentVariable("KONGOR_MCP_OUTCOME_LOG");
        InitializeGameLifecycleHandlers(_timeProvider);
        InitializeLuaUiHandlers(_timeProvider);
        InitializeVfsAssetsHandlers(_timeProvider);
        InitializeForensicsHandlers(_timeProvider);
        InitializeDedicatedServerHandlers(_timeProvider);
        InitializeControlFabricHandlers(_timeProvider);

        if (registerDefaults)
        {
            RegisterDefaultTools();
            if (_isBundledMode && _bundledSkippedCount > 0)
            {
                Console.Error.WriteLine(
                    $"[BundledSafeMcp] Registered {_tools.Count} standalone-safe MCP tools ({_bundledSkippedCount} dev-only repo tools gated; set KONGOR_MCP_VERBOSE=1 for details).");
            }
        }
    }

    private readonly ConcurrentDictionary<string, ToolCallOutcomeStats> _outcomeStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..12];
    private readonly object _outcomeLogLock = new();
    private long _lastPreflightCheckTicks;
    private bool _cachedClientOnline;
    public string? OutcomeLogFilePath { get; set; }

    private static readonly Lazy<IReadOnlyList<McpIssueKbEntry>> s_mcpIssueKbEntries = new(LoadMcpIssueKbEntries);

    private static string ResolveRepoRootForKb()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? dir = new(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                    File.Exists(Path.Combine(dir.FullName, "docs", "reference", "mcp-issues", "index.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static IReadOnlyList<McpIssueKbEntry> LoadMcpIssueKbEntries()
    {
        try
        {
            string repoRoot = ResolveRepoRootForKb();
            string indexPath = Path.Combine(repoRoot, "docs", "reference", "mcp-issues", "index.json");
            if (!File.Exists(indexPath))
            {
                return [];
            }

            string json = File.ReadAllText(indexPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<McpIssueKbEntry> list = new();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string status = item.TryGetProperty("status", out JsonElement stEl) ? stEl.GetString() ?? "" : "";
                if (string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string id = item.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                string tool = item.TryGetProperty("tool", out JsonElement tlEl) ? tlEl.GetString() ?? "" : "";
                string category = item.TryGetProperty("category", out JsonElement catEl) ? catEl.GetString() ?? "" : "";
                string title = item.TryGetProperty("title", out JsonElement tiEl) ? tiEl.GetString() ?? "" : "";
                int occurrences = item.TryGetProperty("occurrences", out JsonElement occEl) && occEl.TryGetInt32(out int occ) ? occ : 1;
                string rule = item.TryGetProperty("prevention_rule", out JsonElement prEl) ? prEl.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new McpIssueKbEntry(id, tool, category, title, status, occurrences, rule));
                }
            }

            return list;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpToolRegistry] Warning: failed to load MCP issue KB index: {ex.Message}");
            return [];
        }
    }

    internal FirstCallScoreboardSnapshot GetFirstCallScoreboard()
    {
        Dictionary<string, bool> outcomes = new(StringComparer.OrdinalIgnoreCase);
        int successes = 0;
        int failures = 0;
        int totalCalls = 0;
        int totalErrors = 0;

        foreach (KeyValuePair<string, ToolCallOutcomeStats> kvp in _outcomeStats)
        {
            totalCalls += Volatile.Read(ref kvp.Value.TotalCalls);
            totalErrors += Volatile.Read(ref kvp.Value.TotalErrors);
            if (Volatile.Read(ref kvp.Value.FirstCallRecorded) == 1)
            {
                outcomes[kvp.Key] = kvp.Value.FirstCallSucceeded;
                if (kvp.Value.FirstCallSucceeded)
                {
                    successes++;
                }
                else
                {
                    failures++;
                }
            }
        }

        int distinct = successes + failures;
        double rate = distinct > 0 ? Math.Round((double)successes * 100.0 / distinct, 1) : 100.0;
        return new FirstCallScoreboardSnapshot(
            DistinctToolsCalled: distinct,
            FirstCallSuccesses: successes,
            FirstCallFailures: failures,
            FirstCallSuccessRate: rate,
            TotalToolCalls: totalCalls,
            TotalToolErrors: totalErrors,
            FirstCallOutcomesByTool: outcomes);
    }

    public void ResetFirstCallScoreboard() => _outcomeStats.Clear();

    public ReplayInspectHandler ReplayInspectHandler => _replayInspectHandler;
    public ReplayPlaybackHandler ReplayPlaybackHandler => _replayPlaybackHandler;
    public TelemetryStreamHandler TelemetryStreamHandler => _telemetryStreamHandler;
    public DbMigrationTestHandler DbMigrationTestHandler => _dbMigrationTestHandler;
    public MemoryLeakDetectHandler MemoryLeakDetectHandler => _memoryLeakDetectHandler;
    public CrashDumpAnalyzeHandler CrashDumpAnalyzeHandler => _crashDumpAnalyzeHandler;
    public BotChoreographyHandler BotChoreographyHandler => _botChoreographyHandler;
    public NetworkImpairSimulateHandler NetworkImpairSimulateHandler => _networkImpairSimulateHandler;
    public CacheCoherenceVerifyHandler CacheCoherenceVerifyHandler => _cacheCoherenceVerifyHandler;
    public VectorSearchHandler VectorSearchHandler => _vectorSearchHandler;
    public UiVisualDiffHandler UiVisualDiffHandler => _uiVisualDiffHandler;
    public NativeManageHandler NativeManageHandler => _nativeManageHandler;
    public VulkanManageHandler VulkanManageHandler => _vulkanManageHandler;
    public ModBuildHandler ModBuildHandler => _modBuildHandler;
    public AudioPreviewHandler AudioPreviewHandler => _audioPreviewHandler;
    public ITaoClient TaoClient => _taoClient;
    public TaoCapabilitiesHandler TaoCapabilitiesHandler => _taoCapabilitiesHandler;
    public ErrorsSinceHandler ErrorsSinceHandler => _errorsSinceHandler;
    public HealthCheckHandler HealthCheckHandler => _healthCheckHandler;
    public WaitForHandler WaitForHandler => _waitForHandler;
    public AssertUiHandler AssertUiHandler => _assertUiHandler;
    public ReloadAndVerifyHandler ReloadAndVerifyHandler => _reloadAndVerifyHandler;
    public PerfWindowHandler PerfWindowHandler => _perfWindowHandler;

    public IClientCommandRemediationService ClientCommandRemediationService => _clientCommandRemediationService;
    public IClusterDiagnosticsService ClusterDiagnosticsService => _clusterDiagnosticsService;
    public IHotReloadService HotReloadService => _hotReloadService;
    public HotReloadHandler HotReloadHandler => _hotReloadHandler;
    public IMatchmakingSimulateService MatchmakingSimulateService => _matchmakingSimulateService;
    public MatchmakingSimulateHandler MatchmakingSimulateHandler => _matchmakingSimulateHandler;
    public GameSnapshotHandler GameSnapshotHandler => _gameSnapshotHandler;
    public ScrimmageLaunchHandler ScrimmageLaunchHandler => _scrimmageLaunchHandler;
    public UiFlowHandler UiFlowHandler => _uiFlowHandler;
    public IGameIpcBridge GameIpcBridge => _gameIpcBridge;
    public MatchStateHandler MatchStateHandler => _matchStateHandler;
    public GameTelemetryHandler GameTelemetryHandler => _gameTelemetryHandler;
    public MatchLaunchHandler MatchLaunchHandler => _matchLaunchHandler;
    public EngineCachePurgeHandler EngineCachePurgeHandler => _engineCachePurgeHandler;
    public GameServerManageHandler GameServerManageHandler => _gameServerManageHandler;
    public GameActionHandler GameActionHandler => _gameActionHandler;
    public IServerPortRegistry ServerPortRegistry => _gameServerManageHandler.PortRegistry;
    public DbFixtureHandler DbFixtureHandler => _dbFixtureHandler;
    public CaptureWindowHandler CaptureWindowHandler => _captureWindowHandler;
    public GameCommandHandler GameCommandHandler => _gameCommandHandler;
    public GameLuaHandler GameLuaHandler => _gameLuaHandler;
    public ClientNavigateHandler ClientNavigateHandler => _clientNavigateHandler;

    public void RegisterTool(ToolDefinition definition, ToolHandlerDelegate handler, McpToolCategory category = McpToolCategory.Default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("Tool name cannot be null or empty.", nameof(definition));
        }

        if (_isBundledMode)
        {
            bool isAllowed = (category == McpToolCategory.ReadOnly || BundledSafeTools.Contains(definition.Name))
                && !BundledUnsafeTools.Contains(definition.Name);
            if (!isAllowed)
            {
                Interlocked.Increment(ref _bundledSkippedCount);
                if (Environment.GetEnvironmentVariable("KONGOR_MCP_VERBOSE") == "1")
                {
                    Console.Error.WriteLine($"[BundledSafeMcp] skipped {definition.Name}");
                }
                return;
            }
        }

        EnrichToolDefinitionWithPitfallRule(definition);
        definition.Annotations ??= BuildDefaultAnnotations(definition.Name, category);
        _tools[definition.Name] = new ToolRegistration(definition, handler, category);
    }

    private static ToolAnnotations BuildDefaultAnnotations(string toolName, McpToolCategory category)
    {
        bool isReadOnly = category == McpToolCategory.ReadOnly;
        bool isDestructive = BundledUnsafeTools.Contains(toolName)
            || toolName.Contains("purge", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("patch", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("chaos", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("fuzz", StringComparison.OrdinalIgnoreCase);

        return new ToolAnnotations
        {
            ReadOnlyHint = isReadOnly,
            DestructiveHint = isReadOnly ? false : isDestructive,
            IdempotentHint = isReadOnly,
            OpenWorldHint = false
        };
    }

    private static readonly Dictionary<string, string> AliasRedirects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kongor_asset_inspector"] = "kongor_asset_inspect",
        ["kongor_compel_manager"] = "kongor_compel_manage",
        ["kongor_game_client_manager"] = "kongor_game_client_manage",
        ["kongor_game_server_manager"] = "kongor_game_server_manage",
        ["kongor_ui_controller"] = "kongor_ui_control",
        ["kongor_ui_inspect"] = "kongor_k2_ui_inspect",
        ["kongor_ui_tree"] = "kongor_k2_ui_inspect",
        ["kongor_lua"] = "kongor_game_lua",
        ["kongor_willowmaker_manager"] = "kongor_willowmaker_manage",
        ["kongor_practice_launch"] = "kongor_match_launch",
        ["kongor_cache_purge"] = "kongor_engine_cache_purge",
        ["kongor_cluster_diagnostics"] = "kongor_diagnostics",
        ["kongor_game_client_command"] = "kongor_game_command",
        ["kongor_vfs_find"] = "kongor_vfs_resolve",
        ["kongor_reload"] = "kongor_hot_reload",
        ["kongor_ui_reload"] = "kongor_hot_reload",
        ["kongor_tmm_simulate"] = "kongor_matchmaking_simulate",
        ["kongor_matchmaking_bench"] = "kongor_matchmaking_simulate",
        ["kongor_snapshot"] = "kongor_game_snapshot",
        ["kongor_checkpoint"] = "kongor_game_snapshot",
        ["kongor_scrimmage"] = "kongor_scrimmage_launch",
        ["kongor_5v5_launch"] = "kongor_scrimmage_launch",
        ["kongor_macro"] = "kongor_ui_flow",
        ["kongor_ui_macro"] = "kongor_ui_flow",
        ["kongor_telemetry"] = "kongor_telemetry_stream",
        ["kongor_drift"] = "kongor_db_migration_test",
        ["kongor_leak_detect"] = "kongor_memory_leak_detect",
        ["kongor_crash_dump"] = "kongor_crash_dump_analyze",
        ["kongor_bot"] = "kongor_bot_choreography",
        ["kongor_net_impair"] = "kongor_network_impair_simulate",
        ["kongor_cache_coherence"] = "kongor_cache_coherence_verify",
        ["kongor_visual_diff"] = "kongor_ui_visual_diff",
        ["kongor_vector"] = "kongor_vector_search",
        ["vector_search"] = "kongor_vector_search",
#if !PLAYTEST_BUILD
        ["app_eval_script"] = "kongor_app_eval",
        ["kongor_app_eval_script"] = "kongor_app_eval",
        ["kongor_eval"] = "kongor_app_eval",
#endif
        ["kongor_tao_caps"] = "kongor_tao_capabilities",
        ["tao_capabilities"] = "kongor_tao_capabilities",
        ["state_snapshot_create"] = "kongor_state_snapshot",
        ["state_snapshot_restore"] = "kongor_state_snapshot",
        ["kongor_state_snapshot_create"] = "kongor_state_snapshot",
        ["kongor_state_snapshot_restore"] = "kongor_state_snapshot",


        ["network_chaos_inject"] = "kongor_network_chaos",
        ["kongor_net_chaos"] = "kongor_network_chaos",
        ["realtime_telemetry_stream"] = "kongor_realtime_stream",
        ["kongor_log_tail"] = "kongor_realtime_stream",
        ["kongor_stream"] = "kongor_realtime_stream",
        ["texture_preview"] = "kongor_texture_preview",
        ["kongor_texture_decode"] = "kongor_texture_preview",
        ["texture_in_game_view"] = "kongor_texture_in_game_inspect",
        ["kongor_texture_viewport"] = "kongor_texture_in_game_inspect",
        ["texture_diff"] = "kongor_texture_diff",
        ["kongor_texture_visual_diff"] = "kongor_texture_diff",
        ["material_inspect"] = "kongor_material_inspect",
        ["kongor_material_validate"] = "kongor_material_inspect",
        ["kongor_native"] = "kongor_native_manage",
        ["native_manage"] = "kongor_native_manage",
        ["kongor_vulkan_manage"] = "kongor_vulkan",
        ["vulkan_manage"] = "kongor_vulkan",
        ["kongor_dxvk"] = "kongor_vulkan",
        ["kongor_step"] = "kongor_game_step",
        ["game_step"] = "kongor_game_step",
        ["step_frame"] = "kongor_game_step",
        ["kongor_visual_assert"] = "kongor_assert_visual",
        ["assert_visual"] = "kongor_assert_visual",
        ["verify_visual_golden"] = "kongor_assert_visual",
        ["memory_sync"] = "kongor_memory_sync",
        ["vfs_locate"] = "kongor_vfs_locate",
        ["crash_patch"] = "kongor_crash_patch",
        ["asset_bake_pipeline"] = "kongor_asset_bake_pipeline",
        ["audio_studio"] = "kongor_audio_studio",



        ["kongor_navigate"] = "kongor_client_navigate",
        ["kongor_nav"] = "kongor_client_navigate",
        ["kongor_client_nav"] = "kongor_client_navigate",
        ["client_navigate"] = "kongor_client_navigate",
        ["match_analytics"] = "kongor_match_analytics",
        ["kongor_lakehouse_query"] = "kongor_match_analytics",
        ["lakehouse_query"] = "kongor_match_analytics"
    };

    /// <summary>
    ///     Registers <paramref name="alias" /> as a hidden, callable alias of an already-registered canonical tool.
    ///     The alias shares the canonical tool's handler, schema and rate-limit budget, and is omitted from tools/list.
    /// </summary>
    public void RegisterAlias(string alias, string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        if (!_tools.ContainsKey(canonicalName))
        {
            if (_isBundledMode)
            {
                Console.Error.WriteLine($"[BundledSafeMcp] skipped alias {alias} -> {canonicalName}");
                return;
            }

            throw new InvalidOperationException(
                $"Cannot register alias '{alias}': canonical tool '{canonicalName}' is not registered (register it first).");
        }

        if (_tools.ContainsKey(alias))
        {
            throw new InvalidOperationException(
                $"Cannot register alias '{alias}': a canonical tool with that name is already registered.");
        }

        _aliases[alias] = canonicalName;
    }

    /// <summary>
    ///     All hidden aliases (instance-registered and static redirects) whose canonical tool is registered.
    /// </summary>
    public IReadOnlyDictionary<string, string> ListAliases()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> redirect in AliasRedirects)
        {
            if (_tools.ContainsKey(redirect.Value) && !_tools.ContainsKey(redirect.Key))
                result[redirect.Key] = redirect.Value;
        }

        foreach (KeyValuePair<string, string> alias in _aliases)
            result[alias.Key] = alias.Value;

        return result;
    }

    /// <summary>
    ///     Resolves a requested tool name to the name of a registered tool: canonical name first, then instance aliases,
    ///     then static <see cref="AliasRedirects" />. Returns NULL when nothing matches.
    /// </summary>
    private string? ResolveToolName(string name)
    {
        if (_tools.ContainsKey(name))
            return name;

        if (_aliases.TryGetValue(name, out string? aliasTarget) && _tools.ContainsKey(aliasTarget))
            return aliasTarget;

        if (AliasRedirects.TryGetValue(name, out string? redirectTarget) && _tools.ContainsKey(redirectTarget))
            return redirectTarget;

        return null;
    }

    public bool HasTool(string name) => ResolveToolName(name) is not null;

    public ToolDefinition? GetTool(string name)
    {
        string? resolved = ResolveToolName(name);
        return resolved is not null && _tools.TryGetValue(resolved, out ToolRegistration? reg) ? reg.Definition : null;
    }

    public void SetDefaultToolsets(string? toolsets)
    {
        lock (_toolsetLock)
        {
            _configuredDefaultToolsets = toolsets;
        }
    }

    public IReadOnlyCollection<string> EnableToolsets(IEnumerable<string> domains, Action? onListChanged = null)
    {
        ArgumentNullException.ThrowIfNull(domains);

        lock (_toolsetLock)
        {
            foreach (string d in domains)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                string[] parts = d.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        _runtimeEnabledToolsets.Add(part);
                    }
                }
            }
        }

        onListChanged?.Invoke();
        ToolListChanged?.Invoke();

        return GetActiveToolsets();
    }

    public IReadOnlyCollection<string> GetActiveToolsets()
    {
        lock (_toolsetLock)
        {
            HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
            string? baseToolsets = Environment.GetEnvironmentVariable("KONGOR_MCP_TOOLSETS") ?? _configuredDefaultToolsets;
            if (!string.IsNullOrWhiteSpace(baseToolsets))
            {
                var parsed = ParseActiveToolsets(baseToolsets);
                if (parsed != null)
                {
                    foreach (var s in parsed) active.Add(s);
                }
                else
                {
                    active.Add("all");
                }
            }

            foreach (var rt in _runtimeEnabledToolsets)
            {
                active.Add(rt);
            }

            if (active.Count == 0)
            {
                active.Add("all");
            }

            return active.ToList();
        }
    }

    private HashSet<string>? ResolveEffectiveActiveToolsets(string? explicitToolsets)
    {
        if (explicitToolsets != null)
        {
            return ParseActiveToolsets(explicitToolsets);
        }

        lock (_toolsetLock)
        {
            string? baseToolsets = Environment.GetEnvironmentVariable("KONGOR_MCP_TOOLSETS") ?? _configuredDefaultToolsets;
            if (baseToolsets == null && _runtimeEnabledToolsets.Count == 0)
            {
                return null;
            }

            HashSet<string> combined = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(baseToolsets))
            {
                var parsedBase = ParseActiveToolsets(baseToolsets);
                if (parsedBase == null)
                {
                    return null;
                }
                foreach (var b in parsedBase) combined.Add(b);
            }

            foreach (var rt in _runtimeEnabledToolsets)
            {
                if (string.Equals(rt, "all", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rt, "*", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                combined.Add(rt);
            }

            return combined.Count > 0 ? combined : null;
        }
    }

    public IReadOnlyList<ToolDefinition> GetAllTools(bool? includeAliases = null, string? toolsets = null, bool? includeSimulated = null) =>
        ListTools(includeAliases, toolsets, includeSimulated);

    public IReadOnlyList<ToolDefinition> ListTools(bool? includeAliases = null, string? toolsets = null, bool? includeSimulated = null)
    {
        bool shouldIncludeAliases = includeAliases ?? IsAliasListingEnabled();
        bool shouldIncludeSimulated = includeSimulated ?? IsSimulationListingAllowed();
        HashSet<string>? activeToolsets = ResolveEffectiveActiveToolsets(toolsets);

        List<ToolDefinition> results = new(_tools.Count + (shouldIncludeAliases ? _aliases.Count + AliasRedirects.Count : 0));

        foreach (ToolRegistration reg in _tools.Values)
        {
            if (!shouldIncludeSimulated && QuarantinedSimulatedTools.Contains(reg.Definition.Name))
            {
                continue;
            }

            if (activeToolsets is not null && !MatchesToolset(reg, activeToolsets))
            {
                continue;
            }

            results.Add(reg.Definition);
        }

        if (shouldIncludeAliases)
        {
            IReadOnlyDictionary<string, string> aliases = ListAliases();
            foreach (KeyValuePair<string, string> kvp in aliases)
            {
                if (!_tools.TryGetValue(kvp.Value, out ToolRegistration? targetReg))
                {
                    continue;
                }

                if (!shouldIncludeSimulated && (QuarantinedSimulatedTools.Contains(kvp.Key) || QuarantinedSimulatedTools.Contains(targetReg.Definition.Name)))
                {
                    continue;
                }

                if (activeToolsets is not null && !MatchesToolset(targetReg, activeToolsets))
                {
                    continue;
                }

                results.Add(new ToolDefinition
                {
                    Name = kvp.Key,
                    Description = $"[Alias for {targetReg.Definition.Name}] {targetReg.Definition.Description}",
                    InputSchema = targetReg.Definition.InputSchema
                });
            }
        }

        results.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return results;
    }

    private static bool IsSimulationListingAllowed()
    {
        return string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS"), "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED"), "1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAliasListingEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_ALIASES");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string>? ParseActiveToolsets(string? rawToolsets)
    {
        if (string.IsNullOrWhiteSpace(rawToolsets))
        {
            return null;
        }

        string[] tokens = rawToolsets.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (string token in tokens)
        {
            if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "*", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            set.Add(token);
        }

        return set.Count > 0 ? set : null;
    }

    private static bool MatchesToolset(ToolRegistration reg, HashSet<string> activeToolsets)
    {
        foreach (string toolset in activeToolsets)
        {
            if (MatchesSingleToolset(reg, toolset))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSingleToolset(ToolRegistration reg, string toolset)
    {
        string name = reg.Definition.Name;

        if (string.Equals(toolset, "ui", StringComparison.OrdinalIgnoreCase))
        {
            return reg.Category == McpToolCategory.UiControl ||
                   name.Contains("_ui_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("capture_window", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("render_view", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("assert_ui", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("visual_diff", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("client_navigate", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(toolset, "re", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolset, "reverse_engineering", StringComparison.OrdinalIgnoreCase))
        {
            return reg.Category == McpToolCategory.HeavyProfiling ||
                   name.Contains("native_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("vulkan_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("memory_leak", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("engine_inspect", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("tao_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("vector_search", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(toolset, "server", StringComparison.OrdinalIgnoreCase))
        {
            return reg.Category == McpToolCategory.DatabaseOps ||
                   name.Contains("server_manage", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("willowmaker", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("compel", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("cluster_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("db_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("matchmaking_", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("network_impair", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("scrimmage", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("bot_choreography", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("match_launch", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(toolset, "core", StringComparison.OrdinalIgnoreCase))
        {
            return CoreToolsetAllowlist.Contains(name);


        }

        if (Enum.TryParse<McpToolCategory>(toolset, ignoreCase: true, out McpToolCategory parsedCategory))
        {
            return reg.Category == parsedCategory;
        }

        return name.Contains(toolset, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRecognizedToolsetDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        string trimmed = domain.Trim();
        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("core", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ui", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("re", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("reverse_engineering", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("server", StringComparison.OrdinalIgnoreCase) ||
            Enum.TryParse<McpToolCategory>(trimmed, ignoreCase: true, out _))
        {
            return true;
        }

        return _tools.Values.Any(reg => reg.Definition.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ToolCallResult> ExecuteToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string? resolvedName = ResolveToolName(name);

        if (resolvedName is null || !_tools.TryGetValue(resolvedName, out ToolRegistration? reg))
        {
            ToolCallResult unknownErr = ToolCallResult.Error($"Unknown tool '{name}'. Available tools: {string.Join(", ", _tools.Keys.OrderBy(k => k))}");
            sw.Stop();
            RecordToolOutcome(name, name, unknownErr, sw.Elapsed.TotalMilliseconds, null, "precondition", arguments);
            return unknownErr;
        }

        string targetName = resolvedName;

        if (!string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        {
            // stderr only: stdout carries the JSON-RPC stream.
            Console.Error.WriteLine($"[McpToolRegistry] Alias '{name}' resolved to canonical tool '{targetName}'.");
        }

        // Call-time quarantine enforcement (MCP-283): tools in QuarantinedSimulatedTools fail closed
        // unless KONGOR_MCP_ALLOW_LEGACY_MOCKS=1 / KONGOR_MCP_INCLUDE_SIMULATED=1 or allowSimulation=true.
        bool allowSimulationArg = arguments.ValueKind == JsonValueKind.Object &&
                                  ((arguments.TryGetProperty("allowSimulation", out JsonElement asEl) ||
                                    arguments.TryGetProperty("allow_simulation", out asEl)) &&
                                   (asEl.ValueKind == JsonValueKind.True ||
                                    (asEl.ValueKind == JsonValueKind.String && bool.TryParse(asEl.GetString(), out bool asB) && asB)));
        if (QuarantinedSimulatedTools.Contains(targetName) && !IsSimulationListingAllowed() && !allowSimulationArg)
        {
            string qMsg = $"Tool '{targetName}' is quarantined because it previously returned simulated/canned data (MCP-283). Pass allowSimulation=true only for synthetic test harness evaluation.";
            ToolCallResult qErr = ToolCallResult.Json(new
            {
                status = "error",
                ok = false,
                tool = targetName,
                errorCode = "quarantined_simulated_tool",
                executionMode = ExecutionMode.SyntheticFallback,
                executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
                error = qMsg,
                message = qMsg
            }, isError: true);
            sw.Stop();
            RecordToolOutcome(targetName, name, qErr, sw.Elapsed.TotalMilliseconds, "MCP-283", "quarantined_tool", arguments);
            return qErr;
        }

        // Worker ROE (kongor-offline-worker isolation) & in-process 3-Strike Circuit Breaker
        ToolCallResult? roeOrBreakerResult = WorkerRoeGuard.EvaluatePreCall(targetName, arguments);
        if (roeOrBreakerResult is not null)
        {
            sw.Stop();
            RecordToolOutcome(targetName, name, roeOrBreakerResult, sw.Elapsed.TotalMilliseconds, null, "worker_roe_or_breaker", arguments);
            return roeOrBreakerResult;
        }

        // Single-billing pass-through for kongor_call_tool(action='call'):
        // When bridging to a valid non-recursive target tool, bypass outer rate-limiting,
        // outer preflight, and outer RecordToolOutcome so the inner ExecuteToolAsync call
        // bills and logs the inner tool exactly once (protecting SLO 1 FirstCallSuccessRate).
        if (string.Equals(targetName, CallToolHandler.CanonicalToolName, StringComparison.OrdinalIgnoreCase) &&
            CallToolHandler.TryExtractBridgedInvocation(arguments, out string? bridgedTarget, out _, out _, out string? bridgeParseErr) &&
            bridgeParseErr == null &&
            !CallToolHandler.IsBridgeName(bridgedTarget) &&
            ResolveToolName(bridgedTarget!) is string resolvedInner &&
            _tools.ContainsKey(resolvedInner))
        {
            try
            {
                return await reg.Handler(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error($"Error executing bridged tool '{bridgedTarget}': {ex.Message}");
            }
        }

        McpToolCategory effectiveCategory = ResolveEffectiveCategory(targetName, reg.Category, arguments);

        if (_rateLimiter.IsEnabled)
        {
            string? agentId = McpAgentContext.CurrentAgentId;
            (bool permitted, TimeSpan? retryAfter) = _rateLimiter.TryAcquire(targetName, effectiveCategory, agentId);
            if (!permitted)
            {
                int seconds = (int)Math.Max(1, Math.Ceiling(retryAfter?.TotalSeconds ?? 60));
                int retryAfterMs = (int)Math.Max(1000, Math.Ceiling(retryAfter?.TotalMilliseconds ?? 60000));
                string rateMsg =
                    $"Rate limit exceeded for '{effectiveCategory}' category. " +
                    $"Tool '{name}' was blocked to prevent runaway loops. " +
                    $"Try again in {seconds} seconds. " +
                    $"Use 'kongor_rate_limit_status' to check remaining budgets.";
                ToolCallResult rateErr = ToolCallResult.Json(new
                {
                    status = "error",
                    ok = false,
                    tool = targetName,
                    errorCode = "rate_limit_exceeded",
                    category = effectiveCategory.ToString(),
                    retryAfterSeconds = seconds,
                    retryAfterMs,
                    error = rateMsg,
                    message = rateMsg,
                    remediation_call = new
                    {
                        tool = "kongor_rate_limit_status",
                        arguments = new { }
                    }
                }, isError: true);
                string? rateIssueId = EnrichErrorWithMcpIssue(targetName, rateErr, out string rateErrClass);
                sw.Stop();
                RecordToolOutcome(targetName, name, rateErr, sw.Elapsed.TotalMilliseconds, rateIssueId, rateErrClass, arguments);
                return rateErr;
            }
        }

        ToolCallResult? preflightFailure = await TryEvaluatePreflightAsync(targetName, arguments, cancellationToken).ConfigureAwait(false);
        if (preflightFailure != null)
        {
            string? pfIssueId = EnrichErrorWithMcpIssue(targetName, preflightFailure, out string pfErrClass);
            sw.Stop();
            RecordToolOutcome(targetName, name, preflightFailure, sw.Elapsed.TotalMilliseconds, pfIssueId, pfErrClass, arguments);
            return preflightFailure;
        }

        if (!NormalizeAndValidateToolArguments(targetName, reg.Definition, arguments, out JsonElement normalizedArgs, out ToolCallResult? argConflictError))
        {
            string? confIssueId = EnrichErrorWithMcpIssue(targetName, argConflictError!, out string confErrClass);
            sw.Stop();
            RecordToolOutcome(targetName, name, argConflictError!, sw.Elapsed.TotalMilliseconds, confIssueId, confErrClass, arguments);
            return argConflictError!;
        }

        ToolCallResult result;
        try
        {
            result = await reg.Handler(normalizedArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = ToolCallResult.Error($"Error executing tool '{name}': {ex.Message}");
        }

        result = EnforceExecutionFidelityGate(targetName, normalizedArgs, result);
        result = ApplyDefaultResponseCapIfNeeded(targetName, normalizedArgs, result);

        string? matchedIssueId = null;
        string errorClass = "none";
        if (result.IsError)
        {
            matchedIssueId = EnrichErrorWithMcpIssue(targetName, result, out errorClass);
            string? errPreview = result.Content.OfType<TextContentItem>().FirstOrDefault()?.Text;
            WorkerRoeGuard.RecordOutcome(targetName, isError: true, errorSummary: errPreview);
        }
        else
        {
            WorkerRoeGuard.RecordOutcome(targetName, isError: false);
        }

        sw.Stop();
        RecordToolOutcome(targetName, name, result, sw.Elapsed.TotalMilliseconds, matchedIssueId, errorClass, normalizedArgs);
        return result;
    }

    public static McpToolCategory ResolveEffectiveCategory(string targetName, McpToolCategory defaultCategory, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return defaultCategory;
        }

        // MCP-292: Any dryRun / dry_run call across any tool is read-only and must never consume Simulation permits.
        if ((arguments.TryGetProperty("dryRun", out JsonElement drEl) ||
             arguments.TryGetProperty("dry_run", out drEl)) &&
            (drEl.ValueKind == JsonValueKind.True ||
             (drEl.ValueKind == JsonValueKind.String && bool.TryParse(drEl.GetString(), out bool drB) && drB)))
        {
            return McpToolCategory.ReadOnly;
        }

        if (!arguments.TryGetProperty("action", out JsonElement actEl) || actEl.ValueKind != JsonValueKind.String)
        {
            return defaultCategory;
        }

        string action = actEl.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(action))
        {
            return defaultCategory;
        }

        // Universal read-only action verbs across all tools (including kongor_client_practice, kongor_scrimmage_launch,
        // kongor_match_launch, kongor_bot_choreography, and *_manage tools; MCP-237 & MCP-292).
        if (action is "status" or "list" or "list_files" or "list_recent" or "clients" or "pipes" or "pipe_status"
            or "logs" or "errors" or "clear_errors" or "history" or "health" or "diagnostics" or "verify"
            or "inspect" or "check" or "discover" or "describe" or "search" or "summary" or "info")
        {
            return McpToolCategory.ReadOnly;
        }

        if (string.Equals(targetName, "kongor_game_client_manage", StringComparison.OrdinalIgnoreCase) &&
            action is "command" or "lua" or "set_window_mode" or "login")
        {
            return McpToolCategory.GameCommand;
        }

        if (string.Equals(targetName, "kongor_client_practice", StringComparison.OrdinalIgnoreCase) &&
            action is "start_match" or "level_up" or "give_items" or "spawn_bots" or "select_hero")
        {
            return McpToolCategory.GameCommand;
        }

        return defaultCategory;
    }

    private async Task<bool> CheckClientOnlineCachedAsync(CancellationToken ct)
    {
        if (_gameIpcBridge is CompositeGameIpcBridge compositeBridge)
        {
            if (compositeBridge.Primary.IsConnected || compositeBridge.Secondary.IsConnected)
            {
                return true;
            }
        }
        else if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            return true;
        }

        long nowTicks = Environment.TickCount64;
        if (nowTicks - Interlocked.Read(ref _lastPreflightCheckTicks) < 2000)
        {
            return _cachedClientOnline;
        }

        bool online = false;
        try
        {
            if (_gameIpcBridge is CompositeGameIpcBridge comp)
            {
                await comp.Primary.ConnectAsync(200, ct).ConfigureAwait(false);
                online = comp.Primary.IsConnected || comp.Secondary.IsConnected;
            }
            else if (_gameIpcBridge != null)
            {
                await _gameIpcBridge.ConnectAsync(200, ct).ConfigureAwait(false);
                online = _gameIpcBridge.IsConnected;
            }

            if (!online && _gameClientManageHandler?.ProcessRegistry != null && !GameServerManageHandler.IsTestExecution)
            {
                ClientInstance? primary = _gameClientManageHandler.ProcessRegistry.GetPrimaryClient();
                online = primary != null && primary.IsAlive;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpToolRegistry] Preflight client check error: {ex.Message}");
        }

        _cachedClientOnline = online;
        Interlocked.Exchange(ref _lastPreflightCheckTicks, nowTicks);
        return online;
    }

    public Task<ToolCallResult> DispatchToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        => ExecuteToolAsync(name, arguments, cancellationToken);

    private void RegisterDefaultTools()
    {
        // Rate Limiting Status Introspection Tool
        RegisterTool(new ToolDefinition
        {
            Name = "kongor_rate_limit_status",
            Description = "Inspect current MCP rate limit budgets, remaining permits, and replenishment countdowns across categories.",
            InputSchema = new ToolInputSchema
            {
                Type = "object"
            }
        }, (_, _) =>
        {
            string? agentId = McpAgentContext.CurrentAgentId;
            object status = _rateLimiter.GetCategoryStatus(agentId);
            return Task.FromResult(ToolCallResult.Success(status));
        }, McpToolCategory.ReadOnly);

        RegisterGameLifecycleTools();
        RegisterLuaUiTools();
        RegisterVfsAssetsTools();
        RegisterForensicsTools();
        RegisterDedicatedServerTools();
        RegisterControlFabricTools();

        // Universal Cross-IDE Meta-Bridge & Catalog Discovery Tool
        RegisterTool(new ToolDefinition
        {
            Name = CallToolHandler.CanonicalToolName,
            Description =
                "Universal cross-IDE meta-bridge and catalog discovery tool for all TAO MCP tools. " +
                "Use action='list' or action='search' (with 'query') to discover non-core tools across UI, VFS, forensics, dedicated servers, and telemetry; " +
                "action='describe' (with 'tool_name') to inspect exact inputSchema parameters and examples; " +
                "or action='call' (with 'tool_name' and JSON string 'arguments_json') to execute any registered tool on the first try even in IDEs without dynamic list_changed support.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Title = "kongor_call_toolArguments",
                Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                {
                    ["action"] = new()
                    {
                        Type = "string",
                        Title = "Action",
                        Description = "Bridge operation: 'call' (invoke tool_name with arguments_json), 'list' (list catalog), 'search' (filter catalog by query), or 'describe' (return full inputSchema and example call for tool_name). Defaults to 'call' when tool_name is provided, or 'list'/'search' otherwise.",
                        Enum = ["call", "list", "search", "describe"],
                        Default = "call"
                    },
                    ["tool_name"] = new()
                    {
                        Type = "string",
                        Title = "Target Tool Name",
                        Description = "Canonical or alias TAO MCP tool name to invoke ('call') or inspect ('describe'), e.g. 'kongor_k2_lint', 'kongor_texture_preview', 'kongor_perf_window'."
                    },
                    ["arguments_json"] = new()
                    {
                        Type = "string",
                        Title = "Arguments JSON String",
                        Description = "JSON-encoded object string of arguments to pass to tool_name when action='call' (e.g. \"{\\\"path\\\":\\\"games/client/override/ui\\\"}\"). Defaults to \"{}\".",
                        Default = "{}"
                    },
                    ["query"] = new()
                    {
                        Type = "string",
                        Title = "Search Query or Category",
                        Description = "Optional keyword, tool substring, or category filter when action='list' or action='search' (e.g. 'texture', 'replay', 'ReadOnly', 'ui')."
                    }
                },
                Required = []
            }
        }, _callToolHandler.HandleAsync, McpToolCategory.Default);
        RegisterAlias("call_tool", CallToolHandler.CanonicalToolName);
        RegisterAlias("kongor_bridge", CallToolHandler.CanonicalToolName);
        RegisterAlias("kongor_discover_tools", CallToolHandler.CanonicalToolName);

        // Dynamic Toolset Expansion Tool (retained for backwards compatibility with dynamic list_changed clients)
        RegisterTool(new ToolDefinition
        {
            Name = "kongor_enable_toolset",
            Description = "Dynamically enable MCP toolsets (e.g. 'ui', 're', 'server', 'all', or category names) for the active session, advertising additional tools and emitting notifications/tools/list_changed.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["domain"] = new()
                    {
                        Type = "string",
                        Description = "Toolset or domain name to enable: 'ui', 're', 'server', 'all', or comma-separated list."
                    },
                    ["toolsets"] = new()
                    {
                        Type = "string",
                        Description = "Array or comma-separated string of toolset names to enable."
                    }
                }
            }
        }, _enableToolsetHandler.HandleAsync, McpToolCategory.ReadOnly);
    }
}