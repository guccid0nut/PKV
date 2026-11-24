// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace DAWNBRINGER.WebPortal.UI.Services;

public enum ChessMatchStatus
{
    WaitingForOpponent,
    WaitingForReady,
    Playing,
    Finished
}

public enum ChessTimeControl
{
    Unlimited,
    Blitz3m,
    Rapid5m,
    Rapid10m,
    Classical30m
}

public sealed class ChessPlayer
{
    public string PlayerId { get; init; } = "";
    public required string ConnectionId { get; set; }
    public required string Name { get; set; }
    public int Slot { get; init; } // 1 = Legion (White), 2 = Hellbourne (Black)
    public bool IsReady { get; set; }
    public bool WantsAr { get; set; }
    public int TimeRemainingMs { get; set; } = 300_000; // Default 5m in ms
    public DateTimeOffset JoinedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActiveAt { get; set; } = TimeProvider.System.GetUtcNow();
}

public sealed class ChessRoom
{
    public required string RoomCode { get; init; }
    public ChessPlayer? Player1 { get; set; } // Legion (White)
    public ChessPlayer? Player2 { get; set; } // Hellbourne (Black)
    public string CurrentTurn { get; set; } = "w"; // "w" (Legion) or "b" (Hellbourne)
    public string Fen { get; set; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public ChessTimeControl TimeControl { get; set; } = ChessTimeControl.Rapid5m;
    public ChessMatchStatus Status { get; set; } = ChessMatchStatus.WaitingForOpponent;
    public int? WinnerSlot { get; set; }
    public string? WinReason { get; set; }
    public DateTimeOffset? LastMoveTimeUtc { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActivityAt { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonIgnore]
    public bool IsFull => Player1 != null && Player2 != null;
    [JsonIgnore]
    public bool IsEmpty => Player1 == null && Player2 == null;
}

public interface IChessRoomManager
{
    ChessRoom GetOrCreateRoom(string roomCode, ChessTimeControl timeControl = ChessTimeControl.Rapid5m);
    ChessRoom? GetRoom(string roomCode);
    (ChessRoom Room, int AssignedSlot, bool BothInRoom) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null, ChessTimeControl timeControl = ChessTimeControl.Rapid5m);
    (ChessRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr);
    (ChessRoom? Room, int VacatedSlot) Leave(string connectionId);
    (ChessRoom? Room, bool IsValid, string NextTurn, int P1TimeMs, int P2TimeMs, bool IsTimeout) RegisterMove(string roomCode, string connectionId, string from, string to, string? promotion, string newFen);
    (ChessRoom? Room, int WinnerSlot, string Reason) Forfeit(string roomCode, string connectionId);
    ChessRoom? ResetMatch(string roomCode);
    IReadOnlyCollection<ChessRoom> GetActiveRooms();
}
