// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

public class VfsAndInjectionTests : IDisposable
{
    private readonly string _clientDir;
    private readonly string _modsDir;

    public VfsAndInjectionTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "KongorVfsTests_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(root, "client");
        _modsDir = Path.Combine(root, "mods");

        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
        Directory.CreateDirectory(_modsDir);
    }

    public void Dispose()
    {
        string? root = Path.GetDirectoryName(_clientDir);
        if (root != null && Directory.Exists(root))
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                /* ignore */
            }
    }

    [Fact]
    public async Task LayeredVfs_ApplyAndRollback_RestoresCleanClientState()
    {
        string modPath = Path.Combine(_modsDir, "TestUI.honmod");
        ModManifest manifest = new()
        {
            Name = "TestUI Mod",
            Version = "1.0.0",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "ui/custom.interface", SourcePath = "custom.interface" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["custom.interface"] = Encoding.UTF8.GetBytes("<interface name='custom' />")
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, payloads);

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(_modsDir);
        mods.Should().HaveCount(1);
        mods[0].IsEnabled = true;

        // 1. Apply in VFS mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = mods,
            Mode = InjectionMode.Vfs
        });

        applyResult.Success.Should().BeTrue();
        string overrideFile = Path.Combine(_clientDir, "override", "ui", "custom.interface");
        File.Exists(overrideFile).Should().BeTrue();
        string triggerFile = Path.Combine(_clientDir, "override", ".hotreload.trigger");
        File.Exists(triggerFile).Should().BeTrue();
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeTrue();

        // 2. Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();
        File.Exists(overrideFile).Should().BeFalse();
        File.Exists(RollbackJournal.GetJournalPath(_clientDir)).Should().BeFalse();
    }

    [Fact]
    public async Task DirectArchive_ApplyAndRollback_CreatesAndCleansArchive()
    {
        string modPath = Path.Combine(_modsDir, "TestArchiveMod.honmod");
        ModManifest manifest = new()
        {
            Name = "TestArchive Mod",
            Version = "1.0.0",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "heroes/custom/hero.entity", SourcePath = "hero.entity" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["hero.entity"] = Encoding.UTF8.GetBytes("<hero name='CustomHero' />")
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, payloads);

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(_modsDir);
        mods[0].IsEnabled = true;

        // 1. Apply in Archive mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = mods,
            Mode = InjectionMode.Archive
        });

        applyResult.Success.Should().BeTrue();
        string s2zFile = Path.Combine(_clientDir, "game", "resources999.s2z");
        File.Exists(s2zFile).Should().BeTrue();

        using (S2zArchiveReader s2zReader = S2zArchiveReader.Open(s2zFile))
        {
            s2zReader.HasEntry("heroes/custom/hero.entity").Should().BeTrue();
        }

        // 2. Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();
        File.Exists(s2zFile).Should().BeFalse();
    }

    [Fact]
    public async Task LayeredVfs_ShadowedLooseFileInGameDirectory_IsBackedUpAndRestoredOnRollback()
    {
        // 1. Create a pre-existing loose file in game/ (which would otherwise shadow override/)
        string inGamePath = Path.Combine(_clientDir, "game", "ui", "mod_options_elements.package");
        Directory.CreateDirectory(Path.GetDirectoryName(inGamePath)!);
        await File.WriteAllTextAsync(inGamePath, "ORIGINAL_UNPATCHED_STUB", Encoding.UTF8);

        // 2. Prepare mod package with copyfile targeting ui/mod_options_elements.package
        string modPath = Path.Combine(_modsDir, "OptionsMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Options Mod",
            Version = "1.0.0",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "ui/mod_options_elements.package", SourcePath = "mod_options_elements.package" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["mod_options_elements.package"] = Encoding.UTF8.GetBytes("<package><instance name='test_opt' /></package>")
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, payloads);

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(_modsDir);
        mods.Should().HaveCount(1);
        mods[0].IsEnabled = true;

        // 3. Apply in VFS mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = mods,
            Mode = InjectionMode.Vfs
        });

        applyResult.Success.Should().BeTrue();

        // Verify override/ contains the patched file
        string overrideFile = Path.Combine(_clientDir, "override", "ui", "mod_options_elements.package");
        File.Exists(overrideFile).Should().BeTrue();
        (await File.ReadAllTextAsync(overrideFile)).Should().Contain("test_opt");

        // Verify loose file in game/ was patched so K2 engine reads the modded content
        (await File.ReadAllTextAsync(inGamePath)).Should().Contain("test_opt");

        // Verify backup was created in journal
        applyResult.Journal.Should().NotBeNull();
        applyResult.Journal!.OverwrittenFiles.Should().ContainSingle();
        applyResult.Journal.OverwrittenFiles[0].RelativePath.Should().Be("game/ui/mod_options_elements.package");

        // 4. Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();

        // Verify loose file in game/ is restored to pristine unpatched state
        (await File.ReadAllTextAsync(inGamePath)).Should().Be("ORIGINAL_UNPATCHED_STUB");

        // Verify override file is removed
        File.Exists(overrideFile).Should().BeFalse();
    }

    [Fact]
    public async Task DirectArchive_ShadowedLooseFile_IsBackedUpAndRestoredOnRollback()
    {
        // 1. Create a pre-existing loose game_apex.interface in game/ui/ and mod_options.package
        string inGameInterface = Path.Combine(_clientDir, "game", "ui", "game_apex.interface");
        string inGameModOpts = Path.Combine(_clientDir, "game", "ui", "mod_options.package");
        Directory.CreateDirectory(Path.GetDirectoryName(inGameInterface)!);
        await File.WriteAllTextAsync(inGameInterface, "<interface><panel name=\"main\" /></interface>", Encoding.UTF8);
        await File.WriteAllTextAsync(inGameModOpts, "<package><include file=\"/ui/mod_options_elements.package\" /></package>", Encoding.UTF8);

        // 2. Prepare a mod that edits game_apex.interface and mod_options_elements.package
        string modPath = Path.Combine(_modsDir, "HeartbeatMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Heartbeat Mod",
            Version = "1.5",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game_apex.interface",
                    Operations =
                    [
                        new FindOperation("</interface>"),
                        new InsertOperation("<include file=\"heartbeat.package\" /><instance name=\"hb_main\" />", Position: InsertPosition.Before)
                    ]
                }
            ],
            FileCopies =
            [
                new CopyFileOperation { TargetName = "ui/heartbeat.package", SourcePath = "heartbeat.package" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["heartbeat.package"] = Encoding.UTF8.GetBytes("<package><panel name=\"hb_border\" /></package>")
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, payloads);

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(_modsDir);
        mods.Should().HaveCount(1);
        mods[0].IsEnabled = true;

        // 3. Apply in Archive mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = mods,
            Mode = InjectionMode.Archive
        });

        applyResult.Success.Should().BeTrue();

        // Verify resources999.s2z was created and contains heartbeat.package
        string s2zFile = Path.Combine(_clientDir, "game", "resources999.s2z");
        File.Exists(s2zFile).Should().BeTrue();

        // Verify loose file in game/ui/game_apex.interface was updated so loose priority doesn't break the mod
        string moddedInterfaceContent = await File.ReadAllTextAsync(inGameInterface);
        moddedInterfaceContent.Should().Contain("heartbeat.package");

        // Verify overwritten files tracking in journal
        applyResult.Journal.Should().NotBeNull();
        applyResult.Journal!.OverwrittenFiles.Should().Contain(b => b.RelativePath.Contains("game_apex.interface"));

        // 4. Rollback
        RollbackResult rollbackResult = await manager.RollbackAsync(_clientDir);
        rollbackResult.Success.Should().BeTrue();

        // Verify resources999.s2z is deleted
        File.Exists(s2zFile).Should().BeFalse();

        // Verify loose file is restored to original clean state
        string restoredInterfaceContent = await File.ReadAllTextAsync(inGameInterface);
        restoredInterfaceContent.Should().Be("<interface><panel name=\"main\" /></interface>");
        restoredInterfaceContent.Should().NotContain("heartbeat.package");
    }
}
