// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.ClientRequester;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class RewardsHandler(ILogger<RewardsHandler> logger) : IClientRequestHandler
{
    public ILogger<RewardsHandler> Logger { get; } = logger;

    public Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        LogClaimingRewards();

        RewardsResponseDTO response = new();

        return Task.FromResult<IActionResult>(new PhpResult(response));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[Rewards] Claiming season rewards (STUB).")]
    private partial void LogClaimingRewards();
}
