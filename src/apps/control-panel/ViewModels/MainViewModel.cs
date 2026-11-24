// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.Contracts.Domain;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly INexusOrchestratorService _orchestrator;
    private int _isEvaluatingWindowClose;
    private int _disposed;

    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private bool _isUnsavedChangesModalOpen;
    [ObservableProperty] private int _pendingNavigationIndex;
    [ObservableProperty] private int _selectedNavigationIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnsavedModalPromptText), nameof(UnsavedModalSaveButtonText), nameof(UnsavedModalDiscardButtonText))]
    private bool _isPendingWindowCloseOnUnsavedModal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeepDockerDefaultButton))]
    private bool _isExitConfirmationModalOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsKeepDockerDefaultButton),
        nameof(ExitShutDownButtonText),
        nameof(ExitModalBorderColor),
        nameof(ExitModalBadgeBackground),
        nameof(ExitModalBadgeForeground))]
    private bool _isDockerExitMode;

    [ObservableProperty] private string _exitModalBadgeText = "🐳 DOCKER STACK ACTIVE";
    [ObservableProperty] private string _exitModalTitle = "Shut Down Docker Stack Too?";
    [ObservableProperty] private string _exitModalDescription = string.Empty;
    [ObservableProperty] private string _exitModalSubtext = string.Empty;

    public event Action<string>? OnRequestDetachExit;
    public event Action<string>? OnRequestFullShutdownExit;

    public string UnsavedModalPromptText => IsPendingWindowCloseOnUnsavedModal
        ? "What would you like to do before closing NEXUS Control Panel?"
        : "What would you like to do before navigating to another tab?";

    public string UnsavedModalSaveButtonText => IsPendingWindowCloseOnUnsavedModal
        ? "💾 Save & Continue"
        : "💾 Save & Go";

    public string UnsavedModalDiscardButtonText => IsPendingWindowCloseOnUnsavedModal
        ? "🗑️ Discard & Continue"
        : "🗑️ Discard & Go";

    public bool IsKeepDockerDefaultButton => IsExitConfirmationModalOpen && IsDockerExitMode;

    public string ExitShutDownButtonText => IsDockerExitMode
        ? "⏹ Shut Down Docker & Exit"
        : "⏹ Stop Local Cluster & Exit";

    public string ExitModalBorderColor => IsDockerExitMode ? "#00F0FF" : "#F59E0B";
    public string ExitModalBadgeBackground => IsDockerExitMode ? "#083344" : "#451A03";
    public string ExitModalBadgeForeground => IsDockerExitMode ? "#22D3EE" : "#FBBF24";

    partial void OnIsUnsavedChangesModalOpenChanged(bool value)
    {
        SyncViewportModalAirspace();
    }

    partial void OnIsExitConfirmationModalOpenChanged(bool value)
    {
        SyncViewportModalAirspace();
    }

    private void SyncViewportModalAirspace()
    {
        Controls.Kongor3DViewportControl.SetModalOverlayActive(
            IsUnsavedChangesModalOpen || IsExitConfirmationModalOpen);
    }

    public MainViewModel(
        INexusOrchestratorService orchestrator,
        ISecretsManagerService secretsManager,
        IDockerHealthService dockerHealth,
        IPortScannerService portScanner,
        IDatabaseHealthService? databaseHealth = null,
        IUserManagementService? userManagement = null,
        IGameClientAssetService? assetService = null,
        ICockpitAppBridgeService? cockpitBridge = null,
        IModerationQueueService? moderationQueue = null)
    {
        _orchestrator = orchestrator;
        ICockpitAppBridgeService sharedCockpitBridge = cockpitBridge ?? new CockpitAppBridgeService(orchestrator);

        IUserManagementService userService = userManagement ?? new UserManagementService(
            secretsManager,
            logSink: (svc, msg, isErr) => orchestrator.AppendLog(new LogMessage
            {
                ServiceName = svc,
                Message = msg,
                Level = isErr ? "ERROR" : "INFO",
                IsError = isErr,
                Timestamp = TimeProvider.System.GetUtcNow()
            }));

        GameServers = new GameServersViewModel(
            new GameServerFleetService(
                secretsManager,
                logSink: (svc, msg, isErr) => orchestrator.AppendLog(new LogMessage
                {
                    ServiceName = svc,
                    Message = msg,
                    Level = isErr ? "ERROR" : "INFO",
                    IsError = isErr,
                    Timestamp = TimeProvider.System.GetUtcNow()
                })),
            orchestrator,
            secretsManager,
            dockerHealth,
            onNavigateToSecretsConfig: () => SelectedNavigationIndex = 2,
            cockpitBridge: sharedCockpitBridge);

        Dashboard = new DashboardViewModel(
            orchestrator,
            dockerHealth,
            databaseHealth,
            secretsManager,
            onNavigateToGameServers: () => SelectedNavigationIndex = 5,
            onNavigateToHostedServers: () =>
            {
                SelectedNavigationIndex = 5;
                if (GameServers != null)
                {
                    GameServers.IsChatModeActive = false;
                    GameServers.IsQueueArcadeContainerMode = false;
                    GameServers.ShowContainerLauncherDialog = true;
                }
            },
            onLaunchDockerServer: () =>
            {
                SelectedNavigationIndex = 5;
                if (GameServers != null)
                {
                    GameServers.IsChatModeActive = false;
                    GameServers.IsQueueArcadeContainerMode = false;
                    GameServers.SelectedContainerPlatform = "Docker";
                    GameServers.ShowContainerLauncherDialog = true;
                }
            },
            onLaunchK3sAgonesServer: () =>
            {
                SelectedNavigationIndex = 5;
                if (GameServers != null)
                {
                    GameServers.IsChatModeActive = false;
                    GameServers.IsQueueArcadeContainerMode = false;
                    GameServers.SelectedContainerPlatform = "K3s (Agones)";
                    GameServers.ShowContainerLauncherDialog = true;
                }
            },
            onLaunchDockerQueueArcade: () =>
            {
                SelectedNavigationIndex = 5;
                if (GameServers != null)
                {
                    GameServers.IsChatModeActive = false;
                    GameServers.IsQueueArcadeContainerMode = true;
                    GameServers.SelectedContainerPlatform = "Docker";
                    GameServers.ShowContainerLauncherDialog = true;
                    GameServers.UpdateGeneratedContainerCommand();
                }
            },
            onLaunchK3sQueueArcade: () =>
            {
                SelectedNavigationIndex = 5;
                if (GameServers != null)
                {
                    GameServers.IsChatModeActive = false;
                    GameServers.IsQueueArcadeContainerMode = true;
                    GameServers.SelectedContainerPlatform = "K3s (Agones)";
                    GameServers.ShowContainerLauncherDialog = true;
                    GameServers.UpdateGeneratedContainerCommand();
                }
            },
            cockpitBridge: sharedCockpitBridge,
            onSecretsRotatedExternal: async () =>
            {
                if (SecretsConfig is { HasChanges: false })
                {
                    await SecretsConfig.LoadSecretsAsync();
                }
            },
            onOpenServerChatPopOut: () => GameServers.PopOutChat(),
            getServerPipeHints: () => GameServers.GetServerPipeHints());
        Users = new UsersViewModel(userService);
        IModerationQueueService moderationService = moderationQueue ?? new ModerationQueueService(
            secretsManager,
            logSink: (svc, msg, isErr) => orchestrator.AppendLog(new LogMessage
            {
                ServiceName = svc,
                Message = msg,
                Level = isErr ? "ERROR" : "INFO",
                IsError = isErr,
                Timestamp = TimeProvider.System.GetUtcNow()
            }));
        Moderation = new ModerationQueueViewModel(moderationService, autoLoad: false);
        SecretsConfig = new SecretsConfigViewModel(secretsManager, assetService, dockerHealth);
        LiveLogs = new LiveLogsViewModel(orchestrator);
        Diagnostics = new DiagnosticsViewModel(
            portScanner,
            dockerHealth,
            databaseHealth,
            sharedCockpitBridge,
            getServerPipeHints: () => GameServers.GetServerPipeHints());
        ReplayStudio = new ReplayStudioViewModel(
            new LocalReplayParserService(),
            new ReplayRenderManagerService((svc, msg, isErr) => orchestrator.AppendLog(new LogMessage
            {
                ServiceName = svc,
                Message = msg,
                Level = isErr ? "ERROR" : "INFO",
                IsError = isErr,
                Timestamp = TimeProvider.System.GetUtcNow()
            })));

        _currentPage = Dashboard;
    }

    public string FullVersionDisplay => NexusVersionInfo.FullDisplayVersion;

    public DashboardViewModel Dashboard { get; }
    public UsersViewModel Users { get; }
    public SecretsConfigViewModel SecretsConfig { get; }
    public LiveLogsViewModel LiveLogs { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public GameServersViewModel GameServers { get; }
    public ModerationQueueViewModel Moderation { get; }
    public ReplayStudioViewModel ReplayStudio { get; }

    partial void OnSelectedNavigationIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            0 => Dashboard,
            1 => Users,
            2 => SecretsConfig,
            3 => LiveLogs,
            4 => Diagnostics,
            5 => GameServers,
            6 => Moderation,
            _ => Dashboard
        };

        if (value == 5)
        {
            GameServers.OnNavigatedTo();
        }
        else if (value == 6)
        {
            _ = Moderation.RefreshReportsAsync();
        }
    }

    [RelayCommand]
    public void NavigateTo(object? parameter)
    {
        int index = 0;
        if (parameter is int i)
            index = i;
        else if (parameter is string s && int.TryParse(s, out int parsed)) index = parsed;

        // Navigation guard: Prompt if leaving Secrets & Config with unsaved changes
        if (CurrentPage == SecretsConfig && SecretsConfig.HasChanges && index != 2)
        {
            PendingNavigationIndex = index;
            IsPendingWindowCloseOnUnsavedModal = false;
            IsUnsavedChangesModalOpen = true;
            return;
        }

        SelectedNavigationIndex = index;
    }

    [RelayCommand]
    public async Task ConfirmSaveAndNavigateAsync()
    {
        bool continueWindowClose = IsPendingWindowCloseOnUnsavedModal;
        await SecretsConfig.SaveSecretsAsync();
        IsPendingWindowCloseOnUnsavedModal = false;
        IsUnsavedChangesModalOpen = false;

        if (continueWindowClose)
        {
            await EvaluateAndHandleWindowCloseAsync();
        }
        else
        {
            SelectedNavigationIndex = PendingNavigationIndex;
        }
    }

    [RelayCommand]
    public async Task DiscardAndNavigateAsync()
    {
        bool continueWindowClose = IsPendingWindowCloseOnUnsavedModal;
        SecretsConfig.DiscardChanges();
        IsPendingWindowCloseOnUnsavedModal = false;
        IsUnsavedChangesModalOpen = false;

        if (continueWindowClose)
        {
            await EvaluateAndHandleWindowCloseAsync();
        }
        else
        {
            SelectedNavigationIndex = PendingNavigationIndex;
        }
    }

    [RelayCommand]
    public void CancelNavigation()
    {
        IsPendingWindowCloseOnUnsavedModal = false;
        IsUnsavedChangesModalOpen = false;
    }

    public async Task RequestWindowCloseAsync()
    {
        if (IsExitConfirmationModalOpen || (IsUnsavedChangesModalOpen && IsPendingWindowCloseOnUnsavedModal))
        {
            Console.WriteLine("[ControlPanel] [EXIT-MODAL] Window close request ignored because a close modal is already active.");
            return;
        }

        if (Interlocked.CompareExchange(ref _isEvaluatingWindowClose, 1, 0) != 0)
        {
            Console.WriteLine("[ControlPanel] [EXIT-MODAL] Window close request ignored because an exit probe is already in flight.");
            return;
        }

        try
        {
            if (SecretsConfig.HasChanges)
            {
                Console.WriteLine("[ControlPanel] [EXIT-GUARD] Unsaved Secrets & Config edits detected on window close; opening Unsaved Changes guard modal.");
                _orchestrator.AppendLog(new LogMessage
                {
                    ServiceName = "CONTROL PANEL",
                    Message = "[EXIT-GUARD] Prompting to save or discard unsaved Secrets & Config edits before window close.",
                    Level = "INFO"
                });
                IsPendingWindowCloseOnUnsavedModal = true;
                IsUnsavedChangesModalOpen = true;
                return;
            }

            await EvaluateAndHandleWindowCloseCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _isEvaluatingWindowClose, 0);
        }
    }

    private async Task EvaluateAndHandleWindowCloseAsync()
    {
        if (Interlocked.CompareExchange(ref _isEvaluatingWindowClose, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await EvaluateAndHandleWindowCloseCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _isEvaluatingWindowClose, 0);
        }
    }

    private async Task EvaluateAndHandleWindowCloseCoreAsync()
    {
        RuntimeExitInspectionResult inspection = await _orchestrator.InspectRuntimeForExitAsync();
        bool hasDocker = inspection.HasRunningDockerServices || Dashboard.IsDockerStackBusy;

        if (!hasDocker && !inspection.HasRunningLocalHostServices)
        {
            Console.WriteLine("[ControlPanel] [EXIT-MODAL] Runtime is idle; closing NEXUS Control Panel immediately without prompt.");
            _orchestrator.AppendLog(new LogMessage
            {
                ServiceName = "CONTROL PANEL",
                Message = "[EXIT-MODAL] No active Docker or local cluster services detected — exiting silently.",
                Level = "INFO"
            });
            OnRequestDetachExit?.Invoke("WindowCloseIdle");
            return;
        }

        if (hasDocker)
        {
            IsDockerExitMode = true;
            if (inspection.IsDockerStackActive || (Dashboard.IsDockerStackBusy && inspection.RunningDockerContainerCount == 0))
            {
                ExitModalBadgeText = "🐳 DOCKER STACK ACTIVE";
                ExitModalTitle = "Shut Down Docker Stack Too?";
                string containerSummary = inspection.RunningDockerContainerCount > 0
                    ? $"{inspection.RunningDockerContainerCount} active container(s)"
                    : "a Docker stack operation is currently in progress";
                ExitModalDescription =
                    $"The ProjectKongor Docker stack is currently running in the background ({containerSummary}). " +
                    "Would you also like to shut down the Docker stack, or just close the Control Panel?";
            }
            else
            {
                ExitModalBadgeText = "🐳 DOCKER CONTAINERS ACTIVE";
                ExitModalTitle = "Shut Down Docker Stack Too?";
                ExitModalDescription =
                    $"ProjectKongor Docker game server container(s) are running in the background ({string.Join(", ", inspection.RunningDockerContainerNames)}). " +
                    "Would you like to shut them down too, or just close the Control Panel?";
            }

            ExitModalSubtext =
                "Default: No — 'Close Panel Only' keeps Docker services and WILLOWMAKER / HoN running uninterrupted.";
            IsExitConfirmationModalOpen = true;
            Console.WriteLine(
                $"[ControlPanel] [EXIT-MODAL] Opened 3-button Docker close confirmation modal (stackActive={inspection.IsDockerStackActive}, count={inspection.RunningDockerContainerCount}, default='Close Panel Only').");
            return;
        }

        IsDockerExitMode = false;
        ExitModalBadgeText = "⚡ LOCAL CLUSTER ACTIVE";
        ExitModalTitle = "Stop Local Cluster & Exit?";
        ExitModalDescription =
            "Local ProjectKongor host services are currently running on this machine. " +
            "Closing NEXUS Control Panel will stop the local cluster and exit.";
        ExitModalSubtext = "Click Cancel to keep NEXUS Control Panel and your local cluster running.";
        IsExitConfirmationModalOpen = true;
        Console.WriteLine("[ControlPanel] [EXIT-MODAL] Opened 2-button Local Cluster close confirmation modal.");
    }

    [RelayCommand]
    public void ConfirmExitKeepDockerRunning()
    {
        Console.WriteLine("[ControlPanel] [EXIT-MODAL] User chose 'Close Panel Only (Keep Docker Running)' — detaching without stopping services.");
        _orchestrator.AppendLog(new LogMessage
        {
            ServiceName = "CONTROL PANEL",
            Message = "[EXIT-MODAL] User chose 'Close Panel Only' — detaching silently with Docker/client preserved.",
            Level = "INFO"
        });
        IsExitConfirmationModalOpen = false;
        OnRequestDetachExit?.Invoke("UserConfirmedKeepDockerRunning");
    }

    [RelayCommand]
    public void ConfirmExitShutDownServices()
    {
        string reason = IsDockerExitMode
            ? "UserConfirmedShutDownDockerAndExit"
            : "UserConfirmedStopLocalClusterAndExit";
        Console.WriteLine($"[ControlPanel] [EXIT-MODAL] User chose '{ExitShutDownButtonText}' (reason={reason}) — initiating full graceful service shutdown.");
        _orchestrator.AppendLog(new LogMessage
        {
            ServiceName = "CONTROL PANEL",
            Message = $"[EXIT-MODAL] User confirmed '{ExitShutDownButtonText}' — initiating full shutdown.",
            Level = "INFO"
        });
        IsExitConfirmationModalOpen = false;
        OnRequestFullShutdownExit?.Invoke(reason);
    }

    [RelayCommand]
    public void CancelExitConfirmation()
    {
        Console.WriteLine("[ControlPanel] [EXIT-MODAL] User cancelled window close confirmation modal.");
        IsExitConfirmationModalOpen = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Dashboard.Dispose();
        GameServers.Dispose();
        LiveLogs.Dispose();
    }
}
