// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.InteropServices;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services.IPC;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel
{
    private bool _cockpitIpcPersistent;
    private string? _cockpitDiscordAppId;
    private WillowmakerIpcService? _boundStreamEventsService;
    private readonly SemaphoreSlim _ipcInitLock = new(1, 1);

    public string? ActiveIpcPipeName => _activeIpcService is { IsRunning: true } ? _activeIpcService.PipeName : null;
    public string? CockpitDiscordAppId => _cockpitDiscordAppId;

    /// <summary>
    ///     Starts or reuses the persistent Named Pipe IPC listener (<c>willowmaker-ipc</c>) so
    ///     <c>NEXUS.ControlPanel</c> can detect a running WILLOWMAKER instance, bring its window to
    ///     the foreground, and synchronise Master Server + CDN endpoints in real time.
    /// </summary>
    public async Task<bool> EnsureCockpitIpcServerAsync(
        string? pipeNameOverride = null,
        bool persistAcrossSessions = true,
        CancellationToken cancellationToken = default)
    {
        string targetPipe = string.IsNullOrWhiteSpace(pipeNameOverride)
            ? WillowmakerIpcService.DefaultPipeName
            : pipeNameOverride.Trim();

        await _ipcInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (persistAcrossSessions)
            {
                _cockpitIpcPersistent = true;
            }

            if (_activeIpcService is { IsRunning: true } existing &&
                string.Equals(existing.PipeName, targetPipe, StringComparison.Ordinal))
            {
                BindCockpitIpcCallbacks(existing);
                await existing.EnsureListeningAsync(1000, cancellationToken).ConfigureAwait(false);
                return existing.IsRunning;
            }

            if (_activeIpcService is not null)
            {
                await _activeIpcService.DisposeAsync().ConfigureAwait(false);
                _activeIpcService = null;
                _boundStreamEventsService = null;
            }

            WillowmakerIpcService ipc = new(
                targetPipe,
                authKey: _activeIpcAuthKey,
                sandboxMode: NEXUS.Contracts.Ipc.IpcSandboxMode.Enforced,
                logAction: msg => Log(LogCategory.Executable, msg));

            BindCockpitIpcCallbacks(ipc);
            await ipc.StartServerAsync(cancellationToken).ConfigureAwait(false);
            await ipc.EnsureListeningAsync(1000, cancellationToken).ConfigureAwait(false);
            _activeIpcService = ipc;

            Log(LogCategory.Executable,
                $"[CockpitIPC] Persistent Named Pipe listener ready on '{targetPipe}' (PersistAcrossSessions={_cockpitIpcPersistent}, PID={Environment.ProcessId}).");

            string? bootMaster = Environment.GetEnvironmentVariable("KONGOR_COCKPIT_MASTER_SERVER");
            string? bootCdn = Environment.GetEnvironmentVariable("KONGOR_COCKPIT_CDN_ENDPOINT");
            string? bootDiscordId = Environment.GetEnvironmentVariable("KONGOR_DISCORD_APP_ID")
                                    ?? Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(bootDiscordId))
            {
                _cockpitDiscordAppId = bootDiscordId.Trim();
            }
            if (!string.IsNullOrWhiteSpace(bootMaster) && pipeNameOverride is null)
            {
                _ = ApplyCockpitEndpointSyncAsync(new CockpitEndpointSyncRequestDto
                {
                    MasterServerEndpoint = bootMaster.Trim(),
                    CdnEndpoint = (bootCdn ?? $"{bootMaster.Trim().TrimEnd('/')}/cdn").Trim(),
                    DiscordClientId = _cockpitDiscordAppId ?? string.Empty,
                    BringToFront = false,
                    Source = "NEXUS.ControlPanel.ColdLaunch"
                });
            }

            return ipc.IsRunning;
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[CockpitIPC] Failed starting Named Pipe listener on '{targetPipe}': {ex.Message}");
            return false;
        }
        finally
        {
            _ipcInitLock.Release();
        }
    }

    internal void BindCockpitIpcCallbacks(WillowmakerIpcService ipc)
    {
        ipc.OnEndpointSyncRequested = ApplyCockpitEndpointSyncAsync;
        ipc.OnBringToFrontRequested = HandleCockpitBringToFrontAsync;
        ipc.OnLauncherStatusRequested = GetCockpitLauncherStatus;

        if (!ReferenceEquals(_boundStreamEventsService, ipc))
        {
            ipc.OnLogStreamReceived += logLine =>
            {
                Log(LogCategory.GameClient, logLine);
                _activeDiscordPresence?.ProcessConsoleLogLine(logLine);
            };
            ipc.OnEngineStateReceived += state =>
            {
                Log(LogCategory.GameClient,
                    $"[ENGINE] FPS: {state.CurrentFps:F1}, Memory: {state.MemoryWorkingSetBytes / (1024 * 1024)} MB, Map: {state.CurrentMap}");
                if (!string.IsNullOrWhiteSpace(state.CurrentMap))
                {
                    _activeDiscordPresence?.UpdateMapContext(state.CurrentMap);
                }
            };
            _boundStreamEventsService = ipc;
        }
    }

    public async Task StopCockpitIpcAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cockpitIpcPersistent = false;
        WillowmakerIpcService? ipc = Interlocked.Exchange(ref _activeIpcService, null);
        _boundStreamEventsService = null;
        if (ipc is null)
        {
            return;
        }

        try
        {
            await ipc.DisposeAsync().ConfigureAwait(false);
            Log(LogCategory.Executable, $"[CockpitIPC] Stopped Named Pipe listener on '{ipc.PipeName}'.");
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[CockpitIPC] Shutdown dispose notice for '{ipc.PipeName}': {ex.Message}");
        }
    }

    public void StopCockpitIpcOnApplicationShutdown()
    {
        _cockpitIpcPersistent = false;
        WillowmakerIpcService? ipc = Interlocked.Exchange(ref _activeIpcService, null);
        _boundStreamEventsService = null;
        if (ipc is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ipc.DisposeAsync().ConfigureAwait(false);
                Log(LogCategory.Executable, $"[CockpitIPC] Stopped Named Pipe listener on '{ipc.PipeName}' during shutdown.");
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable, $"[CockpitIPC] Shutdown dispose notice for '{ipc.PipeName}': {ex.Message}");
            }
        });
    }

    private Task<bool> HandleCockpitBringToFrontAsync()
    {
        bool focused = RequestBringToFront?.Invoke() ?? false;
        Log(LogCategory.General, $"[CockpitIPC] BringToFront invoked (Focused={focused}).");
        return Task.FromResult(focused);
    }

    public CockpitLauncherStatusDto GetCockpitLauncherStatus()
    {
        string activeMaster = GetSelectedMasterServerDisplayEndpoint();
        string activeCdn = CanShowCustomCDNAddressField && !string.IsNullOrWhiteSpace(CustomCDNAddress)
            ? CustomCDNAddress.Trim()
            : StripHttpScheme(SelectedCDNAddressItem?.TargetURL ?? SelectedCDNAddressItem?.DisplayText ?? string.Empty);

        return new CockpitLauncherStatusDto
        {
            ProcessId = Environment.ProcessId,
            Version = VersionDisplay,
            MasterServerEndpoint = activeMaster,
            CdnEndpoint = activeCdn,
            IsGameRunning = LaunchIsInProgress,
            Platform = RuntimeInformation.RuntimeIdentifier
        };
    }

    private string GetSelectedMasterServerDisplayEndpoint()
    {
        if (CanShowCustomMasterServerAddressField && !string.IsNullOrWhiteSpace(CustomMasterServerAddress))
        {
            return CustomMasterServerAddress.Trim();
        }

        string? comboText = TryGetMasterServerComboText();
        if (!string.IsNullOrWhiteSpace(comboText) &&
            !string.Equals(comboText, EndpointPairRegistry.MasterServerCustomAddress, StringComparison.OrdinalIgnoreCase))
        {
            return comboText.Trim();
        }

        return BuildMasterServerAddress();
    }

    public Task<CockpitEndpointSyncResponseDto> ApplyCockpitEndpointSyncAsync(CockpitEndpointSyncRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string rawMaster = request.MasterServerEndpoint?.Trim() ?? string.Empty;
        string cleanMaster = StripHttpScheme(rawMaster);
        if (string.IsNullOrWhiteSpace(cleanMaster) ||
            (!IsOfflineModeSelection(cleanMaster) &&
             !IsLocalOfflineAutoSelection(cleanMaster) &&
             !AddressValidation.IsValidAddress(cleanMaster)))
        {
            Log(LogCategory.General,
                $"[CockpitIPC] Rejected invalid Master Server endpoint '{rawMaster}' from '{request.Source}'.");
            return Task.FromResult(new CockpitEndpointSyncResponseDto
            {
                Success = false,
                ProcessId = Environment.ProcessId,
                Message = $"Invalid Master Server endpoint '{rawMaster}'. Provide host:port or domain (e.g. localhost:55555)."
            });
        }

        string rawCdn = request.CdnEndpoint?.Trim() ?? string.Empty;
        string cleanCdn = StripHttpScheme(rawCdn);
        if (!string.IsNullOrWhiteSpace(cleanCdn) &&
            !string.Equals(cleanCdn, "offline", StringComparison.OrdinalIgnoreCase) &&
            !AddressValidation.IsValidAddress(cleanCdn))
        {
            Log(LogCategory.General,
                $"[CockpitIPC] Rejected invalid CDN endpoint '{rawCdn}' from '{request.Source}'.");
            return Task.FromResult(new CockpitEndpointSyncResponseDto
            {
                Success = false,
                ProcessId = Environment.ProcessId,
                Message = $"Invalid CDN endpoint '{rawCdn}'."
            });
        }

        TaskCompletionSource<CockpitEndpointSyncResponseDto> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? callerSyncCtx = SynchronizationContext.Current ?? _uiSyncContext;

        void ApplyOnUi()
        {
            try
            {
                HasActiveCliMasterServerOverride = false;
                SynchronisationIsFailed = false;
                SynchronisationStatusMessage = string.Empty;

                bool matchesRegisteredPreset = _registeredMasterServerItems.Any(item =>
                    string.Equals(item.Content?.ToString()?.Trim(), cleanMaster, StringComparison.OrdinalIgnoreCase));
                bool isBuiltInKnownMaster =
                    matchesRegisteredPreset ||
                    string.Equals(cleanMaster, "localhost:55555", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanMaster, "127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanMaster, "masterserver.newbs.io", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanMaster, "api.kongor.net", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanMaster, "masterserver.kongor.net", StringComparison.OrdinalIgnoreCase) ||
                    IsOfflineModeSelection(cleanMaster) ||
                    IsLocalOfflineAutoSelection(cleanMaster);

                _isSyncingEndpointPair = true;
                try
                {
                    if (isBuiltInKnownMaster && (_registeredMasterServerItems.Count == 0 || matchesRegisteredPreset))
                    {
                        CanShowCustomMasterServerAddressField = false;
                        CustomMasterServerAddress = null;
                        LoadedMasterServerAddress = cleanMaster;
                        MasterServerAddressIsValid = true;
                        MasterServerAddress = GetOrCreateMasterServerComboItem(cleanMaster);
                        PopulateCDNOptions(cleanMaster, forcePairedSelection: true);
                    }
                    else
                    {
                        LoadedMasterServerAddress = "Custom Address";
                        CanShowCustomMasterServerAddressField = true;
                        CustomMasterServerAddress = cleanMaster;
                        MasterServerAddressIsValid = true;
                        MasterServerAddress = GetOrCreateMasterServerComboItem("Custom Address");
                        PopulateCDNOptions(cleanMaster, forcePairedSelection: true);
                    }

                    if (!string.IsNullOrWhiteSpace(cleanCdn))
                    {
                        string normalizedCdnUrl = AddressValidation.NormaliseCDNURL(cleanCdn).TrimEnd('/');
                        AddressSelectItem? matchedCdn = AvailableCDNOptions.FirstOrDefault(o =>
                            !o.IsCustom &&
                            (string.Equals(StripHttpScheme(o.TargetURL ?? string.Empty), cleanCdn, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals((o.TargetURL ?? string.Empty).TrimEnd('/'), normalizedCdnUrl, StringComparison.OrdinalIgnoreCase)));

                        if (matchedCdn is not null)
                        {
                            CanShowCustomCDNAddressField = false;
                            CustomCDNAddress = null;
                            CDNAddressIsValid = true;
                            SelectedCDNAddressItem = matchedCdn;
                        }
                        else
                        {
                            AddressSelectItem? customItem = AvailableCDNOptions.FirstOrDefault(o => o.IsCustom);
                            if (customItem is not null)
                            {
                                SelectedCDNAddressItem = customItem;
                            }
                            CanShowCustomCDNAddressField = true;
                            CustomCDNAddress = cleanCdn;
                            CDNAddressIsValid = true;
                        }
                    }
                }
                finally
                {
                    _isSyncingEndpointPair = false;
                }

                SynchronisationIsFailed = false;
                SynchronisationStatusMessage = string.Empty;

                ScheduleMasterServerProbe(cleanMaster, TimeSpan.Zero);
                string? activeCdnProbeTarget = CanShowCustomCDNAddressField && !string.IsNullOrWhiteSpace(CustomCDNAddress)
                    ? CustomCDNAddress
                    : SelectedCDNAddressItem?.TargetURL;
                ScheduleCDNProbe(activeCdnProbeTarget, TimeSpan.Zero);
                RefreshTelemetryPollingForSelection();
                if (!string.IsNullOrWhiteSpace(request.DiscordClientId))
                {
                    _cockpitDiscordAppId = request.DiscordClientId.Trim();
                    _activeDiscordPresence?.SetCustomDiscordAppId(_cockpitDiscordAppId);
                }
                SaveLauncherSettings();

                bool focused = request.BringToFront && (RequestBringToFront?.Invoke() ?? false);
                string appliedMaster = GetSelectedMasterServerDisplayEndpoint();
                string appliedCdn = CanShowCustomCDNAddressField && !string.IsNullOrWhiteSpace(CustomCDNAddress)
                    ? CustomCDNAddress.Trim()
                    : StripHttpScheme(SelectedCDNAddressItem?.TargetURL ?? cleanCdn);

                Log(LogCategory.General,
                    $"[CockpitIPC] Synced endpoints from '{request.Source}': Master='{appliedMaster}', CDN='{appliedCdn}', Focused={focused}.");

                tcs.TrySetResult(new CockpitEndpointSyncResponseDto
                {
                    Success = true,
                    AppliedMasterServer = appliedMaster,
                    AppliedCdnEndpoint = appliedCdn,
                    BroughtToFront = focused,
                    ProcessId = Environment.ProcessId,
                    Message = $"Synchronised Master='{appliedMaster}' + CDN='{appliedCdn}'" + (focused ? " and focused WILLOWMAKER." : ".")
                });
            }
            catch (Exception ex)
            {
                Log(LogCategory.General, $"[CockpitIPC] Error applying endpoint sync: {ex.Message}");
                tcs.TrySetResult(new CockpitEndpointSyncResponseDto
                {
                    Success = false,
                    ProcessId = Environment.ProcessId,
                    Message = $"Failed applying endpoint sync: {ex.Message}"
                });
            }
        }

        RunOnUiThread(ApplyOnUi, callerSyncCtx);
        return tcs.Task;
    }

    private static string StripHttpScheme(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[8..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        return trimmed.TrimEnd('/');
    }
}
