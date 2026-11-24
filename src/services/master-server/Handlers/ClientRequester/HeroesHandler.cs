// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Catalog;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class HeroesHandler(
    ILogger<HeroesHandler> logger,
    IPrebakedCatalogCache? catalogCache = null)
    : IClientRequestHandler
{
    private ILogger Logger { get; } = logger;
    private IPrebakedCatalogCache CatalogCache { get; } = catalogCache ?? PrebakedCatalogCache.Instance;

    public Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        LogRequestReceived();
        LogReturningHeroes(CatalogCache.HeroesCount);

        return Task.FromResult<IActionResult>(new PhpResult(CatalogCache.PrebakedPhpHeroesBytes));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[Heroes] Request received for hero list.")]
    private partial void LogRequestReceived();

    [LoggerMessage(Level = LogLevel.Information, Message = "[Heroes] Returning {Count} heroes.")]
    private partial void LogReturningHeroes(int count);
}
