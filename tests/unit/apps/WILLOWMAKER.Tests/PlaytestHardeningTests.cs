// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Vfs;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Serialization;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class PlaytestHardeningTests
{
    [Fact]
    public void WillowmakerAssembly_HasZeroReferencesToKongorCli()
    {
        // protects: WILLOWMAKER launcher binary decoupling from developer MCP CLI toolset (Kongor.Cli / kongor).
        // fails_when: Any WILLOWMAKER type imports Kongor.Cli or WILLOWMAKER.csproj references Kongor.Cli.csproj.
        // seam: Compiled WILLOWMAKER assembly metadata boundary.
        Assembly willowmakerAssembly = typeof(MainViewModel).Assembly;
        AssemblyName[] referenced = willowmakerAssembly.GetReferencedAssemblies();

        referenced.Select(a => a.Name)
            .Should()
            .NotContain(name =>
                string.Equals(name, "kongor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Kongor.Cli", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClientModMountSpec_PureCompatibilityModeAndLocalhostRouting_EnforcesExactContract()
    {
        // protects: Pure 4.10 Compatibility Mode (-mod without override) and remote master server chat routing.
        // fails_when: ';override' leaks into Pure 4.10 mode or '-chatserver 127.0.0.1:11031' hijacks remote chat.
        // seam: Real ClientModMountSpec + ClientModMountBuilder.BuildClientLaunchArguments.
        string defaultMod = ClientModMountSpec.GetCanonicalModString(includeOverride: true);
        string pureMod = ClientModMountSpec.GetCanonicalModString(includeOverride: false);

        defaultMod.Should().Be("-mod base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override");
        pureMod.Should().Be("-mod base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions");
        pureMod.Should().NotContain("override");

        ClientModMountSpec.IsLocalhostEndpoint("localhost:55555").Should().BeTrue();
        ClientModMountSpec.IsLocalhostEndpoint("127.0.0.1:55555").Should().BeTrue();
        ClientModMountSpec.IsLocalhostEndpoint("http://[::1]:55555").Should().BeTrue();
        ClientModMountSpec.IsLocalhostEndpoint("masterserver.newbs.io").Should().BeFalse();
        ClientModMountSpec.IsLocalhostEndpoint("api.kongor.net").Should().BeFalse();
        ClientModMountSpec.IsLocalhostEndpoint("192.168.1.50:55555").Should().BeFalse();

        ClientModMountBuilder builder = new();
        IReadOnlyList<string> remoteArgs = builder.BuildClientLaunchArguments(
            gameDir: Path.GetTempPath(),
            mode: "normal",
            connectAddress: null,
            masterServer: "masterserver.newbs.io",
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 144);
        remoteArgs.Should().NotContain("-chatserver 127.0.0.1:11031");
        remoteArgs.Should().NotContain("+Set chat_serverAddrOverride 127.0.0.1");

        IReadOnlyList<string> localArgs = builder.BuildClientLaunchArguments(
            gameDir: Path.GetTempPath(),
            mode: "normal",
            connectAddress: null,
            masterServer: "127.0.0.1:55555",
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 144);
        localArgs.Should().Contain("-chatserver 127.0.0.1:11031");
        localArgs.Should().Contain("+Set chat_serverAddrOverride 127.0.0.1");
    }

    [Fact]
    public async Task ContentBroker_WhenAllowDeletionsIsFalse_PreservesUnpinnedLocalArchivesAndOverrides()
    {
        // protects: Tester game installation against destructive CDN 'NUKE' deletions of playtest archives & overrides.
        // fails_when: ContentBroker.Synchronise(..., allowDeletions: false) deletes local files absent from the manifest.
        // seam: Real filesystem ContentBroker.Synchronise execution.
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_playtest_cdn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] pinnedBytes = Encoding.UTF8.GetBytes("pinned-base-archive");
            string pinnedHash = Convert.ToHexStringLower(SHA256.HashData(pinnedBytes));
            string pinnedPath = Path.Combine(tempDir, "base", "resources0.s2z");
            Directory.CreateDirectory(Path.GetDirectoryName(pinnedPath)!);
            await File.WriteAllBytesAsync(pinnedPath, pinnedBytes);

            string unpinnedArchive = Path.Combine(tempDir, "game", "resources99.s2z");
            Directory.CreateDirectory(Path.GetDirectoryName(unpinnedArchive)!);
            await File.WriteAllTextAsync(unpinnedArchive, "kongor-playtest-packed-assets");

            string unpinnedOverride = Path.Combine(tempDir, "override", "heroes", "custom.entity");
            Directory.CreateDirectory(Path.GetDirectoryName(unpinnedOverride)!);
            await File.WriteAllTextAsync(unpinnedOverride, "<hero />");

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 1,
                Mode = "overlay",
                ObjectLayout = "sha256",
                HashAlgorithm = "SHA-256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["base/resources0.s2z"] = new() { Size = pinnedBytes.Length, Hash = pinnedHash }
                }
            };

            SynchronisationPlan? observedPlan = null;
            Progress<SynchronisationEvent> progress = new(ev =>
            {
                if (ev.Kind == SynchronisationEventKind.PlanReady)
                {
                    observedPlan = ev.Plan;
                }
            });

            SynchronisationSummary summary = await ContentBroker.Synchronise(
                manifest,
                "wac",
                tempDir,
                progress: progress,
                allowDeletions: false);

            summary.FilesDeleted.Should().Be(0);
            summary.FilesUpToDate.Should().Be(1);
            if (observedPlan is not null)
            {
                observedPlan.FilesToDelete.Should().Be(0);
            }

            File.Exists(unpinnedArchive).Should().BeTrue();
            File.Exists(unpinnedOverride).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ClientFolderNormalizer_WhenPlaytestPolicyActive_NeverRenamesSiblingClientDirectories()
    {
        // protects: Tester parent folders from unexpected Directory.Move renames up to 6 parent directories.
        // fails_when: ClientFolderNormalizer renames a '*client*' directory while playtest_policy.json is active.
        // seam: Real filesystem ClientFolderNormalizer + PlaytestPolicyService JSON deserialization.
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_playtest_norm_" + Guid.NewGuid().ToString("N"));
        string userDroppedClient = Path.Combine(tempRoot, "HoN Clean Client v4.10");
        Directory.CreateDirectory(userDroppedClient);

        try
        {
            File.WriteAllBytes(
                Path.Combine(userDroppedClient, global::WILLOWMAKER.Core.Services.DeploymentManifest.HeroesOfNewerthExecutableFileName),
                [0x4D, 0x5A]);

            string policyJson = JsonSerializer.Serialize(
                PlaytestPolicyDto.CreatePlaytestDefault(),
                WillowmakerJsonSerializerContext.Default.PlaytestPolicyDto);
            File.WriteAllText(Path.Combine(tempRoot, PlaytestPolicyService.PolicyFileName), policyJson);

            string? normalized = ClientFolderNormalizer.NormalizeClientDirectory(tempRoot);

            normalized.Should().BeNull();
            Directory.Exists(userDroppedClient).Should().BeTrue("playtest mode must never rename user folders");
            Directory.Exists(Path.Combine(tempRoot, "games", "client")).Should().BeFalse();
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
    public void LiveTelemetryAndBuildManifest_WhenPlaytestPolicyActive_EnforcesStrictRemoteTlsAndAotSerialization()
    {
        // protects: Remote HTTPS certificate validation against MITM, 6-port localhost scan suppression, and AOT BuildManifest reading.
        // fails_when: Invalid remote TLS certs pass validation, remote master servers trigger 6-port localhost scans, or AOT context misses DTOs.
        // seam: Real LiveTelemetryService + PlaytestPolicyService + WillowmakerJsonSerializerContext.
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_playtest_tls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, PlaytestPolicyService.PolicyFileName),
                JsonSerializer.Serialize(PlaytestPolicyDto.CreatePlaytestDefault(), WillowmakerJsonSerializerContext.Default.PlaytestPolicyDto));

            BuildManifestDto writtenManifest = new()
            {
                BuildId = "playtest-20261025-a1b2c3d",
                GitCommit = "a1b2c3d",
                Platform = "win-x64",
                BuiltAtUtc = "2026-10-25T19:00:00Z",
                Channel = "playtest",
                ArchiveHashes = new Dictionary<string, string> { ["game/resources99.s2z"] = "deadbeef" }
            };
            File.WriteAllText(
                Path.Combine(tempDir, PlaytestPolicyService.BuildManifestFileName),
                JsonSerializer.Serialize(writtenManifest, WillowmakerJsonSerializerContext.Default.BuildManifestDto));

            LiveTelemetryService.ValidateTlsCertificate(
                new Uri("https://masterserver.newbs.io/api/telemetry/live"),
                SslPolicyErrors.RemoteCertificateChainErrors,
                tempDir).Should().BeFalse();

            LiveTelemetryService.ValidateTlsCertificate(
                new Uri("https://masterserver.newbs.io/api/telemetry/live"),
                SslPolicyErrors.None,
                tempDir).Should().BeTrue();

            LiveTelemetryService.ValidateTlsCertificate(
                new Uri("https://localhost:5557/api/telemetry/live"),
                SslPolicyErrors.RemoteCertificateNameMismatch,
                tempDir).Should().BeTrue();

            LiveTelemetryService.ShouldPollLocalClusterEndpoints("masterserver.newbs.io", tempDir).Should().BeFalse();
            LiveTelemetryService.ShouldPollLocalClusterEndpoints("127.0.0.1:55555", tempDir).Should().BeTrue();

            BuildManifestDto? loadedManifest = PlaytestPolicyService.TryReadBuildManifest(tempDir);
            loadedManifest.Should().NotBeNull();
            loadedManifest!.BuildId.Should().Be("playtest-20261025-a1b2c3d");
            loadedManifest.ArchiveHashes["game/resources99.s2z"].Should().Be("deadbeef");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WriteCustomConfiguration_WhenPlaytestPolicyActive_EmitsStableReleaseStageAndPreservesOfflineModes()
    {
        // protects: Playtest autoexec.cfg CVar hygiene (hides internal QA widgets while keeping Practice/Bot/Lobby creation & local-only dumps).
        // fails_when: releaseStage_dev/ui_dev remain true in playtest exports or ui_canCreateGame/ui_showQuickStart are disabled.
        // seam: Real MainViewModel.WriteCustomConfiguration writing autoexec.cfg to disk.
        string tempClientDir = Path.Combine(Path.GetTempPath(), "kongor_playtest_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempClientDir);

        try
        {
            File.WriteAllBytes(
                Path.Combine(tempClientDir, global::WILLOWMAKER.Core.Services.DeploymentManifest.HeroesOfNewerthExecutableFileName),
                [0x4D, 0x5A]);
            File.WriteAllText(
                Path.Combine(tempClientDir, PlaytestPolicyService.PolicyFileName),
                JsonSerializer.Serialize(PlaytestPolicyDto.CreatePlaytestDefault(), WillowmakerJsonSerializerContext.Default.PlaytestPolicyDto));

            MainViewModel vm = new();
            vm.WriteCustomConfiguration(tempClientDir);

            string autoexecPath = Path.Combine(tempClientDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            File.Exists(autoexecPath).Should().BeTrue();

            string cfg = File.ReadAllText(autoexecPath);
            cfg.Should().Contain("SetSave \"releaseStage_dev\" \"false\"");
            cfg.Should().Contain("SetSave \"releaseStage_test\" \"false\"");
            cfg.Should().Contain("SetSave \"releaseStage_stable\" \"true\"");
            cfg.Should().Contain("SetSave \"ui_dev\" \"false\"");
            cfg.Should().Contain("SetSave \"ui_dev_button\" \"false\"");
            cfg.Should().Contain("SetSave \"ui_showQuickStart\" \"true\"");
            cfg.Should().Contain("SetSave \"ui_canCreateGame\" \"true\"");
            cfg.Should().Contain("Set \"sys_disableCrashReporter\" \"true\"");
            cfg.Should().Contain("SetSave \"cloud_autoupload\" \"false\"");
            cfg.Should().Contain("SetSave \"host_dynamicResReload\" \"false\"");
            cfg.Should().NotContain("res_checkModified");
        }
        finally
        {
            if (Directory.Exists(tempClientDir))
            {
                Directory.Delete(tempClientDir, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildClientLaunchArgumentList_DeduplicatesHostDynamicResReloadAndOmitsDeadResCheckModified()
    {
        // protects: K2 launch argument hygiene (zero duplicate host_dynamicResReload flags, zero non-existent [S A] ghost res_checkModified CVars, and intact UseStableReleaseStageCvars toggling).
        // fails_when: host_dynamicResReload is emitted more than once, dead res_checkModified re-enters CLI arguments, or UseStableReleaseStageCvars fails to toggle releaseStage_* / ui_dev*.
        // seam: Real MainViewModel.BuildClientLaunchArgumentList across both playtest-stable and developer policies.
        string tempClientDir = Path.Combine(Path.GetTempPath(), "kongor_launch_args_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempClientDir);

        try
        {
            MainViewModel vm = new();

            PlaytestPolicyDto stablePolicy = PlaytestPolicyDto.CreatePlaytestDefault();
            List<string> stableArgs = vm.BuildClientLaunchArgumentList(
                tempClientDir,
                "masterserver.newbs.io",
                stablePolicy,
                ["+Set host_affinity -1"]);

            stableArgs.Count(a => a.StartsWith("+Set host_dynamicResReload ", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
            stableArgs.Should().Contain("+Set host_dynamicResReload false");
            stableArgs.Should().NotContain(a => a.Contains("res_checkModified", StringComparison.OrdinalIgnoreCase));
            stableArgs.Should().Contain("+Set releaseStage_stable true");
            stableArgs.Should().Contain("+Set releaseStage_dev false");
            stableArgs.Should().Contain("+Set ui_dev false");
            stableArgs.Should().Contain("+Set host_affinity -1");

            PlaytestPolicyDto devPolicy = new() { UseStableReleaseStageCvars = false };
            List<string> devArgs = vm.BuildClientLaunchArgumentList(
                tempClientDir,
                "127.0.0.1:55555",
                devPolicy);

            devArgs.Count(a => a.StartsWith("+Set host_dynamicResReload ", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
            devArgs.Should().NotContain(a => a.Contains("res_checkModified", StringComparison.OrdinalIgnoreCase));
            devArgs.Should().Contain("+Set releaseStage_stable false");
            devArgs.Should().Contain("+Set releaseStage_dev true");
            devArgs.Should().Contain("+Set ui_dev true");
            devArgs.Should().Contain("-chatserver 127.0.0.1:11031");
        }
        finally
        {
            if (Directory.Exists(tempClientDir))
            {
                Directory.Delete(tempClientDir, recursive: true);
            }
        }
    }
}
