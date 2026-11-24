// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Collections;
using System.Globalization;
using ASPIRE.Common.Security;
using KONGOR.MasterServer.Extensions;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class GuideHandler(
    IGuideRepository guideRepository,
    IAccountRepository accountRepository,
    ISessionRepository sessionRepository,
    IDatabase distributedCache,
    ILogger<GuideHandler> logger) : IClientRequestHandler
{
    private IGuideRepository GuideRepository { get; } = guideRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionRepository SessionRepository { get; } = sessionRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.GetQueryOrFormValue("f");

        LogHandlingRequestFunction(functionName);

        if (functionName == "get_guide")
        {
            return await GetGuide(Request, cancellationToken);
        }

        if (functionName is "get_guide_list_filtered" or "get_guide_list")
        {
            return await GetGuideList(Request, cancellationToken);
        }

        if (functionName is "guide_default_set" or "guide_default_remove")
        {
            return await HandleGuideDefaultAction(Request, functionName == "guide_default_set", cancellationToken);
        }

        if (functionName is "guide_favorite_set" or "guide_favorite_remove")
        {
            return await HandleGuideFavoriteAction(Request, functionName == "guide_favorite_set", cancellationToken);
        }

        if (functionName == "guide_vote")
        {
            return await HandleGuideVoteAction(Request, cancellationToken);
        }

        LogFallbackToGetGuideList(functionName);
        return await GetGuideList(Request, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] Handling request function: '{FunctionName}'")]
    private partial void LogHandlingRequestFunction(string? functionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[GuideHandler] Fallback to GetGuideList for unknown function: '{FunctionName}'")]
    private partial void LogFallbackToGetGuideList(string? functionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] GetGuideList request: account='{AccountId}', hero='{HeroIdentifier}'")]
    private partial void LogGetGuideList(string? accountId, string? heroIdentifier);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[GuideHandler] No accounts exist in DB to process GetGuideList")]
    private partial void LogNoAccountsInDb();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] No guides found for Hero '{HeroIdentifier}' - returning synthetic guide")]
    private partial void LogNoGuidesFound(string heroIdentifier);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] Found {Count} guides for Hero '{HeroIdentifier}' for Account ID {AccountId}")]
    private partial void LogFoundGuides(int count, string heroIdentifier, int accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[GuideHandler] GetGuide request for GID '{GuideId}'")]
    private partial void LogGetGuide(string? guideId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] Generating synthetic fallback Featured Guide for Hero '{Hero}'")]
    private partial void LogGeneratingSyntheticFallback(string hero);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[GuideHandler] Returning guide ID {GuideId} ({GuideName}) for Hero '{Hero}' (isDef={IsDef}, isFav={IsFav})")]
    private partial void LogReturningGuide(int guideId, string guideName, string hero, bool isDef, bool isFav);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[GuideHandler] GuideDefaultAction: isSet={IsSet}, Acc={AccountId}, Gid={GuideId}, HostTime={HostTime}")]
    private partial void LogGuideDefaultAction(bool isSet, int accountId, int guideId, int hostTime);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[GuideHandler] Saved default guide {GuideId} for Account {AccountId} (DefaultGuideIDs Count: {Count})")]
    private partial void LogSavedDefaultGuide(int guideId, int accountId, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[GuideHandler] GuideFavoriteAction: isSet={IsSet}, Acc={AccountId}, Gid={GuideId}, HostTime={HostTime}")]
    private partial void LogGuideFavoriteAction(bool isSet, int accountId, int guideId, int hostTime);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "[GuideHandler] Saved favorite guide {GuideId} for Account {AccountId} (FavoriteGuideIDs Count: {Count})")]
    private partial void LogSavedFavoriteGuide(int guideId, int accountId, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] GuideVoteAction: VoteVal={VoteVal}, Gid={GuideId}, HostTime={HostTime}")]
    private partial void LogGuideVoteAction(int voteVal, int guideId, int hostTime);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GuideHandler] Guide {GuideId} voted: Up={Up}, Down={Down}, Rating={Rating}")]
    private partial void LogGuideVoted(int guideId, int up, int down, float rating);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[GuideHandler] Exception occurred while resolving account; proceeding anonymously")]
    private partial void LogResolveAccountFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[GuideHandler] Repository exception fetching guides for hero '{Hero}'; falling back to synthetic guide")]
    private partial void LogGetGuidesForHeroFailed(Exception ex, string hero);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[GuideHandler] Repository exception fetching guide #{GuideId}")]
    private partial void LogGetGuideByIdFailed(Exception ex, int guideId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[GuideHandler] Repository exception fetching active guide for hero '{Hero}'")]
    private partial void LogGetActiveGuideFailed(Exception ex, string hero);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[GuideHandler] Repository exception updating guide default/favorite/vote state")]
    private partial void LogGuideMutationFailed(Exception ex);

    private static string SanitizeHeroIdentifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Hero_Valkyrie";
        string trimmed = raw.Trim();
        if (trimmed.Length > 50) trimmed = trimmed[..50];

        System.Text.StringBuilder sb = new();
        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
        }
        string sanitized = sb.ToString();
        return string.IsNullOrEmpty(sanitized) ? "Hero_Valkyrie" : sanitized;
    }

    private static string? GetParam(HttpRequest request, string key)
    {
        return request.GetFormOrQueryValue(key);
    }

    private async Task<Account?> ResolveAccount(HttpRequest request, CancellationToken cancellationToken = default)
    {
        // Strictly bind to the authenticated session account (never resolve by caller-supplied account/account_id).
        string? sessionAccountName = request.HttpContext.Items["SessionAccountName"] as string;
        if (!string.IsNullOrEmpty(sessionAccountName))
        {
            Account? acc = await AccountRepository.GetByNicknameAsync(sessionAccountName, cancellationToken);
            if (acc != null)
            {
                return acc;
            }
        }

        string? cookie = GetParam(request, "cookie") ?? ClientRequestHelper.GetCookie(request);
        if (!string.IsNullOrEmpty(cookie))
        {
            (bool isValid, string? accName) = await DistributedCache.ValidateAccountSessionCookie(cookie);
            if (!isValid || string.IsNullOrEmpty(accName))
            {
                accName = await SessionRepository.GetAccountNameForSessionCookieAsync(cookie, cancellationToken);
                if (string.IsNullOrEmpty(accName))
                {
                    string cleanCookie = cookie.Replace("-", string.Empty);
                    if (!string.Equals(cleanCookie, cookie, StringComparison.Ordinal))
                        accName = await SessionRepository.GetAccountNameForSessionCookieAsync(cleanCookie, cancellationToken);
                }
            }

            if (!string.IsNullOrEmpty(accName))
            {
                Account? acc = await AccountRepository.GetByNicknameAsync(accName, cancellationToken);
                if (acc != null)
                {
                    return acc;
                }
            }
        }

        return null;
    }

    private async Task<IActionResult> GetGuideList(HttpRequest Request, CancellationToken cancellationToken = default)
    {
        string? accountID = GetParam(Request, "account");
        string? heroIdentifier = GetParam(Request, "hero");
        string? hostTime = GetParam(Request, "hosttime");

        LogGetGuideList(accountID, heroIdentifier);

        int hostTimeInt = int.TryParse(hostTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ht)
            ? ht
            : (int)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();

        Account? account = null;
        try
        {
            account = await ResolveAccount(Request, cancellationToken);
        }
        catch (Exception ex)
        {
            LogResolveAccountFailed(ex);
        }

        heroIdentifier = SanitizeHeroIdentifier(heroIdentifier);

        IReadOnlyList<HeroGuide> fetchedGuides;
        try
        {
            fetchedGuides = await GuideRepository.GetGuidesForHeroAsync(
                heroIdentifier, account?.ID, account?.Name, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGetGuidesForHeroFailed(ex, heroIdentifier);
            fetchedGuides = [];
        }

        List<HeroGuide> guides = fetchedGuides.Select(SanitizeGuideForEngine).ToList();

        if (guides.Count == 0)
        {
            LogNoGuidesFound(heroIdentifier);
            guides.Add(CreateSyntheticFallbackGuide(heroIdentifier, account));
        }

        LogFoundGuides(guides.Count, heroIdentifier, account?.ID ?? 0);
        return new PhpResult(new GuideListResponse(guides, account, hostTimeInt));
    }

    private static HeroGuide SanitizeGuideForEngine(HeroGuide guide)
    {
        return new HeroGuide
        {
            ID = guide.ID,
            Name = PerimeterSecuritySanitizer.SanitizeGuideText(guide.Name, PerimeterSecuritySanitizer.MaxGuideNameLength),
            HeroIdentifier = guide.HeroIdentifier,
            HeroName = guide.HeroName,
            Author = guide.Author,
            Featured = guide.Featured,
            Public = guide.Public,
            Rating = guide.Rating,
            UpVotes = guide.UpVotes,
            DownVotes = guide.DownVotes,
            TargetMap = guide.TargetMap,
            Intro = PerimeterSecuritySanitizer.SanitizeGuideText(guide.Intro, PerimeterSecuritySanitizer.MaxGuideIntroLength),
            Content = PerimeterSecuritySanitizer.SanitizeGuideText(guide.Content, PerimeterSecuritySanitizer.MaxGuideContentLength),
            StartingItems = PerimeterSecuritySanitizer.SanitizeItemList(guide.StartingItems),
            EarlyGameItems = PerimeterSecuritySanitizer.SanitizeItemList(guide.EarlyGameItems),
            CoreItems = PerimeterSecuritySanitizer.SanitizeItemList(guide.CoreItems),
            LuxuryItems = PerimeterSecuritySanitizer.SanitizeItemList(guide.LuxuryItems),
            AbilityQueue = PerimeterSecuritySanitizer.SanitizeAbilityQueue(guide.AbilityQueue),
            TimestampCreated = guide.TimestampCreated,
            TimestampLastUpdated = guide.TimestampLastUpdated
        };
    }

    private static HeroGuide CreateSyntheticFallbackGuide(string heroIdentifier, Account? account)
    {
        string cleanHeroName = heroIdentifier.Replace("Hero_", "");
        Account defaultAuthor = account ?? new Account { ID = 1, Name = "KONGOR", User = null!, IsMain = true };
        return new HeroGuide
        {
            ID = 99999,
            Name = $"Featured {cleanHeroName} Guide",
            HeroIdentifier = heroIdentifier,
            HeroName = cleanHeroName,
            Author = defaultAuthor,
            Featured = true,
            Public = true,
            Rating = 1.0f,
            UpVotes = 1,
            DownVotes = 0,
            Intro =
                $"{cleanHeroName} is a versatile hero capable of leading your team to victory with the right items and ability timing.",
            Content =
                "Use abilities effectively in early trades, secure core items, and coordinate with team for objectives.",
            StartingItems =
                "Item_RunesOfTheBlight|Item_FlamingEye|Item_GoldenApple|Item_ManaPotion|Item_MinorTotem|Item_MinorTotem",
            EarlyGameItems = "Item_GraveLocket|Item_Marchers|Item_BloodChalice|Item_PowerSupply",
            CoreItems = "Item_PushStaff|Item_EnhancedMarchers|Item_PortalKey",
            LuxuryItems = "Item_Morph|Item_BarrierIdol|Item_DaemonicBreastplate",
            AbilityQueue =
                $"Ability_{cleanHeroName}1|Ability_{cleanHeroName}2|Ability_{cleanHeroName}1|Ability_{cleanHeroName}3|Ability_{cleanHeroName}1|Ability_{cleanHeroName}4|Ability_{cleanHeroName}1|Ability_{cleanHeroName}2|Ability_{cleanHeroName}2|Ability_{cleanHeroName}2|Ability_{cleanHeroName}4|Ability_{cleanHeroName}3|Ability_{cleanHeroName}3|Ability_{cleanHeroName}3|Ability_AttributeBoost|Ability_{cleanHeroName}4"
        };
    }

    private async Task<IActionResult> GetGuide(HttpRequest Request, CancellationToken cancellationToken = default)
    {
        string? accountID = GetParam(Request, "account");
        string targetHero = SanitizeHeroIdentifier(GetParam(Request, "hero"));
        string? hostTime = GetParam(Request, "hosttime");
        string? guideID = GetParam(Request, "gid");

        LogGetGuide(guideID);

        int hostTimeInt = int.TryParse(hostTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ht)
            ? ht
            : (int)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();

        Account? account = null;
        try
        {
            account = await ResolveAccount(Request, cancellationToken);
        }
        catch (Exception ex)
        {
            LogResolveAccountFailed(ex);
        }

        HeroGuide? guide = null;
        if (guideID != "99999" &&
            int.TryParse(guideID, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guideIdInt))
        {
            try
            {
                guide = await GuideRepository.GetByIdAsync(guideIdInt, cancellationToken);
            }
            catch (Exception ex)
            {
                LogGetGuideByIdFailed(ex, guideIdInt);
            }

            if (guide != null)
            {
                bool canAccess = guide.Featured ||
                                 guide.Public ||
                                 (account != null && ((guide.Author != null && guide.Author.ID == account.ID) ||
                                                      (account.User != null && guide.Author?.User != null &&
                                                       guide.Author.User.ID == account.User.ID) ||
                                                      string.Equals(guide.Author?.Name, account.Name,
                                                          StringComparison.OrdinalIgnoreCase)));

                if (!canAccess)
                {
                    guide = null;
                }
            }
        }

        if (guide is null)
        {
            try
            {
                guide = await GuideRepository.GetActiveGuideForHeroAsync(
                    targetHero, account?.ID, account?.Name, account?.DefaultGuideIDs, cancellationToken);
            }
            catch (Exception ex)
            {
                LogGetActiveGuideFailed(ex, targetHero);
            }
        }

        if (guide is null)
        {
            LogGeneratingSyntheticFallback(targetHero);
            guide = CreateSyntheticFallbackGuide(targetHero, account);
        }

        int defaultGuideId = -1;
        if (account is { DefaultGuideIDs.Count: > 0 })
        {
            try
            {
                IReadOnlyList<HeroGuide> heroGuides = await GuideRepository.GetGuidesForHeroAsync(
                    guide.HeroIdentifier, account.ID, account.Name, cancellationToken);
                HeroGuide? customDef = heroGuides.FirstOrDefault(g => account.DefaultGuideIDs.Contains(g.ID));
                if (customDef != null)
                {
                    defaultGuideId = customDef.ID;
                }
            }
            catch (Exception ex)
            {
                LogGetGuidesForHeroFailed(ex, guide.HeroIdentifier);
            }
        }

        if (defaultGuideId == -1)
        {
            try
            {
                IReadOnlyList<HeroGuide> fallbackGuides = await GuideRepository.GetGuidesForHeroAsync(
                    guide.HeroIdentifier, null, null, cancellationToken);
                HeroGuide? fallbackDef = fallbackGuides.FirstOrDefault(g => g.Featured) ?? fallbackGuides.FirstOrDefault();
                if (fallbackDef != null)
                {
                    defaultGuideId = fallbackDef.ID;
                }
            }
            catch (Exception ex)
            {
                LogGetGuidesForHeroFailed(ex, guide.HeroIdentifier);
            }
        }

        guide = SanitizeGuideForEngine(guide);
        bool isDef = (guide.ID == defaultGuideId);
        bool isFav = account?.FavoriteGuideIDs.Contains(guide.ID) == true;

        LogReturningGuide(guide.ID, guide.Name, targetHero, isDef, isFav);
        return new PhpResult(new GuideResponseSuccess(guide, hostTimeInt, isDef, isFav));
    }

    private async Task<IActionResult> HandleGuideDefaultAction(HttpRequest Request, bool isSet,
        CancellationToken cancellationToken = default)
    {
        string? hostTime = GetParam(Request, "hosttime");
        string? guideID = GetParam(Request, "gid");
        int hostTimeInt = int.TryParse(hostTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ht)
            ? ht
            : 0;
        int guideIdInt = int.TryParse(guideID, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gid)
            ? gid
            : 0;

        Account? account;
        try
        {
            account = await ResolveAccount(Request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResolveAccountFailed(ex);
            account = null;
        }

        if (account is null)
        {
            Logger.LogWarning("[GuideHandler] Rejected unauthenticated guide_default action (Guide #{GuideId}, IsSet={IsSet})",
                guideIdInt, isSet);
            return new UnauthorizedObjectResult("Invalid or missing session");
        }

        int accountIdInt = account.ID;

        LogGuideDefaultAction(isSet, accountIdInt, guideIdInt, hostTimeInt);

        int resultGuideId = isSet ? guideIdInt : -1;
        if (guideIdInt > 0)
        {
            try
            {
                resultGuideId = await GuideRepository.SetDefaultGuideAsync(account.ID, guideIdInt, isSet, cancellationToken);
                LogSavedDefaultGuide(guideIdInt, account.ID, account.DefaultGuideIDs.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogGuideMutationFailed(ex);
            }
        }

        Hashtable responseDict = new()
        {
            ["errors"] = string.Empty,
            ["set"] = isSet ? 1 : 0,
            ["success"] = 1,
            ["guide_default"] = resultGuideId,
            ["hosttime"] = hostTimeInt
        };

        return new PhpResult(responseDict);
    }

    private async Task<IActionResult> HandleGuideFavoriteAction(HttpRequest Request, bool isSet,
        CancellationToken cancellationToken = default)
    {
        string? hostTime = GetParam(Request, "hosttime");
        string? guideID = GetParam(Request, "gid");
        int hostTimeInt = int.TryParse(hostTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ht)
            ? ht
            : 0;
        int guideIdInt = int.TryParse(guideID, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gid)
            ? gid
            : 0;

        Account? account;
        try
        {
            account = await ResolveAccount(Request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResolveAccountFailed(ex);
            account = null;
        }

        if (account is null)
        {
            Logger.LogWarning("[GuideHandler] Rejected unauthenticated guide_favorite action (Guide #{GuideId}, IsSet={IsSet})",
                guideIdInt, isSet);
            return new UnauthorizedObjectResult("Invalid or missing session");
        }

        int accountIdInt = account.ID;

        LogGuideFavoriteAction(isSet, accountIdInt, guideIdInt, hostTimeInt);

        int resultGuideId = isSet ? guideIdInt : -1;
        if (guideIdInt > 0)
        {
            try
            {
                resultGuideId = await GuideRepository.SetFavoriteGuideAsync(account.ID, guideIdInt, isSet, cancellationToken);
                LogSavedFavoriteGuide(guideIdInt, account.ID, account.FavoriteGuideIDs.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogGuideMutationFailed(ex);
            }
        }

        Hashtable responseDict = new()
        {
            ["errors"] = string.Empty,
            ["set"] = isSet ? 1 : 0,
            ["success"] = 1,
            ["guide_favorite"] = resultGuideId,
            ["hosttime"] = hostTimeInt
        };

        return new PhpResult(responseDict);
    }

    private async Task<IActionResult> HandleGuideVoteAction(HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        string? hostTime = GetParam(Request, "hosttime");
        string? guideID = GetParam(Request, "gid");
        string? val = GetParam(Request, "val");
        int hostTimeInt = int.TryParse(hostTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ht)
            ? ht
            : 0;
        int guideIdInt = int.TryParse(guideID, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gid)
            ? gid
            : 0;
        int voteVal = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 1;

        Account? account;
        try
        {
            account = await ResolveAccount(Request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResolveAccountFailed(ex);
            account = null;
        }

        if (account is null)
        {
            Logger.LogWarning("[GuideHandler] Rejected unauthenticated guide_vote request for Guide #{GuideId}", guideIdInt);
            return new UnauthorizedObjectResult("Invalid or missing session");
        }

        LogGuideVoteAction(voteVal, guideIdInt, hostTimeInt);

        if (guideIdInt > 0)
        {
            try
            {
                var (up, down, rating) = await GuideRepository.VoteGuideAsync(guideIdInt, voteVal, cancellationToken);
                LogGuideVoted(guideIdInt, up, down, rating);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogGuideMutationFailed(ex);
            }
        }

        Hashtable responseDict = new()
        {
            ["errors"] = string.Empty,
            ["vote"] = voteVal,
            ["guide_vote"] = guideIdInt,
            ["success"] = 1,
            ["hosttime"] = hostTimeInt
        };

        return new PhpResult(responseDict);
    }
}
