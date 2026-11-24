// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;

namespace KONGOR.MasterServer.Services.Plinko;

/// <summary>
///     Database-backed domain operations for Codex Graffiti walls, poses, lottery, and standings.
/// </summary>
public interface ICodexGraffitiService
{
    Task<GraffitiWallListResult> BuildWallListAsync(
        Account? account,
        string reqType,
        string pageRaw,
        CancellationToken cancellationToken = default);

    Task<GraffitiPoseUpdateResult> UpdatePoseOverridesAsync(
        Account account,
        string rawJson,
        CancellationToken cancellationToken = default);

    Task<GraffitiLotteryListResult> GetLotteryListAsync(
        Account? account,
        CancellationToken cancellationToken = default);

    Task<GraffitiLotteryRollResult> RollLotteryAsync(
        Account account,
        int requestedGiftId,
        CancellationToken cancellationToken = default);

    Task<List<Dictionary<string, object>>> GetLotteryHistoryAsync(
        Account? account,
        CancellationToken cancellationToken = default);

    Task<List<GraffitiLadderStanding>> GetLadderAsync(
        int maxEntries,
        CancellationToken cancellationToken = default);

    Task RecordWallPurchaseAsync(
        Account account,
        string wallId,
        CancellationToken cancellationToken = default);

    Task<GraffitiCommunityStatsResult> GetCommunityGoalStatsAsync(
        CancellationToken cancellationToken = default);
}

public sealed record GraffitiPoseOverride(double Rot, double Frame, int Ani);

public sealed record GraffitiLadderStanding(
    int AccountId,
    string AccountName,
    string DisplayName,
    int WallsCompleted,
    int AvatarsTagged,
    int Wins,
    int Losses);

public sealed record GraffitiWallListResult(
    int AccountId,
    string AccountName,
    string SelectedUpgrades,
    int RankLevel,
    int OwnedPunkCount,
    int WallAvatarCount,
    int ShowcaseFilledCount,
    bool UsingStarterShowcase,
    int TotalWalls,
    int SelectedPage,
    int PaintedToday,
    int PoseOverrideCount,
    List<Dictionary<string, object>> ResponseWalls);

public sealed record GraffitiPoseUpdateResult(
    bool Success,
    string? Error,
    int UpdatedCount);

public sealed record GraffitiLotteryListResult(
    int ClaimedCount,
    int LeftNum,
    List<Dictionary<string, object>> LotteryList);

public sealed record GraffitiLotteryRollResult(
    int ChosenGiftId,
    bool AlreadyOwned);

public sealed record GraffitiCommunityStatsResult(
    int InventoryCount,
    double Progress);
