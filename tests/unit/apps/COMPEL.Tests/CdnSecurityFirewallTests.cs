// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Services.Vfs;
using COMPEL.Services.ContentBroker;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class CdnSecurityFirewallTests : IDisposable
{
    private readonly string _tempDirectory;

    public CdnSecurityFirewallTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Kongor_CdnSecurityTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Best effort cleanup in test tear down
            }
        }
    }

    #region CDN ContentBroker Hash Validation Tests

    [Theory]
    [InlineData("MD5")]
    [InlineData("md5")]
    [InlineData("MD-5")]
    [InlineData("SHA1")]
    [InlineData("sha1")]
    [InlineData("SHA-1")]
    public void ValidateSecureHashAlgorithm_InsecureAlgorithms_ThrowsNotSupportedException(string algorithm)
    {
        Action act = () => ContentBroker.ValidateSecureHashAlgorithm(algorithm);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Insecure Manifest Hash Algorithm*");
    }

    [Theory]
    [InlineData("CRC32")]
    [InlineData("BLAKE2")]
    [InlineData("UNKNOWN_ALGO")]
    public void ValidateSecureHashAlgorithm_UnsupportedAlgorithms_ThrowsNotSupportedException(string algorithm)
    {
        Action act = () => ContentBroker.ValidateSecureHashAlgorithm(algorithm);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Unsupported Manifest Hash Algorithm*");
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("sha256")]
    [InlineData("SHA-256")]
    [InlineData("SHA384")]
    [InlineData("SHA-384")]
    [InlineData("SHA512")]
    [InlineData("SHA-512")]
    public void ValidateSecureHashAlgorithm_ApprovedAlgorithms_DoesNotThrow(string algorithm)
    {
        Action act = () => ContentBroker.ValidateSecureHashAlgorithm(algorithm);

        act.Should().NotThrow();
    }

    #endregion

    #region S2z Package Cryptographic Verification Tests

    [Fact]
    public async Task S2zSecurityValidator_ComputeAndVerifySha256_Succeeds()
    {
        string filePath = Path.Combine(_tempDirectory, "test_resource.s2z");
        byte[] content = Encoding.UTF8.GetBytes("Project Kongor S2Z Package Resource Content");
        await File.WriteAllBytesAsync(filePath, content);

        byte[] rawHash = SHA256.HashData(content);
        string expectedHash = Convert.ToHexStringLower(rawHash);

        string computedHash = await S2zPackageSecurityValidator.ComputeSha256HexAsync(filePath);
        computedHash.Should().Be(expectedHash);

        var (isValid, actualHash) = await S2zPackageSecurityValidator.VerifyPackageHashAsync(filePath, expectedHash);
        isValid.Should().BeTrue();
        actualHash.Should().Be(expectedHash);

        // Mismatched hash check
        var (isInvalid, _) = await S2zPackageSecurityValidator.VerifyPackageHashAsync(filePath, "0000000000000000000000000000000000000000000000000000000000000000");
        isInvalid.Should().BeFalse();
    }

    #endregion

    #region S2z Package Structural & Zip-Slip Protection Tests

    [Fact]
    public void VerifyArchiveStructure_ValidArchive_ReturnsSafe()
    {
        string archivePath = Path.Combine(_tempDirectory, "valid_package.s2z");

        using (FileStream fs = File.Create(archivePath))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry1 = archive.CreateEntry("game/game.dll");
            using (Stream s = entry1.Open())
            {
                s.Write(Encoding.UTF8.GetBytes("MZ_DUMMY_BINARY"));
            }

            ZipArchiveEntry entry2 = archive.CreateEntry("resources/textures/icon.tga");
            using (Stream s = entry2.Open())
            {
                s.Write(Encoding.UTF8.GetBytes("TGA_IMAGE_HEADER"));
            }
        }

        var (isSafe, reason) = S2zPackageSecurityValidator.VerifyArchiveStructure(archivePath);

        isSafe.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Theory]
    [InlineData("../../windows/system32/cmd.exe")]
    [InlineData("../parent_dir_escape.txt")]
    [InlineData("sub/../../traversal.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\win.ini")]
    [InlineData("C:/escaped_drive.txt")]
    public void VerifyArchiveStructure_ZipSlipPathTraversal_ReturnsUnsafe(string maliciousPath)
    {
        string archivePath = Path.Combine(_tempDirectory, "malicious_slip_" + Guid.NewGuid().ToString("N") + ".s2z");

        using (FileStream fs = File.Create(archivePath))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry(maliciousPath);
            using Stream s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes("MALICIOUS_PAYLOAD"));
        }

        var (isSafe, reason) = S2zPackageSecurityValidator.VerifyArchiveStructure(archivePath);

        isSafe.Should().BeFalse();
        reason.Should().Contain("Zip-slip path traversal detected");
    }

    [Fact]
    public void VerifyArchiveStructure_EntryPathTooLong_ReturnsUnsafe()
    {
        string archivePath = Path.Combine(_tempDirectory, "long_path.s2z");
        string longEntryPath = "nested/" + new string('a', 265) + ".txt";

        using (FileStream fs = File.Create(archivePath))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry(longEntryPath);
            using Stream s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes("PAYLOAD"));
        }

        var (isSafe, reason) = S2zPackageSecurityValidator.VerifyArchiveStructure(archivePath);

        isSafe.Should().BeFalse();
        reason.Should().Contain("exceeds maximum allowed");
    }

    [Fact]
    public void VerifyArchiveStructure_NonExistentFile_ReturnsUnsafe()
    {
        string nonExistent = Path.Combine(_tempDirectory, "does_not_exist.s2z");

        var (isSafe, reason) = S2zPackageSecurityValidator.VerifyArchiveStructure(nonExistent);

        isSafe.Should().BeFalse();
        reason.Should().Contain("does not exist");
    }

    #endregion
}
