// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class DevoWarsRoomManagerTests
{
    [Fact]
    public void DevoWarsRoomManager_JoinRoom_FirstPlayerBecomesSlot1()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        var (room, assignedSlot, started) = manager.JoinRoom("TEST", "conn-1", "DevoPlayer1");

        Assert.NotNull(room);
        Assert.Equal("TEST", room.RoomCode);
        Assert.Equal(1, assignedSlot);
        Assert.False(started);
        Assert.Equal(DevoMatchStatus.WaitingForOpponent, room.Status);
        Assert.NotNull(room.Player1);
        Assert.Equal("DevoPlayer1", room.Player1.Name);
        Assert.Null(room.Player2);
    }

    [Fact]
    public void DevoWarsRoomManager_JoinRoom_SecondPlayerBecomesSlot2AndTriggersReadyCheck()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("DUEL", "conn-1", "P1");
        var (room, assignedSlot, bothInRoom) = manager.JoinRoom("DUEL", "conn-2", "P2");

        Assert.Equal(2, assignedSlot);
        Assert.True(bothInRoom);
        Assert.Equal(DevoMatchStatus.WaitingForReady, room.Status);
        Assert.NotNull(room.Player1);
        Assert.NotNull(room.Player2);
        Assert.True(room.IsFull);
        Assert.False(room.Player1.IsReady);
        Assert.False(room.Player2.IsReady);

        // Player 1 confirms ready
        var (_, p1Ready) = manager.SetPlayerReady("DUEL", "conn-1", wantsAr: true);
        Assert.False(p1Ready);
        Assert.Equal(DevoMatchStatus.WaitingForReady, room.Status);

        // Player 2 confirms ready -> match starts!
        var (_, bothReady) = manager.SetPlayerReady("DUEL", "conn-2", wantsAr: false);
        Assert.True(bothReady);
        Assert.Equal(DevoMatchStatus.Playing, room.Status);
    }

    [Fact]
    public void DevoWarsRoomManager_JoinRoom_SamePlayerReconnectingMaintainsSlot()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("RECON", "conn-1", "P1");
        var (room, assignedSlot, _) = manager.JoinRoom("RECON", "conn-1", "P1_Updated");

        Assert.Equal(1, assignedSlot);
        Assert.Equal("P1_Updated", room.Player1?.Name);
    }

    [Fact]
    public void DevoWarsRoomManager_RegisterScore_IncrementsScoresAndDeclaresWinnerAtTen()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("ARENA", "c1", "P1");
        manager.JoinRoom("ARENA", "c2", "P2");
        manager.SetPlayerReady("ARENA", "c1", false);
        manager.SetPlayerReady("ARENA", "c2", false);

        for (int i = 1; i <= 9; i++)
        {
            var (room, matchWon, winner) = manager.RegisterScore("ARENA", 1);
            Assert.NotNull(room);
            Assert.False(matchWon);
            Assert.Equal(0, winner);
            Assert.Equal(i, room.Score1);
        }

        // 10th score should trigger victory
        var (finalRoom, won, winnerSlot) = manager.RegisterScore("ARENA", 1);
        Assert.NotNull(finalRoom);
        Assert.True(won);
        Assert.Equal(1, winnerSlot);
        Assert.Equal(10, finalRoom.Score1);
        Assert.Equal(DevoMatchStatus.Finished, finalRoom.Status);
        Assert.Equal(1, finalRoom.WinnerSlot);
    }

    [Fact]
    public void RegisterScore_WithDebounce_PreventsRapidDuplicateScores()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("DEBOUNCE", "c1", "P1");
        manager.JoinRoom("DEBOUNCE", "c2", "P2");
        manager.SetPlayerReady("DEBOUNCE", "c1", false);
        manager.SetPlayerReady("DEBOUNCE", "c2", false);

        // First score should register
        var (room1, won1, _) = manager.RegisterScore("DEBOUNCE", 1, winningThreshold: 10, debounceMs: 1000);
        Assert.NotNull(room1);
        Assert.Equal(1, room1.Score1);
        Assert.False(won1);

        // Immediate second score within 1000ms debounce window should be ignored
        var (room2, won2, _) = manager.RegisterScore("DEBOUNCE", 1, winningThreshold: 10, debounceMs: 1000);
        Assert.NotNull(room2);
        Assert.Equal(1, room2.Score1); // Still 1!
        Assert.False(won2);
    }

    [Fact]
    public void DevoWarsRoomManager_ResetMatch_ClearsScoresAndReactivatesGame()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("REMATCH", "c1", "P1");
        manager.JoinRoom("REMATCH", "c2", "P2");
        manager.SetPlayerReady("REMATCH", "c1", false);
        manager.SetPlayerReady("REMATCH", "c2", false);
        for (int i = 0; i < 10; i++) manager.RegisterScore("REMATCH", 2);

        DevoRoom? resetRoom = manager.ResetMatch("REMATCH");
        Assert.NotNull(resetRoom);
        Assert.Equal(0, resetRoom.Score1);
        Assert.Equal(0, resetRoom.Score2);
        Assert.Null(resetRoom.WinnerSlot);
        Assert.Equal(DevoMatchStatus.WaitingForReady, resetRoom.Status);
    }

    [Fact]
    public void DevoWarsRoomManager_Leave_VacatesSlotAndPurgesWhenEmpty()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("VACATE", "c1", "P1");
        manager.JoinRoom("VACATE", "c2", "P2");

        var (roomAfterP1Leave, vacatedSlot) = manager.Leave("c1");
        Assert.NotNull(roomAfterP1Leave);
        Assert.Equal(1, vacatedSlot);
        Assert.Null(roomAfterP1Leave.Player1);
        Assert.NotNull(roomAfterP1Leave.Player2);
        Assert.Equal(DevoMatchStatus.WaitingForOpponent, roomAfterP1Leave.Status);

        // When second player leaves, room is purged
        var (roomAfterP2Leave, vacatedSlot2) = manager.Leave("c2");
        Assert.Equal(2, vacatedSlot2);
        Assert.Null(manager.GetRoom("VACATE"));
    }

    [Fact]
    public void SetPlayerReady_UnknownConnection_ReturnsFalseAndDoesNotCrash()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        manager.JoinRoom("SAFE", "c1", "P1");
        manager.JoinRoom("SAFE", "c2", "P2");

        var (room, bothReady) = manager.SetPlayerReady("SAFE", "unknown_conn_999", wantsAr: true);
        Assert.NotNull(room);
        Assert.False(bothReady);
        Assert.False(room.Player1!.IsReady);
        Assert.False(room.Player2!.IsReady);
    }

    [Fact]
    public void JoinRoom_ThirdPlayerAttemptingToJoin_ReturnsSlotZeroAndRejects()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        var (room1, slot1, started1) = manager.JoinRoom("FULLTEST", "conn-1", "Player1");
        var (room2, slot2, started2) = manager.JoinRoom("FULLTEST", "conn-2", "Player2");

        Assert.Equal(1, slot1);
        Assert.Equal(2, slot2);
        Assert.True(started2);
        Assert.True(room2.IsFull);

        // 3rd person attempts to join
        var (room3, slot3, started3) = manager.JoinRoom("FULLTEST", "conn-3", "Player3_Intruder");

        Assert.NotNull(room3);
        Assert.Equal(0, slot3); // Rejected
        Assert.False(started3);
        Assert.Equal("Player1", room3.Player1?.Name);
        Assert.Equal("Player2", room3.Player2?.Name);

        // If player 3 tries to leave, they are not registered in the room map
        var (leaveRoom, vacatedSlot) = manager.Leave("conn-3");
        Assert.Null(leaveRoom);
        Assert.Equal(0, vacatedSlot);
    }

    [Fact]
    public void JoinRoom_ReconnectingPlayerWithSameSession_ReclaimsSlotAndUpdatesConnectionId()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        var (room1, slot1, _) = manager.JoinRoom("HOOK", "conn-old", "Gucci", "session-gucci-123");
        Assert.Equal(1, slot1);
        Assert.Equal("conn-old", room1.Player1?.ConnectionId);

        // Player refreshes browser: same session token, new connectionId
        var (room2, slot2, _) = manager.JoinRoom("HOOK", "conn-new", "Gucci", "session-gucci-123");
        Assert.Equal(1, slot2);
        Assert.Equal("conn-new", room2.Player1?.ConnectionId);
        Assert.Null(room2.Player2);
    }

    [Fact]
    public void JoinRoom_TwoDifferentPlayersWithSameDefaultName_AssignsSeparateSlots()
    {
        DevoWarsRoomManager manager = new(NullLogger<DevoWarsRoomManager>.Instance);
        // Player 1 and Player 2 both have default name "Devourer" but distinct session tokens
        var (room1, slot1, started1) = manager.JoinRoom("HOOK", "conn-1", "Devourer", "p-token-1");
        var (room2, slot2, started2) = manager.JoinRoom("HOOK", "conn-2", "Devourer", "p-token-2");

        Assert.Equal(1, slot1);
        Assert.Equal(2, slot2);
        Assert.True(started2);
        Assert.True(room2.IsFull);
        Assert.Equal("Devourer", room2.Player1?.Name);
        Assert.Equal("Devourer", room2.Player2?.Name);
        Assert.Equal("conn-1", room2.Player1?.ConnectionId);
        Assert.Equal("conn-2", room2.Player2?.ConnectionId);
    }

    [Fact]
    public void DevoWarsRoomManager_NormalizeCode_DefaultsToHookWhenEmptyOrWhitespace()
    {
        Assert.Equal("HOOK", DevoWarsRoomManager.NormalizeCode(null));
        Assert.Equal("HOOK", DevoWarsRoomManager.NormalizeCode(""));
        Assert.Equal("HOOK", DevoWarsRoomManager.NormalizeCode("   "));
        Assert.Equal("HOOK", DevoWarsRoomManager.NormalizeCode("hook"));
        Assert.Equal("WAR42", DevoWarsRoomManager.NormalizeCode("war-42!"));
    }
}
