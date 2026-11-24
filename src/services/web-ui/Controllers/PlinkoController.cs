// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[ApiController]
[Route("api/plinko")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class PlinkoController(
    IStoreApiClient storeApiClient,
    IUserApiClient userApiClient,
    IConfiguration? configuration = null,
    ILogger<PlinkoController>? logger = null) : ControllerBase
{
    public const int PlinkoTicketCost = 100;
    public const int PlinkoGoldCost = 50;

    private static readonly string[] AllPlinkoBorders =
    [
        "border_flaming_kongor",
        "border_frostbite",
        "border_electric_diamond",
        "border_prismatic_void",
        "border_hellbourne_pyre"
    ];

    private static readonly string[] AllPlinkoTitles =
    [
        "title_legion_veteran",
        "title_hellbourne_slayer",
        "title_tavern_regular",
        "title_kongors_bane",
        "title_immortal_legend"
    ];

    private readonly ILogger<PlinkoController> _logger = logger ?? NullLogger<PlinkoController>.Instance;
    private bool IsUnlockAllItemsEnabled =>
        NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(configuration, defaultFreeAll: true);

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        bool unlockAll = IsUnlockAllItemsEnabled;
        UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Ok(new PlinkoStatusResponse(
                false,
                false,
                0,
                0,
                0,
                0,
                unlockAll ? 0 : PlinkoTicketCost,
                unlockAll ? 0 : PlinkoGoldCost,
                [],
                [],
                null,
                null,
                unlockAll
            ));
        }

        PlinkoStatusResponseDto status = await storeApiClient.GetPlinkoStatusAsync(user.ID, cancellationToken);
        List<string> unlockedBorders = unlockAll
            ? [.. AllPlinkoBorders.Concat(status.UnlockedBorders).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [.. status.UnlockedBorders];
        List<string> unlockedTitles = unlockAll
            ? [.. AllPlinkoTitles.Concat(status.UnlockedTitles).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [.. status.UnlockedTitles];

        return Ok(new PlinkoStatusResponse(
            status.IsAuthenticated,
            unlockAll || status.CanClaimDaily,
            unlockAll ? 0 : status.DailyCooldownRemainingSeconds,
            status.PlinkoTickets,
            status.GoldCoins,
            status.SilverCoins,
            unlockAll ? 0 : status.TicketCost,
            unlockAll ? 0 : status.GoldCost,
            unlockedBorders,
            unlockedTitles,
            status.EquippedBorder,
            status.EquippedTitle,
            unlockAll
        ));
    }

    [HttpPost("drop")]
    public async Task<IActionResult> Drop([FromBody] PlinkoDropRequest request,
        CancellationToken cancellationToken = default)
    {
        UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Unauthorized(new { message = "You must be logged in to play Goblin Plinko." });
        }

        PlinkoDropResultDto result = await storeApiClient.PlinkoDropAsync(user.ID,
            request.Currency ?? (request.UseDaily ? "daily" : "tickets"), request.UseDaily, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Message ?? "Plinko drop failed." });
        }

        return Ok(new PlinkoDropResponse(
            true,
            result.Message ?? "Plinko drop completed successfully!",
            result.DropSlot,
            result.RewardType ?? "silver",
            result.RewardAmount,
            result.RewardName ?? "Reward",
            result.RewardCode,
            result.NewGold,
            result.NewSilver,
            result.NewTickets,
            result.IsJackpot
        ));
    }

    [HttpPost("equip-cosmetic")]
    public async Task<IActionResult> EquipCosmetic([FromBody] EquipCosmeticsRequest request,
        CancellationToken cancellationToken = default)
    {
        UserDto? user = await ResolveCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Unauthorized();
        }

        bool unlockAll = IsUnlockAllItemsEnabled;
        IReadOnlyList<UserWebRewardDto> ownedRewards = unlockAll
            ? []
            : await userApiClient.GetUserWebRewardsAsync(user.ID, cancellationToken);

        if (request.BorderId != null)
        {
            if (!unlockAll &&
                !ownedRewards.Any(r =>
                    string.Equals(r.RewardType, "Border", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.RewardCode, request.BorderId, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new { success = false, message = "You do not own this border." });
            }

            _logger.LogInformation(
                "[PlinkoController] Equipping Border '{BorderId}' for User #{UserId} (UnlockAll={UnlockAll})",
                request.BorderId, user.ID, unlockAll);
            await userApiClient.EquipWebRewardAsync(user.ID, "Border", request.BorderId, cancellationToken);
        }

        if (request.TitleId != null)
        {
            if (!unlockAll &&
                !ownedRewards.Any(r =>
                    string.Equals(r.RewardType, "Title", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.RewardCode, request.TitleId, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new { success = false, message = "You do not own this title." });
            }

            _logger.LogInformation(
                "[PlinkoController] Equipping Title '{TitleId}' for User #{UserId} (UnlockAll={UnlockAll})",
                request.TitleId, user.ID, unlockAll);
            await userApiClient.EquipWebRewardAsync(user.ID, "Title", request.TitleId, cancellationToken);
        }

        return Ok(new { success = true, message = "Website cosmetics updated successfully!" });
    }

    private async Task<UserDto?> ResolveCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? discordId = User.FindFirstValue("discord_id")
                            ?? User.FindFirstValue("urn:discord:id")
                            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        string? email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue(ClaimTypes.Name);

        if (!string.IsNullOrWhiteSpace(discordId))
        {
            UserDto? user = await userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            if (user != null) return user;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            return await userApiClient.GetUserByEmailAsync(email, cancellationToken);
        }

        return null;
    }

    public sealed record EquipCosmeticsRequest(string? BorderId, string? TitleId);

    // ReSharper disable NotAccessedPositionalProperty.Global
    public sealed record PlinkoStatusResponse(
        bool IsAuthenticated,
        bool CanSpinDaily,
        double SecondsUntilNextDailySpin,
        int PlinkoTickets,
        int GoldCoins,
        int SilverCoins,
        int TicketCost,
        int GoldCost,
        List<string> UnlockedBorders,
        List<string> UnlockedTitles,
        string? EquippedBorder,
        string? EquippedTitle,
        bool IsUnlockAllItemsEnabled = false
    );

    public sealed record PlinkoDropRequest(
        bool UseDaily,
        string? Currency = null
    );

    public sealed record PlinkoDropResponse(
        bool Success,
        string Message,
        int SlotIndex,
        string PrizeType,
        int PrizeAmount,
        string PrizeName,
        string? CosmeticId,
        int UpdatedGold,
        int UpdatedSilver,
        int UpdatedTickets,
        bool IsJackpot
    );
    // ReSharper restore NotAccessedPositionalProperty.Global
}
