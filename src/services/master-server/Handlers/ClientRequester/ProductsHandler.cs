// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Catalog;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class ProductsHandler(IDatabase distributedCache, IPrebakedCatalogCache? catalogCache = null)
    : IClientRequestHandler
{
    private IDatabase DistributedCache { get; } = distributedCache;
    private IPrebakedCatalogCache CatalogCache { get; } = catalogCache ?? PrebakedCatalogCache.Instance;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(Request);
        if (string.IsNullOrEmpty(cookie)) return new UnauthorizedResult();

        string cleanCookie = cookie.Replace("-", string.Empty);
        string? sessionAccountName = context.Items["SessionAccountName"] as string;

        if (sessionAccountName is null)
        {
            (bool accountSessionCookieIsValid, string? cacheAccountName) =
                await DistributedCache.ValidateAccountSessionCookie(cleanCookie);
            if (accountSessionCookieIsValid) sessionAccountName = cacheAccountName;
        }

        if (sessionAccountName is null)
            return new UnauthorizedObjectResult($@"No Session Found For Cookie ""{cookie}""");

        return new PhpResult(CatalogCache.PrebakedPhpProductsBytes);
    }
}

