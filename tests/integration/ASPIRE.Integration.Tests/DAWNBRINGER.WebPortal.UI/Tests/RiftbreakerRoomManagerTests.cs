// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Reflection;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class RiftbreakerRoomManagerTests
{
    [Fact]
    public void RiftbreakerRoomManager_JoinAndReady_TransitionsToPlayingInActOne()
    {
        RiftbreakerRoomManager manager = new(NullLogger<RiftbreakerRoomManager>.Instance);

        var (room1, slot1, started1) = manager.JoinRoom("rift01", "c-1", "ChronoFan");
        Assert.Equal("RIFT01", room1.RoomCode);
        Assert.Equal(1, slot1);
        Assert.False(started1);
        Assert.Equal(RiftbreakerMatchStatus.WaitingForOpponent, room1.Status);

        var (room2, slot2, started2) = manager.JoinRoom("rift01", "c-2", "BeheMain");
        Assert.Equal(2, slot2);
        Assert.True(started2);
        Assert.Equal(RiftbreakerMatchStatus.WaitingForReady, room2.Status);

        var (_, ready1) = manager.SetPlayerReady("RIFT01", "c-1", wantsAr: false);
        Assert.False(ready1);

        var (activeRoom, bothReady) = manager.SetPlayerReady("RIFT01", "c-2", wantsAr: true);
        Assert.True(bothReady);
        Assert.NotNull(activeRoom);
        Assert.Equal(RiftbreakerMatchStatus.Playing, activeRoom.Status);
        Assert.Equal(1, activeRoom.Act);
    }

    [Fact]
    public void RiftbreakerRoomManager_FourActProgression_TriggersHeroDraftCardDraftBossAndVictory()
    {
        RiftbreakerRoomManager manager = new(NullLogger<RiftbreakerRoomManager>.Instance);
        manager.JoinRoom("ESCA", "c-1", "P1");
        manager.JoinRoom("ESCA", "c-2", "P2");
        manager.SetPlayerReady("ESCA", "c-1", false);
        manager.SetPlayerReady("ESCA", "c-2", false);

        // Point 1 (Act I)
        var r1 = manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        Assert.False(r1.TriggeredHeroDraft);
        Assert.Equal(1, r1.Room!.Score1);

        // Point 2 -> triggers Hero Soul-Bind Draft
        var r2 = manager.RegisterScore("ESCA", 2, winningThreshold: 11, debounceMs: 0);
        Assert.True(r2.TriggeredHeroDraft);
        Assert.Equal(RiftbreakerMatchStatus.HeroDraft, r2.Room!.Status);
        Assert.Equal(2, r2.Room.Act);

        // Selecting both heroes resumes Playing in Act II
        var h1 = manager.SelectHero("ESCA", "c-1", "chronos");
        Assert.False(h1.BothSelected);
        Assert.Equal("chronos", h1.CanonicalHero);

        var h2 = manager.SelectHero("ESCA", "c-2", "behemoth");
        Assert.True(h2.BothSelected);
        Assert.Equal(RiftbreakerMatchStatus.Playing, h2.Room!.Status);

        // Score P1 up to 6 -> triggers Act III Card Draft
        for (int i = 0; i < 4; i++)
        {
            manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        }
        var rCard = manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        Assert.Equal(6, rCard.Room!.Score1);
        Assert.True(rCard.TriggeredCardDraft);
        Assert.Equal(RiftbreakerMatchStatus.CardDraft, rCard.Room.Status);
        Assert.Equal(3, rCard.Room.Act);

        var cardPick = manager.SelectRiftCard("ESCA", "c-2", "portal_sidelines");
        Assert.True(cardPick.Accepted);
        Assert.Contains("portal_sidelines", cardPick.Room!.ActiveRiftCards);
        Assert.Equal(RiftbreakerMatchStatus.Playing, cardPick.Room.Status);

        // Advance to 9 -> triggers Act IV Kongor Overdrive
        manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        var rBoss = manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        Assert.Equal(9, rBoss.Room!.Score1);
        Assert.True(rBoss.TriggeredBoss);
        Assert.Equal(4, rBoss.Room.Act);
        Assert.False(rBoss.MatchWon);

        // Score to 11 -> Match Won
        manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        var rWin = manager.RegisterScore("ESCA", 1, winningThreshold: 11, debounceMs: 0);
        Assert.True(rWin.MatchWon);
        Assert.Equal(1, rWin.WinnerSlot);
        Assert.Equal(RiftbreakerMatchStatus.Finished, rWin.Room!.Status);
    }

    [Fact]
    public void RiftbreakerRoomManager_InvalidSelectionsAndDebounce_AreRejectedSafely()
    {
        RiftbreakerRoomManager manager = new(NullLogger<RiftbreakerRoomManager>.Instance);
        manager.JoinRoom("GUARD", "c-1", "P1");
        manager.JoinRoom("GUARD", "c-2", "P2");
        manager.SetPlayerReady("GUARD", "c-1", false);
        manager.SetPlayerReady("GUARD", "c-2", false);

        var invalidHero = manager.SelectHero("GUARD", "c-1", "invalid_hero");
        Assert.Null(invalidHero.CanonicalHero);

        var invalidCard = manager.SelectRiftCard("GUARD", "c-1", "invalid_card");
        Assert.False(invalidCard.Accepted);

        var first = manager.RegisterScore("GUARD", 1, winningThreshold: 11, debounceMs: 5000);
        Assert.Equal(1, first.Room!.Score1);

        // Immediate duplicate within 5000ms debounce window should be ignored
        var dup = manager.RegisterScore("GUARD", 1, winningThreshold: 11, debounceMs: 5000);
        Assert.Equal(1, dup.Room!.Score1);
    }

    [Fact]
    public void RiftbreakerRoomManager_NormalizeCode_SanitizesAndDefaultsToRift()
    {
        Assert.Equal("RIFT", RiftbreakerRoomManager.NormalizeCode(null));
        Assert.Equal("RIFT", RiftbreakerRoomManager.NormalizeCode("   "));
        Assert.Equal("AB12CD34", RiftbreakerRoomManager.NormalizeCode("ab-12! cd34999"));
    }

    [Fact]
    public void RiftbreakerRoomManager_ResetAndLeave_PreservesCleanState()
    {
        RiftbreakerRoomManager manager = new(NullLogger<RiftbreakerRoomManager>.Instance);
        manager.JoinRoom("RESET", "c-1", "P1");
        manager.JoinRoom("RESET", "c-2", "P2");
        manager.SetPlayerReady("RESET", "c-1", false);
        manager.SetPlayerReady("RESET", "c-2", false);
        manager.SelectHero("RESET", "c-1", "flux");
        manager.RegisterScore("RESET", 1, debounceMs: 0);

        var reset = manager.ResetMatch("RESET", keepHeroes: true);
        Assert.NotNull(reset);
        Assert.Equal(0, reset.Score1);
        Assert.Equal(2, reset.Act);
        Assert.Equal("flux", reset.Player1?.SelectedHero);
        Assert.Equal(RiftbreakerMatchStatus.WaitingForReady, reset.Status);

        var (leftRoom, vacatedSlot) = manager.Leave("c-2");
        Assert.Equal(2, vacatedSlot);
        Assert.NotNull(leftRoom);
        Assert.Equal(RiftbreakerMatchStatus.WaitingForOpponent, leftRoom.Status);
    }

    [Fact]
    public void RiftbreakerHub_WhileParkedForFutureDevelopment_RequiresPortalAuthorization()
    {
        Type hubType = typeof(RiftbreakerHub);
        Assert.NotNull(hubType.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(hubType.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
