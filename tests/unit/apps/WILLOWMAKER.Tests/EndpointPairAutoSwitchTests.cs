// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Avalonia.Controls;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class EndpointPairAutoSwitchTests
{
    [Fact]
    public void ForwardAutoSwitch_AcrossAllMasterServerSelections_UpdatesPairedCdn()
    {
        // protects: Deterministic 1-to-1 forward auto-switching from Master Server to paired CDN across all 6 ecosystem options.
        // fails_when: Switching Master Server leaves CDN stuck on a mismatched domain or unselected.
        // seam: MainViewModel.OnMasterServerAddressChanged and EndpointPairRegistry.ResolvePairedCdnOption.

        MainViewModel vm = new();
        List<ComboBoxItem> comboItems =
        [
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
        ];
        vm.RegisterMasterServerComboItems(comboItems);

        // 1. Local / Offline Auto
        vm.MasterServerAddress = comboItems[0];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal(EndpointPairRegistry.CdnLocalOfflineAuto, vm.SelectedCDNAddressItem.DisplayText);

        // 2. masterserver.newbs.io -> cdn.newbs.io
        vm.MasterServerAddress = comboItems[3];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal(EndpointPairRegistry.CdnNewbsIo, vm.SelectedCDNAddressItem.DisplayText);
        Assert.Equal(EndpointPairRegistry.CdnNewbsIo, vm.SelectedCDNAddressItem.TargetURL);

        // 3. api.kongor.net -> cdn.newbs.io (while kongor.net PKV is disabled); cdn.kongor.net when enabled
        vm.MasterServerAddress = comboItems[4];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal(EndpointPairRegistry.CdnNewbsIo, vm.SelectedCDNAddressItem.DisplayText);

        try
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = true;
            vm.MasterServerAddress = comboItems[3];
            vm.MasterServerAddress = comboItems[4];
            Assert.NotNull(vm.SelectedCDNAddressItem);
            Assert.Equal(EndpointPairRegistry.CdnKongorNet, vm.SelectedCDNAddressItem.DisplayText);
            Assert.Equal(EndpointPairRegistry.CdnKongorNet, vm.SelectedCDNAddressItem.TargetURL);
        }
        finally
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = false;
        }

        // 4. Offline Mode -> Offline Mode (Local Files Only)
        vm.MasterServerAddress = comboItems[1];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal(EndpointPairRegistry.CdnOfflineMode, vm.SelectedCDNAddressItem.DisplayText);
        Assert.Equal("offline", vm.SelectedCDNAddressItem.TargetURL);

        // 5. localhost:55555 -> localhost:55555/cdn
        vm.MasterServerAddress = comboItems[2];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal(EndpointPairRegistry.CdnLocalhost55555, vm.SelectedCDNAddressItem.DisplayText);
        Assert.Equal("localhost:55555/cdn", vm.SelectedCDNAddressItem.TargetURL);

        // 6. Custom Address ... with host
        vm.MasterServerAddress = comboItems[5];
        vm.CustomMasterServerAddress = "192.168.1.50:55555";
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.Equal("192.168.1.50:55555/cdn", vm.SelectedCDNAddressItem.DisplayText);

        // 7. Custom Address ... without host
        vm.CustomMasterServerAddress = string.Empty;
        vm.MasterServerAddress = comboItems[5];
        Assert.NotNull(vm.SelectedCDNAddressItem);
        Assert.True(vm.SelectedCDNAddressItem.IsCustom);
    }

    [Fact]
    public void ReverseAutoSwitch_AcrossAllCdnSelections_UpdatesPairedMasterServer_WithoutRecursion()
    {
        // protects: Reverse auto-switching from CDN selection back to paired Master Server without infinite event loops or recursion.
        // fails_when: Selecting a CDN item fails to update Master Server or triggers a stack overflow exception.
        // seam: MainViewModel.OnSelectedCDNAddressItemChanged re-entrancy guard and EndpointPairRegistry.ResolvePairedMasterServerSelection.

        MainViewModel vm = new();
        List<ComboBoxItem> comboItems =
        [
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
        ];
        vm.RegisterMasterServerComboItems(comboItems);

        // Initial populate of all CDN options
        vm.PopulateCDNOptions(EndpointPairRegistry.MasterServerLocalOfflineAuto);
        Assert.NotEmpty(vm.AvailableCDNOptions);

        // 1. Select cdn.newbs.io -> masterserver.newbs.io
        AddressSelectItem newbsCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnNewbsIo);
        vm.SelectedCDNAddressItem = newbsCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerNewbsIo, vm.MasterServerAddress?.Content?.ToString());

        // 2. Select cdn.kongor.net -> api.kongor.net
        AddressSelectItem kongorCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnKongorNet);
        vm.SelectedCDNAddressItem = kongorCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerKongorNet, vm.MasterServerAddress?.Content?.ToString());

        // 3. Select api.kongor.net/cdn -> api.kongor.net
        AddressSelectItem kongorServicesCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnKongorNetServices);
        vm.SelectedCDNAddressItem = kongorServicesCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerKongorNet, vm.MasterServerAddress?.Content?.ToString());

        // 4. Select Offline Mode (Local Files Only) -> Offline Mode
        AddressSelectItem offlineCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnOfflineMode);
        vm.SelectedCDNAddressItem = offlineCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerOfflineMode, vm.MasterServerAddress?.Content?.ToString());

        // 5. Select Local / Offline Auto (localhost:55555/cdn) -> Local / Offline Auto (localhost:55555)
        AddressSelectItem localAutoCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnLocalOfflineAuto);
        vm.SelectedCDNAddressItem = localAutoCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerLocalOfflineAuto, vm.MasterServerAddress?.Content?.ToString());

        // 6. Select localhost:55555/cdn -> localhost:55555
        AddressSelectItem localhostCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnLocalhost55555);
        vm.SelectedCDNAddressItem = localhostCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerLocalhost55555, vm.MasterServerAddress?.Content?.ToString());

        // 7. Select Custom Address ... -> Custom Address ...
        AddressSelectItem customCdn = vm.AvailableCDNOptions.First(o => o.IsCustom);
        vm.SelectedCDNAddressItem = customCdn;
        Assert.Equal(EndpointPairRegistry.MasterServerCustomAddress, vm.MasterServerAddress?.Content?.ToString());
    }

    [Fact]
    public void ReverseAutoSwitch_FromOfflineModeToRemoteCdn_UpdatesMasterServer_ClearsOfflineProbe_AndEnablesUpdateCheck()
    {
        // protects: Switching CDN away from Offline Mode immediately transitions MasterServer, clears offline quiet probe status, and enables manual update checking.
        // fails_when: Reverse CDN sync fails to update CanCheckForCdnUpdates or leaves CDNProbe stuck on 'Offline Mode — Local Files'.
        // seam: MainViewModel.OnSelectedCDNAddressItemChanged reverse ordering and ScheduleCDNProbe target resolution.

        MainViewModel vm = new();
        List<ComboBoxItem> comboItems =
        [
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
        ];
        vm.RegisterMasterServerComboItems(comboItems);

        // Start on Offline Mode
        vm.MasterServerAddress = comboItems[1];
        Assert.Equal(EndpointPairRegistry.CdnOfflineMode, vm.SelectedCDNAddressItem?.DisplayText);
        Assert.False(vm.CanCheckForCdnUpdates);
        Assert.Equal("Offline Mode — Local Files", vm.CDNProbe.StatusMessage);

        // Reverse-switch by selecting cdn.newbs.io
        AddressSelectItem newbsCdn = vm.AvailableCDNOptions.First(o => o.DisplayText == EndpointPairRegistry.CdnNewbsIo);
        vm.SelectedCDNAddressItem = newbsCdn;

        // Verify MasterServerAddress updated to masterserver.newbs.io
        Assert.Equal(EndpointPairRegistry.MasterServerNewbsIo, vm.MasterServerAddress?.Content?.ToString());

        // Verify CanCheckForCdnUpdates became true
        Assert.True(vm.CanCheckForCdnUpdates);

        // Verify CDNProbe cleared the offline mode message
        Assert.NotEqual("Offline Mode — Local Files", vm.CDNProbe.StatusMessage);
    }

    [Fact]
    public void LoadLauncherSettings_CrossEcosystemMismatch_AutoMigratesToPairedCdn()
    {
        // protects: Startup self-healing of legacy cross-ecosystem pairings (e.g. masterserver.newbs.io + cdn.kongor.net) in launcher_settings.json.
        // fails_when: Stale saved CDN remains selected after loading settings despite mismatched master server.
        // seam: MainViewModel.LoadLauncherSettings and EndpointPairRegistry.IsCrossEcosystemMismatch.

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_CrossEco_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Case 1: Legacy mismatch masterserver.newbs.io + cdn.kongor.net -> auto-migrated to cdn.newbs.io
            LauncherSettingsModel mismatchSettings = new()
            {
                MasterServerAddress = EndpointPairRegistry.MasterServerNewbsIo,
                SelectedCDNAddress = EndpointPairRegistry.CdnKongorNet
            };
            LauncherSettingsService.Save(mismatchSettings, tempDir);

            MainViewModel vm1 = new();
            vm1.LoadLauncherSettings(tempDir);

            Assert.NotNull(vm1.SelectedCDNAddressItem);
            Assert.Equal(EndpointPairRegistry.CdnNewbsIo, vm1.SelectedCDNAddressItem.DisplayText);

            // Case 2: Matching pair api.kongor.net + api.kongor.net/cdn -> auto-migrated to newbs.io when kongor.net PKV disabled; preserved when enabled
            LauncherSettingsModel compatibleSettings = new()
            {
                MasterServerAddress = EndpointPairRegistry.MasterServerKongorNet,
                SelectedCDNAddress = EndpointPairRegistry.CdnKongorNetServices
            };
            LauncherSettingsService.Save(compatibleSettings, tempDir);

            MainViewModel vm2 = new();
            vm2.LoadLauncherSettings(tempDir);

            Assert.NotNull(vm2.SelectedCDNAddressItem);
            Assert.Equal(EndpointPairRegistry.CdnNewbsIo, vm2.SelectedCDNAddressItem.DisplayText);

            try
            {
                EndpointPairRegistry.IsKongorNetPkvEnabled = true;
                MainViewModel vm2b = new();
                vm2b.LoadLauncherSettings(tempDir);
                Assert.NotNull(vm2b.SelectedCDNAddressItem);
                Assert.Equal(EndpointPairRegistry.CdnKongorNetServices, vm2b.SelectedCDNAddressItem.DisplayText);
            }
            finally
            {
                EndpointPairRegistry.IsKongorNetPkvEnabled = false;
            }

            // Case 3: Custom override preserved verbatim
            LauncherSettingsModel customSettings = new()
            {
                MasterServerAddress = EndpointPairRegistry.MasterServerCustomAddress,
                CustomMasterServerAddress = "10.0.0.99:55555",
                SelectedCDNAddress = "Custom",
                CustomCDNAddress = "http://10.0.0.99:55555/cdn"
            };
            LauncherSettingsService.Save(customSettings, tempDir);

            MainViewModel vm3 = new();
            vm3.LoadLauncherSettings(tempDir);

            Assert.NotNull(vm3.SelectedCDNAddressItem);
            Assert.True(vm3.SelectedCDNAddressItem.IsCustom);
            Assert.Equal("http://10.0.0.99:55555/cdn", vm3.CustomCDNAddress);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task OfflineAndAutoOfflineProbes_QuietState_AndBuildCdnAddress()
    {
        // protects: Offline and Auto-Offline modes from firing failing HTTP probes, setting ready status with zero network requests.
        // fails_when: Offline/Auto-Offline modes trigger red HTTP 404/connection banners or fail BuildCDNAddress normalisation.
        // seam: MainViewModel.ScheduleCDNProbe and MainViewModel.BuildCDNAddress.

        MainViewModel vm = new();

        // 1. Offline Mode: synchronous quiet ready status, CanCheckForCdnUpdates is false, BuildCDNAddress is empty
        vm.MasterServerAddress = new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode };
        Assert.Equal("Offline Mode — Local Files", vm.CDNProbe.StatusMessage);
        Assert.False(vm.CDNProbe.Failed);
        Assert.False(vm.CanCheckForCdnUpdates);
        Assert.Equal(string.Empty, vm.BuildCDNAddress());

        // 2. Local / Offline Auto with server not running: port probe returns false, sets quiet ready status with 0 HTTP calls
        vm.LocalMasterServerPortProbe = (_, _) => Task.FromResult(false);
        vm.MasterServerAddress = new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto };
        // Wait asynchronously for port probe completion under thread-pool load
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && vm.CDNProbe.StatusMessage != "Auto-Offline Active — Local Files")
        {
            await Task.Delay(10);
        }
        Assert.Equal("Auto-Offline Active — Local Files", vm.CDNProbe.StatusMessage);
        Assert.False(vm.CDNProbe.Failed);

        // 3. masterserver.newbs.io -> BuildCDNAddress returns https://cdn.newbs.io/
        vm.MasterServerAddress = new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo };
        Assert.Equal("https://cdn.newbs.io/", vm.BuildCDNAddress());
    }

    [Fact]
    public void WindowsHighDpiLaunchEnv_Helper_AppliesAndSkipsCorrectly()
    {
        // protects: Windows HighDpiAware compatibility layer injection at process startup for non-blurry client rendering.
        // fails_when: HighDpiAware compatibility layer is omitted on Windows or emergency KONGOR_DISABLE_HIGHDPI is ignored.
        // seam: MainViewModel.ApplyWindowsHighDpiEnvironment and MainViewModel.ShouldEnableWindowsHighDpi.

        // 1. Default (no disable variable) -> HighDpiAware enabled
        ProcessStartInfo psiDefault = new();
        string? capturedProcessEnv = null;
        MainViewModel.ApplyWindowsHighDpiEnvironment(
            psiDefault,
            (key, val) => capturedProcessEnv = val,
            disableEnvOverride: string.Empty);

        Assert.Equal("HighDpiAware", psiDefault.EnvironmentVariables["__COMPAT_LAYER"]);
        Assert.Equal("HighDpiAware", capturedProcessEnv);

        // 2. Emergency escape hatch "1" -> skipped
        ProcessStartInfo psiDisabled1 = new();
        string? capturedDisabled1 = null;
        MainViewModel.ApplyWindowsHighDpiEnvironment(
            psiDisabled1,
            (key, val) => capturedDisabled1 = val,
            disableEnvOverride: "1");

        Assert.False(psiDisabled1.EnvironmentVariables.ContainsKey("__COMPAT_LAYER"));
        Assert.Null(capturedDisabled1);

        // 3. Emergency escape hatch "true" / "Y" -> ShouldEnableWindowsHighDpi returns false
        Assert.False(MainViewModel.ShouldEnableWindowsHighDpi("true"));
        Assert.False(MainViewModel.ShouldEnableWindowsHighDpi("Y"));
        Assert.True(MainViewModel.ShouldEnableWindowsHighDpi("0"));
    }

    [Fact]
    public void TryGetMasterServerComboText_ReturnsComboContentOrNullWhenUnset()
    {
        // protects: Safe reading of MasterServerAddress ComboBoxItem selection text across MainViewModel launch and config helpers.
        // fails_when: TryGetMasterServerComboText returns stale/empty string when ComboBoxItem is selected or throws when MasterServerAddress is null.
        // seam: MainViewModel.TryGetMasterServerComboText and Avalonia.Controls.ComboBoxItem.CheckAccess.

        MainViewModel vm = new();
        Assert.Null(vm.TryGetMasterServerComboText());

        vm.MasterServerAddress = new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo };
        Assert.Equal(EndpointPairRegistry.MasterServerNewbsIo, vm.TryGetMasterServerComboText());
    }

    [Fact]
    public void TryResolveCommandLineMasterServerOverride_NormalizesControlPanelLocalhostAndCustomHosts()
    {
        // protects: Control Panel -> Willowmaker local cluster handoff (--masterserver localhost:55555) so client does not silently join remote masterserver.newbs.io when local cluster is active.
        // fails_when: CLI --masterserver / -masterserver / --masterserver= flags are ignored or 127.0.0.1:55555 fails to normalize to localhost:55555.
        // seam: MainViewModel.TryResolveCommandLineMasterServerOverride.
        Assert.Null(MainViewModel.TryResolveCommandLineMasterServerOverride([]));
        Assert.Null(MainViewModel.TryResolveCommandLineMasterServerOverride(["WILLOWMAKER.exe", "--other", "1"]));

        Assert.Equal(
            EndpointPairRegistry.MasterServerLocalhost55555,
            MainViewModel.TryResolveCommandLineMasterServerOverride(["WILLOWMAKER.exe", "--masterserver", "127.0.0.1:55555"]));

        Assert.Equal(
            EndpointPairRegistry.MasterServerLocalhost55555,
            MainViewModel.TryResolveCommandLineMasterServerOverride(["WILLOWMAKER.exe", "--masterserver=http://localhost:55555/"]));

        Assert.Equal(
            EndpointPairRegistry.MasterServerNewbsIo,
            MainViewModel.TryResolveCommandLineMasterServerOverride(["WILLOWMAKER.exe", "-masterserver", "masterserver.newbs.io"]));
    }

    [Fact]
    public async Task StaleLocalAutoPortProbe_DoesNotClobberSubsequentSelectionStatus()
    {
        // protects: Selection-change monotonicity for MasterServerProbe, CDNProbe, and LiveTelemetry when switching away from Local / Offline Auto while a port 55555 TCP probe is in flight.
        // fails_when: A slow background LocalMasterServerPortProbe callback completes after the user switched to Offline Mode or a remote CDN and overwrites CDNProbe/MasterServerProbe status.
        // seam: MainViewModel.ScheduleCDNProbe, ScheduleMasterServerProbe, RefreshTelemetryPollingForSelection, and ConnectivityProbe generation tracking.

        MainViewModel vm = new();
        List<ComboBoxItem> comboItems =
        [
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
            new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
        ];
        vm.RegisterMasterServerComboItems(comboItems);

        TaskCompletionSource<bool> pendingPortProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.LocalMasterServerPortProbe = (_, _) =>
        {
            probeEntered.TrySetResult();
            return pendingPortProbe.Task;
        };

        // 1. Select Local / Offline Auto -> starts in-flight port 55555 probes
        vm.MasterServerAddress = comboItems[0];
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 2. Switch immediately to Offline Mode while the Local / Offline Auto probe is still pending
        vm.MasterServerAddress = comboItems[1];
        Assert.Equal("Offline Mode — Local Files", vm.CDNProbe.StatusMessage);
        Assert.Equal("Offline Mode Ready (Bots, Practice & P2P)", vm.MasterServerProbe.StatusMessage);
        Assert.Equal("Offline Hub", vm.ServerStatusText);

        // 3. Complete the stale Local / Offline Auto port probe (port not listening)
        pendingPortProbe.SetResult(false);
        await Task.Delay(60);

        // 4. Stale callbacks must be discarded; Offline Mode status must remain intact
        Assert.Equal("Offline Mode — Local Files", vm.CDNProbe.StatusMessage);
        Assert.Equal("Offline Mode Ready (Bots, Practice & P2P)", vm.MasterServerProbe.StatusMessage);
        Assert.Equal("Offline Hub", vm.ServerStatusText);
    }

    [Fact]
    public void ResetCdnUpdateStateToOffline_ClearsStaleFailureBanner()
    {
        // protects: Stale PKV Guard or CDN inspection failure banners are cleared when resetting or switching away from failed endpoints.
        // fails_when: Switching from an unreachable or blocked CDN to localhost or offline leaves the red failure message stuck in the UI.
        // seam: MainViewModel.ResetCdnUpdateStateToOffline and MainViewModel.OnSelectedCDNAddressItemChanged.

        MainViewModel vm = new();
        vm.SynchronisationIsFailed = true;
        vm.SynchronisationStatusMessage = "PKV Guard — Updates Blocked (Non-PKV Server)";

        vm.ResetCdnUpdateStateToOffline();

        Assert.False(vm.SynchronisationIsFailed);
        Assert.Equal(string.Empty, vm.SynchronisationStatusMessage);
    }

    [Fact]
    public void SwitchingCdnSelection_WhenPriorCdnFailed_ClearsStaleFailureBanner()
    {
        // protects: Switching CDN selection clears previous sync failure banner.
        // fails_when: Selecting a new CDN retains the previous failure banner.
        // seam: MainViewModel.OnSelectedCDNAddressItemChanged.

        MainViewModel vm = new();
        vm.SynchronisationIsFailed = true;
        vm.SynchronisationStatusMessage = "PKV Guard — Updates Blocked (Non-PKV Server)";

        AddressSelectItem item = new() { DisplayText = EndpointPairRegistry.CdnLocalhost55555, TargetURL = EndpointPairRegistry.CdnLocalhost55555 };
        vm.SelectedCDNAddressItem = item;

        Assert.False(vm.SynchronisationIsFailed);
        Assert.Equal(string.Empty, vm.SynchronisationStatusMessage);
    }
}
