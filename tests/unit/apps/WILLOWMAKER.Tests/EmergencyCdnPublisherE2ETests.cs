// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EmergencyCdnPublisherE2ETests.cs
// System: NEXUS CDN Broker & WILLOWMAKER
// Subsystem: Emergency Tester CDN Update Plan (1-Command Publisher & E2E Suite)
// ============================================================================

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Enumerations;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Update;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class EmergencyCdnPublisherE2ETests
{
    private sealed class EphemeralStaticFileServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _listenTask;
        public volatile string ActiveRootDir;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}/";

        public EphemeralStaticFileServer(string initialRootDir)
        {
            ActiveRootDir = initialRootDir;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _listenTask = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine)) return;

                    while (true)
                    {
                        string? headerLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(headerLine)) break;
                    }

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0].ToUpperInvariant();
                    string rawPath = parts[1];

                    int queryIdx = rawPath.IndexOf('?');
                    if (queryIdx >= 0) rawPath = rawPath[..queryIdx];
                    string relPath = rawPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    string unescaped = Uri.UnescapeDataString(relPath);
                    string fullPath = Path.Combine(ActiveRootDir, unescaped);

                    if (File.Exists(fullPath))
                    {
                        byte[] fileBytes = await File.ReadAllBytesAsync(fullPath, _cts.Token).ConfigureAwait(false);
                        string contentType = fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/octet-stream";
                        string header = $"HTTP/1.1 200 OK\r\nContent-Length: {fileBytes.Length}\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n";
                        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(headerBytes, _cts.Token).ConfigureAwait(false);
                        if (method == "GET")
                        {
                            await stream.WriteAsync(fileBytes, _cts.Token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        byte[] notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                        await stream.WriteAsync(notFound, _cts.Token).ConfigureAwait(false);
                    }
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public void ProductionPublicKey_VerifiesPowerShellSignedBaselineManifestVector()
    {
        // protects: Cross-runtime cryptographic parity between PowerShell 5.1 ECDsaCng publisher and .NET 11 ManifestSignatureVerifier with production ECDSA P-256 public key.
        // fails_when: Manifest canonical transcript formatting differs between PowerShell and C#, or the production public key fails to verify PowerShell-generated signatures.
        // seam: ManifestSignatureVerifier.VerifyManifest against embedded JSON manifest signed by scripts/publish_cdn_update.ps1.

        const string BaselineManifestJson = """
        {
            "protocol": "pkv-v5",
            "version": "5.0.0",
            "sequence": 1,
            "hashAlgorithm": "SHA-256",
            "excludeFromSource": [],
            "excludeFromTarget": [],
            "files": {},
            "mode": "overlay",
            "objectLayout": "sha256",
            "remove": [],
            "executablePaths": [],
            "signature": "DRT1A9Fd6Qj8suQLK3ehyfz5j7N38JjdVh4nW09cHoTkBFko4x1Uahwi2EpWBPdhbVb5JwRmIRUsad0UUZ2k8Q=="
        }
        """;

        Manifest? manifest = JsonSerializer.Deserialize(BaselineManifestJson, ManifestJSONContext.Default.Manifest);
        manifest.Should().NotBeNull();

        bool verified = ManifestSignatureVerifier.VerifyManifest(manifest!, out string? reason);
        reason.Should().BeNull();
        verified.Should().BeTrue();
    }

    [Fact]
    public void OfflineRecoveryPublicKey_VerifiesPowerShellSignedBaselineManifestVector_WithoutOverrides()
    {
        // protects: Dual-oracle trust model allowing offline emergency recovery key (recovery-2026) to sign manifests accepted by default client verifiers without code overrides.
        // fails_when: ManifestSignatureVerifier fails to fallback to recovery key coordinates or rejects recovery-signed canonical transcript.
        // seam: ManifestSignatureVerifier.VerifyManifest against embedded recovery-signed JSON manifest.

        const string RecoveryManifestJson = """
        {
            "protocol": "pkv-v5",
            "version": "5.0.0",
            "sequence": 1,
            "hashAlgorithm": "SHA-256",
            "excludeFromSource": [],
            "excludeFromTarget": [],
            "files": {},
            "mode": "overlay",
            "objectLayout": "sha256",
            "remove": [],
            "executablePaths": [],
            "signature": "nQXD6qyh0MjEpjVZeup8skoF5lFEREYB/WZ+1i2LvWvW/cXLaOXqhxOtiqtmv9ZPdkBI3yIHjCnF5eZbdCYCvg=="
        }
        """;

        Manifest? manifest = JsonSerializer.Deserialize(RecoveryManifestJson, ManifestJSONContext.Default.Manifest);
        manifest.Should().NotBeNull();

        bool verified = ManifestSignatureVerifier.VerifyManifest(manifest!, out string? reason);
        reason.Should().BeNull();
        verified.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task EndToEnd_LoopbackHttp_VerifyZeroChurnWriteBudget_ServerJunctionParity_AndRollback()
    {
        // protects: End-to-end zero-churn write budget, server directory junction parity, and rollback integrity over real loopback HTTP.
        // fails_when: Manifest fetch, CAS download, server junction mirroring, or rollback leaves unverified files, writes unnecessary bytes, or fails cryptographic verification.
        // seam: MainViewModel.CheckForCdnUpdates -> ContentBroker.Synchronise -> real HTTP socket -> real filesystem bundle root.

        string? stageRoot = Environment.GetEnvironmentVariable("KONGOR_E2E_CDN_STAGE_ROOT");
        if (!NexusTestEnvironment.IsManualTestEnabled && string.IsNullOrWhiteSpace(stageRoot))
            return;

        if (string.IsNullOrWhiteSpace(stageRoot))
            return;

        string stageV020 = Path.Combine(stageRoot, "v0.2.0");
        string stageV021 = Path.Combine(stageRoot, "v0.2.1");
        string stageV022 = Path.Combine(stageRoot, "v0.2.2");

        await using EphemeralStaticFileServer server = new(stageV020);

        string scratchBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_scratch_bundle_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(scratchBundleRoot, "games", "client");
        string clientOverrideDir = Path.Combine(clientDir, "override");
        string clientGameDir = Path.Combine(clientDir, "game");
        string clientUserdataDir = Path.Combine(clientDir, "userdata");

        string serverDir = Path.Combine(scratchBundleRoot, "games", "server");
        string serverGameDir = Path.Combine(serverDir, "game");
        string serverOverrideJunction = Path.Combine(serverDir, "override");

        Directory.CreateDirectory(clientOverrideDir);
        Directory.CreateDirectory(clientGameDir);
        Directory.CreateDirectory(clientUserdataDir);
        Directory.CreateDirectory(serverGameDir);

        try
        {
            // 1. games/client/hon_x64.exe (dummy stub)
            string honExe = Path.Combine(clientDir, "hon_x64.exe");
            await File.WriteAllBytesAsync(honExe, [0x4D, 0x5A, 0x90, 0x00]);

            // 2. games/client/override/resources00.s2z (valid minimal zip/s2z archive)
            string resources00Path = Path.Combine(clientOverrideDir, "resources00.s2z");
            using (FileStream fs = File.Create(resources00Path))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("keep.txt");
                using StreamWriter writer = new(entry.Open());
                await writer.WriteAsync("initial override s2z");
            }
            DateTime resources00OriginalMtime = File.GetLastWriteTimeUtc(resources00Path);

            // 3. games/client/game/resourcesa0.s2z + hardlink games/server/game/resourcesa0.s2z
            string clientArchivePath = Path.Combine(clientGameDir, "resourcesa0.s2z");
            using (FileStream fs = File.Create(clientArchivePath))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("game.txt");
                using StreamWriter writer = new(entry.Open());
                await writer.WriteAsync("initial base archive");
            }
            DateTime baseArchiveOriginalMtime = File.GetLastWriteTimeUtc(clientArchivePath);

            string serverArchivePath = Path.Combine(serverGameDir, "resourcesa0.s2z");
            bool hardlinked = NativeFileOperations.CreateHardLink(serverArchivePath, clientArchivePath);
            hardlinked.Should().BeTrue();
            NativeFileOperations.GetHardLinkCount(clientArchivePath).Should().Be(2);
            NativeFileOperations.GetHardLinkCount(serverArchivePath).Should().Be(2);

            // 4. games/server/override directory junction -> games/client/override
            using (System.Diagnostics.Process proc = new())
            {
                proc.StartInfo.FileName = "cmd.exe";
                proc.StartInfo.Arguments = $"/c mklink /J \"{serverOverrideJunction}\" \"{clientOverrideDir}\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();
                await proc.WaitForExitAsync();
                proc.ExitCode.Should().Be(0);
            }
            Directory.Exists(serverOverrideJunction).Should().BeTrue();

            // 5. games/client/userdata/settings.cfg ("SetSave cg_cloud 1")
            string userdataPath = Path.Combine(clientUserdataDir, "settings.cfg");
            await File.WriteAllTextAsync(userdataPath, "SetSave cg_cloud 1\n");
            DateTime userdataOriginalMtime = File.GetLastWriteTimeUtc(userdataPath);

            // Configure MainViewModel with NO CdnPublicKeyXHexOverride and NO CdnHttpHandlerOverride!
            MainViewModel vm = new()
            {
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalOfflineAuto,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnLocalOfflineAuto,
                    TargetURL = "http://localhost:55555/cdn/"
                }
            };

            vm.BundleRootOverride = scratchBundleRoot;
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            vm.LocalMasterServerPortProbe = (_, _) => Task.FromResult(false);
            vm.ConfirmUpdatePrompt = _ => Task.FromResult(true);
            vm.PrimaryEmergencyCdnBaseUrl = server.BaseUrl;

            // ====================================================================
            // Stage A: v0.2.0 baseline empty
            // ====================================================================
            server.ActiveRootDir = stageV020;
            vm.CanCheckForCdnUpdates.Should().BeTrue();
            await vm.CheckForCdnUpdates();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);

            string[] overrideFilesA = Directory.GetFiles(clientOverrideDir);
            overrideFilesA.Should().Equal([resources00Path]);
            File.GetLastWriteTimeUtc(resources00Path).Should().Be(resources00OriginalMtime);

            string ledgerPath = Path.Combine(clientDir, "KONGOR", "cdn_install_ledger.json");
            File.Exists(ledgerPath).Should().BeFalse();

            // ====================================================================
            // Stage B: v0.2.1 hotfix with 2 loose .lua files
            // ====================================================================
            server.ActiveRootDir = stageV021;
            await vm.CheckForCdnUpdates();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            vm.HasAvailableCdnUpdate.Should().BeTrue();
            await vm.DownloadCdnUpdate();

            string lua1 = Path.Combine(clientOverrideDir, "ui", "fe2", "store2.lua");
            string lua2 = Path.Combine(clientOverrideDir, "ui", "scripts", "hotfix.lua");
            File.Exists(lua1).Should().BeTrue();
            File.Exists(lua2).Should().BeTrue();
            File.Exists(ledgerPath).Should().BeTrue();

            // Server junction parity: identical files and bytes under games/server/override
            string srvLua1 = Path.Combine(serverOverrideJunction, "ui", "fe2", "store2.lua");
            string srvLua2 = Path.Combine(serverOverrideJunction, "ui", "scripts", "hotfix.lua");
            File.Exists(srvLua1).Should().BeTrue();
            File.Exists(srvLua2).Should().BeTrue();
            (await File.ReadAllBytesAsync(srvLua1)).Should().Equal(await File.ReadAllBytesAsync(lua1));
            (await File.ReadAllBytesAsync(srvLua2)).Should().Equal(await File.ReadAllBytesAsync(lua2));

            // ====================================================================
            // Stage C: v0.2.1 repeat check (zero churn write budget)
            // ====================================================================
            DateTime lua1MtimeB = File.GetLastWriteTimeUtc(lua1);
            DateTime lua2MtimeB = File.GetLastWriteTimeUtc(lua2);
            DateTime ledgerMtimeB = File.GetLastWriteTimeUtc(ledgerPath);

            await Task.Delay(25);

            await vm.CheckForCdnUpdates();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            vm.HasAvailableCdnUpdate.Should().BeFalse();

            File.GetLastWriteTimeUtc(lua1).Should().Be(lua1MtimeB);
            File.GetLastWriteTimeUtc(lua2).Should().Be(lua2MtimeB);
            File.GetLastWriteTimeUtc(ledgerPath).Should().Be(ledgerMtimeB);

            // ====================================================================
            // Stage D: v0.2.2 rollback removing 1 .lua file
            // ====================================================================
            server.ActiveRootDir = stageV022;
            await vm.CheckForCdnUpdates();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            vm.HasAvailableCdnUpdate.Should().BeTrue();
            await vm.DownloadCdnUpdate();

            // Rolled-back .lua file removed from both games/client/override and games/server/override
            File.Exists(lua2).Should().BeFalse();
            File.Exists(srvLua2).Should().BeFalse();

            // Remaining .lua file kept
            File.Exists(lua1).Should().BeTrue();
            File.Exists(srvLua1).Should().BeTrue();

            // Untouched files check
            File.Exists(resources00Path).Should().BeTrue();
            File.GetLastWriteTimeUtc(resources00Path).Should().Be(resources00OriginalMtime);

            File.Exists(userdataPath).Should().BeTrue();
            File.GetLastWriteTimeUtc(userdataPath).Should().Be(userdataOriginalMtime);

            File.Exists(clientArchivePath).Should().BeTrue();
            File.GetLastWriteTimeUtc(clientArchivePath).Should().Be(baseArchiveOriginalMtime);

            NativeFileOperations.GetHardLinkCount(clientArchivePath).Should().Be(2);
            NativeFileOperations.GetHardLinkCount(serverArchivePath).Should().Be(2);

            // ====================================================================
            // Stage E: Anti-replay / sequence downgrade rejection
            // Switching server back to stageV021 (sequence 2) after v0.2.2 (sequence 3)
            // must be rejected as a sequence downgrade with 0 files modified on disk.
            // ====================================================================
            DateTime lua1MtimeD = File.GetLastWriteTimeUtc(lua1);
            DateTime ledgerMtimeD = File.GetLastWriteTimeUtc(ledgerPath);

            server.ActiveRootDir = stageV021;
            await vm.CheckForCdnUpdates();
            vm.SynchronisationIsFailed.Should().BeTrue();
            vm.SynchronisationStatusMessage.Should().Contain("sequence downgrade rejected");

            // Zero disk writes verification: files and ledger must be completely untouched
            File.Exists(lua2).Should().BeFalse();
            File.Exists(lua1).Should().BeTrue();
            File.GetLastWriteTimeUtc(lua1).Should().Be(lua1MtimeD);
            File.GetLastWriteTimeUtc(ledgerPath).Should().Be(ledgerMtimeD);
        }
        finally
        {
            if (Directory.Exists(serverOverrideJunction))
            {
                try { Directory.Delete(serverOverrideJunction); } catch { }
            }
            if (Directory.Exists(scratchBundleRoot))
            {
                try { Directory.Delete(scratchBundleRoot, true); } catch { }
            }
        }
    }
}
