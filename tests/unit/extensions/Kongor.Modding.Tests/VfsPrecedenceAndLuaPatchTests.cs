// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Patching;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Value-weighted regression tests guarding K2 VFS mount-tier precedence (ClientModMountSpec),
///     pre-existing override/ preservation on rollback, numeric resources*.s2z ordering, missing
///     editfile target safety, and Lua '&lt;' / UTF-8 BOM patching integrity validated via luac51.
/// </summary>
public sealed class VfsPrecedenceAndLuaPatchTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _clientDir;
    private readonly string _modsDir;

    public VfsPrecedenceAndLuaPatchTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "KongorVfsPrecedence_" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(_rootDir, "client");
        _modsDir = Path.Combine(_rootDir, "mods");

        Directory.CreateDirectory(Path.Combine(_clientDir, "game"));
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
                // Ignore transient Windows file handle cleanup race in temp dir
            }
        }
    }

    [Fact]
    public async Task ApplyMods_ArchiveMode_WhenFileExistsInOverrideAndS2z_PatchesOverrideVersionAndRestoresOnRollback()
    {
        // protects: Project Kongor override/ modernizations (e.g. main_v2.lua) against stale .s2z clobbering
        // fails_when: ModManager resolves <editfile> from game/resources0.s2z instead of override/
        // seam: Real S2zArchiveWriter + ModManager + DirectArchiveInjector + luac51 + RollbackJournal

        const string staleS2zLua =
            "function MainV2_Init()\n    local version = 'stale_4_10_s2z'\n    return version\nend\n";
        const string modernizedOverrideLua =
            "function MainV2_Init()\n    local version = 'kongor_modernized_override'\n    local ready = true\n    return version\nend\n";

        await S2zArchiveWriter.WriteArchiveAsync(
            Path.Combine(_clientDir, "game", "resources0.s2z"),
            new Dictionary<string, byte[]>
            {
                ["ui/scripts/main_v2.lua"] = Encoding.UTF8.GetBytes(staleS2zLua)
            });

        string overrideLuaPath = Path.Combine(_clientDir, "override", "ui", "scripts", "main_v2.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(overrideLuaPath)!);
        await File.WriteAllTextAsync(overrideLuaPath, modernizedOverrideLua, new UTF8Encoding(false));

        string modPath = Path.Combine(_modsDir, "MainV2Hook.honmod");
        ModManifest manifest = new()
        {
            Name = "MainV2 Hook Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/scripts/main_v2.lua",
                    Operations =
                    [
                        new FindOperation("return version"),
                        new InsertOperation("    local modded = 'hook_active'\n", Position: InsertPosition.Before)
                    ]
                }
            ]
        };
        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, new Dictionary<string, byte[]>());

        using ModPackage mod = await HonModArchiveReader.ReadPackageAsync(modPath);
        mod.IsEnabled = true;

        List<string> diagLogs = [];
        ModManager manager = new();

        // 1. Apply in Archive mode (and re-apply to prove idempotency without double-patching)
        for (int pass = 0; pass < 2; pass++)
        {
            ApplyResult apply = await manager.ApplyModsAsync(new ApplyOptions
            {
                GameClientDirectory = _clientDir,
                ModsToApply = [mod],
                Mode = InjectionMode.Archive,
                StrictIntegrity = true,
                DiagnosticLogger = msg => diagLogs.Add(msg)
            });

            apply.Success.Should().BeTrue(string.Join("; ", apply.Errors));
            apply.Errors.Should().BeEmpty();

            string activeLoose = await File.ReadAllTextAsync(overrideLuaPath);
            activeLoose.Should().Contain("kongor_modernized_override",
                "applying a .honmod must patch the modernized override/ file, never revert to stale resources0.s2z");
            activeLoose.Should().NotContain("stale_4_10_s2z");
            activeLoose.Split("hook_active").Length.Should().Be(2, "hook must appear exactly once even across re-apply");

            using S2zArchiveReader res999 = S2zArchiveReader.Open(Path.Combine(_clientDir, "game", "resources999.s2z"));
            string archiveText = await res999.ReadEntryTextAsync("ui/scripts/main_v2.lua");
            archiveText.Should().Contain("kongor_modernized_override");
            archiveText.Should().Contain("hook_active");
        }

        diagLogs.Should().Contain(l => l.Contains("loose:override"));

        // 2. Rollback must restore the exact un-modded override/ file
        RollbackResult rollback = await manager.RollbackAsync(_clientDir);
        rollback.Success.Should().BeTrue();
        File.Exists(overrideLuaPath).Should().BeTrue();
        (await File.ReadAllTextAsync(overrideLuaPath)).Should().Be(modernizedOverrideLua);
    }

    [Fact]
    public async Task ApplyMods_VfsMode_WhenFilePreExistsInOverride_BacksUpAndRestoresPreExistingOverrideOnRollback()
    {
        // protects: Pre-existing override/ files against deletion during LayeredVfsInjector rollback
        // fails_when: LayeredVfsInjector records pre-existing override/ targets in CreatedFiles instead of OverwrittenFiles
        // seam: Real ModManager (InjectionMode.Vfs) + LayeredVfsInjector + RollbackJournal on both Disk and InMemoryVfs

        const string originalOverrideXml = "<package name=\"store2_kongor_modern\"><panel name=\"vault_btn\" /></package>";

        // --- Disk seam ---
        string overridePkgPath = Path.Combine(_clientDir, "override", "ui", "store2.package");
        Directory.CreateDirectory(Path.GetDirectoryName(overridePkgPath)!);
        await File.WriteAllTextAsync(overridePkgPath, originalOverrideXml, new UTF8Encoding(false));

        string modPath = Path.Combine(_modsDir, "StoreBadgeMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Store Badge Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/store2.package",
                    Operations =
                    [
                        new FindOperation("</package>"),
                        new InsertOperation("<panel name=\"mod_badge\" />", Position: InsertPosition.Before)
                    ]
                }
            ]
        };
        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, new Dictionary<string, byte[]>());
        using ModPackage mod = await HonModArchiveReader.ReadPackageAsync(modPath);
        mod.IsEnabled = true;

        ModManager diskManager = new();
        ApplyResult diskApply = await diskManager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [mod],
            Mode = InjectionMode.Vfs,
            StrictIntegrity = true
        });

        diskApply.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(overridePkgPath)).Should().Contain("vault_btn").And.Contain("mod_badge");

        RollbackResult diskRollback = await diskManager.RollbackAsync(_clientDir);
        diskRollback.Success.Should().BeTrue();
        File.Exists(overridePkgPath).Should().BeTrue("pre-existing override/ file must NOT be deleted on rollback");
        (await File.ReadAllTextAsync(overridePkgPath)).Should().Be(originalOverrideXml);

        // --- InMemoryVirtualFileSystem seam ---
        using InMemoryVirtualFileSystem vfs = new();
        await vfs.WriteAllTextAsync("client/override/ui/store2.package", originalOverrideXml);
        await HonModArchiveWriter.WritePackageAsync(vfs, "client/mods/StoreBadgeMod.honmod", manifest,
            new Dictionary<string, byte[]>());

        ModManager vfsManager = new(vfs);
        IReadOnlyList<ModPackage> vfsMods = await vfsManager.DiscoverModsAsync("client/mods");
        vfsMods[0].IsEnabled = true;

        ApplyResult vfsApply = await vfsManager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = "client",
            ModsToApply = vfsMods,
            Mode = InjectionMode.Vfs,
            StrictIntegrity = true,
            Vfs = vfs
        });
        vfsApply.Success.Should().BeTrue();
        (await vfs.ReadAllTextAsync("client/override/ui/store2.package"))
            .Should().Contain("vault_btn").And.Contain("mod_badge");

        RollbackResult vfsRollback = await vfsManager.RollbackAsync("client");
        vfsRollback.Success.Should().BeTrue();
        vfs.FileExists("client/override/ui/store2.package").Should().BeTrue();
        (await vfs.ReadAllTextAsync("client/override/ui/store2.package")).Should().Be(originalOverrideXml);
    }

    [Fact]
    public async Task ApplyMods_ArchiveMode_WhenLuaExistsOnlyInClientPackageFilesOrShared_PatchesAndRestoresCleanly()
    {
        // protects: ClientPackageFiles/-only NewUI scripts and ../shared/ scripts during Archive injection
        // fails_when: ModManager omits ClientPackageFiles/ or fails '..' normalization in InMemoryVirtualFileSystem
        // seam: Real ModManager + DirectArchiveInjector + InMemoryVirtualFileSystem + luac51 + RollbackJournal

        const string storeLua = "function Store2_Open()\n    local tab = 1\n    return tab\nend\n";
        const string sharedLua = "function Shared_Helper()\n    return 42\nend\n";

        using InMemoryVirtualFileSystem vfs = new();
        await vfs.WriteAllTextAsync("games/client/ClientPackageFiles/ui/scripts/newui/store2.lua", storeLua);
        await vfs.WriteAllTextAsync("games/shared/scripts/shared_helper.lua", sharedLua);

        ModManifest manifest = new()
        {
            Name = "NewUI & Shared Patch Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/scripts/newui/store2.lua",
                    Operations =
                    [
                        new FindOperation("return tab"),
                        new InsertOperation("    tab = tab + 1\n", Position: InsertPosition.Before)
                    ]
                },
                new EditFileOperation
                {
                    TargetName = "scripts/shared_helper.lua",
                    Operations =
                    [
                        new FindOperation("return 42"),
                        new ReplaceOperation("return 99")
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(
            vfs,
            "games/client/mods/NewUiPatch.honmod",
            manifest,
            new Dictionary<string, byte[]>());

        ModManager manager = new(vfs);
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync("games/client/mods");
        mods[0].IsEnabled = true;

        ApplyResult apply = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = "games/client",
            ModsToApply = mods,
            Mode = InjectionMode.Archive,
            StrictIntegrity = true,
            Vfs = vfs
        });

        apply.Success.Should().BeTrue(string.Join("; ", apply.Errors));
        (await vfs.ReadAllTextAsync("games/client/ClientPackageFiles/ui/scripts/newui/store2.lua"))
            .Should().Contain("local tab = 1").And.Contain("tab = tab + 1");
        (await vfs.ReadAllTextAsync("games/shared/scripts/shared_helper.lua"))
            .Should().Contain("return 99");

        RollbackResult rollback = await manager.RollbackAsync("games/client");
        rollback.Success.Should().BeTrue();
        (await vfs.ReadAllTextAsync("games/client/ClientPackageFiles/ui/scripts/newui/store2.lua"))
            .Should().Be(storeLua);
        (await vfs.ReadAllTextAsync("games/shared/scripts/shared_helper.lua"))
            .Should().Be(sharedLua);
    }

    [Fact]
    public async Task ApplyMods_WhenEditFileTargetDoesNotExistAnywhere_EmitsActionableWarningAndDoesNotCreateZeroByteFile()
    {
        // protects: Loose game/ directories and resources999.s2z against 0-byte file poisoning on missing targets
        // fails_when: ModManager falls back to string.Empty when baseContent is null
        // seam: Real ModManager.ApplyModsAsync + DirectArchiveInjector

        string modPath = Path.Combine(_modsDir, "MissingTargetMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Missing Target Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/scripts/removed_legacy_widget.lua",
                    Operations =
                    [
                        new FindOperation("function LegacyWidget()"),
                        new InsertOperation("\n    print('patched')\n", Position: InsertPosition.After)
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, new Dictionary<string, byte[]>());
        using ModPackage mod = await HonModArchiveReader.ReadPackageAsync(modPath);
        mod.IsEnabled = true;

        ModManager manager = new();
        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [mod],
            Mode = InjectionMode.Archive,
            StrictIntegrity = true
        });

        result.Success.Should().BeTrue();
        result.ModifiedFiles.Should().NotContain("ui/scripts/removed_legacy_widget.lua");
        result.Warnings.Should().Contain(w =>
            w.Contains("skipped editfile") && w.Contains("removed_legacy_widget.lua"));

        string bogusLooseFile = Path.Combine(_clientDir, "game", "ui", "scripts", "removed_legacy_widget.lua");
        File.Exists(bogusLooseFile).Should().BeFalse("missing editfile target must never write a 0-byte loose file");
    }

    [Fact]
    public async Task FuzzyStringPatcher_OnLuaWithLessThanOperatorAndUtf8Bom_DoesNotOverExpandAndPassesLuac51()
    {
        // protects: .lua <editfile> patches using '<' comparison operators or UTF-8 BOMs against syntax corruption
        // fails_when: ApplyFind/ApplyFindUp expands '< maxHp' to the next '>' on a later line, or leaves '\uFEFF' mid-file
        // seam: Real ModManager.ApplyModsAsync (StrictIntegrity=true) + FuzzyStringPatcher + luac51 bytecode compiler

        string luaWithBomAndComparisons =
            "\uFEFFfunction CheckLowHealth(hp, maxHp, shield)\n" +
            "    if\n" +
            "        < maxHp\n" + // Contrived token or real "< maxHp" find target followed by '>' on later line
            "    then end\n" +
            "end\n";

        // Even more realistic HoN Lua pattern: finding a line starting with '<' inside string/comment or comparison
        // followed later in the file by `if shield > 0 then`!
        const string realisticLuaBody =
            "function EvaluatePulse(hp, maxHp, shield)\n" +
            "    local ratio = hp / maxHp\n" +
            "    local isDanger = ratio\n" +
            "        < 0.25\n" +
            "    if shield > 0 then\n" +
            "        isDanger = false\n" +
            "    end\n" +
            "    return isDanger\n" +
            "end\n";

        byte[] bomPrefixedBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(realisticLuaBody)];
        await S2zArchiveWriter.WriteArchiveAsync(
            Path.Combine(_clientDir, "game", "resources0.s2z"),
            new Dictionary<string, byte[]>
            {
                ["ui/scripts/pulse_check.lua"] = bomPrefixedBytes
            });

        string modPath = Path.Combine(_modsDir, "LuaBomAndComparisonMod.honmod");
        ModManifest manifest = new()
        {
            Name = "Lua BOM & Comparison Mod",
            Version = "1.0.0",
            FileEdits =
            [
                new EditFileOperation
                {
                    TargetName = "ui/scripts/pulse_check.lua",
                    Operations =
                    [
                        // 1. Insert header at Position.Head on a BOM-prefixed Lua file (previously pushed \uFEFF mid-file!)
                        new FindOperation(string.Empty, FindPosition.Head),
                        new InsertOperation("local PULSE_THRESHOLD = 0.30\n", Position: InsertPosition.Before),
                        // 2. Find target starting with '<' followed later by '>' in 'if shield > 0'
                        new FindOperation("        < 0.25"),
                        new ReplaceOperation("        < PULSE_THRESHOLD")
                    ]
                }
            ]
        };

        await HonModArchiveWriter.WritePackageAsync(modPath, manifest, new Dictionary<string, byte[]>());
        using ModPackage mod = await HonModArchiveReader.ReadPackageAsync(modPath);
        mod.IsEnabled = true;

        ModManager manager = new();
        ApplyResult result = await manager.ApplyModsAsync(new ApplyOptions
        {
            GameClientDirectory = _clientDir,
            ModsToApply = [mod],
            Mode = InjectionMode.Vfs,
            StrictIntegrity = true
        });

        _ = luaWithBomAndComparisons;
        result.Success.Should().BeTrue(
            $"Lua patch must pass luac51 validation with 0 syntax errors. Errors: {string.Join("; ", result.Errors)}");
        result.Errors.Should().BeEmpty();

        string patchedLua = await File.ReadAllTextAsync(
            Path.Combine(_clientDir, "override", "ui", "scripts", "pulse_check.lua"));
        patchedLua.Should().NotContain("\uFEFF");
        patchedLua.Should().Contain("if shield > 0 then",
            "Lua '<' find target must never expand across lines to swallow 'if shield >'");
        patchedLua.Should().Contain("< PULSE_THRESHOLD");
    }

    [Fact]
    public async Task GameResourceResolver_CreateForClient_OrdersResources10AboveResources2Numerically()
    {
        // protects: Numeric archive precedence (resources10.s2z > resources2.s2z > resources0.s2z)
        // fails_when: Lexicographical string sort orders 'resources2.s2z' ahead of 'resources10.s2z'
        // seam: Real GameResourceResolver.CreateForClient + S2zArchiveWriter

        string gameDir = Path.Combine(_clientDir, "game");
        await S2zArchiveWriter.WriteArchiveAsync(
            Path.Combine(gameDir, "resources2.s2z"),
            new Dictionary<string, byte[]> { ["heroes/test/hero.entity"] = Encoding.UTF8.GetBytes("<hero version='2' />") });
        await S2zArchiveWriter.WriteArchiveAsync(
            Path.Combine(gameDir, "resources10.s2z"),
            new Dictionary<string, byte[]> { ["heroes/test/hero.entity"] = Encoding.UTF8.GetBytes("<hero version='10' />") });

        using GameResourceResolver resolver = GameResourceResolver.CreateForClient(_clientDir);
        (string? content, string? source) = await resolver.TryResolveGameTextWithSourceAsync("heroes/test/hero.entity");

        content.Should().Be("<hero version='10' />");
        source.Should().Be("s2z:game/resources10.s2z");
    }
}
