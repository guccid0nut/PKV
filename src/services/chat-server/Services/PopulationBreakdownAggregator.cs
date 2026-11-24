// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text;
using NEXUS.Contracts.CQRS.Telemetry;

namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
/// Pure, deterministic aggregator that projects live ChatServer match server sessions and
/// matchmaking groups into a <see cref="PopulationBreakdownResponseDto"/> snapshot for
/// <c>TELEMETRY:METRICS:PopulationBreakdownJson</c>.
/// </summary>
public static class PopulationBreakdownAggregator
{
    /// <summary>
    /// Normalizes a game server location code to its canonical region display name.
    /// </summary>
    public static string NormalizeServerRegion(string? location) =>
        PopulationBreakdownDefaults.NormalizeServerRegion(location);

    /// <summary>
    /// Normalizes a TMM region selection code to its canonical TMM region pool name,
    /// collapsing <c>USE</c> and <c>USW</c> into <c>US (shared pool)</c> and empty/<c>ANY</c>/<c>ALL</c>/<c>AUTO</c>
    /// into <c>Any region</c>.
    /// </summary>
    public static string NormalizeTmmRegion(string? region) =>
        PopulationBreakdownDefaults.NormalizeTmmRegion(region);

    /// <summary>
    /// Resolves the canonical human-readable queue section title for a matchmaking group.
    /// </summary>
    public static string ResolveQueueTitle(MatchmakingGroupInformation? information)
    {
        if (information is null)
        {
            return PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal;
        }

        if (information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP)
        {
            return "Co-Op vs Bots";
        }

        return information.GameType switch
        {
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL or
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL or
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_NORMAL
                => PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal,

            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS or
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS_REBORN
                => PopulationBreakdownDefaults.QueueTitleMidWars,

            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL or
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL or
            ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_CASUAL
                => "Forests of Caldavar | Casual",

            ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS
                => "Rift Wars",

            ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC
                => "Public Match",

            ChatProtocol.TMMGameType.TMM_GAME_TYPE_CUSTOM
                => !string.IsNullOrWhiteSpace(information.MapName)
                    ? $"Custom ({information.MapName.Trim()})"
                    : "Custom Match",

            _ => ResolveFallbackTitleFromMapName(information.MapName)
        };
    }

    /// <summary>
    /// Creates an empty zero-filled population breakdown snapshot.
    /// </summary>
    public static PopulationBreakdownResponseDto CreateEmpty(DateTimeOffset updatedAtUtc, int onlinePlayers = 0) =>
        PopulationBreakdownDefaults.CreateEmpty(updatedAtUtc, onlinePlayers);

    /// <summary>
    /// Computes a population breakdown snapshot directly from live connected <see cref="ChatSession"/> server sessions
    /// and matchmaking groups.
    /// </summary>
    public static PopulationBreakdownResponseDto Compute(
        int onlinePlayers,
        IEnumerable<ChatSession>? matchServerSessions,
        IEnumerable<MatchmakingGroup>? groups,
        DateTimeOffset updatedAtUtc)
    {
        List<MatchServerChatSessionMetadata> connectedServers = [];
        if (matchServerSessions is not null)
        {
            foreach (ChatSession session in matchServerSessions)
            {
                if (session is { IsSessionConnected: true, ServerMetadata: not null })
                {
                    connectedServers.Add(session.ServerMetadata);
                }
            }
        }

        return Compute(onlinePlayers, connectedServers, groups, updatedAtUtc);
    }

