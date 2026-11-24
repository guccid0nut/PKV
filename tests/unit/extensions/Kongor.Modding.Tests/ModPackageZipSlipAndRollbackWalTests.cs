// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ModPackageZipSlipAndRollbackWalTests.cs
// System: Kongor.Modding.Tests - Wave 4A Zip-Slip & WAL Rollback Hardening
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Compression;
using System.Security;
using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

public sealed class ModPackageZipSlipAndRollbackWalTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _clientDir;
    private readonly string _modsDir;

    public ModPackageZipSlipAndRollbackWalTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "KongorWave4AModding_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_rootDir, "client");
        _modsDir = Path.Combine(_rootDir, "mods");

        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
        Directory.CreateDirectory(Path.Combine(_clientDir, "override"));
        Directory.CreateDirectory(_modsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            try
            {
                Directory.Delete(_rootDir, true);
            }
            catch
            {
                // Ignore temp cleanup contention
            }
        }
    }

    [Theory]
    [InlineData("../evil.lua")]
    [InlineData("ui/../../evil.lua")]
    [InlineData("..\\..\\evil.lua")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("ui/icon.tga:Zone.Identifier")]
    [InlineData("\\\\attacker\\share\\payload.lua")]
    [InlineData("//attacker/share/payload.lua")]
    [InlineData("ui/bad\0name.lua")]
    public void NormalizeZipPath_MaliciousPaths_ThrowsSecurityException(string maliciousPath)
    {
        Action act = () => ModPackage.NormalizeZipPath(maliciousPath);
        act.Should().Throw<SecurityException>();
    }

    [Fact]
    public async Task GetPayloadFileAsync_DirectoryAndSymlinkZipSource_RejectsEscapeAndSymlinkEntries()
    {
        string outsideSecretPath = Path.Combine(_modsDir, "outside_secret.txt");
        await File.WriteAllTextAsync(outsideSecretPath, "TOP_SECRET_TOKEN");

        string modSourceDir = Path.Combine(_modsDir, "DirMod");
        Directory.CreateDirectory(modSourceDir);

        ModManifest manifest = new ModManifest
        {
            Name = "DirMod",
            Version = "1.0.0"
        };

        using ModPackage dirPkg = new ModPackage
        {
            FilePath = modSourceDir,
            Manifest = manifest,
            IsDirectorySource = true
        };

        Func<Task> dirAct = async () => await dirPkg.GetPayloadFileAsync("../outside_secret.txt");
        await dirAct.Should().ThrowAsync<SecurityException>();

        string zipModPath = Path.Combine(_modsDir, "SymlinkMod.honmod");
        await using (FileStream fs = new FileStream(zipModPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            ZipArchiveEntry symlinkEntry = zip.CreateEntry("ui/symlink_target.lua");
            // POSIX S_IFLNK (0xA000) in upper 16 bits of ExternalAttributes
            symlinkEntry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            await using Stream entryStream = symlinkEntry.Open();
            byte[] targetBytes = Encoding.UTF8.GetBytes("/etc/passwd");
            await entryStream.WriteAsync(targetBytes);
        }

        using ModPackage zipPkg = new ModPackage
        {
            FilePath = zipModPath,
            Manifest = manifest,
            IsDirectorySource = false
        };

        Func<Task> symlinkAct = async () => await zipPkg.GetPayloadFileAsync("ui/symlink_target.lua");
        await symlinkAct.Should().ThrowAsync<SecurityException>();
    }

    [Fact]
    public async Task InjectorsAndModManager_ZipSlipPayloads_BlockArbitraryWriteOutsideTargetRoots()
    {
        string escapedVfsTarget = Path.Combine(_clientDir, "escaped_from_vfs.lua");
        Dictionary<string, byte[]> maliciousVfsFiles = new Dictionary<string, byte[]>
        {
            ["../escaped_from_vfs.lua"] = Encoding.UTF8.GetBytes("-- pwned vfs")
        };

        Func<Task> vfsAct = async () => await LayeredVfsInjector.InjectAsync(_clientDir, maliciousVfsFiles, []);
        await vfsAct.Should().ThrowAsync<SecurityException>();
        File.Exists(escapedVfsTarget).Should().BeFalse();

        string escapedArchiveTarget = Path.Combine(_rootDir, "escaped_from_archive.lua");
        Dictionary<string, byte[]> maliciousArchiveFiles = new Dictionary<string, byte[]>
        {
            ["../../escaped_from_archive.lua"] = Encoding.UTF8.GetBytes("-- pwned archive")
        };

        Func<Task> archiveAct = async () => await DirectArchiveInjector.InjectAsync(_clientDir, maliciousArchiveFiles, []);
        await archiveAct.Should().ThrowAsync<SecurityException>();
        File.Exists(escapedArchiveTarget).Should().BeFalse();

        // Also verify ModManager.ApplyModsAsync fails closed on traversal <copyfile> even when StrictIntegrity = false
        string craftedHonmodPath = Path.Combine(_modsDir, "ZipSlipCrafted.honmod");
        await using (FileStream fs = new FileStream(craftedHonmodPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            ZipArchiveEntry xmlEntry = zip.CreateEntry("mod.xml");
            await using (Stream xs = xmlEntry.Open())
            {
                string xml = "<modification application=\"Heroes of Newerth\" name=\"ZipSlipCrafted\" version=\"1.0\"><copyfile name=\"../escaped_via_modmanager.lua\" source=\"payload.lua\" /></modification>";
                await xs.WriteAsync(Encoding.UTF8.GetBytes(xml));
            }

            ZipArchiveEntry payloadEntry = zip.CreateEntry("payload.lua");
            await using (Stream ps = payloadEntry.Open())
            {
                await ps.WriteAsync(Encoding.UTF8.GetBytes("-- escaped via modmanager"));
            }
        }

        ModManager manager = new ModManager();
        ModManifest parsedManifest = new ModManifest
        {
            Name = "ZipSlipCrafted",
            Version = "1.0",
            FileCopies =
            [
                new CopyFileOperation
                {
                    TargetName = "../escaped_via_modmanager.lua",
                    SourcePath = "payload.lua"
                }
            ]
        };

        using ModPackage craftedMod = new ModPackage
        {
            FilePath = craftedHonmodPath,
            Manifest = parsedManifest,
            IsEnabled = true
        };

        Func<Task> applyAct = async () => await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [craftedMod],
            Mode = InjectionMode.Vfs,
            StrictIntegrity = false
        });

        await applyAct.Should().ThrowAsync<SecurityException>();
        File.Exists(Path.Combine(_clientDir, "escaped_via_modmanager.lua")).Should().BeFalse();
    }

    [Fact]
    public async Task DirectArchiveInjector_MidWriteIoException_RollsBackPartialMutationsBeforeVfsFallback()
    {
        // Pre-seed existing resources999.s2z and two shadowing loose files in KONGOR/extensions/ui/
        string archivePath = Path.Combine(_clientDir, "game", "resources999.s2z");
        byte[] originalArchiveBytes = Encoding.UTF8.GetBytes("ORIGINAL_CLEAN_ARCHIVE_SENTINEL");
        await File.WriteAllBytesAsync(archivePath, originalArchiveBytes);

        string extUiDir = Path.Combine(_clientDir, "KONGOR", "extensions", "ui");
        Directory.CreateDirectory(extUiDir);
        string firstLoosePath = Path.Combine(extUiDir, "alpha.lua");
        string secondLoosePath = Path.Combine(extUiDir, "beta.lua");
        await File.WriteAllTextAsync(firstLoosePath, "ORIGINAL_ALPHA_LUA");
        await File.WriteAllTextAsync(secondLoosePath, "ORIGINAL_BETA_LUA");

        Dictionary<string, byte[]> preparedFiles = new Dictionary<string, byte[]>
        {
            ["ui/alpha.lua"] = Encoding.UTF8.GetBytes("MODDED_ALPHA_LUA"),
            ["ui/beta.lua"] = Encoding.UTF8.GetBytes("MODDED_BETA_LUA")
        };

        ModPackage mod = new ModPackage
        {
            FilePath = Path.Combine(_modsDir, "AtomicTest.honmod"),
            Manifest = new ModManifest { Name = "AtomicTest", Version = "1.0.0" },
            Sha256Hash = "deadbeef"
        };

        // Hold a read-only lock on secondLoosePath so File.Copy succeeds for backup,
        // DirectArchiveInjector mutates archivePath + firstLoosePath, and then throws IOException writing secondLoosePath!
        JournalEntry resultJournal;
        await using (FileStream lockStream = new FileStream(secondLoosePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            resultJournal = await DirectArchiveInjector.InjectAsync(_clientDir, preparedFiles, [mod]);
        }

        // Fallback to LayeredVfsInjector must succeed AND partial mutations to archivePath + firstLoosePath must have been rolled back!
        resultJournal.Mode.Should().Be(InjectionMode.Vfs);
        byte[] postArchiveBytes = await File.ReadAllBytesAsync(archivePath);
        postArchiveBytes.Should().Equal(originalArchiveBytes);
        (await File.ReadAllTextAsync(firstLoosePath)).Should().Be("ORIGINAL_ALPHA_LUA");
        (await File.ReadAllTextAsync(secondLoosePath)).Should().Be("ORIGINAL_BETA_LUA");
        (await File.ReadAllTextAsync(Path.Combine(_clientDir, "override", "ui", "alpha.lua"))).Should().Be("MODDED_ALPHA_LUA");
    }

    [Fact]
    public async Task RollbackJournal_WhenOverwrittenTargetLocked_PreservesBackupsAndJournalUntilRetrySucceeds()
    {
        string goldenOverrideHud = Path.Combine(_clientDir, "override", "ui", "hud.interface");
        Directory.CreateDirectory(Path.GetDirectoryName(goldenOverrideHud)!);
        await File.WriteAllTextAsync(goldenOverrideHud, "<interface name='golden_hud' />");

        Dictionary<string, byte[]> modPayload = new Dictionary<string, byte[]>
        {
            ["ui/hud.interface"] = Encoding.UTF8.GetBytes("<interface name='modded_hud' />")
        };

        ModPackage mod = new ModPackage
        {
            FilePath = Path.Combine(_modsDir, "HudMod.honmod"),
            Manifest = new ModManifest { Name = "HudMod", Version = "1.0.0" }
        };

        await LayeredVfsInjector.InjectAsync(_clientDir, modPayload, [mod]);
        (await File.ReadAllTextAsync(goldenOverrideHud)).Should().Be("<interface name='modded_hud' />");

        string backupDir = Path.Combine(_clientDir, ".mod_backups");
        string journalPath = RollbackJournal.GetJournalPath(_clientDir);

        // Lock target file exclusively so restore fails during first rollback attempt
        bool firstRollbackSucceeded;
        await using (FileStream targetLock = new FileStream(goldenOverrideHud, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            firstRollbackSucceeded = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        }

        firstRollbackSucceeded.Should().BeFalse("locked target could not be restored yet");
        Directory.Exists(backupDir).Should().BeTrue("backups MUST be preserved when restore fails on a locked file");
        File.Exists(journalPath).Should().BeTrue("residual WAL journal MUST remain on disk so retry can finish restoring");

        // Retry after lock is released: must succeed and restore golden override file cleanly
        bool secondRollbackSucceeded = await RollbackJournal.ExecuteRollbackAsync(_clientDir);
        secondRollbackSucceeded.Should().BeTrue();
        (await File.ReadAllTextAsync(goldenOverrideHud)).Should().Be("<interface name='golden_hud' />");
        File.Exists(journalPath).Should().BeFalse();
    }
}
