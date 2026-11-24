// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

internal sealed class HostTelemetryResult
{
    public required bool HasLinkedHostAccounts { get; set; }
    public required bool HasPendingHostApplication { get; set; }
    public required List<string> LinkedHostAccountNames { get; set; } = [];
    public required List<int> LinkedPlayerAccountIds { get; set; } = [];
    public required List<string> LinkedPlayerAccountNames { get; set; } = [];
    public required int GamesHosted { get; set; }
    public required int TotalPlayedAcrossLinkedAccounts { get; set; }
    public required double HostingRatio { get; set; }
    public required double CommunityContributionPercentage { get; set; }
    public required string? HostBadgeTier { get; set; }
    public required HostApplicationDto? PrimaryHostApplication { get; set; }
    public required string HostUsername { get; set; }
}

internal static class HostTelemetryHelper
{
    public static async Task<HostTelemetryResult> ResolveHostTelemetryAsync(
        IUserApiClient userApiClient,
        AccountDto account,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (logger?.IsEnabled(LogLevel.Information) == true)
            logger.LogInformation(
                "[HostTelemetry] Resolving host telemetry for Account: {AccountName} (ID: {AccountId}, Type: {Type})",
                account.Name, account.ID, account.Type);

        HostTelemetryResultDto? dto = await userApiClient.GetHostTelemetryAsync(account.Name, cancellationToken);
        if (dto != null)
            return new HostTelemetryResult
            {
                HasLinkedHostAccounts = dto.HasLinkedHostAccounts,
                HasPendingHostApplication = dto.HasPendingHostApplication,
                LinkedHostAccountNames = dto.LinkedHostAccountNames,
                LinkedPlayerAccountIds = dto.LinkedPlayerAccountIds,
                LinkedPlayerAccountNames = dto.LinkedPlayerAccountNames,
                GamesHosted = dto.GamesHosted,
                TotalPlayedAcrossLinkedAccounts = dto.TotalPlayedAcrossLinkedAccounts,
                HostingRatio = dto.HostingRatio,
                CommunityContributionPercentage = dto.CommunityContributionPercentage,
                HostBadgeTier = dto.HostBadgeTier,
                PrimaryHostApplication = dto.PrimaryHostApplication,
                HostUsername = dto.HostUsername
            };

        return new HostTelemetryResult
        {
            HasLinkedHostAccounts = false,
            HasPendingHostApplication = false,
            LinkedHostAccountNames = [],
            LinkedPlayerAccountIds = [account.ID],
            LinkedPlayerAccountNames = [account.Name],
            GamesHosted = 0,
            TotalPlayedAcrossLinkedAccounts = 0,
            HostingRatio = 0.0,
            CommunityContributionPercentage = 0.0,
            HostBadgeTier = null,
            PrimaryHostApplication = null,
            HostUsername = $"{account.Name}_host"
        };
    }
}
