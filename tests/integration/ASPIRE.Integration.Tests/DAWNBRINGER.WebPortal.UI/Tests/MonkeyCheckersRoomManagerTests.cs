// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class MonkeyCheckersRoomManagerTests
{
    [Fact]
    public void MonkeyCheckersRoomManager_JoinRoom_FirstPlayerBecomesSlot1Legion()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        var (room, assignedSlot, bothInRoom) = manager.JoinRoom("TEST", "conn-1", "WukongHost");

        Assert.NotNull(room);
        Assert.Equal("TEST", room.RoomCode);
        Assert.Equal(1, assignedSlot);
        Assert.False(bothInRoom);
        Assert.Equal(CheckersMatchStatus.WaitingForOpponent, room.Status);
        Assert.NotNull(room.Player1);
        Assert.Equal("WukongHost", room.Player1.Name);
        Assert.Equal(1, room.Player1.Slot);
        Assert.Null(room.Player2);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_JoinRoom_SecondPlayerBecomesSlot2HellbourneAndTriggersReadyCheck()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("DUEL", "conn-1", "P1_Legion");
        var (room, assignedSlot, bothInRoom) = manager.JoinRoom("DUEL", "conn-2", "P2_Hellbourne");

        Assert.Equal(2, assignedSlot);
        Assert.True(bothInRoom);
        Assert.Equal(CheckersMatchStatus.WaitingForReady, room.Status);
        Assert.NotNull(room.Player1);
        Assert.NotNull(room.Player2);
        Assert.True(room.IsFull);
        Assert.False(room.Player1.IsReady);
        Assert.False(room.Player2.IsReady);

        // Player 1 confirms ready
        var (_, p1Ready) = manager.SetPlayerReady("DUEL", "conn-1", wantsAr: true);
        Assert.False(p1Ready);
        Assert.Equal(CheckersMatchStatus.WaitingForReady, room.Status);
        Assert.True(room.Player1.IsReady);
        Assert.True(room.Player1.WantsAr);

        // Player 2 confirms ready -> match starts!
        var (_, bothReady) = manager.SetPlayerReady("DUEL", "conn-2", wantsAr: false);
        Assert.True(bothReady);
        Assert.Equal(CheckersMatchStatus.Playing, room.Status);
        Assert.True(room.Player2.IsReady);
        Assert.False(room.Player2.WantsAr);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_JoinRoom_SamePlayerReconnectingMaintainsSlot()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("RECON", "conn-1", "MonkeyKing", "session-token-1");
        var (room, assignedSlot, _) = manager.JoinRoom("RECON", "conn-1-new", "MonkeyKingUpdated", "session-token-1");

        Assert.Equal(1, assignedSlot);
        Assert.Equal("MonkeyKingUpdated", room.Player1?.Name);
        Assert.Equal("conn-1-new", room.Player1?.ConnectionId);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_RegisterMove_AlternatesTurnAndValidatesTurnOwnership()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("TURN", "c1", "P1");
        manager.JoinRoom("TURN", "c2", "P2");
        manager.SetPlayerReady("TURN", "c1", false);
        manager.SetPlayerReady("TURN", "c2", false);

        CheckersRoom? room = manager.GetRoom("TURN");
        Assert.NotNull(room);
        Assert.Equal("legion", room.CurrentTurn);

        // Player 2 attempts to move first (out of turn) -> should be rejected!
        var (_, p2Invalid, _) = manager.RegisterMove("TURN", "c2", 5, 1, 4, 0, false);
        Assert.False(p2Invalid);

        // Player 1 moves (valid Legion turn) -> succeeds and swaps turn to Hellbourne!
        var (_, p1Valid, nextTurn1) = manager.RegisterMove("TURN", "c1", 2, 0, 3, 1, false);
        Assert.True(p1Valid);
        Assert.Equal("hellbourne", nextTurn1);
        Assert.Equal("hellbourne", room.CurrentTurn);

        // Player 2 moves (valid Hellbourne turn) -> succeeds and swaps turn to Legion!
        var (_, p2Valid, nextTurn2) = manager.RegisterMove("TURN", "c2", 5, 1, 4, 0, false);
        Assert.True(p2Valid);
        Assert.Equal("legion", nextTurn2);
        Assert.Equal("legion", room.CurrentTurn);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_RegisterMove_MultiJumpKeepsTurn()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("JUMP", "c1", "P1");
        manager.JoinRoom("JUMP", "c2", "P2");
        manager.SetPlayerReady("JUMP", "c1", false);
        manager.SetPlayerReady("JUMP", "c2", false);

        // A jump move where isJump=true can alternate turn
        var (_, valid, nextTurn) = manager.RegisterMove("JUMP", "c1", 2, 1, 4, 3, true);
        Assert.True(valid);
        Assert.Equal("hellbourne", nextTurn);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_ResetMatch_ClearsWinnerAndSetsStatusWaitingForReady()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("REMATCH", "c1", "P1");
        manager.JoinRoom("REMATCH", "c2", "P2");
        manager.SetPlayerReady("REMATCH", "c1", false);
        manager.SetPlayerReady("REMATCH", "c2", false);

        CheckersRoom? resetRoom = manager.ResetMatch("REMATCH");
        Assert.NotNull(resetRoom);
        Assert.Null(resetRoom.WinnerSlot);
        Assert.Equal("legion", resetRoom.CurrentTurn);
        Assert.Equal(CheckersMatchStatus.WaitingForReady, resetRoom.Status);
        Assert.False(resetRoom.Player1?.IsReady);
        Assert.False(resetRoom.Player2?.IsReady);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_Leave_VacatesSlotAndPurgesWhenEmpty()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        manager.JoinRoom("VACATE", "c1", "P1");
        manager.JoinRoom("VACATE", "c2", "P2");

        var (roomAfterP1Leave, vacatedSlot) = manager.Leave("c1");
        Assert.NotNull(roomAfterP1Leave);
        Assert.Equal(1, vacatedSlot);
        Assert.Null(roomAfterP1Leave.Player1);
        Assert.NotNull(roomAfterP1Leave.Player2);
        Assert.Equal(CheckersMatchStatus.WaitingForOpponent, roomAfterP1Leave.Status);

        // When second player leaves, room is purged
        var (roomAfterP2Leave, vacatedSlot2) = manager.Leave("c2");
        Assert.Equal(2, vacatedSlot2);
        Assert.Null(manager.GetRoom("VACATE"));
    }

    [Fact]
    public void JoinRoom_ThirdPlayerAttemptingToJoin_ReturnsSlotZeroAndRejects()
    {
        MonkeyCheckersRoomManager manager = new(NullLogger<MonkeyCheckersRoomManager>.Instance);
        var (room1, slot1, started1) = manager.JoinRoom("FULLTEST", "conn-1", "Player1");
        var (room2, slot2, started2) = manager.JoinRoom("FULLTEST", "conn-2", "Player2");

        Assert.Equal(1, slot1);
        Assert.Equal(2, slot2);
        Assert.True(started2);
        Assert.True(room2.IsFull);

        // 3rd person attempts to join
        var (room3, slot3, started3) = manager.JoinRoom("FULLTEST", "conn-3", "Player3_Intruder");

        Assert.NotNull(room3);
        Assert.Equal(0, slot3); // Rejected!
        Assert.False(started3);
        Assert.Equal("Player1", room3.Player1?.Name);
        Assert.Equal("Player2", room3.Player2?.Name);

        // If player 3 tries to leave, they are not registered in the room map
        var (leaveRoom, vacatedSlot) = manager.Leave("conn-3");
        Assert.Null(leaveRoom);
        Assert.Equal(0, vacatedSlot);
    }

    [Fact]
    public void MonkeyCheckersRoomManager_NormalizeCode_NormalizesInputCorrectly()
    {
        Assert.StartsWith("MK", MonkeyCheckersRoomManager.NormalizeCode(null));
        Assert.StartsWith("MK", MonkeyCheckersRoomManager.NormalizeCode(""));
        Assert.StartsWith("MK", MonkeyCheckersRoomManager.NormalizeCode("   "));
        Assert.Equal("MK42", MonkeyCheckersRoomManager.NormalizeCode("mk42"));
        Assert.Equal("WAR99", MonkeyCheckersRoomManager.NormalizeCode("war-99!"));
    }
}