    /// <summary>
    /// Computes a population breakdown snapshot from pure server metadata and matchmaking groups.
    /// </summary>
    public static PopulationBreakdownResponseDto Compute(
        int onlinePlayers,
        IEnumerable<MatchServerChatSessionMetadata>? servers,
        IEnumerable<MatchmakingGroup>? groups,
        DateTimeOffset updatedAtUtc)
    {
        int gamesInProgressTotal = 0;
        Dictionary<string, int> gamesInProgressCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> serversAvailableCounts = new(StringComparer.Ordinal);

        if (servers is not null)
        {
            foreach (MatchServerChatSessionMetadata server in servers)
            {
                if (server is null)
                {
                    continue;
                }

                // Strictly exclude Queue Arcade (Tier Q) warm-up servers from public game/server availability counts.
                if (server.IsQueueArcade ||
                    MatchServer.IsQueueArcadePort(server.Port) ||
                    MatchServer.IsQueueArcadePort(server.LocalPort))
                {
                    continue;
                }

                string region = NormalizeServerRegion(server.Location);
                switch (server.Status)
                {
                    case ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE:
                    case ChatProtocol.ServerStatus.SERVER_STATUS_LOADING:
                        gamesInProgressTotal++;
                        gamesInProgressCounts[region] = gamesInProgressCounts.TryGetValue(region, out int inProg)
                            ? inProg + 1
                            : 1;
                        break;

                    case ChatProtocol.ServerStatus.SERVER_STATUS_IDLE:
                    case ChatProtocol.ServerStatus.SERVER_STATUS_SLEEPING:
                        serversAvailableCounts[region] = serversAvailableCounts.TryGetValue(region, out int avail)
                            ? avail + 1
                            : 1;
                        break;
                }
            }
        }

        // Seed default TMM queues so they are always present in canonical order even when empty.
        Dictionary<string, QueueAccumulator> queueAccumulators = new(StringComparer.Ordinal);
        foreach (string defaultTitle in PopulationBreakdownDefaults.StandardTmmQueueTitles)
        {
            queueAccumulators[defaultTitle] = new QueueAccumulator();
        }

        if (groups is not null)
        {
            foreach (MatchmakingGroup group in groups)
            {
                if (group is null ||
                    group.QueueStartTime is null ||
                    group.Information is null ||
                    group.Members is null ||
                    group.Members.Count == 0)
                {
                    continue;
                }

                int memberCount = group.Members.Count;
                string queueTitle = ResolveQueueTitle(group.Information);
                if (!queueAccumulators.TryGetValue(queueTitle, out QueueAccumulator? accumulator))
                {
                    accumulator = new QueueAccumulator();
                    queueAccumulators[queueTitle] = accumulator;
                }

                accumulator.TotalQueued += memberCount;

                // CRITICAL: Per-group region deduplication so USE + USW count once under "US (shared pool)",
                // while distinct pools (e.g., AU + USE) each receive the group's member count.
                HashSet<string> distinctPools = new(StringComparer.Ordinal);
                string[]? rawRegions = group.Information.GameRegions;
                if (rawRegions is null || rawRegions.Length == 0)
                {
                    distinctPools.Add(PopulationBreakdownDefaults.TmmRegionAny);
                }
                else
                {
                    foreach (string rawRegion in rawRegions)
                    {
                        if (string.IsNullOrWhiteSpace(rawRegion))
                        {
                            continue;
                        }

                        distinctPools.Add(NormalizeTmmRegion(rawRegion));
                    }

                    if (distinctPools.Count == 0)
                    {
                        distinctPools.Add(PopulationBreakdownDefaults.TmmRegionAny);
                    }
                }

                foreach (string pool in distinctPools)
                {
                    accumulator.RegionCounts[pool] = accumulator.RegionCounts.TryGetValue(pool, out int current)
                        ? current + memberCount
                        : memberCount;
                }
            }
        }

        List<RegionCountDto> gamesInProgressByRegion = new(gamesInProgressCounts.Count + 1)
        {
            new RegionCountDto
            {
                Region = PopulationBreakdownDefaults.AllLocationsLabel,
                Count = gamesInProgressTotal
            }
        };

        foreach (KeyValuePair<string, int> kvp in gamesInProgressCounts
                     .Where(static kv => kv.Value > 0)
                     .OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            gamesInProgressByRegion.Add(new RegionCountDto { Region = kvp.Key, Count = kvp.Value });
        }

        List<RegionCountDto> serversAvailableByRegion =
        [
            .. serversAvailableCounts
                .Where(static kv => kv.Value > 0)
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => new RegionCountDto { Region = kv.Key, Count = kv.Value })
        ];

        List<TmmQueueBreakdownDto> tmmQueues = new(queueAccumulators.Count);
        HashSet<string> emittedTitles = new(StringComparer.Ordinal);

