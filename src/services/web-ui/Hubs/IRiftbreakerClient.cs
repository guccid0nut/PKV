// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public interface IRiftbreakerClient
{
    Task OnRoomJoined(
        string roomCode,
        int slot,
        string p1Name,
        string p2Name,
        int p1Score,
        int p2Score,
        int act,
        string status,
        string? p1Hero,
        string? p2Hero,
        IReadOnlyList<string> activeRiftCards);

    Task OnRoomFull(string roomCode, string message);
    Task OnOpponentJoined(int slot, string opponentName);
    Task OnOpponentLeft(int vacatedSlot);
    Task OnMatchPromptReady();
    Task OnPlayerReadyStatus(int slot, bool isReady, bool wantsAr);
    Task OnMatchStarted(string p1Name, string p2Name);

    Task OnOpponentPaddleState(int slot, float x, float z, float vx, float vz, float parryTimer, float energy);
    Task OnBallRallySynced(
        int authoritativeSlot,
        float x,
        float y,
        float z,
        float vx,
        float vy,
        float vz,
        float spinZ,
        int rallyCount,
        bool parried);

    Task OnHeroUltimateCast(int casterSlot, string heroId, float originX, float originZ, float targetX, float targetZ);
    Task OnHeroDraftTriggered();
    Task OnHeroSelected(int slot, string heroId, bool bothSelected, string? p1Hero, string? p2Hero);
    Task OnCardDraftTriggered(int pickerSlot);
    Task OnRiftCardActivated(int pickerSlot, string cardId, IReadOnlyList<string> activeRiftCards);
    Task OnBossOverdriveTriggered();

    Task OnScoreUpdated(int p1Score, int p2Score, int scorerSlot, int act);
    Task OnMatchWon(int winnerSlot, int finalP1Score, int finalP2Score);
    Task OnMatchReset(bool keepHeroes);
    Task OnOpponentTaunt(string tauntId);
}
