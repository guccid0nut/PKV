// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Enumerations;
using ASPIRE.Common.Enumerations.Statistics;
using KONGOR.MasterServer.Configuration;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using ConfigStoreItem = KONGOR.MasterServer.Configuration.Store.StoreItem;

namespace KONGOR.MasterServer.Services.Plinko;

/// <summary>
///     Database-backed Codex Graffiti showcase, pose persistence, roulette lottery, and ladder standing queries.
/// </summary>
public sealed class CodexGraffitiService(
    MerrickContext context,
    IAccountRepository accountRepository,
    ILogger<CodexGraffitiService> logger) : ICodexGraffitiService
{
    public const int MaxAvatarsPerWall = 7;
    public const int MaxLotteryGiftCount = 68;
    public const int DefaultBoostPrice = 150;
    public const string PoseRewardType = "CodexPose";
    public const string LotteryRewardType = "CodexLottery";
    public const string WallPurchaseRewardType = "CodexWall";
    public const string CodexRewardSource = "CodexGraffiti";

    private static readonly string[] MultiLevelPunkAvatars =
    [
        "Hero_Pyromancer.Alt8",
        "Hero_Valkyrie.Alt12",
        "Hero_DwarfMagi.Alt6",
        "Hero_Hiro.Alt12",
        "Hero_Nomad.Alt12",
        "Hero_PollywogPriest.Alt7",
        "Hero_DoctorRepulsor.Alt10"
    ];

    private static readonly string[] OneLevelPunkAvatars =
    [
        "Hero_Ravenor.Alt8",
        "Hero_Hammerstorm.Alt11",
        "Hero_FlintBeastwood.Alt13",
        "Hero_Chronos.Alt9",
        "Hero_Rampage.Alt14"
    ];

    private static readonly HashSet<string> MultiLevelPunkSet =
        new(MultiLevelPunkAvatars, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> OneLevelPunkSet =
        new(OneLevelPunkAvatars, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<int> Plinko80GiftIds = [19, 33, 60, 68];
    private static readonly HashSet<int> Plinko120GiftIds = [24, 48, 53];

    private static readonly Dictionary<int, string> LotteryAvatarGiftCodes = new()
    {
        [2] = "Hero_Hiro.Alt12",
        [16] = "Hero_Valkyrie.Alt12",
        [26] = "Hero_Nomad.Alt12",
        [27] = "Hero_DwarfMagi.Alt6",
        [40] = "Hero_Pyromancer.Alt8",
        [55] = "Hero_DoctorRepulsor.Alt10",
        [62] = "Hero_PollywogPriest.Alt7"
    };

    public async Task<GraffitiWallListResult> BuildWallListAsync(
        Account? account,
        string reqType,
        string pageRaw,
        CancellationToken cancellationToken = default)
    {
        int accountId = account?.ID ?? 0;
        string accountName = account?.Name ?? "Player";

        List<string> ownedAltAvatars = account != null
            ? await ResolveOwnedAltAvatarsAsync(account, cancellationToken).ConfigureAwait(false)
            : [];
        int ownedPunkCount = ownedAltAvatars.Count;

        // Always showcase all 12 official Punk Collection (Set 61) avatars across 2 walls (7 + 5):
        // player-owned Set 61 avatars first (MultiLevel then OneLevel), followed by remaining official Set 61 avatars.
        List<string> wallAvatars = new(MultiLevelPunkAvatars.Length + OneLevelPunkAvatars.Length);
        HashSet<string> seenWallAvatars = new(StringComparer.OrdinalIgnoreCase);

        foreach (string ownedPunk in ownedAltAvatars)
        {
            if (seenWallAvatars.Add(ownedPunk))
            {
                wallAvatars.Add(ownedPunk);
            }
        }

        foreach (string punkCode in MultiLevelPunkAvatars)
        {
            if (seenWallAvatars.Add(punkCode))
            {
                wallAvatars.Add(punkCode);
            }
        }

        foreach (string punkCode in OneLevelPunkAvatars)
        {
            if (seenWallAvatars.Add(punkCode))
            {
                wallAvatars.Add(punkCode);
            }
        }

        int showcaseFilledCount = Math.Max(0, wallAvatars.Count - ownedPunkCount);
        bool usingStarterShowcase = showcaseFilledCount > 0;

        Dictionary<int, GraffitiPoseOverride> poseOverrides = account != null
            ? await LoadPoseOverridesAsync(account, cancellationToken).ConfigureAwait(false)
            : [];

        List<string> killRibbons = account != null
            ? await ResolveKillStreakRibbonsAsync(account.ID, cancellationToken).ConfigureAwait(false)
            : [];

        int rankLevel = account != null
            ? await ComputeAccountGraffitiRankAsync(account.ID, ownedPunkCount, cancellationToken).ConfigureAwait(false)
            : 0;

        string selectedUpgrades = ResolveSelectedUpgradesCsv(account);

        int totalWalls = Math.Max(1, (int)Math.Ceiling(wallAvatars.Count / (double)MaxAvatarsPerWall));
        int selectedPage = 1;
        if (int.TryParse(pageRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPage))
        {
            selectedPage = Math.Clamp(parsedPage, 1, totalWalls);
        }

        List<Dictionary<string, object>> allWalls = new(totalWalls);
        for (int wallIdx = 1; wallIdx <= totalWalls; wallIdx++)
        {
            int startSlot = (wallIdx - 1) * MaxAvatarsPerWall;
            int slotCount = Math.Min(MaxAvatarsPerWall, wallAvatars.Count - startSlot);
            List<Dictionary<string, object>> infos = new(Math.Max(0, slotCount));

            for (int slotOffset = 0; slotOffset < slotCount; slotOffset++)
            {
                int graffitiId = startSlot + slotOffset + 1;
                string avatarCode = wallAvatars[startSlot + slotOffset];
                int avatarLevel = MultiLevelPunkSet.Contains(avatarCode) ? 5 : 1;

                poseOverrides.TryGetValue(graffitiId, out GraffitiPoseOverride? pose);
                double rot = pose?.Rot ?? 0.0;
                double frame = pose?.Frame ?? 0.5;
                int ani = pose?.Ani ?? 1;
                string killRibbon = (graffitiId - 1) < killRibbons.Count ? killRibbons[graffitiId - 1] : string.Empty;

                Dictionary<string, object> avatarLayout = new()
                {
                    ["name"] = avatarCode,
                    ["rot"] = rot,
                    ["frame"] = frame,
                    ["level"] = avatarLevel,
                    ["ani"] = ani
                };

                Dictionary<string, object> layoutInfo = new()
                {
                    ["kill"] = killRibbon,
                    ["avatar"] = avatarLayout
                };

                infos.Add(new Dictionary<string, object>
                {
                    ["graffiti_id"] = graffitiId,
                    ["buddy_nickname"] = string.Empty,
                    ["layout_info"] = layoutInfo
                });
            }

            allWalls.Add(new Dictionary<string, object>
            {
                ["wall_id"] = wallIdx,
                ["wall_index"] = wallIdx,
                ["show_level"] = ((wallIdx - 1) % 3) + 1,
                ["infos"] = infos
            });
        }

        List<Dictionary<string, object>> responseWalls = string.Equals(reqType, "pageturning", StringComparison.OrdinalIgnoreCase)
            ? [allWalls[selectedPage - 1]]
            : allWalls;

        int paintedToday = Math.Min(MaxAvatarsPerWall, wallAvatars.Count);

        return new GraffitiWallListResult(
            accountId,
            accountName,
            selectedUpgrades,
            rankLevel,
            ownedPunkCount,
            wallAvatars.Count,
            showcaseFilledCount,
            usingStarterShowcase,
            totalWalls,
            selectedPage,
            paintedToday,
            poseOverrides.Count,
            responseWalls);
    }

    public async Task<GraffitiPoseUpdateResult> UpdatePoseOverridesAsync(
        Account account,
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        int updatedCount = 0;

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    List<UserWebReward> existingPoses = await context.UserWebRewards
                        .Where(r => (r.AccountID == account.ID || r.UserID == account.UserID) && r.RewardType == PoseRewardType)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    Dictionary<string, UserWebReward> existingByCode = existingPoses
                        .GroupBy(r => r.RewardCode, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    DateTimeOffset now = TimeProvider.System.GetUtcNow();

                    foreach (JsonElement elem in doc.RootElement.EnumerateArray())
                    {
                        int graffitiId = ReadJsonInt(elem, "graffiti_id", 0);
                        if (graffitiId <= 0) continue;

                        double rot = Math.Clamp(ReadJsonDouble(elem, "rot", 0.0), -360.0, 360.0);
                        double frame = Math.Clamp(ReadJsonDouble(elem, "frame", 0.5), 0.0, 1.0);
                        int ani = Math.Clamp(ReadJsonInt(elem, "ani", 1), 1, 10);

                        string rewardCode = $"graffiti_{graffitiId.ToString(CultureInfo.InvariantCulture)}";
                        string metadataJson = JsonSerializer.Serialize(new GraffitiPoseOverride(rot, frame, ani));

                        if (existingByCode.TryGetValue(rewardCode, out UserWebReward? existing))
                        {
                            existing.AccountID = account.ID;
                            existing.MetadataJson = metadataJson;
                            existing.TimestampAcquired = now;
                        }
                        else
                        {
                            UserWebReward created = new()
                            {
                                UserID = account.UserID,
                                AccountID = account.ID,
                                RewardType = PoseRewardType,
                                RewardCode = rewardCode,
                                Name = $"Graffiti Slot #{graffitiId.ToString(CultureInfo.InvariantCulture)} Pose",
                                Source = CodexRewardSource,
                                TimestampAcquired = now,
                                MetadataJson = metadataJson
                            };
                            context.UserWebRewards.Add(created);
                            existingByCode[rewardCode] = created;
                        }

                        updatedCount++;
                    }

                    if (updatedCount > 0)
                    {
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "[CodexGraffiti] Invalid JSON payload in updategraffitiinfo for AccountId={AccountId}", account.ID);
                return new GraffitiPoseUpdateResult(false, "Invalid pose payload.", 0);
            }
        }

        return new GraffitiPoseUpdateResult(true, null, updatedCount);
    }

    public async Task<GraffitiLotteryListResult> GetLotteryListAsync(
        Account? account,
        CancellationToken cancellationToken = default)
    {
        HashSet<int> claimedGifts = account != null
            ? await LoadClaimedLotteryGiftsAsync(account, cancellationToken).ConfigureAwait(false)
            : [];

        List<string> ownedAvatars = account != null
            ? await ResolveOwnedAltAvatarsAsync(account, cancellationToken).ConfigureAwait(false)
            : [];
        HashSet<string> ownedAvatarSet = new(ownedAvatars, StringComparer.OrdinalIgnoreCase);

        List<Dictionary<string, object>> lotteryList = new(MaxLotteryGiftCount);
        for (int giftId = 1; giftId <= MaxLotteryGiftCount; giftId++)
        {
            bool isOwned = claimedGifts.Contains(giftId) ||
                           (LotteryAvatarGiftCodes.TryGetValue(giftId, out string? avatarCode) && ownedAvatarSet.Contains(avatarCode));
            lotteryList.Add(new Dictionary<string, object>
            {
                ["gift_id"] = giftId,
                ["owned"] = isOwned ? 1 : 0
            });
        }

        int earnedDraws = Math.Max(1, ownedAvatars.Count / MaxAvatarsPerWall + 1);
        int leftNum = account != null ? Math.Max(1, earnedDraws - claimedGifts.Count) : 0;

        return new GraffitiLotteryListResult(claimedGifts.Count, leftNum, lotteryList);
    }

    public async Task<GraffitiLotteryRollResult> RollLotteryAsync(
        Account account,
        int requestedGiftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        HashSet<int> claimedGifts = await LoadClaimedLotteryGiftsAsync(account, cancellationToken).ConfigureAwait(false);
        List<int> availableGifts = [];
        for (int i = 1; i <= MaxLotteryGiftCount; i++)
        {
            if (!claimedGifts.Contains(i))
            {
                availableGifts.Add(i);
            }
        }

        bool alreadyOwned;
        int chosenGiftId;
        if (requestedGiftId is >= 1 and <= MaxLotteryGiftCount && !claimedGifts.Contains(requestedGiftId))
        {
            alreadyOwned = false;
            chosenGiftId = requestedGiftId;
        }
        else
        {
            alreadyOwned = availableGifts.Count == 0;
            chosenGiftId = alreadyOwned
                ? Random.Shared.Next(1, MaxLotteryGiftCount + 1)
                : availableGifts[Random.Shared.Next(availableGifts.Count)];
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        context.UserWebRewards.Add(new UserWebReward
        {
            UserID = account.UserID,
            AccountID = account.ID,
            RewardType = LotteryRewardType,
            RewardCode = $"gift_{chosenGiftId.ToString(CultureInfo.InvariantCulture)}",
            Name = $"Codex Roulette Gift #{chosenGiftId.ToString(CultureInfo.InvariantCulture)}",
            Source = CodexRewardSource,
            TimestampAcquired = now
        });

        if (account.User != null)
        {
            if (Plinko80GiftIds.Contains(chosenGiftId))
            {
                account.User.PlinkoTickets += 80;
                await accountRepository.UpdateUserAsync(account.User, cancellationToken).ConfigureAwait(false);
            }
            else if (Plinko120GiftIds.Contains(chosenGiftId))
            {
                account.User.PlinkoTickets += 120;
                await accountRepository.UpdateUserAsync(account.User, cancellationToken).ConfigureAwait(false);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new GraffitiLotteryRollResult(chosenGiftId, alreadyOwned);
    }

    public async Task<List<Dictionary<string, object>>> GetLotteryHistoryAsync(
        Account? account,
        CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, object>> history = [];

        if (account != null)
        {
            List<UserWebReward> rows = await context.UserWebRewards
                .AsNoTracking()
                .Where(r => (r.AccountID == account.ID || r.UserID == account.UserID) && r.RewardType == LotteryRewardType)
                .OrderByDescending(r => r.TimestampAcquired)
                .Take(50)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (UserWebReward row in rows)
            {
                int giftId = ParseTrailingInt(row.RewardCode, "gift_");
                if (giftId is >= 1 and <= MaxLotteryGiftCount)
                {
                    history.Add(new Dictionary<string, object>
                    {
                        ["create_time"] = row.TimestampAcquired.ToUnixTimeSeconds(),
                        ["gift_id"] = giftId
                    });
                }
            }
        }

        return history;
    }

    public Task<List<GraffitiLadderStanding>> GetLadderAsync(
        int maxEntries,
        CancellationToken cancellationToken = default) =>
        QueryGraffitiStandingsAsync(context, maxEntries, cancellationToken);

    public async Task RecordWallPurchaseAsync(
        Account account,
        string wallId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        context.UserWebRewards.Add(new UserWebReward
        {
            UserID = account.UserID,
            AccountID = account.ID,
            RewardType = WallPurchaseRewardType,
            RewardCode = $"wall_{wallId}",
            Name = $"Graffiti Wall #{wallId}",
            Source = CodexRewardSource,
            TimestampAcquired = TimeProvider.System.GetUtcNow()
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraffitiCommunityStatsResult> GetCommunityGoalStatsAsync(
        CancellationToken cancellationToken = default)
    {
        int inventoryCount = await context.UserInventory.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        double progress = Math.Clamp(Math.Max(0.25, inventoryCount / 500.0), 0.0, 1.0);
        return new GraffitiCommunityStatsResult(inventoryCount, progress);
    }

    public static async Task<List<GraffitiLadderStanding>> QueryGraffitiStandingsAsync(
        MerrickContext dbContext,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        List<Account> candidateAccounts = await dbContext.Accounts
            .AsNoTracking()
            .Include(a => a.Clan)
            .Where(a => a.Type != AccountType.ServerHost)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasRealAccounts = candidateAccounts.Any(a => !IsSyntheticSeedAccountName(a.Name));
        if (hasRealAccounts)
        {
            candidateAccounts = candidateAccounts
                .Where(a => !IsSyntheticSeedAccountName(a.Name))
                .ToList();
        }

        List<UserInventory> allInventoryRows = await dbContext.UserInventory
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AccountStatistics> allStats = await dbContext.AccountStatistics
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, (int Wins, int Losses)> statsByAccountId = allStats
            .GroupBy(s => s.AccountID)
            .ToDictionary(
                g => g.Key,
                g => (
                    Wins: g.Sum(s => Math.Max(0, s.MatchesWon)),
                    Losses: g.Sum(s => Math.Max(0, s.MatchesLost))));

        HashSet<int> candidateAccountIds = candidateAccounts.Select(a => a.ID).ToHashSet();
        Dictionary<int, int> primaryAccountByUserId = candidateAccounts
            .Where(a => a.UserID > 0)
            .GroupBy(a => a.UserID)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.IsMain).ThenBy(a => a.ID).First().ID);

        Dictionary<int, HashSet<string>> byAccountId = [];

        foreach (UserInventory inv in allInventoryRows)
        {
            string? normalized = NormalizeAltAvatarCode(inv.ItemCode);
            if (string.IsNullOrEmpty(normalized) || !IsOfficialPunkSetAvatar(normalized)) continue;

            int targetAccountId = 0;
            if (inv.AccountID is > 0 && candidateAccountIds.Contains(inv.AccountID.Value))
            {
                targetAccountId = inv.AccountID.Value;
            }
            else if (inv.UserID > 0 && primaryAccountByUserId.TryGetValue(inv.UserID, out int primaryAccId))
            {
                targetAccountId = primaryAccId;
            }

            if (targetAccountId > 0)
            {
                if (!byAccountId.TryGetValue(targetAccountId, out HashSet<string>? accSet))
                {
                    accSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byAccountId[targetAccountId] = accSet;
                }
                accSet.Add(normalized);
            }
        }

        List<GraffitiLadderStanding> ranked = new(candidateAccounts.Count);
        foreach (Account acc in candidateAccounts)
        {
            int avatarsTagged = byAccountId.TryGetValue(acc.ID, out HashSet<string>? aSet) ? aSet.Count : 0;
            int wallsCompleted = avatarsTagged > 0
                ? (int)Math.Ceiling(avatarsTagged / (double)MaxAvatarsPerWall)
                : 0;

            statsByAccountId.TryGetValue(acc.ID, out (int Wins, int Losses) stat);
            int wins = stat.Wins;
            int losses = stat.Losses;

            string displayName = !string.IsNullOrWhiteSpace(acc.Clan?.Tag)
                ? $"[{acc.Clan.Tag}] {acc.Name}"
                : acc.Name;

            ranked.Add(new GraffitiLadderStanding(
                acc.ID,
                acc.Name,
                displayName,
                wallsCompleted,
                avatarsTagged,
                wins,
                losses));
        }

        return ranked
            .OrderByDescending(r => r.AvatarsTagged)
            .ThenByDescending(r => r.Wins)
            .ThenBy(r => r.AccountId)
            .Take(Math.Max(1, maxEntries))
            .ToList();
    }

    public static bool IsSyntheticSeedAccountName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string trimmed = name.Trim();
        return trimmed.StartsWith("GUEST-", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("[PK] GUEST-", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("TEST_USER_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOfficialPunkSetAvatar(string? avatarCode) =>
        !string.IsNullOrWhiteSpace(avatarCode) &&
        (MultiLevelPunkSet.Contains(avatarCode) || OneLevelPunkSet.Contains(avatarCode));

    public static string? NormalizeAltAvatarCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode)) return null;
        string trimmed = rawCode.Trim();

        try
        {
            var storeConfig = JSONConfiguration.StoreItemsConfiguration;
            if (storeConfig != null)
            {
                ConfigStoreItem? item = storeConfig.GetByPrefixedCode(trimmed)
                                        ?? storeConfig.GetByCode(trimmed)
                                        ?? storeConfig.GetByName(trimmed);
                if (item is null && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
                {
                    item = storeConfig.GetByID(numericId);
                }

                if (item != null)
                {
                    if (item.StoreItemType != StoreItemType.AlternativeAvatar) return null;
                    trimmed = item.Code.Trim();
                }
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("[CODEX_GRAFFITI] StoreItemsConfiguration unavailable during code normalization; falling back to prefix parsing.");
        }

        if (trimmed.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..];
        }

        if (!trimmed.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int dotIdx = trimmed.IndexOf('.');
        if (dotIdx <= 5 || dotIdx >= trimmed.Length - 1)
        {
            return null;
        }

        string suffix = trimmed[(dotIdx + 1)..];
        if (string.Equals(suffix, "Hero", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private async Task<List<string>> ResolveOwnedAltAvatarsAsync(Account account, CancellationToken cancellationToken)
    {
        HashSet<string> rawCodes = new(StringComparer.OrdinalIgnoreCase);
        if (account.User?.InventoryItems != null)
        {
            foreach (UserInventory inv in account.User.InventoryItems)
            {
                if (!string.IsNullOrWhiteSpace(inv.ItemCode))
                {
                    rawCodes.Add(inv.ItemCode.Trim());
                }
            }
        }

        int userId = account.UserID;
        int accountId = account.ID;
        List<string> dbCodes = await context.UserInventory
            .AsNoTracking()
            .Where(i => (userId > 0 && i.UserID == userId) || (accountId > 0 && i.AccountID == accountId))
            .Select(i => i.ItemCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (string code in dbCodes)
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                rawCodes.Add(code.Trim());
            }
        }

        HashSet<string> normalizedSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in rawCodes)
        {
            string? normalized = NormalizeAltAvatarCode(raw);
            if (!string.IsNullOrEmpty(normalized))
            {
                normalizedSet.Add(normalized);
            }
        }

        List<string> ordered = [];
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

        foreach (string punkCode in MultiLevelPunkAvatars)
        {
            if (normalizedSet.Contains(punkCode) && emitted.Add(punkCode))
            {
                ordered.Add(punkCode);
            }
        }

        foreach (string punkCode in OneLevelPunkAvatars)
        {
            if (normalizedSet.Contains(punkCode) && emitted.Add(punkCode))
            {
                ordered.Add(punkCode);
            }
        }

        int excludedNonPunkCount = Math.Max(0, normalizedSet.Count - ordered.Count);
        logger.LogInformation(
            "[CODEX_GRAFFITI] ResolveOwnedAltAvatarsAsync AccountId={AccountId} PunkSetMatched={PunkCount}/12 ExcludedNonPunk={ExcludedCount} TotalNormalizedAltAvatars={TotalNormalized}",
            account.ID,
            ordered.Count,
            excludedNonPunkCount,
            normalizedSet.Count);

        return ordered;
    }

    private async Task<Dictionary<int, GraffitiPoseOverride>> LoadPoseOverridesAsync(
        Account account,
        CancellationToken cancellationToken)
    {
        List<UserWebReward> rows = await context.UserWebRewards
            .AsNoTracking()
            .Where(r => (r.AccountID == account.ID || r.UserID == account.UserID) && r.RewardType == PoseRewardType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, GraffitiPoseOverride> map = [];
        foreach (UserWebReward row in rows)
        {
            int slotId = ParseTrailingInt(row.RewardCode, "graffiti_");
            if (slotId <= 0 || string.IsNullOrWhiteSpace(row.MetadataJson)) continue;

            try
            {
                GraffitiPoseOverride? parsed = JsonSerializer.Deserialize<GraffitiPoseOverride>(row.MetadataJson);
                if (parsed != null)
                {
                    map[slotId] = parsed;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "[CODEX_GRAFFITI] Ignoring malformed legacy pose metadata for slot {SlotId}", slotId);
            }
        }

        return map;
    }

    private async Task<HashSet<int>> LoadClaimedLotteryGiftsAsync(Account account, CancellationToken cancellationToken)
    {
        List<string> codes = await context.UserWebRewards
            .AsNoTracking()
            .Where(r => (r.AccountID == account.ID || r.UserID == account.UserID) && r.RewardType == LotteryRewardType)
            .Select(r => r.RewardCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> set = [];
        foreach (string code in codes)
        {
            int id = ParseTrailingInt(code, "gift_");
            if (id is >= 1 and <= MaxLotteryGiftCount)
            {
                set.Add(id);
            }
        }

        return set;
    }

    private async Task<List<string>> ResolveKillStreakRibbonsAsync(int accountId, CancellationToken cancellationToken)
    {
        List<AccountStatistics> stats = await context.AccountStatistics
            .AsNoTracking()
            .Where(s => s.AccountID == accountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int annihilations = stats.Sum(s => s.AwardMostAnnihilations ?? 0);
        int quadKills = stats.Sum(s => s.AwardMostQuadKills ?? 0);
        int mostKills = stats.Sum(s => s.AwardMostKills ?? 0);
        int smackdowns = stats.Sum(s => s.AwardMostSmackdowns ?? 0);
        int totalWins = stats.Sum(s => Math.Max(0, s.MatchesWon));

        List<string> ribbons = [];
        if (annihilations > 0) ribbons.Add("Annihilation");
        if (mostKills >= 5 || totalWins >= 25) ribbons.Add("Immortal");
        if (quadKills > 0) ribbons.Add("Quad Kill");
        if (smackdowns > 0 || totalWins >= 10) ribbons.Add("Bloodbath");
        if (totalWins >= 3) ribbons.Add("Hat-Trick");
        return ribbons;
    }

    private async Task<int> ComputeAccountGraffitiRankAsync(
        int accountId,
        int fallbackAvatarCount,
        CancellationToken cancellationToken)
    {
        List<GraffitiLadderStanding> standings = await QueryGraffitiStandingsAsync(context, 100, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < standings.Count; i++)
        {
            if (standings[i].AccountId == accountId)
            {
                return i + 1;
            }
        }

        return fallbackAvatarCount > 0 ? Math.Max(1, standings.Count + 1) : 0;
    }

    private static string ResolveSelectedUpgradesCsv(Account? account)
    {
        if (account?.SelectedStoreItems is not { Count: > 0 }) return string.Empty;

        List<string> tokens = [];
        foreach (AccountSelectedStoreItem sel in account.SelectedStoreItems)
        {
            if (string.IsNullOrWhiteSpace(sel.ItemCode)) continue;
            string code = sel.ItemCode.Trim();
            try
            {
                var storeConfig = JSONConfiguration.StoreItemsConfiguration;
                ConfigStoreItem? item = storeConfig?.GetByPrefixedCode(code) ?? storeConfig?.GetByCode(code);
                if (item != null)
                {
                    tokens.Add(item.ID.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[CODEX_GRAFFITI] StoreItemsConfiguration unavailable during upgrade CSV resolution; keeping raw token.");
            }

            tokens.Add(code);
        }

        return string.Join(",", tokens);
    }

    private static int ParseTrailingInt(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    private static int ReadJsonInt(JsonElement elem, string propName, int fallback)
    {
        if (!elem.TryGetProperty(propName, out JsonElement prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int num)) return num;
        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static double ReadJsonDouble(JsonElement elem, string propName, double fallback)
    {
        if (!elem.TryGetProperty(propName, out JsonElement prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double num)) return num;
        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
