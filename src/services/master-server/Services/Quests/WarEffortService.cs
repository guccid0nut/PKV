// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.Globalization;
using ASPIRE.Common.Services.Distributed;
using KONGOR.MasterServer.Configuration.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Quests;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.Quests;

namespace KONGOR.MasterServer.Services.Quests;

public sealed class WarEffortService : IWarEffortService
{
    private const int ActiveQuestSlotCount = 2;
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(10);

    private readonly MerrickContext _db;
    private readonly IOptionsMonitor<WarEffortOptions> _optionsMonitor;
    private readonly IDistributedLockService? _distributedLockService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WarEffortService> _logger;

    public WarEffortService(
        MerrickContext db,
        IOptionsMonitor<WarEffortOptions> optionsMonitor,
        ILogger<WarEffortService> logger,
        IDistributedLockService? distributedLockService = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _distributedLockService = distributedLockService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsEnabled => _optionsMonitor.CurrentValue.Enabled;

    public async Task<WarEffortAccountState> EnsureActiveCycleAndAccountStateAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        WarEffortOptions opts = _optionsMonitor.CurrentValue;

        await WarEffortCatalogLoader.EnsureCatalogSeededAsync(_db, _logger, cancellationToken).ConfigureAwait(false);
        WarEffortCycle activeCycle = await EnsureActiveCycleAsync(nowUtc, opts, cancellationToken).ConfigureAwait(false);
        await WarEffortCatalogLoader.EnsureBagMilestonesSeededAsync(_db, activeCycle, _logger, cancellationToken).ConfigureAwait(false);

        List<QuestDefinition> allDefs = await _db.QuestDefinitions
            .AsNoTracking()
            .Where(q => q.IsEnabled)
            .OrderBy(q => q.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, QuestDefinition> defsById = allDefs.ToDictionary(q => q.Id);

        AccountWarEffortProgress? progress = await _db.AccountWarEffortProgress
            .FirstOrDefaultAsync(p => p.AccountId == accountId && p.CycleId == activeCycle.Id, cancellationToken)
            .ConfigureAwait(false);

        string todayKey = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool dirty = false;

        if (progress is null)
        {
            progress = new AccountWarEffortProgress
            {
                AccountId = accountId,
                CycleId = activeCycle.Id,
                TotalBagPoints = 0,
                QuestsCompletedCount = 0,
                QuestsResetCount = 0,
                RerollsAvailable = Math.Clamp(1, 0, Math.Max(1, opts.DailyRerollCap)),
                LastDailyRefreshUtcDate = todayKey,
                LastUpdatedUtc = nowUtc
            };
            _db.AccountWarEffortProgress.Add(progress);
            dirty = true;
            _logger.LogInformation(
                "[WAR_EFFORT] Initialized progress for AccountId={AccountId} Cycle={CycleNumber} (Rerolls={Rerolls})",
                accountId, activeCycle.CycleNumber, progress.RerollsAvailable);
        }
        else if (!string.Equals(progress.LastDailyRefreshUtcDate, todayKey, StringComparison.Ordinal))
        {
            int cap = Math.Max(1, opts.DailyRerollCap);
            int before = progress.RerollsAvailable;
            progress.RerollsAvailable = Math.Min(cap, progress.RerollsAvailable + 1);
            progress.LastDailyRefreshUtcDate = todayKey;
            progress.LastUpdatedUtc = nowUtc;
            dirty = true;
            _logger.LogInformation(
                "[WAR_EFFORT] Daily 00:00 UTC refresh for AccountId={AccountId} Date={Date}: Rerolls {Before}->{After} (cap={Cap})",
                accountId, todayKey, before, progress.RerollsAvailable, cap);
        }

        List<AccountQuestSlot> slots = await _db.AccountQuestSlots
            .Where(s => s.AccountId == accountId && s.CycleId == activeCycle.Id)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> activeQuestIds = slots
            .Where(s => s.CompletedAtUtc is null && defsById.ContainsKey(s.QuestDefinitionId))
            .Select(s => s.QuestDefinitionId)
            .ToHashSet();

        for (int slotIdx = 0; slotIdx < ActiveQuestSlotCount; slotIdx++)
        {
            AccountQuestSlot? slot = slots.FirstOrDefault(s => s.SlotIndex == slotIdx);
            bool needsAssignment = slot is null
                                   || slot.CompletedAtUtc is not null
                                   || !defsById.ContainsKey(slot.QuestDefinitionId);

            if (!needsAssignment)
            {
                continue;
            }

            QuestDefinition? picked = PickWeightedQuest(allDefs, activeQuestIds, accountId, activeCycle.CycleNumber, slotIdx, nowUtc);
            if (picked is null)
            {
                continue;
            }

            activeQuestIds.Add(picked.Id);
            if (slot is null)
            {
                slot = new AccountQuestSlot
                {
                    AccountId = accountId,
                    CycleId = activeCycle.Id,
                    SlotIndex = slotIdx,
                    QuestDefinitionId = picked.Id,
                    Progress = 0,
                    Goal = Math.Max(1, picked.GoalValue),
                    AssignedAtUtc = nowUtc,
                    CompletedAtUtc = null
                };
                _db.AccountQuestSlots.Add(slot);
                slots.Add(slot);
            }
            else
            {
                slot.QuestDefinitionId = picked.Id;
                slot.Progress = 0;
                slot.Goal = Math.Max(1, picked.GoalValue);
                slot.AssignedAtUtc = nowUtc;
                slot.CompletedAtUtc = null;
                slot.LastMatchId = null;
            }

            dirty = true;
            _logger.LogInformation(
                "[WAR_EFFORT] Assigned AccountId={AccountId} Slot={SlotIndex} -> QuestId={QuestId} ({Code}, Goal={Goal}, BagPts={BagPts})",
                accountId, slotIdx, picked.Id, picked.Code, picked.GoalValue, picked.BagPointsReward);
        }

        if (dirty)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        List<WarEffortBagMilestone> milestones = await _db.WarEffortBagMilestones
            .AsNoTracking()
            .Where(m => m.CycleId == activeCycle.Id)
            .OrderBy(m => m.BagIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AccountWarEffortBagClaim> claims = await _db.AccountWarEffortBagClaims
            .AsNoTracking()
            .Where(c => c.AccountId == accountId && c.CycleId == activeCycle.Id)
            .OrderBy(c => c.BagIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        slots = slots.OrderBy(s => s.SlotIndex).ToList();
        return new WarEffortAccountState(activeCycle, progress, slots, milestones, claims, defsById);
    }

    public async Task<Dictionary<string, object?>> BuildClientPayloadAsync(
        int accountId,
        bool includeOnlyNativeTriggers = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "disabled"
            };
        }

        WarEffortAccountState state = await EnsureActiveCycleAndAccountStateAsync(accountId, cancellationToken).ConfigureAwait(false);
        return BuildPayloadFromState(accountId, state, includeOnlyNativeTriggers);
    }

    public async Task<Dictionary<string, object?>> BuildDedicatedServerPlayerQuestsPayloadAsync(
        IReadOnlyCollection<int> accountIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || accountIds.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "disabled"
            };
        }

