// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services.Store;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class GetBundleContentsHandler(
    IDatabase distributedCache,
    IStoreRepository storeRepository,
    IAccountRepository accountRepository,
    IBundleCatalogService bundleCatalogService,
    ILogger<GetBundleContentsHandler> logger) : IClientRequestHandler
{
    private IDatabase DistributedCache { get; } = distributedCache;
    private IStoreRepository StoreRepository { get; } = storeRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IBundleCatalogService BundleCatalogService { get; } = bundleCatalogService;
    private ILogger<GetBundleContentsHandler> Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? bundleIdString = request.Query["bundle_id"].FirstOrDefault() ??
                                 request.Query["product_id"].FirstOrDefault() ??
                                 (request.HasFormContentType
                                     ? request.Form["bundle_id"].FirstOrDefault() ??
                                       request.Form["product_id"].FirstOrDefault()
                                     : null);

        if (string.IsNullOrEmpty(bundleIdString) || !int.TryParse(bundleIdString, out int bundleId))
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning("[GetBundleContents] Missing or invalid 'bundle_id' in request.");

            return new BadRequestObjectResult("Missing or invalid 'bundle_id'");
        }

        string? cookie = ClientRequestHelper.GetCookie(request)?.Trim('"', '\'');
        string? sessionAccountName = context.Items["SessionAccountName"] as string;

        if (sessionAccountName is null && !string.IsNullOrEmpty(cookie))
        {
            (bool accountSessionCookieIsValid, string? cacheAccountName) =
                await DistributedCache.ValidateAccountSessionCookie(cookie);

            if (!accountSessionCookieIsValid)
            {
                string cleanCookie = cookie.Replace("-", string.Empty);
                (accountSessionCookieIsValid, cacheAccountName) =
                    await DistributedCache.ValidateAccountSessionCookie(cleanCookie);
            }

            if (accountSessionCookieIsValid) sessionAccountName = cacheAccountName;
        }

        Account? account = null;
        if (!string.IsNullOrEmpty(sessionAccountName))
        {
            account = await StoreRepository.GetAccountWithUserForStoreAsync(sessionAccountName, cancellationToken)
                      ?? await AccountRepository.GetByNicknameAsync(sessionAccountName, cancellationToken);
        }

        if (account is null)
        {
            string? accountIdString = request.Query["account_id"].FirstOrDefault() ??
                                      (request.HasFormContentType
                                          ? request.Form["account_id"].FirstOrDefault()
                                          : null);

            if (int.TryParse(accountIdString, out int accountId))
            {
                account = await StoreRepository.GetAccountWithUserForStoreByIdAsync(accountId, cancellationToken)
                          ?? await AccountRepository.GetByIdAsync(accountId, cancellationToken);
            }
        }

        BundleContentResponse response = BundleCatalogService.GetBundleContent(bundleId, account);
        string jsonOutput = JsonSerializer.Serialize(response, MasterServerJsonContext.Default.BundleContentResponse);

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[GetBundleContents] Responded for Bundle {BundleId} ({ItemCount} items, isGrabBag={IsGrabBag}, isOwned={IsOwned}) to user '{User}'",
                bundleId, response.List.Count, response.Info.GrabBag, response.Info.IsOwned,
                account?.Name ?? "ANONYMOUS");

        return new ContentResult
        {
            Content = jsonOutput,
            ContentType = "application/json; charset=utf-8",
            StatusCode = 200
        };
    }
}
