// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using ASPIRE.Common.Services.Vfs;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class CdnBrokerSecurityHardeningTests
{
    [Fact]
    public async Task ContentBroker_Synchronise_DirectoryTraversalInManifest_ThrowsSecurityException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_cdn_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            Manifest manifest = new()
            {
                Protocol = Manifest.PkvProtocolV5,
                Version = "5.0.0",
                Sequence = 1,
                Mode = "overlay",
                ObjectLayout = "sha256",
                HashAlgorithm = "sha256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["../../windows/win.ini"] = new() { Size = 100, Hash = "d41d8cd98f00b204e9800998ecf8427e" }
                }
            };

            Func<Task> act = async () => await ContentBroker.Synchronise(manifest, "retail/x86-64", tempDir);

            await act.Should().ThrowAsync<SecurityException>()
                .WithMessage("*Directory traversal*");
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
    public void S2zPackageSecurityValidator_ValidZipArchive_Succeeds()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "kongor_valid_" + Guid.NewGuid() + ".s2z");
        try
        {
            using (FileStream fs = File.Create(tempFile))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("textures/hero.dds");
                using Stream s = entry.Open();
                s.WriteByte(0x42);
            }

            var (isSafe, failureReason) = S2zPackageSecurityValidator.VerifyArchiveStructure(tempFile);
            isSafe.Should().BeTrue();
            failureReason.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void S2zPackageSecurityValidator_InvalidMagicHeader_FailsValidation()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "kongor_invalid_magic_" + Guid.NewGuid() + ".s2z");
        try
        {
            File.WriteAllBytes(tempFile, [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x00]); // ELF header, not PK

            var (isSafe, failureReason) = S2zPackageSecurityValidator.VerifyArchiveStructure(tempFile);
            isSafe.Should().BeFalse();
            failureReason.Should().Contain("Invalid archive magic header");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void S2zPackageSecurityValidator_FileTooSmall_FailsValidation()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "kongor_too_small_" + Guid.NewGuid() + ".s2z");
        try
        {
            File.WriteAllBytes(tempFile, [0x50, 0x4B]); // Only 2 bytes

            var (isSafe, failureReason) = S2zPackageSecurityValidator.VerifyArchiveStructure(tempFile);
            isSafe.Should().BeFalse();
            failureReason.Should().Contain("too small");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void S2zPackageSecurityValidator_ZipSlipEntry_FailsValidation()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "kongor_zipslip_" + Guid.NewGuid() + ".s2z");
        try
        {
            using (FileStream fs = File.Create(tempFile))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("../../../malicious.dll");
                using Stream s = entry.Open();
                s.WriteByte(0x90);
            }

            var (isSafe, failureReason) = S2zPackageSecurityValidator.VerifyArchiveStructure(tempFile);
            isSafe.Should().BeFalse();
            failureReason.Should().Contain("Zip-slip path traversal");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LocalFileMatchesManifestEntry_WithSha384AndSha512_MatchesAccurately()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "kongor_hash_test_" + Guid.NewGuid() + ".dat");
        byte[] content = "Hello, KONGOR Modernization!"u8.ToArray();
        await File.WriteAllBytesAsync(tempFile, content);

        try
        {
            byte[] sha256Bytes = SHA256.HashData(content);
            byte[] sha384Bytes = SHA384.HashData(content);
            byte[] sha512Bytes = SHA512.HashData(content);

            ManifestEntry entry256 = new() { Size = content.Length, Hash = Convert.ToHexStringLower(sha256Bytes) };
            ManifestEntry entry384 = new() { Size = content.Length, Hash = Convert.ToHexStringLower(sha384Bytes) };
            ManifestEntry entry512 = new() { Size = content.Length, Hash = Convert.ToHexStringLower(sha512Bytes) };

            // Accurate matches
            bool match256 = await ContentBroker.LocalFileMatchesManifestEntry(tempFile, entry256, "SHA-256", CancellationToken.None);
            bool match384 = await ContentBroker.LocalFileMatchesManifestEntry(tempFile, entry384, "SHA-384", CancellationToken.None);
            bool match512 = await ContentBroker.LocalFileMatchesManifestEntry(tempFile, entry512, "SHA-512", CancellationToken.None);

            match256.Should().BeTrue();
            match384.Should().BeTrue();
            match512.Should().BeTrue();

            // Mismatched hash
            ManifestEntry mismatchEntry = new() { Size = content.Length, Hash = "0000000000000000000000000000000000000000" };
            bool mismatch = await ContentBroker.LocalFileMatchesManifestEntry(tempFile, mismatchEntry, "SHA-256", CancellationToken.None);
            mismatch.Should().BeFalse();

            // Insecure algorithms rejected
            Func<Task> actMd5 = async () => await ContentBroker.LocalFileMatchesManifestEntry(tempFile, entry256, "MD5", CancellationToken.None);
            await actMd5.Should().ThrowAsync<NotSupportedException>().WithMessage("*Insecure Manifest Hash Algorithm*");

            // Unknown algorithm rejected
            Func<Task> actUnknown = async () => await ContentBroker.LocalFileMatchesManifestEntry(tempFile, entry256, "BLAKE3", CancellationToken.None);
            await actUnknown.Should().ThrowAsync<NotSupportedException>().WithMessage("*Unsupported Manifest Hash Algorithm*");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ContentBroker_PlanConfirmation_Declined_DoesNotDownloadOrDeleteAnyFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_decline_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        string localFile = Path.Combine(tempDir, "local_untouched.txt");
        await File.WriteAllTextAsync(localFile, "Keep this safe");

        string partialFile = Path.Combine(tempDir, "sample.partial");
        await File.WriteAllTextAsync(partialFile, "Interrupted partial");

        try
        {
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
                    ["new_remote_asset.s2z"] = new() { Size = 1024, Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789" }
                }
            };

            bool planConfirmationInvoked = false;
            Func<SynchronisationPlan, Task<bool>> declinePrompt = plan =>
            {
                planConfirmationInvoked = true;
                plan.FilesToDownload.Should().Be(1);
                // User declines the update:
                return Task.FromResult(false);
            };

            SynchronisationSummary summary = await ContentBroker.Synchronise(
                manifest,
                "wac",
                tempDir,
                confirmPlan: declinePrompt);

            planConfirmationInvoked.Should().BeTrue();
            summary.FilesDownloaded.Should().Be(0);
            summary.FilesDeleted.Should().Be(0);
            summary.FilesFailed.Should().Be(0);

            // Verify local files and partial files were not touched
            File.Exists(localFile).Should().BeTrue();
            File.Exists(partialFile).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("api.kongor.net", true)]
    [InlineData("masterserver.kongor.net", true)]
    [InlineData("https://api.kongor.net:55555", true)]
    [InlineData("localhost:55555", false)]
    [InlineData("127.0.0.1:55555", false)]
    [InlineData("::1", false)]
    [InlineData("192.168.1.50:55555", false)]
    [InlineData("10.0.0.5:55555", false)]
    [InlineData("masterserver.newbs.io", false)]
    [InlineData("Custom Address ...", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MainViewModel_IsOfficialCdnMasterServer_ValidatesWhitelistedEndpoints(string? address, bool expected)
    {
        try
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = true;
            bool actual = MainViewModel.IsOfficialCdnMasterServer(address);
            actual.Should().Be(expected);
        }
        finally
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = false;
        }
    }
}
