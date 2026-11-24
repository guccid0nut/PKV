// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using KONGOR.MasterServer.Services.Matches;
using MERRICK.DatabaseContext.Entities.Quests;

namespace KONGOR.MasterServer.Services.Quests;

public sealed record WarEffortAccountState(
    WarEffortCycle Cycle,
    AccountWarEffortProgress Progress,
    IReadOnlyList<AccountQuestSlot> Slots,
    IReadOnlyList<WarEffortBagMilestone> Milestones,
    IReadOnlyList<AccountWarEffortBagClaim> Claims,
    IReadOnlyDictionary<int, QuestDefinition> Definitions);

public sealed record WarEffortLeaderboardEntry(
    int Rank,
    int AccountId,
    string AccountName,
    string? ClanTag,
    int TotalBagPoints,
    int QuestsCompletedCount);

public interface IWarEffortService
{
    bool IsEnabled { get; }

    Task<WarEffortAccountState> EnsureActiveCycleAndAccountStateAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object?>> BuildClientPayloadAsync(
        int accountId,
        bool includeOnlyNativeTriggers = false,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object?>> BuildDedicatedServerPlayerQuestsPayloadAsync(
        IReadOnlyCollection<int> accountIds,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object?>> ResetQuestAsync(
        int accountId,
        int questId,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object?>> OpenBagAsync(
        int accountId,
        int bagId,
        CancellationToken cancellationToken = default);

    Task<(int QuestsCompleted, int QuestsReset, int TotalBagPoints)> GetQuestProfileStatsAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WarEffortLeaderboardEntry> Entries, int TotalEntries, int CycleNumber)> GetLeaderboardAsync(
        int page,
        int pageSize,
        int? cycleNumber = null,
        CancellationToken cancellationToken = default);
}

public interface IWarEffortProgressionService
{
    Task ProcessMatchProgressionAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default);
}
