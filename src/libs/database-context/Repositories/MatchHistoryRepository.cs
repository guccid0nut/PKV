// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace MERRICK.DatabaseContext.Repositories;

public class MatchHistoryRepository(
    MerrickContext db,
    ILogger<MatchHistoryRepository>? logger = null) : IMatchHistoryRepository
{
    private readonly MerrickContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<MatchHistoryRepository> _logger = logger ?? NullLogger<MatchHistoryRepository>.Instance;

    public async Task<MatchStatistics?> GetMatchByIdAsync(int matchId, CancellationToken ct = default)
    {
        return await _db.MatchStatistics
            .AsSplitQuery()
            .Include(m => m.Bans)
            .Include(m => m.Frags)
                .ThenInclude(f => f.Assists)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchId, ct);
    }

    public async Task<IReadOnlyList<MatchStatistics>> GetRecentMatchesByAccountIdAsync(int accountId, int limit = 20,
        CancellationToken ct = default)
    {
        List<int> matchIds = await _db.PlayerStatistics
            .AsNoTracking()
            .Where(p => p.AccountID == accountId)
            .OrderByDescending(p => p.MatchID)
            .Take(limit)
            .Select(p => p.MatchID)
            .ToListAsync(ct);

        if (matchIds.Count == 0) return [];

        return await _db.MatchStatistics
            .AsSplitQuery()
            .AsNoTracking()
            .Where(m => matchIds.Contains(m.MatchID))
            .OrderByDescending(m => m.MatchID)
            .ToListAsync(ct);
    }

    public async Task SaveMatchAsync(MatchStatistics match, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(match);

        try
        {
            MatchStatistics? existing =
                await _db.MatchStatistics
                    .AsSplitQuery()
                    .Include(m => m.Bans)
                    .Include(m => m.Frags)
                        .ThenInclude(f => f.Assists)
                    .FirstOrDefaultAsync(m => m.MatchID == match.MatchID, ct);
            if (existing != null)
            {
                match.ID = existing.ID;
                _db.Entry(existing).CurrentValues.SetValues(match);
                existing.FragHistory = match.FragHistory != null ? [.. match.FragHistory] : null;

                if (!ReferenceEquals(existing, match))
                {
                    if (existing.Bans.Count > 0)
                    {
                        _db.MatchBans.RemoveRange(existing.Bans);
                        existing.Bans.Clear();
                    }

                    foreach (MatchBan ban in match.Bans)
                    {
                        existing.Bans.Add(new MatchBan
                        {
                            MatchID = existing.MatchID,
                            BanOrder = ban.BanOrder,
                            Team = ban.Team,
                            HeroIdentifier = ban.HeroIdentifier
                        });
                    }

                    if (existing.Frags.Count > 0)
                    {
                        _db.MatchFrags.RemoveRange(existing.Frags);
                        existing.Frags.Clear();
                    }

                    foreach (MatchFrag frag in match.Frags)
                    {
                        MatchFrag newFrag = new()
                        {
                            MatchID = existing.MatchID,
                            KillerAccountID = frag.KillerAccountID,
                            VictimAccountID = frag.VictimAccountID,
                            GameTimeSeconds = frag.GameTimeSeconds,
                            FirstBlood = frag.FirstBlood
                        };
                        foreach (MatchFragAssist assist in frag.Assists)
                        {
                            newFrag.Assists.Add(new MatchFragAssist
                            {
                                Frag = newFrag,
                                AssistingAccountID = assist.AssistingAccountID
                            });
                        }
                        existing.Frags.Add(newFrag);
                    }
                }
            }
            else
            {
                await _db.MatchStatistics.AddAsync(match, ct);
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "[MatchHistoryRepository] Concurrency collision detected saving Match #{MatchId}. Retrying with update semantics.",
                match.MatchID);
            _db.ChangeTracker.Clear();

            MatchStatistics? existing =
                await _db.MatchStatistics
                    .AsSplitQuery()
                    .Include(m => m.Bans)
                    .Include(m => m.Frags)
                        .ThenInclude(f => f.Assists)
                    .FirstOrDefaultAsync(m => m.MatchID == match.MatchID, ct);
            if (existing != null)
            {
                match.ID = existing.ID;
                _db.Entry(existing).CurrentValues.SetValues(match);
                existing.FragHistory = match.FragHistory != null ? [.. match.FragHistory] : null;

                if (!ReferenceEquals(existing, match))
                {
                    if (existing.Bans.Count > 0)
                    {
                        _db.MatchBans.RemoveRange(existing.Bans);
                        existing.Bans.Clear();
                    }

                    foreach (MatchBan ban in match.Bans)
                    {
                        existing.Bans.Add(new MatchBan
                        {
                            MatchID = existing.MatchID,
                            BanOrder = ban.BanOrder,
                            Team = ban.Team,
                            HeroIdentifier = ban.HeroIdentifier
                        });
                    }

                    if (existing.Frags.Count > 0)
                    {
                        _db.MatchFrags.RemoveRange(existing.Frags);
                        existing.Frags.Clear();
                    }

                    foreach (MatchFrag frag in match.Frags)
                    {
                        MatchFrag newFrag = new()
                        {
                            MatchID = existing.MatchID,
                            KillerAccountID = frag.KillerAccountID,
                            VictimAccountID = frag.VictimAccountID,
                            GameTimeSeconds = frag.GameTimeSeconds,
                            FirstBlood = frag.FirstBlood
                        };
                        foreach (MatchFragAssist assist in frag.Assists)
                        {
                            newFrag.Assists.Add(new MatchFragAssist
                            {
                                Frag = newFrag,
                                AssistingAccountID = assist.AssistingAccountID
                            });
                        }
                        existing.Frags.Add(newFrag);
                    }
                }
            }
            else
            {
                _db.MatchStatistics.Update(match);
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<AccountStatistics?> GetAccountStatisticsAsync(int accountId, CancellationToken ct = default)
    {
        return await _db.AccountStatistics
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.AccountID == accountId, ct);
    }

    public async Task<IReadOnlyList<AccountStatistics>> GetAccountStatisticsByIdsAsync(IEnumerable<int> accountIds,
        CancellationToken ct = default)
    {
        List<int> ids = [.. accountIds];
        return await _db.AccountStatistics
            .Include(s => s.Account)
            .Where(s => ids.Contains(s.AccountID))
            .ToListAsync(ct);
    }

    public async Task UpdateAccountStatisticsAsync(AccountStatistics stats, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stats);

        try
        {
            AccountStatistics? existing = await _db.AccountStatistics
                .FirstOrDefaultAsync(s => s.AccountID == stats.AccountID && s.Type == stats.Type, ct);

            if (existing != null)
            {
                stats.ID = existing.ID;
                _db.Entry(existing).CurrentValues.SetValues(stats);
            }
            else
            {
                AccountStatistics newStats = new()
                {
                    AccountID = stats.AccountID,
                    Account = null!,
                    Type = stats.Type,
                    MatchesPlayed = stats.MatchesPlayed,
                    MatchesWon = stats.MatchesWon,
                    MatchesLost = stats.MatchesLost,
                    MatchesConceded = stats.MatchesConceded,
                    MatchesDisconnected = stats.MatchesDisconnected,
                    MatchesKicked = stats.MatchesKicked,
                    SkillRating = stats.SkillRating,
                    PerformanceScore = stats.PerformanceScore,
                    PlacementMatchesData = stats.PlacementMatchesData,
                    AwardMostAnnihilations = stats.AwardMostAnnihilations,
                    AwardMostQuadKills = stats.AwardMostQuadKills,
                    AwardMostSmackdowns = stats.AwardMostSmackdowns,
                    AwardMostKills = stats.AwardMostKills,
                    AwardMostAssists = stats.AwardMostAssists,
                    AwardLeastDeaths = stats.AwardLeastDeaths,
                    AwardMostBuildingDamage = stats.AwardMostBuildingDamage,
                    AwardMostWardsKilled = stats.AwardMostWardsKilled,
                    AwardMostHeroDamageDealt = stats.AwardMostHeroDamageDealt,
                    AwardMostCreepKills = stats.AwardMostCreepKills
                };
                await _db.AccountStatistics.AddAsync(newStats, ct);
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "[MatchHistoryRepository] Concurrency collision detected updating stats for Account #{AccountId}, Type {Type}. Retrying with update semantics.",
                stats.AccountID, stats.Type);
            _db.ChangeTracker.Clear();

            AccountStatistics? existing = await _db.AccountStatistics
                .FirstOrDefaultAsync(s => s.AccountID == stats.AccountID && s.Type == stats.Type, ct);

            if (existing != null)
            {
                stats.ID = existing.ID;
                _db.Entry(existing).CurrentValues.SetValues(stats);
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsForMatchAsync(int matchId,
        CancellationToken ct = default)
    {
        return await _db.PlayerStatistics
            .AsSplitQuery()
            .Include(p => p.Inventories)
            .Include(p => p.ItemEvents)
            .Include(p => p.AbilityEvents)
            .Where(p => p.MatchID == matchId)
            .ToListAsync(ct);
    }

    public async Task<PlayerStatistics?> GetPlayerStatisticsAsync(int matchId, int accountId,
        CancellationToken ct = default)
    {
        return await _db.PlayerStatistics
            .AsSplitQuery()
            .Include(p => p.Inventories)
            .Include(p => p.ItemEvents)
            .Include(p => p.AbilityEvents)
            .FirstOrDefaultAsync(p => p.MatchID == matchId && p.AccountID == accountId, ct);
    }

    public async Task<IReadOnlyList<PlayerStatistics>> GetRecentPlayerStatisticsByAccountIdAsync(int accountId,
        int limit = 20, CancellationToken ct = default)
    {
        return await _db.PlayerStatistics
            .AsSplitQuery()
            .Include(p => p.Inventories)
            .Include(p => p.ItemEvents)
            .Include(p => p.AbilityEvents)
            .Where(p => p.AccountID == accountId)
            .OrderByDescending(p => p.MatchID)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task SavePlayerStatisticsAsync(PlayerStatistics playerStats, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playerStats);

        foreach (MatchPlayerInventory inv in playerStats.Inventories)
        {
            inv.MatchID = playerStats.MatchID;
            inv.AccountID = playerStats.AccountID;
        }
        foreach (MatchItemEvent ev in playerStats.ItemEvents)
        {
            ev.MatchID = playerStats.MatchID;
            ev.AccountID = playerStats.AccountID;
        }
        foreach (MatchAbilityEvent ev in playerStats.AbilityEvents)
        {
            ev.MatchID = playerStats.MatchID;
            ev.AccountID = playerStats.AccountID;
        }

        try
        {
            await _db.PlayerStatistics.AddAsync(playerStats, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "[MatchHistoryRepository] Concurrency collision or duplicate saving PlayerStatistics for Match #{MatchId}, Account #{AccountId}. Retrying with update semantics.",
                playerStats.MatchID, playerStats.AccountID);
            _db.ChangeTracker.Clear();

            PlayerStatistics? existing =
                await _db.PlayerStatistics
                    .AsSplitQuery()
            .Include(p => p.Inventories)
                    .Include(p => p.ItemEvents)
                    .Include(p => p.AbilityEvents)
                    .FirstOrDefaultAsync(
                        p => p.MatchID == playerStats.MatchID && p.AccountID == playerStats.AccountID, ct);
            if (existing != null)
            {
                playerStats.ID = existing.ID;
                _db.Entry(existing).CurrentValues.SetValues(playerStats);
                existing.AbilityHistory = playerStats.AbilityHistory;

                if (!ReferenceEquals(existing, playerStats))
                {
                    if (existing.Inventories.Count > 0)
                    {
                        _db.MatchPlayerInventories.RemoveRange(existing.Inventories);
                        existing.Inventories.Clear();
                    }
                    foreach (MatchPlayerInventory inv in playerStats.Inventories)
                    {
                        existing.Inventories.Add(new MatchPlayerInventory
                        {
                            PlayerStatisticID = existing.ID,
                            MatchID = existing.MatchID,
                            AccountID = existing.AccountID,
                            SlotIndex = inv.SlotIndex,
                            ItemIdentifier = inv.ItemIdentifier,
                            Charges = inv.Charges
                        });
                    }

                    if (existing.ItemEvents.Count > 0)
                    {
                        _db.MatchItemEvents.RemoveRange(existing.ItemEvents);
                        existing.ItemEvents.Clear();
                    }
                    foreach (MatchItemEvent ev in playerStats.ItemEvents)
                    {
                        existing.ItemEvents.Add(new MatchItemEvent
                        {
                            PlayerStatisticID = existing.ID,
                            MatchID = existing.MatchID,
                            AccountID = existing.AccountID,
                            GameTimeSeconds = ev.GameTimeSeconds,
                            EventType = ev.EventType,
                            ItemIdentifier = ev.ItemIdentifier
                        });
                    }

                    if (existing.AbilityEvents.Count > 0)
                    {
                        _db.MatchAbilityEvents.RemoveRange(existing.AbilityEvents);
                        existing.AbilityEvents.Clear();
                    }
                    foreach (MatchAbilityEvent ev in playerStats.AbilityEvents)
                    {
                        existing.AbilityEvents.Add(new MatchAbilityEvent
                        {
                            PlayerStatisticID = existing.ID,
                            MatchID = existing.MatchID,
                            AccountID = existing.AccountID,
                            HeroIdentifier = ev.HeroIdentifier,
                            AbilityIdentifier = ev.AbilityIdentifier,
                            SlotIndex = ev.SlotIndex,
                            Level = ev.Level,
                            GameTimeSeconds = ev.GameTimeSeconds
                        });
                    }
                }
            }
            else
            {
                _db.PlayerStatistics.Update(playerStats);
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task SavePlayerStatisticsBatchAsync(IEnumerable<PlayerStatistics> playerStatsBatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playerStatsBatch);
        List<PlayerStatistics> list = [.. playerStatsBatch];
        if (list.Count == 0) return;

        foreach (PlayerStatistics ps in list)
        {
            foreach (MatchPlayerInventory inv in ps.Inventories)
            {
                inv.MatchID = ps.MatchID;
                inv.AccountID = ps.AccountID;
            }
            foreach (MatchItemEvent ev in ps.ItemEvents)
            {
                ev.MatchID = ps.MatchID;
                ev.AccountID = ps.AccountID;
            }
            foreach (MatchAbilityEvent ev in ps.AbilityEvents)
            {
                ev.MatchID = ps.MatchID;
                ev.AccountID = ps.AccountID;
            }
        }

        try
        {
            await _db.PlayerStatistics.AddRangeAsync(list, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "[MatchHistoryRepository] Concurrency collision detected while saving batch of {Count} PlayerStatistics. Falling back to individual upserts.",
                list.Count);
            _db.ChangeTracker.Clear();
            foreach (PlayerStatistics item in list) await SavePlayerStatisticsAsync(item, ct);
        }
    }

    public async Task<Mastery?> GetMasteryByAccountIdAsync(int accountId, CancellationToken ct = default)
    {
        return await _db.Masteries
            .Include(m => m.Account)
            .Include(m => m.HeroExperiences)
            .FirstOrDefaultAsync(m => m.AccountID == accountId, ct);
    }

    public async Task SaveMasteryAsync(Mastery mastery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mastery);

        for (int attempt = 0; attempt < 5; attempt++)
            try
            {
                Mastery? existing = await _db.Masteries
                    .Include(m => m.HeroExperiences)
                    .FirstOrDefaultAsync(m => m.AccountID == mastery.AccountID, ct);

                if (existing != null)
                {
                    // Reconcile child collection:
                    // 1. Remove records no longer in incoming mastery
                    existing.HeroExperiences.RemoveAll(e =>
                        !mastery.HeroExperiences.Any(m => string.Equals(m.HeroIdentifier, e.HeroIdentifier, StringComparison.OrdinalIgnoreCase)));

                    // 2. Update existing records or insert new records
                    foreach (HeroMasteryExperience incoming in mastery.HeroExperiences)
                    {
                        HeroMasteryExperience? match = existing.HeroExperiences.FirstOrDefault(e =>
                            string.Equals(e.HeroIdentifier, incoming.HeroIdentifier, StringComparison.OrdinalIgnoreCase));

                        if (match != null)
                        {
                            match.Experience = incoming.Experience;
                        }
                        else
                        {
                            existing.HeroExperiences.Add(new HeroMasteryExperience
                            {
                                MasteryID = existing.ID,
                                Mastery = existing,
                                HeroIdentifier = incoming.HeroIdentifier,
                                Experience = incoming.Experience
                            });
                        }
                    }
                }
                else
                {
                    Mastery newMastery = new()
                    {
                        AccountID = mastery.AccountID,
                        Account = null!,
                        HeroExperiences = []
                    };
                    foreach (HeroMasteryExperience incoming in mastery.HeroExperiences)
                    {
                        newMastery.HeroExperiences.Add(new HeroMasteryExperience
                        {
                            Mastery = newMastery,
                            HeroIdentifier = incoming.HeroIdentifier,
                            Experience = incoming.Experience
                        });
                    }
                    await _db.Masteries.AddAsync(newMastery, ct);
                }

                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException or ArgumentException)
            {
                _logger.LogWarning(ex,
                    "[MatchHistoryRepository] Concurrency collision detected saving Mastery for Account #{AccountId} (attempt {Attempt}). Retrying.",
                    mastery.AccountID, attempt + 1);
                _db.ChangeTracker.Clear();
                if (attempt == 4) throw;
                await Task.Delay(10 * (attempt + 1), ct);
            }
    }

    public async Task<IReadOnlyList<AccountStatistics>> GetTopRankedAccountsAsync(int count = 100,
        CancellationToken ct = default)
    {
        return await _db.AccountStatistics
            .Include(s => s.Account)
            .OrderByDescending(s => s.SkillRating)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MatchStatistics>> GetRecentMatchesAsync(int limit = 50,
        CancellationToken ct = default)
    {
        return await _db.MatchStatistics
            .AsSplitQuery()
            .AsNoTracking()
            .OrderByDescending(m => m.MatchID)
            .Take(limit)
            .ToListAsync(ct);
    }

    // --- Milestone 1 Expanded Methods ---

    public async Task<IReadOnlyList<HeroUsageAggregateDTO>> GetHeroUsageAggregatesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[MatchHistoryRepository] Aggregating hero usage statistics across player statistics");

        return await _db.PlayerStatistics
            .AsNoTracking()
            .Where(ps => !string.IsNullOrEmpty(ps.HeroIdentifier))
            .GroupBy(ps => ps.HeroIdentifier)
            .Select(g => new HeroUsageAggregateDTO
            {
                HeroName = g.Key,
                TotalUse = g.Count(),
                TotalWins = g.Sum(ps => ps.Win),
                TotalLosses = g.Sum(ps => ps.Loss)
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HeroBanAggregateDTO>> GetHeroBanRatesAsync(int limit = 20, CancellationToken ct = default)
    {
        _logger.LogInformation("[MatchHistoryRepository] Aggregating hero ban rates across match bans with limit {Limit}", limit);

        int totalMatches = await _db.MatchStatistics.CountAsync(ct);
        if (totalMatches == 0) return [];

        var banCounts = await _db.MatchBans
            .AsNoTracking()
            .Where(b => !string.IsNullOrEmpty(b.HeroIdentifier))
            .GroupBy(b => b.HeroIdentifier)
            .Select(g => new
            {
                HeroIdentifier = g.Key,
                BanCount = g.Count()
            })
            .OrderByDescending(x => x.BanCount)
            .Take(limit)
            .ToListAsync(ct);

        return banCounts.Select(x => new HeroBanAggregateDTO
        {
            HeroIdentifier = x.HeroIdentifier,
            BanCount = x.BanCount,
            BanRate = Math.Round((double)x.BanCount / totalMatches, 4)
        }).ToList();
    }

    private bool IsPostgresProvider =>
        _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsForAwardSummaryAsync(
        int accountId, string accountName, string cleanName, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[MatchHistoryRepository] Fetching award summary player statistics for Account #{AccountId}, Name: '{AccountName}', Clean: '{CleanName}'",
            accountId, accountName, cleanName);

        if (IsPostgresProvider)
        {
            return await _db.PlayerStatistics
                .AsNoTracking()
                .Where(ps => ps.AccountID == accountId
                             || (!string.IsNullOrEmpty(accountName) && (ps.AccountName == accountName || EF.Functions.ILike(ps.AccountName, accountName)))
                             || (!string.IsNullOrEmpty(cleanName) && (ps.AccountName == cleanName || EF.Functions.ILike(ps.AccountName, cleanName) || EF.Functions.ILike(ps.AccountName, "%" + cleanName))))
                .ToListAsync(ct);
        }

        return await _db.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ps.AccountID == accountId
                         || (!string.IsNullOrEmpty(accountName) && (ps.AccountName == accountName || EF.Functions.Like(ps.AccountName, accountName)))
                         || (!string.IsNullOrEmpty(cleanName) && (ps.AccountName == cleanName || EF.Functions.Like(ps.AccountName, cleanName) || EF.Functions.Like(ps.AccountName, "%" + cleanName))))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsByMatchIdsAsync(
        IEnumerable<int> matchIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(matchIds);
        List<int> ids = matchIds as List<int> ?? [.. matchIds];
        if (ids.Count == 0) return [];

        _logger.LogDebug("[MatchHistoryRepository] Fetching PlayerStatistics for {Count} match IDs", ids.Count);

        return await _db.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ids.Contains(ps.MatchID))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MatchStatistics>> GetMatchesByIdsAsync(
        IEnumerable<int> matchIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(matchIds);
        List<int> ids = matchIds as List<int> ?? [.. matchIds];
        if (ids.Count == 0) return [];

        _logger.LogDebug("[MatchHistoryRepository] Fetching MatchStatistics for {Count} match IDs", ids.Count);

        return await _db.MatchStatistics
            .AsSplitQuery()
            .AsNoTracking()
            .Where(ms => ids.Contains(ms.MatchID))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<int>> GetRecentMatchIdsByAccountAsync(
        int accountId, string? accountName = null, int limit = 100, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[MatchHistoryRepository] Fetching up to {Limit} recent match IDs for Account #{AccountId} ('{AccountName}')",
            limit, accountId, accountName ?? string.Empty);

        IQueryable<PlayerStatistics> query = _db.PlayerStatistics.AsNoTracking();

        if (!string.IsNullOrEmpty(accountName))
        {
            query = IsPostgresProvider
                ? query.Where(ps => ps.AccountID == accountId || ps.AccountName == accountName || EF.Functions.ILike(ps.AccountName, accountName))
                : query.Where(ps => ps.AccountID == accountId || ps.AccountName == accountName || EF.Functions.Like(ps.AccountName, accountName));
        }
        else
            query = query.Where(ps => ps.AccountID == accountId);

        return await query
            .OrderByDescending(ps => ps.MatchID)
            .Take(limit)
            .Select(ps => ps.MatchID)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlayerMatchRecordDTO>> GetPlayerMatchHistoryOverviewAsync(
        int accountId, string nickname, string? table, string? currentSeason, int limit = 100, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[MatchHistoryRepository] Fetching player match history overview for Account #{AccountId} ('{Nickname}'), Table: '{Table}', Season: '{Season}', Limit: {Limit}",
            accountId, nickname, table ?? "default", currentSeason ?? "null", limit);

        bool isPostgres = IsPostgresProvider;

        IQueryable<PlayerMatchRecordDTO> BaseQuery() =>
            from ps in _db.PlayerStatistics.AsNoTracking()
            join ms in _db.MatchStatistics.AsNoTracking() on ps.MatchID equals ms.MatchID into matchGroup
            from ms in matchGroup.DefaultIfEmpty()
            where ps.AccountID == accountId || (!string.IsNullOrEmpty(nickname) && (ps.AccountName == nickname || (isPostgres ? EF.Functions.ILike(ps.AccountName, nickname) : EF.Functions.Like(ps.AccountName, nickname))))
            select new PlayerMatchRecordDTO
            {
                MatchID = ps.MatchID,
                Team = ps.Team,
                HeroKills = ps.HeroKills,
                HeroDeaths = ps.HeroDeaths,
                HeroAssists = ps.HeroAssists,
                HeroProductID = ps.HeroProductID,
                HeroIdentifier = ps.HeroIdentifier,
                AlternativeAvatarProductID = ps.AlternativeAvatarProductID,
                AlternativeAvatarName = ps.AlternativeAvatarName,
                Win = ps.Win,
                RankedMatch = ps.RankedMatch,
                PublicMatch = ps.PublicMatch,
                Map = ms != null ? ms.Map : "caldavar",
                GameMode = ms != null ? ms.GameMode : "caldavar",
                TimestampRecorded = ms != null ? ms.TimestampRecorded : TimeProvider.System.GetUtcNow(),
                TimePlayed = ms != null ? ms.TimePlayed : 1800,
                FileName = ms != null ? ms.FileName : string.Empty,
                HeroLevel = ps.HeroLevel,
                Gold = ps.Gold,
                Experience = ps.Experience,
                WardsPlaced = ps.WardsPlaced
            };

        string cleanTable = table?.Trim().ToLowerInvariant() ?? string.Empty;
        IQueryable<PlayerMatchRecordDTO> filteredQuery = BaseQuery();

        MatchFilterClassification classification = MapCatalog.ClassifyMatchFilter(cleanTable);
        switch (classification.Kind)
        {
            case MatchFilterKind.RankedFoC:
                filteredQuery = isPostgres
                    ? filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.ILike(x.Map, "caldavar") || EF.Functions.ILike(x.Map, "caldavar_halloween")) &&
                        !EF.Functions.ILike(x.GameMode, "%casual%") &&
                        !EF.Functions.ILike(x.GameMode, "%quickmatch%") && x.GameMode != "cm")
                    : filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.Like(x.Map, "caldavar") || EF.Functions.Like(x.Map, "caldavar_halloween")) &&
                        !EF.Functions.Like(x.GameMode, "%casual%") &&
                        !EF.Functions.Like(x.GameMode, "%quickmatch%") && x.GameMode != "cm");
                break;

            case MatchFilterKind.Quickmatch:
                filteredQuery = isPostgres
                    ? filteredQuery.Where(x =>
                        (EF.Functions.ILike(x.GameMode, "%casual%") || EF.Functions.ILike(x.Map, "%casual%") ||
                         EF.Functions.ILike(x.GameMode, "%quickmatch%") || x.GameMode == "cm") &&
                        !EF.Functions.ILike(x.GameMode, "%midwars%") && !EF.Functions.ILike(x.Map, "%midwars%"))
                    : filteredQuery.Where(x =>
                        (EF.Functions.Like(x.GameMode, "%casual%") || EF.Functions.Like(x.Map, "%casual%") ||
                         EF.Functions.Like(x.GameMode, "%quickmatch%") || x.GameMode == "cm") &&
                        !EF.Functions.Like(x.GameMode, "%midwars%") && !EF.Functions.Like(x.Map, "%midwars%"));
                break;

            case MatchFilterKind.RankedMidWars:
                filteredQuery = isPostgres
                    ? filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.ILike(x.GameMode, "%midwars%") || EF.Functions.ILike(x.Map, "%midwars%")))
                    : filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.Like(x.GameMode, "%midwars%") || EF.Functions.Like(x.Map, "%midwars%")));
                break;

            case MatchFilterKind.RankedRiftWars:
                filteredQuery = isPostgres
                    ? filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.ILike(x.GameMode, "%riftwars%") || EF.Functions.ILike(x.Map, "%riftwars%")))
                    : filteredQuery.Where(x => x.RankedMatch == 1 &&
                        (EF.Functions.Like(x.GameMode, "%riftwars%") || EF.Functions.Like(x.Map, "%riftwars%")));
                break;

            case MatchFilterKind.AllPublic:
                filteredQuery = filteredQuery.Where(x => x.PublicMatch == 1 || x.RankedMatch == 0);
                break;

            case MatchFilterKind.PublicMap when !string.IsNullOrEmpty(classification.MapKey):
                string key = classification.MapKey;
                if (key == "caldavar")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%caldavar%") || EF.Functions.ILike(x.GameMode, "%caldavar%")) &&
                            !EF.Functions.ILike(x.GameMode, "%midwars%") && !EF.Functions.ILike(x.Map, "%midwars%") &&
                            !EF.Functions.ILike(x.Map, "%caldavar_wtf%") && !EF.Functions.ILike(x.Map, "%caldavar_10v10%"))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%caldavar%") || EF.Functions.Like(x.GameMode, "%caldavar%")) &&
                            !EF.Functions.Like(x.GameMode, "%midwars%") && !EF.Functions.Like(x.Map, "%midwars%") &&
                            !EF.Functions.Like(x.Map, "%caldavar_wtf%") && !EF.Functions.Like(x.Map, "%caldavar_10v10%"));
                }
                else if (key == "midwars")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%midwars%") || EF.Functions.ILike(x.GameMode, "%midwars%")) &&
                            !EF.Functions.ILike(x.Map, "%10v10%") && !EF.Functions.ILike(x.Map, "%mids_omg%"))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%midwars%") || EF.Functions.Like(x.GameMode, "%midwars%")) &&
                            !EF.Functions.Like(x.Map, "%10v10%") && !EF.Functions.Like(x.Map, "%mids_omg%"));
                }
                else if (key == "riftwars")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%riftwars%") || EF.Functions.ILike(x.GameMode, "%riftwars%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%riftwars%") || EF.Functions.Like(x.GameMode, "%riftwars%")));
                }
                else if (key == "devowars")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%devowars%") || EF.Functions.ILike(x.Map, "%devo_wars%") ||
                            EF.Functions.ILike(x.GameMode, "%devowars%") || EF.Functions.ILike(x.GameMode, "%devo_wars%")) &&
                            !EF.Functions.ILike(x.Map, "%truedevo%") && !EF.Functions.ILike(x.Map, "%10v10%"))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%devowars%") || EF.Functions.Like(x.Map, "%devo_wars%") ||
                            EF.Functions.Like(x.GameMode, "%devowars%") || EF.Functions.Like(x.GameMode, "%devo_wars%")) &&
                            !EF.Functions.Like(x.Map, "%truedevo%") && !EF.Functions.Like(x.Map, "%10v10%"));
                }
                else if (key == "truedevo")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%truedevo%") || EF.Functions.ILike(x.GameMode, "%truedevo%")) &&
                            !EF.Functions.ILike(x.Map, "%10v10%"))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%truedevo%") || EF.Functions.Like(x.GameMode, "%truedevo%")) &&
                            !EF.Functions.Like(x.Map, "%10v10%"));
                }
                else if (key == "prophets")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%prophet%") || EF.Functions.ILike(x.GameMode, "%prophet%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%prophet%") || EF.Functions.Like(x.GameMode, "%prophet%")));
                }
                else if (key == "strikers")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%striker%") || EF.Functions.ILike(x.Map, "%soccer%") ||
                            EF.Functions.ILike(x.GameMode, "%striker%") || EF.Functions.ILike(x.GameMode, "%soccer%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%striker%") || EF.Functions.Like(x.Map, "%soccer%") ||
                            EF.Functions.Like(x.GameMode, "%striker%") || EF.Functions.Like(x.GameMode, "%soccer%")));
                }
                else if (key == "grimmscrossing")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%grimm%") || EF.Functions.ILike(x.GameMode, "%grimm%")) &&
                            !EF.Functions.ILike(x.Map, "%grimmhunt%"))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%grimm%") || EF.Functions.Like(x.GameMode, "%grimm%")) &&
                            !EF.Functions.Like(x.Map, "%grimmhunt%"));
                }
                else if (key == "thegrimmhunt")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%grimmhunt%") || EF.Functions.ILike(x.GameMode, "%grimmhunt%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%grimmhunt%") || EF.Functions.Like(x.GameMode, "%grimmhunt%")));
                }
                else if (key == "darkwoodvale")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%darkwood%") || EF.Functions.ILike(x.GameMode, "%darkwood%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%darkwood%") || EF.Functions.Like(x.GameMode, "%darkwood%")));
                }
                else if (key == "ctf")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%ctf%") || EF.Functions.ILike(x.Map, "%capturetheflag%") ||
                            EF.Functions.ILike(x.GameMode, "%ctf%") || EF.Functions.ILike(x.GameMode, "%capturetheflag%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%ctf%") || EF.Functions.Like(x.Map, "%capturetheflag%") ||
                            EF.Functions.Like(x.GameMode, "%ctf%") || EF.Functions.Like(x.GameMode, "%capturetheflag%")));
                }
                else if (key == "team_deathmatch")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%deathmatch%") || EF.Functions.ILike(x.GameMode, "%deathmatch%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%deathmatch%") || EF.Functions.Like(x.GameMode, "%deathmatch%")));
                }
                else if (key == "caldavar_wtf")
                {
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, "%caldavar_wtf%") || EF.Functions.ILike(x.GameMode, "%caldavar_wtf%")))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, "%caldavar_wtf%") || EF.Functions.Like(x.GameMode, "%caldavar_wtf%")));
                }
                else
                {
                    string pattern = $"%{key}%";
                    filteredQuery = isPostgres
                        ? filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.ILike(x.Map, pattern) || EF.Functions.ILike(x.GameMode, pattern)))
                        : filteredQuery.Where(x => (x.PublicMatch == 1 || x.RankedMatch == 0) &&
                            (EF.Functions.Like(x.Map, pattern) || EF.Functions.Like(x.GameMode, pattern)));
                }
                break;

            case MatchFilterKind.All:
            default:
                break;
        }

        List<PlayerMatchRecordDTO> historyData = await filteredQuery
            .OrderByDescending(x => x.MatchID)
            .Take(limit)
            .ToListAsync(ct);

        return historyData;
    }

    public async Task<IReadOnlyList<PlayerStatistics>> GetPlayerHeroStatisticsAsync(
        int accountId, string heroIdentifier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heroIdentifier);

        _logger.LogInformation(
            "[MatchHistoryRepository] Fetching PlayerStatistics for Account #{AccountId}, Hero: '{HeroIdentifier}'",
            accountId, heroIdentifier);

        return await _db.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ps.AccountID == accountId && (ps.HeroIdentifier == heroIdentifier || ps.HeroIdentifier.ToLower() == heroIdentifier.ToLower()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlayerStatistics>> GetAllPlayerStatisticsForAccountAsync(
        int accountId, string? accountName = null, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[MatchHistoryRepository] Fetching all PlayerStatistics for Account #{AccountId} ('{AccountName}')",
            accountId, accountName ?? string.Empty);

        IQueryable<PlayerStatistics> query = _db.PlayerStatistics.AsNoTracking();

        if (!string.IsNullOrEmpty(accountName))
            query = query.Where(ps => ps.AccountID == accountId || ps.AccountName == accountName || EF.Functions.Like(ps.AccountName, accountName));
        else
            query = query.Where(ps => ps.AccountID == accountId);

        return await query.ToListAsync(ct);
    }

    public async Task<bool> UpdateMatchReplayUrlAsync(
        int matchId, string? replayUrl, int? fileSize = null, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[MatchHistoryRepository] Updating ReplayUrl for Match #{MatchId}: '{ReplayUrl}', FileSize: {FileSize}",
            matchId, replayUrl ?? "null", fileSize);

        try
        {
            MatchStatistics? match = await _db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId, ct);
            if (match == null)
            {
                _logger.LogWarning("[MatchHistoryRepository] Match #{MatchId} not found when updating ReplayUrl", matchId);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(replayUrl))
            {
                match.ReplayUrl = replayUrl;
            }

            if (fileSize.HasValue && fileSize.Value > 0)
            {
                match.FileSize = fileSize.Value;
            }

            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbUpdateConcurrencyException)
        {
            _logger.LogWarning(ex,
                "[MatchHistoryRepository] Concurrency collision detected updating ReplayUrl for Match #{MatchId}. Retrying.",
                matchId);
            _db.ChangeTracker.Clear();

            MatchStatistics? match = await _db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId, ct);
            if (match == null) return false;

            match.ReplayUrl = replayUrl;
            if (fileSize.HasValue && fileSize.Value > 0)
                match.FileSize = fileSize.Value;

            await _db.SaveChangesAsync(ct);
            return true;
        }
    }

    public async Task<IReadOnlyList<HeroItemPopularityDTO>> GetTopItemBuildsByHeroAsync(string heroIdentifier, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(heroIdentifier)) return [];

        var query = from inv in _db.MatchPlayerInventories.AsNoTracking()
                    join stat in _db.PlayerStatistics.AsNoTracking() on inv.PlayerStatisticID equals stat.ID
                    where stat.HeroIdentifier == heroIdentifier && !string.IsNullOrEmpty(inv.ItemIdentifier)
                    group new { inv, stat } by inv.ItemIdentifier into g
                    select new HeroItemPopularityDTO
                    {
                        HeroIdentifier = heroIdentifier,
                        ItemIdentifier = g.Key,
                        MatchesCount = g.Count(),
                        WinsCount = g.Sum(x => x.stat.Win > 0 ? 1 : 0)
                    };

        return await query
            .OrderByDescending(x => x.MatchesCount)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HeroItemTimingDTO>> GetAverageItemTimingsAsync(string heroIdentifier, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(heroIdentifier)) return [];

        var query = from ev in _db.MatchItemEvents.AsNoTracking()
                    join stat in _db.PlayerStatistics.AsNoTracking() on ev.PlayerStatisticID equals stat.ID
                    where stat.HeroIdentifier == heroIdentifier && ev.EventType == 1 && !string.IsNullOrEmpty(ev.ItemIdentifier)
                    group ev by ev.ItemIdentifier into g
                    select new HeroItemTimingDTO
                    {
                        HeroIdentifier = heroIdentifier,
                        ItemIdentifier = g.Key,
                        PurchasesCount = g.Count(),
                        AverageGameTimeSeconds = g.Average(x => (double)x.GameTimeSeconds),
                        FastestGameTimeSeconds = g.Min(x => x.GameTimeSeconds)
                    };

        return await query
            .OrderByDescending(x => x.PurchasesCount)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HeroSkillBuildWinRateDTO>> GetHeroSkillBuildWinRatesAsync(string heroIdentifier, int level = 1, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(heroIdentifier)) return [];

        var query = from ab in _db.MatchAbilityEvents.AsNoTracking()
                    join stat in _db.PlayerStatistics.AsNoTracking() on ab.PlayerStatisticID equals stat.ID
                    where stat.HeroIdentifier == heroIdentifier && ab.Level == level && !string.IsNullOrEmpty(ab.AbilityIdentifier)
                    group new { ab, stat } by new { ab.AbilityIdentifier, ab.SlotIndex, ab.Level } into g
                    select new HeroSkillBuildWinRateDTO
                    {
                        HeroIdentifier = heroIdentifier,
                        AbilityIdentifier = g.Key.AbilityIdentifier,
                        SlotIndex = g.Key.SlotIndex,
                        Level = g.Key.Level,
                        PickCount = g.Count(),
                        WinsCount = g.Sum(x => x.stat.Win > 0 ? 1 : 0)
                    };

        return await query
            .OrderByDescending(x => x.PickCount)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlayerRivalryDTO>> GetPlayerRivalriesAndNemesisAsync(int accountId, int limit = 10, CancellationToken ct = default)
    {
        if (accountId <= 0) return [];

        var kills = await _db.MatchFrags.AsNoTracking()
            .Where(f => f.KillerAccountID == accountId && f.VictimAccountID > 0)
            .GroupBy(f => f.VictimAccountID)
            .Select(g => new { OpponentID = g.Key, Kills = g.Count() })
            .ToListAsync(ct);

        var deaths = await _db.MatchFrags.AsNoTracking()
            .Where(f => f.VictimAccountID == accountId && f.KillerAccountID > 0)
            .GroupBy(f => f.KillerAccountID)
            .Select(g => new { OpponentID = g.Key, Deaths = g.Count() })
            .ToListAsync(ct);

        HashSet<int> opponents = kills.Select(k => k.OpponentID).Concat(deaths.Select(d => d.OpponentID)).ToHashSet();
        Dictionary<int, int> killMap = kills.ToDictionary(k => k.OpponentID, k => k.Kills);
        Dictionary<int, int> deathMap = deaths.ToDictionary(d => d.OpponentID, d => d.Deaths);

        List<PlayerRivalryDTO> result = opponents
            .Select(opId => new PlayerRivalryDTO
            {
                AccountID = accountId,
                OpponentAccountID = opId,
                KillsOnOpponent = killMap.GetValueOrDefault(opId, 0),
                DeathsToOpponent = deathMap.GetValueOrDefault(opId, 0)
            })
            .OrderByDescending(r => r.KillsOnOpponent + r.DeathsToOpponent)
            .Take(limit)
            .ToList();

        return result;
    }

    public async Task<IReadOnlyList<MatchFrag>> GetMatchFragsAsync(int matchId, CancellationToken ct = default)
    {
        return await _db.MatchFrags.AsNoTracking()
            .Include(f => f.Assists)
            .Where(f => f.MatchID == matchId)
            .OrderBy(f => f.GameTimeSeconds)
            .ThenBy(f => f.ID)
            .ToListAsync(ct);
    }
}
