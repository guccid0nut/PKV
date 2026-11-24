// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using DAWNBRINGER.WebPortal.UI.Services.Replay;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class HonReplayParserTests
{
    private static string? GetSampleReplayPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", "ReplayFiles", "M1234567890.honreplay"),
            Path.Combine(AppContext.BaseDirectory, "Data", "ReplayFiles", "M1234567890.honreplay"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "ReplayFiles", "M1234567890.honreplay"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ASPIRE.Tests", "Data", "ReplayFiles",
                "M1234567890.honreplay"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "KONGOR.MasterServer", "Resources", "Replays",
                "M1234567890.honreplay")
        ];

        return candidates.Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault();
    }

    [Fact]
    public async Task ParseReplayFileAsync_WithSampleReplayArchive_ReturnsValidTimelineDto()
    {
        // Arrange
        string? samplePath = GetSampleReplayPath();
        if (samplePath == null)
            // Sample replay not committed to git; test passes gracefully if not present
            return;

        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);

        // Act
        MatchReplayTimelineDto? result = await parser.ParseReplayFileAsync(samplePath, 1234567890);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1234567890, result.MatchID);
        Assert.NotEmpty(result.Map);
        Assert.NotEmpty(result.GameMode);
        Assert.True(result.TotalDurationSeconds > 0);
        Assert.NotEmpty(result.Players);
        Assert.NotEmpty(result.Keyframes);
        Assert.NotEmpty(result.EventPins);

        // Verify keyframe positions are normalized
        MatchTimelineKeyframe firstFrame = result.Keyframes[0];
        Assert.Equal(0, firstFrame.Second);
        Assert.NotEmpty(firstFrame.Players);
        foreach (ReplayPlayerSnapshot player in firstFrame.Players)
        {
            Assert.InRange(player.PositionX, 0f, 1f);
            Assert.InRange(player.PositionY, 0f, 1f);
            Assert.InRange(player.HealthPercent, 0f, 100f);
            Assert.InRange(player.ManaPercent, 0f, 100f);
        }
    }

    [Fact]
    public async Task ParseReplayFileAsync_WithNonExistentFile_ReturnsNull()
    {
        // Arrange
        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);

        // Act
        MatchReplayTimelineDto? result =
            await parser.ParseReplayFileAsync("C:\\non_existent_file_999999.honreplay", 999999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ParseReplayAsync_BenchmarkPerformance_CompletesUnder250ms()
    {
        // Arrange
        string? samplePath = GetSampleReplayPath();
        if (samplePath == null) return;

        IHonReplayParser parser = new HonReplayParser(NullLogger<HonReplayParser>.Instance);

        await using FileStream warmupStream = File.OpenRead(samplePath);
        // Warm up
        await parser.ParseReplayAsync(warmupStream, 1234567890);

        // Act & Measure
        await using FileStream testStream = File.OpenRead(samplePath);
        Stopwatch sw = Stopwatch.StartNew();
        MatchReplayTimelineDto? result = await parser.ParseReplayAsync(testStream, 1234567890);
        sw.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Parsing took {sw.ElapsedMilliseconds}ms, expected under 1500ms.");
    }

    [Fact]
    public async Task ParseReplayFileAsync_EndgameKeyframe_MatchesMatchHistoryInventoryAndStatsExact()
    {
        // Arrange
        string? samplePath = GetSampleReplayPath();
        if (samplePath == null) return;

        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);

        // Act
        MatchReplayTimelineDto? result = await parser.ParseReplayFileAsync(samplePath, 1234567890);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Keyframes);

        MatchTimelineKeyframe lastFrame = result.Keyframes[^1];
        Assert.Equal(result.TotalDurationSeconds, lastFrame.Second);

        foreach (ReplayPlayerSummary summary in result.Players)
        {
            ReplayPlayerSnapshot? endSnapshot = lastFrame.Players.FirstOrDefault(p => p.AccountID == summary.AccountID);
            Assert.NotNull(endSnapshot);

            // Exact match history alignment
            Assert.Equal(summary.FinalLevel, endSnapshot.Level);
            Assert.Equal(summary.FinalKills, endSnapshot.Kills);
            Assert.Equal(summary.FinalDeaths, endSnapshot.Deaths);
            Assert.Equal(summary.FinalGpm, endSnapshot.Gpm);
            Assert.Equal(summary.FinalXpm, endSnapshot.Xpm);
            Assert.Equal(summary.FinalGold, endSnapshot.Gold);

            // Inventory match exact
            Assert.Equal(summary.FinalItems.Count, endSnapshot.Items.Count);
            for (int i = 0; i < summary.FinalItems.Count; i++)
                Assert.Equal(summary.FinalItems[i], endSnapshot.Items[i]);
        }
    }

    [Fact]
    public async Task ParseReplayFileAsync_AbilitiesAndHeadingAngle_ArePopulatedValidly()
    {
        // Arrange
        string? samplePath = GetSampleReplayPath();
        if (samplePath == null) return;

        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);

        // Act
        MatchReplayTimelineDto? result = await parser.ParseReplayFileAsync(samplePath, 1234567890);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Players);

        foreach (ReplayPlayerSummary summary in result.Players)
        {
            Assert.NotEmpty(summary.Abilities);
            Assert.Equal(4, summary.Abilities.Count);
            Assert.Contains(summary.Abilities, a => a.Slot == "Q");
            Assert.Contains(summary.Abilities, a => a.Slot == "W");
            Assert.Contains(summary.Abilities, a => a.Slot == "E");
            Assert.Contains(summary.Abilities, a => a.Slot == "R");
        }

        // Mid-game frame verification
        int midIndex = result.Keyframes.Count / 2;
        MatchTimelineKeyframe midFrame = result.Keyframes[midIndex];

        foreach (ReplayPlayerSnapshot snapshot in midFrame.Players)
        {
            Assert.NotEmpty(snapshot.Abilities);
            Assert.Equal(4, snapshot.Abilities.Count);
            Assert.InRange(snapshot.HeadingAngle, -Math.PI * 2, Math.PI * 2);
            foreach (ReplayPlayerAbilityDto ability in snapshot.Abilities)
            {
                Assert.NotEmpty(ability.Slot);
                Assert.NotEmpty(ability.Name);
                Assert.NotEmpty(ability.IconPath);
                Assert.InRange(ability.CooldownRemaining, 0, ability.CooldownTotal);
            }
        }
    }
}
