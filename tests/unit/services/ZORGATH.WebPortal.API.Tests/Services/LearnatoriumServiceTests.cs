// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LearnatoriumServiceTests.cs
// System: ZORGATH Unit Tests - Learnatorium Replay Playback & Guide Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Learnatorium;
using Xunit;
using ZORGATH.WebPortal.API.Services.Learnatorium;

namespace ZORGATH.WebPortal.API.Tests.Services;

public sealed class LearnatoriumServiceTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(databaseName: $"LearnatoriumDb_{Guid.NewGuid()}")
            .Options;
        return new MerrickContext(options);
    }

    [Fact]
    public async Task GetReplayPlaybackAsync_ReturnsChronologicalFrames_AndFull10Players()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        LearnatoriumService service = new(db, NullLogger<LearnatoriumService>.Instance);

        // Act
        LearnatoriumReplayPlaybackDto? playback = await service.GetReplayPlaybackAsync(15984210);

        // Assert
        playback.Should().NotBeNull();
        playback!.MatchID.Should().Be(15984210);
        playback.Map.Should().Be("caldavar");
        playback.Players.Should().HaveCount(10);

        // Verify Legion and Hellbourne separation
        List<PlaybackHeroPositionDto> legion = playback.Players.Where(p => p.Team == 1).ToList();
        List<PlaybackHeroPositionDto> hellbourne = playback.Players.Where(p => p.Team == 2).ToList();
        legion.Should().HaveCount(5);
        hellbourne.Should().HaveCount(5);

        // Verify frames
        playback.Frames.Should().NotBeEmpty();
        PlaybackFrameDto firstFrame = playback.Frames[0];
        firstFrame.Heroes.Should().HaveCount(10);

        // At frame 0, Legion heroes should be near bottom-left base (X < 0.35, Y < 0.35)
        foreach (PlaybackHeroPositionDto lh in firstFrame.Heroes.Where(h => h.Team == 1))
        {
            lh.PositionX.Should().BeLessThan(0.35f);
            lh.PositionY.Should().BeLessThan(0.35f);
        }

        // At frame 0, Hellbourne heroes should be near top-right base (X > 0.65, Y > 0.65)
        foreach (PlaybackHeroPositionDto hh in firstFrame.Heroes.Where(h => h.Team == 2))
        {
            hh.PositionX.Should().BeGreaterThan(0.65f);
            hh.PositionY.Should().BeGreaterThan(0.65f);
        }

        // Verify Highlights
        playback.Highlights.Should().NotBeEmpty();
        playback.Highlights.Should().Contain(h => h.EventType == "FIRST_BLOOD");
        playback.Highlights.Should().Contain(h => h.EventType == "KONGOR");
        playback.Highlights.Should().Contain(h => h.EventType == "VICTORY");
    }

    [Fact]
    public async Task GetHeroGuideAsync_GeneratesCompleteLevelProgression_AndItemBuilds()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        LearnatoriumService service = new(db, NullLogger<LearnatoriumService>.Instance);

        // Act
        LearnatoriumHeroGuideDto? guide = await service.GetHeroGuideAsync("Hero_Pyromancer");

        // Assert
        guide.Should().NotBeNull();
        guide!.HeroIdentifier.Should().Be("Hero_Pyromancer");
        guide.SkillBuildOrder.Should().HaveCount(25);
        guide.CoreItems.Should().NotBeEmpty();
        guide.Tips.Should().NotBeEmpty();
        guide.WinRate.Should().BeGreaterThan(50.0);
    }

    [Fact]
    public async Task GetCombatLogAsync_ReturnsHighlightsChronologically()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        LearnatoriumService service = new(db, NullLogger<LearnatoriumService>.Instance);

        // Act
        IReadOnlyList<PlaybackCombatHighlightDto> combatLog = await service.GetCombatLogAsync(15984210);

        // Assert
        combatLog.Should().NotBeEmpty();
        combatLog.Should().BeInAscendingOrder(h => h.MatchTimeSeconds);
    }

    [Fact]
    public async Task GetReplayPlaybackAsync_MidWars_ReturnsCanonical10MidWarsHeroes_TransmutensteinBoss_AndSingleLaneStructures()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        LearnatoriumService service = new(db, NullLogger<LearnatoriumService>.Instance);

        // Act
        LearnatoriumReplayPlaybackDto? playback = await service.GetReplayPlaybackAsync(2060568725);

        // Assert
        playback.Should().NotBeNull();
        playback!.MatchID.Should().Be(2060568725);
        playback.Map.Should().Be("midwars");
        playback.GameMode.Should().Be("Mid Wars (All Pick)");
        playback.Players.Should().HaveCount(10);

        // Verify Mid Wars Canonical Heroes: Bubbles, Valkyrie, Armadon, Pebbles, Glacius vs Wretched Hag, Flint, Devourer, Deadwood, Demented Shaman
        playback.Players.Select(p => p.HeroIdentifier).Should().Contain(new[]
        {
            "Hero_Bubbles", "Hero_Valkyrie", "Hero_Armadon", "Hero_Pebbles", "Hero_Glacius",
            "Hero_WretchedHag", "Hero_FlintBeastwood", "Hero_Devourer", "Hero_Deadwood", "Hero_DementedShaman"
        });

        // Verify Mid Wars Highlights: Transmutenstein, First Blood, Genocide
        playback.Highlights.Should().Contain(h => h.EventType == "TRANSMUTENSTEIN");
        playback.Highlights.Should().Contain(h => h.EventType == "FIRST_BLOOD");
        playback.Highlights.Should().Contain(h => h.EventType == "GENOCIDE");

        // Verify Single-Lane Towers
        playback.Frames.Should().NotBeEmpty();
        PlaybackFrameDto firstFrame = playback.Frames[0];
        firstFrame.Structures.Should().ContainKey("legion_t1_mid");
        firstFrame.Structures.Should().ContainKey("hb_t1_mid");
        firstFrame.Structures.Should().NotContainKey("hb_t1_top"); // No side towers in Mid Wars

        // Verify Transmutenstein pit diversion contest around second 660
        PlaybackFrameDto? pitFrame = playback.Frames.FirstOrDefault(f => f.Second == 660);
        pitFrame.Should().NotBeNull();
        PlaybackHeroPositionDto? bubbles = pitFrame!.Heroes.FirstOrDefault(h => h.Slot == 0);
        bubbles.Should().NotBeNull();
        bubbles!.PositionX.Should().BeInRange(0.48f, 0.57f);
        bubbles.PositionY.Should().BeInRange(0.75f, 0.84f);
    }
}
