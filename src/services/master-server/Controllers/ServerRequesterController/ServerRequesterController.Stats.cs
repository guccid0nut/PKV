// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private Task<IActionResult> HandleGetQuickStats(CancellationToken cancellationToken = default)
    {
        // Stub implementation to prevent 500 errors.
        // Quick stats likely returns summary data for player profiles.
        Dictionary<string, object> response = [];

        return Task.FromResult<IActionResult>(Ok(PhpSerialization.Serialize(response)));
    }

    private async Task<IActionResult> HandleGetServerUpgrades(CancellationToken cancellationToken = default)
    {
        string? cookie = null;
        try
        {
            cookie = Request.Query["cookie"].FirstOrDefault();
            if (string.IsNullOrEmpty(cookie) && Request.HasFormContentType)
                cookie = Request.Form["cookie"].FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ServerRequester] Failed to parse cookie from request body.");
        }

        if (string.IsNullOrEmpty(cookie)) return BadRequest(@"Missing Value For Parameter ""cookie""");

        string? accountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null)
        {
            string maskedCookie = SecurityMasking.MaskToken(cookie);
            Logger.LogWarning(
                "[ServerRequester:GetUpgrades] Unauthorized: No valid session found for cookie '{Cookie}'",
                maskedCookie);
            return Unauthorized($@"No Valid Session Cookie Could Be Found For Cookie ""{maskedCookie}""");
        }

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account is null) return NotFound($@"Account With Name ""{accountName}"" Could Not Be Found");

        HashSet<string> expandedOwned = await UpgradesResolver.ResolveExpandedOwnedItemsAsync(account, cancellationToken);

        Dictionary<string, object> response = new()
        {
            ["my_upgrades"] = expandedOwned.ToList(),
            ["selected_upgrades"] = account.SelectedStoreItemCodes
        };

        return Ok(PhpSerialization.Serialize(response));
    }
}
