// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public enum DevoMatchStatus
{
    WaitingForOpponent,
    WaitingForReady,
    Playing,
    Finished
}

public sealed class DevoPlayer
{
    public string PlayerId { get; init; } = "";
    public required string ConnectionId { get; set; }
    public required string Name { get; set; }
    public int Slot { get; init; } // 1 or 2
    public bool IsReady { get; set; }
    public bool WantsAr { get; set; }
    public DateTimeOffset JoinedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActiveAt { get; set; } = TimeProvider.System.GetUtcNow();
}

public sealed class DevoRoom
{
    public required string RoomCode { get; init; }
    public DevoPlayer? Player1 { get; set; }
    public DevoPlayer? Player2 { get; set; }
    public int Score1 { get; set; }
    public int Score2 { get; set; }
    public DevoMatchStatus Status { get; set; } = DevoMatchStatus.WaitingForOpponent;
    public int? WinnerSlot { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActivityAt { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastScoreAt { get; set; } = DateTimeOffset.MinValue;

    [JsonIgnore]
    public bool IsFull => Player1 != null && Player2 != null;
    [JsonIgnore]
    public bool IsEmpty => Player1 == null && Player2 == null;
}

public interface IDevoWarsRoomManager
{
    DevoRoom GetOrCreateRoom(string roomCode);
    DevoRoom? GetRoom(string roomCode);
    (DevoRoom Room, int AssignedSlot, bool Started) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null);
    (DevoRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr);
    (DevoRoom? Room, int VacatedSlot) Leave(string connectionId);
    (DevoRoom? Room, bool MatchWon, int WinnerSlot) RegisterScore(string roomCode, int scoringSlot, int winningThreshold = 10, int debounceMs = 0);
    DevoRoom? ResetMatch(string roomCode);
    IReadOnlyCollection<DevoRoom> GetActiveRooms();
}

public class DevoWarsRoomManager : IDevoWarsRoomManager
{
    public const int MaxActiveRooms = 256;

    private readonly ConcurrentDictionary<string, DevoRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToRoomMap = new();
    private readonly ILogger<DevoWarsRoomManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly Lock _lock = new();

