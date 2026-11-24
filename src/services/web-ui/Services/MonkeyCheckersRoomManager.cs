// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public enum CheckersMatchStatus
{
    WaitingForOpponent,
    WaitingForReady,
    Playing,
    Finished
}

public sealed class CheckersPlayer
{
    public string PlayerId { get; init; } = "";
    public required string ConnectionId { get; set; }
    public required string Name { get; set; }
    public int Slot { get; init; } // 1 = Legion, 2 = Hellbourne
    public bool IsReady { get; set; }
    public bool WantsAr { get; set; }
    public DateTimeOffset JoinedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActiveAt { get; set; } = TimeProvider.System.GetUtcNow();
}

public sealed class CheckersRoom
{
    public required string RoomCode { get; init; }
    public CheckersPlayer? Player1 { get; set; } // Legion (Green)
    public CheckersPlayer? Player2 { get; set; } // Hellbourne (Red)
    public string CurrentTurn { get; set; } = "legion"; // "legion" or "hellbourne"
    public CheckersMatchStatus Status { get; set; } = CheckersMatchStatus.WaitingForOpponent;
    public int? WinnerSlot { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActivityAt { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonIgnore]
    public bool IsFull => Player1 != null && Player2 != null;
    [JsonIgnore]
    public bool IsEmpty => Player1 == null && Player2 == null;
}

public interface IMonkeyCheckersRoomManager
{
    CheckersRoom GetOrCreateRoom(string roomCode);
    CheckersRoom? GetRoom(string roomCode);
    (CheckersRoom Room, int AssignedSlot, bool BothInRoom) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null);
    (CheckersRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr);
    (CheckersRoom? Room, int VacatedSlot) Leave(string connectionId);
    (CheckersRoom? Room, bool IsValid, string NextTurn) RegisterMove(string roomCode, string connectionId, int fromRow, int fromCol, int toRow, int toCol, bool isJump);
    CheckersRoom? ResetMatch(string roomCode);
    IReadOnlyCollection<CheckersRoom> GetActiveRooms();
}

public class MonkeyCheckersRoomManager : IMonkeyCheckersRoomManager
{
    public const int MaxActiveRooms = 256;

    private readonly ConcurrentDictionary<string, CheckersRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToRoomMap = new();
    private readonly ILogger<MonkeyCheckersRoomManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly Lock _lock = new();

