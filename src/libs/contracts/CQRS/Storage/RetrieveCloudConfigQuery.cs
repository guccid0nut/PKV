// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Storage;

/// <summary>
///     Query to retrieve a player's cloud configuration zip archive.
/// </summary>
public record RetrieveCloudConfigQuery(int AccountId, string? Bucket) : IRequest<RetrieveCloudConfigResultDto>;

/// <summary>
///     Result containing the downloaded or fallback zip archive bytes.
/// </summary>
public record RetrieveCloudConfigResultDto(
    bool Found,
    byte[] ZipBytes,
    string ContentType,
    string FileName);
