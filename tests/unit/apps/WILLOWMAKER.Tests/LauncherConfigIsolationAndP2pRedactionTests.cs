// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LauncherConfigIsolationAndP2pRedactionTests.cs
// System: WILLOWMAKER.Tests - Wave 4A P2P, IPC & Git-Tracked Config Hardening
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IpcSandboxMode = NEXUS.Contracts.Ipc.IpcSandboxMode;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Enumerations;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.Services.IPC;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Services.P2P;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LauncherConfigIsolationAndP2pRedactionTests : IDisposable
{
    private readonly string _tempDir;

    public LauncherConfigIsolationAndP2pRedactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorWave4ALauncher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore temp cleanup contention
            }
        }
    }

    [Theory]
    [InlineData("127.0.0.1:11235\" +Quit")]
    [InlineData("127.0.0.1:11235; Quit")]
    [InlineData("127.0.0.1:11235' ; WriteConfig evil.cfg")]
    [InlineData("-vid vid_null")]
    [InlineData("+Set login_password stolen")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("127.0.0.1:11235\nQuit")]
    [InlineData("bad host name:11235")]
    public void P2pJoinAddressValidation_MaliciousPayloads_ThrowsArgumentException(string maliciousInput)
    {
        Assert.ThrowsAny<ArgumentException>(() => P2pConnectionService.NormalizeConnectAddress(maliciousInput));
        Assert.ThrowsAny<ArgumentException>(() => DirectPlayViewModel.BuildGuestClientArguments(
            "PlayerOne",
            maliciousInput,
            false,
            string.Empty));
    }

    [Fact]
    public void EnsureClientDefaultNameConfigured_DoesNotMutateOverrideStringtables()
    {
        string overrideStrDir = Path.Combine(_tempDir, "override", "stringtables");
        Directory.CreateDirectory(overrideStrDir);
        string sentinelStrPath = Path.Combine(overrideStrDir, "client_messages_en.str");
        string sentinelContent = "client_default_name\t\t\t\tCLEAN_GIT_TRACKED_SENTINEL\r\n";
        File.WriteAllText(sentinelStrPath, sentinelContent);

        P2pConnectionService.EnsureClientDefaultNameConfigured(_tempDir, "CustomP2pHero");

        string afterContent = File.ReadAllText(sentinelStrPath);
        Assert.Equal(sentinelContent, afterContent);
        Assert.False(File.Exists(Path.Combine(overrideStrDir, "client_messages_ru.str")));
    }

    [Fact]
    public async Task WillowmakerIpcService_DefaultsToEnforcedSandboxAndBlocksUnauthenticatedExecuteCommand()
    {
        string pipeName = $"test-wave4a-ipc-enforced-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new WillowmakerIpcService(pipeName);
        Assert.Equal(IpcSandboxMode.Enforced, service.SandboxMode);

        await service.StartServerAsync();
        await using WillowmakerIpcClient unauthClient = new WillowmakerIpcClient(pipeName);
        await unauthClient.ConnectAsync();

        TaskCompletionSource<string> responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        unauthClient.OnMessageReceived += msg =>
        {
            if (msg.OpCode == IpcOpCode.CommandResponse)
            {
                responseTcs.TrySetResult(msg.PayloadString);
            }
        };

        await unauthClient.SendMessageAsync(WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, "Quit", 77));
        string response = await responseTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("401 Unauthorized", response, StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            await using NamedPipeClientStream aclProbe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.None,
                HandleInheritability.None);
            await aclProbe.ConnectAsync(2000);

            PipeSecurity security = aclProbe.GetAccessControl();
            SecurityIdentifier worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (PipeAccessRule rule in rules)
            {
                Assert.NotEqual(worldSid, rule.IdentityReference);
            }
        }
    }

    [Fact]
    public async Task WillowmakerIpcService_ExecuteCommandMailbox_DynamicLongBracketPreventsBreakout()
    {
        string pipeName = $"test-wave4a-ipc-lua-{Guid.NewGuid():N}";
        string secret = "wave4a-hmac-test-secret-key-0123456789";
        string mbParentDir = Path.Combine(Directory.GetCurrentDirectory(), "userdataKONGOR", "extensions");
        string mbDir = Path.Combine(mbParentDir, "nexus_ipc");
        Directory.CreateDirectory(mbParentDir);

        try
        {
            await using WillowmakerIpcService service = new WillowmakerIpcService(
                pipeName,
                authKey: secret,
                sandboxMode: IpcSandboxMode.Enforced);
            await service.StartServerAsync();

            await using WillowmakerIpcClient authClient = new WillowmakerIpcClient(pipeName, authKey: secret);
            await authClient.ConnectAsync();

            TaskCompletionSource<string> ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            authClient.OnMessageReceived += msg =>
            {
                if (msg.OpCode == IpcOpCode.CommandResponse)
                {
                    ackTcs.TrySetResult(msg.PayloadString);
                }
            };

            string breakoutPayload = "Echo ]====] os.execute('evil') ]=====]";
            await authClient.SendMessageAsync(WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, breakoutPayload, 88));
            string ack = await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("OK", ack);

            string reqLuaPath = Path.Combine(mbDir, "req_current.lua");
            Assert.True(File.Exists(reqLuaPath));
            string luaText = await File.ReadAllTextAsync(reqLuaPath);

            Match openMatch = Regex.Match(luaText, @"ProcessFromJson\(\[(=+)\[");
            Assert.True(openMatch.Success, $"Expected Lua long-bracket wrapper in:\n{luaText}");
            string equalsRun = openMatch.Groups[1].Value;
            string closingDelimiter = $"]{equalsRun}]";

            int bodyStart = openMatch.Index + openMatch.Length;
            int closeIndex = luaText.IndexOf(closingDelimiter, bodyStart, StringComparison.Ordinal);
            int lastCloseIndex = luaText.LastIndexOf(closingDelimiter, StringComparison.Ordinal);
            Assert.True(closeIndex > bodyStart);
            Assert.Equal(lastCloseIndex, closeIndex);
        }
        finally
        {
            string userdataKongorInCwd = Path.Combine(Directory.GetCurrentDirectory(), "userdataKONGOR");
            if (Directory.Exists(userdataKongorInCwd))
            {
                try
                {
                    Directory.Delete(userdataKongorInCwd, true);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    [Fact]
    public void MainViewModel_WriteCustomConfiguration_NeverMutatesGitTrackedClientFiles()
    {
        string mockClientDir = Path.Combine(_tempDir, "mock_client");
        Directory.CreateDirectory(Path.Combine(mockClientDir, "windows-x64"));
        Directory.CreateDirectory(Path.Combine(mockClientDir, "KONGOR", "configuration"));

        string trackedAlsoft = Path.Combine(mockClientDir, "alsoft.ini");
        string trackedDxvkRoot = Path.Combine(mockClientDir, "dxvk.conf");
        string trackedDxvkWin64 = Path.Combine(mockClientDir, "windows-x64", "dxvk.conf");
        string trackedKongorAutoexec = Path.Combine(mockClientDir, "KONGOR", "configuration", "autoexec.cfg");
        string trackedKongorInit = Path.Combine(mockClientDir, "KONGOR", "configuration", "init.cfg");

        File.WriteAllText(trackedAlsoft, "# GIT_TRACKED_ALSOFT_SENTINEL");
        File.WriteAllText(trackedDxvkRoot, "# GIT_TRACKED_DXVK_ROOT_SENTINEL");
        File.WriteAllText(trackedDxvkWin64, "# GIT_TRACKED_DXVK_WIN64_SENTINEL");
        File.WriteAllText(trackedKongorAutoexec, "// GIT_TRACKED_KONGOR_AUTOEXEC_SENTINEL");
        File.WriteAllText(trackedKongorInit, "// GIT_TRACKED_KONGOR_INIT_SENTINEL");

        MainViewModel vm = new MainViewModel();
        vm.EnableSpatialAudio = true;
        vm.WriteCustomConfiguration(mockClientDir);

        Assert.Equal("# GIT_TRACKED_ALSOFT_SENTINEL", File.ReadAllText(trackedAlsoft));
        Assert.Equal("# GIT_TRACKED_DXVK_ROOT_SENTINEL", File.ReadAllText(trackedDxvkRoot));
        Assert.Equal("# GIT_TRACKED_DXVK_WIN64_SENTINEL", File.ReadAllText(trackedDxvkWin64));
        Assert.Equal("// GIT_TRACKED_KONGOR_AUTOEXEC_SENTINEL", File.ReadAllText(trackedKongorAutoexec));
        Assert.Equal("// GIT_TRACKED_KONGOR_INIT_SENTINEL", File.ReadAllText(trackedKongorInit));

        // Verify per-user configs were routed to userdataKONGOR/ instead
        Assert.True(File.Exists(Path.Combine(mockClientDir, "userdataKONGOR", "dxvk.conf")));
        Assert.True(File.Exists(Path.Combine(mockClientDir, "userdataKONGOR", "alsoft.ini")));
        Assert.True(File.Exists(Path.Combine(mockClientDir, "userdataKONGOR", "configuration", "autoexec.cfg")));
    }

    [Fact]
    public void DiagnosticRedactor_ScrubsJsonSecretsUriUserInfoIpv6AndPlayerNicknameWhilePreservingLoopbackAndTimestamps()
    {
        // protects: [L3A-P1-01] Diagnostic privacy scrubbing across JSON quoted keys, URI credentials, IPv6 peers, and player nicknames.
        // fails_when: JSON closing quote before ':' skips redaction, spaces truncate password scrubbing, IPv6 leaks or corrupts timestamps, or nickname leaks.
        // seam: DiagnosticRedactor.ScrubText.
        string syntheticUser = "dbadmin";
        string syntheticUriSecret = string.Concat("Sup3r", "$ecret!Pass");
        string syntheticDbUri = string.Concat("postgres://", syntheticUser, ":", syntheticUriSecret, "@198.51.100.77:5432/kongor_prod");
        string syntheticJsonSecretKey = string.Concat("pass", "word");
        string syntheticJsonSecretVal = string.Concat("My Spaced ", "S3cr3t!@# P@ssword");
        string rawPayload = string.Join(
            Environment.NewLine,
            $"[12:00:00] Connecting via {syntheticDbUri}",
            $"{{\"{syntheticJsonSecretKey}\": \"{syntheticJsonSecretVal}\", \"LastPlayerNickname\": \"ShadowBlade99\"}}",
            "SetSave \"login_name\" \"ShadowBlade99\"",
            "Peer connected from 2001:0db8:85a3:0000:0000:8a2e:0370:7334 and local ::1 at 23:59:59",
            "Active player ShadowBlade99 joined lobby");

        (string scrubbed, RedactionAuditCounts counts) = DiagnosticRedactor.ScrubText(
            rawPayload,
            allowedMasterServerHost: "api.kongor.net",
            allowedCdnHost: "cdn.kongor.net",
            playerNickname: "ShadowBlade99");

        Assert.DoesNotContain(syntheticUriSecret, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("dbadmin:", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticJsonSecretVal, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:0db8:85a3", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ShadowBlade99", scrubbed, StringComparison.Ordinal);

        Assert.Contains("[12:00:00]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("23:59:59", scrubbed, StringComparison.Ordinal);
        Assert.Contains("::1", scrubbed, StringComparison.Ordinal);
        Assert.True(counts.SecretsRedacted >= 2);
        Assert.True(counts.PeerIpsRedacted >= 2);
        Assert.True(counts.UserIdentifiersRedacted >= 1);
    }

    [Fact]
    public async Task DiagnosticBundleService_ExportBundleAsync_ScrubsReadmeTransparencyNoticeAndPlayerNickname()
    {
        // protects: [L3A-P1-01] Full-archive privacy scrubbing including README_WHAT_IS_SHARED.txt and manifest LocalFullDumpPath.
        // fails_when: README_WHAT_IS_SHARED.txt or manifest.json leaks OS user profile paths or configured LastPlayerNickname.
        // seam: DiagnosticBundleService.ExportBundleAsync + ZipArchive inspection.
        string clientRoot = Path.Combine(_tempDir, "diag_client");
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string outDir = Path.Combine(_tempDir, "diag_out");
        Directory.CreateDirectory(crashDir);
        Directory.CreateDirectory(outDir);

        LauncherSettingsModel settings = new LauncherSettingsModel
        {
            LastPlayerNickname = "SecretHeroNick77"
        };
        LauncherSettingsService.SaveToCanonicalPath(settings, Path.Combine(clientRoot, "userdataKONGOR"));

        string logDir = Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game");
        Directory.CreateDirectory(logDir);
        await File.WriteAllTextAsync(
            Path.Combine(logDir, "console.log"),
            "Login initiated for SecretHeroNick77 from 2001:db8:1234::abcd:9999\n");

        byte[] tier1Bytes = Encoding.UTF8.GetBytes("MDMP_STUB_SecretHeroNick77_PAYLOAD");
        string tier1DumpPath = Path.Combine(crashDir, "crash_20261103_180000.dmp");
        string richDumpPath = Path.Combine(crashDir, "crash_20261103_180000.rich.dmp");
        await File.WriteAllBytesAsync(tier1DumpPath, tier1Bytes);
        await File.WriteAllBytesAsync(richDumpPath, new byte[] { 1, 2, 3, 4 });

        DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
            clientRoot: clientRoot,
            outputDirectoryOverride: outDir,
            isCrashTriggered: true,
            revealInFileManager: false,
            preferredDumpPath: tier1DumpPath);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.ZipPath));

        using ZipArchive archive = ZipFile.OpenRead(result.ZipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            {
                using Stream dmpStream = entry.Open();
                using MemoryStream dmpMs = new MemoryStream();
                await dmpStream.CopyToAsync(dmpMs);
                string dmpAscii = Encoding.UTF8.GetString(dmpMs.ToArray());
                Assert.DoesNotContain("SecretHeroNick77", dmpAscii, StringComparison.Ordinal);
                continue;
            }

            using Stream entryStream = entry.Open();
            using StreamReader reader = new StreamReader(entryStream, Encoding.UTF8);
            string content = await reader.ReadToEndAsync();
            Assert.DoesNotContain("SecretHeroNick77", content, StringComparison.Ordinal);
            Assert.DoesNotContain("2001:db8:1234", content, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(Environment.UserName) && Environment.UserName.Length >= 3)
            {
                string userSegment = Path.DirectorySeparatorChar + Environment.UserName + Path.DirectorySeparatorChar;
                Assert.DoesNotContain(userSegment, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("https://evil-attacker.example.com/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://kongor.net.evil.example/")]
    [InlineData("http://cdn.kongor.net/")]
    [InlineData("CREDENTIALED_CDN_URI_CASE")]
    [InlineData("file:///etc/passwd")]
    public async Task ContentBroker_UntrustedOrInsecureCdnBaseUrl_ThrowsSecurityExceptionBeforeNetworkIo(string untrustedBaseUrl)
    {
        // protects: [L3A-P1-02] CDN domain allowlist and SSRF prevention in ContentBroker.FetchManifest and Synchronise.
        // fails_when: Untrusted domains, cloud metadata IPs, suffix-spoofed hosts, embedded credentials, or plain HTTP remote URLs issue HTTP requests.
        // seam: ContentBroker.ValidateAllowedCdnBaseUrl, FetchManifest, and Synchronise.
        string candidateUrl = string.Equals(untrustedBaseUrl, "CREDENTIALED_CDN_URI_CASE", StringComparison.Ordinal)
            ? string.Concat("https://", "admin", ":", "redacted_test", "@cdn.kongor.net/")
            : untrustedBaseUrl;

        int httpCalls = 0;
        CountingHttpMessageHandler handler = new CountingHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref httpCalls);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        SecurityException fetchEx = await Assert.ThrowsAsync<SecurityException>(() =>
            ContentBroker.FetchManifest("windows-x86-64", baseURL: candidateUrl, httpHandlerOverride: handler));
        Assert.Contains("[SECURITY-ALERT][CDN-ALLOWLIST]", fetchEx.Message, StringComparison.Ordinal);

        Manifest emptyManifest = new Manifest
        {
            Version = "1.0.0",
            Mode = "mirror",
            HashAlgorithm = "SHA256",
            ExcludeFromSource = Array.Empty<string>(),
            ExcludeFromTarget = Array.Empty<string>(),
            Files = new Dictionary<string, ManifestEntry>()
        };

        string syncTarget = Path.Combine(_tempDir, "ssrf_target");
        Directory.CreateDirectory(syncTarget);
        SecurityException syncEx = await Assert.ThrowsAsync<SecurityException>(() =>
            ContentBroker.Synchronise(emptyManifest, "windows-x86-64", syncTarget, baseURL: candidateUrl, httpHandlerOverride: handler));
        Assert.Contains("[SECURITY-ALERT][CDN-ALLOWLIST]", syncEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, httpCalls);
    }

    [Fact]
    public async Task MainViewModel_InspectCdnUpdateAsync_RejectsUnsignedRemoteNonOverlayMirrorManifest()
    {
        // protects: [L3A-P1-02] Mandatory ECDSA manifest signature verification for ALL remote CDN manifests, including Mode="mirror" (IsOverlay == false).
        // fails_when: MainViewModel only checks requireSignature = manifest.IsOverlay || isRemoteNewbsCdn, allowing unsigned cdn.kongor.net mirror manifests.
        // seam: MainViewModel.InspectCdnUpdateAsync -> ContentBroker.InspectUpdatePlan.
        string bundleRoot = Path.Combine(_tempDir, "unsigned_remote_bundle");
        Directory.CreateDirectory(Path.Combine(bundleRoot, "games", "client"));

        Manifest unsignedMirrorManifest = new Manifest
        {
            Version = "9.9.9",
            Mode = "mirror",
            HashAlgorithm = "SHA256",
            ExcludeFromSource = Array.Empty<string>(),
            ExcludeFromTarget = Array.Empty<string>(),
            Files = new Dictionary<string, ManifestEntry>
            {
                ["hon_x64.exe"] = new ManifestEntry
                {
                    Size = 4,
                    Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
                }
            },
            Signature = null
        };
        string manifestJson = JsonSerializer.Serialize(unsignedMirrorManifest, ManifestJSONContext.Default.Manifest);

        CountingHttpMessageHandler stubHandler = new CountingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
            });

        MainViewModel vm = new MainViewModel
        {
            LoadedMasterServerAddress = "api.kongor.net",
            MasterServerAddressIsValid = true,
            SelectedCDNAddressItem = new AddressSelectItem
            {
                DisplayText = "cdn.kongor.net",
                TargetURL = "https://cdn.kongor.net/"
            },
            BundleRootOverride = bundleRoot,
            CdnHttpHandlerOverride = stubHandler
        };
        vm.SetLocationSafetyVerdictForTesting(LocationSafetyVerdict.Safe);

        SynchronisationPlan? plan = await vm.InspectCdnUpdateAsync(silent: false);

        Assert.Null(plan);
        Assert.False(vm.HasAvailableCdnUpdate);
        Assert.True(vm.SynchronisationIsFailed);
        Assert.True(vm.SynchronisationStatusMessage.Contains("PKV Guard", StringComparison.OrdinalIgnoreCase) ||
                    vm.SynchronisationStatusMessage.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                    vm.SynchronisationStatusMessage.Contains("CDN-ALLOWLIST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AudioModernizationService_ResolveOpenAlDrivers_ReturnsPlatformSpecificBackend()
    {
        // protects: [L3A-P1-03] Cross-platform OpenAL Soft backend selection without hardcoding Windows-only 'wasapi' on Linux/macOS.
        // fails_when: GenerateAlsoftIni or ResolveOpenAlDrivers emits 'drivers = wasapi' on Linux or macOS.
        // seam: AudioModernizationService.ResolveOpenAlDrivers.
        Assert.Equal("wasapi", AudioModernizationService.ResolveOpenAlDrivers(OSPlatform.Windows));
        Assert.Equal("pulse,alsa", AudioModernizationService.ResolveOpenAlDrivers(OSPlatform.Linux));
        Assert.Equal("core", AudioModernizationService.ResolveOpenAlDrivers(OSPlatform.OSX));
    }

    [Fact]
    public void MainViewModel_ConfigureChildClientEnvironment_IsolatesEnvVarsToChildStartInfoOnly()
    {
        // protects: [L3A-P1-03] Host launcher process environment isolation (zero Environment.SetEnvironmentVariable pollution).
        // fails_when: ConfigureChildClientEnvironment mutates host process VK_INSTANCE_LAYERS, DXVK_CONFIG_FILE, ALSOFT_CONF, or KONGOR_IPC_AUTH_KEY.
        // seam: MainViewModel.ConfigureChildClientEnvironment + ProcessStartInfo.EnvironmentVariables.
        string sentinelLayer = "VK_LAYER_HOST_SENTINEL_" + Guid.NewGuid().ToString("N");
        string? prevVkLayers = Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS");
        string? prevIpcAuth = Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        string? prevAlsoft = Environment.GetEnvironmentVariable("ALSOFT_CONF");

        try
        {
            Environment.SetEnvironmentVariable("VK_INSTANCE_LAYERS", sentinelLayer);
            Environment.SetEnvironmentVariable("KONGOR_IPC_AUTH_KEY", null);
            Environment.SetEnvironmentVariable("ALSOFT_CONF", null);

            string clientDir = Path.Combine(_tempDir, "child_env_client");
            Directory.CreateDirectory(Path.Combine(clientDir, "userdataKONGOR"));
            File.WriteAllText(Path.Combine(clientDir, "userdataKONGOR", "dxvk.conf"), "dxvk.numCompilerThreads = 0");
            File.WriteAllText(Path.Combine(clientDir, "userdataKONGOR", "alsoft.ini"), "[general]\nhrtf = true");

            MainViewModel vm = new MainViewModel
            {
                AllowThirdPartyVulkanOverlays = false,
                SelectedVulkanHudMode = "Pro Diagnostics"
            };

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(clientDir, "hon_x64.exe"),
                UseShellExecute = false
            };

            vm.ConfigureChildClientEnvironment(startInfo, clientDir, ipcAuthKey: "per-launch-secret-token", isWindowsOverride: true);

            // Child ProcessStartInfo receives isolated configuration
            Assert.Equal("per-launch-secret-token", startInfo.EnvironmentVariables["KONGOR_IPC_AUTH_KEY"]);
            Assert.Equal(Path.Combine(clientDir, "userdataKONGOR", "dxvk.conf"), startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"]);
            Assert.Equal(Path.Combine(clientDir, "userdataKONGOR", "alsoft.ini"), startInfo.EnvironmentVariables["ALSOFT_CONF"]);
            Assert.Equal("VK_LAYER_HON_replay_capture", startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"]);
            Assert.Contains("~implicit~", startInfo.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"], StringComparison.Ordinal);

            // Host process environment remains 100% untouched
            Assert.Equal(sentinelLayer, Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS"));
            Assert.Null(Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY"));
            Assert.Null(Environment.GetEnvironmentVariable("ALSOFT_CONF"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VK_INSTANCE_LAYERS", prevVkLayers);
            Environment.SetEnvironmentVariable("KONGOR_IPC_AUTH_KEY", prevIpcAuth);
            Environment.SetEnvironmentVariable("ALSOFT_CONF", prevAlsoft);
        }
    }

    private sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
