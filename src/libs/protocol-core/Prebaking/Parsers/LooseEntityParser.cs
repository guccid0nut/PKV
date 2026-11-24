// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Prebaking.Parsers;

public sealed class LooseEntityParser(ILogger<EntityFileParser>? logger = null)
{
    private readonly EntityFileParser _inner = new(logger);

    public Task<object?> ParseEntityFileAsync(string filePath, CancellationToken ct = default)
    {
        return _inner.ParseEntityFileAsync(filePath, ct);
    }

    public Task<object?> ParseEntityStreamAsync(Stream stream, string sourcePath, CancellationToken ct = default)
    {
        return _inner.ParseEntityStreamAsync(stream, sourcePath, ct);
    }
}
