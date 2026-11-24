// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

[Authorize]
public class DevoWarsHub(
    IDevoWarsRoomManager roomManager,
    ILogger<DevoWarsHub> logger) : Hub<IDevoWarsClient>
{
    public static string GetRoomGroup(string roomCode) => $"DevoWars_{DevoWarsRoomManager.NormalizeCode(roomCode)}";

    public async Task JoinRoom(string roomCode, string playerName, string? playerId = null)
    {
        string group = GetRoomGroup(roomCode);

        string effectivePlayerName = string.IsNullOrWhiteSpace(playerName)
            ? $"Player_{Context.ConnectionId[..4]}"
            : playerName.Trim();

        var (room, assignedSlot, started) = roomManager.JoinRoom(roomCode, Context.ConnectionId, effectivePlayerName, playerId);

        if (assignedSlot == 0)
        {
            logger.LogWarning("[DevoHub] Connection {ConnId} ({PlayerName}) rejected: Room {RoomCode} is full (2/2)",
                Context.ConnectionId, effectivePlayerName, room.RoomCode);

            await Clients.Caller.OnRoomFull(room.RoomCode,
                $"Room \"{room.RoomCode}\" is full (2/2 gladiators). Please create another room with a new room code!");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        logger.LogInformation("[DevoHub] Connection {ConnId} joined Room {RoomCode} as Slot {Slot} (Started: {Started})",
            Context.ConnectionId, room.RoomCode, assignedSlot, started);

        await Clients.Caller.OnRoomJoined(
            room.RoomCode,
            assignedSlot,
            room.Player1?.Name ?? "",
            room.Player2?.Name ?? "",
            room.Score1,
            room.Score2,
            room.Status.ToString()
        );

        // Notify other player that opponent joined
        await Clients.OthersInGroup(group).OnOpponentJoined(assignedSlot, effectivePlayerName);

        if ((started || room.Status == DevoMatchStatus.WaitingForReady) && room.Player1 != null && room.Player2 != null)
        {
            await Clients.Group(group).OnMatchPromptReady();
        }

        // Synchronize existing player readiness to caller if reconnected/late
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
        logger.LogInformation("[DevoHub] SetReady invoked: Room={RoomCode}, ConnId={ConnId}, WantsAr={WantsAr}",
            roomCode, Context.ConnectionId, wantsAr);

        var (room, bothReady) = roomManager.SetPlayerReady(roomCode, Context.ConnectionId, wantsAr);
        if (room == null)
        {
            logger.LogWarning("[DevoHub] SetReady failed: room {RoomCode} not found for conn {ConnId}", roomCode, Context.ConnectionId);
            return;
        }

        int slot = room.Player1?.ConnectionId == Context.ConnectionId ? 1 : (room.Player2?.ConnectionId == Context.ConnectionId ? 2 : 0);
        logger.LogInformation("[DevoHub] SetReady resolved: Room={RoomCode}, Slot={Slot}, BothReady={BothReady}",
            room.RoomCode, slot, bothReady);

        if (slot > 0)
        {
            await Clients.Group(group).OnPlayerReadyStatus(slot, true, wantsAr);
        }

        if (bothReady && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[DevoHub] Both players ready in {RoomCode}. Starting match!", roomCode);
            await Clients.Group(group).OnMatchStarted(room.Player1.Name, room.Player2.Name);
        }
    }

    public async Task SendPosition(string roomCode, float x, float z, float rotY, bool isMoving)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentPosition(x, z, rotY, isMoving);
    }

    public async Task SendHook(string roomCode, int throwerSlot, float startX, float startZ, float dirX, float dirZ, float speed)
    {
        string group = GetRoomGroup(roomCode);
        logger.LogDebug("[DevoHub] Hook launched in {RoomCode} by Slot {Slot} from ({StartX},{StartZ}) dir ({DirX},{DirZ})",
            roomCode, throwerSlot, startX, startZ, dirX, dirZ);

        await Clients.OthersInGroup(group).OnHookThrown(throwerSlot, startX, startZ, dirX, dirZ, speed);
    }

    public async Task ReportHit(string roomCode, int scorerSlot, float impactX = 0, float impactZ = 0)
    {
        string group = GetRoomGroup(roomCode);
        DevoRoom? existingRoom = roomManager.GetRoom(roomCode);
        int prevScore1 = existingRoom?.Score1 ?? 0;
        int prevScore2 = existingRoom?.Score2 ?? 0;

        var (room, matchWon, winnerSlot) = roomManager.RegisterScore(roomCode, scorerSlot, winningThreshold: 10, debounceMs: 1200);

        if (room == null) return;

        // If score didn't change (debounced duplicate hit), ignore
        if (room.Score1 == prevScore1 && room.Score2 == prevScore2)
        {
            logger.LogDebug("[DevoHub] Hit ignored in {RoomCode} (debounced duplicate)", roomCode);
            return;
        }

        int victimSlot = scorerSlot == 1 ? 2 : 1;
        logger.LogInformation("[DevoHub] Hit confirmed in {RoomCode}! Scorer: Slot {ScorerSlot}, Victim: Slot {VictimSlot}, Pos: ({X:F2},{Z:F2}), Scores: [{Score1}]-[{Score2}] (Won: {MatchWon})",
            roomCode, scorerSlot, victimSlot, impactX, impactZ, room.Score1, room.Score2, matchWon);

        // Broadcast synchronized hook latching & victim reeling to all clients in the duel
        await Clients.Group(group).OnPlayerHooked(scorerSlot, victimSlot, impactX, impactZ);

        await Clients.Group(group).OnScoreUpdated(room.Score1, room.Score2, scorerSlot);

        if (matchWon)
        {
            await Clients.Group(group).OnMatchWon(winnerSlot, room.Score1, room.Score2);
        }
    }

    public async Task SendTaunt(string roomCode, string tauntId)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentTaunt(tauntId);
    }

    public async Task ResetMatch(string roomCode)
    {
        string group = GetRoomGroup(roomCode);
        DevoRoom? room = roomManager.ResetMatch(roomCode);
        if (room != null)
        {
            logger.LogInformation("[DevoHub] Match reset in {RoomCode}", roomCode);
            await Clients.Group(group).OnMatchReset();
            if (room.Status == DevoMatchStatus.WaitingForReady && room.Player1 != null && room.Player2 != null)
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
            logger.LogInformation("[DevoHub] Connection {ConnId} disconnected from room {RoomCode}, vacated slot {Slot}",
                Context.ConnectionId, room.RoomCode, vacatedSlot);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
