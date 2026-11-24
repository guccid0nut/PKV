// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using FluentAssertions;
using Kongor.Modding.Conditions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Resolution;
using Kongor.Modding.Tests.Infrastructure;
using Kongor.Modding.Vfs;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive automated ingestion & baseline verification test fixture for all local HoN mods.
///     Ingests, parses, validates, and tests:
///     - 16 UI and gameplay .honmod packages (MODS-main.zip and loose extensions)
///     - 74 custom hero avatar .s2z packages (Avatar Mods.zip)
///     - 43 Smash Bros custom character packages
///     - AST XML parsing, condition evaluation, DAG resolution, Layered VFS & Direct Archive injection.
/// </summary>
public class LocalModsIngestionTests : IDisposable
{
    private static readonly string ModsZipPath = Environment.GetEnvironmentVariable("HON_TEST_MODS_ZIP")
                                                 ?? Path.Combine(
                                                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                     "Downloads", "MODS-main.zip");

    private static readonly string AvatarModsZipPath = Environment.GetEnvironmentVariable("HON_TEST_AVATAR_MODS_ZIP")
                                                       ?? Path.Combine(
                                                           Environment.GetFolderPath(Environment.SpecialFolder
                                                               .UserProfile), "Downloads",
                                                           "Avatar Mods-20251226T043303Z-3-002.zip");

    private static readonly string LooseModsDir = Environment.GetEnvironmentVariable("HON_TEST_LOOSE_MODS_DIR")
                                                  ?? @"C:\Games\Heroes of Newerth - Backup\extensions\mods";

    private static readonly string BackupGameDir = Environment.GetEnvironmentVariable("HON_TEST_BACKUP_GAME_DIR")
                                                   ?? @"C:\Games\Heroes of Newerth - Backup\game";

