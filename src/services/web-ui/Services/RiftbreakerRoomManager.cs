// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public enum RiftbreakerMatchStatus
{
    WaitingForOpponent,
    WaitingForReady,
    Playing,
    HeroDraft,
    CardDraft,
    Finished
}

public static class RiftbreakerCatalog
{
    public const int DefaultWinningScore = 11;

    public static readonly IReadOnlyList<string> ValidHeroes =
    [
        "chronos",
        "behemoth",
        "devourer",
        "bubbles",
        "flux",
        "doctor_repulsor"
    ];

    public static readonly IReadOnlyList<string> ValidRiftCards =
    [
        "portal_sidelines",
        "twin_illusion",
        "prism_obelisks",
        "gravity_conduit"
    ];

    public static string? NormalizeHero(string? heroId)
    {
        if (string.IsNullOrWhiteSpace(heroId)) return null;
        string clean = heroId.Trim().ToLowerInvariant();
        return ValidHeroes.Contains(clean, StringComparer.OrdinalIgnoreCase) ? clean : null;
    }

    public static string? NormalizeRiftCard(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        string clean = cardId.Trim().ToLowerInvariant();
        return ValidRiftCards.Contains(clean, StringComparer.OrdinalIgnoreCase) ? clean : null;
    }
}

public sealed class RiftbreakerPlayer
{
    public string PlayerId { get; init; } = "";
    public required string ConnectionId { get; set; }
    public required string Name { get; set; }
    public int Slot { get; init; } // 1 or 2
    public bool IsReady { get; set; }
    public bool WantsAr { get; set; }
    public string? SelectedHero { get; set; }
    public string? SelectedRiftCard { get; set; }
    public DateTimeOffset JoinedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActiveAt { get; set; } = TimeProvider.System.GetUtcNow();
}

public sealed class RiftbreakerRoom
{
    public required string RoomCode { get; init; }
    public RiftbreakerPlayer? Player1 { get; set; }
    public RiftbreakerPlayer? Player2 { get; set; }
    public int Score1 { get; set; }
    public int Score2 { get; set; }
    public int Act { get; set; } = 1;
    public bool HeroDraftTriggered { get; set; }
    public bool CardDraftTriggered { get; set; }
    public bool BossSpawned { get; set; }
    public List<string> ActiveRiftCards { get; set; } = [];
    public RiftbreakerMatchStatus Status { get; set; } = RiftbreakerMatchStatus.WaitingForOpponent;
    public int? WinnerSlot { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastActivityAt { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastScoreAt { get; set; } = DateTimeOffset.MinValue;

    [JsonIgnore]
    public bool IsFull => Player1 != null && Player2 != null;

    [JsonIgnore]
    public bool IsEmpty => Player1 == null && Player2 == null;

    [JsonIgnore]
    public bool BothHeroesSelected =>
        !string.IsNullOrEmpty(Player1?.SelectedHero) &&
        !string.IsNullOrEmpty(Player2?.SelectedHero);
}

public interface IRiftbreakerRoomManager
{
    RiftbreakerRoom GetOrCreateRoom(string roomCode);
    RiftbreakerRoom? GetRoom(string roomCode);
    (RiftbreakerRoom Room, int AssignedSlot, bool Started) JoinRoom(string roomCode, string connectionId, string playerName, string? playerId = null);
    (RiftbreakerRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr);
    (RiftbreakerRoom? Room, int Slot, bool BothSelected, string? CanonicalHero) SelectHero(string roomCode, string connectionId, string heroId);
    (RiftbreakerRoom? Room, int Slot, bool Accepted, string? CanonicalCard) SelectRiftCard(string roomCode, string connectionId, string cardId);
    (RiftbreakerRoom? Room, int VacatedSlot) Leave(string connectionId);
    (RiftbreakerRoom? Room, bool MatchWon, int WinnerSlot, bool TriggeredHeroDraft, bool TriggeredCardDraft, bool TriggeredBoss) RegisterScore(
        string roomCode,
        int scoringSlot,
        int winningThreshold = RiftbreakerCatalog.DefaultWinningScore,
        int debounceMs = 450);
    RiftbreakerRoom? ResetMatch(string roomCode, bool keepHeroes = false);
    IReadOnlyCollection<RiftbreakerRoom> GetActiveRooms();
}

public class RiftbreakerRoomManager : IRiftbreakerRoomManager
{
    public const int MaxActiveRooms = 256;

    private readonly ConcurrentDictionary<string, RiftbreakerRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionToRoomMap = new();
    private readonly ILogger<RiftbreakerRoomManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly Lock _lock = new();

    public RiftbreakerRoomManager(ILogger<RiftbreakerRoomManager> logger, IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                ISubscriber sub = _redis.GetSubscriber();
                sub.Subscribe(RedisChannel.Pattern("kongor:riftbreaker:events:*"), (_, message) =>
                {
                    OnRemoteRoomEvent(message);
                });
                _logger.LogInformation("[Riftbreaker] Subscribed to Redis pub/sub backplane kongor:riftbreaker:events:*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Riftbreaker] Failed to subscribe to Redis pub/sub backplane");
            }
        }
    }

