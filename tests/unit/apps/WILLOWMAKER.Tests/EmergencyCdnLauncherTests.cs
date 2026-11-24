// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Enumerations;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class EmergencyCdnLauncherTests
{
    [Fact]
    public void ResolveBundleRoot_AcrossWindowsLinuxAndMacOsLayouts_ReturnsUnifiedBundleRoot()
    {
        // protects: Cross-platform bundle root resolution across Windows root, Linux launcher, macOS launcher, and client subdirectories.
        // fails_when: Running WILLOWMAKER from non-root launcher directories or client subdirectories fails to resolve bundle root or resolves an incorrect path.
        // seam: MainViewModel.ResolveBundleRoot structural parent traversal and LocationGuard fallback.

        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_test_bundleroot_" + Guid.NewGuid().ToString("N"));
        string bundleRoot = Path.Combine(tempRoot, "ProjectKongor");
        string gamesClient = Path.Combine(bundleRoot, "games", "client");
        string launcherLinux = Path.Combine(bundleRoot, "launcher-linux-x64");
        string launcherOsx = Path.Combine(bundleRoot, "launcher-osx-arm64");
        string clientLinux = Path.Combine(gamesClient, "linux-x64");
        // Deep isolation hierarchy so an 8-level upward walk cannot escape tempRoot to host root C:\
        string unrelatedDir = Path.Combine(tempRoot, "Unrelated", "d1", "d2", "d3", "d4", "d5", "d6", "d7", "d8", "isolated");

        Directory.CreateDirectory(gamesClient);
        Directory.CreateDirectory(launcherLinux);
        Directory.CreateDirectory(launcherOsx);
        Directory.CreateDirectory(clientLinux);
        Directory.CreateDirectory(unrelatedDir);

        try
        {
            string expected = Path.GetFullPath(bundleRoot);

            // 1. From Windows bundle root
            string? rootFromWindows = MainViewModel.ResolveBundleRoot(bundleRoot);
            rootFromWindows.Should().Be(expected);

            // 2. From Linux launcher subdirectory
            string? rootFromLinux = MainViewModel.ResolveBundleRoot(launcherLinux);
            rootFromLinux.Should().Be(expected);

            // 3. From macOS launcher subdirectory
            string? rootFromOsx = MainViewModel.ResolveBundleRoot(launcherOsx);
            rootFromOsx.Should().Be(expected);

            // 4. From games/client directory
            string? rootFromClient = MainViewModel.ResolveBundleRoot(gamesClient);
            rootFromClient.Should().Be(expected);

            // 5. From games/client/linux-x64 platform subdirectory
            string? rootFromClientLinux = MainViewModel.ResolveBundleRoot(clientLinux);
            rootFromClientLinux.Should().Be(expected);

            // 6. Unrelated directory returns null
            string? rootFromUnrelated = MainViewModel.ResolveBundleRoot(unrelatedDir);
            rootFromUnrelated.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void DetectRunningBundleProcesses_MatchesOnlyProcessesInsideBundleRoot()
    {
        // protects: Process detection boundary guarding active game/server binaries only within the active bundle root.
        // fails_when: Unrelated processes or instances of the game running in other directories incorrectly block updates, or active bundle instances are missed.
        // seam: MainViewModel.DetectRunningBundleProcesses process snapshot evaluation.

        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_test_procguard_" + Guid.NewGuid().ToString("N"));
        string bundleRoot = Path.Combine(tempRoot, "ActiveBundle");
        string otherRoot = Path.Combine(tempRoot, "OtherBundle");

        Directory.CreateDirectory(Path.Combine(bundleRoot, "games", "client"));
        Directory.CreateDirectory(Path.Combine(bundleRoot, "games", "server"));
        Directory.CreateDirectory(Path.Combine(otherRoot, "games", "client"));

        try
        {
            List<(int Pid, string ProcessName, string? ExecutablePath)> processSnapshot =
            [
                (101, "hon_x64", Path.Combine(bundleRoot, "games", "client", "hon_x64.exe")),
                (102, "hon-x86_64-server", Path.Combine(bundleRoot, "games", "server", "hon-x86_64-server")),
                (103, "hon_x64", Path.Combine(otherRoot, "games", "client", "hon_x64.exe")),
                (104, "explorer", Path.Combine(bundleRoot, "explorer.exe"))
            ];

            IReadOnlyList<string> running = MainViewModel.DetectRunningBundleProcesses(bundleRoot, processSnapshot);

            running.Should().HaveCount(2);
            running.Should().Contain("hon_x64 (PID 101)");
            running.Should().Contain("hon-x86_64-server (PID 102)");
            running.Should().NotContain("hon_x64 (PID 103)");
            running.Should().NotContain("explorer (PID 104)");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task CheckForCdnUpdates_WhenBundleProcessIsRunning_BlocksUpdateBeforeNetworkFetch()
    {
        // protects: Running game process guard blocking CDN sync attempts before any network fetch or filesystem mutation occurs.
        // fails_when: Manifest fetch or update plan executes while active game processes are locking bundle files.
        // seam: MainViewModel.CheckForCdnUpdates pre-fetch bundle process check.

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

        vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
        vm.BundleRootOverride = "C:\\Games\\ProjectKongor";
        vm.RunningBundleProcessDetectorOverride = _ => ["hon_x64 (PID 4242)"];

        int requestsSent = 0;
        vm.CdnHttpHandlerOverride = new StubRouteHandler(_ =>
        {
            Interlocked.Increment(ref requestsSent);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await vm.CheckForCdnUpdates();

        requestsSent.Should().Be(0);
        vm.SynchronisationIsFailed.Should().BeTrue();
        vm.SynchronisationStatusMessage.Should().Contain("Close Heroes of Newerth / COMPEL");
    }

    [Fact]
    public async Task InspectCdnUpdateAsync_WhenSilentAndBundleProcessIsRunning_GracefullySkipsWithoutFailingStatusOrErrorLogs()
    {
        // protects: Background probe silent CDN inspection gracefully skipping without error logs or failed status during active gameplay.
        // fails_when: Background probe emits scary FAIL logs or marks launcher in failed state while the game is running.
        // seam: MainViewModel.InspectCdnUpdateAsync silent process guard.

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

        vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
        vm.BundleRootOverride = "C:\\Games\\ProjectKongor";
        vm.RunningBundleProcessDetectorOverride = _ => ["hon_x64 (PID 4242)"];

        int requestsSent = 0;
        vm.CdnHttpHandlerOverride = new StubRouteHandler(_ =>
        {
            Interlocked.Increment(ref requestsSent);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var plan = await vm.InspectCdnUpdateAsync(silent: true);

        plan.Should().BeNull();
        requestsSent.Should().Be(0);
        vm.SynchronisationIsFailed.Should().BeFalse();
        vm.SynchronisationStatusMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckForCdnUpdates_FromLocalOfflineAuto_WhenLocalPortClosed_FallsBackToEmergencyCdnWithoutMutatingSelection_AndAppliesSignedOverlayAtBundleRoot()
    {
        // protects: One-click update check from Local / Offline Auto falling back to remote emergency CDN when local :55555 is not listening, applying signed overlay at bundle root without mutating launcher selection.
        // fails_when: Launcher changes master server / CDN mode, queries closed local port, rejects signed overlay, or writes outside bundle root.
        // seam: MainViewModel.SynchroniseContent emergency CDN fallback and bundle-root overlay application.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_emergency_cdn_" + Guid.NewGuid().ToString("N"));
        string gamesClientDir = Path.Combine(tempBundleRoot, "games", "client");
        Directory.CreateDirectory(gamesClientDir);

        try
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            byte[] hotfixBytes = "SetSave('ui_emergency_hotfix', 'active');"u8.ToArray();
            string hotfixHash = Convert.ToHexString(SHA256.HashData(hotfixBytes)).ToLowerInvariant();

            Manifest overlayManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.3",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/ui/hotfix.lua"] = new()
                    {
                        Size = hotfixBytes.Length,
                        Hash = hotfixHash
                    }
                }
            };
            overlayManifest = overlayManifest with
            {
                Signature = ManifestSignatureVerifier.SignManifest(overlayManifest, ecdsa)
            };
            string manifestJson = JsonSerializer.Serialize(overlayManifest, ManifestJSONContext.Default.Manifest);

            List<string> queriedUrls = [];

            StubRouteHandler stubHandler = new(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                lock (queriedUrls)
                {
                    queriedUrls.Add(url);
                }

                if (url.Contains("localhost:55555", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("Connection refused to closed local port :55555");
                }

                if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(manifestJson)
                    };
                }

                string objectsPath = $"objects/{hotfixHash[..2]}/{hotfixHash}";
                if (url.EndsWith(objectsPath, StringComparison.OrdinalIgnoreCase) ||
                    url.EndsWith("games/client/override/ui/hotfix.lua", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(hotfixBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

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

            vm.BundleRootOverride = tempBundleRoot;
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            vm.LocalMasterServerPortProbe = (_, _) => Task.FromResult(false);
            vm.ConfirmUpdatePrompt = _ => Task.FromResult(true);
            vm.CdnPublicKeyXHexOverride = pubXHex;
            vm.CdnPublicKeyYHexOverride = pubYHex;
            vm.CdnHttpHandlerOverride = stubHandler;

            await vm.CheckForCdnUpdates();

            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            vm.LoadedMasterServerAddress.Should().Be(EndpointPairRegistry.MasterServerLocalOfflineAuto);
            vm.SelectedCDNAddressItem?.DisplayText.Should().Contain("Offline Auto");
            vm.HasAvailableCdnUpdate.Should().BeTrue();
            vm.CanDownloadCdnUpdate.Should().BeTrue();

            string hotfixPath = Path.Combine(tempBundleRoot, "games", "client", "override", "ui", "hotfix.lua");
            // Zero auto-downloads: CheckForCdnUpdates must never write files to disk
            File.Exists(hotfixPath).Should().BeFalse();

            // Player explicitly initiates download
            await vm.DownloadCdnUpdate();

            lock (queriedUrls)
            {
                queriedUrls.Should().NotContain(u => u.Contains("localhost:55555"));
                queriedUrls.Should().Contain(u => u.Contains("cdn.newbs.io"));
                queriedUrls.Count(u => u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)).Should().BeGreaterThanOrEqualTo(1);
            }

            File.Exists(hotfixPath).Should().BeTrue();
            File.ReadAllBytes(hotfixPath).Should().Equal(hotfixBytes);

            string ledgerPath = Path.Combine(tempBundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");
            File.Exists(ledgerPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
            {
                Directory.Delete(tempBundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task CheckForCdnUpdates_FromLocalOfflineAuto_WhenLocalPortListeningButReturns404_FallsBackToEmergencyCdn()
    {
        // protects: Fallback from listening but unpopulated local master server CDN (:55555 returning 404) to remote emergency CDN.
        // fails_when: 404 from local CDN halts update check instead of trying configured emergency fallback CDN.
        // seam: MainViewModel.SynchroniseContent candidate CDN iteration and fallback handling.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_local_fallback_" + Guid.NewGuid().ToString("N"));
        string gamesClientDir = Path.Combine(tempBundleRoot, "games", "client");
        Directory.CreateDirectory(gamesClientDir);

        try
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            Manifest emptyOverlayManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            emptyOverlayManifest = emptyOverlayManifest with
            {
                Signature = ManifestSignatureVerifier.SignManifest(emptyOverlayManifest, ecdsa)
            };
            string manifestJson = JsonSerializer.Serialize(emptyOverlayManifest, ManifestJSONContext.Default.Manifest);

            List<string> queriedUrls = [];

            StubRouteHandler stubHandler = new(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                lock (queriedUrls)
                {
                    queriedUrls.Add(url);
                }

                if (url.Contains("localhost:55555", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (url.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase) &&
                    url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(manifestJson)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

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

            vm.BundleRootOverride = tempBundleRoot;
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            vm.LocalMasterServerPortProbe = (_, _) => Task.FromResult(true);
            vm.ConfirmUpdatePrompt = _ => Task.FromResult(true);
            vm.CdnPublicKeyXHexOverride = pubXHex;
            vm.CdnPublicKeyYHexOverride = pubYHex;
            vm.CdnHttpHandlerOverride = stubHandler;

            await vm.CheckForCdnUpdates();

            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);

            lock (queriedUrls)
            {
                queriedUrls.Should().Contain(u => u.Contains("localhost:55555"));
                queriedUrls.Should().Contain(u => u.Contains("cdn.newbs.io"));
            }
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
            {
                Directory.Delete(tempBundleRoot, true);
            }
        }
    }

    [Theory]
    [InlineData("TaskCanceled")]
    [InlineData("CorruptJson")]
    public async Task CheckForCdnUpdates_WhenPrimaryCdnFailsWithTransientError_FallsBackToSecondaryEmergencyCdn(string failureMode)
    {
        // protects: Launcher update check resilience against broken or timed-out primary CDN responses falling back to configured secondary emergency CDN.
        // fails_when: Primary CDN throwing TaskCanceledException or JsonException is not caught by candidate fallback loop, failing sync.
        // seam: MainViewModel.SynchroniseContent typed exception filter and candidate CDN fallback iteration.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_secondary_fallback_" + Guid.NewGuid().ToString("N"));
        string gamesClientDir = Path.Combine(tempBundleRoot, "games", "client");
        Directory.CreateDirectory(gamesClientDir);

        try
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            Manifest validManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            validManifest = validManifest with
            {
                Signature = ManifestSignatureVerifier.SignManifest(validManifest, ecdsa)
            };
            string validManifestJson = JsonSerializer.Serialize(validManifest, ManifestJSONContext.Default.Manifest);

            string secondaryCdnUrl = "https://secondary.cdn.newbs.io/cdn/";
            List<string> queriedUrls = [];

            StubRouteHandler stubHandler = new(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                lock (queriedUrls)
                {
                    queriedUrls.Add(url);
                }

                if (url.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("secondary", StringComparison.OrdinalIgnoreCase))
                {
                    if (failureMode == "TaskCanceled")
                    {
                        throw new TaskCanceledException("Simulated request timeout on primary CDN");
                    }
                    if (failureMode == "CorruptJson")
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{ not valid json content }")
                        };
                    }
                }

                if (url.Contains("secondary.cdn.newbs.io", StringComparison.OrdinalIgnoreCase) &&
                    url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(validManifestJson)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            MainViewModel vm = new()
            {
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalOfflineAuto,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnLocalOfflineAuto,
                    TargetURL = "http://localhost:55555/cdn/"
                },
                SecondaryEmergencyCdnBaseUrl = secondaryCdnUrl
            };

            vm.BundleRootOverride = tempBundleRoot;
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            vm.LocalMasterServerPortProbe = (_, _) => Task.FromResult(false);
            vm.ConfirmUpdatePrompt = _ => Task.FromResult(true);
            vm.CdnPublicKeyXHexOverride = pubXHex;
            vm.CdnPublicKeyYHexOverride = pubYHex;
            vm.CdnHttpHandlerOverride = stubHandler;

            await vm.CheckForCdnUpdates();

            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);

            lock (queriedUrls)
            {
                queriedUrls.Should().Contain(u => u.Contains("cdn.newbs.io") && !u.Contains("secondary"));
                queriedUrls.Should().Contain(u => u.Contains("secondary.cdn.newbs.io"));
            }
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
            {
                Directory.Delete(tempBundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task ConnectivityProbe_WhenR2BucketRootReturns404_ProbesVariantManifestAndReportsManifestReady()
    {
        // protects: Cloudflare R2 bucket roots returning 404 on bare prefix from being falsely flagged as offline by falling back to variant manifest probe for CDN subjects.
        // fails_when: Bare R2 CDN root 404 causes probe failure, or non-CDN subject probes manifest.json inappropriately.
        // seam: ConnectivityProbe.ProbeAsync 404 handling with subject-specific manifest verification.

        string variant = OperatingSystem.IsWindows() ? "wac"
            : OperatingSystem.IsLinux() ? "lac"
            : OperatingSystem.IsMacOS() ? "mac"
            : "wac";

        ConnectivityProbe cdnProbe = new(
            "CDN",
            _ => { },
            () => new StubRouteHandler(req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? string.Empty;

                if (path == "/")
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (path.Equals($"/{variant}/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        await cdnProbe.ProbeAsync("https://cdn.newbs.io/");
        cdnProbe.Succeeded.Should().BeTrue();
        cdnProbe.Failed.Should().BeFalse();
        cdnProbe.StatusMessage.Should().Be("CDN Is Online: Manifest Ready (HTTP 200)");

        ConnectivityProbe msProbe = new(
            "Master Server",
            _ => { },
            () => new StubRouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await msProbe.ProbeAsync("https://masterserver.newbs.io/");
        msProbe.Succeeded.Should().BeFalse();
        msProbe.Failed.Should().BeTrue();
        msProbe.StatusMessage.Should().Be("Master Server Is Unreachable: HTTP 404 (NotFound)");
    }

    [Fact]
    public async Task CheckForCdnUpdates_PreservesUiSynchronizationContextAcrossAsyncBoundaries()
    {
        // protects: MainViewModel UI SynchronizationContext affinity across async port probes, manifest fetches, and Progress<T> callbacks.
        // fails_when: ConfigureAwait(false) drops ViewModel continuations onto ThreadPool threads, causing off-thread PropertyChanged or AvaloniaObject VerifyAccess crashes.
        // seam: MainViewModel.CheckForCdnUpdates, TryGetMasterServerComboText, and Progress<SynchronisationEvent> under a single-thread UI SynchronizationContext.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_ui_syncctx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempBundleRoot, "games", "client"));

        try
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            Manifest emptyOverlay = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            emptyOverlay = emptyOverlay with
            {
                Signature = ManifestSignatureVerifier.SignManifest(emptyOverlay, ecdsa)
            };
            string manifestJson = JsonSerializer.Serialize(emptyOverlay, ManifestJSONContext.Default.Manifest);

            List<string> offThreadMutations = [];
            TaskCompletionSource completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

            Thread uiThread = new(() =>
            {
                int uiThreadId = Environment.CurrentManagedThreadId;
                SingleThreadUiSyncContext syncCtx = new(queue);
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                syncCtx.Post(async _ =>
                {
                    try
                    {
                        MainViewModel vm = new()
                        {
                            LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalOfflineAuto,
                            MasterServerAddressIsValid = true,
                            SelectedCDNAddressItem = new AddressSelectItem
                            {
                                DisplayText = EndpointPairRegistry.CdnLocalOfflineAuto,
                                TargetURL = "http://localhost:55555/cdn/"
                            },
                            BundleRootOverride = tempBundleRoot,
                            RunningBundleProcessDetectorOverride = _ => [],
                            CdnPublicKeyXHexOverride = pubXHex,
                            CdnPublicKeyYHexOverride = pubYHex,
                            LocalMasterServerPortProbe = async (_, _) =>
                            {
                                await Task.Delay(5).ConfigureAwait(false);
                                return false;
                            },
                            CdnHttpHandlerOverride = new AsyncYieldRouteHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(manifestJson, System.Text.Encoding.UTF8, "application/json")
                            })
                        };
                        vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);

                        vm.PropertyChanged += (_, args) =>
                        {
                            if (Environment.CurrentManagedThreadId != uiThreadId)
                            {
                                lock (offThreadMutations)
                                {
                                    offThreadMutations.Add($"{args.PropertyName} on thread {Environment.CurrentManagedThreadId} (expected {uiThreadId})");
                                }
                            }
                        };

                        await vm.CheckForCdnUpdates();
                        if (Environment.CurrentManagedThreadId != uiThreadId)
                        {
                            lock (offThreadMutations)
                            {
                                offThreadMutations.Add($"Post-CheckForCdnUpdates continuation on thread {Environment.CurrentManagedThreadId}");
                            }
                        }

                        completionTcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completionTcs.TrySetException(ex);
                    }
                    finally
                    {
                        queue.CompleteAdding();
                    }
                }, null);

                foreach ((SendOrPostCallback callback, object? state) in queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            {
                IsBackground = true,
                Name = "TestAvaloniaUiPump"
            };

            uiThread.Start();
            try
            {
                await completionTcs.Task;
            }
            finally
            {
                queue.CompleteAdding();
                uiThread.Join();
            }

            offThreadMutations.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
                Directory.Delete(tempBundleRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContentBroker_InspectUpdatePlan_CalculatesPlan_WithoutMutatingDiskOrDownloadingFiles()
    {
        // protects: Pure read-only inspection contract of ContentBroker.InspectUpdatePlan.
        // fails_when: InspectUpdatePlan writes files to disk, deletes files, creates directories, or attempts CAS downloads.
        // seam: ContentBroker.InspectUpdatePlan in-memory calculation.

        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_inspect_plan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] fileBytes = "test-payload"u8.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/test.txt"] = new() { Size = fileBytes.Length, Hash = hash }
                }
            };

            SynchronisationPlan plan = await ContentBroker.InspectUpdatePlan(
                manifest,
                variant: "wac",
                targetDirectory: tempDir
            );

            plan.FilesToDownload.Should().Be(1);
            plan.TotalBytesToDownload.Should().Be(fileBytes.Length);

            // Invariant: Zero files written to disk
            File.Exists(Path.Combine(tempDir, "games", "client", "override", "test.txt")).Should().BeFalse();
            Directory.Exists(Path.Combine(tempDir, "games")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ContentBroker_InspectUpdatePlan_FreshInstall_PlansBothTextureShards()
    {
        // protects: Fresh install CDN sync plan correctly identifies both textures.s2z shards (game/ and KONGOR/updates/) for download.
        // fails_when: Sharded texture archives in KONGOR/updates or game/ are dropped or miscounted in the download plan.
        // seam: ContentBroker.InspectUpdatePlan with fresh empty target directory.

        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_fresh_shards_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] shard1Bytes = "canary-shard1-updates-textures"u8.ToArray();
            byte[] shard2Bytes = "canary-shard2-game-textures"u8.ToArray();
            string shard1Hash = Convert.ToHexString(SHA256.HashData(shard1Bytes)).ToLowerInvariant();
            string shard2Hash = Convert.ToHexString(SHA256.HashData(shard2Bytes)).ToLowerInvariant();

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/KONGOR/updates/textures.s2z"] = new() { Size = shard1Bytes.Length, Hash = shard1Hash },
                    ["games/client/game/textures.s2z"] = new() { Size = shard2Bytes.Length, Hash = shard2Hash }
                }
            };

            SynchronisationPlan plan = await ContentBroker.InspectUpdatePlan(
                manifest,
                variant: "wac",
                targetDirectory: tempDir
            );

            plan.FilesToDownload.Should().Be(2);
            plan.TotalBytesToDownload.Should().Be(shard1Bytes.Length + shard2Bytes.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ContentBroker_InspectUpdatePlan_OneArchiveToTwoShardUpgrade_PlansBothUpdatesCleanly()
    {
        // protects: Transition from legacy monolithic textures.s2z to 2-shard layout properly updates game/textures.s2z and fetches KONGOR/updates/textures.s2z.
        // fails_when: Legacy game/textures.s2z hash mismatch is ignored or KONGOR/updates shard is not scheduled.
        // seam: ContentBroker.InspectUpdatePlan with pre-existing legacy monolithic textures.s2z file.

        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_upgrade_shards_" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(tempDir, "games", "client", "game");
        Directory.CreateDirectory(gameDir);

        try
        {
            // Seed legacy 2GB monolithic textures.s2z dummy
            byte[] legacyBytes = "legacy-monolithic-textures-2gb"u8.ToArray();
            string legacyPath = Path.Combine(gameDir, "textures.s2z");
            await File.WriteAllBytesAsync(legacyPath, legacyBytes);

            byte[] shard1Bytes = "sharded-updates-textures"u8.ToArray();
            byte[] shard2Bytes = "sharded-game-textures"u8.ToArray();
            string shard1Hash = Convert.ToHexString(SHA256.HashData(shard1Bytes)).ToLowerInvariant();
            string shard2Hash = Convert.ToHexString(SHA256.HashData(shard2Bytes)).ToLowerInvariant();

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.3",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/KONGOR/updates/textures.s2z"] = new() { Size = shard1Bytes.Length, Hash = shard1Hash },
                    ["games/client/game/textures.s2z"] = new() { Size = shard2Bytes.Length, Hash = shard2Hash }
                }
            };

            SynchronisationPlan plan = await ContentBroker.InspectUpdatePlan(
                manifest,
                variant: "wac",
                targetDirectory: tempDir
            );

            // Both files must be planned for download: shard1 is new, shard2 replaces legacy monolithic archive
            plan.FilesToDownload.Should().Be(2);
            plan.TotalBytesToDownload.Should().Be(shard1Bytes.Length + shard2Bytes.Length);

            // Now simulate successful update: write both target files matching manifest
            string updatesDir = Path.Combine(tempDir, "games", "client", "KONGOR", "updates");
            Directory.CreateDirectory(updatesDir);
            await File.WriteAllBytesAsync(Path.Combine(updatesDir, "textures.s2z"), shard1Bytes);
            await File.WriteAllBytesAsync(legacyPath, shard2Bytes);

            SynchronisationPlan syncedPlan = await ContentBroker.InspectUpdatePlan(
                manifest,
                variant: "wac",
                targetDirectory: tempDir
            );

            syncedPlan.FilesToDownload.Should().Be(0);
            syncedPlan.TotalBytesToDownload.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InspectCdnUpdateAsync_WhenCdnHasUpdate_SetsHasAvailableCdnUpdateAndComparisonMessage_WithoutAutoDownloading()
    {
        // protects: Player-controlled update discovery indicating version difference between local installed and remote CDN without automatic downloading.
        // fails_when: Manifest probe mutates disk, initiates automatic CAS downloads, or miscalculates version comparison.
        // seam: MainViewModel.InspectCdnUpdateAsync and CDNProbe status binding.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_update_ui_" + Guid.NewGuid().ToString("N"));
        string gamesClientDir = Path.Combine(tempBundleRoot, "games", "client");
        Directory.CreateDirectory(gamesClientDir);

        try
        {
            byte[] hotfixBytes = "test-hotfix"u8.ToArray();
            string hotfixHash = Convert.ToHexString(SHA256.HashData(hotfixBytes)).ToLowerInvariant();

            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            Manifest overlayManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.3",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 2,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/hotfix.txt"] = new()
                    {
                        Size = hotfixBytes.Length,
                        Hash = hotfixHash
                    }
                }
            };
            overlayManifest = overlayManifest with
            {
                Signature = ManifestSignatureVerifier.SignManifest(overlayManifest, ecdsa)
            };
            string manifestJson = JsonSerializer.Serialize(overlayManifest, ManifestJSONContext.Default.Manifest);

            int casRequests = 0;
            StubRouteHandler stubHandler = new(req =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                string path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(manifestJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                Interlocked.Increment(ref casRequests);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            MainViewModel vm = new()
            {
                BundleRootOverride = tempBundleRoot,
                CdnHttpHandlerOverride = stubHandler,
                CdnPublicKeyXHexOverride = pubXHex,
                CdnPublicKeyYHexOverride = pubYHex,
                LoadedMasterServerAddress = "api.kongor.net",
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = "cdn.newbs.io",
                    TargetURL = "https://cdn.newbs.io"
                }
            };
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);

            SynchronisationPlan? plan = await vm.InspectCdnUpdateAsync(silent: false);

            // Allow any UI SynchronizationContext.Post status callback (or initial constructor auto-probe debounce)
            // to settle deterministically under parallel xUnit test load.
            for (int attempt = 0; attempt < 15 && !vm.CDNProbe.StatusMessage.Contains("Update Available", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(25);
                if (!vm.CDNProbe.StatusMessage.Contains("Update Available", StringComparison.Ordinal))
                {
                    plan = await vm.InspectCdnUpdateAsync(silent: false);
                }
            }

            plan.Should().NotBeNull();
            plan!.FilesToDownload.Should().Be(1);

            // Invariants:
            vm.HasAvailableCdnUpdate.Should().BeTrue();
            vm.CanDownloadCdnUpdate.Should().BeTrue();
            vm.AvailableCdnVersion.Should().Be("5.0.3");
            vm.DownloadCdnButtonText.Should().Contain("5.0.3");
            vm.CDNProbe.StatusMessage.Should().Contain("Update Available");
            vm.CDNProbe.StatusMessage.Should().Contain("5.0.2");
            vm.CDNProbe.StatusMessage.Should().Contain("5.0.3");

            // Zero CAS requests and Zero disk writes during inspection
            casRequests.Should().Be(0);
            File.Exists(Path.Combine(tempBundleRoot, "games", "client", "override", "hotfix.txt")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
                Directory.Delete(tempBundleRoot, true);
        }
    }

    [Fact]
    public async Task LaunchGameClientCore_And_LaunchMapEditor_NeverCallCdnOrTriggerDownloads()
    {
        // protects: Strict firewall ensuring game launch and map editor launch NEVER trigger CDN synchronisation or auto-downloads.
        // fails_when: Launching game client or map editor issues HTTP requests to CDN or invokes ContentBroker.
        // seam: MainViewModel.LaunchGameClientCore and LaunchMapEditor.

        int cdnRequests = 0;
        StubRouteHandler stubHandler = new(req =>
        {
            Interlocked.Increment(ref cdnRequests);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        MainViewModel vm = new()
        {
            LoadedMasterServerAddress = "masterserver.newbs.io",
            MasterServerAddressIsValid = true,
            SelectedCDNAddressItem = new AddressSelectItem
            {
                DisplayText = "cdn.newbs.io",
                TargetURL = "https://cdn.newbs.io"
            },
            CdnHttpHandlerOverride = stubHandler,
            ProcessLauncherOverride = (_, _) => Task.CompletedTask
        };

        // Calling LaunchGameClientCore
        await vm.LaunchGameClientCore(skipSynchronisation: true);

        // Calling LaunchMapEditor
        await vm.LaunchMapEditorCommand.ExecuteAsync(null);

        // Zero CDN requests guaranteed
        cdnRequests.Should().Be(0);
    }

    [Fact]
    public async Task InspectCdnUpdateAsync_LocalLoopbackDev_EmptyUnsignedManifest_ReportsLocalDevManualUpdatesWithoutFailure()
    {
        // protects: Local loopback dev server returning an empty unsigned manifest stays non-failed with Local Dev status.
        // fails_when: Local dev workflow shows a false-alarm red PKV Guard error banner on an empty unsigned loopback manifest.
        // seam: MainViewModel.InspectCdnUpdateAsync loopback dev unsigned empty manifest handling.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_test_local_dev_unsigned_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempBundleRoot, "games", "client"));

        try
        {
            Manifest emptyUnsignedManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                Signature = null,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            string manifestJson = JsonSerializer.Serialize(emptyUnsignedManifest, ManifestJSONContext.Default.Manifest);

            StubRouteHandler stubHandler = new(req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(manifestJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            MainViewModel vm = new()
            {
                BundleRootOverride = tempBundleRoot,
                CdnHttpHandlerOverride = stubHandler,
                AllowUnsignedLoopbackCdnManifestsForDev = false,
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalhost55555,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnLocalhost55555,
                    TargetURL = "http://127.0.0.1:55555/cdn/"
                }
            };
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            vm.RunningBundleProcessDetectorOverride = _ => [];

            SynchronisationPlan? plan = await vm.InspectCdnUpdateAsync(silent: false);

            plan.Should().BeNull();
            vm.HasAvailableCdnUpdate.Should().BeFalse();
            vm.SynchronisationIsFailed.Should().BeFalse();
            vm.SynchronisationStatusMessage.Should().Be("Local Dev Server (Manual Updates)");
            vm.CDNProbe.StatusMessage.Should().Be("Local Dev Server (Manual Updates)");
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
                Directory.Delete(tempBundleRoot, true);
        }
    }

    [Fact]
    public async Task InspectCdnUpdateAsync_RemoteUnsignedManifest_And_PlaytestLoopbackUnsignedManifest_RemainStrictlyBlockedByPkvGuard()
    {
        // protects: Strict ECDSA P-256 PKV Guard remains enforced for ALL remote CDNs, ALL Playtest builds, and non-empty loopback manifests.
        // fails_when: Unsigned manifests from remote CDNs, Playtest builds, or non-empty loopback CDNs bypass [PKV-GUARD].
        // seam: MainViewModel.InspectCdnUpdateAsync PKV signature enforcement.

        string tempDevRoot = Path.Combine(Path.GetTempPath(), "kongor_test_pkv_remote_" + Guid.NewGuid().ToString("N"));
        string tempPlaytestRoot = Path.Combine(Path.GetTempPath(), "kongor_test_pkv_playtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDevRoot, "games", "client"));
        Directory.CreateDirectory(Path.Combine(tempPlaytestRoot, "games", "client"));

        try
        {
            // Mark tempPlaytestRoot as a Playtest build (IsPlaytest = true)
            await File.WriteAllTextAsync(
                Path.Combine(tempPlaytestRoot, PlaytestPolicyService.PolicyFileName),
                """{"IsPlaytest": true}""");

            Manifest emptyUnsignedManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                Sequence = 1,
                Signature = null,
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            string emptyUnsignedJson = JsonSerializer.Serialize(emptyUnsignedManifest, ManifestJSONContext.Default.Manifest);

            Manifest nonEmptyUnsignedManifest = emptyUnsignedManifest with
            {
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/test.lua"] = new()
                    {
                        Size = 16,
                        Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                }
            };
            string nonEmptyUnsignedJson = JsonSerializer.Serialize(nonEmptyUnsignedManifest, ManifestJSONContext.Default.Manifest);

            // Case 1: Remote CDN returning empty unsigned manifest MUST be blocked by [PKV-GUARD]
            MainViewModel remoteVm = new()
            {
                BundleRootOverride = tempDevRoot,
                CdnHttpHandlerOverride = new StubRouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(emptyUnsignedJson, System.Text.Encoding.UTF8, "application/json")
                }),
                LoadedMasterServerAddress = "masterserver.newbs.io",
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = "cdn.newbs.io",
                    TargetURL = "https://cdn.newbs.io"
                }
            };
            remoteVm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            remoteVm.RunningBundleProcessDetectorOverride = _ => [];

            SynchronisationPlan? remotePlan = await remoteVm.InspectCdnUpdateAsync(silent: false);
            remotePlan.Should().BeNull();
            remoteVm.HasAvailableCdnUpdate.Should().BeFalse();
            remoteVm.SynchronisationIsFailed.Should().BeTrue();
            remoteVm.SynchronisationStatusMessage.Should().Be("PKV Guard — Updates Blocked (Non-PKV Server)");
            remoteVm.CDNProbe.StatusMessage.Should().Be("PKV Guard — Updates Blocked (Non-PKV Server)");

            // Case 2: Playtest build on loopback CDN returning empty unsigned manifest MUST be blocked by [PKV-GUARD]
            MainViewModel playtestVm = new()
            {
                BundleRootOverride = tempPlaytestRoot,
                CdnHttpHandlerOverride = new StubRouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(emptyUnsignedJson, System.Text.Encoding.UTF8, "application/json")
                }),
                AllowUnsignedLoopbackCdnManifestsForDev = false,
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalhost55555,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnLocalhost55555,
                    TargetURL = "http://127.0.0.1:55555/cdn/"
                }
            };
            playtestVm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            playtestVm.RunningBundleProcessDetectorOverride = _ => [];

            SynchronisationPlan? playtestPlan = await playtestVm.InspectCdnUpdateAsync(silent: false);
            playtestPlan.Should().BeNull();
            playtestVm.HasAvailableCdnUpdate.Should().BeFalse();
            playtestVm.SynchronisationIsFailed.Should().BeTrue();
            playtestVm.SynchronisationStatusMessage.Should().Be("PKV Guard — Updates Blocked (Non-PKV Server)");
            playtestVm.CDNProbe.StatusMessage.Should().Be("PKV Guard — Updates Blocked (Non-PKV Server)");

            // Case 3: Local loopback dev returning NON-empty unsigned manifest MUST STILL be blocked by [PKV-GUARD]
            MainViewModel nonEmptyLoopbackVm = new()
            {
                BundleRootOverride = tempDevRoot,
                CdnHttpHandlerOverride = new StubRouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(nonEmptyUnsignedJson, System.Text.Encoding.UTF8, "application/json")
                }),
                AllowUnsignedLoopbackCdnManifestsForDev = false,
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerLocalhost55555,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnLocalhost55555,
                    TargetURL = "http://127.0.0.1:55555/cdn/"
                }
            };
            nonEmptyLoopbackVm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);
            nonEmptyLoopbackVm.RunningBundleProcessDetectorOverride = _ => [];

            SynchronisationPlan? nonEmptyPlan = await nonEmptyLoopbackVm.InspectCdnUpdateAsync(silent: false);
            nonEmptyPlan.Should().BeNull();
            nonEmptyLoopbackVm.HasAvailableCdnUpdate.Should().BeFalse();
            nonEmptyLoopbackVm.SynchronisationIsFailed.Should().BeTrue();
            nonEmptyLoopbackVm.SynchronisationStatusMessage.Should().Be("PKV Guard — Updates Blocked (Non-PKV Server)");
        }
        finally
        {
            if (Directory.Exists(tempDevRoot))
                Directory.Delete(tempDevRoot, true);
            if (Directory.Exists(tempPlaytestRoot))
                Directory.Delete(tempPlaytestRoot, true);
        }
    }

    private sealed class SingleThreadUiSyncContext(
        System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> queue)
        : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                if (!queue.IsAddingCompleted)
                {
                    queue.Add((d, state));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Queue completed or disposed after test scope exited
            }
        }
    }

    private sealed class AsyncYieldRouteHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            return responder();
        }
    }

    private sealed class StubRouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
