// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using KONGOR.MasterServer.Models.RequestResponse.Upgrades;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class InitStatsHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    IPlayerStatisticsService statisticsService,
    IHeroDefinitionService heroDefinitions,
    IOptions<OperationalConfiguration> operationalConfiguration,
    ILogger<InitStatsHandler> logger) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IPlayerStatisticsService StatisticsService { get; } = statisticsService;
    private IHeroDefinitionService HeroDefinitions { get; } = heroDefinitions;
    private OperationalConfiguration OperationalConfiguration { get; } = operationalConfiguration.Value;
    private ILogger Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(Request);

        LogRequestReceived(SecurityMasking.MaskToken(cookie));

        if (cookie is null)
        {
            LogMissingCookie();
            return new BadRequestObjectResult(@"Missing Value For Form Parameter ""cookie""");
        }

        string? accountName = context.Items["SessionAccountName"] as string
                              ?? await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null)
        {
            LogSessionNotFound(SecurityMasking.MaskToken(cookie));
            return new UnauthorizedObjectResult("Session Not Found");
        }

        LogFetchingAccount(accountName);
        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account is null)
        {
            LogAccountNotFound(accountName);
            return new NotFoundObjectResult("Account Not Found");
        }

        LogComputingStats(accountName);

        // Fetch aggregated player stats for the account
        PlayerStatisticsAggregatedDTO stats =
            await StatisticsService.GetAggregatedStatisticsAsync(account.ID, cancellationToken: cancellationToken);

        ShowSimpleStatsResponse fullResponse = ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats,
            int.Parse(OperationalConfiguration.CurrentSeason, CultureInfo.InvariantCulture), HeroDefinitions);

        double rnkRating = 1500.0 + stats.RankedRatingChange;
        double csRating = 1500.0 + stats.CasualRatingChange;

        AccountFieldStatsDTO accountFieldStats = new()
        {
            AccountID = account.ID.ToString(),
            Level = fullResponse.Level,
            LevelExp = fullResponse.LevelExperience.ToString(CultureInfo.InvariantCulture),
            PublicSkillRating = "1500.000",
            RankedRating = rnkRating.ToString("F3", CultureInfo.InvariantCulture),
            CasualRating = csRating.ToString("F3", CultureInfo.InvariantCulture),
            TotalGamesPlayed = stats.TotalMatches,
            TotalDiscos = stats.Disconnected,
            RankedGamesPlayed = stats.RankedMatches,
            RankedDiscos = stats.RankedDiscos,
            CasualGamesPlayed = stats.CasualMatches,
            CasualDiscos = stats.CasualDiscos,
            MidGamesPlayed = 0,
            MidDiscos = 0,
            RiftGamesPlayed = 0,
            RiftDiscos = 0,
            CampaignGamesPlayed = 0,
            CampaignDiscos = 0,
            CampaignCasualGamesPlayed = 0,
            CampaignCasualDisconnections = 0,
            TrialGamesPlayed = 0,
            Standing = 3,
            IsNew = 0
        };

        GetInitialStatisticsResponseDTO responseDto = new()
        {
            Information = new Dictionary<int, AccountFieldStatsDTO> { { account.ID, accountFieldStats } }
        };

        return new PhpResult(responseDto);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[InitStats] Request received. Cookie: {CookiePrefix}")]
    private partial void LogRequestReceived(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InitStats] Missing cookie.")]
    private partial void LogMissingCookie();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InitStats] Session not found for cookie '{CookiePrefix}'.")]
    private partial void LogSessionNotFound(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InitStats] Fetching account details for '{AccountName}'...")]
    private partial void LogFetchingAccount(string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InitStats] Account '{AccountName}' not found in DB.")]
    private partial void LogAccountNotFound(string accountName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InitStats] Computing full stats for '{AccountName}'...")]
    private partial void LogComputingStats(string accountName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InitStats] Constructing response dictionary.")]
    private partial void LogConstructingResponse();

    [LoggerMessage(Level = LogLevel.Information, Message = "[InitStats] Response generated. Length: {Length}")]
    private partial void LogResponseGenerated(int length);
}