    public DevoWarsRoomManager(ILogger<DevoWarsRoomManager> logger, IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                ISubscriber sub = _redis.GetSubscriber();
                sub.Subscribe(RedisChannel.Pattern("kongor:devowars:events:*"), (_, message) =>
                {
                    OnRemoteRoomEvent(message);
                });
                _logger.LogInformation("[DevoWars] Subscribed to Redis pub/sub backplane kongor:devowars:events:*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DevoWars] Failed to subscribe to Redis pub/sub backplane");
            }
        }
    }

    private void OnRemoteRoomEvent(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;
        try
        {
            DevoRoom? remoteRoom = JsonSerializer.Deserialize<DevoRoom>(message.ToString());
            if (remoteRoom != null && !string.IsNullOrWhiteSpace(remoteRoom.RoomCode))
            {
                string code = NormalizeCode(remoteRoom.RoomCode);
                lock (_lock)
                {
                    if (remoteRoom.IsEmpty)
                    {
                        _rooms.TryRemove(code, out _);
                        _logger.LogInformation("[DevoWars] Remote sync: Room {RoomCode} is empty, evicted from local cache", code);
                    }
                    else
                    {
                        _rooms[code] = remoteRoom;
                        _logger.LogDebug("[DevoWars] Remote sync: Updated room {RoomCode} in local cache (Status: {Status})", code, remoteRoom.Status);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to process remote room sync event");
        }
    }

    private void SyncToRedis(DevoRoom room)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(room);
            string key = $"kongor:devowars:room:{room.RoomCode}";
            _ = db.StringSetAsync(key, json, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
            _ = db.SetAddAsync("kongor:devowars:rooms", room.RoomCode, CommandFlags.FireAndForget);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:devowars:events:{room.RoomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to sync room {RoomCode} to Redis", room.RoomCode);
        }
    }

    private void RemoveFromRedis(string roomCode)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = $"kongor:devowars:room:{roomCode}";
            _ = db.KeyDeleteAsync(key, CommandFlags.FireAndForget);
            _ = db.SetRemoveAsync("kongor:devowars:rooms", roomCode, CommandFlags.FireAndForget);
            var emptyRoom = new DevoRoom { RoomCode = roomCode };
            string json = JsonSerializer.Serialize(emptyRoom);
            _ = _redis.GetSubscriber().PublishAsync(RedisChannel.Literal($"kongor:devowars:events:{roomCode}"), json, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to remove room {RoomCode} from Redis", roomCode);
        }
    }

    private void SaveConnectionMapping(string connectionId, string roomCode)
    {
        _connectionToRoomMap[connectionId] = roomCode;
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.StringSetAsync($"kongor:devowars:conn:{connectionId}", roomCode, TimeSpan.FromHours(2), flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to save connection mapping {ConnectionId} -> {RoomCode} to Redis", connectionId, roomCode);
        }
    }

    private void RemoveConnectionMapping(string connectionId)
    {
        _connectionToRoomMap.TryRemove(connectionId, out _);
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            _ = db.KeyDeleteAsync($"kongor:devowars:conn:{connectionId}", CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to remove connection mapping {ConnectionId} from Redis", connectionId);
        }
    }

    private DevoRoom? HydrateRoomFromRedis(string normalized)
    {
        if (_redis == null || !_redis.IsConnected) return null;
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue val = db.StringGet($"kongor:devowars:room:{normalized}");
            if (!val.IsNullOrEmpty)
            {
                DevoRoom? redisRoom = JsonSerializer.Deserialize<DevoRoom>(val.ToString());
                if (redisRoom != null)
                {
                    _rooms[normalized] = redisRoom;
                    return redisRoom;
                }
            }
            else
            {
                // Authoritative Redis store indicates room does not exist; evict stale local cache if any
                bool removed = _rooms.TryRemove(normalized, out _);
                _logger.LogInformation("[DevoWars] Redis key was empty for {RoomCode}, removedFromLocal={Removed}", normalized, removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DevoWars] Failed to hydrate room {RoomCode} from Redis: {Message}", normalized, ex.Message);
        }
        return null;
    }

    public DevoRoom GetOrCreateRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        DevoRoom? hydrated = HydrateRoomFromRedis(normalized);
        DevoRoom room;
        bool shouldSync = true;
        lock (_lock)
        {
            room = hydrated!;
            if (room == null && !_rooms.TryGetValue(normalized, out room!))
            {
                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                if (_rooms.Count >= MaxActiveRooms)
                {
                    foreach (KeyValuePair<string, DevoRoom> kvp in _rooms)
                    {
                        if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                        {
                            _rooms.TryRemove(kvp.Key, out _);
                        }
                    }

                    if (_rooms.Count >= MaxActiveRooms)
                    {
                        _logger.LogWarning(
                            "[SECURITY-ALERT] [DevoWars] Rejected GetOrCreateRoom allocation '{RoomCode}': MaxActiveRooms cap ({MaxActiveRooms}) reached",
                            normalized,
                            MaxActiveRooms);
                        room = new DevoRoom { RoomCode = normalized };
                        shouldSync = false;
                        return room;
                    }
                }

                _logger.LogInformation("[DevoWars] Created new room {RoomCode}", normalized);
                room = new DevoRoom { RoomCode = normalized };
                _rooms[normalized] = room;
            }
        }

        if (shouldSync)
        {
            SyncToRedis(room);
        }
        return room;
    }

    public DevoRoom? GetRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        DevoRoom? hydrated = HydrateRoomFromRedis(normalized);
        if (hydrated != null)
        {
            return hydrated;
        }

        if (_rooms.TryGetValue(normalized, out DevoRoom? room))
            return room;

        return null;
    }

    public (DevoRoom Room, int AssignedSlot, bool Started) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null)
    {
        string normalized = NormalizeCode(roomCode);
        string effectivePlayerId = string.IsNullOrWhiteSpace(playerId) ? $"p_{connectionId}" : playerId.Trim();
        lock (_lock)
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (!_rooms.ContainsKey(normalized) && _rooms.Count >= MaxActiveRooms)
            {
                foreach (KeyValuePair<string, DevoRoom> kvp in _rooms)
                {
                    if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt) > TimeSpan.FromMinutes(10))
                    {
                        _rooms.TryRemove(kvp.Key, out _);
                    }
                }

                if (_rooms.Count >= MaxActiveRooms)
                {
                    _logger.LogWarning(
                        "[SECURITY-ALERT] [DevoWars] Rejected room allocation '{RoomCode}' from connection {ConnectionId}: MaxActiveRooms cap ({MaxActiveRooms}) reached",
                        normalized,
                        connectionId,
                        MaxActiveRooms);
                    return (new DevoRoom { RoomCode = normalized }, 0, false);
                }
            }

            DevoRoom? existing = HydrateRoomFromRedis(normalized);
            DevoRoom room = existing ?? _rooms.GetOrAdd(normalized, code => new DevoRoom { RoomCode = code });
            room.LastActivityAt = now;

            // 1. Evict stale players who haven't had heartbeat/activity in 60s
            if (room.Player1 != null && (now - room.Player1.LastActiveAt) > TimeSpan.FromSeconds(60))
            {
                _logger.LogInformation("[DevoWars] Room {RoomCode} Slot 1 stale ({Name}, inactive for {Sec:F0}s). Evicting.",
                    normalized, room.Player1.Name, (now - room.Player1.LastActiveAt).TotalSeconds);
                RemoveConnectionMapping(room.Player1.ConnectionId);
                room.Player1 = null;
                room.Status = DevoMatchStatus.WaitingForOpponent;
            }
            if (room.Player2 != null && (now - room.Player2.LastActiveAt) > TimeSpan.FromSeconds(60))
            {
                _logger.LogInformation("[DevoWars] Room {RoomCode} Slot 2 stale ({Name}, inactive for {Sec:F0}s). Evicting.",
                    normalized, room.Player2.Name, (now - room.Player2.LastActiveAt).TotalSeconds);
                RemoveConnectionMapping(room.Player2.ConnectionId);
                room.Player2 = null;
                room.Status = DevoMatchStatus.WaitingForOpponent;
            }

            // 2. Check if player is reconnecting by PlayerId or ConnectionId
            if (room.Player1 != null && (room.Player1.PlayerId == effectivePlayerId || room.Player1.ConnectionId == connectionId))
            {
                _logger.LogInformation("[DevoWars] Player {PlayerName} ({PlayerId}) reconnecting to Slot 1 in Room {RoomCode}",
                    playerName, effectivePlayerId, normalized);
                RemoveConnectionMapping(room.Player1.ConnectionId);
                room.Player1.ConnectionId = connectionId;
                room.Player1.Name = playerName;
                room.Player1.LastActiveAt = now;
                SaveConnectionMapping(connectionId, normalized);
                SyncToRedis(room);
                return (room, 1, room.Status == DevoMatchStatus.Playing || room.Status == DevoMatchStatus.WaitingForReady);
            }

            if (room.Player2 != null && (room.Player2.PlayerId == effectivePlayerId || room.Player2.ConnectionId == connectionId))
            {
                _logger.LogInformation("[DevoWars] Player {PlayerName} ({PlayerId}) reconnecting to Slot 2 in Room {RoomCode}",
                    playerName, effectivePlayerId, normalized);
                RemoveConnectionMapping(room.Player2.ConnectionId);
                room.Player2.ConnectionId = connectionId;
                room.Player2.Name = playerName;
                room.Player2.LastActiveAt = now;
                SaveConnectionMapping(connectionId, normalized);
                SyncToRedis(room);
                return (room, 2, room.Status == DevoMatchStatus.Playing || room.Status == DevoMatchStatus.WaitingForReady);
            }

            int assignedSlot;
            if (room.Player1 == null)
            {
                assignedSlot = 1;
                room.Player1 = new DevoPlayer
                {
                    PlayerId = effectivePlayerId,
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 1
                };
            }
            else if (room.Player2 == null)
            {
                assignedSlot = 2;
                room.Player2 = new DevoPlayer
                {
                    PlayerId = effectivePlayerId,
                    ConnectionId = connectionId,
                    Name = playerName,
                    Slot = 2
                };
            }
            else
            {
                // Room is full! 3rd person rejected
                _logger.LogWarning("[DevoWars] Room {RoomCode} is FULL (2/2). Rejecting 3rd person {PlayerName} ({PlayerId})",
                    normalized, playerName, effectivePlayerId);
                return (room, 0, false);
            }

            SaveConnectionMapping(connectionId, normalized);

            bool bothInRoom = false;
            if (room.Player1 != null && room.Player2 != null && (room.Status == DevoMatchStatus.WaitingForOpponent || room.Status == DevoMatchStatus.WaitingForReady))
            {
                room.Status = DevoMatchStatus.WaitingForReady;
                room.Player1.IsReady = false;
                room.Player2.IsReady = false;
                bothInRoom = true;
                _logger.LogInformation("[DevoWars] Room {RoomCode} full! Waiting for ready check. P1 ({P1}) vs P2 ({P2})",
                    normalized, room.Player1.Name, room.Player2.Name);
            }
            else
            {
                _logger.LogInformation("[DevoWars] Player {PlayerName} ({ConnectionId}) joined Room {RoomCode} as Slot {Slot}",
                    playerName, connectionId, normalized, assignedSlot);
            }

            SyncToRedis(room);
            return (room, assignedSlot, bothInRoom);
        }
    }

    public (DevoRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            DevoRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out DevoRoom? r) ? r : null);
            if (room == null)
            {
                return (null, false);
            }

            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            DevoPlayer? player = null;
            if (room.Player1?.ConnectionId == connectionId) player = room.Player1;
            else if (room.Player2?.ConnectionId == connectionId) player = room.Player2;

            if (player == null)
            {
                _logger.LogWarning("[DevoWars] SetPlayerReady: Connection {ConnId} not matched in room {RoomCode} (P1: {P1Conn}, P2: {P2Conn})",
                    connectionId, normalized, room.Player1?.ConnectionId ?? "null", room.Player2?.ConnectionId ?? "null");
                return (room, false);
            }

            player.IsReady = true;
            player.WantsAr = wantsAr;
            player.LastActiveAt = TimeProvider.System.GetUtcNow();

            _logger.LogInformation("[DevoWars] Room {RoomCode} Player {Slot} ({Name}) is READY (AR: {WantsAr})",
                normalized, player.Slot, player.Name, wantsAr);

            bool bothReady = room.Player1?.IsReady == true && room.Player2?.IsReady == true;
            if (bothReady)
            {
                room.Status = DevoMatchStatus.Playing;
                _logger.LogInformation("[DevoWars] Room {RoomCode} both players ready! Match STARTED.", normalized);
            }

            SyncToRedis(room);
            return (room, bothReady);
        }
    }

    public (DevoRoom? Room, int VacatedSlot) Leave(string connectionId)
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
                        RedisValue val = db.StringGet($"kongor:devowars:conn:{connectionId}");
                        if (!val.IsNullOrEmpty)
                        {
                            roomCode = val.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[DevoWars] Failed to resolve connection mapping {ConnectionId} from Redis", connectionId);
                    }
                }
            }

            RemoveConnectionMapping(connectionId);

            if (string.IsNullOrWhiteSpace(roomCode))
            {
                return (null, 0);
            }

            string normalized = NormalizeCode(roomCode);
            DevoRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out DevoRoom? r) ? r : null);
            if (room == null)
            {
                return (null, 0);
            }

            int vacatedSlot = 0;
            if (room.Player1?.ConnectionId == connectionId)
            {
                vacatedSlot = 1;
                room.Player1 = null;
                _logger.LogInformation("[DevoWars] Player 1 left room {RoomCode}", normalized);
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                vacatedSlot = 2;
                room.Player2 = null;
                _logger.LogInformation("[DevoWars] Player 2 left room {RoomCode}", normalized);
            }

            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            if (room.IsEmpty)
            {
                _rooms.TryRemove(normalized, out _);
                RemoveFromRedis(normalized);
                _logger.LogInformation("[DevoWars] Room {RoomCode} is empty, purged from memory and Redis", normalized);
            }
            else
            {
                // Pause match & clear readiness
                room.Status = DevoMatchStatus.WaitingForOpponent;
                if (room.Player1 != null) room.Player1.IsReady = false;
                if (room.Player2 != null) room.Player2.IsReady = false;
                SyncToRedis(room);
            }

            return (room, vacatedSlot);
        }
    }

    public (DevoRoom? Room, bool MatchWon, int WinnerSlot) RegisterScore(string roomCode, int scoringSlot, int winningThreshold = 10, int debounceMs = 0)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            DevoRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out DevoRoom? r) ? r : null);
            if (room == null)
            {
                return (null, false, 0);
            }

            if (room.Status != DevoMatchStatus.Playing)
            {
                return (room, false, 0);
            }

            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (debounceMs > 0 && (now - room.LastScoreAt).TotalMilliseconds < debounceMs)
            {
                _logger.LogInformation("[DevoWars] Room {RoomCode} hit debounce active ({Elapsed:F2}s since last score). Ignoring duplicate report from slot {Slot}.",
                    normalized, (now - room.LastScoreAt).TotalSeconds, scoringSlot);
                return (room, false, 0);
            }

            room.LastScoreAt = now;
            room.LastActivityAt = now;

            if (scoringSlot == 1) room.Score1++;
            else if (scoringSlot == 2) room.Score2++;

            _logger.LogInformation("[DevoWars] Room {RoomCode} Score: P1 [{P1Score}] - [{P2Score}] P2 (Scored by Slot {Slot})",
                normalized, room.Score1, room.Score2, scoringSlot);

            bool matchWon = false;
            int winner = 0;

            if (room.Score1 >= winningThreshold)
            {
                matchWon = true;
                winner = 1;
                room.WinnerSlot = 1;
                room.Status = DevoMatchStatus.Finished;
                _logger.LogInformation("[DevoWars] Room {RoomCode} VICTORY for Player 1 ({P1Score}-{P2Score})!",
                    normalized, room.Score1, room.Score2);
            }
            else if (room.Score2 >= winningThreshold)
            {
                matchWon = true;
                winner = 2;
                room.WinnerSlot = 2;
                room.Status = DevoMatchStatus.Finished;
                _logger.LogInformation("[DevoWars] Room {RoomCode} VICTORY for Player 2 ({P2Score}-{P1Score})!",
                    normalized, room.Score2, room.Score1);
            }

            SyncToRedis(room);
            return (room, matchWon, winner);
        }
    }

    public DevoRoom? ResetMatch(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            DevoRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out DevoRoom? r) ? r : null);
            if (room == null)
            {
                return null;
            }

            room.Score1 = 0;
            room.Score2 = 0;
            room.WinnerSlot = null;
            room.LastScoreAt = DateTimeOffset.MinValue;
            room.Status = room.IsFull ? DevoMatchStatus.WaitingForReady : DevoMatchStatus.WaitingForOpponent;
            if (room.Player1 != null) room.Player1.IsReady = false;
            if (room.Player2 != null) room.Player2.IsReady = false;
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            _logger.LogInformation("[DevoWars] Room {RoomCode} match scores reset to 0-0", normalized);
            SyncToRedis(room);
            return room;
        }
    }

    public IReadOnlyCollection<DevoRoom> GetActiveRooms()
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue[] members = db.SetMembers("kongor:devowars:rooms");
                if (members.Length > 0)
                {
                    var result = new List<DevoRoom>();
                    foreach (RedisValue member in members)
                    {
                        if (member.IsNullOrEmpty) continue;
                        string code = member.ToString();
                        DevoRoom? room = GetRoom(code);
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
                _logger.LogWarning(ex, "[DevoWars] Failed to retrieve active rooms from Redis set, falling back to local memory");
            }
        }

        return _rooms.Values.Where(r => !r.IsEmpty).ToArray();
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "HOOK";
        string clean = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "HOOK" : (clean.Length > 8 ? clean[..8] : clean);
    }
}
