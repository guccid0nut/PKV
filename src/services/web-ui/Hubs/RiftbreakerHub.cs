// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

[Authorize]
public class RiftbreakerHub(
    IRiftbreakerRoomManager roomManager,
    ILogger<RiftbreakerHub> logger) : Hub<IRiftbreakerClient>
{
    public static string GetRoomGroup(string roomCode) => $"Riftbreaker_{RiftbreakerRoomManager.NormalizeCode(roomCode)}";

    public async Task JoinRoom(string roomCode, string playerName, string? playerId = null)
    {
        string group = GetRoomGroup(roomCode);
        string connShort = Context.ConnectionId.Length >= 4 ? Context.ConnectionId[..4] : Context.ConnectionId;
        string effectivePlayerName = string.IsNullOrWhiteSpace(playerName)
            ? $"Challenger_{connShort}"
            : playerName.Trim();

        var (room, assignedSlot, started) = roomManager.JoinRoom(roomCode, Context.ConnectionId, effectivePlayerName, playerId);

        if (assignedSlot == 0)
        {
            logger.LogWarning("[RiftbreakerHub] Connection {ConnId} ({Name}) rejected: Room {RoomCode} is full",
                Context.ConnectionId, effectivePlayerName, room.RoomCode);
            await Clients.Caller.OnRoomFull(room.RoomCode,
                $"Room \"{room.RoomCode}\" is full (2/2 champions). Enter another code to open a fresh rift.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        logger.LogInformation("[RiftbreakerHub] {ConnId} ({Name}) joined Room {RoomCode} as Slot {Slot} (Act={Act}, Status={Status})",
            Context.ConnectionId, effectivePlayerName, room.RoomCode, assignedSlot, room.Act, room.Status);

        await Clients.Caller.OnRoomJoined(
            room.RoomCode,
            assignedSlot,
            room.Player1?.Name ?? "",
            room.Player2?.Name ?? "",
            room.Score1,
            room.Score2,
            room.Act,
            room.Status.ToString(),
            room.Player1?.SelectedHero,
            room.Player2?.SelectedHero,
            room.ActiveRiftCards);

        await Clients.OthersInGroup(group).OnOpponentJoined(assignedSlot, effectivePlayerName);

        if ((started || room.Status == RiftbreakerMatchStatus.WaitingForReady) && room.Player1 != null && room.Player2 != null)
        {
            await Clients.Group(group).OnMatchPromptReady();
        }

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
        if (room == null)
        {
            logger.LogWarning("[RiftbreakerHub] SetReady ignored: room {RoomCode} not found for {ConnId}", roomCode, Context.ConnectionId);
            return;
        }

        int slot = room.Player1?.ConnectionId == Context.ConnectionId ? 1 : (room.Player2?.ConnectionId == Context.ConnectionId ? 2 : 0);
        if (slot > 0)
        {
            await Clients.Group(group).OnPlayerReadyStatus(slot, true, wantsAr);
        }

        if (bothReady && room.Player1 != null && room.Player2 != null)
        {
            logger.LogInformation("[RiftbreakerHub] Room {RoomCode} starting match ({P1} vs {P2})",
                room.RoomCode, room.Player1.Name, room.Player2.Name);
            await Clients.Group(group).OnMatchStarted(room.Player1.Name, room.Player2.Name);
        }
    }

    public async Task SendPaddleState(string roomCode, int slot, float x, float z, float vx, float vz, float parryTimer, float energy)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentPaddleState(slot, x, z, vx, vz, parryTimer, energy);
    }

    public async Task SyncBallRally(
        string roomCode,
        int authoritativeSlot,
        float x,
        float y,
        float z,
        float vx,
        float vy,
        float vz,
        float spinZ,
        int rallyCount,
        bool parried)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnBallRallySynced(
            authoritativeSlot, x, y, z, vx, vy, vz, spinZ, rallyCount, parried);
    }

    public async Task CastHeroUltimate(string roomCode, int casterSlot, string heroId, float originX, float originZ, float targetX, float targetZ)
    {
        string group = GetRoomGroup(roomCode);
        logger.LogDebug("[RiftbreakerHub] Room {RoomCode} Slot {Slot} cast ultimate ({Hero})", roomCode, casterSlot, heroId);
        await Clients.OthersInGroup(group).OnHeroUltimateCast(casterSlot, heroId, originX, originZ, targetX, targetZ);
    }

    public async Task SelectHero(string roomCode, string heroId)
    {
        string group = GetRoomGroup(roomCode);
        var (room, slot, bothSelected, canonicalHero) = roomManager.SelectHero(roomCode, Context.ConnectionId, heroId);
        if (room == null || slot == 0 || canonicalHero == null)
        {
            return;
        }

        logger.LogInformation("[RiftbreakerHub] Room {RoomCode} Slot {Slot} locked hero {Hero} (BothSelected={Both})",
            room.RoomCode, slot, canonicalHero, bothSelected);

        await Clients.Group(group).OnHeroSelected(
            slot,
            canonicalHero,
            bothSelected,
            room.Player1?.SelectedHero,
            room.Player2?.SelectedHero);
    }

    public async Task SelectRiftCard(string roomCode, string cardId)
    {
        string group = GetRoomGroup(roomCode);
        var (room, slot, accepted, canonicalCard) = roomManager.SelectRiftCard(roomCode, Context.ConnectionId, cardId);
        if (room == null || !accepted || canonicalCard == null)
        {
            return;
        }

        logger.LogInformation("[RiftbreakerHub] Room {RoomCode} Slot {Slot} activated Rift Card {Card}",
            room.RoomCode, slot, canonicalCard);

        await Clients.Group(group).OnRiftCardActivated(slot, canonicalCard, room.ActiveRiftCards);
    }

    public async Task ReportPointScored(string roomCode, int scorerSlot)
    {
        string group = GetRoomGroup(roomCode);
        RiftbreakerRoom? existing = roomManager.GetRoom(roomCode);
        int prev1 = existing?.Score1 ?? 0;
        int prev2 = existing?.Score2 ?? 0;

        var (room, matchWon, winnerSlot, triggeredHeroDraft, triggeredCardDraft, triggeredBoss) =
            roomManager.RegisterScore(roomCode, scorerSlot, RiftbreakerCatalog.DefaultWinningScore, debounceMs: 450);

        if (room == null || (room.Score1 == prev1 && room.Score2 == prev2))
        {
            return;
        }

        await Clients.Group(group).OnScoreUpdated(room.Score1, room.Score2, scorerSlot, room.Act);

        if (matchWon)
        {
            await Clients.Group(group).OnMatchWon(winnerSlot, room.Score1, room.Score2);
            return;
        }

        if (triggeredHeroDraft)
        {
            await Clients.Group(group).OnHeroDraftTriggered();
        }

        if (triggeredCardDraft)
        {
            int pickerSlot = room.Score1 <= room.Score2 ? 1 : 2;
            await Clients.Group(group).OnCardDraftTriggered(pickerSlot);
        }

        if (triggeredBoss)
        {
            await Clients.Group(group).OnBossOverdriveTriggered();
        }
    }

    public async Task SendTaunt(string roomCode, string tauntId)
    {
        string group = GetRoomGroup(roomCode);
        await Clients.OthersInGroup(group).OnOpponentTaunt(tauntId);
    }

    public async Task ResetMatch(string roomCode, bool keepHeroes = false)
    {
        string group = GetRoomGroup(roomCode);
        RiftbreakerRoom? room = roomManager.ResetMatch(roomCode, keepHeroes);
        if (room != null)
        {
            logger.LogInformation("[RiftbreakerHub] Match reset in {RoomCode} (KeepHeroes={KeepHeroes})", room.RoomCode, keepHeroes);
            await Clients.Group(group).OnMatchReset(keepHeroes);
            if (room.Status == RiftbreakerMatchStatus.WaitingForReady && room.Player1 != null && room.Player2 != null)
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
            logger.LogInformation("[RiftbreakerHub] {ConnId} disconnected from {RoomCode}, vacated slot {Slot}",
                Context.ConnectionId, room.RoomCode, vacatedSlot);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
