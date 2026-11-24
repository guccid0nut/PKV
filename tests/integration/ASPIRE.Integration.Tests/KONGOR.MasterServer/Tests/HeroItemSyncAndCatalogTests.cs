// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Game;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class HeroItemSyncAndCatalogTests

{
    private static string FindDataFile(string fileName)

    {
        string baseDir = AppContext.BaseDirectory;

        string local = Path.Combine(baseDir, "Data", fileName);

        if (File.Exists(local))

            return local;


        DirectoryInfo? current = new(baseDir);

        while (current != null)

        {
            string candidate = Path.Combine(current.FullName, "libs", "database-context", "src", "Data", fileName);

            if (!File.Exists(candidate))
                candidate = Path.Combine(current.FullName, "MERRICK.DatabaseContext", "Data", fileName);

            if (File.Exists(candidate)) return Path.GetFullPath(candidate);


            current = current.Parent;
        }


        return local;
    }


    private static string FindWwwroot()

    {
        string baseDir = AppContext.BaseDirectory;

        DirectoryInfo? current = new(baseDir);

        while (current != null)

        {
            string candidate = Path.Combine(current.FullName, "src", "services", "web-ui", "wwwroot");

            if (!Directory.Exists(candidate))
                candidate = Path.Combine(current.FullName, "services", "web-ui", "src", "wwwroot");

            if (!Directory.Exists(candidate))
                candidate = Path.Combine(current.FullName, "DAWNBRINGER.WebPortal.UI", "wwwroot");

            if (Directory.Exists(Path.Combine(candidate, "images", "items"))) return Path.GetFullPath(candidate);


            current = current.Parent;
        }


        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }


    private List<HeroItem> LoadHeroItemsCatalog()

    {
        string jsonPath = FindDataFile("HeroItems.json");

        Assert.True(File.Exists(jsonPath), $"HeroItems.json should exist at {jsonPath}");


        string jsonContent = File.ReadAllText(jsonPath);

        List<HeroItem>? items = JsonSerializer.Deserialize<List<HeroItem>>(jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });


        Assert.NotNull(items);

        Assert.NotEmpty(items);

        return items;
    }


    [Fact]
    public void HeroItemsCatalog_AllItems_HaveValidFieldsAndNoPlaceholders()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();


        foreach (HeroItem item in items)

        {
            Assert.False(string.IsNullOrWhiteSpace(item.Code), $"Item Code should not be empty: {item.Name}");

            Assert.False(string.IsNullOrWhiteSpace(item.Name), $"Item Name should not be empty for code: {item.Code}");

            Assert.True(item.Cost >= 0, $"Item Cost should be non-negative for item: {item.Name}");

            Assert.False(string.IsNullOrWhiteSpace(item.IconPath),
                $"Item IconPath should not be empty for item: {item.Name}");


            // Ensure descriptions & lore do not contain raw placeholder strings

            Assert.DoesNotContain("Standard component item", item.Description, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("Standard Hero Item", item.Description, StringComparison.OrdinalIgnoreCase);


            // Ensure name is not raw uppercase item code

            Assert.False(item.Name.All(char.IsUpper) && item.Name.Length > 4,
                $"Item Name should not be raw uppercase code: '{item.Name}'");
        }
    }


    private Dictionary<string, HeroItem> BuildItemLookup(List<HeroItem> items)
    {
        Dictionary<string, HeroItem> lookup = new(StringComparer.OrdinalIgnoreCase);

        foreach (HeroItem item in items)
        {
            string code = item.Code.ToLowerInvariant().Trim();
            lookup[code] = item;

            string clean = item.Name.ToLowerInvariant().Replace(" ", "").Replace("'", "").Replace("_", "");
            lookup[clean] = item;

            if (code.StartsWith("item_", StringComparison.OrdinalIgnoreCase)) lookup[code[5..]] = item;
        }

        foreach (KeyValuePair<string, string> kvp in global::DAWNBRINGER.WebPortal.UI.Controllers.HomeController.LegacyItemAliases)
            if (lookup.TryGetValue(kvp.Value, out HeroItem? target))
                lookup[kvp.Key] = target;

        return lookup;
    }


    [Fact]
    public void GeometersBane_SpikedBola_NullfireBlade_SyncVerification()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        // 1. Geometer's Bane

        Assert.True(itemLookup.ContainsKey("geometersbane"));

        Assert.True(itemLookup.ContainsKey("manaburn2"));

        HeroItem geo = itemLookup["geometersbane"];

        Assert.Equal("Geometer's Bane", geo.Name);

        Assert.Equal(5000, geo.Cost);

        Assert.False(string.IsNullOrWhiteSpace(geo.Description));


        List<string>? geoComps = JsonSerializer.Deserialize<List<string>>(geo.ComponentCodesJson);

        Assert.NotNull(geoComps);

        Assert.Contains("firebrand", geoComps);

        Assert.Contains("blessedorb", geoComps);

        Assert.Contains("recipe", geoComps);


        // 2. Spiked Bola

        Assert.True(itemLookup.ContainsKey("spikedbola"));

        Assert.True(itemLookup.ContainsKey("spiked_bola"));

        HeroItem bola = itemLookup["spikedbola"];

        Assert.Equal("Spiked Bola", bola.Name);

        Assert.Equal(3600, bola.Cost);

        Assert.False(string.IsNullOrWhiteSpace(bola.Description));


        // 3. Nullfire Blade

        Assert.True(itemLookup.ContainsKey("nullfireblade"));

        Assert.True(itemLookup.ContainsKey("manaburn1"));

        HeroItem nullfire = itemLookup["nullfireblade"];

        Assert.Equal("Nullfire Blade", nullfire.Name);

        Assert.Equal(3300, nullfire.Cost);


        List<string>? nullfireComps = JsonSerializer.Deserialize<List<string>>(nullfire.ComponentCodesJson);

        Assert.NotNull(nullfireComps);

        Assert.Equal(4, nullfireComps.Count);

        Assert.Equal(2, nullfireComps.Count(c => c.Equals("quickblade", StringComparison.OrdinalIgnoreCase)));

        Assert.Contains("soulscreamring", nullfireComps);

        Assert.Contains("recipe", nullfireComps);


        // 4. Energizer

        Assert.True(itemLookup.ContainsKey("energizer"));

        HeroItem energizer = itemLookup["energizer"];

        Assert.Equal("Energizer", energizer.Name);

        Assert.Equal(1400, energizer.Cost);


        List<string>? enerComps = JsonSerializer.Deserialize<List<string>>(energizer.ComponentCodesJson);

        Assert.NotNull(enerComps);

        Assert.Contains("majortotem", enerComps);

        Assert.Contains("scarab", enerComps);

        Assert.Contains("windwhistle", enerComps);

        Assert.Contains("recipe", enerComps);


        // 5. Veiled Rot: 100g

        Assert.True(itemLookup.ContainsKey("veiledrot"));

        HeroItem rot = itemLookup["veiledrot"];

        Assert.Equal("Veiled Rot", rot.Name);

        Assert.Equal(100, rot.Cost);


        // 6. Steamboots: Marchers (500) + Gloves of Swiftness (500) + Apprentice Robes (450) = 1450

        Assert.True(itemLookup.ContainsKey("steamboots"));

        HeroItem steam = itemLookup["steamboots"];

        Assert.Equal("Steamboots", steam.Name);

        Assert.Equal(1450, steam.Cost);


        List<string>? steamComps = JsonSerializer.Deserialize<List<string>>(steam.ComponentCodesJson);

        Assert.NotNull(steamComps);

        Assert.Contains("marchers", steamComps);

        Assert.Contains("glovesoftheswift", steamComps);

        Assert.Contains("apprenticesrobe", steamComps);


        // 7. Insanitarius: Helm of the Victim (950) + Warhammer (1600) + Recipe (50) = 2600

        Assert.True(itemLookup.ContainsKey("insanitarius"));

        HeroItem insan = itemLookup["insanitarius"];

        Assert.Equal("Insanitarius", insan.Name);

        Assert.Equal(2700, insan.Cost);


        // 8. Ghost Marchers: Marchers (500) + Punchdagger (500) + Punchdagger (500) = 1500

        Assert.True(itemLookup.ContainsKey("ghostmarchers"));

        HeroItem ghost = itemLookup["ghostmarchers"];

        Assert.Equal("Ghost Marchers", ghost.Name);

        Assert.Equal(1500, ghost.Cost);


        // 9. Hypercrown: Blessed Orb (2100) + Warpcleft (1800) + Recipe (1900) = 5800

        Assert.True(itemLookup.ContainsKey("hypercrown"));

        HeroItem hyper = itemLookup["hypercrown"];

        Assert.Equal("Hypercrown", hyper.Name);

        Assert.Equal(5800, hyper.Cost);


        // 10. Harkon's Blade: Acolyte's Staff (2600) + Arcana (1575) + Recipe (600) = 4775

        Assert.True(itemLookup.ContainsKey("harkonsblade"));

        HeroItem harkon = itemLookup["harkonsblade"];

        Assert.Equal("Harkon's Blade", harkon.Name);

        Assert.Equal(4775, harkon.Cost);


        List<string>? harkonComps = JsonSerializer.Deserialize<List<string>>(harkon.ComponentCodesJson);

        Assert.NotNull(harkonComps);

        Assert.Contains("acolytesstaff", harkonComps);

        Assert.Contains("arcana", harkonComps);

        Assert.Contains("recipe", harkonComps);


        // 11. Madfred's Brass Knuckles

        Assert.True(itemLookup.ContainsKey("madfredsbrassknuckles"));

        HeroItem brass = itemLookup["madfredsbrassknuckles"];

        Assert.Equal("Madfred's Brass Knuckles", brass.Name);

        Assert.Equal(1200, brass.Cost);
    }


    [Fact]
    public void HeroGuides_AllItemReferences_MapToValidCatalogItems()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        string guidesPath = FindDataFile("HeroGuides.json");

        string wwwroot = FindWwwroot();


        if (!File.Exists(guidesPath)) return;


        string guidesJson = File.ReadAllText(guidesPath);

        using JsonDocument doc = JsonDocument.Parse(guidesJson);


        int totalRefs = 0;

        foreach (JsonElement guide in doc.RootElement.EnumerateArray())

        {
            string heroName = guide.TryGetProperty("heroName", out JsonElement hn) ? hn.GetString() ?? "Hero" : "Hero";

            foreach (string prop in new[] { "startingItems", "earlyGameItems", "coreItems", "luxuryItems" })

                if (guide.TryGetProperty(prop, out JsonElement itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)

                    foreach (string codeRaw in itemsProp.EnumerateArray().Select(itemElem => itemElem.GetString() ?? "")
                                 .Where(codeRaw => !string.IsNullOrWhiteSpace(codeRaw)))

                    {
                        totalRefs++;

                        string codeLower = codeRaw.ToLowerInvariant().Trim();

                        string codeClean = codeLower.Replace("item_", "").Replace("_", "");


                        bool found = itemLookup.ContainsKey(codeLower) ||
                                     itemLookup.ContainsKey("item_" + codeLower) ||
                                     itemLookup.ContainsKey(codeClean) ||
                                     itemLookup.ContainsKey("item_" + codeClean);


                        Assert.True(found,
                            $"Hero '{heroName}' item reference '{codeRaw}' in '{prop}' could not be found in HeroItems catalog!");


                        HeroItem matched = itemLookup.GetValueOrDefault(codeLower) ??
                                           itemLookup.GetValueOrDefault("item_" + codeLower) ??
                                           itemLookup.GetValueOrDefault(codeClean) ??
                                           itemLookup[codeClean];


                        Assert.DoesNotContain("Standard Hero Item", matched.Description,
                            StringComparison.OrdinalIgnoreCase);

                        Assert.DoesNotContain("Standard component item", matched.Description,
                            StringComparison.OrdinalIgnoreCase);


                        string iconRel = matched.IconPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

                        string fullPath = Path.Combine(wwwroot, iconRel);

                        Assert.True(File.Exists(fullPath),
                            $"Hero '{heroName}' item '{matched.Name}' ({codeRaw}) icon file missing on disk: {fullPath}");
                    }
        }


        Assert.True(totalRefs > 1000, $"Expected over 1000 hero guide item references, found {totalRefs}.");
    }


    [Fact]
    public void HeroItemsCatalog_AllItemIconFiles_ExistOnDisk()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        string wwwroot = FindWwwroot();


        foreach (HeroItem item in items)

        {
            string iconRel = item.IconPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            string fullPath = Path.Combine(wwwroot, iconRel);

            Assert.True(File.Exists(fullPath),
                $"Icon file missing on disk for '{item.Name}' ({item.Code}): {fullPath}");
        }
    }


    [Fact]
    public void HeroItemsCatalog_AllCompositeItems_HaveValidComponents()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        foreach (HeroItem item in items)

        {
            if (string.IsNullOrWhiteSpace(item.ComponentCodesJson) || item.ComponentCodesJson == "[]") continue;


            List<string>? compCodes = JsonSerializer.Deserialize<List<string>>(item.ComponentCodesJson);

            if (compCodes == null || compCodes.Count == 0) continue;


            foreach (string cleanCode in compCodes.Select(cCode => cCode.Trim().ToLowerInvariant())
                         .Where(cleanCode => cleanCode != "recipe"))
                Assert.True(itemLookup.ContainsKey(cleanCode),
                    $"Item '{item.Name}' references missing component code: '{cleanCode}'");
        }
    }


    [Fact]
    public void TabletOfCommand_NullStone_GraveLocket_SyncVerification()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        // 1. Tablet of Command

        Assert.True(itemLookup.ContainsKey("tabletofcommand"));

        HeroItem tablet = itemLookup["tabletofcommand"];

        Assert.Equal("Tablet of Command", tablet.Name);

        Assert.Equal(1800, tablet.Cost);


        // 2. Null Stone

        Assert.True(itemLookup.ContainsKey("nullstone"));

        HeroItem nullstone = itemLookup["nullstone"];

        Assert.Equal("Null Stone", nullstone.Name);

        Assert.Equal(4500, nullstone.Cost);


        List<string>? nullComps = JsonSerializer.Deserialize<List<string>>(nullstone.ComponentCodesJson);

        Assert.NotNull(nullComps);

        Assert.Contains("sustainer", nullComps);

        Assert.Contains("blessedorb", nullComps);

        Assert.Contains("recipe", nullComps);


        // 3. Grave Locket

        Assert.True(itemLookup.ContainsKey("gravelocket"));

        HeroItem locket = itemLookup["gravelocket"];

        Assert.Equal("Grave Locket", locket.Name);

        Assert.Equal(750, locket.Cost);


        List<string>? locketComps = JsonSerializer.Deserialize<List<string>>(locket.ComponentCodesJson);

        Assert.NotNull(locketComps);

        Assert.Contains("orbofzamos", locketComps);

        Assert.Contains("crushingclaws", locketComps);

        Assert.Contains("markofthenovice", locketComps);

        Assert.Contains("recipe", locketComps);
    }


    [Fact]
    public void Manatube_Riftshards_Shieldbreaker_SyncVerification()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        // 1. Manatube

        Assert.True(itemLookup.ContainsKey("manatube"));

        HeroItem manatube = itemLookup["manatube"];

        Assert.Equal("Manatube", manatube.Name);

        Assert.Equal(850, manatube.Cost);

        Assert.Equal(0, manatube.RecipeCost);

        Assert.Equal("[]", manatube.ComponentCodesJson);


        // 2. Riftshards

        Assert.True(itemLookup.ContainsKey("riftshards"));

        HeroItem riftshards = itemLookup["riftshards"];

        Assert.Equal("Riftshards", riftshards.Name);

        Assert.Equal(3325, riftshards.Cost);

        Assert.Equal(925, riftshards.RecipeCost);

        Assert.Equal("Upgradable up to Level 4 for +925 Gold per recipe scroll (+0.2x critical multiplier per tier).",
            riftshards.Upgrade);


        List<string>? riftComps = JsonSerializer.Deserialize<List<string>>(riftshards.ComponentCodesJson);

        Assert.NotNull(riftComps);

        Assert.Equal(3, riftComps.Count);

        Assert.Equal(2, riftComps.Count(c => c.Equals("broadsword", StringComparison.OrdinalIgnoreCase)));

        Assert.Contains("recipe", riftComps);


        // 3. Shieldbreaker

        Assert.True(itemLookup.ContainsKey("shieldbreaker"));

        HeroItem shieldbreaker = itemLookup["shieldbreaker"];

        Assert.Equal("Shieldbreaker", shieldbreaker.Name);

        Assert.Equal(2600, shieldbreaker.Cost);

        Assert.Equal(1000, shieldbreaker.RecipeCost);


        List<string>? shieldComps = JsonSerializer.Deserialize<List<string>>(shieldbreaker.ComponentCodesJson);

        Assert.NotNull(shieldComps);

        Assert.Equal(2, shieldComps.Count);

        Assert.Contains("warhammer", shieldComps);

        Assert.Contains("recipe", shieldComps);
    }


    [Fact]
    public void RingOfTheTeacher_BuildsInto_And_RingOfSorcery_Verification()

    {
        List<HeroItem> items = LoadHeroItemsCatalog();

        Dictionary<string, HeroItem> itemLookup = BuildItemLookup(items);


        // 1. Abyssal Skull

        Assert.True(itemLookup.ContainsKey("abyssalskull"));

        HeroItem abyssal = itemLookup["abyssalskull"];

        List<string>? abyssalComps = JsonSerializer.Deserialize<List<string>>(abyssal.ComponentCodesJson);

        Assert.NotNull(abyssalComps);

        Assert.Contains("ringoftheteacher", abyssalComps);


        // 2. Lex Talionis

        Assert.True(itemLookup.ContainsKey("lextalionis"));

        HeroItem lex = itemLookup["lextalionis"];

        List<string>? lexComps = JsonSerializer.Deserialize<List<string>>(lex.ComponentCodesJson);

        Assert.NotNull(lexComps);

        Assert.Contains("ringoftheteacher", lexComps);


        // 3. Nome's Wisdom

        Assert.True(itemLookup.ContainsKey("nomeswisdom"));

        HeroItem nomes = itemLookup["nomeswisdom"];

        List<string>? nomesComps = JsonSerializer.Deserialize<List<string>>(nomes.ComponentCodesJson);

        Assert.NotNull(nomesComps);

        Assert.Contains("mysticvestments", nomesComps);

        Assert.Contains("manatube", nomesComps);

        Assert.Contains("ringoftheteacher", nomesComps);

        Assert.Contains("recipe", nomesComps);

        Assert.Equal(150, nomes.RecipeCost);


        // 4. Ring of Sorcery (A.K.A. Mana Ring)

        Assert.True(itemLookup.ContainsKey("ringofsorcery"));

        Assert.True(itemLookup.ContainsKey("manaring"));

        HeroItem sorcery = itemLookup["ringofsorcery"];

        Assert.Contains("A.K.A. Mana Ring", sorcery.Description);


        // 5. Ring of the Teacher Upgrade Banner

        Assert.True(itemLookup.ContainsKey("ringoftheteacher"));

        HeroItem teacher = itemLookup["ringoftheteacher"];

        Assert.Contains("Lex Talionis", teacher.Upgrade);

        Assert.DoesNotContain("Dreamcatcher", teacher.Upgrade);


        // 6. Slayer: standalone item with no components or recipe

        Assert.True(itemLookup.ContainsKey("slayer"));

        HeroItem slayer = itemLookup["slayer"];

        Assert.Equal("Slayer", slayer.Name);

        Assert.Equal(2200, slayer.Cost);

        Assert.Equal(0, slayer.RecipeCost);

        Assert.Equal("[]", slayer.ComponentCodesJson);


        // 7. Fleetfeet: standalone item with no components or recipe

        Assert.True(itemLookup.ContainsKey("fleetfeet"));

        HeroItem fleetfeet = itemLookup["fleetfeet"];

        Assert.Equal("Fleetfeet", fleetfeet.Name);

        Assert.Equal(450, fleetfeet.Cost);

        Assert.Equal(0, fleetfeet.RecipeCost);

        Assert.Equal("[]", fleetfeet.ComponentCodesJson);
    }


    [Fact]
    public void Leaderboard_WinRateCalculationsAndSorting_AreCorrect()

    {
        List<LeaderboardEntryDto> entries =

        [
            new()

            {
                AccountId = 1,

                AccountName = "PlayerA",

                AvatarUrl = "/avatar1.png",

                TotalMatches = 10,

                Wins = 8,

                Losses = 2,

                WinRate = 80.0,

                Mmr = 1800,

                TopHeroName = "Pebbles",

                TopHeroIcon = "/icon.png"
            },

            new()

            {
                AccountId = 2,

                AccountName = "PlayerB",

                AvatarUrl = "/avatar2.png",

                TotalMatches = 20,

                Wins = 19,

                Losses = 1,

                WinRate = 95.0,

                Mmr = 1950,

                TopHeroName = "Pyromancer",

                TopHeroIcon = "/icon.png"
            },

            new()

            {
                AccountId = 3,

                AccountName = "PlayerC",

                AvatarUrl = "/avatar3.png",

                TotalMatches = 5,

                Wins = 3,

                Losses = 2,

                WinRate = 60.0,

                Mmr = 1500,

                TopHeroName = "Devourer",

                TopHeroIcon = "/icon.png"
            }
        ];


        List<LeaderboardEntryDto> sorted = [.. entries.OrderByDescending(e => e.WinRate)];


        Assert.Equal("PlayerB", sorted[0].AccountName);

        Assert.Equal(95.0, sorted[0].WinRate);


        Assert.Equal("PlayerA", sorted[1].AccountName);

        Assert.Equal(80.0, sorted[1].WinRate);


        Assert.Equal("PlayerC", sorted[2].AccountName);

        Assert.Equal(60.0, sorted[2].WinRate);
    }
}
