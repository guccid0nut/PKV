// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Catalog;

/// <summary>
///     High-performance zero-allocation cache that pre-serializes static catalogs
///     (store products and heroes) into immutable UTF-8 wire protocol byte buffers.
/// </summary>
public interface IPrebakedCatalogCache
{
    /// <summary>
    ///     Pre-serialized PHP wire protocol byte buffer for the store product catalog (f=get_products).
    /// </summary>
    ReadOnlyMemory<byte> PrebakedPhpProductsBytes { get; }

    /// <summary>
    ///     Pre-serialized JSON byte buffer for the store product catalog (for modern 5.0 consumers).
    /// </summary>
    ReadOnlyMemory<byte> PrebakedJsonProductsBytes { get; }

    /// <summary>
    ///     Pre-serialized PHP wire protocol byte buffer for the heroes catalog (f=get_all_heroes).
    /// </summary>
    ReadOnlyMemory<byte> PrebakedPhpHeroesBytes { get; }

    /// <summary>
    ///     Stable CRC32 of the store product catalog.
    /// </summary>
    int ProductsCrc { get; }

    /// <summary>
    ///     Total number of heroes in the catalog.
    /// </summary>
    int HeroesCount { get; }

    /// <summary>
    ///     Rebuilds pre-baked byte buffers atomically in response to configuration updates.
    /// </summary>
    void Refresh();
}
