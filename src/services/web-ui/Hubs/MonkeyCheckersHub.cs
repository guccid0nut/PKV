// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

[Authorize]
public class MonkeyCheckersHub(
    IMonkeyCheckersRoomManager roomManager,
    ILogger<MonkeyCheckersHub> logger) : Hub<IMonkeyCheckersClient>
{
    public static string GetRoomGroup(string roomCode) => $"Checkers_{MonkeyCheckersRoomManager.NormalizeCode(roomCode)}";

    public async Task JoinRoom(string roomCode, string playerName, string? playerId = null)
    {
        string group = GetRoomGroup(roomCode);

        string effectivePlayerName = string.IsNullOrWhiteSpace(playerName)
            ? $"MonkeyKing_{Context.ConnectionId[..4]}"
            : playerName.Trim();

        var (room, assignedSlot, bothInRoom) = roomManager.JoinRoom(roomCode, Context.ConnectionId, effectivePlayerName, playerId);

        if (assignedSlot == 0)
        {
            logger.LogWarning("[CheckersHub] Connection {ConnId} ({PlayerName}) rejected: Room {RoomCode} is full (2/2)",
                Context.ConnectionId, effectivePlayerName, room.RoomCode);

            await Clients.Caller.OnRoomFull(room.RoomCode,
                $"Room \"{room.RoomCode}\" is full (2/2 players). Please create or join another room!");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        logger.LogInformation("[CheckersHub] Connection {ConnId} joined Room {RoomCode} as Slot {Slot} (BothInRoom: {BothInRoom})",
            Context.ConnectionId, room.RoomCode, assignedSlot, bothInRoom);

        await Clients.Caller.OnRoomJoined(
            room.RoomCode,
            assignedSlot,
            room.Player1?.Name ?? "",
            room.Player2?.Name ?? "",
            room.Status.ToString()
        );

        // Notify other player that opponent joined
        await Clients.OthersInGroup(group).OnOpponentJoined(assignedSlot, effectivePlayerName);

        if ((bothInRoom || room.Status == CheckersMatchStatus.WaitingForReady) && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[CheckersHub] Room {RoomCode} is full (2/2). Triggering Pre-Match Ready Prompt!", room.RoomCode);
            await Clients.Group(group).OnMatchPromptReady();
        }

        // Synchronize existing player readiness to caller if reconnected
        if (room.Player1?.IsReady == true)
        {
            await Clients.Caller.OnPlayerReadyStatus(1, true, room.Player1.WantsAr);
        }
        if (room.Player2?.IsReady == true)
        {
            await Clients.Caller.OnPlayerReadyStatus(2, true, room.Player2.WantsAr);
        }
    }

    public async Task SetReady(string roomCode, bool wantsAr)
    {
        string group = GetRoomGroup(roomCode);
        logger.LogInformation("[CheckersHub] SetReady invoked: Room={RoomCode}, ConnId={ConnId}, WantsAr={WantsAr}",
            roomCode, Context.ConnectionId, wantsAr);

        var (room, bothReady) = roomManager.SetPlayerReady(roomCode, Context.ConnectionId, wantsAr);
        if (room == null)
        {
            logger.LogWarning("[CheckersHub] SetReady failed: room {RoomCode} not found for conn {ConnId}", roomCode, Context.ConnectionId);
            return;
        }

        int slot = room.Player1?.ConnectionId == Context.ConnectionId ? 1 : (room.Player2?.ConnectionId == Context.ConnectionId ? 2 : 0);
        if (slot > 0)
        {
            await Clients.Group(group).OnPlayerReadyStatus(slot, true, wantsAr);
        }

        if (bothReady && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[CheckersHub] Both players ready in {RoomCode}! Starting match (P1: {P1}, P2: {P2})",
                roomCode, room.Player1.Name, room.Player2.Name);
            await Clients.Group(group).OnMatchStarted(room.Player1.Name, room.Player2.Name, room.CurrentTurn);
        }
    }

    public async Task SendMove(string roomCode, int fromRow, int fromCol, int toRow, int toCol, bool isJump)
    {
        string group = GetRoomGroup(roomCode);
        var (room, isValid, nextTurn) = roomManager.RegisterMove(roomCode, Context.ConnectionId, fromRow, fromCol, toRow, toCol, isJump);

        if (!isValid || room == null)
        {
            logger.LogWarning("[CheckersHub] Invalid move rejected in {RoomCode} from {ConnId}", roomCode, Context.ConnectionId);
            return;
        }

        logger.LogInformation("[CheckersHub] Move in {RoomCode}: ({FromR},{FromC}) -> ({ToR},{ToC}), isJump={IsJump}, nextTurn={NextTurn}",
            roomCode, fromRow, fromCol, toRow, toCol, isJump, nextTurn);

        await Clients.OthersInGroup(group).OnOpponentMove(fromRow, fromCol, toRow, toCol, isJump);
    }

    public async Task SendTaunt(string roomCode, string tauntId)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentTaunt(tauntId);
    }

    public async Task ResetMatch(string roomCode)
    {
        string group = GetRoomGroup(roomCode);
        CheckersRoom? room = roomManager.ResetMatch(roomCode);
        if (room != null)
        {
            logger.LogInformation("[CheckersHub] Match reset in {RoomCode}", roomCode);
            await Clients.Group(group).OnMatchReset();
            if (room.Status == CheckersMatchStatus.WaitingForReady && room.Player1 != null && room.Player2 != null)
            {
                await Clients.Group(group).OnMatchPromptReady();
            }
        }
    }

    public async Task LeaveRoom(string roomCode)
    {
        string group = GetRoomGroup(roomCode);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

        var (room, vacatedSlot) = roomManager.Leave(Context.ConnectionId);
        if (vacatedSlot > 0)
        {
            logger.LogInformation("[CheckersHub] Slot {Slot} left room {RoomCode}", vacatedSlot, roomCode);
            await Clients.OthersInGroup(group).OnOpponentLeft(vacatedSlot);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (room, vacatedSlot) = roomManager.Leave(Context.ConnectionId);
        if (room != null && vacatedSlot > 0)
        {
            string group = GetRoomGroup(room.RoomCode);
            await Clients.OthersInGroup(group).OnOpponentLeft(vacatedSlot);
            logger.LogInformation("[CheckersHub] Connection {ConnId} disconnected from room {RoomCode}, vacated slot {Slot}",
                Context.ConnectionId, room.RoomCode, vacatedSlot);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