    public MonkeyCheckersRoomManager(ILogger<MonkeyCheckersRoomManager> logger, IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                ISubscriber sub = _redis.GetSubscriber();
                sub.Subscribe(RedisChannel.Pattern("kongor:checkers:events:*"), (_, message) =>
                {
                    OnRemoteRoomEvent(message);
                });
                _logger.LogInformation("[MonkeyCheckers] Subscribed to Redis pub/sub backplane kongor:checkers:events:*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MonkeyCheckers] Failed to subscribe to Redis pub/sub backplane");
            }
        }
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "MK" + Random.Shared.Next(10, 99);
        var clean = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length > 8) clean = clean[..8];
        return clean.Length >= 2 ? clean : "MK" + Random.Shared.Next(10, 99);
    }

    private void OnRemoteRoomEvent(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;
        try
        {
            CheckersRoom? remoteRoom = JsonSerializer.Deserialize<CheckersRoom>(message.ToString());
            if (remoteRoom != null && !string.IsNullOrWhiteSpace(remoteRoom.RoomCode))
            {
                string code = NormalizeCode(remoteRoom.RoomCode);
                lock (_lock)
                {
                    if (remoteRoom.IsEmpty)
                    {
                        _rooms.TryRemove(code, out _);
                        _logger.LogInformation("[MonkeyCheckers] Remote sync: Room {RoomCode} is empty, evicted from local cache", code);
                    }
                    else
                    {
                        _rooms[code] = remoteRoom;
                        _logger.LogDebug("[MonkeyCheckers] Remote sync: Updated room {RoomCode} in local cache (Status: {Status})", code, remoteRoom.Status);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to process remote room sync event");
        }
    }

    private void SyncToRedis(CheckersRoom room)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(room);
            string key = $"kongor:checkers:room:{room.RoomCode}";
            _ = db.StringSetAsync(key, json, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
            _ = db.SetAddAsync("kongor:checkers:rooms", room.RoomCode, CommandFlags.FireAndForget);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:checkers:events:{room.RoomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to sync room {RoomCode} to Redis", room.RoomCode);
        }
    }

    private void RemoveFromRedis(string roomCode)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = $"kongor:checkers:room:{roomCode}";
            _ = db.KeyDeleteAsync(key, CommandFlags.FireAndForget);
            _ = db.SetRemoveAsync("kongor:checkers:rooms", roomCode, CommandFlags.FireAndForget);
            var emptyRoom = new CheckersRoom { RoomCode = roomCode };
            string json = JsonSerializer.Serialize(emptyRoom);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:checkers:events:{roomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to remove room {RoomCode} from Redis", roomCode);
        }
    }

    private void SaveConnectionMapping(string connectionId, string roomCode)
    {
        _connectionToRoomMap[connectionId] = roomCode;
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.StringSetAsync($"kongor:checkers:conn:{connectionId}", roomCode, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to save connection mapping {ConnectionId} -> {RoomCode} to Redis", connectionId, roomCode);
        }
    }

    private void RemoveConnectionMapping(string connectionId)
    {
        _connectionToRoomMap.TryRemove(connectionId, out _);
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.KeyDeleteAsync($"kongor:checkers:conn:{connectionId}", CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to remove connection mapping {ConnectionId} from Redis", connectionId);
        }
    }

    private CheckersRoom? HydrateRoomFromRedis(string normalized)
    {
        if (_redis == null || !_redis.IsConnected) return null;
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue val = db.StringGet($"kongor:checkers:room:{normalized}");
            if (!val.IsNullOrEmpty)
            {
                CheckersRoom? redisRoom = JsonSerializer.Deserialize<CheckersRoom>(val.ToString());
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
            _logger.LogWarning(ex, "[MonkeyCheckers] Failed to hydrate room {RoomCode} from Redis", normalized);
        }
        return null;
    }

    public CheckersRoom GetOrCreateRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        CheckersRoom? hydrated = HydrateRoomFromRedis(normalized);
        CheckersRoom room;
        lock (_lock)
        {
            room = hydrated!;
            if (room == null && !_rooms.TryGetValue(normalized, out room!))
            {
                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                if (_rooms.Count >= MaxActiveRooms)
                {
                    foreach (KeyValuePair<string, CheckersRoom> kvp in _rooms)
                    {
                        if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                        {
                            _rooms.TryRemove(kvp.Key, out _);
                        }
                    }

                    if (_rooms.Count >= MaxActiveRooms)
                    {
                        _logger.LogWarning(
                            "[SECURITY-ALERT] [MonkeyCheckers] Rejected GetOrCreateRoom allocation '{RoomCode}': MaxActiveRooms cap ({MaxActiveRooms}) reached",
                            normalized,
                            MaxActiveRooms);
                        return new CheckersRoom
                        {
                            RoomCode = normalized,
                            Status = CheckersMatchStatus.WaitingForOpponent,
                            CurrentTurn = "legion"
                        };
                    }
                }

                room = new CheckersRoom
                {
                    RoomCode = normalized,
                    Status = CheckersMatchStatus.WaitingForOpponent,
                    CurrentTurn = "legion"
                };
                _rooms[normalized] = room;
            }
        }
        SyncToRedis(room);
        return room;
    }

    public CheckersRoom? GetRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        CheckersRoom? hydrated = HydrateRoomFromRedis(normalized);
        if (hydrated != null)
        {
            return hydrated;
        }

        if (_rooms.TryGetValue(normalized, out var room))
            return room;

        return null;
    }

    public (CheckersRoom Room, int AssignedSlot, bool BothInRoom) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null)
    {
        string normalized = NormalizeCode(roomCode);
        string effectivePlayerId = string.IsNullOrWhiteSpace(playerId) ? connectionId : playerId.Trim();

        lock (_lock)
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (!_rooms.ContainsKey(normalized) && _rooms.Count >= MaxActiveRooms)
            {
                foreach (KeyValuePair<string, CheckersRoom> kvp in _rooms)
                {
                    if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                    {
                        _rooms.TryRemove(kvp.Key, out _);
                    }
                }

                if (_rooms.Count >= MaxActiveRooms)
                {
                    _logger.LogWarning(
                        "[SECURITY-ALERT] [MonkeyCheckers] Rejected room allocation '{RoomCode}' from connection {ConnectionId}: MaxActiveRooms cap ({MaxActiveRooms}) reached",
                        normalized,
                        connectionId,
                        MaxActiveRooms);
                    return (new CheckersRoom { RoomCode = normalized }, 0, false);
                }
            }

            CheckersRoom? existing = HydrateRoomFromRedis(normalized);
            CheckersRoom room = existing ?? _rooms.GetOrAdd(normalized, code => new CheckersRoom
            {
                RoomCode = code,
                Status = CheckersMatchStatus.WaitingForOpponent,
                CurrentTurn = "legion"
            });
            room.LastActivityAt = now;

            // Reconnecting player check
            if (room.Player1 != null && (room.Player1.ConnectionId == connectionId || (!string.IsNullOrEmpty(room.Player1.PlayerId) && room.Player1.PlayerId == effectivePlayerId)))
            {
                RemoveConnectionMapping(room.Player1.ConnectionId);
                room.Player1.ConnectionId = connectionId;
                room.Player1.Name = playerName;
                room.Player1.LastActiveAt = TimeProvider.System.GetUtcNow();
                SaveConnectionMapping(connectionId, normalized);
                SyncToRedis(room);
                return (room, 1, room.IsFull);
            }

            if (room.Player2 != null && (room.Player2.ConnectionId == connectionId || (!string.IsNullOrEmpty(room.Player2.PlayerId) && room.Player2.PlayerId == effectivePlayerId)))
            {
                RemoveConnectionMapping(room.Player2.ConnectionId);
                room.Player2.ConnectionId = connectionId;
                room.Player2.Name = playerName;
                room.Player2.LastActiveAt = TimeProvider.System.GetUtcNow();
                SaveConnectionMapping(connectionId, normalized);
                SyncToRedis(room);
                return (room, 2, room.IsFull);
            }

            // Assign Slot 1 (Host - Legion)
            if (room.Player1 == null)
            {
                room.Player1 = new CheckersPlayer
                {
                    PlayerId = effectivePlayerId,
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 1,
                    IsReady = false,
                    WantsAr = false
                };
                SaveConnectionMapping(connectionId, normalized);
                room.Status = CheckersMatchStatus.WaitingForOpponent;
                SyncToRedis(room);
                return (room, 1, false);
            }

            // Assign Slot 2 (Challenger - Hellbourne)
            if (room.Player2 == null)
            {
                room.Player2 = new CheckersPlayer
                {
                    PlayerId = effectivePlayerId,
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 2,
                    IsReady = false,
                    WantsAr = false
                };
                SaveConnectionMapping(connectionId, normalized);
                room.Status = CheckersMatchStatus.WaitingForReady;
                SyncToRedis(room);
                return (room, 2, true);
            }

            // Room is full (2/2) -> Reject 3rd player
            return (room, 0, false);
        }
    }

    public (CheckersRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            CheckersRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out var r) ? r : null);
            if (room == null) return (null, false);

            CheckersPlayer? player = null;
            if (room.Player1?.ConnectionId == connectionId) player = room.Player1;
            else if (room.Player2?.ConnectionId == connectionId) player = room.Player2;

            if (player == null) return (room, false);

            player.IsReady = true;
            player.WantsAr = wantsAr;
            player.LastActiveAt = TimeProvider.System.GetUtcNow();
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            bool bothReady = room.Player1?.IsReady == true && room.Player2?.IsReady == true;
            if (bothReady)
            {
                room.Status = CheckersMatchStatus.Playing;
                room.CurrentTurn = "legion"; // Legion always moves first
            }

            SyncToRedis(room);
            return (room, bothReady);
        }
    }

    public (CheckersRoom? Room, bool IsValid, string NextTurn) RegisterMove(string roomCode, string connectionId, int fromRow, int fromCol, int toRow, int toCol, bool isJump)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            CheckersRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out var r) ? r : null);
            if (room == null) return (null, false, "");

            int slot = room.Player1?.ConnectionId == connectionId ? 1 : (room.Player2?.ConnectionId == connectionId ? 2 : 0);
            if (slot == 0) return (room, false, room.CurrentTurn);

            string expectedTurn = slot == 1 ? "legion" : "hellbourne";
            if (room.CurrentTurn != expectedTurn && room.Status == CheckersMatchStatus.Playing)
            {
                _logger.LogWarning("[MonkeyCheckers] Move rejected in {RoomCode}: slot {Slot} attempted to move during {Turn}'s turn",
                    normalized, slot, room.CurrentTurn);
                return (room, false, room.CurrentTurn);
            }

            // Move accepted -> alternate turn
            room.CurrentTurn = (room.CurrentTurn == "legion") ? "hellbourne" : "legion";
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            SyncToRedis(room);
            return (room, true, room.CurrentTurn);
        }
    }

    public (CheckersRoom? Room, int VacatedSlot) Leave(string connectionId)
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
                        RedisValue val = db.StringGet($"kongor:checkers:conn:{connectionId}");
                        if (!val.IsNullOrEmpty)
                        {
                            roomCode = val.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MonkeyCheckers] Failed to resolve connection mapping {ConnectionId} from Redis", connectionId);
                    }
                }
            }

            RemoveConnectionMapping(connectionId);

            if (string.IsNullOrWhiteSpace(roomCode)) return (null, 0);

            string normalized = NormalizeCode(roomCode);
            CheckersRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out var r) ? r : null);
            if (room == null) return (null, 0);

            int vacatedSlot = 0;
            if (room.Player1?.ConnectionId == connectionId)
            {
                room.Player1 = null;
                vacatedSlot = 1;
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                room.Player2 = null;
                vacatedSlot = 2;
            }

            if (vacatedSlot > 0)
            {
                room.LastActivityAt = TimeProvider.System.GetUtcNow();
                if (room.IsEmpty)
                {
                    _rooms.TryRemove(normalized, out _);
                    RemoveFromRedis(normalized);
                    return (null, vacatedSlot);
                }

                // If one player remains, revert room to WaitingForOpponent and reset readiness
                room.Status = CheckersMatchStatus.WaitingForOpponent;
                if (room.Player1 != null) room.Player1.IsReady = false;
                if (room.Player2 != null) room.Player2.IsReady = false;
                SyncToRedis(room);
            }

            return (room, vacatedSlot);
        }
    }

    public CheckersRoom? ResetMatch(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            CheckersRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out var r) ? r : null);
            if (room == null) return null;

            room.CurrentTurn = "legion";
            room.WinnerSlot = null;
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            if (room.Player1 != null) room.Player1.IsReady = false;
            if (room.Player2 != null) room.Player2.IsReady = false;

            room.Status = room.IsFull ? CheckersMatchStatus.WaitingForReady : CheckersMatchStatus.WaitingForOpponent;

            SyncToRedis(room);
            return room;
        }
    }

    public IReadOnlyCollection<CheckersRoom> GetActiveRooms()
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue[] members = db.SetMembers("kongor:checkers:rooms");
                if (members.Length > 0)
                {
                    var result = new List<CheckersRoom>();
                    foreach (RedisValue member in members)
                    {
                        if (member.IsNullOrEmpty) continue;
                        string code = member.ToString();
                        CheckersRoom? room = GetRoom(code);
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
                _logger.LogWarning(ex, "[MonkeyCheckers] Failed to retrieve active rooms from Redis set, falling back to local memory");
            }
        }

        return _rooms.Values.Where(r => !r.IsEmpty).ToList();
    }
}