    private void OnRemoteRoomEvent(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;
        try
        {
            RiftbreakerRoom? remoteRoom = JsonSerializer.Deserialize<RiftbreakerRoom>(message.ToString());
            if (remoteRoom != null && !string.IsNullOrEmpty(remoteRoom.RoomCode))
            {
                _rooms[remoteRoom.RoomCode] = remoteRoom;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Riftbreaker] Failed to deserialize remote room event");
        }
    }

    private void SyncToRedis(RiftbreakerRoom room)
    {
        if (_redis == null || !_redis.IsConnected) return;
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(room);
            string key = $"kongor:riftbreaker:room:{room.RoomCode}";
            _ = db.StringSetAsync(key, json, TimeSpan.FromMinutes(30), flags: CommandFlags.FireAndForget);

            if (room.IsEmpty)
            {
                _ = db.SetRemoveAsync("kongor:riftbreaker:rooms", room.RoomCode, CommandFlags.FireAndForget);
            }
            else
            {
                _ = db.SetAddAsync("kongor:riftbreaker:rooms", room.RoomCode, CommandFlags.FireAndForget);
            }

            _ = _redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal($"kongor:riftbreaker:events:{room.RoomCode}"),
                json,
                CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Riftbreaker] Failed to sync room {RoomCode} to Redis", room.RoomCode);
        }
    }

    private RiftbreakerRoom? HydrateRoomFromRedis(string normalizedCode)
    {
        if (_redis == null || !_redis.IsConnected) return null;
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue val = db.StringGet($"kongor:riftbreaker:room:{normalizedCode}");
            if (!val.IsNullOrEmpty)
            {
                RiftbreakerRoom? room = JsonSerializer.Deserialize<RiftbreakerRoom>(val.ToString());
                if (room != null)
                {
                    _rooms[normalizedCode] = room;
                    return room;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Riftbreaker] Failed to hydrate room {RoomCode} from Redis", normalizedCode);
        }
        return null;
    }

    private void CleanupStaleRoomsIfNeeded()
    {
        if (_rooms.Count < MaxActiveRooms) return;

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        foreach (var kvp in _rooms)
        {
            if (kvp.Value.IsEmpty || (now - kvp.Value.LastActivityAt).TotalMinutes > 30)
            {
                if (_rooms.TryRemove(kvp.Key, out _))
                {
                    _logger.LogInformation("[Riftbreaker] Evicted inactive room {RoomCode}", kvp.Key);
                }
            }
        }

        if (_rooms.Count >= MaxActiveRooms)
        {
            var oldest = _rooms.OrderBy(r => r.Value.LastActivityAt).FirstOrDefault();
            if (oldest.Key != null && _rooms.TryRemove(oldest.Key, out _))
            {
                _logger.LogWarning("[Riftbreaker] Room capacity {Max} reached; evicted oldest room {RoomCode}", MaxActiveRooms, oldest.Key);
            }
        }
    }

    public RiftbreakerRoom GetOrCreateRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            if (_rooms.TryGetValue(normalized, out RiftbreakerRoom? existing))
            {
                existing.LastActivityAt = TimeProvider.System.GetUtcNow();
                return existing;
            }

            RiftbreakerRoom? hydrated = HydrateRoomFromRedis(normalized);
            if (hydrated != null)
            {
                hydrated.LastActivityAt = TimeProvider.System.GetUtcNow();
                return hydrated;
            }

            CleanupStaleRoomsIfNeeded();

            var created = new RiftbreakerRoom
            {
                RoomCode = normalized,
                Status = RiftbreakerMatchStatus.WaitingForOpponent,
                CreatedAt = TimeProvider.System.GetUtcNow(),
                LastActivityAt = TimeProvider.System.GetUtcNow()
            };
            _rooms[normalized] = created;
            _logger.LogInformation("[Riftbreaker] Created room {RoomCode}", normalized);
            SyncToRedis(created);
            return created;
        }
    }

    public RiftbreakerRoom? GetRoom(string roomCode)
    {
        string normalized = NormalizeCode(roomCode);
        if (_rooms.TryGetValue(normalized, out RiftbreakerRoom? room))
        {
            return room;
        }
        return HydrateRoomFromRedis(normalized);
    }

    public (RiftbreakerRoom Room, int AssignedSlot, bool Started) JoinRoom(
        string roomCode,
        string connectionId,
        string playerName,
        string? playerId = null)
    {
        string normalized = NormalizeCode(roomCode);
        string safeName = string.IsNullOrWhiteSpace(playerName) ? "Champion" : playerName.Trim();
        if (safeName.Length > 28) safeName = safeName[..28];

        lock (_lock)
        {
            RiftbreakerRoom room = GetOrCreateRoom(normalized);
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            room.LastActivityAt = now;

            // Check reconnect by ConnectionId or matching PlayerId
            if (room.Player1 != null &&
                (room.Player1.ConnectionId == connectionId ||
                 (!string.IsNullOrEmpty(playerId) && string.Equals(room.Player1.PlayerId, playerId, StringComparison.Ordinal))))
            {
                if (!string.IsNullOrEmpty(room.Player1.ConnectionId) && room.Player1.ConnectionId != connectionId)
                {
                    _connectionToRoomMap.TryRemove(room.Player1.ConnectionId, out _);
                }

                room.Player1.ConnectionId = connectionId;
                room.Player1.Name = safeName;
                room.Player1.LastActiveAt = now;
                _connectionToRoomMap[connectionId] = normalized;
                _logger.LogInformation("[Riftbreaker] Player 1 ({Name}) reconnected to room {RoomCode}", safeName, normalized);
                SyncToRedis(room);
                return (room, 1, room.IsFull);
            }

            if (room.Player2 != null &&
                (room.Player2.ConnectionId == connectionId ||
                 (!string.IsNullOrEmpty(playerId) && string.Equals(room.Player2.PlayerId, playerId, StringComparison.Ordinal))))
            {
                if (!string.IsNullOrEmpty(room.Player2.ConnectionId) && room.Player2.ConnectionId != connectionId)
                {
                    _connectionToRoomMap.TryRemove(room.Player2.ConnectionId, out _);
                }

                room.Player2.ConnectionId = connectionId;
                room.Player2.Name = safeName;
                room.Player2.LastActiveAt = now;
                _connectionToRoomMap[connectionId] = normalized;
                _logger.LogInformation("[Riftbreaker] Player 2 ({Name}) reconnected to room {RoomCode}", safeName, normalized);
                SyncToRedis(room);
                return (room, 2, room.IsFull);
            }

            int assignedSlot;
            if (room.Player1 == null)
            {
                assignedSlot = 1;
                room.Player1 = new RiftbreakerPlayer
                {
                    PlayerId = playerId ?? "",
                    ConnectionId = connectionId,
                    Name = safeName,
                    Slot = 1,
                    JoinedAt = now,
                    LastActiveAt = now
                };
            }
            else if (room.Player2 == null)
            {
                assignedSlot = 2;
                room.Player2 = new RiftbreakerPlayer
                {
                    PlayerId = playerId ?? "",
                    ConnectionId = connectionId,
                    Name = safeName,
                    Slot = 2,
                    JoinedAt = now,
                    LastActiveAt = now
                };
            }
            else
            {
                _logger.LogWarning("[Riftbreaker] Room {RoomCode} rejected {ConnId} ({Name}): room full (2/2)",
                    normalized, connectionId, safeName);
                return (room, 0, false);
            }

            _connectionToRoomMap[connectionId] = normalized;
            bool started = false;
            if (room.IsFull && room.Status == RiftbreakerMatchStatus.WaitingForOpponent)
            {
                room.Status = RiftbreakerMatchStatus.WaitingForReady;
                started = true;
            }

            _logger.LogInformation("[Riftbreaker] {Name} joined room {RoomCode} in Slot {Slot} (Status: {Status})",
                safeName, normalized, assignedSlot, room.Status);
            SyncToRedis(room);
            return (room, assignedSlot, started);
        }
    }

    public (RiftbreakerRoom? Room, bool BothReady) SetPlayerReady(string roomCode, string connectionId, bool wantsAr)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out RiftbreakerRoom? r) ? r : null);
            if (room == null) return (null, false);

            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            room.LastActivityAt = now;

            if (room.Player1?.ConnectionId == connectionId)
            {
                room.Player1.IsReady = true;
                room.Player1.WantsAr = wantsAr;
                room.Player1.LastActiveAt = now;
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                room.Player2.IsReady = true;
                room.Player2.WantsAr = wantsAr;
                room.Player2.LastActiveAt = now;
            }
            else
            {
                return (room, false);
            }

            bool bothReady = room.Player1?.IsReady == true && room.Player2?.IsReady == true;
            if (bothReady && room.Status == RiftbreakerMatchStatus.WaitingForReady)
            {
                room.Status = RiftbreakerMatchStatus.Playing;
                room.Act = 1;
                _logger.LogInformation("[Riftbreaker] Room {RoomCode} both players ready -> Playing (Act 1)", normalized);
            }

            SyncToRedis(room);
            return (room, bothReady);
        }
    }

    public (RiftbreakerRoom? Room, int Slot, bool BothSelected, string? CanonicalHero) SelectHero(
        string roomCode,
        string connectionId,
        string heroId)
    {
        string normalized = NormalizeCode(roomCode);
        string? canonicalHero = RiftbreakerCatalog.NormalizeHero(heroId);
        if (canonicalHero == null)
        {
            _logger.LogWarning("[Riftbreaker] Rejected invalid heroId '{HeroId}' in room {RoomCode}", heroId, normalized);
            return (GetRoom(normalized), 0, false, null);
        }

        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out RiftbreakerRoom? r) ? r : null);
            if (room == null) return (null, 0, false, null);

            int slot = 0;
            if (room.Player1?.ConnectionId == connectionId)
            {
                slot = 1;
                room.Player1.SelectedHero = canonicalHero;
                room.Player1.LastActiveAt = TimeProvider.System.GetUtcNow();
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                slot = 2;
                room.Player2.SelectedHero = canonicalHero;
                room.Player2.LastActiveAt = TimeProvider.System.GetUtcNow();
            }
            else
            {
                return (room, 0, false, null);
            }

            room.LastActivityAt = TimeProvider.System.GetUtcNow();
            bool bothSelected = room.BothHeroesSelected;
            if (bothSelected && room.Status == RiftbreakerMatchStatus.HeroDraft)
            {
                room.Status = RiftbreakerMatchStatus.Playing;
                room.Act = Math.Max(room.Act, 2);
                _logger.LogInformation("[Riftbreaker] Room {RoomCode} Hero Draft complete ({H1} vs {H2}) -> Act {Act}",
                    normalized, room.Player1?.SelectedHero, room.Player2?.SelectedHero, room.Act);
            }

            SyncToRedis(room);
            return (room, slot, bothSelected, canonicalHero);
        }
    }

    public (RiftbreakerRoom? Room, int Slot, bool Accepted, string? CanonicalCard) SelectRiftCard(
        string roomCode,
        string connectionId,
        string cardId)
    {
        string normalized = NormalizeCode(roomCode);
        string? canonicalCard = RiftbreakerCatalog.NormalizeRiftCard(cardId);
        if (canonicalCard == null)
        {
            _logger.LogWarning("[Riftbreaker] Rejected invalid rift card '{CardId}' in room {RoomCode}", cardId, normalized);
            return (GetRoom(normalized), 0, false, null);
        }

        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out RiftbreakerRoom? r) ? r : null);
            if (room == null) return (null, 0, false, null);

            int slot = room.Player1?.ConnectionId == connectionId ? 1 : (room.Player2?.ConnectionId == connectionId ? 2 : 0);
            if (slot == 0) return (room, 0, false, null);

            if (!room.ActiveRiftCards.Contains(canonicalCard, StringComparer.OrdinalIgnoreCase))
            {
                room.ActiveRiftCards.Add(canonicalCard);
            }

            if (slot == 1 && room.Player1 != null)
            {
                room.Player1.SelectedRiftCard = canonicalCard;
                room.Player1.LastActiveAt = TimeProvider.System.GetUtcNow();
            }
            else if (slot == 2 && room.Player2 != null)
            {
                room.Player2.SelectedRiftCard = canonicalCard;
                room.Player2.LastActiveAt = TimeProvider.System.GetUtcNow();
            }

            if (room.Status == RiftbreakerMatchStatus.CardDraft)
            {
                room.Status = RiftbreakerMatchStatus.Playing;
            }
            room.Act = Math.Max(room.Act, 3);
            room.LastActivityAt = TimeProvider.System.GetUtcNow();

            _logger.LogInformation("[Riftbreaker] Room {RoomCode} Slot {Slot} activated Rift Card '{Card}' -> Act {Act}",
                normalized, slot, canonicalCard, room.Act);

            SyncToRedis(room);
            return (room, slot, true, canonicalCard);
        }
    }

    public (RiftbreakerRoom? Room, int VacatedSlot) Leave(string connectionId)
    {
        if (!_connectionToRoomMap.TryRemove(connectionId, out string? roomCode))
        {
            return (null, 0);
        }

        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(roomCode) ?? (_rooms.TryGetValue(roomCode, out RiftbreakerRoom? r) ? r : null);
            if (room == null) return (null, 0);

            int vacatedSlot = 0;
            if (room.Player1?.ConnectionId == connectionId)
            {
                vacatedSlot = 1;
                room.Player1 = null;
            }
            else if (room.Player2?.ConnectionId == connectionId)
            {
                vacatedSlot = 2;
                room.Player2 = null;
            }

            if (vacatedSlot > 0)
            {
                if (room.Status != RiftbreakerMatchStatus.Finished)
                {
                    room.Status = RiftbreakerMatchStatus.WaitingForOpponent;
                }
                room.LastActivityAt = TimeProvider.System.GetUtcNow();
                _logger.LogInformation("[Riftbreaker] Connection {ConnId} vacated Slot {Slot} in room {RoomCode}",
                    connectionId, vacatedSlot, roomCode);
                SyncToRedis(room);
            }

            return (room, vacatedSlot);
        }
    }

    public (RiftbreakerRoom? Room, bool MatchWon, int WinnerSlot, bool TriggeredHeroDraft, bool TriggeredCardDraft, bool TriggeredBoss) RegisterScore(
        string roomCode,
        int scoringSlot,
        int winningThreshold = RiftbreakerCatalog.DefaultWinningScore,
        int debounceMs = 450)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out RiftbreakerRoom? r) ? r : null);
            if (room == null)
            {
                return (null, false, 0, false, false, false);
            }

            if (room.Status != RiftbreakerMatchStatus.Playing)
            {
                return (room, false, 0, false, false, false);
            }

            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (debounceMs > 0 && (now - room.LastScoreAt).TotalMilliseconds < debounceMs)
            {
                _logger.LogInformation("[Riftbreaker] Room {RoomCode} score debounce active ({Elapsed:F0}ms). Ignoring duplicate from slot {Slot}.",
                    normalized, (now - room.LastScoreAt).TotalMilliseconds, scoringSlot);
                return (room, false, 0, false, false, false);
            }

            room.LastScoreAt = now;
            room.LastActivityAt = now;

            if (scoringSlot == 1) room.Score1++;
            else if (scoringSlot == 2) room.Score2++;
            else return (room, false, 0, false, false, false);

            int maxScore = Math.Max(room.Score1, room.Score2);
            int totalScore = room.Score1 + room.Score2;

            bool triggeredHeroDraft = false;
            bool triggeredCardDraft = false;
            bool triggeredBoss = false;
            bool matchWon = false;
            int winner = 0;

            if (room.Score1 >= winningThreshold)
            {
                matchWon = true;
                winner = 1;
                room.WinnerSlot = 1;
                room.Status = RiftbreakerMatchStatus.Finished;
            }
            else if (room.Score2 >= winningThreshold)
            {
                matchWon = true;
                winner = 2;
                room.WinnerSlot = 2;
                room.Status = RiftbreakerMatchStatus.Finished;
            }
            else if (!room.HeroDraftTriggered && (totalScore >= 2 || maxScore >= 2))
            {
                triggeredHeroDraft = true;
                room.HeroDraftTriggered = true;
                room.Status = RiftbreakerMatchStatus.HeroDraft;
                room.Act = 2;
            }
            else if (!room.CardDraftTriggered && (maxScore >= 6 || totalScore >= 8))
            {
                triggeredCardDraft = true;
                room.CardDraftTriggered = true;
                room.Status = RiftbreakerMatchStatus.CardDraft;
                room.Act = 3;
            }

            if (!matchWon && !room.BossSpawned && maxScore >= 9)
            {
                triggeredBoss = true;
                room.BossSpawned = true;
                room.Act = 4;
            }

            _logger.LogInformation(
                "[Riftbreaker] Room {RoomCode} Score P1 {S1} - {S2} P2 (Slot {Slot}, Act={Act}, HeroDraft={HD}, CardDraft={CD}, Boss={Boss}, Won={Won})",
                normalized, room.Score1, room.Score2, scoringSlot, room.Act, triggeredHeroDraft, triggeredCardDraft, triggeredBoss, matchWon);

            SyncToRedis(room);
            return (room, matchWon, winner, triggeredHeroDraft, triggeredCardDraft, triggeredBoss);
        }
    }

    public RiftbreakerRoom? ResetMatch(string roomCode, bool keepHeroes = false)
    {
        string normalized = NormalizeCode(roomCode);
        lock (_lock)
        {
            RiftbreakerRoom? room = HydrateRoomFromRedis(normalized) ?? (_rooms.TryGetValue(normalized, out RiftbreakerRoom? r) ? r : null);
            if (room == null) return null;

            room.Score1 = 0;
            room.Score2 = 0;
            room.Act = keepHeroes ? 2 : 1;
            room.HeroDraftTriggered = keepHeroes;
            room.CardDraftTriggered = false;
            room.BossSpawned = false;
            room.ActiveRiftCards.Clear();
            room.WinnerSlot = null;
            room.LastScoreAt = DateTimeOffset.MinValue;
            room.Status = room.IsFull ? RiftbreakerMatchStatus.WaitingForReady : RiftbreakerMatchStatus.WaitingForOpponent;

            if (room.Player1 != null)
            {
                room.Player1.IsReady = false;
                if (!keepHeroes) room.Player1.SelectedHero = null;
                room.Player1.SelectedRiftCard = null;
            }
            if (room.Player2 != null)
            {
                room.Player2.IsReady = false;
                if (!keepHeroes) room.Player2.SelectedHero = null;
                room.Player2.SelectedRiftCard = null;
            }

            room.LastActivityAt = TimeProvider.System.GetUtcNow();
            _logger.LogInformation("[Riftbreaker] Room {RoomCode} reset to 0-0 (KeepHeroes={KeepHeroes})", normalized, keepHeroes);
            SyncToRedis(room);
            return room;
        }
    }

    public IReadOnlyCollection<RiftbreakerRoom> GetActiveRooms()
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue[] members = db.SetMembers("kongor:riftbreaker:rooms");
                if (members.Length > 0)
                {
                    var result = new List<RiftbreakerRoom>();
                    foreach (RedisValue member in members)
                    {
                        if (member.IsNullOrEmpty) continue;
                        RiftbreakerRoom? room = GetRoom(member.ToString());
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
                _logger.LogWarning(ex, "[Riftbreaker] Failed to retrieve active rooms from Redis set, falling back to local memory");
            }
        }

        return _rooms.Values.Where(r => !r.IsEmpty).ToArray();
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "RIFT";
        string clean = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "RIFT" : (clean.Length > 8 ? clean[..8] : clean);
    }
}
