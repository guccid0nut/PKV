// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public interface IMonkeyCheckersClient
{
    Task OnRoomJoined(string roomCode, int slot, string p1Name, string p2Name, string status);
    Task OnRoomFull(string roomCode, string message);
    Task OnOpponentJoined(int slot, string opponentName);
    Task OnOpponentLeft(int vacatedSlot);
    Task OnMatchPromptReady();
    Task OnPlayerReadyStatus(int slot, bool isReady, bool wantsAr);
    Task OnMatchStarted(string p1Name, string p2Name, string startingTurn);
    Task OnOpponentMove(int fromRow, int fromCol, int toRow, int toCol, bool isJump);
    Task OnMatchReset();
    Task OnOpponentTaunt(string tauntId);
}
