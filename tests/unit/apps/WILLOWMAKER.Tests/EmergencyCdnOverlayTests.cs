// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EmergencyCdnOverlayTests.cs
// System: NEXUS CDN Broker & WILLOWMAKER
// Subsystem: Emergency Tester CDN Update Plan (Signed Additive Overlay & CAS)
// ============================================================================

using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Update;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class EmergencyCdnOverlayTests
{
    private static (ECDsa ecdsa, string xHex, string yHex) CreateTestKeyPair()
    {
        ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = ecdsa.ExportParameters(true);
        string xHex = Convert.ToHexStringLower(parameters.Q.X!);
        string yHex = Convert.ToHexStringLower(parameters.Q.Y!);
        return (ecdsa, xHex, yHex);
    }

    private sealed class MockCdnHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte[]> _routes = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string url, byte[] content)
        {
            _routes[url] = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            if (_routes.TryGetValue(uri, out byte[]? bytes))
            {
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Synchronise_ValidSignedOverlayCasManifest_DownloadsExactBytes_PreservesBaseArchives_AndIsIdempotent()
    {
        // protects: CDN overlay update idempotency, CAS object retrieval, and base game archive preservation
        // fails_when: CDN overlay deletes unlisted files, fails to write ledger, or touches disk on up-to-date checks
        // seam: ContentBroker.Synchronise with MockCdnHttpMessageHandler against real filesystem temp bundle

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_cdn_overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            // Seed base game archive that must NEVER be touched
            string baseArchivePath = Path.Combine(bundleRoot, "games", "client", "game", "resources00.s2z");
            Directory.CreateDirectory(Path.GetDirectoryName(baseArchivePath)!);
            byte[] baseBytes = "Original 15GB Base Game Archive Data"u8.ToArray();
            await File.WriteAllBytesAsync(baseArchivePath, baseBytes);
            DateTime baseArchiveOriginalMtime = File.GetLastWriteTimeUtc(baseArchivePath);

            // Prepare hotfix payload to be delivered via CAS
            string hotfixRelPath = "games/client/override/ui/fe2/store2.lua";
            byte[] hotfixBytes = "-- Emergency Fix for Store v2.1\nreturn true;\n"u8.ToArray();
            string hotfixHash = Convert.ToHexStringLower(SHA256.HashData(hotfixBytes));

            // Setup CAS object URL route
            string casUrl = ContentBroker.BuildObjectURL("https://cdn.test.local/", hotfixHash);
            MockCdnHttpMessageHandler mockHandler = new();
            mockHandler.Register(casUrl, hotfixBytes);

            // Construct manifest
            var (testEcdsa, testXHex, testYHex) = CreateTestKeyPair();
            using (testEcdsa)
            {
            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    [hotfixRelPath] = new() { Size = hotfixBytes.Length, Hash = hotfixHash }
                }
            };

            // Sign manifest
            string signature = ManifestSignatureVerifier.SignManifest(manifest, testEcdsa);
            manifest = manifest with { Signature = signature };

            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");

            // First Synchronise: Apply hotfix
            SynchronisationSummary summary1 = await ContentBroker.Synchronise(
                manifest,
                "wac",
                bundleRoot,
                baseURL: "https://cdn.test.local/",
                requireSignature: true,
                publicKeyXHexOverride: testXHex,
                publicKeyYHexOverride: testYHex,
                enforceEmergencyBundleAllowlist: true,
                installLedgerPath: ledgerPath,
                httpHandlerOverride: mockHandler);

            summary1.FilesDownloaded.Should().Be(1);
            summary1.FilesDeleted.Should().Be(0);
            summary1.FilesFailed.Should().Be(0);

            // Verify hotfix file is present with exact bytes
            string hotfixLocalPath = Path.Combine(bundleRoot, hotfixRelPath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(hotfixLocalPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(hotfixLocalPath)).Should().Equal(hotfixBytes);

            // Verify base archive was untouched
            File.Exists(baseArchivePath).Should().BeTrue();
            (await File.ReadAllBytesAsync(baseArchivePath)).Should().Equal(baseBytes);
            File.GetLastWriteTimeUtc(baseArchivePath).Should().Be(baseArchiveOriginalMtime);

            // Verify ledger recorded the installation
            File.Exists(ledgerPath).Should().BeTrue();
            CdnInstallLedger ledger = CdnInstallLedger.LoadOrEmpty(ledgerPath);
            ledger.Version.Should().Be("5.0.1");
            ledger.InstalledFiles.Should().ContainKey(hotfixRelPath);
            ledger.InstalledFiles[hotfixRelPath].Should().Be(hotfixHash);

            DateTime hotfixMtime1 = File.GetLastWriteTimeUtc(hotfixLocalPath);
            DateTime ledgerMtime1 = File.GetLastWriteTimeUtc(ledgerPath);

            // Wait 15ms so timestamp differences would be visible if disk was touched
            await Task.Delay(15);

            // Second Synchronise: Idempotent repeat check
            SynchronisationSummary summary2 = await ContentBroker.Synchronise(
                manifest,
                "wac",
                bundleRoot,
                baseURL: "https://cdn.test.local/",
                requireSignature: true,
                publicKeyXHexOverride: testXHex,
                publicKeyYHexOverride: testYHex,
                enforceEmergencyBundleAllowlist: true,
                installLedgerPath: ledgerPath,
                httpHandlerOverride: mockHandler);

            summary2.FilesDownloaded.Should().Be(0);
            summary2.FilesDeleted.Should().Be(0);
            summary2.FilesUpToDate.Should().Be(1);
            summary2.FilesFailed.Should().Be(0);

            // Zero disk writes verification: file timestamps must be bit-identical
            File.GetLastWriteTimeUtc(hotfixLocalPath).Should().Be(hotfixMtime1);
            File.GetLastWriteTimeUtc(ledgerPath).Should().Be(ledgerMtime1);
            }
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task Synchronise_UnsignedOrTamperedManifest_ThrowsSecurityExceptionBeforeDiskAccess()
    {
        // protects: Cryptographic verification boundary protecting testers from unauthenticated CDN modifications
        // fails_when: Manifest with missing or modified fields bypasses signature verification
        // seam: ManifestSignatureVerifier.VerifyManifest and ContentBroker.Synchronise validation gate

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_tamper_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            var (testEcdsa, testXHex, testYHex) = CreateTestKeyPair();
            using (testEcdsa)
            {

            Manifest validManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/ui/fix.lua"] = new() { Size = 100, Hash = new string('a', 64) }
                }
            };
            string signature = ManifestSignatureVerifier.SignManifest(validManifest, testEcdsa);

            // Case 1: Unsigned manifest when signature required
            Manifest unsignedManifest = validManifest with { Signature = null };
            Func<Task> actUnsigned = async () => await ContentBroker.Synchronise(
                unsignedManifest, "wac", bundleRoot, requireSignature: true, publicKeyXHexOverride: testXHex, publicKeyYHexOverride: testYHex);
            await actUnsigned.Should().ThrowAsync<SecurityException>()
                .WithMessage("*signature*");

            // Case 2: Tampered hash in files
            Manifest tamperedHash = validManifest with
            {
                Signature = signature,
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/override/ui/fix.lua"] = new() { Size = 100, Hash = new string('b', 64) }
                }
            };
            Func<Task> actTamperedHash = async () => await ContentBroker.Synchronise(
                tamperedHash, "wac", bundleRoot, requireSignature: true, publicKeyXHexOverride: testXHex, publicKeyYHexOverride: testYHex);
            await actTamperedHash.Should().ThrowAsync<SecurityException>()
                .WithMessage("*signature verification failed*");

            // Case 3: Tampered version string
            Manifest tamperedVersion = validManifest with { Version = "5.0.2", Signature = signature };
            Func<Task> actTamperedVersion = async () => await ContentBroker.Synchronise(
                tamperedVersion, "wac", bundleRoot, requireSignature: true, publicKeyXHexOverride: testXHex, publicKeyYHexOverride: testYHex);
            await actTamperedVersion.Should().ThrowAsync<SecurityException>()
                .WithMessage("*signature verification failed*");

            // Case 4: Tampered remove list
            Manifest tamperedRemove = validManifest with { Remove = ["games/client/override/old.lua"], Signature = signature };
            Func<Task> actTamperedRemove = async () => await ContentBroker.Synchronise(
                tamperedRemove, "wac", bundleRoot, requireSignature: true, publicKeyXHexOverride: testXHex, publicKeyYHexOverride: testYHex);
            await actTamperedRemove.Should().ThrowAsync<SecurityException>()
                .WithMessage("*signature verification failed*");
            }
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public void IsAllowedEmergencyBundlePath_EnforcesAllowlistAndRejectsBaseArchives()
    {
        // protects: Strict path boundary preventing changes outside emergency overlay scope
        // fails_when: Base archives or userdata are permitted, or legal hotfix binary paths are rejected
        // seam: ContentBroker.IsAllowedEmergencyBundlePath

        // Allowed Hotfix Paths
        ContentBroker.IsAllowedEmergencyBundlePath("games/client/override/ui/fe2/store2.lua", out string? r1).Should().BeTrue();
        r1.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/override/scripts/common.lua", out string? r2).Should().BeTrue();
        r2.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/version.dll", out string? r3).Should().BeTrue();
        r3.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/server/version.dll", out string? r4).Should().BeTrue();
        r4.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/linux-x64/hon-x86_64", out string? r5).Should().BeTrue();
        r5.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/macos-x64/libk2.dylib", out string? r6).Should().BeTrue();
        r6.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/server/hon-x86_64-server", out string? r7).Should().BeTrue();
        r7.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/server_p2p/hon-x86_64-server", out string? r8).Should().BeTrue();
        r8.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/libcurl.so.4", out string? r9).Should().BeTrue();
        r9.Should().BeNull();

        ContentBroker.IsAllowedEmergencyBundlePath("games/server/libcurl.so.4", out string? r10).Should().BeTrue();
        r10.Should().BeNull();

        // Disallowed Dangerous Paths
        ContentBroker.IsAllowedEmergencyBundlePath("games/client/game/resources00.s2z", out string? d1).Should().BeFalse();
        d1.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/shared/resources00.s2z", out string? d2).Should().BeFalse();
        d2.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/override/resources99.s2z", out string? d3).Should().BeFalse();
        d3.Should().Contain("S2Z archives are forbidden under games/client/override");

        ContentBroker.IsAllowedEmergencyBundlePath("games/server/game/resources00.s2z", out string? d4).Should().BeFalse();
        d4.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("userdata/player.cfg", out string? d5).Should().BeFalse();
        d5.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/userdata/options.cfg", out string? d6).Should().BeFalse();
        d6.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/unknown_tool.txt", out string? d7).Should().BeFalse();
        d7.Should().Contain("outside the emergency bundle allowlist");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/windows-x64/patch.s2z", out string? d8).Should().BeFalse();
        d8.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/linux-x64/patch.zip", out string? d9).Should().BeFalse();
        d9.Should().Contain("strictly forbidden");

        ContentBroker.IsAllowedEmergencyBundlePath("games/client/linux-x64/base/core.cfg", out string? d10).Should().BeFalse();
        d10.Should().Contain("strictly forbidden");
    }

    [Fact]
    public async Task Synchronise_HardlinkedTargetFile_RefusesOverwriteToPreventMultiplayerDesync()
    {
        // protects: Shared client/server asset hardlinks (FN-014) from being broken by updates
        // fails_when: ContentBroker overwrites a hardlinked file instead of aborting safely
        // seam: NativeFileOperations.GetHardLinkCount and ContentBroker.Synchronise hardlink guard

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_hardlink_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            string fileA = Path.Combine(bundleRoot, "shared_master.dll");
            string fileB = Path.Combine(bundleRoot, "games", "client", "version.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);

            byte[] originalBytes = "Original Shared Version DLL"u8.ToArray();
            await File.WriteAllBytesAsync(fileA, originalBytes);

            bool hardlinkCreated = NativeFileOperations.CreateHardLink(fileB, fileA);
            hardlinkCreated.Should().BeTrue();

            NativeFileOperations.GetHardLinkCount(fileB).Should().BeGreaterThanOrEqualTo(2);

            // Setup manifest that attempts to overwrite fileB with new content
            byte[] newBytes = "New Version DLL"u8.ToArray();
            string newHash = Convert.ToHexStringLower(SHA256.HashData(newBytes));

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["games/client/version.dll"] = new() { Size = newBytes.Length, Hash = newHash }
                }
            };

            Func<Task> act = async () => await ContentBroker.Synchronise(
                manifest,
                "wac",
                bundleRoot);

            await act.Should().ThrowAsync<IOException>()
                .WithMessage("*Refusing to overwrite hardlinked target file*");

            // Ensure original hardlinked file bytes were preserved intact
            (await File.ReadAllBytesAsync(fileA)).Should().Equal(originalBytes);
            (await File.ReadAllBytesAsync(fileB)).Should().Equal(originalBytes);
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task Synchronise_RollbackViaManifestRemove_DeletesOnlyLedgerTrackedMatchingFiles()
    {
        // protects: Safe hotfix uninstallation and rollback while protecting user modifications and untracked files
        // fails_when: Manifest Remove deletes files not installed by CDN or deletes user-modified files
        // seam: ContentBroker.Synchronise manifest.Remove pass against CdnInstallLedger

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_rollback_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

            // File 1: Legitimate CDN-installed hotfix file
            string file1Rel = "games/client/override/ui/revert_target.lua";
            string file1Full = Path.Combine(bundleRoot, file1Rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file1Full)!);
            byte[] file1Bytes = "Hotfix Code to Revert"u8.ToArray();
            await File.WriteAllBytesAsync(file1Full, file1Bytes);
            string file1Hash = Convert.ToHexStringLower(SHA256.HashData(file1Bytes));

            // File 2: User modified hotfix file (hash on disk differs from ledger)
            string file2Rel = "games/client/override/ui/user_modified.lua";
            string file2Full = Path.Combine(bundleRoot, file2Rel.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(file2Full, "User customized modifications"u8.ToArray());
            string file2OriginalHash = Convert.ToHexStringLower(SHA256.HashData("Original Hotfix Code"u8.ToArray()));

            // File 3: Untracked file (absent from ledger)
            string file3Rel = "games/client/override/ui/untracked_user.lua";
            string file3Full = Path.Combine(bundleRoot, file3Rel.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(file3Full, "Untracked User Script"u8.ToArray());

            // Initialize ledger with file1 and file2's original hash
            CdnInstallLedger ledger = new()
            {
                Version = "1.0.0",
                UpdatedAtUtc = TimeProvider.System.GetUtcNow().ToString("O"),
                InstalledFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [file1Rel] = file1Hash,
                    [file2Rel] = file2OriginalHash
                }
            };
            CdnInstallLedger.SaveIfChangedAtomically(ledgerPath, ledger);

            // Manifest requests removing all 3 files
            Manifest rollbackManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>(),
                Remove = [file1Rel, file2Rel, file3Rel]
            };

            SynchronisationSummary summary = await ContentBroker.Synchronise(
                rollbackManifest,
                "wac",
                bundleRoot,
                installLedgerPath: ledgerPath);

            // File 1 should have been deleted
            summary.FilesDeleted.Should().Be(1);
            File.Exists(file1Full).Should().BeFalse();

            // File 2 should NOT be deleted (user modified)
            File.Exists(file2Full).Should().BeTrue();

            // File 3 should NOT be deleted (not tracked in ledger)
            File.Exists(file3Full).Should().BeTrue();

            // Ledger should now be updated without file1, but retaining file2
            CdnInstallLedger updatedLedger = CdnInstallLedger.LoadOrEmpty(ledgerPath);
            updatedLedger.InstalledFiles.Should().NotContainKey(file1Rel);
            updatedLedger.InstalledFiles.Should().ContainKey(file2Rel);
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task Synchronise_RemoveOnNativeBinaryOrLooseShader_ThrowsSecurityException()
    {
        // protects: Non-destructive rollback guarantee preventing deletions of shipped native binaries or loose HLSL shaders.
        // fails_when: Manifest Remove list containing DLLs, executables, or shaders executes deletion instead of throwing SecurityException.
        // seam: ContentBroker.IsAllowedEmergencyRemovePath and ContentBroker.Synchronise remove validation gate.

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_remove_guard_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            // Unit check: IsAllowedEmergencyRemovePath
            ContentBroker.IsAllowedEmergencyRemovePath("games/client/version.dll", out string? r1).Should().BeFalse();
            r1.Should().Contain("Destructive Remove is forbidden");

            ContentBroker.IsAllowedEmergencyRemovePath("games/client/override/core/shaders/ps_3_0/mesh.psh", out string? r2).Should().BeFalse();
            r2.Should().Contain("Destructive Remove is forbidden");

            ContentBroker.IsAllowedEmergencyRemovePath("games/client/override/ui/fe2/store2.lua", out string? r3).Should().BeTrue();
            r3.Should().BeNull();

            // Integration check: ContentBroker.Synchronise with Remove on native binary
            Manifest manifestNative = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>(),
                Remove = ["games/client/version.dll"]
            };

            Func<Task> actNative = async () => await ContentBroker.Synchronise(
                manifestNative,
                "wac",
                bundleRoot,
                enforceEmergencyBundleAllowlist: true);

            await actNative.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Disallowed emergency bundle path in manifest remove list*");

            // Integration check: ContentBroker.Synchronise with Remove on loose shader
            Manifest manifestShader = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>(),
                Remove = ["games/client/override/core/shaders/ps_3_0/mesh.psh"]
            };

            Func<Task> actShader = async () => await ContentBroker.Synchronise(
                manifestShader,
                "wac",
                bundleRoot,
                enforceEmergencyBundleAllowlist: true);

            await actShader.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Disallowed emergency bundle path in manifest remove list*");
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task Synchronise_PreExistingPaths_ProtectsPreExistingLooseOverrideFile_FromFutureRemove()
    {
        // protects: Pre-existing loose override files present prior to CDN installation from being destroyed during rollback.
        // fails_when: CDN rollback deletes a file that existed on disk before CDN update installation.
        // seam: CdnInstallLedger.PreExistingPaths guard inside ContentBroker.Synchronise Remove pass.

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_preexist_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            string preExistRel = "games/client/override/ui/preexisting_user.lua";
            string preExistFull = Path.Combine(bundleRoot, preExistRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(preExistFull)!);
            byte[] fileBytes = "-- Pre-existing user override file\nreturn 42;\n"u8.ToArray();
            await File.WriteAllBytesAsync(preExistFull, fileBytes);
            string fileHash = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

            CdnInstallLedger ledger = new()
            {
                Version = "1.0.0",
                MaxSequenceSeen = 1,
                UpdatedAtUtc = TimeProvider.System.GetUtcNow().ToString("O"),
                InstalledFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [preExistRel] = fileHash
                },
                PreExistingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    preExistRel
                }
            };
            CdnInstallLedger.SaveIfChangedAtomically(ledgerPath, ledger);

            Manifest rollbackManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 2,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>(),
                Remove = [preExistRel]
            };

            SynchronisationSummary summary = await ContentBroker.Synchronise(
                rollbackManifest,
                "wac",
                bundleRoot,
                installLedgerPath: ledgerPath);

            summary.FilesDeleted.Should().Be(0);
            File.Exists(preExistFull).Should().BeTrue();
            (await File.ReadAllBytesAsync(preExistFull)).Should().Equal(fileBytes);
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public async Task Synchronise_SequenceDowngrade_ThrowsSecurityException()
    {
        // protects: Monotonic sequence anti-replay and anti-downgrade boundary.
        // fails_when: Manifest with an older sequence than installed ledger is applied.
        // seam: ContentBroker.Synchronise preflight ledger sequence check.

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_seq_downgrade_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

            CdnInstallLedger ledger = new()
            {
                Version = "1.2.0",
                MaxSequenceSeen = 2,
                UpdatedAtUtc = TimeProvider.System.GetUtcNow().ToString("O"),
                InstalledFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            CdnInstallLedger.SaveIfChangedAtomically(ledgerPath, ledger);

            Manifest downgradeManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };

            Func<Task> act = async () => await ContentBroker.Synchronise(
                downgradeManifest,
                "wac",
                bundleRoot,
                installLedgerPath: ledgerPath);

            await act.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Manifest sequence downgrade rejected: manifest sequence 1 is older than installed sequence 2.*");

            // Skipped-hotfix anti-replay check: a clean install (!File.Exists) that first checks during
            // an empty rollback release (Sequence = 3) must record MaxSequenceSeen = 3 so Sequence = 2 cannot be replayed.
            string skippedLedgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "skipped_hotfix_ledger.json");
            File.Exists(skippedLedgerPath).Should().BeFalse();

            Manifest cleanRollbackSeq3 = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.2-rollback",
                Sequence = 3,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>(),
                Remove = ["games/client/override/ui/fe2/never_installed.lua"]
            };

            await ContentBroker.Synchronise(
                cleanRollbackSeq3,
                "wac",
                bundleRoot,
                installLedgerPath: skippedLedgerPath);

            File.Exists(skippedLedgerPath).Should().BeTrue();
            CdnInstallLedger.LoadOrEmpty(skippedLedgerPath).MaxSequenceSeen.Should().Be(3);

            Manifest replayedSeq2 = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1",
                Sequence = 2,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };

            Func<Task> replayAct = async () => await ContentBroker.Synchronise(
                replayedSeq2,
                "wac",
                bundleRoot,
                installLedgerPath: skippedLedgerPath);

            await replayAct.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Manifest sequence downgrade rejected: manifest sequence 2 is older than installed sequence 3.*");

            // Scalar transcript control-character injection check
            Manifest badScalarManifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.1\nsequence=999",
                Sequence = 1,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>()
            };
            Action scalarAct = () => ManifestSignatureVerifier.BuildCanonicalTranscript(badScalarManifest);
            scalarAct.Should().Throw<SecurityException>()
                .WithMessage("*Control character forbidden in manifest Version*");
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }

    [Fact]
    public void VerifyManifest_OfflineRecoveryKey_AcceptsRecoverySignedManifestWithoutOverrides()
    {
        // protects: Production dual-oracle signing fallback allowing disaster recovery signatures using the offline recovery key.
        // fails_when: ManifestSignatureVerifier fails to fallback to RecoveryPublicKeyXHex/YHex or rejects signature.
        // seam: ManifestSignatureVerifier.VerifyManifest dual-key verification fallback.

        Manifest manifest = new()
        {
            Protocol = Manifest.PkvProtocolV5,
            Version = "5.0.0",
            Sequence = 1,
            HashAlgorithm = "SHA-256",
            Mode = "overlay",
            ObjectLayout = "sha256",
            ExcludeFromSource = [],
            ExcludeFromTarget = [],
            Files = new Dictionary<string, ManifestEntry>(),
            Remove = [],
            ExecutablePaths = [],
            Signature = "nQXD6qyh0MjEpjVZeup8skoF5lFEREYB/WZ+1i2LvWvW/cXLaOXqhxOtiqtmv9ZPdkBI3yIHjCnF5eZbdCYCvg=="
        };

        bool verified = ManifestSignatureVerifier.VerifyManifest(manifest, out string? failureReason);
        failureReason.Should().BeNull();
        verified.Should().BeTrue();
    }

    [Fact]
    public async Task Synchronise_PartialDownloadFailure_PersistsSucceededDownloadsToLedger()
    {
        // protects: Partial-failure ledger durability ensuring files successfully placed during an interrupted or multi-file failure run are tracked.
        // fails_when: Single file 404 causes ledger to lose record of sibling files that successfully downloaded.
        // seam: ContentBroker.Synchronise partial-failure ledger persistence.

        string bundleRoot = Path.Combine(Path.GetTempPath(), "kongor_partial_fail_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);

        try
        {
            string file1Rel = "games/client/override/ui/file1.lua";
            byte[] file1Bytes = "-- Succeeded File"u8.ToArray();
            string file1Hash = Convert.ToHexStringLower(SHA256.HashData(file1Bytes));

            string file2Rel = "games/client/override/ui/file2.lua";
            byte[] file2Bytes = "-- Missing Remote File"u8.ToArray();
            string file2Hash = Convert.ToHexStringLower(SHA256.HashData(file2Bytes));

            string file1Url = ContentBroker.BuildObjectURL("https://cdn.test.local/", file1Hash);
            MockCdnHttpMessageHandler mockHandler = new();
            mockHandler.Register(file1Url, file1Bytes);
            // file2Url is not registered -> returns 404

            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 5,
                Mode = "overlay",
                HashAlgorithm = "SHA-256",
                ObjectLayout = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    [file1Rel] = new() { Size = file1Bytes.Length, Hash = file1Hash },
                    [file2Rel] = new() { Size = file2Bytes.Length, Hash = file2Hash }
                }
            };

            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");

            SynchronisationSummary summary = await ContentBroker.Synchronise(
                manifest,
                "wac",
                bundleRoot,
                baseURL: "https://cdn.test.local/",
                installLedgerPath: ledgerPath,
                httpHandlerOverride: mockHandler);

            summary.FilesDownloaded.Should().Be(1);
            summary.FilesFailed.Should().Be(1);

            string file1Local = Path.Combine(bundleRoot, file1Rel.Replace('/', Path.DirectorySeparatorChar));
            string file2Local = Path.Combine(bundleRoot, file2Rel.Replace('/', Path.DirectorySeparatorChar));

            File.Exists(file1Local).Should().BeTrue();
            File.Exists(file2Local).Should().BeFalse();

            // Succeeded file must be tracked in the ledger
            File.Exists(ledgerPath).Should().BeTrue();
            CdnInstallLedger ledger = CdnInstallLedger.LoadOrEmpty(ledgerPath);
            ledger.InstalledFiles.Should().ContainKey(file1Rel);
            ledger.InstalledFiles[file1Rel].Should().Be(file1Hash);
            ledger.InstalledFiles.Should().NotContainKey(file2Rel);

            // Because filesFailed > 0, MaxSequenceSeen must NOT advance
            ledger.MaxSequenceSeen.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(bundleRoot))
            {
                Directory.Delete(bundleRoot, true);
            }
        }
    }
}
