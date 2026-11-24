// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Threading.Channels;
using TRANSMUTANSTEIN.ChatServer.Serialization;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public interface IDiscordPresencePublisherService
{
    void PublishPresenceUpdate(ChatSession session, MatchmakingGroup? group = null, bool forceReset = false);
    void PublishGroupPresenceUpdate(MatchmakingGroup group);
}

public class DiscordPresencePublisherService : IDiscordPresencePublisherService, IAsyncDisposable
{
    private const string PresenceChannel = "discord-presence-updates";

    private readonly CancellationTokenSource _cts = new();
    private readonly IMatchmakingService _matchmakingService;

    private readonly Channel<DiscordPresenceUpdateDto> _presenceQueue =
        Channel.CreateUnbounded<DiscordPresenceUpdateDto>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Task _processingTask;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;

    public DiscordPresencePublisherService(IConnectionMultiplexer redis,
        IMatchmakingService matchmakingService)
    {
        _redis = redis;
        _matchmakingService = matchmakingService;
        _subscriber = redis.GetSubscriber();
        _processingTask = Task.Run(ProcessPresenceQueueAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _presenceQueue.Writer.TryComplete();
        try
        {
            await _processingTask;
        }
        catch
        {
            // Ignore cancel exception on shutdown
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    public void PublishPresenceUpdate(ChatSession session, MatchmakingGroup? group = null, bool forceReset = false)
    {
        Account? account = (Account?)session.Account;
        if (account is null) return;

        int accountId = account.ID;
        string accountName = account.Name;
        ClientChatSessionMetadata? metadata = (ClientChatSessionMetadata?)session.ClientMetadata;
        ChatProtocol.ChatClientStatus clientState = metadata?.LastKnownClientState ??
                                                    ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED;
        int? matchId = metadata?.MatchID;
        int? activeArcadeMatchId = metadata?.ActiveArcadeMatchId;
        DateTimeOffset? arcadeJoinSentAtUtc = metadata?.ArcadeJoinSentAtUtc;

        group ??= _matchmakingService.GetMatchmakingGroupByMemberID(accountId);

        _ = Task.Run(async () =>
            await EnqueuePresenceUpdateSnapshotAsync(accountId, accountName, clientState, matchId, group, forceReset,
                activeArcadeMatchId, arcadeJoinSentAtUtc));
    }

    public void PublishGroupPresenceUpdate(MatchmakingGroup group)
    {
        foreach (MatchmakingGroupMember member in group.Members) PublishPresenceUpdate(member.Session, group);
    }

    private async Task EnqueuePresenceUpdateSnapshotAsync(int accountId, string accountName,
        ChatProtocol.ChatClientStatus clientState, int? matchId, MatchmakingGroup? group = null,
        bool forceReset = false, int? activeArcadeMatchId = null, DateTimeOffset? arcadeJoinSentAtUtc = null)
    {
        DiscordPresenceUpdateDto dto = new() { AccountId = accountId };

        MatchStartData? matchData = null;
        bool isQueueArcadeMatch = false;

        // Determine State and Details based on ChatClientStatus and MatchmakingGroup
        switch (clientState)
        {
            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED:
            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTING:
            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_WAITING_FOR_AUTH:
                dto.State = "Offline";
                dto.Details = "Logged Out";
                break;

            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED:
                if (group != null && (group.Members.Count > 1 || group.QueueStartTime.HasValue))
                {
                    dto.PartyId = group.GUID.ToString();
                    dto.PartySize = group.Members.Count;
                    dto.PartyMax = group.Information.TeamSize;

                    if (group.QueueStartTime.HasValue)
                    {
                        dto.State = "In Matchmaking Queue";
                        dto.Details = $"Queueing {GetFriendlyGameType(group.Information.GameType)}";
                        dto.StartTimestamp = group.QueueStartTime.Value.ToUnixTimeSeconds();
                    }
                    else
                    {
                        dto.State = "In Party";
                        dto.Details = "Forming Group";
                    }
                }
                else
                {
                    dto.State = "Online in Menus";
                    dto.Details = "In Main Menu";
                }

                break;

            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME:
            case ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME:
                if (matchId.HasValue) matchData = await _redis.GetDatabase().GetMatchStartData(matchId.Value);

                isQueueArcadeMatch =
                    (activeArcadeMatchId.HasValue && matchId.HasValue && activeArcadeMatchId.Value == matchId.Value) ||
                    (matchData != null &&
                     ((!string.IsNullOrEmpty(matchData.ServerName) &&
                       matchData.ServerName.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrEmpty(matchData.MatchName) &&
                       matchData.MatchName.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase))));

                if (isQueueArcadeMatch)
                {
                    dto.State = "In Matchmaking Queue";
                    DateTimeOffset? effectiveQueueStart = group?.QueueStartTime ?? arcadeJoinSentAtUtc;
                    if (effectiveQueueStart.HasValue && effectiveQueueStart.Value > DateTimeOffset.UnixEpoch)
                    {
                        dto.StartTimestamp = effectiveQueueStart.Value.ToUnixTimeSeconds();
                    }

                    if (group is { Members.Count: > 1 })
                    {
                        dto.PartyId = group.GUID.ToString();
                        dto.PartySize = group.Members.Count;
                        dto.PartyMax = group.Information.TeamSize;
                    }

                    dto.Details = matchData != null && !string.IsNullOrEmpty(matchData.Map)
                        ? $"In Queue • {GetFriendlyMapName(matchData.Map)}"
                        : group != null
                            ? $"Queueing {GetFriendlyGameType(group.Information.GameType)}"
                            : "In Queue (Queue Arcade)";
                    break;
                }

                bool isMatchStarted = matchData is { IsStarted: true };
                dto.State = isMatchStarted
                    ? "Playing Match"
                    : clientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                        ? "Joining Game"
                        : matchData != null
                            ? "In Game Lobby"
                            : "Playing Match";

                if (isMatchStarted && matchData!.TimestampStarted > DateTimeOffset.UnixEpoch)
                {
                    dto.StartTimestamp = matchData.TimestampStarted.ToUnixTimeSeconds();
                }

                if (group is { Members.Count: > 1 })
                {
                    dto.PartyId = group.GUID.ToString();
                    dto.PartySize = group.Members.Count;
                    dto.PartyMax = group.Information.TeamSize;
                }
                else if (matchData is { IsStarted: false, MaximumPlayersCount: > 1 })
                {
                    dto.PartyId = matchData.MatchID.ToString();
                    dto.PartySize = matchData.ConnectedPlayersCount > 0 ? matchData.ConnectedPlayersCount : 1;
                    dto.PartyMax = matchData.MaximumPlayersCount;
                }

                if (matchData != null)
                {
                    string effectiveMap = matchData.Map;
                    if (group != null) effectiveMap = GetMatchmakingMapName(group.Information.GameType) ?? effectiveMap;

                    string friendlyMap = GetFriendlyMapName(effectiveMap);

                    if (group == null)
                    {
                        dto.Details = $"Public - {friendlyMap}";
                    }
                    else
                    {
                        dto.Details = $"{GetFriendlyGameType(group.Information.GameType)} - {friendlyMap}";
                    }
                }
                else if (group != null)
                {
                    string effectiveMap =
                        GetMatchmakingMapName(group.Information.GameType) ?? group.Information.MapName;
                    dto.Details = !string.IsNullOrEmpty(effectiveMap)
                        ? $"{GetFriendlyGameType(group.Information.GameType)} - {GetFriendlyMapName(effectiveMap)}"
                        : $"{GetFriendlyGameType(group.Information.GameType)}";
                }
                else
                {
                    dto.Details = "Playing Solo";
                }

                break;
        }

        // Set Images
        dto.LargeImageKey = "large";

        string? currentMap = null;

        if (matchData != null && !string.IsNullOrEmpty(matchData.Map))
        {
            currentMap = matchData.Map;
            if (group != null && !isQueueArcadeMatch) currentMap = GetMatchmakingMapName(group.Information.GameType) ?? currentMap;
        }
        else if (group != null)
        {
            currentMap = GetMatchmakingMapName(group.Information.GameType) ?? group.Information.MapName;
        }

        if (!string.IsNullOrEmpty(currentMap))
        {
            dto.LargeImageText = GetFriendlyMapName(currentMap);
            dto.LargeImageKey = GetMapAssetKey(currentMap);
        }
        else
        {
            dto.LargeImageText = "KONGOR";
        }

        bool isLoggedIn = dto.State != "Offline" && !string.IsNullOrWhiteSpace(accountName);
        dto.SmallImageKey = string.Empty;
        dto.SmallImageText = isLoggedIn ? accountName : "KONGOR Player";

        _presenceQueue.Writer.TryWrite(dto);
    }

    private async Task ProcessPresenceQueueAsync()
    {
        ChannelReader<DiscordPresenceUpdateDto> reader = _presenceQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            while (reader.TryRead(out DiscordPresenceUpdateDto? dto))
            {
                string message = JsonSerializer.Serialize(dto, ChatServerJsonContext.Default.DiscordPresenceUpdateDto);
                await _redis.GetDatabase()
                    .StringSetAsync($"presence:dto:{dto.AccountId}", message, TimeSpan.FromHours(24));
                await _subscriber.PublishAsync(RedisChannel.Literal(PresenceChannel), message);

                Log.Debug(
                    "[DiscordPresencePublisherService] Published presence update for {AccountId}: {State} - {Details}",
                    dto.AccountId, dto.State, dto.Details);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DiscordPresencePublisherService] ERROR processing presence queue");
        }
    }

    private string GetFriendlyGameType(ChatProtocol.TMMGameType type)
    {
        return type switch
        {
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC => "Public Match",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL => "Casual Matchmaking",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL => "Ranked Matchmaking",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS => "Mid Wars",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS_REBORN => "Mid Wars Reborn",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS => "Rift Wars",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL => "Ranked Season",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL => "Casual Season",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_NORMAL => "Ranked Reborn",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_CASUAL => "Casual Reborn",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CUSTOM => "Custom Match",
            _ => "Matchmaking"
        };
    }

    private string? GetMatchmakingMapName(ChatProtocol.TMMGameType type)
    {
        return type switch
        {
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC => "caldavar",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL => "caldavar",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL => "caldavar",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS => "midwars",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS_REBORN => "midwars_reborn",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS => "riftwars",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL => "caldavar",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL => "caldavar",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_NORMAL => "caldavar_reborn",
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_CASUAL => "caldavar_reborn",
            _ => null
        };
    }

    public static string GetFriendlyMapName(string mapName)
    {
        if (string.IsNullOrEmpty(mapName)) return "Unknown";

        string mapLower = mapName.Trim().ToLowerInvariant();

        // Specific variants before base map substrings
        if (mapLower.Contains("caldavar_halloween")) return "Forests of Caldavar (Halloween)";
        if (mapLower.Contains("caldavar_wtf")) return "Forests of Caldavar (WTF)";
        if (mapLower.Contains("caldavar_reborn")) return "Forests of Caldavar Reborn";
        if (mapLower.Contains("caldavar_old")) return "Forests of Caldavar (Classic)";

        if (mapLower.Contains("midwars_halloween")) return "Mid Wars (Halloween)";
        if (mapLower.Contains("midwars_10v10")) return "Mid Wars (10v10 Chaos)";
        if (mapLower.Contains("midwars_reborn")) return "Mid Wars Reborn";
        if (mapLower.Contains("mids_omg")) return "Mid Wars Classic";

        if (mapLower.Contains("truedevo_10v10")) return "True Devo Wars (10v10 Chaos)";
        if (mapLower.Contains("truedevo") || mapLower.Contains("true devo") || mapLower.Contains("true_devo"))
            return "True Devo Wars";
        if (mapLower.Contains("devowars_10v10")) return "Devo Wars (10v10 Chaos)";

        // 10 Additional Standalone Custom Maps
        if (mapLower.Contains("colosseumduel") || mapLower.Contains("colosseum_duel") || mapLower.Contains("colosseum duel"))
            return "Colosseum Duel";
        if (mapLower.Contains("empathmaze") || mapLower.Contains("empath_maze") || mapLower.Contains("empath maze"))
            return "Empath Maze";
        if (mapLower.Contains("nightmarishcoop") || mapLower.Contains("nightmarish_coop") || mapLower.Contains("nightmarish co-op") || mapLower.Contains("nightmarish coop"))
            return "Nightmarish Co-Op";
        if (mapLower.Contains("pebblesduel") || mapLower.Contains("pebbles_duel") || mapLower.Contains("pebbles duel"))
            return "Pebbles Duel";
        if (mapLower.Contains("persianpass") || mapLower.Contains("persian_pass") || mapLower.Contains("persian pass"))
            return "Persian Pass";
        if (mapLower.Contains("threekingdoms") || mapLower.Contains("three_kingdoms") || mapLower.Contains("three kingdoms"))
            return "Three Kingdoms";
        if (mapLower.Contains("valkwars") || mapLower.Contains("valk_wars") || mapLower.Contains("valkyrie wars"))
            return "Valkyrie Wars";
        if (mapLower == "archers" || mapLower == "map_archers" || mapLower.Contains("archer_wars") || mapLower.Contains("archer wars"))
            return "Archer Wars";
        if (mapLower == "enfos" || mapLower == "map_enfos" || mapLower.Contains("enfo's team survival") || mapLower.Contains("enfos team survival"))
            return "Enfo's Team Survival";
        if (mapLower == "arena" || mapLower == "map_arena" || mapLower.Contains("hon arena"))
            return "HoN Arena";

        // 14 Official Maps
        if (mapLower.Contains("caldavar") || mapLower.Contains("forests of caldavar")) return "Forests of Caldavar";

        if (mapLower.Contains("capturetheflag") || mapLower.Contains("capture_the_flag") || mapLower.Contains("capture the flag") || mapLower.Contains("ctf"))
            return "Capture the Flag";

        if (mapLower.Contains("darkwoodvale") || mapLower.Contains("darkwood vale")) return "Darkwood Vale";

        if (mapLower.Contains("devowars") || mapLower.Contains("devo_wars") || mapLower.Contains("devo wars"))
            return "Devo Wars";

        if (mapLower.Contains("grimmscrossing") || mapLower.Contains("grimrnscrossing") ||
            mapLower.Contains("grimm's crossing") || mapLower.Contains("grimms crossing")) return "Grimm's Crossing";

        if (mapLower.Contains("midwars") || mapLower.Contains("mid_wars") || mapLower.Contains("mid wars")) return "Mid Wars";

        if (mapLower.Contains("prophets")) return "Prophets";

        if (mapLower.Contains("riftwars") || mapLower.Contains("rift_wars") || mapLower.Contains("rift wars")) return "Rift Wars";

        if (mapLower.Contains("spotlight")) return "Spotlight";

        if (mapLower.Contains("soccer") || mapLower.Contains("strikers")) return "Strikers";

        if (mapLower.Contains("team_deathmatch") || mapLower.Contains("teamdeathmatch") || mapLower.Contains("team deathmatch") || mapLower.Contains("team death match"))
            return "Team Death Match";

        if (mapLower.Contains("thegrimmhunt") || mapLower.Contains("grimmhunt") || mapLower.Contains("grimm hunt"))
            return "The Grimm Hunt";

        if (mapLower.Contains("tutorial")) return "Tutorial";

        return mapLower.Contains("watchtower") ? "Watchtower" : mapName;
    }

    public static string GetMapAssetKey(string mapName)
    {
        if (string.IsNullOrEmpty(mapName)) return "large";

        string mapLower = mapName.Trim().ToLowerInvariant();

        // 11 Standalone Custom Maps (check truedevo before devowars)
        if (mapLower.Contains("truedevo") || mapLower.Contains("true devo") || mapLower.Contains("true_devo"))
            return "map_truedevo";
        if (mapLower.Contains("colosseumduel") || mapLower.Contains("colosseum_duel") || mapLower.Contains("colosseum duel"))
            return "map_colosseumduel";
        if (mapLower.Contains("empathmaze") || mapLower.Contains("empath_maze") || mapLower.Contains("empath maze"))
            return "map_empathmaze";
        if (mapLower.Contains("nightmarishcoop") || mapLower.Contains("nightmarish_coop") || mapLower.Contains("nightmarish co-op") || mapLower.Contains("nightmarish coop"))
            return "map_nightmarishcoop";
        if (mapLower.Contains("pebblesduel") || mapLower.Contains("pebbles_duel") || mapLower.Contains("pebbles duel"))
            return "map_pebblesduel";
        if (mapLower.Contains("persianpass") || mapLower.Contains("persian_pass") || mapLower.Contains("persian pass"))
            return "map_persianpass";
        if (mapLower.Contains("threekingdoms") || mapLower.Contains("three_kingdoms") || mapLower.Contains("three kingdoms"))
            return "map_threekingdoms";
        if (mapLower.Contains("valkwars") || mapLower.Contains("valk_wars") || mapLower.Contains("valkyrie wars"))
            return "map_valkwars";
        if (mapLower == "archers" || mapLower == "map_archers" || mapLower.Contains("archer_wars") || mapLower.Contains("archer wars"))
            return "map_archers";
        if (mapLower == "enfos" || mapLower == "map_enfos" || mapLower.Contains("enfo's team survival") || mapLower.Contains("enfos team survival"))
            return "map_enfos";
        if (mapLower == "arena" || mapLower == "map_arena" || mapLower.Contains("hon arena"))
            return "map_arena";

        // 14 Official Maps + Variant Reuse
        if (mapLower.Contains("caldavar") || mapLower.Contains("forests of caldavar")) return "map_caldavar";

        if (mapLower.Contains("capturetheflag") || mapLower.Contains("capture_the_flag") || mapLower.Contains("capture the flag") || mapLower.Contains("ctf"))
            return "map_capturetheflag";

        if (mapLower.Contains("darkwoodvale") || mapLower.Contains("darkwood vale")) return "map_darkwoodvale";

        if (mapLower.Contains("devowars") || mapLower.Contains("devo_wars") || mapLower.Contains("devo wars"))
            return "map_devowars";

        if (mapLower.Contains("grimmscrossing") || mapLower.Contains("grimrnscrossing") ||
            mapLower.Contains("grimm's crossing") || mapLower.Contains("grimms crossing")) return "map_grimmscrossing";

        if (mapLower.Contains("midwars") || mapLower.Contains("mid_wars") || mapLower.Contains("mid wars") || mapLower.Contains("mids_omg"))
            return "map_midwars";

        if (mapLower.Contains("prophets")) return "map_prophets";

        if (mapLower.Contains("riftwars") || mapLower.Contains("rift_wars") || mapLower.Contains("rift wars")) return "map_riftwars";

        if (mapLower.Contains("spotlight")) return "map_spotlight";

        if (mapLower.Contains("soccer") || mapLower.Contains("strikers")) return "map_strikers";

        if (mapLower.Contains("team_deathmatch") || mapLower.Contains("teamdeathmatch") ||
            mapLower.Contains("team deathmatch") || mapLower.Contains("team death match")) return "map_teamdeathmatch";

        if (mapLower.Contains("thegrimmhunt") || mapLower.Contains("grimmhunt") || mapLower.Contains("grimm hunt"))
            return "map_thegrimmhunt";

        if (mapLower.Contains("tutorial")) return "map_tutorial";

        if (mapLower.Contains("watchtower")) return "map_watchtower";

        return "large";
    }
}
