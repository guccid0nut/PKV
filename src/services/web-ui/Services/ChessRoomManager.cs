// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public class ChessRoomManager : IChessRoomManager
{
    public const int MaxActiveRooms = 256;

    private readonly ConcurrentDictionary<string, ChessRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToRoomMap = new();
    private readonly ILogger<ChessRoomManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly Lock _lock = new();

    public ChessRoomManager(ILogger<ChessRoomManager> logger, IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                ISubscriber sub = _redis.GetSubscriber();
                sub.Subscribe(RedisChannel.Pattern("kongor:chess:events:*"), (_, message) =>
                {
                    OnRemoteRoomEvent(message);
                });
                _logger.LogInformation("[KongorChess] Subscribed to Redis pub/sub backplane kongor:chess:events:*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KongorChess] Failed to subscribe to Redis pub/sub backplane");
            }
        }
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "MK42";
        string clean = new(code.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return clean.Length > 8 ? clean[..8] : (clean.Length == 0 ? "MK42" : clean);
    }

    public static int GetInitialClockMs(ChessTimeControl control) => control switch
    {
        ChessTimeControl.Blitz3m => 180_000,
        ChessTimeControl.Rapid5m => 300_000,
        ChessTimeControl.Rapid10m => 600_000,
        ChessTimeControl.Classical30m => 1_800_000,
        ChessTimeControl.Unlimited => int.MaxValue,
        _ => 300_000
    };

    private void OnRemoteRoomEvent(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;
        try
        {
            ChessRoom? remoteRoom = JsonSerializer.Deserialize<ChessRoom>(message.ToString());
            if (remoteRoom != null && !string.IsNullOrWhiteSpace(remoteRoom.RoomCode))
            {
                string code = NormalizeCode(remoteRoom.RoomCode);
                lock (_lock)
                {
                    if (remoteRoom.IsEmpty)
                    {
                        _rooms.TryRemove(code, out _);
                        _logger.LogInformation("[KongorChess] Remote sync: Room {RoomCode} is empty, evicted from local cache", code);
                    }
                    else
                    {
                        _rooms[code] = remoteRoom;
                        _logger.LogDebug("[KongorChess] Remote sync: Updated room {RoomCode} in local cache (Status: {Status})", code, remoteRoom.Status);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to process remote room sync event");
        }
    }

    private void SyncToRedis(ChessRoom room)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(room);
            string key = $"kongor:chess:room:{room.RoomCode}";
            _ = db.StringSetAsync(key, json, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
            _ = db.SetAddAsync("kongor:chess:rooms", room.RoomCode, CommandFlags.FireAndForget);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:chess:events:{room.RoomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to sync room {RoomCode} to Redis", room.RoomCode);
        }
    }

    private void RemoveFromRedis(string roomCode)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = $"kongor:chess:room:{roomCode}";
            _ = db.KeyDeleteAsync(key, CommandFlags.FireAndForget);
            _ = db.SetRemoveAsync("kongor:chess:rooms", roomCode, CommandFlags.FireAndForget);
            var emptyRoom = new ChessRoom { RoomCode = roomCode };
            string json = JsonSerializer.Serialize(emptyRoom);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:chess:events:{roomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to remove room {RoomCode} from Redis", roomCode);
        }
    }

    private void SaveConnectionMapping(string connectionId, string roomCode)
    {
        _connectionToRoomMap[connectionId] = roomCode;
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.StringSetAsync($"kongor:chess:conn:{connectionId}", roomCode, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to save connection mapping {ConnectionId} -> {RoomCode} to Redis", connectionId, roomCode);
        }
    }

    private void RemoveConnectionMapping(string connectionId)
    {
        _connectionToRoomMap.TryRemove(connectionId, out _);
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.KeyDeleteAsync($"kongor:chess:conn:{connectionId}", CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to remove connection mapping {ConnectionId} from Redis", connectionId);
        }
    }

    private ChessRoom? HydrateRoomFromRedis(string normalized)
    {
        if (_redis == null || !_redis.IsConnected) return null;
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue val = db.StringGet($"kongor:chess:room:{normalized}");
            if (!val.IsNullOrEmpty)
            {
                ChessRoom? redisRoom = JsonSerializer.Deserialize<ChessRoom>(val.ToString());
                if (redisRoom != null)
                {
                    _rooms[normalized] = redisRoom;
                    return redisRoom;
                }
            }
            else
            {
                // Authoritative Redis store indicates room does not exist; evict stale local cache if any
                _rooms.TryRemove(normalized, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KongorChess] Failed to hydrate room {RoomCode} from Redis", normalized);
        }
        return null;
    }

    public ChessRoom GetOrCreateRoom(string roomCode, ChessTimeControl timeControl = ChessTimeControl.Rapid5m)
    {
        string norm = NormalizeCode(roomCode);
        ChessRoom? hydrated = HydrateRoomFromRedis(norm);
        ChessRoom room;
        lock (_lock)
        {
            room = hydrated!;
            if (room == null && !_rooms.TryGetValue(norm, out room!))
            {
                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                if (_rooms.Count >= MaxActiveRooms)
                {
                    foreach (KeyValuePair<string, ChessRoom> kvp in _rooms)
                    {
                        if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                        {
                            _rooms.TryRemove(kvp.Key, out _);
                        }
                    }

                    if (_rooms.Count >= MaxActiveRooms)
                    {
                        _logger.LogWarning(
                            "[SECURITY-ALERT] [KongorChess] Rejected GetOrCreateRoom allocation '{RoomCode}': MaxActiveRooms cap ({MaxActiveRooms}) reached",
                            norm,
                            MaxActiveRooms);
                        return new ChessRoom
                        {
                            RoomCode = norm,
                            TimeControl = timeControl,
                            CurrentTurn = "w",
                            Status = ChessMatchStatus.WaitingForOpponent
                        };
                    }
                }

                room = new ChessRoom
                {
                    RoomCode = norm,
                    TimeControl = timeControl,
                    Player1 = null,
                    Player2 = null,
                    CurrentTurn = "w",
                    Status = ChessMatchStatus.WaitingForOpponent
                };
                _rooms[norm] = room;
            }
        }
        SyncToRedis(room);
        return room;
    }

    public ChessRoom? GetRoom(string roomCode)
    {
        string norm = NormalizeCode(roomCode);
        ChessRoom? hydrated = HydrateRoomFromRedis(norm);
        if (hydrated != null)
        {
            return hydrated;
        }

        return _rooms.TryGetValue(norm, out ChessRoom? r) ? r : null;
    }

    public (ChessRoom Room, int AssignedSlot, bool BothInRoom) JoinRoom(
        string roomCode,
        string connectionId,
        string playerName,
        string? playerId = null,
        ChessTimeControl timeControl = ChessTimeControl.Rapid5m)
    {
        string norm = NormalizeCode(roomCode);

        lock (_lock)
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (!_rooms.ContainsKey(norm) && _rooms.Count >= MaxActiveRooms)
            {
                foreach (KeyValuePair<string, ChessRoom> kvp in _rooms)
                {
                    if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                    {
                        _rooms.TryRemove(kvp.Key, out _);
                    }
                }

                if (_rooms.Count >= MaxActiveRooms)
                {
                    _logger.LogWarning(
                        "[SECURITY-ALERT] [KongorChess] Rejected room allocation '{RoomCode}' from connection {ConnectionId}: MaxActiveRooms cap ({MaxActiveRooms}) reached",
                        norm,
                        connectionId,
                        MaxActiveRooms);
                    return (new ChessRoom { RoomCode = norm, TimeControl = timeControl }, 0, false);
                }
            }

            ChessRoom? existing = HydrateRoomFromRedis(norm);
            ChessRoom room = existing ?? _rooms.GetOrAdd(norm, code => new ChessRoom
            {
                RoomCode = code,
                TimeControl = timeControl,
                Player1 = null,
                Player2 = null,
                CurrentTurn = "w",
                Status = ChessMatchStatus.WaitingForOpponent
            });

            // Reconnection: Check existing playerId
            if (!string.IsNullOrWhiteSpace(playerId))
            {
                if (room.Player1?.PlayerId == playerId)
                {
                    RemoveConnectionMapping(room.Player1.ConnectionId);
                    room.Player1.ConnectionId = connectionId;
                    room.Player1.Name = playerName;
                    room.Player1.LastActiveAt = TimeProvider.System.GetUtcNow();
                    room.LastActivityAt = TimeProvider.System.GetUtcNow();
                    SaveConnectionMapping(connectionId, norm);
                    SyncToRedis(room);
                    return (room, 1, room.IsFull);
                }
                if (room.Player2?.PlayerId == playerId)
                {
                    RemoveConnectionMapping(room.Player2.ConnectionId);
                    room.Player2.ConnectionId = connectionId;
                    room.Player2.Name = playerName;
                    room.Player2.LastActiveAt = TimeProvider.System.GetUtcNow();
                    room.LastActivityAt = TimeProvider.System.GetUtcNow();
                    SaveConnectionMapping(connectionId, norm);
                    SyncToRedis(room);
                    return (room, 2, room.IsFull);
                }
            }

            int clockMs = GetInitialClockMs(room.TimeControl);

            // Slot 1: Legion (White)
            if (room.Player1 == null)
            {
                room.Player1 = new ChessPlayer
                {
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 1,
                    PlayerId = playerId ?? Guid.NewGuid().ToString("N"),
                    TimeRemainingMs = clockMs
                };
                room.Status = room.Player2 != null ? ChessMatchStatus.WaitingForReady : ChessMatchStatus.WaitingForOpponent;
                room.LastActivityAt = TimeProvider.System.GetUtcNow();
                SaveConnectionMapping(connectionId, norm);
                SyncToRedis(room);
                return (room, 1, room.IsFull);
            }

            // Slot 2: Hellbourne (Black)
            if (room.Player2 == null)
            {
                room.Player2 = new ChessPlayer
                {
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 2,
                    PlayerId = playerId ?? Guid.NewGuid().ToString("N"),
                    TimeRemainingMs = clockMs
                };
                room.Status = ChessMatchStatus.WaitingForReady;
                room.LastActivityAt = TimeProvider.System.GetUtcNow();
                SaveConnectionMapping(connectionId, norm);
                SyncToRedis(room);
                return (room, 2, true);
            }

            // Room Full
            return (room, 0, true);
        }
    }

    public (ChessRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr)
    {
        string norm = NormalizeCode(roomCode);
        lock (_lock)
        {
            ChessRoom? room = HydrateRoomFromRedis(norm) ?? (_rooms.TryGetValue(norm, out ChessRoom? r) ? r : null);
            if (room == null) return (null, false);

            if (room.Player1?.ConnectionId == connectionId)
            {
                room.Player1.IsReady = true;
                room.Player1.WantsAr = wantsAr;
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                room.Player2.IsReady = true;
                room.Player2.WantsAr = wantsAr;
            }
            else
            {
                return (room, false);
            }

            bool bothReady = room.Player1?.IsReady == true && room.Player2?.IsReady == true;
            if (bothReady)
            {
                room.Status = ChessMatchStatus.Playing;
                room.CurrentTurn = "w";
                room.Fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
                room.LastMoveTimeUtc = TimeProvider.System.GetUtcNow();
                int clockMs = GetInitialClockMs(room.TimeControl);
                if (room.Player1 != null) room.Player1.TimeRemainingMs = clockMs;
                if (room.Player2 != null) room.Player2.TimeRemainingMs = clockMs;
            }

            room.LastActivityAt = TimeProvider.System.GetUtcNow();
            SyncToRedis(room);
            return (room, bothReady);
        }
    }

    public (ChessRoom? Room, int VacatedSlot) Leave(string connectionId)
    {
        lock (_lock)
        {
            string? roomCode = null;
            if (!_connectionToRoomMap.TryRemove(connectionId, out roomCode))
            {
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        IDatabase db = _redis.GetDatabase();
                        RedisValue val = db.StringGet($"kongor:chess:conn:{connectionId}");
                        if (!val.IsNullOrEmpty)
                        {
                            roomCode = val.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[KongorChess] Failed to resolve connection mapping {ConnectionId} from Redis", connectionId);
                    }
                }
            }

            RemoveConnectionMapping(connectionId);

            if (string.IsNullOrWhiteSpace(roomCode)) return (null, 0);

            string norm = NormalizeCode(roomCode);
            ChessRoom? room = HydrateRoomFromRedis(norm) ?? (_rooms.TryGetValue(norm, out ChessRoom? r) ? r : null);
            if (room == null) return (null, 0);

            int vacated = 0;
            if (room.Player1?.ConnectionId == connectionId)
            {
                room.Player1 = null;
                vacated = 1;
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                room.Player2 = null;
                vacated = 2;
            }

            if (room.IsEmpty)
            {
                _rooms.TryRemove(norm, out _);
                RemoveFromRedis(norm);
                return (null, vacated);
            }
            else
            {
                room.Status = ChessMatchStatus.WaitingForOpponent;
                room.LastActivityAt = TimeProvider.System.GetUtcNow();
                SyncToRedis(room);
            }

            return (room, vacated);
        }
    }

    public (ChessRoom? Room, bool IsValid, string NextTurn, int P1TimeMs, int P2TimeMs, bool IsTimeout) RegisterMove(
        string roomCode,
        string connectionId,
        string from,
        string to,
        string? promotion,
        string newFen)
    {
        string norm = NormalizeCode(roomCode);
        lock (_lock)
        {
            ChessRoom? room = HydrateRoomFromRedis(norm) ?? (_rooms.TryGetValue(norm, out ChessRoom? r) ? r : null);
            if (room == null)
                return (null, false, "w", 0, 0, false);

            if (room.Status != ChessMatchStatus.Playing)
                return (room, false, room.CurrentTurn, room.Player1?.TimeRemainingMs ?? 0, room.Player2?.TimeRemainingMs ?? 0, false);

            bool isP1 = room.Player1?.ConnectionId == connectionId;
            bool isP2 = room.Player2?.ConnectionId == connectionId;

            if (!isP1 && !isP2) return (room, false, room.CurrentTurn, 0, 0, false);

            // Validate turn ownership: P1 moves on "w", P2 moves on "b"
            if (isP1 && room.CurrentTurn != "w") return (room, false, room.CurrentTurn, room.Player1?.TimeRemainingMs ?? 0, room.Player2?.TimeRemainingMs ?? 0, false);
            if (isP2 && room.CurrentTurn != "b") return (room, false, room.CurrentTurn, room.Player1?.TimeRemainingMs ?? 0, room.Player2?.TimeRemainingMs ?? 0, false);

            // Decrement active player's clock if timed match
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (room.LastMoveTimeUtc.HasValue && room.TimeControl != ChessTimeControl.Unlimited)
            {
                int elapsedMs = (int)(now - room.LastMoveTimeUtc.Value).TotalMilliseconds;
                if (isP1 && room.Player1 != null)
                {
                    room.Player1.TimeRemainingMs = Math.Max(0, room.Player1.TimeRemainingMs - elapsedMs);
                    if (room.Player1.TimeRemainingMs <= 0)
                    {
                        room.Status = ChessMatchStatus.Finished;
                        room.WinnerSlot = 2;
                        room.WinReason = "Timeout";
                        SyncToRedis(room);
                        return (room, false, "b", 0, room.Player2?.TimeRemainingMs ?? 0, true);
                    }
                }
                else if (isP2 && room.Player2 != null)
                {
                    room.Player2.TimeRemainingMs = Math.Max(0, room.Player2.TimeRemainingMs - elapsedMs);
                    if (room.Player2.TimeRemainingMs <= 0)
                    {
                        room.Status = ChessMatchStatus.Finished;
                        room.WinnerSlot = 1;
                        room.WinReason = "Timeout";
                        SyncToRedis(room);
                        return (room, false, "w", room.Player1?.TimeRemainingMs ?? 0, 0, true);
                    }
                }
            }

            room.LastMoveTimeUtc = now;
            room.Fen = newFen;
            room.CurrentTurn = isP1 ? "b" : "w";
            room.LastActivityAt = now;

            SyncToRedis(room);

            return (
                room,
                true,
                room.CurrentTurn,
                room.Player1?.TimeRemainingMs ?? 0,
                room.Player2?.TimeRemainingMs ?? 0,
                false
            );
        }
    }

    public (ChessRoom? Room, int WinnerSlot, string Reason) Forfeit(string roomCode, string connectionId)
    {
        string norm = NormalizeCode(roomCode);
        lock (_lock)
        {
            ChessRoom? room = HydrateRoomFromRedis(norm) ?? (_rooms.TryGetValue(norm, out ChessRoom? r) ? r : null);
            if (room == null || room.IsEmpty) return (null, 0, "");

            int winner = room.Player1?.ConnectionId == connectionId ? 2 : 1;
            room.Status = ChessMatchStatus.Finished;
            room.WinnerSlot = winner;
            room.WinReason = "Resignation";
            room.LastActivityAt = TimeProvider.System.GetUtcNow();
            SyncToRedis(room);
            return (room, winner, "Resignation");
        }
    }

    public ChessRoom? ResetMatch(string roomCode)
    {
        string norm = NormalizeCode(roomCode);
        lock (_lock)
        {
            ChessRoom? room = HydrateRoomFromRedis(norm) ?? (_rooms.TryGetValue(norm, out ChessRoom? r) ? r : null);
            if (room == null) return null;

            int clockMs = GetInitialClockMs(room.TimeControl);
            if (room.Player1 != null)
            {
                room.Player1.IsReady = false;
                room.Player1.TimeRemainingMs = clockMs;
            }
            if (room.Player2 != null)
            {
                room.Player2.IsReady = false;
                room.Player2.TimeRemainingMs = clockMs;
            }

            room.CurrentTurn = "w";
            room.Fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
            room.Status = (room.Player1 != null && room.Player2 != null)
                ? ChessMatchStatus.WaitingForReady
                : ChessMatchStatus.WaitingForOpponent;
            room.WinnerSlot = null;
            room.WinReason = null;
            room.LastMoveTimeUtc = null;
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            SyncToRedis(room);
            return room;
        }
    }

    public IReadOnlyCollection<ChessRoom> GetActiveRooms()
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue[] members = db.SetMembers("kongor:chess:rooms");
                if (members.Length > 0)
                {
                    var result = new List<ChessRoom>();
                    foreach (RedisValue member in members)
                    {
                        if (member.IsNullOrEmpty) continue;
                        string code = member.ToString();
                        ChessRoom? room = GetRoom(code);
                        if (room != null && !room.IsEmpty)
                        {
                            result.Add(room);
                        }
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KongorChess] Failed to retrieve active rooms from Redis set, falling back to local memory");
            }
        }

        return _rooms.Values.Where(r => !r.IsEmpty).ToList();
    }
}
