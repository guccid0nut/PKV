// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

public sealed class DisklessModManagerTests
{
    [Fact]
    public async Task Diskless_ModDiscovery_DiscoversHonModPackagesAndUnpackedDirectories()
    {
        using InMemoryVirtualFileSystem vfs = new();

        // 1. Create a packaged .honmod in vfs
        ModManifest packagedManifest = new()
        {
            Name = "Astryon Custom Hero Mod",
            Version = "1.0.0",
            Author = "Kongor Team",
            Description = "Custom hero Astryon"
        };
        Dictionary<string, byte[]> packagedPayloads = new()
        {
            ["heroes/astryon/hero.entity"] = Encoding.UTF8.GetBytes("<hero name='Hero_Astryon' />")
        };
        await HonModArchiveWriter.WritePackageAsync(vfs, "client/mods/Astryon.honmod", packagedManifest,
            packagedPayloads);

        // 2. Create an unpacked mod directory in vfs
        ModManifest unpackedManifest = new()
        {
            Name = "UI Enhancement Mod",
            Version = "2.5.0",
            Author = "UI Modder",
            Description = "Enhances game HUD"
        };
        await vfs.WriteAllTextAsync("client/mods/ui_enhancement/mod.xml",
            "<mod name='UI Enhancement Mod' version='2.5.0' author='UI Modder' description='Enhances game HUD' />");
        await vfs.WriteAllTextAsync("client/mods/ui_enhancement/ui/custom_hud.package", "<hud />");

        // 3. Discover mods disklessly
        ModManager manager = new(vfs);
        IReadOnlyList<ModPackage> discovered = await manager.DiscoverModsAsync("client/mods");

        discovered.Should().HaveCount(2);
        discovered.Should().Contain(m => m.Manifest.Name == "Astryon Custom Hero Mod");
        discovered.Should().Contain(m => m.Manifest.Name == "UI Enhancement Mod");
    }

    [Fact]
    public async Task Diskless_LayeredVfs_ApplyAndRollback_OperatesEntirelyInMemory()
    {
        using InMemoryVirtualFileSystem vfs = new();

        // 1. Create baseline game resource archive in memory
        Dictionary<string, byte[]> baseGameFiles = new()
        {
            ["ui/game.interface"] =
                Encoding.UTF8.GetBytes("<interface><include file='/ui/original.package' /></interface>"),
            ["shared/core.lua"] = Encoding.UTF8.GetBytes("function InitCore() end")
        };
        await S2zArchiveWriter.WriteArchiveAsync(vfs, "client/game/resources0.s2z", baseGameFiles);

        // 2. Create mod package in memory
        ModManifest modManifest = new()
        {
            Name = "Astryon Void Sentinel Mod",
            Version = "1.0.0",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "heroes/astryon/hero.entity", SourcePath = "hero.entity" }
            ],
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/game.interface",
                    Operations =
                    [
                        new FindOperation("<interface>", FindPosition.End),
                        new InsertOperation("<include file='/heroes/astryon/astryon_hud.package' />",
                            Position: InsertPosition.After)
                    ]
                }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["hero.entity"] = Encoding.UTF8.GetBytes("<hero name='Hero_Astryon' />")
        };

        await HonModArchiveWriter.WritePackageAsync(vfs, "client/mods/Astryon.honmod", modManifest, payloads);

        // 3. Run ModManager disklessly
        ModManager manager = new(vfs);
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync("client/mods");
        mods.Should().HaveCount(1);
        mods[0].IsEnabled = true;

        // 4. Apply mods in VFS Mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = "client",
            ModsToApply = mods,
            Mode = InjectionMode.Vfs,
            Vfs = vfs
        });

        applyResult.Success.Should().BeTrue();
        vfs.FileExists("client/override/heroes/astryon/hero.entity").Should().BeTrue();
        vfs.FileExists("client/override/ui/game.interface").Should().BeTrue();
        vfs.FileExists("client/override/.hotreload.trigger").Should().BeTrue();
        vfs.FileExists("client/rollback_journal.json").Should().BeTrue();

        string patchedInterface = await vfs.ReadAllTextAsync("client/override/ui/game.interface");
        patchedInterface.Should().Contain("<include file='/heroes/astryon/astryon_hud.package' />");

        // 5. Rollback
        RollbackResult rollback = await manager.RollbackAsync("client");
        rollback.Success.Should().BeTrue();
        vfs.FileExists("client/override/heroes/astryon/hero.entity").Should().BeFalse();
        vfs.FileExists("client/override/ui/game.interface").Should().BeFalse();
        vfs.FileExists("client/rollback_journal.json").Should().BeFalse();
    }

    [Fact]
    public async Task Diskless_DirectArchive_ApplyAndRollback_OperatesEntirelyInMemory()
    {
        using InMemoryVirtualFileSystem vfs = new();

        // 1. Create baseline game resource archive in memory
        Dictionary<string, byte[]> baseGameFiles = new()
        {
            ["heroes/chronos/hero.entity"] = Encoding.UTF8.GetBytes("<hero name='Hero_Chronos' />")
        };
        await S2zArchiveWriter.WriteArchiveAsync(vfs, "client/game/resources0.s2z", baseGameFiles);

        // 2. Create mod package in memory
        ModManifest modManifest = new()
        {
            Name = "Astryon Direct Mod",
            Version = "1.0.0",
            FileCopies =
            [
                new CopyFileOperation { TargetName = "heroes/astryon/hero.entity", SourcePath = "hero.entity" }
            ]
        };

        Dictionary<string, byte[]> payloads = new()
        {
            ["hero.entity"] = Encoding.UTF8.GetBytes("<hero name='Hero_Astryon' />")
        };

        await HonModArchiveWriter.WritePackageAsync(vfs, "client/mods/AstryonDirect.honmod", modManifest, payloads);

        ModManager manager = new(vfs);
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync("client/mods");
        mods.Should().HaveCount(1);
        mods[0].IsEnabled = true;

        // 3. Apply in Archive mode
        ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = "client",
            ModsToApply = mods,
            Mode = InjectionMode.Archive,
            Vfs = vfs
        });

        applyResult.Success.Should().BeTrue();
        vfs.FileExists("client/game/resources999.s2z").Should().BeTrue();

        using (S2zArchiveReader s2zReader = await S2zArchiveReader.OpenAsync(vfs, "client/game/resources999.s2z"))
        {
            s2zReader.HasEntry("heroes/astryon/hero.entity").Should().BeTrue();
            string content = await s2zReader.ReadEntryTextAsync("heroes/astryon/hero.entity");
            content.Should().Be("<hero name='Hero_Astryon' />");
        }

        // 4. Rollback
        RollbackResult rollback = await manager.RollbackAsync("client");
        rollback.Success.Should().BeTrue();
        vfs.FileExists("client/game/resources999.s2z").Should().BeFalse();
    }
}