        foreach (string standardTitle in PopulationBreakdownDefaults.StandardTmmQueueTitles)
        {
            tmmQueues.Add(BuildQueueBreakdownDto(standardTitle, queueAccumulators[standardTitle]));
            emittedTitles.Add(standardTitle);
        }

        foreach (KeyValuePair<string, QueueAccumulator> kvp in queueAccumulators
                     .Where(kv => !emittedTitles.Contains(kv.Key) && kv.Value.TotalQueued > 0)
                     .OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            tmmQueues.Add(BuildQueueBreakdownDto(kvp.Key, kvp.Value));
        }

        return new PopulationBreakdownResponseDto
        {
            OnlinePlayers = Math.Max(0, onlinePlayers),
            GamesInProgressTotal = gamesInProgressTotal,
            GamesInProgressByRegion = gamesInProgressByRegion,
            ServersAvailableByRegion = serversAvailableByRegion,
            TmmQueues = tmmQueues,
            UpdatedAtUtc = updatedAtUtc
        };
    }

    /// <summary>
    /// Computes a deterministic structural fingerprint of all counts in <paramref name="snapshot"/>,
    /// deliberately excluding <see cref="PopulationBreakdownResponseDto.UpdatedAtUtc"/> so change detection
    /// can distinguish real count changes from 5-second heartbeat refreshes.
    /// </summary>
    public static string BuildContentFingerprint(PopulationBreakdownResponseDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        StringBuilder sb = new(256);
        sb.Append("ccu=").Append(snapshot.OnlinePlayers.ToString(CultureInfo.InvariantCulture))
          .Append("|gip=").Append(snapshot.GamesInProgressTotal.ToString(CultureInfo.InvariantCulture))
          .Append("|gr=[");

        foreach (RegionCountDto row in snapshot.GamesInProgressByRegion)
        {
            sb.Append(row.Region).Append('=').Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append("]|sa=[");
        foreach (RegionCountDto row in snapshot.ServersAvailableByRegion)
        {
            sb.Append(row.Region).Append('=').Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append("]|q=[");
        foreach (TmmQueueBreakdownDto queue in snapshot.TmmQueues)
        {
            sb.Append(queue.Title).Append('(').Append(queue.TotalQueued.ToString(CultureInfo.InvariantCulture)).Append("):");
            foreach (RegionCountDto row in queue.Regions)
            {
                sb.Append(row.Region).Append('=').Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            sb.Append(';');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static TmmQueueBreakdownDto BuildQueueBreakdownDto(string title, QueueAccumulator accumulator)
    {
        List<RegionCountDto> regions = new(PopulationBreakdownDefaults.StandardTmmRegions.Count + accumulator.RegionCounts.Count);
        HashSet<string> emittedRegions = new(StringComparer.Ordinal);

        foreach (string standardRegion in PopulationBreakdownDefaults.StandardTmmRegions)
        {
            accumulator.RegionCounts.TryGetValue(standardRegion, out int count);
            regions.Add(new RegionCountDto { Region = standardRegion, Count = count });
            emittedRegions.Add(standardRegion);
        }

        foreach (KeyValuePair<string, int> kvp in accumulator.RegionCounts
                     .Where(kv => !emittedRegions.Contains(kv.Key) && kv.Value > 0)
                     .OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            regions.Add(new RegionCountDto { Region = kvp.Key, Count = kvp.Value });
        }

        return new TmmQueueBreakdownDto
        {
            Title = title,
            TotalQueued = accumulator.TotalQueued,
            Regions = regions
        };
    }

    private static string ResolveFallbackTitleFromMapName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return "Other";
        }

        string trimmed = mapName.Trim();
        if (trimmed.Contains("midwars", StringComparison.OrdinalIgnoreCase))
        {
            return PopulationBreakdownDefaults.QueueTitleMidWars;
        }

        if (trimmed.Contains("caldavar", StringComparison.OrdinalIgnoreCase))
        {
            return PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal;
        }

        return trimmed;
    }

    private sealed class QueueAccumulator
    {
        public int TotalQueued { get; set; }

        public Dictionary<string, int> RegionCounts { get; } = new(StringComparer.Ordinal);
    }
}
