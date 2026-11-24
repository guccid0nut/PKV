// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Enumerations;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class ModManagerUiThreadAndCdnFallbackTests
{
    [Fact]
    public async Task ScheduleAutoApply_UnderSingleThreadUiSyncContext_MutatesCollectionsAndPropertiesStrictlyOnUiThread()
    {
        // protects: Avalonia UI-thread affinity contract for ModManagerViewModel debounced auto-apply (L3A-P0-01).
        // fails_when: ScheduleAutoApply invokes ApplyModsAsync on a ThreadPool thread via ConfigureAwait(false), mutating ActiveAndFavoriteMods or ObservableProperties off the UI thread.
        // seam: ModManagerViewModel.ScheduleAutoApply -> ApplyModsAsync -> UpdateActiveCount -> ActiveAndFavoriteMods under a single-thread UI SynchronizationContext.

        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_wave2_mod_ui_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempRoot, "games", "client");
        Directory.CreateDirectory(Path.Combine(clientDir, "game"));
        Directory.CreateDirectory(Path.Combine(clientDir, "KONGOR", "extensions"));

        try
        {
            List<string> offThreadMutations = [];
            TaskCompletionSource completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

            Thread uiThread = new(() =>
            {
                int uiThreadId = Environment.CurrentManagedThreadId;
                SingleThreadUiSyncContext syncCtx = new(queue);
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                syncCtx.Post(async _ =>
                {
                    try
                    {
                        ModManagerViewModel vm = new(autoLoad: false)
                        {
                            SettingsDirectory = tempRoot,
                            GameClientDirectoryOverride = clientDir
                        };

                        ModItemViewModel sampleMod = new()
                        {
                            Id = "wave2_ui_mod",
                            Name = "Wave 2 UI Mod",
                            Category = "UI & HUD",
                            Type = ModType.AudioPack,
                            IsEnabled = true,
                            IsFavorite = true
                        };
                        vm.Mods.Add(sampleMod);

                        TaskCompletionSource autoApplyCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        bool sawBusy = false;

                        vm.PropertyChanged += (_, args) =>
                        {
                            int actualThread = Environment.CurrentManagedThreadId;
                            if (actualThread != uiThreadId)
                            {
                                lock (offThreadMutations)
                                {
                                    offThreadMutations.Add($"PropertyChanged({args.PropertyName}) on thread {actualThread} (expected {uiThreadId})");
                                }
                            }

                            if (args.PropertyName == nameof(ModManagerViewModel.IsBusy))
                            {
                                if (vm.IsBusy)
                                {
                                    sawBusy = true;
                                }
                                else if (sawBusy)
                                {
                                    autoApplyCompletedTcs.TrySetResult();
                                }
                            }
                        };

                        vm.ActiveAndFavoriteMods.CollectionChanged += (_, args) =>
                        {
                            int actualThread = Environment.CurrentManagedThreadId;
                            if (actualThread != uiThreadId)
                            {
                                lock (offThreadMutations)
                                {
                                    offThreadMutations.Add($"ActiveAndFavoriteMods.CollectionChanged({args.Action}) on thread {actualThread} (expected {uiThreadId})");
                                }
                            }
                        };

                        vm.ScheduleAutoApply(debounceDelayMs: 15);
                        await autoApplyCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(4));
                        await vm.EnsureSynchronizedForLaunchAsync();

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
                Name = "Wave2ModManagerUiPump"
            };

            uiThread.Start();
            try
            {
                await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                queue.CompleteAdding();
                uiThread.Join(TimeSpan.FromSeconds(2));
            }

            offThreadMutations.Should().BeEmpty("all ModManagerViewModel property and ObservableCollection mutations must execute on the UI thread");
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
    public async Task ApplyModsAsync_WhenConcurrentApplyArrivesDuringActiveSync_SerializesViaGateInsteadOfDroppingSecondToggle()
    {
        // protects: Deterministic VFS synchronization across overlapping debounced mod toggles and pre-launch mod sync (L3A-P0-01).
        // fails_when: A pre-launch or subsequent mod synchronization call returns early via `if (IsBusy) return;` while an earlier apply is mid-flight, leaving newly enabled avatar archives unmounted.
        // seam: ModManagerViewModel synchronization gate across overlapping ApplyModsAsync / pre-launch sync calls on real temp filesystem archives.

        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_wave2_mod_gate_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempRoot, "games", "client");
        string gameDir = Path.Combine(clientDir, "game");
        string sourceModsDir = Path.Combine(tempRoot, "source_mods");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(sourceModsDir);

        string avatar1Path = Path.Combine(sourceModsDir, "custom_one.s2z");
        string avatar2Path = Path.Combine(sourceModsDir, "custom_two.s2z");
        await File.WriteAllBytesAsync(avatar1Path, new byte[64 * 1024]);
        await File.WriteAllBytesAsync(avatar2Path, new byte[64 * 1024]);

        try
        {
            ModManagerViewModel vm = new(autoLoad: false)
            {
                SettingsDirectory = tempRoot,
                GameClientDirectoryOverride = clientDir
            };

            ModItemViewModel mod1 = new()
            {
                Id = "custom_one",
                Name = "Custom Avatar One",
                Type = ModType.AvatarS2z,
                FilePath = avatar1Path,
                IsEnabled = true
            };
            ModItemViewModel mod2 = new()
            {
                Id = "custom_two",
                Name = "Custom Avatar Two",
                Type = ModType.AvatarS2z,
                FilePath = avatar2Path,
                IsEnabled = false
            };

            vm.Mods.Add(mod1);
            vm.Mods.Add(mod2);

            TaskCompletionSource enteredFirstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseFirstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int hookHits = 0;
            vm.BeforeAvatarInjectHookForTesting = async () =>
            {
                if (Interlocked.Increment(ref hookHits) == 1)
                {
                    enteredFirstApply.TrySetResult();
                    await releaseFirstApply.Task;
                }
            };

            // Start first apply and deterministically pause inside the gate while IsBusy == true.
            Task inFlight = vm.ApplyModsAsync();
            await enteredFirstApply.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.IsBusy.Should().BeTrue();

            // User enables mod2 and clicks Launch while first apply is mid-flight; EnsureSynchronizedForLaunchAsync MUST wait on _modSyncGate!
            mod2.IsEnabled = true;
            Task launchSync = vm.EnsureSynchronizedForLaunchAsync();
            launchSync.IsCompleted.Should().BeFalse("pre-launch sync must wait on _modSyncGate instead of returning early while IsBusy is true");

            releaseFirstApply.TrySetResult();
            await Task.WhenAll(inFlight, launchSync).WaitAsync(TimeSpan.FromSeconds(5));

            File.Exists(Path.Combine(gameDir, "resources_avatar_custom_one.s2z")).Should().BeTrue();
            File.Exists(Path.Combine(gameDir, "resources_avatar_custom_two.s2z")).Should().BeTrue();
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
    public async Task NewbsCdnFallback_WhenPrimaryDnsFails_RetriesMasterServerCdn_AndWhenBothFail_DegradesToLocalFiles()
    {
        // protects: Seamless cdn.newbs.io -> masterserver.newbs.io/cdn -> local files fallback without tripping static Polly circuit breakers (OPS-P0-01).
        // fails_when: SecondaryEmergencyCdnBaseUrl defaults to empty, cdn.newbs.io NXDOMAIN fails to try masterserver.newbs.io/cdn, or dual remote outage leaves SynchronisationIsFailed=true when local client files exist.
        // seam: EndpointPairRegistry, ConnectivityProbe, MainViewModel.InspectCdnUpdateAsync, and MainViewModel.DownloadCdnUpdate.

        string tempBundleRoot = Path.Combine(Path.GetTempPath(), "kongor_wave2_newbs_cdn_" + Guid.NewGuid().ToString("N"));
        string gamesClientDir = Path.Combine(tempBundleRoot, "games", "client");
        Directory.CreateDirectory(Path.Combine(gamesClientDir, "game"));
        await File.WriteAllBytesAsync(Path.Combine(gamesClientDir, "hon_x64.exe"), [0x4D, 0x5A]);

        try
        {
            MainViewModel defaultVm = new();
            defaultVm.SecondaryEmergencyCdnBaseUrl.Should().Be("https://masterserver.newbs.io/cdn/");

            List<AddressSelectItem> options = EndpointPairRegistry.BuildAvailableCdnOptions(
                EndpointPairRegistry.MasterServerNewbsIo,
                customMasterServerAddress: null,
                isCustomMasterFieldVisible: false);
            options.Should().Contain(o => o.TargetURL == "masterserver.newbs.io/cdn");
            EndpointPairRegistry.IsCrossEcosystemMismatch("masterserver.newbs.io", "masterserver.newbs.io/cdn").Should().BeFalse();

            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
            string pubXHex = Convert.ToHexString(ecParams.Q.X!);
            string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

            byte[] patchBytes = "-- wave2 fallback hotfix"u8.ToArray();
            string patchHash = Convert.ToHexString(SHA256.HashData(patchBytes)).ToLowerInvariant();

            Manifest signedManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/ui/wave2_hotfix.lua"] = new()
                    {
                        Size = patchBytes.Length,
                        Hash = patchHash
                    }
                }
            };
            signedManifest = signedManifest with
            {
                Signature = ManifestSignatureVerifier.SignManifest(signedManifest, ecdsa)
            };
            string manifestJson = JsonSerializer.Serialize(signedManifest, ManifestJSONContext.Default.Manifest);

            bool allowMasterServerCdn = true;
            List<string> queriedUrls = [];

            StubHandler handler = new(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                lock (queriedUrls)
                {
                    queriedUrls.Add(url);
                }

                if (url.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("Host Not Found (cdn.newbs.io NXDOMAIN)");
                }

                if (allowMasterServerCdn && url.Contains("masterserver.newbs.io/cdn/", StringComparison.OrdinalIgnoreCase))
                {
                    if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
                        };
                    }

                    if (url.EndsWith("games/client/override/ui/wave2_hotfix.lua", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains(patchHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(patchBytes)
                        };
                    }
                }

                throw new HttpRequestException("Fallback CDN unavailable");
            });

            MainViewModel vm = new()
            {
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerNewbsIo,
                MasterServerAddressIsValid = true,
                SelectedCDNAddressItem = new AddressSelectItem
                {
                    DisplayText = EndpointPairRegistry.CdnNewbsIo,
                    TargetURL = EndpointPairRegistry.CdnNewbsIo
                },
                BundleRootOverride = tempBundleRoot,
                RunningBundleProcessDetectorOverride = _ => [],
                ConfirmUpdatePrompt = _ => Task.FromResult(true),
                CdnPublicKeyXHexOverride = pubXHex,
                CdnPublicKeyYHexOverride = pubYHex,
                CdnHttpHandlerOverride = handler
            };
            vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);

            // Leg 1 -> Leg 2: cdn.newbs.io NXDOMAIN -> masterserver.newbs.io/cdn succeeds!
            SynchronisationPlan? plan = await vm.InspectCdnUpdateAsync(silent: false);
            plan.Should().NotBeNull();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            vm.HasAvailableCdnUpdate.Should().BeTrue();

            await vm.DownloadCdnUpdate();
            vm.SynchronisationIsFailed.Should().BeFalse(vm.SynchronisationStatusMessage);
            File.Exists(Path.Combine(tempBundleRoot, "games", "client", "override", "ui", "wave2_hotfix.lua")).Should().BeTrue();

            lock (queriedUrls)
            {
                queriedUrls.Should().Contain(u => u.Contains("cdn.newbs.io"));
                queriedUrls.Should().Contain(u => u.Contains("masterserver.newbs.io/cdn/"));
            }

            // Leg 3: Both cdn.newbs.io AND masterserver.newbs.io/cdn unreachable -> graceful degradation to local files!
            allowMasterServerCdn = false;
            SynchronisationPlan? degradedPlan = await vm.InspectCdnUpdateAsync(silent: false);
            degradedPlan.Should().BeNull();
            vm.SynchronisationIsFailed.Should().BeFalse("local client files exist, so dual Newbs CDN outage must degrade gracefully to local files");
            vm.HasAvailableCdnUpdate.Should().BeFalse();
            vm.CDNProbe.Succeeded.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempBundleRoot))
            {
                Directory.Delete(tempBundleRoot, recursive: true);
            }
        }
    }

    private sealed class SingleThreadUiSyncContext(
        BlockingCollection<(SendOrPostCallback Callback, object? State)> queue)
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
                // Queue completed after test finished
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
