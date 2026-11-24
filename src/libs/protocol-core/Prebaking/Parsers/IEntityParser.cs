// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Prebaking.Parsers;

public interface IEntityParser<T>
{
    Task<T> ParseFileAsync(string filePath, CancellationToken ct = default);
    Task<T> ParseStreamAsync(Stream stream, CancellationToken ct = default);
}
