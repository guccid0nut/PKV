// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Prebaking.Parsers;

namespace ASPIRE.Common.Prebaking;

public sealed class EntityHarvestEngine(
    MasterXmlParser? masterXmlParser = null,
    ILogger<EntityHarvestEngine>? logger = null)
{
    private readonly ILogger<EntityHarvestEngine>? _logger = logger;
    private readonly MasterXmlParser _masterXmlParser = masterXmlParser ?? new MasterXmlParser();

    public async Task<EntityCacheContainer> HarvestAllAsync(
        string clientDir,
        string? sharedDir = null,
        CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _logger?.LogInformation("Starting Entity Harvesting pipeline: clientDir='{ClientDir}', sharedDir='{SharedDir}'",
            clientDir, sharedDir);

        EntityCacheContainer container =
            await _masterXmlParser.ParseMasterDataAsync(clientDir, sharedDir, ct).ConfigureAwait(false);

        sw.Stop();
        _logger?.LogInformation("Harvested {Total} entities across all categories in {ElapsedMs} ms",
            container.TotalEntityCount, sw.ElapsedMilliseconds);
        return container;
    }
}
