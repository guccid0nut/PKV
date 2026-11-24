// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.IPC;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests.Security;

public sealed class Wave0UpdaterAndExperimentalD3d12Tests
{
    [Fact]
    public void VersionChecker_LatestReleaseUrl_TargetsGuccidonutPkvRepository()
    {
        // protects: Self-update repository trust anchor so WILLOWMAKER never polls a stale upstream GitHub org.
        // fails_when: LatestReleaseURL points outside guccidonut/PKV.
        // seam: VersionChecker.LatestReleaseURL constant + TryParseSha256SumsForAsset parser.

        Assert.Equal("https://api.github.com/repos/guccidonut/PKV/releases/latest", VersionChecker.LatestReleaseURL);

        string sampleSums =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *windows-x64-v1.2.3.tar.zst\n" +
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210  linux-x64-v1.2.3.zip\n";

        string? parsed = VersionChecker.TryParseSha256SumsForAsset(sampleSums, "windows-x64-v1.2.3.tar.zst");
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", parsed);
    }

    [Fact]
    public async Task VersionChecker_VerifyUpdateArchiveIntegrityAsync_FailsClosedOnMissingOrTamperedHash_AndDeletesTamperedArchive()
    {
        // protects: Self-update archive integrity gate prior to extracting or executing replacement binaries.
        // fails_when: Unsigned or tampered archive passes verification without throwing CryptographicException.
        // seam: VersionChecker.VerifyUpdateArchiveIntegrityAsync + ExtractZipSafely (ZipSlip protection).

        string tempDir = Path.Combine(Path.GetTempPath(), $"wm_upd_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string? prevUnsignedEnv = Environment.GetEnvironmentVariable("KONGOR_ALLOW_UNSIGNED_SELF_UPDATE");
        Environment.SetEnvironmentVariable("KONGOR_ALLOW_UNSIGNED_SELF_UPDATE", null);

        try
        {
            string archivePath = Path.Combine(tempDir, "update.zip");
            byte[] payload = Encoding.UTF8.GetBytes("authentic-release-archive-bytes");
            await File.WriteAllBytesAsync(archivePath, payload);
            string validSha = Convert.ToHexStringLower(SHA256.HashData(payload));

            // 1. Missing hash -> fails closed
            await Assert.ThrowsAsync<CryptographicException>(
                () => VersionChecker.VerifyUpdateArchiveIntegrityAsync(archivePath, expectedSha256: null));
            Assert.True(File.Exists(archivePath));

            // 2. Valid hash (including GitHub 'sha256:' prefix) -> succeeds
            await VersionChecker.VerifyUpdateArchiveIntegrityAsync(archivePath, $"sha256:{validSha.ToUpperInvariant()}");

            // 3. Tampered hash -> throws CryptographicException AND deletes tampered file
            string wrongSha = new string('a', 64);
            await Assert.ThrowsAsync<CryptographicException>(
                () => VersionChecker.VerifyUpdateArchiveIntegrityAsync(archivePath, wrongSha));
            Assert.False(File.Exists(archivePath));

            // 4. ZipSlip entry -> blocked by ExtractZipSafely
            string zipSlipArchive = Path.Combine(tempDir, "zipslip.zip");
            using (FileStream fs = File.Create(zipSlipArchive))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry evil = zip.CreateEntry("../../escaped.exe");
                await using Stream entryStream = evil.Open();
                await entryStream.WriteAsync("evil"u8.ToArray());
            }

            string extractDir = Path.Combine(tempDir, "extract");
            InvalidOperationException zipEx = Assert.Throws<InvalidOperationException>(
                () => VersionChecker.ExtractZipSafely(zipSlipArchive, extractDir));
            Assert.Contains("ZipSlip", zipEx.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_ALLOW_UNSIGNED_SELF_UPDATE", prevUnsignedEnv);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WillowmakerIpcService_WhenExplicitAuthKeyConfigured_BlocksUnauthenticatedSyncEndpointsAndBringToFront()
    {
        // protects: Launcher endpoint-redirect & foreground actuation opcodes (0x0030, 0x0032) when session HMAC key is active.
        // fails_when: Unauthenticated IPC client can redirect MasterServer/CDN endpoints while an authenticated session key is configured.
        // seam: WillowmakerIpcClient <-> WillowmakerIpcService (Enforced + AuthKey) <-> IpcSecurityValidator.IsMutatingOpCode.

        string pipeName = $"wm-sec-auth-{Guid.NewGuid():N}";
        await using WillowmakerIpcService service = new(
            pipeName,
            authKey: Guid.NewGuid().ToString("N"),
            sandboxMode: NEXUS.Contracts.Ipc.IpcSandboxMode.Enforced);

        bool syncCalled = false;
        service.OnEndpointSyncRequested = _ =>
        {
            syncCalled = true;
            return Task.FromResult(new CockpitEndpointSyncResponseDto { Success = true });
        };

        await service.EnsureListeningAsync(3000);

        await using WillowmakerIpcClient unauthClient = new(pipeName, authKey: string.Empty);
        await unauthClient.ConnectAsync(timeoutMs: 3000);

        CockpitEndpointSyncResponseDto? syncResp = await unauthClient.SyncEndpointsAsync(
            new CockpitEndpointSyncRequestDto
            {
                MasterServerEndpoint = "attacker.example:55555",
                CdnEndpoint = "attacker.example:55555/cdn"
            },
            timeoutMs: 3000);

        Assert.False(syncCalled);
        Assert.NotNull(syncResp);
        Assert.False(syncResp!.Success);
        Assert.Contains("401 Unauthorized", syncResp.Message);

        bool focused = await unauthClient.BringToFrontAsync(timeoutMs: 3000);
        Assert.False(focused);
    }

    [Fact]
    public void MainViewModel_ExperimentalD3D12_QuarantinedByDefaultUnlessEnvFlagSet()
    {
        // protects: Production swapchain selector so players cannot accidentally select incomplete experimental vid_d3d12.
        // fails_when: AvailableSwapchainBackends exposes D3D9On12 without KONGOR_EXPERIMENTAL_D3D12=1.
        // seam: MainViewModel.IsExperimentalD3D12Enabled + AvailableSwapchainBackends initialization.

        string? prev = Environment.GetEnvironmentVariable("KONGOR_EXPERIMENTAL_D3D12");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_EXPERIMENTAL_D3D12", null);
            Assert.False(MainViewModel.IsExperimentalD3D12Enabled());
            MainViewModel defaultVm = new();
            Assert.DoesNotContain(defaultVm.AvailableSwapchainBackends, b => b.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("KONGOR_EXPERIMENTAL_D3D12", "1");
            Assert.True(MainViewModel.IsExperimentalD3D12Enabled());
            MainViewModel expVm = new();
            Assert.Contains(expVm.AvailableSwapchainBackends, b => b.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_EXPERIMENTAL_D3D12", prev);
        }
    }
}
