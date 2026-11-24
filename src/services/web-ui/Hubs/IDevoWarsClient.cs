// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public interface IDevoWarsClient
{
    Task OnRoomJoined(string roomCode, int slot, string p1Name, string p2Name, int p1Score, int p2Score, string status);
    Task OnRoomFull(string roomCode, string message);
    Task OnOpponentJoined(int slot, string opponentName);
    Task OnOpponentLeft(int vacatedSlot);
    Task OnMatchPromptReady();
    Task OnPlayerReadyStatus(int slot, bool isReady, bool wantsAr);
    Task OnMatchStarted(string p1Name, string p2Name);
    Task OnOpponentPosition(float x, float z, float rotY, bool isMoving);
    Task OnHookThrown(int throwerSlot, float startX, float startZ, float dirX, float dirZ, float speed);
    Task OnPlayerHooked(int throwerSlot, int victimSlot, float impactX, float impactZ);
    Task OnScoreUpdated(int p1Score, int p2Score, int scorerSlot);
    Task OnMatchWon(int winnerSlot, int finalP1Score, int finalP2Score);
    Task OnMatchReset();
    Task OnOpponentTaunt(string tauntId);
}
