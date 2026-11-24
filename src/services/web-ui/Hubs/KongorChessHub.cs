// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

[Authorize]
public class KongorChessHub(
    IChessRoomManager roomManager,
    ILogger<KongorChessHub> logger) : Hub<IKongorChessClient>
{
    public static string GetRoomGroup(string roomCode) => $"Chess_{ChessRoomManager.NormalizeCode(roomCode)}";

    public async Task JoinRoom(string roomCode, string playerName, string? playerId = null, string timeControl = "5m")
    {
        string group = GetRoomGroup(roomCode);

        string effectivePlayerName = !string.IsNullOrWhiteSpace(playerName)
            ? playerName.Trim()
            : (!string.IsNullOrWhiteSpace(Context.User?.Identity?.Name)
                ? Context.User.Identity.Name
                : $"Commander_{Context.ConnectionId[..4]}");

        ChessTimeControl tc = timeControl.ToLowerInvariant() switch
        {
            "3m" => ChessTimeControl.Blitz3m,
            "5m" => ChessTimeControl.Rapid5m,
            "10m" => ChessTimeControl.Rapid10m,
            "30m" => ChessTimeControl.Classical30m,
            "unlimited" => ChessTimeControl.Unlimited,
            _ => ChessTimeControl.Rapid5m
        };

        var (room, assignedSlot, bothInRoom) = roomManager.JoinRoom(roomCode, Context.ConnectionId, effectivePlayerName, playerId, tc);

        if (assignedSlot == 0)
        {
            logger.LogWarning("[KongorChessHub] Connection {ConnId} ({PlayerName}) rejected: Room {RoomCode} is full (2/2)",
                Context.ConnectionId, effectivePlayerName, room.RoomCode);

            await Clients.Caller.OnRoomFull(room.RoomCode,
                $"Room \"{room.RoomCode}\" is full (2/2 players). Please create or join another room!");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        logger.LogInformation("[KongorChessHub] Connection {ConnId} joined Room {RoomCode} as Slot {Slot} (BothInRoom: {BothInRoom})",
            Context.ConnectionId, room.RoomCode, assignedSlot, bothInRoom);

        await Clients.Caller.OnRoomJoined(
            room.RoomCode,
            assignedSlot,
            room.Player1?.Name ?? "",
            room.Player2?.Name ?? "",
            room.Status.ToString(),
            room.TimeControl.ToString(),
            room.Player1?.TimeRemainingMs ?? 0,
            room.Player2?.TimeRemainingMs ?? 0
        );

        // Notify opponent
        await Clients.OthersInGroup(group).OnOpponentJoined(assignedSlot, effectivePlayerName);

        if ((bothInRoom || room.Status == ChessMatchStatus.WaitingForReady) && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[KongorChessHub] Room {RoomCode} is ready for duel. Triggering Ready Check!", room.RoomCode);
            await Clients.Group(group).OnMatchPromptReady();
        }

        // Synchronize ready statuses
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
        var (room, bothReady) = roomManager.SetPlayerReady(roomCode, Context.ConnectionId, wantsAr);
        if (room == null) return;

        int slot = room.Player1?.ConnectionId == Context.ConnectionId ? 1 : (room.Player2?.ConnectionId == Context.ConnectionId ? 2 : 0);
        if (slot > 0)
        {
            await Clients.Group(group).OnPlayerReadyStatus(slot, true, wantsAr);
        }

        if (bothReady && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[KongorChessHub] Match starting in {RoomCode} (P1: {P1}, P2: {P2})",
                roomCode, room.Player1.Name, room.Player2.Name);
            await Clients.Group(group).OnMatchStarted(
                room.Player1.Name,
                room.Player2.Name,
                room.CurrentTurn,
                room.TimeControl.ToString(),
                room.Player1.TimeRemainingMs,
                room.Player2.TimeRemainingMs
            );
        }
    }

    public async Task SendMove(string roomCode, string from, string to, string? promotion, string fen, bool isCheck, bool isCheckmate)
    {
        string group = GetRoomGroup(roomCode);
        var (room, isValid, nextTurn, p1TimeMs, p2TimeMs, isTimeout) = roomManager.RegisterMove(
            roomCode, Context.ConnectionId, from, to, promotion, fen);

        if (isTimeout && room != null)
        {
            logger.LogInformation("[KongorChessHub] Timeout in Room {RoomCode}. Winner Slot {Winner}", roomCode, room.WinnerSlot);
            await Clients.Group(group).OnGameOver(room.WinnerSlot, "Timeout");
            return;
        }

        if (!isValid || room == null)
        {
            logger.LogWarning("[KongorChessHub] Invalid move rejected in {RoomCode} from {ConnId}", roomCode, Context.ConnectionId);
            return;
        }

        // Broadcast move to opponent
        await Clients.OthersInGroup(group).OnOpponentMove(from, to, promotion, fen, isCheck, isCheckmate, p1TimeMs, p2TimeMs);

        // Broadcast synchronized clock state
        await Clients.Group(group).OnClockSync(p1TimeMs, p2TimeMs, nextTurn);

        if (isCheckmate)
        {
            int winner = nextTurn == "b" ? 1 : 2; // Previous turn delivered checkmate
            room.Status = ChessMatchStatus.Finished;
            room.WinnerSlot = winner;
            room.WinReason = "Checkmate";
            await Clients.Group(group).OnGameOver(winner, "Checkmate");
        }
    }

    public async Task Resign(string roomCode)
    {
        string group = GetRoomGroup(roomCode);
        var (room, winnerSlot, reason) = roomManager.Forfeit(roomCode, Context.ConnectionId);
        if (room != null)
        {
            await Clients.Group(group).OnGameOver(winnerSlot, reason);
        }
    }

    public async Task ResetMatch(string roomCode)
    {
        string group = GetRoomGroup(roomCode);
        ChessRoom? room = roomManager.ResetMatch(roomCode);
        if (room != null)
        {
            await Clients.Group(group).OnMatchReset();
        }
    }

    public async Task SendTaunt(string roomCode, string tauntId)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentTaunt(tauntId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (room, vacatedSlot) = roomManager.Leave(Context.ConnectionId);
        if (room != null && vacatedSlot > 0)
        {
            string group = GetRoomGroup(room.RoomCode);
            await Clients.OthersInGroup(group).OnOpponentLeft(vacatedSlot);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
