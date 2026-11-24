// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class KongorChessRoomManagerTests
{
    [Fact]
    public void ChessRoomManager_JoinRoom_FirstPlayerBecomesSlot1Legion()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        var (room, assignedSlot, bothInRoom) = manager.JoinRoom("TEST", "conn-1", "KongorGrandmaster");

        Assert.NotNull(room);
        Assert.Equal("TEST", room.RoomCode);
        Assert.Equal(1, assignedSlot);
        Assert.False(bothInRoom);
        Assert.Equal(ChessMatchStatus.WaitingForOpponent, room.Status);
        Assert.NotNull(room.Player1);
        Assert.Equal("KongorGrandmaster", room.Player1.Name);
        Assert.Equal(1, room.Player1.Slot);
        Assert.Null(room.Player2);
    }

    [Fact]
    public void ChessRoomManager_JoinRoom_SecondPlayerBecomesSlot2HellbourneAndTriggersReadyCheck()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        manager.JoinRoom("DUEL", "conn-1", "P1_White");
        var (room, assignedSlot, bothInRoom) = manager.JoinRoom("DUEL", "conn-2", "P2_Black");

        Assert.Equal(2, assignedSlot);
        Assert.True(bothInRoom);
        Assert.Equal(ChessMatchStatus.WaitingForReady, room.Status);
        Assert.NotNull(room.Player1);
        Assert.NotNull(room.Player2);
        Assert.True(room.IsFull);
        Assert.False(room.Player1.IsReady);
        Assert.False(room.Player2.IsReady);

        // Player 1 confirms ready
        var (_, p1Ready) = manager.SetPlayerReady("DUEL", "conn-1", wantsAr: true);
        Assert.False(p1Ready);
        Assert.Equal(ChessMatchStatus.WaitingForReady, room.Status);
        Assert.True(room.Player1.IsReady);
        Assert.True(room.Player1.WantsAr);

        // Player 2 confirms ready -> match starts!
        var (_, bothReady) = manager.SetPlayerReady("DUEL", "conn-2", wantsAr: false);
        Assert.True(bothReady);
        Assert.Equal(ChessMatchStatus.Playing, room.Status);
        Assert.True(room.Player2.IsReady);
        Assert.False(room.Player2.WantsAr);
        Assert.Equal("w", room.CurrentTurn);
    }

    [Fact]
    public void ChessRoomManager_JoinRoom_SamePlayerReconnectingMaintainsSlot()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        manager.JoinRoom("RECON", "conn-1", "LegionKing", "player-token-1");
        var (room, assignedSlot, _) = manager.JoinRoom("RECON", "conn-1-new", "LegionKingUpdated", "player-token-1");

        Assert.Equal(1, assignedSlot);
        Assert.Equal("LegionKingUpdated", room.Player1?.Name);
        Assert.Equal("conn-1-new", room.Player1?.ConnectionId);
    }

    [Fact]
    public void ChessRoomManager_RegisterMove_AlternatesTurnAndValidatesTurnOwnership()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        manager.JoinRoom("PLAY", "conn-1", "P1_White");
        manager.JoinRoom("PLAY", "conn-2", "P2_Black");
        manager.SetPlayerReady("PLAY", "conn-1", false);
        manager.SetPlayerReady("PLAY", "conn-2", false);

        // Illegal: Black tries to move on White's turn
        var (_, p2Illegal, _, _, _, _) = manager.RegisterMove("PLAY", "conn-2", "e7", "e5", null, "fen2");
        Assert.False(p2Illegal);

        // Legal: White moves e2 -> e4
        var (room, p1Legal, nextTurn, _, _, _) = manager.RegisterMove("PLAY", "conn-1", "e2", "e4", null, "fen_e4");
        Assert.True(p1Legal);
        Assert.NotNull(room);
        Assert.Equal("b", nextTurn);
        Assert.Equal("b", room.CurrentTurn);

        // Legal: Black moves e7 -> e5
        var (_, p2Legal, nextTurn2, _, _, _) = manager.RegisterMove("PLAY", "conn-2", "e7", "e5", null, "fen_e5");
        Assert.True(p2Legal);
        Assert.Equal("w", nextTurn2);
    }

    [Fact]
    public void ChessRoomManager_Forfeit_EndsGameWithOpponentAsWinner()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        manager.JoinRoom("RAGE", "conn-1", "P1_White");
        manager.JoinRoom("RAGE", "conn-2", "P2_Black");
        manager.SetPlayerReady("RAGE", "conn-1", false);
        manager.SetPlayerReady("RAGE", "conn-2", false);

        var (room, winnerSlot, reason) = manager.Forfeit("RAGE", "conn-1");
        Assert.NotNull(room);
        Assert.Equal(2, winnerSlot);
        Assert.Equal("Resignation", reason);
        Assert.Equal(ChessMatchStatus.Finished, room.Status);
    }

    [Fact]
    public void ChessRoomManager_ResetMatch_ClearsBoardStateAndReadiness()
    {
        ChessRoomManager manager = new(NullLogger<ChessRoomManager>.Instance);
        manager.JoinRoom("RESET", "conn-1", "P1_White");
        manager.JoinRoom("RESET", "conn-2", "P2_Black");
        manager.SetPlayerReady("RESET", "conn-1", false);
        manager.SetPlayerReady("RESET", "conn-2", false);

        ChessRoom? reset = manager.ResetMatch("RESET");
        Assert.NotNull(reset);
        Assert.Equal(ChessMatchStatus.WaitingForReady, reset.Status);
        Assert.False(reset.Player1?.IsReady);
        Assert.False(reset.Player2?.IsReady);
        Assert.Equal("w", reset.CurrentTurn);
    }
}
