// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Vfs;

namespace Kongor.Modding.Serialization;

/// <summary>
///     Source-generated JsonSerializerContext providing compile-time, zero-reflection
///     Native AOT serialization for modding manifests, rollback journals, and scraper models.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JournalEntry))]
[JsonSerializable(typeof(List<CommunityModEntry>))]
[JsonSerializable(typeof(CommunityModEntry[]))]
[JsonSerializable(typeof(IEnumerable<CommunityModEntry>))]
[JsonSerializable(typeof(TrelloBoardResponse))]
public partial class ModdingJsonSerializerContext : JsonSerializerContext
{
}
