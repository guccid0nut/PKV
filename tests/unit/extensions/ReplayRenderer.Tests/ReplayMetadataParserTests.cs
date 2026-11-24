// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayMetadataParserTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.IO.Compression;
using System.Text;
using ReplayRenderer.Service.Replay;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class ReplayMetadataParserTests(ITestOutputHelper output)
{
    private const string SampleReplayInfoXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<replayinfo version=""4.10.1.0"" gamemode=""Mid Wars"" gameoptions=""forcerandom,norepick,allowduplicate""
            servername=""42243134"" mapname=""midwars"" mapfancyname=""Mid Wars"" teamsize=""1""
            gamename=""Mid Wars 1v1"" matchid=""2060568725"" host=""58"" winner=""hellbourne_team_name""
            date=""08/14/26"" time=""06:08 AM"" matchlength=""1411000"">
  <player name=""ClickBotAlpha"" accountid=""1077"" team=""1"" teamindex=""0"" clientnum=""0""
          heroname=""Hero_BabaYaga"" heroicon=""/heroes/babayaga/icon.tga"" avatar=""""/>
  <player name=""guccidonut"" accountid=""58"" team=""2"" teamindex=""0"" clientnum=""1""
          heroname=""Hero_Bubbles"" heroicon=""/heroes/bubbles/icon.tga"" avatar=""""/>
</replayinfo>";

    private const string FullTenPlayerReplayXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<replayinfo version=""4.10.1.0"" gamemode=""Mid Wars"" gameoptions=""forcerandom,norepick,allowduplicate""
            servername=""Official_Server_99"" mapname=""midwars"" mapfancyname=""Mid Wars"" teamsize=""5""
            gamename=""Mid Wars 5v5 High Excitement"" matchid=""2060999999"" host=""1001"" winner=""1""
            date=""08/29/26"" time=""09:15 PM"" matchlength=""1800000"" concedetime=""0"" firstbloodtime=""65000"">
  <player name=""LegionCarry"" accountid=""101"" team=""1"" teamindex=""0"" clientnum=""0""
          heroname=""Hero_FlintBeastwood"" heroicon=""/heroes/flint_beastwood/icon.tga""
          kills=""15"" deaths=""2"" assists=""10"" herodamage=""45000"" towerdamage=""6000"" creepdamage=""85000""
          gold=""18500"" gpm=""550"" xpm=""620"" level=""25"" denies=""18"" creeps=""210"" actions=""3500"" apm=""116.6""
          wards=""4"" consumables=""8"" smackdowns=""2"" humiliations=""0"" disconnects=""0""/>
  <player name=""LegionMid"" accountid=""102"" team=""1"" teamindex=""1"" clientnum=""1""
          heroname=""Hero_Pyromancer"" heroicon=""/heroes/pyromancer/icon.tga""
          kills=""10"" deaths=""4"" assists=""12"" herodamage=""38000"" towerdamage=""2500"" creepdamage=""45000""
          gold=""14200"" gpm=""430"" xpm=""510"" level=""23"" denies=""8"" creeps=""130"" actions=""3100"" apm=""103.3""
          wards=""2"" consumables=""5"" smackdowns=""0"" humiliations=""0"" disconnects=""0""/>
  <player name=""LegionGank"" accountid=""103"" team=""1"" teamindex=""2"" clientnum=""2""
          heroname=""Hero_Devourer"" heroicon=""/heroes/devourer/icon.tga""
          kills=""8"" deaths=""6"" assists=""18"" herodamage=""32000"" towerdamage=""1200"" creepdamage=""30000""
          gold=""12000"" gpm=""360"" xpm=""450"" level=""21"" denies=""5"" creeps=""85"" actions=""2800"" apm=""93.3""
          wards=""6"" consumables=""6"" smackdowns=""3"" humiliations=""1"" disconnects=""0""/>
  <player name=""LegionOfflane"" accountid=""104"" team=""1"" teamindex=""3"" clientnum=""3""
          heroname=""Hero_Magmus"" heroicon=""/heroes/magmus/icon.tga""
          kills=""4"" deaths=""5"" assists=""22"" herodamage=""26000"" towerdamage=""1800"" creepdamage=""35000""
          gold=""11500"" gpm=""340"" xpm=""420"" level=""20"" denies=""12"" creeps=""95"" actions=""2900"" apm=""96.6""
          wards=""5"" consumables=""7"" smackdowns=""1"" humiliations=""0"" disconnects=""0""/>
  <player name=""LegionSupport"" accountid=""105"" team=""1"" teamindex=""4"" clientnum=""4""
          heroname=""Hero_Glacius"" heroicon=""/heroes/glacius/icon.tga""
          kills=""2"" deaths=""7"" assists=""25"" herodamage=""18000"" towerdamage=""800"" creepdamage=""15000""
          gold=""9500"" gpm=""280"" xpm=""380"" level=""18"" denies=""6"" creeps=""40"" actions=""2600"" apm=""86.6""
          wards=""22"" consumables=""15"" smackdowns=""0"" humiliations=""0"" disconnects=""0""/>

  <player name=""HellbourneCarry"" accountid=""201"" team=""2"" teamindex=""0"" clientnum=""5""
          heroname=""Hero_Chronos"" heroicon=""/heroes/chronos/icon.tga""
          kills=""9"" deaths=""7"" assists=""6"" herodamage=""31000"" towerdamage=""3000"" creepdamage=""75000""
          gold=""15000"" gpm=""460"" xpm=""520"" level=""22"" denies=""14"" creeps=""180"" actions=""3200"" apm=""106.6""
          wards=""1"" consumables=""6"" smackdowns=""1"" humiliations=""0"" disconnects=""0""/>
  <player name=""HellbourneMid"" accountid=""202"" team=""2"" teamindex=""1"" clientnum=""6""
          heroname=""Hero_WretchedHag"" heroicon=""/heroes/wretched_hag/icon.tga""
          kills=""7"" deaths=""8"" assists=""8"" herodamage=""34000"" towerdamage=""1500"" creepdamage=""50000""
          gold=""13500"" gpm=""410"" xpm=""490"" level=""21"" denies=""7"" creeps=""120"" actions=""3400"" apm=""113.3""
          wards=""3"" consumables=""4"" smackdowns=""0"" humiliations=""0"" disconnects=""0""/>
  <player name=""HellbourneGank"" accountid=""203"" team=""2"" teamindex=""2"" clientnum=""7""
          heroname=""Hero_Deadwood"" heroicon=""/heroes/deadwood/icon.tga""
          kills=""5"" deaths=""9"" assists=""7"" herodamage=""24000"" towerdamage=""900"" creepdamage=""28000""
          gold=""10800"" gpm=""330"" xpm=""410"" level=""19"" denies=""4"" creeps=""70"" actions=""2700"" apm=""90.0""
          wards=""4"" consumables=""5"" smackdowns=""1"" humiliations=""0"" disconnects=""0""/>
  <player name=""HellbourneOfflane"" accountid=""204"" team=""2"" teamindex=""3"" clientnum=""8""
          heroname=""Hero_Behemoth"" heroicon=""/heroes/behemoth/icon.tga""
          kills=""2"" deaths=""8"" assists=""12"" herodamage=""21000"" towerdamage=""1100"" creepdamage=""25000""
          gold=""10200"" gpm=""310"" xpm=""390"" level=""18"" denies=""3"" creeps=""60"" actions=""2500"" apm=""83.3""
          wards=""6"" consumables=""6"" smackdowns=""0"" humiliations=""0"" disconnects=""0""/>
  <player name=""HellbourneSupport"" accountid=""205"" team=""2"" teamindex=""4"" clientnum=""9""
          heroname=""Hero_DementedShaman"" heroicon=""/heroes/demented_shaman/icon.tga""
          kills=""1"" deaths=""8"" assists=""15"" herodamage=""15000"" towerdamage=""600"" creepdamage=""12000""
          gold=""8900"" gpm=""260"" xpm=""350"" level=""17"" denies=""9"" creeps=""30"" actions=""2400"" apm=""80.0""
          wards=""18"" consumables=""12"" smackdowns=""0"" humiliations=""0"" disconnects=""0""/>
</replayinfo>";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void ParseFromXml_ValidReplayXml_ExtractsMatchAndPlayerMetadataAccurately()
    {
        _output.WriteLine("[Test] Parsing valid sample replayinfo XML...");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(SampleReplayInfoXml);

        Assert.NotNull(metadata);
        Assert.Equal("4.10.1.0", metadata.Version);
        Assert.Equal("Mid Wars", metadata.GameMode);
        Assert.Equal("forcerandom,norepick,allowduplicate", metadata.GameOptions);
        Assert.Equal("42243134", metadata.ServerName);
        Assert.Equal("midwars", metadata.MapName);
        Assert.Equal("Mid Wars", metadata.MapFancyName);
        Assert.Equal(1, metadata.TeamSize);
        Assert.Equal("Mid Wars 1v1", metadata.GameName);
        Assert.Equal("2060568725", metadata.MatchId);
        Assert.Equal("58", metadata.Host);
        Assert.Equal("hellbourne_team_name", metadata.Winner);
        Assert.Equal("08/14/26", metadata.Date);
        Assert.Equal("06:08 AM", metadata.Time);
        Assert.Equal(1411000L, metadata.MatchLengthMs);
        Assert.Equal(TimeSpan.FromMilliseconds(1411000), metadata.MatchLength);

        Assert.Equal(2, metadata.Players.Count);

        ReplayPlayerMetadata player0 = metadata.Players[0];
        Assert.Equal("ClickBotAlpha", player0.Name);
        Assert.Equal(1077L, player0.AccountId);
        Assert.Equal(1, player0.Team);
        Assert.Equal(0, player0.TeamIndex);
        Assert.Equal(0, player0.ClientNum);
        Assert.Equal("Hero_BabaYaga", player0.HeroName);
        Assert.Equal("/heroes/babayaga/icon.tga", player0.HeroIcon);

        ReplayPlayerMetadata player1 = metadata.Players[1];
        Assert.Equal("guccidonut", player1.Name);
        Assert.Equal(58L, player1.AccountId);
        Assert.Equal(2, player1.Team);
        Assert.Equal(0, player1.TeamIndex);
        Assert.Equal(1, player1.ClientNum);
        Assert.Equal("Hero_Bubbles", player1.HeroName);
        Assert.Equal("/heroes/bubbles/icon.tga", player1.HeroIcon);

        _output.WriteLine(
            $"[Test] Metadata parsed successfully: Match {metadata.MatchId}, Players={metadata.Players.Count}, Duration={metadata.MatchLength.TotalSeconds}s");
    }

    [Theory]
    [InlineData("ClickBotAlpha", 0)]
    [InlineData("clickbotalpha", 0)]
    [InlineData("CLICKBOTALPHA", 0)]
    [InlineData("guccidonut", 1)]
    [InlineData("GucciDonut", 1)]
    [InlineData("GUCCIDONUT", 1)]
    public void FindPlayerByName_CaseInsensitiveMatch_ReturnsCorrectPlayer(string searchName, int expectedClientNum)
    {
        _output.WriteLine($"[Test] Testing FindPlayerByName with query: '{searchName}'");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(SampleReplayInfoXml);
        ReplayPlayerMetadata? player = metadata.FindPlayerByName(searchName);

        Assert.NotNull(player);
        Assert.Equal(expectedClientNum, player.ClientNum);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("ClickBotAlpha", 0)]
    [InlineData("clickbotalpha", 0)]
    [InlineData("guccidonut", 1)]
    public void ResolveClientNum_ValidSlotOrPlayerName_ResolvesCorrectClientNum(string query, int expectedClientNum)
    {
        _output.WriteLine($"[Test] Testing ResolveClientNum with query: '{query}'");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(SampleReplayInfoXml);
        int? clientNum = metadata.ResolveClientNum(query);

        Assert.NotNull(clientNum);
        Assert.Equal(expectedClientNum, clientNum.Value);
    }

    [Theory]
    [InlineData("NonExistentPlayer")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-2")]
    [InlineData("15")]
    public void ResolveClientNum_InvalidSlotOrPlayerName_ReturnsNull(string query)
    {
        _output.WriteLine($"[Test] Testing ResolveClientNum invalid query: '{query}'");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(SampleReplayInfoXml);
        int? clientNum = metadata.ResolveClientNum(query);

        Assert.Null(clientNum);
    }

    [Fact]
    public void ParseFromZipStream_ValidZipArchive_ParsesReplayInfoCorrectly()
    {
        _output.WriteLine("[Test] Creating in-memory zip archive with replayinfo entry...");

        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("replayinfo");
            using Stream entryStream = entry.Open();
            byte[] xmlBytes = Encoding.UTF8.GetBytes(SampleReplayInfoXml);
            entryStream.Write(xmlBytes, 0, xmlBytes.Length);
        }

        ms.Position = 0;
        ReplayMetadata metadata = ReplayMetadataParser.ParseFromZipStream(ms);

        Assert.NotNull(metadata);
        Assert.Equal("2060568725", metadata.MatchId);
        Assert.Equal(2, metadata.Players.Count);
        _output.WriteLine("[Test] In-memory zip archive parsed successfully.");
    }

    [Fact]
    public void ParseFromFile_RealSampleReplayFile_ParsesSuccessfully()
    {
        string candidatePath =
            Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "M2060568725.honreplay");
        if (!File.Exists(candidatePath))
            candidatePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "games", "client",
                "M2060568725.honreplay");

        if (!File.Exists(candidatePath))
        {
            _output.WriteLine($"[Test Skipped] Sample replay file not found at '{candidatePath}'");
            return;
        }

        _output.WriteLine($"[Test] Parsing real sample replay file: {candidatePath}");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromFile(candidatePath);

        Assert.NotNull(metadata);
        Assert.Equal("2060568725", metadata.MatchId);
        Assert.Equal("Mid Wars", metadata.GameMode);
        Assert.Equal(2, metadata.Players.Count);
        Assert.Equal(1411000L, metadata.MatchLengthMs);

        int? slotClickBotAlpha = metadata.ResolveClientNum(metadata.Players[0].Name);
        Assert.Equal(0, slotClickBotAlpha);

        int? slotGuccidonut = metadata.ResolveClientNum("guccidonut");
        Assert.Equal(1, slotGuccidonut);
    }

    [Fact]
    public void ParseFromXml_FullTenPlayerMatch_ExtractsAllRostersAndStatisticsAccurately()
    {
        _output.WriteLine("[Test] Parsing 10-player replay XML with full match statistics...");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(FullTenPlayerReplayXml);

        Assert.NotNull(metadata);
        Assert.Equal("2060999999", metadata.MatchId);
        Assert.Equal("Legion", metadata.WinnerTeamName);
        Assert.Equal(5, metadata.TeamSize);
        Assert.Equal(65000L, metadata.FirstBloodTimeMs);
        Assert.Equal(10, metadata.Players.Count);
        Assert.Equal(5, metadata.LegionPlayers.Count);
        Assert.Equal(5, metadata.HellbournePlayers.Count);

        // Verify Legion Carry (Flint Beastwood)
        ReplayPlayerMetadata flint = metadata.Players[0];
        Assert.Equal("LegionCarry", flint.Name);
        Assert.Equal("Hero_FlintBeastwood", flint.HeroName);
        Assert.Equal("FlintBeastwood", flint.CleanHeroName);
        Assert.Equal(1, flint.Team);
        Assert.Equal("Legion", flint.TeamName);
        Assert.Equal(0, flint.TeamIndex);
        Assert.Equal(0, flint.ClientNum);
        Assert.Equal(15, flint.Kills);
        Assert.Equal(2, flint.Deaths);
        Assert.Equal(10, flint.Assists);
        Assert.Equal("15/2/10", flint.FormattedKda);
        Assert.Equal(45000L, flint.HeroDamage);
        Assert.Equal(6000L, flint.TowerDamage);
        Assert.Equal(85000L, flint.CreepDamage);
        Assert.Equal(18500, flint.Gold);
        Assert.Equal(550, flint.GoldPerMinute);
        Assert.Equal(620, flint.ExpPerMinute);
        Assert.Equal(25, flint.Level);
        Assert.Equal(18, flint.Denies);
        Assert.Equal(210, flint.Creeps);
        Assert.Equal(3500, flint.Actions);
        Assert.Equal(116.6, flint.APM);
        Assert.Equal(4, flint.Wards);
        Assert.Equal(8, flint.Consumables);
        Assert.Equal(2, flint.Smackdowns);

        // Verify Hellbourne Carry (Chronos, Slot 5, Team 2, Index 0)
        ReplayPlayerMetadata chronos = metadata.Players[5];
        Assert.Equal("HellbourneCarry", chronos.Name);
        Assert.Equal("Hero_Chronos", chronos.HeroName);
        Assert.Equal("Chronos", chronos.CleanHeroName);
        Assert.Equal(2, chronos.Team);
        Assert.Equal("Hellbourne", chronos.TeamName);
        Assert.Equal(0, chronos.TeamIndex);
        Assert.Equal(5, chronos.ClientNum);
        Assert.Equal(9, chronos.Kills);
        Assert.Equal(7, chronos.Deaths);
        Assert.Equal(6, chronos.Assists);
        Assert.Equal("9/7/6", chronos.FormattedKda);
        Assert.Equal(460, chronos.GoldPerMinute);

        _output.WriteLine("[Test] Full 10-player match and player statistics verified successfully.");
    }

    [Theory]
    // Exact slot
    [InlineData("0", 0, PlayerMatchConfidence.Exact)]
    [InlineData("5", 5, PlayerMatchConfidence.Exact)]
    [InlineData("9", 9, PlayerMatchConfidence.Exact)]
    // Team slot aliases
    [InlineData("L1", 0, PlayerMatchConfidence.Exact)]
    [InlineData("L2", 1, PlayerMatchConfidence.Exact)]
    [InlineData("L5", 4, PlayerMatchConfidence.Exact)]
    [InlineData("H1", 5, PlayerMatchConfidence.Exact)]
    [InlineData("H3", 7, PlayerMatchConfidence.Exact)]
    [InlineData("H5", 9, PlayerMatchConfidence.Exact)]
    [InlineData("legion:0", 0, PlayerMatchConfidence.Exact)]
    [InlineData("hellbourne:4", 9, PlayerMatchConfidence.Exact)]
    // Exact player name
    [InlineData("LegionCarry", 0, PlayerMatchConfidence.Exact)]
    [InlineData("legionmid", 1, PlayerMatchConfidence.Exact)]
    [InlineData("HellbourneGank", 7, PlayerMatchConfidence.Exact)]
    // Exact hero name (clean or prefixed)
    [InlineData("FlintBeastwood", 0, PlayerMatchConfidence.Exact)]
    [InlineData("Hero_FlintBeastwood", 0, PlayerMatchConfidence.Exact)]
    [InlineData("Devourer", 2, PlayerMatchConfidence.Exact)]
    [InlineData("Hero_Devourer", 2, PlayerMatchConfidence.Exact)]
    [InlineData("Chronos", 5, PlayerMatchConfidence.Exact)]
    [InlineData("Deadwood", 7, PlayerMatchConfidence.Exact)]
    [InlineData("Behemoth", 8, PlayerMatchConfidence.Exact)]
    // Fuzzy player name
    [InlineData("Carry", 0, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Gank", 2, PlayerMatchConfidence.Fuzzy)]
    // Fuzzy hero name
    [InlineData("Flint", 0, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Pyro", 1, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Devo", 2, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Mag", 3, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Glac", 4, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Wretched", 6, PlayerMatchConfidence.Fuzzy)]
    [InlineData("Shaman", 9, PlayerMatchConfidence.Fuzzy)]
    public void ResolvePlayer_VariousQueries_ResolvesExpectedSlotAndConfidence(string query, int expectedSlot,
        PlayerMatchConfidence expectedConfidence)
    {
        _output.WriteLine($"[Test] Testing ResolvePlayer with query: '{query}'");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(FullTenPlayerReplayXml);
        PlayerResolutionResult result = metadata.ResolvePlayer(query);

        Assert.NotNull(result);
        Assert.Equal(expectedSlot, result.ClientIndex);
        Assert.Equal(expectedConfidence, result.Confidence);
        Assert.NotNull(result.Player);
        Assert.Equal(expectedSlot, result.Player!.ClientNum);

        _output.WriteLine($"[Test] Query '{query}' -> {result}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UnknownPlayerName12345")]
    public void ResolvePlayer_UnknownQuery_ReturnsFallbackSlotZero(string query)
    {
        _output.WriteLine($"[Test] Testing ResolvePlayer fallback with query: '{query}'");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(FullTenPlayerReplayXml);
        PlayerResolutionResult result = metadata.ResolvePlayer(query);

        Assert.NotNull(result);
        Assert.Equal(0, result.ClientIndex);
        Assert.Equal(PlayerMatchConfidence.Fallback, result.Confidence);
        _output.WriteLine($"[Test] Query '{query}' successfully fell back: {result}");
    }

    [Fact]
    public void GetRosterSummary_FullMatch_ProducesFormattedAsciiTable()
    {
        _output.WriteLine("[Test] Generating formatted ASCII roster summary...");

        ReplayMetadata metadata = ReplayMetadataParser.ParseFromXml(FullTenPlayerReplayXml);
        string summary = metadata.GetRosterSummary();

        _output.WriteLine("[Test] Formatted Roster:\n" + summary);

        Assert.Contains("MATCH ROSTER", summary);
        Assert.Contains("LEGION (Team 1):", summary);
        Assert.Contains("HELLBOURNE (Team 2):", summary);
        Assert.Contains("LegionCarry", summary);
        Assert.Contains("FlintBeastwood", summary);
        Assert.Contains("HellbourneCarry", summary);
        Assert.Contains("Chronos", summary);
        Assert.Contains("KDA: 15/2/10", summary);
    }
}
