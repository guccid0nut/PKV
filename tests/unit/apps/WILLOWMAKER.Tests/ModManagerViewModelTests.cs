// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public class ModManagerViewModelTests
{
    [Fact]
    public void ModManager_Initializes_And_Loads_Mods()
    {
        ModManagerViewModel vm = new();
        Assert.NotNull(vm.Mods);
        Assert.NotNull(vm.FilteredMods);
        Assert.True(vm.TotalModsCount >= 0);
    }

    [Fact]
    public void ModManager_Reactive_Active_Count_Updates_On_Toggle()
    {
        ModManagerViewModel vm = new();
        ModItemViewModel testMod1 = new() { Name = "Test Mod 1", Category = "UI & HUD", IsEnabled = false };
        ModItemViewModel testMod2 = new()
            { Name = "Pikachu (Super Smash Bros)", Category = "Super Smash Bros", IsEnabled = false };

        vm.Mods.Clear();
        testMod1.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ModItemViewModel.IsEnabled)) vm.UpdateActiveCount();
        };
        testMod2.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ModItemViewModel.IsEnabled)) vm.UpdateActiveCount();
        };

        vm.Mods.Add(testMod1);
        vm.Mods.Add(testMod2);
        vm.UpdateActiveCount();

        Assert.Equal(0, vm.ActiveModsCount);

        // Toggle mod 1
        testMod1.IsEnabled = true;
        Assert.Equal(1, vm.ActiveModsCount);

        // Toggle mod 2
        testMod2.IsEnabled = true;
        Assert.Equal(2, vm.ActiveModsCount);

        // Untoggle mod 1
        testMod1.IsEnabled = false;
        Assert.Equal(1, vm.ActiveModsCount);
    }

    [Fact]
    public void ModManager_Filters_Search_Query_Accurately()
    {
        ModManagerViewModel vm = new();
        vm.Mods.Clear();

        vm.Mods.Add(new ModItemViewModel
            { Name = "Pikachu (Super Smash Bros)", Category = "Super Smash Bros", Author = "Nintendo" });
        vm.Mods.Add(new ModItemViewModel
            { Name = "Bowser (Super Smash Bros)", Category = "Super Smash Bros", Author = "Nintendo" });
        vm.Mods.Add(new ModItemViewModel { Name = "AOE Range Indicators", Category = "UI & HUD", Author = "Viden" });

        // Search "pikachu"
        vm.SearchQuery = "pikachu";
        Assert.Single(vm.FilteredMods);
        Assert.Contains("Pikachu", vm.FilteredMods[0].Name);

        // Search "super"
        vm.SearchQuery = "super";
        Assert.Equal(2, vm.FilteredMods.Count);

        // Search "aoe"
        vm.SearchQuery = "aoe";
        Assert.Single(vm.FilteredMods);
        Assert.Contains("AOE", vm.FilteredMods[0].Name);
    }

    [Fact]
    public void ModManager_Filters_Category_Accurately()
    {
        ModManagerViewModel vm = new();
        vm.Mods.Clear();

        vm.Mods.Add(new ModItemViewModel { Name = "Pikachu", Category = "Super Smash Bros" });
        vm.Mods.Add(new ModItemViewModel { Name = "Rexxar", Category = "Warcraft III" });
        vm.Mods.Add(new ModItemViewModel { Name = "Anti-Mage", Category = "Dota 2" });
        vm.Mods.Add(new ModItemViewModel { Name = "Tower Denier", Category = "Gameplay & Assists" });

        vm.SelectedCategoryFilter = "Super Smash Bros";
        Assert.Single(vm.FilteredMods);
        Assert.Equal("Pikachu", vm.FilteredMods[0].Name);

        vm.SelectedCategoryFilter = "Warcraft III";
        Assert.Single(vm.FilteredMods);
        Assert.Equal("Rexxar", vm.FilteredMods[0].Name);

        vm.SelectedCategoryFilter = "All Mods";
        Assert.Equal(4, vm.FilteredMods.Count);
    }

    [Fact]
    public void ModManager_Favorite_Toggling_And_Sync()
    {
        ModManagerViewModel vm = new();
        vm.Mods.Clear();

        ModItemViewModel mod1 = new() { Id = "mod1", Name = "Mod 1", Category = "UI", IsFavorite = true };
        ModItemViewModel mod2 = new() { Id = "mod2", Name = "Mod 2", Category = "Audio", IsFavorite = false };
        vm.Mods.Add(mod1);
        vm.Mods.Add(mod2);
        vm.UpdateFavorites();

        Assert.Single(vm.FavoriteMods);
        Assert.Equal("Mod 1", vm.FavoriteMods[0].Name);

        // Toggle favorite off
        vm.ToggleFavorite(mod1);
        Assert.False(mod1.IsFavorite);
        Assert.Empty(vm.FavoriteMods);

        // Toggle favorite back on for mod2
        vm.ToggleFavorite(mod2);
        Assert.True(mod2.IsFavorite);
        Assert.Single(vm.FavoriteMods);
        Assert.Equal("Mod 2", vm.FavoriteMods[0].Name);
    }

    [Fact]
    public void ModManager_ToggleModActive_Switches_State()
    {
        ModManagerViewModel vm = new();
        vm.Mods.Clear();

        ModItemViewModel testMod = new() { Id = "mod_test", Name = "Test Mod", Category = "UI", IsEnabled = false, IsFavorite = true };
        vm.Mods.Add(testMod);
        vm.UpdateFavorites();

        Assert.Single(vm.FavoriteMods);
        Assert.False(testMod.IsEnabled);

        vm.ToggleModActive(testMod);
        Assert.True(testMod.IsEnabled);

        vm.ToggleModActive(testMod);
        Assert.False(testMod.IsEnabled);
    }

    [Fact]
    public void BinauralSpatialAudio_Generates_Valid_Pcm_Wav()
    {
        // Front (0 degrees)
        byte[] frontWav = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(0f);
        Assert.NotNull(frontWav);
        Assert.True(frontWav.Length > 44); // At least RIFF header

        // Verify RIFF and WAVE magic numbers
        string riff = System.Text.Encoding.ASCII.GetString(frontWav, 0, 4);
        string wave = System.Text.Encoding.ASCII.GetString(frontWav, 8, 4);
        Assert.Equal("RIFF", riff);
        Assert.Equal("WAVE", wave);

        // Verify Orbit WAV
        byte[] orbitWav = BinauralSpatialAudioSynthesizer.GenerateOrbitWav();
        Assert.NotNull(orbitWav);
        Assert.True(orbitWav.Length > 44);
        string orbitRiff = System.Text.Encoding.ASCII.GetString(orbitWav, 0, 4);
        Assert.Equal("RIFF", orbitRiff);
    }

    [Fact]
    public void ModManager_ActiveAndFavoriteMods_Shows_Active_First_Then_Inactive_Favorites()
    {
        ModManagerViewModel vm = new();
        vm.Mods.Clear();

        // Mod A: Inactive Favorite
        ModItemViewModel modA = new() { Id = "modA", Name = "Mod A (Inactive Fav)", IsEnabled = false, IsFavorite = true };
        // Mod B: Active Non-Favorite
        ModItemViewModel modB = new() { Id = "modB", Name = "Mod B (Active Non-Fav)", IsEnabled = true, IsFavorite = false };
        // Mod C: Active Favorite
        ModItemViewModel modC = new() { Id = "modC", Name = "Mod C (Active Fav)", IsEnabled = true, IsFavorite = true };
        // Mod D: Inactive Non-Favorite
        ModItemViewModel modD = new() { Id = "modD", Name = "Mod D (Inactive Non-Fav)", IsEnabled = false, IsFavorite = false };

        vm.Mods.Add(modA);
        vm.Mods.Add(modB);
        vm.Mods.Add(modC);
        vm.Mods.Add(modD);

        vm.UpdateActiveCount();
        vm.UpdateFavorites();
        vm.UpdateActiveAndFavorites();

        // Expected: Active mods first (modB, modC), then inactive favorites (modA)
        // modD should not appear in ActiveAndFavoriteMods
        Assert.Equal(3, vm.ActiveAndFavoriteMods.Count);
        Assert.True(vm.ActiveAndFavoriteMods[0].IsEnabled); // Active first
        Assert.True(vm.ActiveAndFavoriteMods[1].IsEnabled); // Active first
        Assert.False(vm.ActiveAndFavoriteMods[2].IsEnabled); // Inactive favorite next
        Assert.True(vm.ActiveAndFavoriteMods[2].IsFavorite);
        Assert.DoesNotContain(modD, vm.ActiveAndFavoriteMods);

        // When Mod A is toggled active, it becomes active and stays in shelf
        vm.ToggleModActive(modA);
        Assert.True(modA.IsEnabled);
        Assert.Contains(modA, vm.ActiveAndFavoriteMods);
    }

    [Fact]
    public void ModItemViewModel_FavoriteGlyph_And_Color_Reflect_State()
    {
        ModItemViewModel mod = new() { IsFavorite = false };
        Assert.Equal("☆", mod.FavoriteGlyph);
        Assert.Equal("#64748B", mod.FavoriteColor);

        mod.IsFavorite = true;
        Assert.Equal("★", mod.FavoriteGlyph);
        Assert.Equal("#FBBF24", mod.FavoriteColor);
    }

    [Fact]
    public void SpatialAudio_Generates_InGame_Effects_And_Music()
    {
        // Spell Cast WAV
        byte[] spellWav = BinauralSpatialAudioSynthesizer.GenerateSpellCastWav(45f, 1.0f);
        Assert.NotNull(spellWav);
        Assert.True(spellWav.Length > 100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(spellWav, 0, 4));

        // Melee Attack WAV
        byte[] attackWav = BinauralSpatialAudioSynthesizer.GenerateMeleeAttackWav(-90f, 0.8f);
        Assert.NotNull(attackWav);
        Assert.True(attackWav.Length > 100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(attackWav, 0, 4));

        // Can You Feel My Heart Looping WAV
        byte[] musicWav = BinauralSpatialAudioSynthesizer.GenerateCanYouFeelMyHeartWav(0f, 1.0f);
        Assert.NotNull(musicWav);
        Assert.True(musicWav.Length > 100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(musicWav, 0, 4));

        // Announcer WAV
        byte[] announcerWav = BinauralSpatialAudioSynthesizer.GenerateAnnouncerWav("first_blood", 90f, 1.2f);
        Assert.NotNull(announcerWav);
        Assert.True(announcerWav.Length > 100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(announcerWav, 0, 4));
    }

    [Theory]
    [InlineData("BowserJr", "Bowser Jr")]
    [InlineData("CaptainFalcon", "Captain Falcon")]
    [InlineData("DonkeyKong", "Donkey Kong")]
    [InlineData("PiranhaPlant", "Piranha Plant")]
    [InlineData("KingDedede", "King Dedede")]
    [InlineData("PokemonTrainer", "Pokemon Trainer")]
    [InlineData("Pikachu", "Pikachu")]
    [InlineData("DotaHeroPudge", "Dota Hero Pudge")]
    public void FormatCamelCase_PreservesCapitalLetters_And_SpacesWords(string input, string expected)
    {
        string result = ModManagerViewModel.FormatCamelCase(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ModManager_AutoSync_Debounced_Executes_Without_Exception()
    {
        ModManagerViewModel vm = new(autoLoad: false);
        vm.ScheduleAutoApply(debounceDelayMs: 20);
        await Task.Delay(50);
        Assert.False(vm.HasError, vm.ErrorMessage);
    }

    [Fact]
    public void TreeModels_AllTreeMdfs_PointToExistingModelFiles()
    {
        string baseDir = AppContext.BaseDirectory;
        string repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", ".."));
        string treeDir = Path.Combine(repoRoot, "games", "shared", "world", "props", "trees");
        if (!Directory.Exists(treeDir)) return;

        string[] mdfFiles = Directory.GetFiles(treeDir, "*.mdf", SearchOption.AllDirectories);
        Assert.NotEmpty(mdfFiles);

        foreach (string mdf in mdfFiles)
        {
            string content = File.ReadAllText(mdf);
            var match = System.Text.RegularExpressions.Regex.Match(content, @"file=""([^""]+)""");
            if (match.Success)
            {
                string rel = match.Groups[1].Value;
                string targetPath = Path.Combine(Path.GetDirectoryName(mdf)!, rel);
                Assert.True(File.Exists(targetPath), $"MDF {mdf} references missing model: {rel}");
            }
        }
    }

    [Fact]
    public void ModManager_DoesNotAutoFavoriteTop3_WhenFavoritesEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_EmptyFav_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            LauncherSettingsModel settings = new() { FavoriteModIds = [] };
            LauncherSettingsService.Save(settings, tempDir);

            ModManagerViewModel vm = new(autoLoad: false) { SettingsDirectory = tempDir };
            vm.Mods.Add(new ModItemViewModel { Id = "mod1", Name = "Mod 1" });
            vm.Mods.Add(new ModItemViewModel { Id = "mod2", Name = "Mod 2" });
            vm.Mods.Add(new ModItemViewModel { Id = "mod3", Name = "Mod 3" });
            vm.Mods.Add(new ModItemViewModel { Id = "mod4", Name = "Mod 4" });
            vm.UpdateFavorites();

            Assert.Empty(vm.FavoriteMods);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsProtectedSystemOrExportArchive_ProtectsSeasonalAndPromotedCustomMaps()
    {
        string[] protectedMaps =
        [
            "midwars_halloween.s2z",
            "caldavar_halloween.s2z",
            "three_kingdoms.s2z",
            "persian_pass.s2z",
            "archers.s2z",
            "valkwars.s2z",
            "pebbles_duel.s2z",
            "truedevo.s2z",
            "arena.s2z",
            "colosseum_duel.s2z",
            "empath_maze.s2z",
            "enfos.s2z",
            "nightmarish_coop.s2z",
            "caldavar_wtf.s2z",
            "mids_omg.s2z",
            "midwars_10v10.s2z",
            "devowars_10v10.s2z",
            "truedevo_10v10.s2z"
        ];

        foreach (string mapArchive in protectedMaps)
        {
            Assert.True(ModManagerViewModel.IsProtectedSystemOrExportArchive(mapArchive), $"Expected {mapArchive} to be protected.");
            Assert.True(ModManagerViewModel.IsProtectedSystemOrExportArchive($"games/client/game/maps/{mapArchive}"), $"Expected nested {mapArchive} to be protected.");
        }

        Assert.False(ModManagerViewModel.IsProtectedSystemOrExportArchive("survival_mode.s2z"));
        Assert.False(ModManagerViewModel.IsProtectedSystemOrExportArchive("unregistered_random_user_mod.s2z"));
    }

    [Fact]
    public void ModManager_RestoresExactFavorites_FromSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_RestoreFav_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string targetId = "target_mod_2";
            LauncherSettingsModel settings = new() { FavoriteModIds = [targetId] };
            LauncherSettingsService.Save(settings, tempDir);

            ModManagerViewModel vm = new(autoLoad: false) { SettingsDirectory = tempDir };
            vm.Mods.Add(new ModItemViewModel { Id = "mod1", Name = "Mod 1" });
            vm.Mods.Add(new ModItemViewModel { Id = targetId, Name = "Target Mod 2" });

            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);
            HashSet<string> favSet = new(loaded.FavoriteModIds, StringComparer.OrdinalIgnoreCase);
            foreach (var m in vm.Mods)
            {
                if (favSet.Contains(m.Id) || favSet.Contains(m.Name)) m.IsFavorite = true;
            }
            vm.UpdateFavorites();

            Assert.Single(vm.FavoriteMods);
            Assert.Equal(targetId, vm.FavoriteMods[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}


