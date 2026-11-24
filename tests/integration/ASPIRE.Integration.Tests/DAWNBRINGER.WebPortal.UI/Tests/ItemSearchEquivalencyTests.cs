// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class ItemSearchEquivalencyTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    [Theory]
    [InlineData("pk", "portalkey", "Portal Key")]
    [InlineData("bkb", "shrunkenhead", "Shrunken Head")]
    [InlineData("mkb", "savagemace", "Savage Mace")]
    [InlineData("bf", "runedcleaver", "Runed Cleaver")]
    [InlineData("sny", "frostburn", "Frostburn")]
    [InlineData("ac", "daemonicbreastplate", "Daemonic Breastplate")]
    [InlineData("hex", "kuldrassheepstick", "Kuldra's Sheepstick")]
    [InlineData("sheepstick", "kuldrassheepstick", "Kuldra's Sheepstick")]
    [InlineData("deso", "shieldbreaker", "Shieldbreaker")]
    [InlineData("crit", "riftshards", "Riftshards")]
    [InlineData("daed", "riftshards", "Riftshards")]
    [InlineData("rad", "mockofbrilliance", "Mock of Brilliance")]
    [InlineData("heart", "behemothsheart", "Behemoth's Heart")]
    [InlineData("skadi", "frostwolfsskull", "Frostwolf's Skull")]
    [InlineData("eul", "stormspirit", "Stormspirit")]
    [InlineData("orchid", "hellflower", "Hellflower")]
    [InlineData("diffusal", "nullfireblade", "Nullfire Blade")]
    [InlineData("armlet", "insanitarius", "Insanitarius")]
    [InlineData("satanic", "symbolofrage", "Symbol of Rage")]
    [InlineData("vlads", "abyssalskull", "Abyssal Skull")]
    [InlineData("necro", "puzzlebox", "Puzzlebox")]
    [InlineData("aghs", "staffofthemaster", "Staff of the Master")]
    [InlineData("bot", "posthaste", "Post Haste")]
    [InlineData("phase", "ghostmarchers", "Ghost Marchers")]
    [InlineData("treads", "steamboots", "Steamboots")]
    [InlineData("wand", "powersupply", "Power Supply")]
    [InlineData("stick", "manabattery", "Mana Battery")]
    [InlineData("tango", "blightstones", "Blight Stones")]
    [InlineData("salve", "healthpotion", "Health Potion")]
    [InlineData("clarity", "manapotion", "Mana Potion")]
    [InlineData("gem", "boundeye", "Bound Eye")]
    [InlineData("dust", "dustofrevelation", "Dust of Revelation")]
    [InlineData("sentry", "wardofrevelation", "Ward of Revelation")]
    [InlineData("obs", "wardofsight", "Ward of Sight")]
    [InlineData("tp", "homecomingstone", "Homecoming Stone")]
    [InlineData("shiva", "frostfieldplate", "Frostfield Plate")]
    [InlineData("refresher", "restorationstone", "Restoration Stone")]
    [InlineData("rapier", "doombringer", "Doom Bringer")]
    [InlineData("mom", "elderparasite", "Elder Parasite")]
    [InlineData("halberd", "halberd", "Halberd")]
    [InlineData("bloodstone", "sacrificialstone", "Sacrificial Stone")]
    [InlineData("medallion", "solsbulwark", "Sol's Bulwark")]
    [InlineData("urn", "gravelocket", "Grave Locket")]
    [InlineData("soulring", "bloodchalice", "Blood Chalice")]
    [InlineData("hood", "shamansheaddress", "Shaman's Headdress")]
    [InlineData("vanguard", "helmoftheblacklegion", "Helm of the Black Legion")]
    [InlineData("drums", "energizer", "Energizer")]
    [InlineData("basilius", "ringoftheteacher", "Ring of the Teacher")]
    [InlineData("aquila", "ringoftheteacher", "Ring of the Teacher")]
    [InlineData("arcanering", "ringofsorcery", "Ring of Sorcery")]
    [InlineData("qb", "loggershatchet", "Logger's Hatchet")]
    [InlineData("bracer", "fortifiedbracer", "Fortified Bracer")]
    [InlineData("wraithband", "soulscream", "Soulscream Ring")]
    [InlineData("null", "amuletofexile", "Amulet of Exile")]
    [InlineData("veil", "grimoireofpower", "Grimoire of Power")]
    [InlineData("aetherlens", "jadespire", "Jade Spire")]
    [InlineData("smoke", "veiledrot", "Veiled Rot")]
    [InlineData("aegis", "tokenoflife", "Token of Life")]
    [InlineData("echosabre", "twinblades", "Twin Blades")]
    [InlineData("aeondisk", "adevescloak", "Adeve's Cloak")]
    [InlineData("mango", "lunartear", "Lunar Tear")]
    [InlineData("faeriefire", "goldenapple", "Golden Apple")]
    [InlineData("raindrops", "nihilcrystal", "Nihil Crystal")]
    [InlineData("windlace", "windwhistle", "Wind Whistle")]
    [InlineData("fluffyhat", "bloodruby", "Blood Ruby")]
    public void MatchesSearch_And_GetSearchScore_Resolve_DotaAndAbbreviations(string query, string itemCode,
        string itemName)
    {
        bool matches = HonItemMapping.MatchesSearch(itemCode, itemName, query);
        int score = HonItemMapping.GetSearchScore(itemCode, itemName, query);

        Assert.True(matches, $"Query '{query}' should match item '{itemName}' (code: '{itemCode}')");
        Assert.True(score >= 60, $"Query '{query}' score for '{itemName}' should be at least 60, got {score}");
    }

    [Theory]
    [InlineData("qss", "shrunkenhead", "Shrunken Head")]
    [InlineData("quicksilver sash", "shrunkenhead", "Shrunken Head")]
    [InlineData("flash", "portalkey", "Portal Key")]
    [InlineData("rocketbelt", "portalkey", "Portal Key")]
    [InlineData("thornmail", "barbedarmor", "Barbed Armor")]
    [InlineData("bramble vest", "barbedarmor", "Barbed Armor")]
    [InlineData("zhonya", "voidtalisman", "Void Talisman")]
    [InlineData("stopwatch", "voidtalisman", "Void Talisman")]
    [InlineData("warmog", "behemothsheart", "Behemoth's Heart")]
    [InlineData("warmogs", "behemothsheart", "Behemoth's Heart")]
    [InlineData("sunfire", "mockofbrilliance", "Mock of Brilliance")]
    [InlineData("tiamat", "runedcleaver", "Runed Cleaver")]
    [InlineData("hydra", "runedcleaver", "Runed Cleaver")]
    [InlineData("infinity edge", "riftshards", "Riftshards")]
    [InlineData("ie", "riftshards", "Riftshards")]
    [InlineData("the black cleaver", "shieldbreaker", "Shieldbreaker")]
    [InlineData("last whisper", "shieldbreaker", "Shieldbreaker")]
    [InlineData("botrk", "nullfireblade", "Nullfire Blade")]
    [InlineData("blade of the ruined king", "nullfireblade", "Nullfire Blade")]
    [InlineData("bloodthirster", "symbolofrage", "Symbol of Rage")]
    [InlineData("bt", "symbolofrage", "Symbol of Rage")]
    [InlineData("guardian angel", "tokenoflife", "Token of Life")]
    [InlineData("ga", "tokenoflife", "Token of Life")]
    [InlineData("shurelya", "energizer", "Energizer")]
    [InlineData("dfg", "hellflower", "Hellflower")]
    [InlineData("deathfire grasp", "hellflower", "Hellflower")]
    [InlineData("statikk shiv", "chargedhammer", "Charged Hammer")]
    public void MatchesSearch_And_GetSearchScore_Resolve_LeagueOfLegendsAliases(string query, string itemCode,
        string itemName)
    {
        bool matches = HonItemMapping.MatchesSearch(itemCode, itemName, query);
        int score = HonItemMapping.GetSearchScore(itemCode, itemName, query);

        Assert.True(matches, $"Query '{query}' should match item '{itemName}' (code: '{itemCode}')");
        Assert.True(score >= 60, $"Query '{query}' score for '{itemName}' should be at least 60, got {score}");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GlobalSearch_WithItemAbbreviation_ReturnsItemResult_WithDirectLink()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        MerrickContext context = CreateInMemoryContext();

        // Seed Portal Key, Shrunken Head, Barbed Armor in DB
        context.HeroItems.AddRange(
            new HeroItem
            {
                Code = "Item_PortalKey",
                Name = "Portal Key",
                Cost = 2150,
                Category = "Initiation",
                Description = "Teleports the user up to 1200 units away.",
                Flavor = "A key to planar portals.",
                IconPath = "/images/items/portalkey.png"
            },
            new HeroItem
            {
                Code = "Item_Immunity",
                Name = "Shrunken Head",
                Cost = 3900,
                Category = "Protective",
                Description = "Grants Magic Immunity.",
                Flavor = "A potent ward against magic.",
                IconPath = "/images/items/shrunkenhead.png"
            },
            new HeroItem
            {
                Code = "Item_Excruciator",
                Name = "Barbed Armor",
                Cost = 2200,
                Category = "Protective",
                Description = "Returns damage.",
                Flavor = "Spiked armor.",
                IconPath = "/images/items/barbedarmor.png"
            }
        );
        await context.SaveChangesAsync();

        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        // 1. Search for "pk" -> should return Portal Key
        IActionResult resultPk = await controller.SearchGlobal("pk");
        JsonResult jsonPk = Assert.IsType<JsonResult>(resultPk);
        List<object>? listPk = jsonPk.Value as List<object>;
        Assert.NotNull(listPk);
        Assert.NotEmpty(listPk);

        string jsonStringPk = JsonSerializer.Serialize(listPk[0]);
        using JsonDocument docPk = JsonDocument.Parse(jsonStringPk);
        string typePk = docPk.RootElement.GetProperty("type").GetString() ?? "";
        string namePk = docPk.RootElement.GetProperty("name").GetString() ?? "";
        string urlPk = docPk.RootElement.GetProperty("url").GetString() ?? "";
        string badgePk = docPk.RootElement.GetProperty("badge").GetString() ?? "";

        Assert.Equal("item", typePk);
        Assert.Equal("Portal Key", namePk);
        Assert.Contains("portalkey", urlPk, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ITEM", badgePk);

        // 2. Search for "bkb" -> should return Shrunken Head
        IActionResult resultBkb = await controller.SearchGlobal("bkb");
        JsonResult jsonBkb = Assert.IsType<JsonResult>(resultBkb);
        List<object>? listBkb = jsonBkb.Value as List<object>;
        Assert.NotNull(listBkb);
        Assert.NotEmpty(listBkb);

        string jsonStringBkb = JsonSerializer.Serialize(listBkb[0]);
        using JsonDocument docBkb = JsonDocument.Parse(jsonStringBkb);
        string typeBkb = docBkb.RootElement.GetProperty("type").GetString() ?? "";
        string nameBkb = docBkb.RootElement.GetProperty("name").GetString() ?? "";
        string urlBkb = docBkb.RootElement.GetProperty("url").GetString() ?? "";

        Assert.Equal("item", typeBkb);
        Assert.Equal("Shrunken Head", nameBkb);
        Assert.Contains("shrunkenhead", urlBkb, StringComparison.OrdinalIgnoreCase);

        // 3. Search for "thornmail" -> should return Barbed Armor
        IActionResult resultThorn = await controller.SearchGlobal("thornmail");
        JsonResult jsonThorn = Assert.IsType<JsonResult>(resultThorn);
        List<object>? listThorn = jsonThorn.Value as List<object>;
        Assert.NotNull(listThorn);
        Assert.NotEmpty(listThorn);

        string jsonStringThorn = JsonSerializer.Serialize(listThorn[0]);
        using JsonDocument docThorn = JsonDocument.Parse(jsonStringThorn);
        string nameThorn = docThorn.RootElement.GetProperty("name").GetString() ?? "";
        Assert.Equal("Barbed Armor", nameThorn);
    }

    [Fact]
    public async Task SearchGlobal_FindsLeaderboardPage_WhenSearchingTrophy()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        IActionResult result = await controller.SearchGlobal("trophy");
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        List<object>? list = jsonResult.Value as List<object>;
        Assert.NotNull(list);
        Assert.NotEmpty(list);

        string json = JsonSerializer.Serialize(list);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool hasLeaderboard = false;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (el.GetProperty("name").GetString() == "Leaderboard" &&
                el.GetProperty("type").GetString() == "page" &&
                el.GetProperty("url").GetString() == "/leaderboard")
            {
                hasLeaderboard = true;
                break;
            }
        }
        Assert.True(hasLeaderboard, "Global search for 'trophy' must return the Leaderboard page.");
    }

    [Fact]
    public async Task SearchGlobal_CorrectlyRanksStorQuery_ElevatingPrefixAndStoreOverSubstring()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        IActionResult result = await controller.SearchGlobal("stor");
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        List<object>? list = jsonResult.Value as List<object>;
        Assert.NotNull(list);
        Assert.NotEmpty(list);

        string json = JsonSerializer.Serialize(list);
        using JsonDocument doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();

        // 1. Top item should be Vanity Store or Stormspirit (prefix/word-start matches)
        string firstItemName = items[0].GetProperty("name").GetString() ?? "";
        Assert.True(firstItemName == "Vanity Store" || firstItemName == "Stormspirit",
            $"Expected Vanity Store or Stormspirit at rank 1, but got {firstItemName}");

        // 2. If Restoration Stone is present, its score must be lower than Stormspirit / Store
        int stormspiritIndex = items.FindIndex(i => i.GetProperty("name").GetString() == "Stormspirit");
        int restorationIndex = items.FindIndex(i => i.GetProperty("name").GetString() == "Restoration Stone");
        if (stormspiritIndex >= 0 && restorationIndex >= 0)
        {
            Assert.True(stormspiritIndex < restorationIndex,
                $"Stormspirit (index {stormspiritIndex}) should outrank Restoration Stone (index {restorationIndex})");
        }
    }

    [Fact]
    public async Task SearchGlobal_ResolvesCorrectHeroIconAndUrl_ForDevourer()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        IActionResult result = await controller.SearchGlobal("devou");
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        List<object>? list = jsonResult.Value as List<object>;
        Assert.NotNull(list);
        Assert.NotEmpty(list);

        string json = JsonSerializer.Serialize(list);
        using JsonDocument doc = JsonDocument.Parse(json);
        var devourer = doc.RootElement.EnumerateArray()
            .FirstOrDefault(el => el.GetProperty("name").GetString() == "Devourer");

        Assert.True(devourer.ValueKind != JsonValueKind.Undefined, "Devourer must be found when searching 'devou'");
        Assert.Equal("hero", devourer.GetProperty("type").GetString());
        Assert.Equal("/codex/Hero_Devourer", devourer.GetProperty("url").GetString());
        // Verify hero icon is the dedicated devourer hero image, NOT the Hammerstorm default or icon_ prefix
        Assert.Contains("devourer.png", devourer.GetProperty("iconUrl").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("icon_devourer.png", devourer.GetProperty("iconUrl").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default.png", devourer.GetProperty("iconUrl").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchGlobal_SearchingSpell_RanksSpellshardsAboveNullstone()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        IActionResult result = await controller.SearchGlobal("spell");
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        List<object>? list = jsonResult.Value as List<object>;
        Assert.NotNull(list);
        Assert.NotEmpty(list);

        string json = JsonSerializer.Serialize(list);
        using JsonDocument doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();

        int spellshardsIndex = items.FindIndex(i => i.GetProperty("name").GetString() == "Spellshards");
        int nullstoneIndex = items.FindIndex(i => i.GetProperty("name").GetString() == "Null Stone");

        Assert.True(spellshardsIndex >= 0, "Spellshards must be present when searching 'spell'");
        if (nullstoneIndex >= 0)
        {
            Assert.True(spellshardsIndex < nullstoneIndex,
                $"Spellshards (index {spellshardsIndex}) must rank higher than Null Stone (index {nullstoneIndex}) for query 'spell'");
        }

        // Verify item URL is singular /item/spellshards
        var spellshardsItem = items[spellshardsIndex];
        Assert.Equal("/item/spellshards", spellshardsItem.GetProperty("url").GetString());
    }

    [Fact]
    public async Task SearchGlobal_ReturnsPlayerDiscordAvatar_WhenAvailable()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "guccidonut@projectkongor.local",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            DiscordId = "100000000000000002",
            DiscordUsername = "guccidonut",
            DiscordAvatar = "donutavatarhash123",
            TimestampCreated = DateTimeOffset.UtcNow,
            TimestampLastActive = DateTimeOffset.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        Account account = new()
        {
            Name = "guccidonut",
            User = user,
            TimestampCreated = DateTimeOffset.UtcNow,
            TimestampLastActive = DateTimeOffset.UtcNow
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        IConfiguration configuration = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(
            statsApi,
            userApi,
            guideApi,
            adminApi,
            configuration,
            null,
            null,
            null,
            NullLogger<HomeController>.Instance
        );

        IActionResult result = await controller.SearchGlobal("gucci");
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        List<object>? list = jsonResult.Value as List<object>;
        Assert.NotNull(list);
        Assert.NotEmpty(list);

        string json = JsonSerializer.Serialize(list);
        using JsonDocument doc = JsonDocument.Parse(json);
        var player = doc.RootElement.EnumerateArray()
            .FirstOrDefault(el => el.GetProperty("name").GetString() == "guccidonut");

        Assert.True(player.ValueKind != JsonValueKind.Undefined, "Player 'guccidonut' must be returned");
        string avatarUrl = player.GetProperty("avatarUrl").GetString() ?? "";
        Assert.Contains("cdn.discordapp.com/avatars/100000000000000002/donutavatarhash123", avatarUrl);
    }
}
