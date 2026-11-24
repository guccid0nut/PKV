// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.IPC;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Services.P2P;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class CockpitIpcAndCrossPlatformTests
{
    private static List<ComboBoxItem> CreateStandardMasterComboItems() =>
    [
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerOfflineMode },
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalhost55555 },
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo },
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerKongorNet },
        new ComboBoxItem { Content = EndpointPairRegistry.MasterServerCustomAddress }
    ];

    [Fact]
    public async Task CockpitIpc_SyncEndpoints_Localhost55555_UpdatesMasterAndCdn_ClearsSyncFailure_AndBringsWindowToFront()
    {
        // protects: Live Named Pipe cockpit IPC synchronisation from NEXUS.ControlPanel into a running WILLOWMAKER window.
        // fails_when: IPC opcode 0x0030/0x0034 fails to frame/deserialize, leaves CDN mismatched, fails to clear stale sync errors, or skips BringToFront.
        // seam: WillowmakerIpcClient <-> NamedPipeStream <-> WillowmakerIpcService <-> MainViewModel.ApplyCockpitEndpointSyncAsync.

        string pipeName = $"wm-cockpit-local-{Guid.NewGuid():N}";
        MainViewModel vm = new();
        List<ComboBoxItem> comboItems = CreateStandardMasterComboItems();
        vm.RegisterMasterServerComboItems(comboItems);
        vm.MasterServerAddress = comboItems[1]; // Start in Offline Mode
        vm.SynchronisationIsFailed = true;
        vm.SynchronisationStatusMessage = "Previous connection failed";

        int focusCalls = 0;
        vm.RequestBringToFront = () =>
        {
            focusCalls++;
            return true;
        };

        try
        {
            bool listening = await vm.EnsureCockpitIpcServerAsync(pipeName, persistAcrossSessions: true);
            Assert.True(listening);

            await using WillowmakerIpcClient client = new(pipeName);
            await client.ConnectAsync(timeoutMs: 2000);

            CockpitEndpointSyncResponseDto? syncResp = await client.SyncEndpointsAsync(new CockpitEndpointSyncRequestDto
            {
                MasterServerEndpoint = "localhost:55555",
                CdnEndpoint = "localhost:55555/cdn",
                BringToFront = true,
                Source = "NEXUS.ControlPanel.Test"
            });

            Assert.NotNull(syncResp);
            Assert.True(syncResp!.Success);
            Assert.True(syncResp.BroughtToFront);
            Assert.Equal(1, focusCalls);
            Assert.Equal("localhost:55555", syncResp.AppliedMasterServer);
            Assert.Equal("localhost:55555/cdn", syncResp.AppliedCdnEndpoint);
            Assert.False(vm.SynchronisationIsFailed);
            Assert.Equal(string.Empty, vm.SynchronisationStatusMessage);
            Assert.NotNull(vm.SelectedCDNAddressItem);
            Assert.Equal("localhost:55555/cdn", vm.SelectedCDNAddressItem.TargetURL);

            CockpitLauncherStatusDto? status = await client.GetLauncherStatusAsync();
            Assert.NotNull(status);
            Assert.Equal(Environment.ProcessId, status!.ProcessId);
            Assert.Equal("localhost:55555", status.MasterServerEndpoint);
            Assert.Equal("localhost:55555/cdn", status.CdnEndpoint);

            bool focused = await client.BringToFrontAsync();
            Assert.True(focused);
            Assert.Equal(2, focusCalls);
        }
        finally
        {
            await vm.StopCockpitIpcAsync();
            vm.DisposeApplicationBackgroundServices();
        }
    }

    [Fact]
    public async Task CockpitIpc_SyncEndpoints_WanCustomEndpoint_SwitchesToCustomMasterAndPairedCdn()
    {
        // protects: Live WAN endpoint synchronisation when Control Panel is configured in Public Internet mode.
        // fails_when: Custom WAN host:port fails validation, fails to switch MasterServerAddress to Custom Address, or leaves CDN pointing at localhost.
        // seam: WillowmakerIpcClient.SyncEndpointsAsync <-> MainViewModel.ApplyCockpitEndpointSyncAsync (Custom Address branch).

        string pipeName = $"wm-cockpit-wan-{Guid.NewGuid():N}";
        MainViewModel vm = new();
        List<ComboBoxItem> comboItems = CreateStandardMasterComboItems();
        vm.RegisterMasterServerComboItems(comboItems);
        vm.MasterServerAddress = comboItems[2]; // Start on localhost:55555

        try
        {
            bool listening = await vm.EnsureCockpitIpcServerAsync(pipeName, persistAcrossSessions: true);
            Assert.True(listening);

            await using WillowmakerIpcClient client = new(pipeName);
            await client.ConnectAsync(timeoutMs: 2000);

            CockpitEndpointSyncResponseDto? resp = await client.SyncEndpointsAsync(new CockpitEndpointSyncRequestDto
            {
                MasterServerEndpoint = "203.0.113.42:55555",
                CdnEndpoint = "203.0.113.42:55555/cdn",
                BringToFront = false,
                Source = "NEXUS.ControlPanel.WAN"
            });

            Assert.NotNull(resp);
            Assert.True(resp!.Success);
            Assert.Equal("203.0.113.42:55555", resp.AppliedMasterServer);
            Assert.Equal("203.0.113.42:55555/cdn", resp.AppliedCdnEndpoint);
            Assert.Equal("203.0.113.42:55555", vm.CustomMasterServerAddress);
            Assert.True(vm.CanShowCustomMasterServerAddressField);
        }
        finally
        {
            await vm.StopCockpitIpcAsync();
            vm.DisposeApplicationBackgroundServices();
        }
    }

    [Fact]
    public void TryResolveGameExecutable_CrossPlatformLayouts_ResolvesLinuxAndMacAppBundle()
    {
        // protects: Linux (linux-x64/hon-x86_64) and macOS (.app/Contents/MacOS/HoN64) game executable discovery.
        // fails_when: Windows-only .exe assumptions prevent WILLOWMAKER from locating POSIX client binaries in exported bundles.
        // seam: MainViewModel.TryResolveGameExecutable filesystem layout resolution.

        string tempRoot = Path.Combine(Path.GetTempPath(), $"wm-xplat-{Guid.NewGuid():N}");
        try
        {
            MainViewModel vm = new();
            string linuxDir = Path.Combine(tempRoot, "linux-bundle");
            Directory.CreateDirectory(Path.Combine(linuxDir, "linux-x64"));
            string linuxBin = Path.Combine(linuxDir, "linux-x64", "hon-x86_64");
            File.WriteAllBytes(linuxBin, [0x7F, (byte)'E', (byte)'L', (byte)'F']);

            bool foundLinux = vm.TryResolveGameExecutable(
                out FileInfo? resolvedLinux,
                currentDirOverride: linuxDir,
                baseDirOverride: linuxDir,
                targetPlatformOverride: "linux");
            Assert.True(foundLinux);
            Assert.NotNull(resolvedLinux);
            Assert.Equal(Path.GetFullPath(linuxBin), resolvedLinux!.FullName);

            string macDir = Path.Combine(tempRoot, "mac-bundle");
            string macAppBin = Path.Combine(macDir, "Heroes of Newerth.app", "Contents", "MacOS", "HoN64");
            Directory.CreateDirectory(Path.GetDirectoryName(macAppBin)!);
            File.WriteAllBytes(macAppBin, [0xCF, 0xFA, 0xED, 0xFE]);

            bool foundMac = vm.TryResolveGameExecutable(
                out FileInfo? resolvedMac,
                currentDirOverride: macDir,
                baseDirOverride: macDir,
                targetPlatformOverride: "macos");
            Assert.True(foundMac);
            Assert.NotNull(resolvedMac);
            Assert.Equal(Path.GetFullPath(macAppBin), resolvedMac!.FullName);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void P2pHostServerService_ResolveCompanionServerDirectory_SupportsPosixNestedClientSubdirectories()
    {
        // protects: P2P Direct Play companion K2_SERVER discovery when clientDirectory points at games/client/linux-x64 or games/client/macos-x64.
        // fails_when: Single-level Directory.GetParent(clientDir) stops at games/client instead of reaching sibling games/server_p2p or games/server.
        // seam: P2pHostServerService.ResolveCompanionServerDirectory and ResolveCompanionServerExecutable.

        string tempRoot = Path.Combine(Path.GetTempPath(), $"wm-p2p-xplat-{Guid.NewGuid():N}");
        try
        {
            string linuxClientDir = Path.Combine(tempRoot, "games", "client", "linux-x64");
            string linuxServerP2p = Path.Combine(tempRoot, "games", "server_p2p");
            Directory.CreateDirectory(linuxClientDir);
            Directory.CreateDirectory(Path.Combine(linuxServerP2p, "linux-x64"));
            string linuxServerBin = Path.Combine(linuxServerP2p, "linux-x64", "hon-x86_64-server");
            File.WriteAllBytes(linuxServerBin, [0x7F, (byte)'E', (byte)'L', (byte)'F']);

            string? resolvedServerDir = P2pHostServerService.ResolveCompanionServerDirectory(linuxClientDir, "linux");
            Assert.Equal(linuxServerP2p, resolvedServerDir);
            Assert.Equal(linuxServerBin, P2pHostServerService.ResolveCompanionServerExecutable(linuxServerP2p, "linux"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