        List<Dictionary<string, object?>> questTypes = [];
        Dictionary<string, int> typeIdByKey = new(StringComparer.Ordinal);
        List<Dictionary<string, object?>> questQueue = [];
        HashSet<int> queuedQuestIds = [];
        Dictionary<string, object?> playerQuestQueue = new();

        foreach (int accountId in accountIds.Where(id => id > 0).Distinct())
        {
            WarEffortAccountState state = await EnsureActiveCycleAndAccountStateAsync(accountId, cancellationToken).ConfigureAwait(false);
            List<Dictionary<string, object?>> playerEntries = [];

            foreach (AccountQuestSlot slot in state.Slots)
            {
                if (!state.Definitions.TryGetValue(slot.QuestDefinitionId, out QuestDefinition? def))
                {
                    continue;
                }

                // Dedicated game servers ONLY understand the 18 native C++ EAchievementTrigger enums.
                // Custom kongor_* objectives are evaluated authoritatively by MasterServer Stage 2.
                if (!def.IsNativeEngineTrigger)
                {
                    continue;
                }

                int typeId = GetOrAddQuestType(questTypes, typeIdByKey, def);
                if (queuedQuestIds.Add(def.Id))
                {
                    questQueue.Add(BuildQuestQueueEntry(def, typeId));
                }

                playerEntries.Add(BuildPlayerQuestEntry(slot, def));
            }

            playerQuestQueue[accountId.ToString(CultureInfo.InvariantCulture)] = playerEntries;
        }

