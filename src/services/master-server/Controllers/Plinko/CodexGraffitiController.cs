// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using KONGOR.MasterServer.Services.Plinko;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Core;

namespace KONGOR.MasterServer.Controllers.Plinko;

[ApiController]
[Route("master/eventgraffiti")]
[Consumes("application/x-www-form-urlencoded")]
public class CodexGraffitiController(
    ICodexGraffitiService graffitiService,
    ISessionContextResolver sessionResolver,
    ILogger<CodexGraffitiController> logger) : ControllerBase
{
    public const int MaxAvatarsPerWall = CodexGraffitiService.MaxAvatarsPerWall;
    public const int MaxLotteryGiftCount = CodexGraffitiService.MaxLotteryGiftCount;
    public const int DefaultBoostPrice = CodexGraffitiService.DefaultBoostPrice;
    public const string PoseRewardType = CodexGraffitiService.PoseRewardType;
    public const string LotteryRewardType = CodexGraffitiService.LotteryRewardType;
    public const string WallPurchaseRewardType = CodexGraffitiService.WallPurchaseRewardType;
    public const string CodexRewardSource = CodexGraffitiService.CodexRewardSource;

    [HttpPost("getgraffitiwalllist")]
    public async Task<IActionResult> GetGraffitiWallList(CancellationToken cancellationToken = default)
    {
        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        string reqType = ExtractFormString(form, "req_type", "open");
        string pageRaw = ExtractFormString(form, "page", string.Empty);

        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        GraffitiWallListResult wallResult = await graffitiService
            .BuildWallListAsync(account, reqType, pageRaw, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[CODEX_GRAFFITI] getgraffitiwalllist AccountId={AccountId} ReqType={ReqType} OwnedPunkAvatars={OwnedPunkCount}/12 TotalWallAvatars={WallAvatarCount} ShowcaseFilled={ShowcaseFilled} StarterShowcase={StarterShowcase} Walls={TotalWalls} SelectedPage={SelectedPage} ReturnedWalls={ReturnedWalls} PaintedToday={PaintedToday} PoseOverrides={PoseCount}",
            wallResult.AccountId,
            reqType,
            wallResult.OwnedPunkCount,
            wallResult.WallAvatarCount,
            wallResult.ShowcaseFilledCount,
            wallResult.UsingStarterShowcase,
            wallResult.TotalWalls,
            wallResult.SelectedPage,
            wallResult.ResponseWalls.Count,
            wallResult.PaintedToday,
            wallResult.PoseOverrideCount);

        Dictionary<string, object> response = new()
        {
            ["success"] = true,
            ["req_type"] = reqType,
            ["popup"] = 1,
            ["account"] = new Dictionary<string, object>
            {
                ["account_id"] = wallResult.AccountId,
                ["nickname"] = wallResult.AccountName
            },
            ["selected_upgrades"] = wallResult.SelectedUpgrades,
            ["graffiti_count_today"] = wallResult.PaintedToday,
            ["graffiti_daily_limit"] = MaxAvatarsPerWall,
            ["rank_level"] = wallResult.RankLevel,
            ["share_count_today"] = 0,
            ["share_reward_left_num"] = 1,
            ["can_boost"] = false,
            ["boost_price"] = DefaultBoostPrice,
            ["page_count"] = wallResult.TotalWalls,
            ["page"] = wallResult.SelectedPage,
            ["data"] = wallResult.ResponseWalls
        };

        return new JsonResult(response);
    }

    [HttpPost("updategraffitiinfo")]
    public async Task<IActionResult> UpdateGraffitiInfo(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning("[CodexGraffiti] Rejected unauthenticated updategraffitiinfo request.");
            return new JsonResult(new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Authentication required."
            });
        }

        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        string rawJson = ExtractFormString(form, "data", string.Empty);

        GraffitiPoseUpdateResult result = await graffitiService
            .UpdatePoseOverridesAsync(account, rawJson, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new JsonResult(new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = result.Error ?? "Invalid pose payload."
            });
        }

        logger.LogInformation(
            "[CodexGraffiti] updategraffitiinfo AccountId={AccountId} UpdatedSlots={UpdatedCount}",
            account.ID,
            result.UpdatedCount);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["updated"] = result.UpdatedCount
        });
    }

    [HttpPost("getlotterylist")]
    public async Task<IActionResult> GetLotteryList(CancellationToken cancellationToken = default)
    {
        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        string reqType = ExtractFormString(form, "req_type", "normal");

        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        GraffitiLotteryListResult listResult = await graffitiService
            .GetLotteryListAsync(account, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[CodexGraffiti] getlotterylist AccountId={AccountId} ReqType={ReqType} Claimed={ClaimedCount} LeftNum={LeftNum}",
            account?.ID ?? 0,
            reqType,
            listResult.ClaimedCount,
            listResult.LeftNum);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = new Dictionary<string, object>
            {
                ["req_type"] = reqType,
                ["boost_price"] = DefaultBoostPrice,
                ["left_num"] = listResult.LeftNum,
                ["lottery_list"] = listResult.LotteryList
            }
        });
    }

    [HttpPost("lottery")]
    public async Task<IActionResult> RollLottery(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning("[CodexGraffiti] Rejected unauthenticated lottery roll request.");
            return new JsonResult(new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Authentication required."
            });
        }

        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        int requestedGiftId = int.TryParse(
            ExtractFormString(form, "gift_id", "0"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsedGiftId)
            ? parsedGiftId
            : 0;

        GraffitiLotteryRollResult rollResult = await graffitiService
            .RollLotteryAsync(account, requestedGiftId, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[CodexGraffiti] lottery AccountId={AccountId} GiftId={GiftId} SuperOwned={AlreadyOwned}",
            account.ID,
            rollResult.ChosenGiftId,
            rollResult.AlreadyOwned);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = new Dictionary<string, object>
            {
                ["gift_id"] = rollResult.ChosenGiftId,
                ["super_owned"] = rollResult.AlreadyOwned ? 1 : 0
            }
        });
    }

    [HttpPost("getlotteryhistory")]
    public async Task<IActionResult> GetLotteryHistory(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        List<Dictionary<string, object>> history = await graffitiService
            .GetLotteryHistoryAsync(account, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[CodexGraffiti] getlotteryhistory AccountId={AccountId} Count={HistoryCount}",
            account?.ID ?? 0,
            history.Count);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = history
        });
    }

    [HttpPost("getladder")]
    public async Task<IActionResult> GetLadder(CancellationToken cancellationToken = default)
    {
        List<GraffitiLadderStanding> standings = await graffitiService
            .GetLadderAsync(50, cancellationToken)
            .ConfigureAwait(false);

        List<Dictionary<string, object>> data = new(standings.Count);
        foreach (GraffitiLadderStanding row in standings)
        {
            data.Add(new Dictionary<string, object>
            {
                ["nickname"] = row.DisplayName,
                ["level_max"] = row.WallsCompleted,
                ["avatars_count"] = row.AvatarsTagged
            });
        }

        logger.LogInformation(
            "[CODEX_GRAFFITI] getladder ReturnedRows={RowCount} TopAvatarsTagged={TopCount}/12",
            data.Count,
            standings.Count > 0 ? standings[0].AvatarsTagged : 0);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = data
        });
    }

    [HttpPost("buy")]
    public async Task<IActionResult> BuyWall(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new JsonResult(new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Authentication required."
            });
        }

        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        string wallId = ExtractFormString(form, "wall_id", "1");

        await graffitiService.RecordWallPurchaseAsync(account, wallId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("[CodexGraffiti] buy AccountId={AccountId} WallId={WallId}", account.ID, wallId);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true
        });
    }

    [HttpPost("share")]
    public async Task<IActionResult> ShareGraffiti(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken).ConfigureAwait(false);
        IFormCollection? form = Request.HasFormContentType ? Request.Form : null;
        string wallId = ExtractFormString(form, "wall_id", "1");

        logger.LogInformation("[CodexGraffiti] share AccountId={AccountId} WallId={WallId}", account?.ID ?? 0, wallId);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = 1
        });
    }

    [HttpPost("getgraffitistats")]
    public async Task<IActionResult> GetCommunityGoalStats(CancellationToken cancellationToken = default)
    {
        GraffitiCommunityStatsResult stats = await graffitiService
            .GetCommunityGoalStatsAsync(cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "[CodexGraffiti] getgraffitistats InventoryCount={InventoryCount} Progress={Progress:F2}",
            stats.InventoryCount,
            stats.Progress);

        return new JsonResult(new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = new Dictionary<string, object>
            {
                ["goal_process"] = stats.Progress,
                ["current_week"] = 1
            }
        });
    }

    private static string ExtractFormString(IFormCollection? form, string key, string fallback)
    {
        if (form != null && form.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues val))
        {
            string? str = val.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str.Trim();
            }
        }

        return fallback;
    }
}