    private static readonly HashSet<string> ValidK2AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".model", ".mdf", ".material", ".tga", ".png", ".ogg", ".wav", ".mp3", ".flac",
        ".entity", ".effect", ".interface", ".package", ".lua", ".xml", ".clip", ".hdr",
        ".dds", "", ".str", ".txt", ".cfg", ".bot", ".shader", ".sample"
    };

    private readonly string _stagedModsDir;

    private readonly string _tempDir;
    private readonly string _testClientDir;

    public LocalModsIngestionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorLocalModsTest_" + Guid.NewGuid().ToString("N"));
        _stagedModsDir = Path.Combine(_tempDir, "staged_mods");
        _testClientDir = Path.Combine(_tempDir, "test_client");

        Directory.CreateDirectory(_stagedModsDir);
        Directory.CreateDirectory(Path.Combine(_testClientDir, "game"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore */
            }
    }

    /// <summary>
    ///     Extracts all 14 .honmod files from MODS-main.zip and copies 2 loose .honmod files to staged directory.
    ///     Returns a list of absolute file paths to all 16 staged .honmod packages.
    /// </summary>
    private async Task<List<string>> ExtractAndStageAll16HonModsAsync()
    {
        List<string> stagedPaths = [];

        // 1. Extract from MODS-main.zip (14 mods)
        if (File.Exists(ModsZipPath))
        {
            using FileStream fs = File.OpenRead(ModsZipPath);
            using ZipArchive zip = new(fs, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry entry in zip.Entries)
                if (entry.FullName.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith(".HONMOD", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(entry.FullName);
                    string destPath = Path.Combine(_stagedModsDir, fileName);

                    await using (Stream entryStream = entry.Open())
                    await using (FileStream destStream = File.Create(destPath))
                    {
                        await entryStream.CopyToAsync(destStream);
                    }

                    stagedPaths.Add(destPath);
                }
        }

        // 2. Copy loose .honmod files (2 mods)
        if (Directory.Exists(LooseModsDir))
        {
            string[] looseFiles = Directory.GetFiles(LooseModsDir, "*.honmod", SearchOption.TopDirectoryOnly);
            foreach (string looseFile in looseFiles)
            {
                string fileName = Path.GetFileName(looseFile);
                string destPath = Path.Combine(_stagedModsDir, fileName);
                File.Copy(looseFile, destPath, true);
                if (!stagedPaths.Contains(destPath, StringComparer.OrdinalIgnoreCase)) stagedPaths.Add(destPath);
            }
        }

        return stagedPaths;
    }

    [Fact]
    public async Task IngestAll16HonMods_WithInvalidInput_ParsesAllManifestsWithZeroXmlErrors()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();
        stagedPaths.Should().HaveCount(16, "all 16 local .honmod packages must be staged and available");

        List<ModPackage> loadedPackages = [];

        foreach (string modPath in stagedPaths)
        {
            File.Exists(modPath).Should().BeTrue();

            // Load and parse package
            ModPackage modPackage = await HonModArchiveReader.ReadPackageAsync(modPath);
            loadedPackages.Add(modPackage);

            ModManifest manifest = modPackage.Manifest;

            // Assert metadata integrity
            manifest.Name.Should().NotBeNullOrWhiteSpace($"Mod at '{modPath}' must have a valid Name");
            manifest.Version.Should().NotBeNullOrWhiteSpace($"Mod '{manifest.Name}' must have a valid Version");
            modPackage.Sha256Hash.Should().NotBeNullOrWhiteSpace();
            modPackage.FileSizeBytes.Should().BeGreaterThan(0);

            // Assert that the mod contains at least one actionable operation (copy, edit, or stringtable)
            int totalOps = manifest.FileCopies.Count + manifest.FileEdits.Count + manifest.StringtableEdits.Count;
            totalOps.Should().BeGreaterThan(0, $"Mod '{manifest.Name}' must contain at least one operation");
        }

        loadedPackages.Should().HaveCount(16);

        // Verify the presence of all 16 cataloged mods by name
        List<string> modNames = [.. loadedPackages.Select(m => m.Manifest.Name)];
        modNames.Should().Contain("AOE RI Complete Rework");
        modNames.Should().Contain("311 MIA");
        modNames.Should().Contain("Constant Inventory");
        modNames.Should().Contain("Items Manacost");
        modNames.Should().Contain("Last Hit Assist");
        modNames.Should().Contain("Melee/Ranged Buttons");
        modNames.Should().Contain("Rick and Morty Announcer Pack");
        modNames.Should().Contain("Stumps");
        modNames.Should().Contain("Swift Picker");
        modNames.Should().Contain("TowerDenier");
        modNames.Should().Contain("Unhide Vote");
        modNames.Should().Contain("Ranked Quickstats Tooltips");
        modNames.Should().Contain("Auto Pick/VoteBan Hero");
        modNames.Should().Contain("Lava Water v0.0.2");
        modNames.Should().Contain("Mario Sound Mod (Star Power)");
        modNames.Should().Contain("robrobo's heartbeat mod");

        // Clean up package handles
        foreach (ModPackage pkg in loadedPackages) pkg.Dispose();
    }

    [Fact]
    public async Task LocalHonMods_AstPatchingOperations_ParsedAccurately()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();

        List<ModPackage> loadedPackages = [];
        foreach (string p in stagedPaths) loadedPackages.Add(await HonModArchiveReader.ReadPackageAsync(p));

        try
        {
            // 1. AOE Range Indicators: 121 copyfile, 461 editfile operations
            ModPackage aoeMod = loadedPackages.First(m => m.Manifest.Name == "AOE RI Complete Rework");
            aoeMod.Manifest.FileCopies.Should().HaveCount(121);
            aoeMod.Manifest.FileEdits.Should().HaveCount(461);
            aoeMod.Manifest.Incompatibilities.Should().NotBeEmpty();

            // Check that operations inside AOE RI edits include Find, Insert, Replace, FindAll
            bool hasFind = aoeMod.Manifest.FileEdits.Any(e => e.Operations.Any(op => op is FindOperation));
            bool hasInsert = aoeMod.Manifest.FileEdits.Any(e => e.Operations.Any(op => op is InsertOperation));
            bool hasReplace = aoeMod.Manifest.FileEdits.Any(e => e.Operations.Any(op => op is ReplaceOperation));
            bool hasFindAll = aoeMod.Manifest.FileEdits.Any(e => e.Operations.Any(op => op is FindAllOperation));
            hasFind.Should().BeTrue();
            hasInsert.Should().BeTrue();
            hasReplace.Should().BeTrue();
            hasFindAll.Should().BeTrue();

            // 2. Rick and Morty Announcer: 44 copyfile operations
            ModPackage rnmMod = loadedPackages.First(m => m.Manifest.Name == "Rick and Morty Announcer Pack");
            rnmMod.Manifest.FileCopies.Should().HaveCount(44);
            rnmMod.Manifest.FileCopies.Should().AllSatisfy(c => c.TargetName.Should().StartWith("shared/sounds/"));

            // 3. Ranked Quickstats Tooltips: FindUp operations for nested panel tags
            ModPackage quickstatsMod = loadedPackages.First(m => m.Manifest.Name == "Ranked Quickstats Tooltips");
            quickstatsMod.Manifest.FileEdits.Should().HaveCount(2);
            bool hasFindUp = quickstatsMod.Manifest.FileEdits.Any(e => e.Operations.Any(op => op is FindUpOperation));
            hasFindUp.Should().BeTrue("Ranked Quickstats Tooltips relies on <findup> AST operations");

            // 4. Unhide Vote: Stringtable delta edits via <editfile>
            ModPackage unhideMod = loadedPackages.First(m => m.Manifest.Name == "Unhide Vote");
            unhideMod.Manifest.FileEdits.Should().HaveCount(2);
            unhideMod.Manifest.FileEdits.Select(s => s.TargetName)
                .Should().Contain(["stringtables/game_messages_en.str", "stringtables/game_messages_ru.str"]);

            // 5. Auto Pick / VoteBan: 2 copies and 8 edits
            ModPackage autoPickMod = loadedPackages.First(m => m.Manifest.Name == "Auto Pick/VoteBan Hero");
            autoPickMod.Manifest.FileCopies.Should().HaveCount(2);
            autoPickMod.Manifest.FileEdits.Should().HaveCount(8);

            // 6. Lava Water: 21 water material copies
            ModPackage lavaMod = loadedPackages.First(m => m.Manifest.Name == "Lava Water v0.0.2");
            lavaMod.Manifest.FileCopies.Should().HaveCount(21);
            lavaMod.Manifest.FileCopies.Should()
                .AllSatisfy(c => c.TargetName.Should().StartWith("world/water/materials/"));
        }
        finally
        {
            foreach (ModPackage pkg in loadedPackages) pkg.Dispose();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ConditionEvaluation_OnLocalModConditions_EvaluatesAccurately()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();
        List<ModPackage> loadedPackages = [];
        foreach (string p in stagedPaths) loadedPackages.Add(await HonModArchiveReader.ReadPackageAsync(p));

        try
        {
            // AOE Range Indicators contains conditional edits with condition="not '1337'"
            ModPackage aoeMod = loadedPackages.First(m => m.Manifest.Name == "AOE RI Complete Rework");
            List<EditFileOperation> conditionalEdits =
                [.. aoeMod.Manifest.FileEdits.Where(e => !string.IsNullOrEmpty(e.Condition))];
            conditionalEdits.Should().NotBeEmpty();

            string conditionExpr = conditionalEdits.First().Condition!;
            conditionExpr.Should().Be("not '1337'");

            // When no mod named '1337' is active, condition should evaluate to true
            bool resultWithout1337 = ConditionEvaluator.Evaluate(conditionExpr, loadedPackages);
            resultWithout1337.Should().BeTrue();

            // When a mod named '1337' is enabled in active list, condition should evaluate to false
            ModPackage mod1337 = new()
            {
                FilePath = "1337.honmod",
                IsEnabled = true,
                Manifest = new ModManifest { Name = "1337", Version = "1.0.0" }
            };

            List<ModPackage> activeWith1337 = [.. loadedPackages, mod1337];
            bool resultWith1337 = ConditionEvaluator.Evaluate(conditionExpr, activeWith1337);
            resultWith1337.Should().BeFalse();
        }
        finally
        {
            foreach (ModPackage pkg in loadedPackages) pkg.Dispose();
        }
    }

    [Fact]
    public async Task DependencyGraphAndOrdering_OnLocalModsSuite_ResolvesCleanly()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();
        List<ModPackage> loadedPackages = [];
        foreach (string p in stagedPaths)
        {
            ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p);
            pkg.IsEnabled = true;
            loadedPackages.Add(pkg);
        }

        try
        {
            // 1. Dependency Graph construction
            DependencyGraph graph = DependencyGraph.Build(loadedPackages);
            graph.Nodes.Should().HaveCount(16);

            // 2. Topological Sort resolution
            SortResult sortResult = TopologicalSorter.ResolveAndSort(loadedPackages);
            sortResult.Success.Should().BeTrue("local mods suite has no missing dependencies or cyclic graphs");
            sortResult.ExecutionPlan.Should().HaveCount(16);
            sortResult.CycleReports.Should().BeEmpty();

            // 3. Collision Matrix analysis
            ConflictReport conflictReport = ConflictMatrix.Analyze(loadedPackages);
            conflictReport.Collisions.Should().NotBeNull();

            // Check that collision matrix detects shared target interfaces (e.g. system_bar.package or mod_options_elements)
            bool detectedOverlappingFiles = conflictReport.Collisions.Any(c =>
                c.TargetFile.Contains("system_bar") ||
                c.TargetFile.Contains("main.interface") ||
                c.TargetFile.Contains("mod_options_elements"));

            detectedOverlappingFiles.Should()
                .BeTrue("multiple UI mods legitimately augment the same HUD interface targets");
        }
        finally
        {
            foreach (ModPackage pkg in loadedPackages) pkg.Dispose();
        }
    }

    [Fact]
    public async Task LayeredVfsInjection_LocalMods_InjectsFilesIntoOverrideDirectory()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();

        // Select a representative batch of local mods for VFS injection test
        List<string> selectedNames =
        [
            "MarioSoundModStarPower.honmod",
            "robrobo_hbmod.honmod",
            "Lava Water v0.0.2.HONMOD",
            "Stumps v2.3.HONMOD",
            "BangMIA v0.0.1.HONMOD"
        ];

        List<ModPackage> selectedMods = [];
        foreach (string p in stagedPaths)
            if (selectedNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p);
                pkg.IsEnabled = true;
                selectedMods.Add(pkg);
            }

        selectedMods.Should().HaveCount(5);

        try
        {
            ModManager manager = new();

            // 1. Apply in VFS mode
            ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
            {
                GameClientDirectory = _testClientDir,
                ModsToApply = selectedMods,
                Mode = InjectionMode.Vfs
            });

            applyResult.Success.Should().BeTrue();
            applyResult.AppliedMods.Should().HaveCount(5);
            applyResult.ModifiedFiles.Should().NotBeEmpty();

            // 2. Verify that injected files exist in override directory
            string overrideDir = Path.Combine(_testClientDir, "override");
            Directory.Exists(overrideDir).Should().BeTrue();

            // Check specific copied files from selected mods:
            // - Mario Sound Mod: shared/sounds/items/item_immunity.ogg
            string marioOgg = Path.Combine(overrideDir, "shared", "sounds", "items", "item_immunity.ogg");
            File.Exists(marioOgg).Should().BeTrue();

            // - robrobo heartbeat mod: ui/heartbeat.package
            string hbPkg = Path.Combine(overrideDir, "ui", "heartbeat.package");
            File.Exists(hbPkg).Should().BeTrue();

            // - Lava Water: world/water/materials/bloodwater_glctf.material
            string lavaMat = Path.Combine(overrideDir, "world", "water", "materials", "bloodwater_glctf.material");
            File.Exists(lavaMat).Should().BeTrue();

            // - Stumps: world/props/trees/stump/wc3.material
            string stumpMat = Path.Combine(overrideDir, "world", "props", "trees", "stump", "wc3.material");
            File.Exists(stumpMat).Should().BeTrue();

            // - Trigger file for live client hot-reloading
            string triggerFile = Path.Combine(overrideDir, ".hotreload.trigger");
            File.Exists(triggerFile).Should().BeTrue();

            // - Rollback journal
            string journalPath = RollbackJournal.GetJournalPath(_testClientDir);
            File.Exists(journalPath).Should().BeTrue();

            // 3. Test Rollback
            RollbackResult rollbackResult = await manager.RollbackAsync(_testClientDir);
            rollbackResult.Success.Should().BeTrue();

            // Injected files should be cleaned up
            File.Exists(marioOgg).Should().BeFalse();
            File.Exists(hbPkg).Should().BeFalse();
            File.Exists(lavaMat).Should().BeFalse();
            File.Exists(stumpMat).Should().BeFalse();
            File.Exists(journalPath).Should().BeFalse();
        }
        finally
        {
            foreach (ModPackage pkg in selectedMods) pkg.Dispose();
        }
    }

    [Fact]
    public async Task DirectArchiveInjection_LocalMods_ProducesValidResources999S2z()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();

        // Select representative mods for direct archive injection test
        List<string> selectedNames =
        [
            "MarioSoundModStarPower.honmod",
            "robrobo_hbmod.honmod",
            "Lava Water v0.0.2.HONMOD",
            "Stumps v2.3.HONMOD"
        ];

        List<ModPackage> selectedMods = [];
        foreach (string p in stagedPaths)
            if (selectedNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p);
                pkg.IsEnabled = true;
                selectedMods.Add(pkg);
            }

        selectedMods.Should().HaveCount(4);

        try
        {
            ModManager manager = new();

            // 1. Apply in Archive mode
            ApplyResult applyResult = await manager.ApplyModsAsync(new ApplyOptions
            {
                GameClientDirectory = _testClientDir,
                ModsToApply = selectedMods,
                Mode = InjectionMode.Archive,
                TargetArchiveName = "resources999.s2z"
            });

            applyResult.Success.Should().BeTrue();
            applyResult.AppliedMods.Should().HaveCount(4);

            string s2zPath = Path.Combine(_testClientDir, "game", "resources999.s2z");
            File.Exists(s2zPath).Should().BeTrue();

            // 2. Validate S2Z archive structure with S2zArchiveReader
            using (S2zArchiveReader s2z = S2zArchiveReader.Open(s2zPath))
            {
                s2z.EntryNames.Should().NotBeEmpty();
                s2z.HasEntry("shared/sounds/items/item_immunity.ogg").Should().BeTrue();
                s2z.HasEntry("ui/heartbeat.package").Should().BeTrue();
                s2z.HasEntry("world/water/materials/bloodwater_glctf.material").Should().BeTrue();
                s2z.HasEntry("world/props/trees/stump/wc3.material").Should().BeTrue();

                byte[] oggBytes = await s2z.ReadEntryBytesAsync("shared/sounds/items/item_immunity.ogg");
                oggBytes.Should().NotBeEmpty();
            }

            // 3. Test Rollback
            RollbackResult rollbackResult = await manager.RollbackAsync(_testClientDir);
            rollbackResult.Success.Should().BeTrue();
            File.Exists(s2zPath).Should().BeFalse();
        }
        finally
        {
            foreach (ModPackage pkg in selectedMods) pkg.Dispose();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AvatarMods_IngestAll74S2zPackages_ValidatesArchiveStructureAndAssetPaths()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        File.Exists(AvatarModsZipPath).Should().BeTrue($"Avatar Mods archive must exist at {AvatarModsZipPath}");

        using FileStream fs = File.OpenRead(AvatarModsZipPath);
        using ZipArchive outerZip = new(fs, ZipArchiveMode.Read);

        List<ZipArchiveEntry> s2zEntries =
            [.. outerZip.Entries.Where(e => e.FullName.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase))];

        s2zEntries.Should().HaveCount(74, "Avatar Mods.zip must contain exactly 74 custom avatar packages");

        int dota2Count = 0;
        int smashCount = 0;
        int warcraftCount = 0;
        int totalAssetFilesChecked = 0;

        foreach (ZipArchiveEntry s2zEntry in s2zEntries)
        {
            if (s2zEntry.FullName.Contains("Dota 2", StringComparison.OrdinalIgnoreCase)) dota2Count++;
            else if (s2zEntry.FullName.Contains("Super Smash Bros", StringComparison.OrdinalIgnoreCase)) smashCount++;
            else if (s2zEntry.FullName.Contains("Warcraft III", StringComparison.OrdinalIgnoreCase)) warcraftCount++;

            string tempS2z = Path.Combine(_tempDir, $"temp_avatar_{Guid.NewGuid():N}.s2z");
            try
            {
                // Stream extract to temporary S2Z file
                await using (Stream s2zStream = s2zEntry.Open())
                await using (FileStream outFs = File.Create(tempS2z))
                {
                    await s2zStream.CopyToAsync(outFs);
                }

                using S2zArchiveReader reader = S2zArchiveReader.Open(tempS2z);
                reader.EntryNames.Should().NotBeEmpty($"S2Z package '{s2zEntry.FullName}' must not be empty");

                foreach (string normPath in reader.EntryNames)
                {
                    totalAssetFilesChecked++;

                    // Assert path normalization
                    normPath.Should().NotStartWith("/");
                    normPath.Should().NotContain("\\");

                    // Assert valid K2 asset extension via fast HashSet lookup
                    string ext = Path.GetExtension(normPath);
                    ValidK2AssetExtensions.Contains(ext).Should().BeTrue(
                        $"Asset '{normPath}' in '{s2zEntry.FullName}' must have a valid K2 game engine extension");
                }
            }
            finally
            {
                if (File.Exists(tempS2z))
                    try
                    {
                        File.Delete(tempS2z);
                    }
                    catch
                    {
                        /* ignore */
                    }
            }
        }

        dota2Count.Should().Be(59, "59 Dota 2 avatar mods expected");
        smashCount.Should().Be(3, "3 Super Smash Bros avatar mods expected");
        warcraftCount.Should().Be(12, "12 Warcraft III avatar mods expected");
        totalAssetFilesChecked.Should()
            .BeGreaterThan(15000, "all 74 avatar archives combined contain > 15,000 game assets");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void SmashCharacterPackages_BackupGameDirectory_Validates43S2zArchives()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        if (!Directory.Exists(BackupGameDir))
            // Skip if backup directory is not on current machine
            return;

        string[] characterArchives =
        [
            .. Directory.GetFiles(BackupGameDir, "resources*.s2z", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string fn = Path.GetFileName(f);
                    return !fn.Equals("resources0.s2z", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Equals("resources1.s2z", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Equals("resources2.s2z", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Equals("resources3.s2z", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Equals("resources4.s2z", StringComparison.OrdinalIgnoreCase);
                })
        ];

        characterArchives.Should()
            .HaveCount(43, "Backup game folder contains 43 custom Smash Bros character S2Z archives");

        foreach (string archPath in characterArchives)
        {
            using S2zArchiveReader reader = S2zArchiveReader.Open(archPath);
            reader.EntryNames.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ModManifestParser_RoundTripSerialization_PreservesAllLocalModManifests()
    {
        List<string> stagedPaths = await ExtractAndStageAll16HonModsAsync();

        foreach (string p in stagedPaths)
        {
            using ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(p);
            ModManifest original = pkg.Manifest;

            // Serialize back to XML
            string serializedXml = ModManifestParser.Serialize(original);
            serializedXml.Should().NotBeNullOrWhiteSpace();

            // Re-parse
            ModManifest reparsed = ModManifestParser.Parse(serializedXml);

            // Assert round-trip fidelity
            reparsed.Name.Should().Be(original.Name);
            reparsed.Version.Should().Be(original.Version);
            reparsed.FileCopies.Count.Should().Be(original.FileCopies.Count);
            reparsed.FileEdits.Count.Should().Be(original.FileEdits.Count);
            reparsed.StringtableEdits.Count.Should().Be(original.StringtableEdits.Count);
            reparsed.Requirements.Count.Should().Be(original.Requirements.Count);
            reparsed.Incompatibilities.Count.Should().Be(original.Incompatibilities.Count);
        }
    }
}