        _logger.LogInformation(
            "[WAR_EFFORT] Built dedicated server getplayerquests payload for {AccountCount} accounts ({NativeQuestCount} native quests).",
            playerQuestQueue.Count, questQueue.Count);

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["quest_types"] = questTypes,
            ["quest_queue"] = questQueue,
            ["player_quest_queue"] = playerQuestQueue
        };
    }

    public async Task<Dictionary<string, object?>> ResetQuestAsync(
        int accountId,
        int questId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "disabled" };
        }

        await using IDistributedLock? acquiredLock = await AcquireAccountLockAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (acquiredLock is not null && !acquiredLock.IsAcquired)
        {
            _logger.LogWarning("[WAR_EFFORT] ResetQuest lock contention for AccountId={AccountId} QuestId={QuestId}", accountId, questId);
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "busy" };
        }

        WarEffortAccountState state = await EnsureActiveCycleAndAccountStateAsync(accountId, cancellationToken).ConfigureAwait(false);
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        AccountWarEffortProgress? progress = await _db.AccountWarEffortProgress
            .FirstOrDefaultAsync(p => p.AccountId == accountId && p.CycleId == state.Cycle.Id, cancellationToken)
            .ConfigureAwait(false);

        if (progress is null || progress.RerollsAvailable <= 0)
        {
            _logger.LogInformation("[WAR_EFFORT] ResetQuest rejected (no_resets_available) for AccountId={AccountId} QuestId={QuestId}", accountId, questId);
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "no_resets_available"
            };
        }

        List<AccountQuestSlot> trackedSlots = await _db.AccountQuestSlots
            .Where(s => s.AccountId == accountId && s.CycleId == state.Cycle.Id)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        AccountQuestSlot? targetSlot = trackedSlots.FirstOrDefault(s => s.QuestDefinitionId == questId);
        if (targetSlot is null)
        {
            _logger.LogWarning("[WAR_EFFORT] ResetQuest rejected (invalid_quest_id) for AccountId={AccountId} QuestId={QuestId}", accountId, questId);
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "invalid_quest_id"
            };
        }

        List<QuestDefinition> allDefs = await _db.QuestDefinitions
            .AsNoTracking()
            .Where(q => q.IsEnabled)
            .OrderBy(q => q.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> excludeIds = trackedSlots.Select(s => s.QuestDefinitionId).ToHashSet();
        QuestDefinition? replacement = PickWeightedQuest(
            allDefs,
            excludeIds,
            accountId,
            state.Cycle.CycleNumber,
            targetSlot.SlotIndex + progress.QuestsResetCount + 1,
            nowUtc);

        if (replacement is null)
        {
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "no_replacement_quest"
            };
        }

        int oldQuestId = targetSlot.QuestDefinitionId;
        targetSlot.QuestDefinitionId = replacement.Id;
        targetSlot.Progress = 0;
        targetSlot.Goal = Math.Max(1, replacement.GoalValue);
        targetSlot.AssignedAtUtc = nowUtc;
        targetSlot.CompletedAtUtc = null;
        targetSlot.LastMatchId = null;

        progress.RerollsAvailable = Math.Max(0, progress.RerollsAvailable - 1);
        progress.QuestsResetCount += 1;
        progress.LastUpdatedUtc = nowUtc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[WAR_EFFORT] ResetQuest succeeded for AccountId={AccountId} Slot={SlotIndex}: {OldQuestId} -> {NewQuestId} ({Code}), RerollsLeft={RerollsLeft}",
            accountId, targetSlot.SlotIndex, oldQuestId, replacement.Id, replacement.Code, progress.RerollsAvailable);

        Dictionary<string, object?> payload = await BuildClientPayloadAsync(accountId, includeOnlyNativeTriggers: false, cancellationToken).ConfigureAwait(false);
        payload["reset_quest_id"] = oldQuestId;
        payload["new_quest_id"] = replacement.Id;
        return payload;
    }

    public async Task<Dictionary<string, object?>> OpenBagAsync(
        int accountId,
        int bagId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "disabled" };
        }

        await using IDistributedLock? acquiredLock = await AcquireAccountLockAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (acquiredLock is not null && !acquiredLock.IsAcquired)
        {
            _logger.LogWarning("[WAR_EFFORT] OpenBag lock contention for AccountId={AccountId} BagId={BagId}", accountId, bagId);
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "busy" };
        }

        WarEffortAccountState state = await EnsureActiveCycleAndAccountStateAsync(accountId, cancellationToken).ConfigureAwait(false);
        WarEffortBagMilestone? milestone = state.Milestones.FirstOrDefault(m => m.BagId == bagId);
        if (milestone is null)
        {
            _logger.LogWarning("[WAR_EFFORT] OpenBag invalid bag_id={BagId} for AccountId={AccountId}", bagId, accountId);
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "invalid_bag" };
        }

        AccountWarEffortBagClaim? existingClaim = await _db.AccountWarEffortBagClaims
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.AccountId == accountId && c.CycleId == state.Cycle.Id && c.BagIndex == milestone.BagIndex,
                cancellationToken)
            .ConfigureAwait(false);

        if (existingClaim is not null)
        {
            _logger.LogInformation(
                "[WAR_EFFORT] OpenBag idempotent replay for AccountId={AccountId} BagId={BagId} (ClaimId={ClaimId})",
                accountId, bagId, existingClaim.Id);
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "bag_already_opened" };
        }

        if (state.Progress.TotalBagPoints < milestone.RequiredPoints)
        {
            _logger.LogInformation(
                "[WAR_EFFORT] OpenBag rejected (requirements_not_met) AccountId={AccountId} BagId={BagId}: {Points}/{Required}",
                accountId, bagId, state.Progress.TotalBagPoints, milestone.RequiredPoints);
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "bag_requirements_not_met" };
        }

        Account? account = await _db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, cancellationToken)
            .ConfigureAwait(false);

        if (account?.User is null)
        {
            return new Dictionary<string, object?> { ["success"] = false, ["error"] = "account_not_found" };
        }

        (AccountWarEffortBagClaim claim, Dictionary<string, object?> rewardInfo) = await GrantMilestoneRewardAsync(
            account,
            state.Cycle,
            milestone,
            autoClaimedAtRollover: false,
            cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[WAR_EFFORT] OpenBag granted AccountId={AccountId} Cycle={CycleNumber} Bag={BagIndex} (BagId={BagId}): Type={RewardType} Amount={Amount} ProductId={ProductId} AltFallback={AltFallback} IdempotencyKey={IdemKey}",
            accountId, state.Cycle.CycleNumber, milestone.BagIndex, milestone.BagId,
            claim.RewardType, claim.RewardAmount, claim.GrantedProductId, claim.WasAltFallback, claim.IdempotencyKey);

        string ts = claim.ClaimedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["mmpoints"] = account.User.SilverCoins,
            ["points"] = account.User.GoldCoins,
            ["plinko_tickets"] = account.User.PlinkoTickets,
            ["product_ids"] = claim.GrantedProductId ?? 0,
            ["bag"] = new Dictionary<string, object?>
            {
                ["bagid"] = milestone.BagId.ToString(CultureInfo.InvariantCulture),
                ["active"] = "1",
                ["baggroupid"] = state.Cycle.CycleNumber.ToString(CultureInfo.InvariantCulture),
                ["priority"] = "NORMAL",
                ["achievement"] = milestone.RequiredPoints.ToString(CultureInfo.InvariantCulture),
                ["reward"] = claim.RewardAmount.ToString(CultureInfo.InvariantCulture),
                ["rewardtype"] = claim.RewardType,
                ["addtimestamp"] = ts,
                ["modifytimestamp"] = ts
            },
            ["info"] = rewardInfo
        };
    }

    public async Task<(int QuestsCompleted, int QuestsReset, int TotalBagPoints)> GetQuestProfileStatsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || accountId <= 0)
        {
            return (0, 0, 0);
        }

        List<AccountWarEffortProgress> rows = await _db.AccountWarEffortProgress
            .AsNoTracking()
            .Where(p => p.AccountId == accountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            rows.Sum(r => r.QuestsCompletedCount),
            rows.Sum(r => r.QuestsResetCount),
            rows.Sum(r => r.TotalBagPoints));
    }

    public async Task<(IReadOnlyList<WarEffortLeaderboardEntry> Entries, int TotalEntries, int CycleNumber)> GetLeaderboardAsync(
        int page,
        int pageSize,
        int? cycleNumber = null,
        CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        WarEffortOptions opts = _optionsMonitor.CurrentValue;
        WarEffortCycle cycle = cycleNumber.HasValue
            ? (await _db.WarEffortCycles.AsNoTracking().FirstOrDefaultAsync(c => c.CycleNumber == cycleNumber.Value, cancellationToken).ConfigureAwait(false))
              ?? await EnsureActiveCycleAsync(nowUtc, opts, cancellationToken).ConfigureAwait(false)
            : await EnsureActiveCycleAsync(nowUtc, opts, cancellationToken).ConfigureAwait(false);

        IQueryable<AccountWarEffortProgress> baseQuery = _db.AccountWarEffortProgress
            .AsNoTracking()
            .Where(p => p.CycleId == cycle.Id && (p.TotalBagPoints > 0 || p.QuestsCompletedCount > 0))
            .OrderByDescending(p => p.TotalBagPoints)
            .ThenByDescending(p => p.QuestsCompletedCount)
            .ThenBy(p => p.AccountId);

        int totalEntries = await baseQuery.CountAsync(cancellationToken).ConfigureAwait(false);
        int clampedPage = Math.Max(1, page);
        int clampedSize = Math.Clamp(pageSize, 1, 100);
        int skip = (clampedPage - 1) * clampedSize;

        List<AccountWarEffortProgress> slice = await baseQuery
            .Skip(skip)
            .Take(clampedSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (slice.Count == 0)
        {
            return ([], 0, cycle.CycleNumber);
        }

        List<int> accountIds = slice.Select(s => s.AccountId).Distinct().ToList();
        Dictionary<int, (string Name, string? ClanTag)> accountLookup = await _db.Accounts
            .AsNoTracking()
            .Include(a => a.Clan)
            .Where(a => accountIds.Contains(a.ID))
            .ToDictionaryAsync(a => a.ID, a => (a.Name, a.Clan?.Tag), cancellationToken)
            .ConfigureAwait(false);

        List<WarEffortLeaderboardEntry> entries = new(slice.Count);
        for (int i = 0; i < slice.Count; i++)
        {
            AccountWarEffortProgress row = slice[i];
            (string name, string? clanTag) = accountLookup.TryGetValue(row.AccountId, out var found)
                ? found
                : ($"Hero_{row.AccountId}", null);
            entries.Add(new WarEffortLeaderboardEntry(
                skip + i + 1,
                row.AccountId,
                name,
                clanTag,
                row.TotalBagPoints,
                row.QuestsCompletedCount));
        }

        return (entries, totalEntries, cycle.CycleNumber);
    }

    internal async Task<WarEffortCycle> EnsureActiveCycleAsync(
        DateTime nowUtc,
        WarEffortOptions opts,
        CancellationToken cancellationToken)
    {
        WarEffortCycle? activeCycle = await _db.WarEffortCycles
            .OrderByDescending(c => c.CycleNumber)
            .FirstOrDefaultAsync(c => c.IsActive, cancellationToken)
            .ConfigureAwait(false);

        int cycleLengthDays = Math.Max(1, opts.CycleLengthDays);

        if (activeCycle is null)
        {
            DateTime startOfToday = new(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
            WarEffortCycle first = new()
            {
                CycleNumber = 1,
                Name = "War Effort Cycle 1",
                StartsAtUtc = startOfToday,
                EndsAtUtc = startOfToday.AddDays(cycleLengthDays),
                IsActive = true,
                CreatedAtUtc = nowUtc
            };
            _db.WarEffortCycles.Add(first);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "[WAR_EFFORT] Created initial War Effort Cycle #1 ({Start:u} -> {End:u})",
                first.StartsAtUtc, first.EndsAtUtc);
            return first;
        }

        if (nowUtc < activeCycle.EndsAtUtc)
        {
            return activeCycle;
        }

        // Cycle expired: execute rollover (auto-payout unclaimed completed bags + capture leaderboard snapshot)
        _logger.LogInformation(
            "[WAR_EFFORT] Rolling over expired Cycle #{CycleNumber} (Ended={EndsAtUtc:u}, Now={NowUtc:u})",
            activeCycle.CycleNumber, activeCycle.EndsAtUtc, nowUtc);

        await AutoPayoutAndSnapshotExpiredCycleAsync(activeCycle, nowUtc, cancellationToken).ConfigureAwait(false);

        activeCycle.IsActive = false;
        int nextCycleNumber = activeCycle.CycleNumber + 1;
        DateTime nextStart = new(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        WarEffortCycle nextCycle = new()
        {
            CycleNumber = nextCycleNumber,
            Name = $"War Effort Cycle {nextCycleNumber}",
            StartsAtUtc = nextStart,
            EndsAtUtc = nextStart.AddDays(cycleLengthDays),
            IsActive = true,
            CreatedAtUtc = nowUtc
        };
        _db.WarEffortCycles.Add(nextCycle);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WarEffortCatalogLoader.EnsureBagMilestonesSeededAsync(_db, nextCycle, _logger, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[WAR_EFFORT] Activated new Cycle #{CycleNumber} ({Start:u} -> {End:u})",
            nextCycle.CycleNumber, nextCycle.StartsAtUtc, nextCycle.EndsAtUtc);

        return nextCycle;
    }

    private async Task AutoPayoutAndSnapshotExpiredCycleAsync(
        WarEffortCycle expiredCycle,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        List<AccountWarEffortProgress> progressRows = await _db.AccountWarEffortProgress
            .Where(p => p.CycleId == expiredCycle.Id)
            .OrderByDescending(p => p.TotalBagPoints)
            .ThenByDescending(p => p.QuestsCompletedCount)
            .ThenBy(p => p.AccountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<WarEffortBagMilestone> milestones = await _db.WarEffortBagMilestones
            .AsNoTracking()
            .Where(m => m.CycleId == expiredCycle.Id)
            .OrderBy(m => m.BagIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AccountWarEffortBagClaim> existingClaims = await _db.AccountWarEffortBagClaims
            .AsNoTracking()
            .Where(c => c.CycleId == expiredCycle.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<(int AccountId, int BagIndex)> claimedSet = existingClaims
            .Select(c => (c.AccountId, c.BagIndex))
            .ToHashSet();

        bool hasSnapshot = await _db.WarEffortCycleLeaderboardSnapshots
            .AnyAsync(s => s.CycleId == expiredCycle.Id, cancellationToken)
            .ConfigureAwait(false);

        int autoClaimCount = 0;
        for (int i = 0; i < progressRows.Count; i++)
        {
            AccountWarEffortProgress row = progressRows[i];

            if (!hasSnapshot && (row.TotalBagPoints > 0 || row.QuestsCompletedCount > 0))
            {
                _db.WarEffortCycleLeaderboardSnapshots.Add(new WarEffortCycleLeaderboardSnapshot
                {
                    CycleId = expiredCycle.Id,
                    AccountId = row.AccountId,
                    Rank = i + 1,
                    TotalBagPoints = row.TotalBagPoints,
                    QuestsCompletedCount = row.QuestsCompletedCount,
                    CapturedAtUtc = nowUtc
                });
            }

            List<WarEffortBagMilestone> unclaimedEligible = milestones
                .Where(m => row.TotalBagPoints >= m.RequiredPoints && !claimedSet.Contains((row.AccountId, m.BagIndex)))
                .ToList();

            if (unclaimedEligible.Count == 0)
            {
                continue;
            }

            Account? account = await _db.Accounts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.ID == row.AccountId, cancellationToken)
                .ConfigureAwait(false);

            if (account?.User is null)
            {
                continue;
            }

            foreach (WarEffortBagMilestone milestone in unclaimedEligible)
            {
                await GrantMilestoneRewardAsync(
                    account,
                    expiredCycle,
                    milestone,
                    autoClaimedAtRollover: true,
                    cancellationToken).ConfigureAwait(false);
                claimedSet.Add((row.AccountId, milestone.BagIndex));
                autoClaimCount++;
            }
        }

        _logger.LogInformation(
            "[WAR_EFFORT] Cycle #{CycleNumber} rollover prepared: LeaderboardRows={Rows}, AutoClaimedBags={AutoClaims}",
            expiredCycle.CycleNumber, progressRows.Count, autoClaimCount);
    }

    private async Task<(AccountWarEffortBagClaim Claim, Dictionary<string, object?> RewardInfo)> GrantMilestoneRewardAsync(
        Account account,
        WarEffortCycle cycle,
        WarEffortBagMilestone milestone,
        bool autoClaimedAtRollover,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        User user = account.User!;
        string idempotencyKey = $"wareffort:{cycle.CycleNumber}:{milestone.BagIndex}:{account.ID}";

        string effectiveRewardType = milestone.RewardType;
        int effectiveAmount = milestone.RewardAmount;
        int? grantedProductId = null;
        bool wasAltFallback = false;
        Dictionary<string, object?> rewardInfo = new();

        int silverDelta = 0;
        int goldDelta = 0;
        int plinkoDelta = 0;

        if (string.Equals(milestone.RewardType, "PRODUCT_ID", StringComparison.OrdinalIgnoreCase) && milestone.ProductId.HasValue)
        {
            StoreItem? storeItem = JSONConfiguration.StoreItemsConfiguration?.GetByID(milestone.ProductId.Value);
            string itemCode = storeItem?.PrefixedCode ?? storeItem?.Code ?? $"wareffort.product.{milestone.ProductId.Value}";

            bool alreadyOwned = await _db.UserInventory
                .AnyAsync(
                    inv => inv.UserID == user.ID &&
                           (inv.ItemCode == itemCode || (storeItem != null && inv.ItemCode == storeItem.Code)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!alreadyOwned)
            {
                _db.UserInventory.Add(new UserInventory
                {
                    UserID = user.ID,
                    AccountID = account.ID,
                    ItemCode = itemCode,
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = _timeProvider.GetUtcNow(),
                    Source = "WarEffort"
                });
                grantedProductId = milestone.ProductId.Value;

                string productType = ResolveClientProductType(storeItem);
                string localContent = ResolveClientProductIcon(storeItem, milestone.ProductId.Value);
                rewardInfo["PRODUCT_ID"] = new Dictionary<string, object?>
                {
                    ["productid"] = milestone.ProductId.Value.ToString(CultureInfo.InvariantCulture),
                    ["name"] = storeItem?.Code ?? itemCode,
                    ["type"] = productType,
                    ["localcontent"] = localContent
                };
            }
            else
            {
                // Already owned: credit AltSilverFallback so the player still gets rewarded cleanly
                wasAltFallback = true;
                effectiveRewardType = "MMPOINTS";
                effectiveAmount = Math.Max(100, milestone.AltSilverFallback);
                silverDelta = effectiveAmount;
                user.SilverCoins += silverDelta;
                rewardInfo["MMPOINTS"] = effectiveAmount.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (string.Equals(milestone.RewardType, "POINTS", StringComparison.OrdinalIgnoreCase))
        {
            goldDelta = Math.Max(1, milestone.RewardAmount);
            user.GoldCoins += goldDelta;
            rewardInfo["POINTS"] = goldDelta.ToString(CultureInfo.InvariantCulture);
        }
        else if (string.Equals(milestone.RewardType, "PLINKO_TICKETS", StringComparison.OrdinalIgnoreCase))
        {
            plinkoDelta = Math.Max(1, milestone.RewardAmount);
            user.PlinkoTickets += plinkoDelta;
            rewardInfo["PLINKO_TICKETS"] = plinkoDelta.ToString(CultureInfo.InvariantCulture);
            rewardInfo["TICKETS"] = plinkoDelta.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            effectiveRewardType = "MMPOINTS";
            silverDelta = Math.Max(1, milestone.RewardAmount);
            user.SilverCoins += silverDelta;
            rewardInfo["MMPOINTS"] = silverDelta.ToString(CultureInfo.InvariantCulture);
        }

        bool txExists = await _db.EconomyTransactions
            .AnyAsync(t => t.UserID == user.ID && t.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (!txExists)
        {
            _db.EconomyTransactions.Add(new EconomyTransaction
            {
                UserID = user.ID,
                AccountID = account.ID,
                GoldCoinsDelta = goldDelta,
                SilverCoinsDelta = silverDelta,
                PlinkoTicketsDelta = plinkoDelta,
                GoldCoinsBalanceAfter = user.GoldCoins,
                SilverCoinsBalanceAfter = user.SilverCoins,
                PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                TransactionType = autoClaimedAtRollover ? "WarEffortCycleRolloverReward" : "WarEffortBagReward",
                ReferenceId = $"Cycle{cycle.CycleNumber}:Bag{milestone.BagIndex}:BagId{milestone.BagId}",
                IdempotencyKey = idempotencyKey,
                Description = $"War Effort {milestone.Title} ({effectiveRewardType} x{effectiveAmount})",
                Timestamp = _timeProvider.GetUtcNow()
            });
        }

        AccountWarEffortBagClaim claim = new()
        {
            AccountId = account.ID,
            CycleId = cycle.Id,
            BagIndex = milestone.BagIndex,
            BagId = milestone.BagId,
            ClaimedAtUtc = nowUtc,
            RewardType = effectiveRewardType,
            RewardAmount = effectiveAmount,
            GrantedProductId = grantedProductId,
            WasAltFallback = wasAltFallback,
            AutoClaimedAtRollover = autoClaimedAtRollover,
            IdempotencyKey = idempotencyKey
        };
        _db.AccountWarEffortBagClaims.Add(claim);

        return (claim, rewardInfo);
    }

    private Dictionary<string, object?> BuildPayloadFromState(
        int accountId,
        WarEffortAccountState state,
        bool includeOnlyNativeTriggers)
    {
        List<Dictionary<string, object?>> questTypes = [];
        Dictionary<string, int> typeIdByKey = new(StringComparer.Ordinal);
        List<Dictionary<string, object?>> questQueue = [];
        List<Dictionary<string, object?>> playerQuestEntries = [];
        Dictionary<string, object?> kongorCustomQuests = new();

        foreach (AccountQuestSlot slot in state.Slots)
        {
            if (!state.Definitions.TryGetValue(slot.QuestDefinitionId, out QuestDefinition? def))
            {
                continue;
            }

            if (includeOnlyNativeTriggers && !def.IsNativeEngineTrigger)
            {
                continue;
            }

            int typeId = GetOrAddQuestType(questTypes, typeIdByKey, def);
            questQueue.Add(BuildQuestQueueEntry(def, typeId));
            playerQuestEntries.Add(BuildPlayerQuestEntry(slot, def));

            // Provide rich overlay metadata for custom kongor_* quests AND typed attribute/attack/role quests
            // so quests_v2.lua always renders crisp titles, descriptions, icons, and progress.
            if (!def.IsNativeEngineTrigger || !string.IsNullOrEmpty(def.QuestTypeDef))
            {
                kongorCustomQuests[def.Id.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object?>
                {
                    ["title"] = def.Title,
                    ["description"] = def.Description,
                    ["icon"] = ResolveQuestIconPath(def),
                    ["trigger"] = def.CustomMetricKey ?? def.NativeTriggerName,
                    ["progress"] = slot.Progress,
                    ["goal"] = slot.Goal,
                    ["bag_points"] = def.BagPointsReward
                };
            }
        }

        Dictionary<int, AccountWarEffortBagClaim> claimsByBagIndex = state.Claims.ToDictionary(c => c.BagIndex);
        List<Dictionary<string, object?>> bagQueue = [];
        List<Dictionary<string, object?>> playerBagQueueEntries = [];
        List<Dictionary<string, object?>> playerBagCompleteEntries = [];

        int prevThreshold = 0;
        int totalPoints = state.Progress.TotalBagPoints;

        foreach (WarEffortBagMilestone milestone in state.Milestones.OrderBy(m => m.BagIndex))
        {
            int segmentGoal = Math.Max(1, milestone.RequiredPoints - prevThreshold);
            int segmentProgress = Math.Clamp(totalPoints - prevThreshold, 0, segmentGoal);
            prevThreshold = milestone.RequiredPoints;

            Dictionary<string, object?> previewInfo = BuildBagPreviewInfo(milestone);
            bagQueue.Add(new Dictionary<string, object?>
            {
                ["bagid"] = milestone.BagId,
                ["achievement"] = segmentGoal,
                ["rewardtype"] = milestone.RewardType,
                ["reward"] = milestone.RewardAmount,
                ["info"] = previewInfo
            });

            if (claimsByBagIndex.TryGetValue(milestone.BagIndex, out AccountWarEffortBagClaim? claim))
            {
                playerBagCompleteEntries.Add(new Dictionary<string, object?>
                {
                    ["bagid"] = milestone.BagId,
                    ["info"] = BuildClaimedBagInfo(claim, milestone)
                });
            }
            else if (segmentProgress > 0 || milestone.BagIndex == 1 ||
                     (milestone.BagIndex > 1 && totalPoints >= state.Milestones[milestone.BagIndex - 2].RequiredPoints))
            {
                playerBagQueueEntries.Add(new Dictionary<string, object?>
                {
                    ["bag"] = new Dictionary<string, object?>
                    {
                        ["bagid"] = milestone.BagId,
                        ["achievementstatus"] = segmentProgress
                    },
                    ["achievementstatus"] = segmentProgress
                });
            }
        }

        string acctKey = accountId.ToString(CultureInfo.InvariantCulture);

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["quest_types"] = questTypes,
            ["quest_queue"] = questQueue,
            ["player_quest_queue"] = new Dictionary<string, object?>
            {
                [acctKey] = playerQuestEntries
            },
            ["bag_queue"] = bagQueue,
            ["player_bag_queue"] = new Dictionary<string, object?>
            {
                [acctKey] = playerBagQueueEntries
            },
            ["player_bag_complete"] = playerBagCompleteEntries,
            ["quest_resets_available"] = state.Progress.RerollsAvailable,
            ["kongor_custom_quests"] = kongorCustomQuests,
            ["cycle_info"] = new Dictionary<string, object?>
            {
                ["cycle_number"] = state.Cycle.CycleNumber,
                ["name"] = state.Cycle.Name,
                ["ends_at_utc"] = state.Cycle.EndsAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["total_points"] = state.Progress.TotalBagPoints,
                ["quests_completed"] = state.Progress.QuestsCompletedCount
            }
        };
    }

    private static int GetOrAddQuestType(
        List<Dictionary<string, object?>> questTypes,
        Dictionary<string, int> typeIdByKey,
        QuestDefinition def)
    {
        // Use simple empty questtypedef for custom overlays so C++ never demands extra sub-tables
        string effectiveTypeDef = def.IsNativeEngineTrigger ? def.QuestTypeDef : string.Empty;
        string effectiveTrigger = string.IsNullOrWhiteSpace(def.NativeTriggerName)
            ? "achievement_value_games_won"
            : def.NativeTriggerName;

        string key = $"{effectiveTypeDef}|{effectiveTrigger}";
        if (typeIdByKey.TryGetValue(key, out int existingId))
        {
            return existingId;
        }

        int newId = questTypes.Count + 1;
        typeIdByKey[key] = newId;
        questTypes.Add(new Dictionary<string, object?>
        {
            ["typeid"] = newId,
            ["questtypedef"] = effectiveTypeDef,
            ["triggername"] = effectiveTrigger
        });
        return newId;
    }

    private static Dictionary<string, object?> BuildQuestQueueEntry(QuestDefinition def, int typeId)
    {
        string effectiveTrigger = string.IsNullOrWhiteSpace(def.NativeTriggerName)
            ? "achievement_value_games_won"
            : def.NativeTriggerName;

        return new Dictionary<string, object?>
        {
            ["questid"] = def.Id,
            ["typeid"] = typeId,
            ["questtypekey"] = def.IsNativeEngineTrigger ? def.QuestTypeKey : 0,
            ["questname"] = def.Title,
            ["triggername"] = effectiveTrigger,
            ["achievement"] = Math.Max(1, def.GoalValue),
            ["questreward"] = def.BagPointsReward
        };
    }

    private static Dictionary<string, object?> BuildPlayerQuestEntry(AccountQuestSlot slot, QuestDefinition def)
    {
        Dictionary<string, object?> entry = new()
        {
            ["quest"] = new Dictionary<string, object?>
            {
                ["questid"] = def.Id,
                ["achievementstatus"] = slot.Progress
            }
        };

        if (def.IsNativeEngineTrigger && !string.IsNullOrEmpty(def.QuestTypeDef))
        {
            Dictionary<string, object?> info = new();
            if (string.Equals(def.QuestTypeDef, "PRIMARY_STAT", StringComparison.OrdinalIgnoreCase))
            {
                info["PRIMARY_STAT"] = new Dictionary<string, object?>
                {
                    ["attribute"] = def.QuestTypeKey,
                    ["teamid"] = def.HeroTeamId > 0 ? def.HeroTeamId : 1
                };
            }
            else if (string.Equals(def.QuestTypeDef, "ATTACK_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                info["ATTACK_TYPE"] = new Dictionary<string, object?>
                {
                    ["attacktype"] = def.HeroAttackType ?? "melee",
                    ["teamid"] = def.HeroTeamId > 0 ? def.HeroTeamId : 1
                };
            }
            else if (string.Equals(def.QuestTypeDef, "ROLE", StringComparison.OrdinalIgnoreCase))
            {
                info["ROLE"] = new Dictionary<string, object?>
                {
                    ["roles"] = def.HeroRole ?? "support"
                };
            }
            entry["info"] = info;
        }

        return entry;
    }

    private static Dictionary<string, object?> BuildBagPreviewInfo(WarEffortBagMilestone milestone)
    {
        if (string.Equals(milestone.RewardType, "PRODUCT_ID", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["PRODUCT_ID"] = new Dictionary<string, object?>
                {
                    ["productid"] = (milestone.ProductId ?? 0).ToString(CultureInfo.InvariantCulture)
                }
            };
        }

        if (string.Equals(milestone.RewardType, "POINTS", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["POINTS"] = milestone.RewardAmount
            };
        }

        if (string.Equals(milestone.RewardType, "PLINKO_TICKETS", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["PLINKO_TICKETS"] = milestone.RewardAmount,
                ["POINTS"] = milestone.RewardAmount // Ensures non-unknown bag icon type in legacy Lua
            };
        }

        return new Dictionary<string, object?>
        {
            ["MMPOINTS"] = milestone.RewardAmount
        };
    }

    private static Dictionary<string, object?> BuildClaimedBagInfo(AccountWarEffortBagClaim claim, WarEffortBagMilestone milestone)
    {
        if (claim.GrantedProductId.HasValue)
        {
            StoreItem? item = JSONConfiguration.StoreItemsConfiguration?.GetByID(claim.GrantedProductId.Value);
            return new Dictionary<string, object?>
            {
                ["PRODUCT_ID"] = new Dictionary<string, object?>
                {
                    ["productid"] = claim.GrantedProductId.Value.ToString(CultureInfo.InvariantCulture),
                    ["name"] = item?.Code ?? $"product_{claim.GrantedProductId.Value}",
                    ["type"] = ResolveClientProductType(item),
                    ["localcontent"] = ResolveClientProductIcon(item, claim.GrantedProductId.Value)
                }
            };
        }

        if (string.Equals(claim.RewardType, "POINTS", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?> { ["POINTS"] = claim.RewardAmount };
        }

        if (string.Equals(claim.RewardType, "PLINKO_TICKETS", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["PLINKO_TICKETS"] = claim.RewardAmount,
                ["MMPOINTS"] = claim.RewardAmount
            };
        }

        return new Dictionary<string, object?> { ["MMPOINTS"] = claim.RewardAmount };
    }

    private static string ResolveQuestIconPath(QuestDefinition def)
    {
        if (!string.IsNullOrWhiteSpace(def.IconPath))
        {
            return def.IconPath;
        }

        if (string.Equals(def.NativeTriggerName, "achievement_value_attribute_played", StringComparison.OrdinalIgnoreCase))
        {
            string faction = def.HeroTeamId == 2 ? "hellbourne" : "legion";
            string attr = (def.HeroAttribute ?? "strength").ToLowerInvariant();
            return $"/ui/fe2/quests/quest_icons/{faction}_{attr}.tga";
        }

        if (string.Equals(def.NativeTriggerName, "achievement_value_attack_type_played", StringComparison.OrdinalIgnoreCase))
        {
            string faction = def.HeroTeamId == 2 ? "hellbourne" : "legion";
            string atk = (def.HeroAttackType ?? "melee").ToLowerInvariant();
            return $"/ui/fe2/quests/quest_icons/{faction}_{atk}.tga";
        }

        if (string.Equals(def.NativeTriggerName, "achievement_value_role_played", StringComparison.OrdinalIgnoreCase))
        {
            string role = (def.HeroRole ?? "support").ToLowerInvariant();
            if (string.Equals(role, "carry", StringComparison.Ordinal))
            {
                return "/ui/fe2/quests/quest_icons/role_carry_ganker.tga";
            }
            return $"/ui/fe2/quests/quest_icons/role_{role}.tga";
        }

        return "/ui/fe2/quests/quest_icons/games_won.tga";
    }

    private static string ResolveClientProductType(StoreItem? item)
    {
        if (item is null) return "Account Icon";
        return item.Type switch
        {
            6 => "Couriers",
            11 => "Ward",
            2 => "Account Icon",
            1 => "Chat Symbol",
            0 => "Chat Color",
            4 => "Alt Announcement",
            5 => "Taunt",
            3 => "Alt Avatar",
            _ => "Account Icon"
        };
    }

    private static string ResolveClientProductIcon(StoreItem? item, int productId)
    {
        if (productId == 2909) return "/ui/fe2/store/icons/ward_war_effort.tga";
        if (productId == 2910) return "/shared/automated_courier/couriers/wareffort_fly/icon.tga";
        if (productId == 2770) return "/ui/fe2/store/icons/account_icons/war_effort_winner.tga";
        if (productId == 2769) return "/ui/fe2/store/icons/account_icons/war_effort_contender.tga";
        if (!string.IsNullOrWhiteSpace(item?.Resource)) return item.Resource;
        return "/ui/fe2/store/icons/account_icons/war_effort_contender.tga";
    }

    internal static QuestDefinition? PickWeightedQuest(
        IReadOnlyList<QuestDefinition> allDefs,
        HashSet<int> excludeIds,
        int accountId,
        int cycleNumber,
        int salt,
        DateTime nowUtc)
    {
        List<QuestDefinition> candidates = allDefs
            .Where(q => q.IsEnabled && !excludeIds.Contains(q.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = allDefs.Where(q => q.IsEnabled).ToList();
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        int totalWeight = candidates.Sum(q => Math.Max(1, q.Weight));
        int seed = HashCode.Combine(accountId, cycleNumber, salt, nowUtc.DayOfYear, nowUtc.Year);
        Random rng = new(seed);
        int roll = rng.Next(0, totalWeight);

        int cursor = 0;
        foreach (QuestDefinition candidate in candidates)
        {
            cursor += Math.Max(1, candidate.Weight);
            if (roll < cursor)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private async Task<IDistributedLock?> AcquireAccountLockAsync(int accountId, CancellationToken cancellationToken)
    {
        if (_distributedLockService is null)
        {
            return null;
        }

        try
        {
            return await _distributedLockService
                .AcquireLockAsync($"wareffort:lock:{accountId}", LockExpiry, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WAR_EFFORT] Distributed lock failed for AccountId={AccountId}; proceeding without distributed lock.", accountId);
            return null;
        }
    }
}
