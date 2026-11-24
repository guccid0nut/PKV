// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers;

[ApiController]
[Route("api/presence")]
public class PresenceController(IAccountRepository accountRepository) : ControllerBase
{
    private IAccountRepository AccountRepository { get; } = accountRepository;

    [HttpGet("{accountId:int}")]
    public async Task<IActionResult> GetPresence(int accountId, CancellationToken cancellationToken = default)
    {
        // Fetch the account to ensure they exist
        Account? account = await AccountRepository.GetByIdAsync(accountId, cancellationToken);

        if (account == null) return NotFound("Account not found");


        // Note(integrations): Discord Rich Presence baseline payload providing live status for WILLOWMAKER launcher.

        object presenceResponse = new
        {
            AccountId = accountId,
            State = "Playing Solo",
            Details = "Competitive",
            LargeImageKey = "map_forest",
            LargeImageText = "Forests of Caldavar",
            SmallImageKey = "rank_gold",
            SmallImageText = "Gold Tier"
        };

        return Ok(presenceResponse);
    }
}
