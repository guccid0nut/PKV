// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Preload resource reference for pre-warming client assets.
/// </summary>
[MemoryPackable]
public partial class PreloadResourceDto
{
    public string ResourcePath { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string HeroOrEntityName { get; set; } = string.Empty;
}
