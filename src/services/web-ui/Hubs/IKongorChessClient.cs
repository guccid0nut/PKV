// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public interface IKongorChessClient
{
    Task OnRoomJoined(string roomCode, int slot, string p1Name, string p2Name, string status, string timeControl, int p1TimeMs, int p2TimeMs);
    Task OnRoomFull(string roomCode, string message);
    Task OnOpponentJoined(int slot, string oppName);
    Task OnMatchPromptReady();
    Task OnPlayerReadyStatus(int slot, bool isReady, bool wantsAr);
    Task OnMatchStarted(string p1Name, string p2Name, string startingTurn, string timeControl, int p1TimeMs, int p2TimeMs);
    Task OnOpponentMove(string from, string to, string? promotion, string fen, bool isCheck, bool isCheckmate, int p1TimeMs, int p2TimeMs);
    Task OnClockSync(int p1TimeMs, int p2TimeMs, string currentTurn);
    Task OnGameOver(int? winnerSlot, string reason);
    Task OnOpponentLeft(int vacatedSlot);
    Task OnMatchReset();
    Task OnOpponentTaunt(string tauntId);
}
