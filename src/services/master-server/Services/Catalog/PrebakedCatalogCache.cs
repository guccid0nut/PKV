// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Models.RequestResponse.Store;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KONGOR.MasterServer.Services.Catalog;

/// <summary>
///     Thread-safe zero-allocation singleton that pre-bakes static store products and hero lists
///     into immutable UTF-8 wire protocol byte buffers, eliminating per-request object construction,
///     reflection serialization, and heap churn.
/// </summary>
public sealed class PrebakedCatalogCache : IPrebakedCatalogCache
{
    private static readonly Lazy<PrebakedCatalogCache> _defaultInstance =
        new(() => new PrebakedCatalogCache(NullLogger<PrebakedCatalogCache>.Instance));

    /// <summary>
    ///     Fallback singleton instance for non-DI environments (e.g. standalone unit tests).
    /// </summary>
    public static PrebakedCatalogCache Instance => _defaultInstance.Value;

    private readonly ILogger<PrebakedCatalogCache> _logger;
    private readonly Lock _syncLock = new();

    private volatile CacheState _state = null!;

    public PrebakedCatalogCache(ILogger<PrebakedCatalogCache> logger)
    {
        _logger = logger;
        Rebake();
    }

    public ReadOnlyMemory<byte> PrebakedPhpProductsBytes => _state.PhpProductsBytes;
    public ReadOnlyMemory<byte> PrebakedJsonProductsBytes => _state.JsonProductsBytes;
    public ReadOnlyMemory<byte> PrebakedPhpHeroesBytes => _state.PhpHeroesBytes;
    public int ProductsCrc => _state.ProductsCrc;
    public int HeroesCount => _state.HeroesCount;

    public void Refresh()
    {
        lock (_syncLock)
        {
            Rebake();
        }
    }

    private void Rebake()
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[PrebakedCatalogCache] Pre-baking static catalogs into zero-allocation wire buffers...");

        // 1. Bake Products Catalog
        GetProductsResponse productsResponse = new(JSONConfiguration.StoreItemsConfiguration);
        byte[] phpProducts = PhpSerializer.SerializeToUtf8Bytes(productsResponse);
        byte[] jsonProducts = JsonSerializer.SerializeToUtf8Bytes(
            productsResponse.Products,
            MasterServerJsonContext.Default.DictionaryStringDictionaryInt32GetProductsResponseEntry);

        // 2. Bake Heroes Catalog
        Dictionary<string, object> heroes = [];
        List<string> allHeroIdentifiers = ASPIRE.Common.Constants.Heroes.AllHeroIdentifiers();
        for (int i = 0; i < allHeroIdentifiers.Count; i++)
        {
            int heroIndex = i + 1;
            heroes[heroIndex.ToString(CultureInfo.InvariantCulture)] = allHeroIdentifiers[i];
        }

        if (heroes.Count == 0)
        {
            heroes["1"] = "Hero_Scout";
            heroes["2"] = "Hero_Maliken";
            heroes["3"] = "Hero_Pyromancer";
        }

        byte[] phpHeroes = PhpSerializer.SerializeToUtf8Bytes(heroes);

        _state = new CacheState(
            phpProducts,
            jsonProducts,
            phpHeroes,
            productsResponse.CRC,
            heroes.Count
        );

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[PrebakedCatalogCache] Static catalogs baked successfully. Products: {ProductBytes} bytes (CRC: {CRC}), Heroes: {HeroCount} ({HeroBytes} bytes)",
                phpProducts.Length,
                productsResponse.CRC,
                heroes.Count,
                phpHeroes.Length);
        }
    }

    private sealed record CacheState(
        byte[] PhpProductsBytes,
        byte[] JsonProductsBytes,
        byte[] PhpHeroesBytes,
        int ProductsCrc,
        int HeroesCount);
}
