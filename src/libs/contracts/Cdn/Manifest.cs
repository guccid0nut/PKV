// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     The parsed content manifest published by the GEMINI/PKV distribution pipeline.
///     Lists every file the bucket publishes along with the size and hash needed to verify each one,
///     plus the remote-side and local-side exclusion lists that govern what the synchronisation may transfer and
///     overwrite.
/// </summary>
public sealed record Manifest
{
    public const string PkvProtocolV5 = "pkv-v5";
    public const string PkvHeaderName = "X-Kongor-Update-Protocol";

    /// <summary>
    ///     The PKV distribution protocol identifier (e.g. "pkv-v5").
    /// </summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    /// <summary>
    ///     The manifest version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Monotonic update sequence counter for replay and downgrade prevention.
    /// </summary>
    public long Sequence { get; init; } = 0;

    /// <summary>
    ///     The algorithm used for the <see cref="ManifestEntry.Hash" /> values.
    /// </summary>
    public required string HashAlgorithm { get; init; }

    /// <summary>
    ///     Remote-side glob patterns for files that must not be downloaded from the bucket, such as the manifest itself.
    /// </summary>
    public required IReadOnlyList<string> ExcludeFromSource { get; init; }

    /// <summary>
    ///     Local-side glob patterns for files that must not be changed in the target directory, such as the launcher's own
    ///     files.
    /// </summary>
    public required IReadOnlyList<string> ExcludeFromTarget { get; init; }

    /// <summary>
    ///     Every file the manifest declares, keyed by forward-slash relative path.
    /// </summary>
    public required IReadOnlyDictionary<string, ManifestEntry> Files { get; init; }

    /// <summary>
    ///     The distribution mode: "mirror" or "overlay". Default is empty to disallow implicit mirror assumptions.
    /// </summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>
    ///     Optional object layout strategy, such as "sha256" for CAS-addressed objects.
    /// </summary>
    public string? ObjectLayout { get; init; }

    /// <summary>
    ///     Relative paths of files to be removed during rollback or uninstallation.
    /// </summary>
    public IReadOnlyList<string> Remove { get; init; } = [];

    /// <summary>
    ///     Relative paths of executable files that require POSIX execute (+x) permissions.
    /// </summary>
    public IReadOnlyList<string> ExecutablePaths { get; init; } = [];

    /// <summary>
    ///     The Base64-encoded ECDSA P-256 signature for this manifest over its canonical transcript.
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    ///     Gets a value indicating whether this manifest declares the PKV v5 protocol.
    /// </summary>
    [JsonIgnore]
    public bool IsPkvV5Protocol => string.Equals(Protocol, PkvProtocolV5, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets a value indicating whether this manifest is applied as an additive overlay.
    /// </summary>
    [JsonIgnore]
    public bool IsOverlay => string.Equals(Mode, "overlay", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets a value indicating whether content is stored in CAS layout (objects/ab/{sha256}).
    /// </summary>
    [JsonIgnore]
    public bool UsesSha256ObjectLayout => string.Equals(ObjectLayout, "sha256", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     Source-generated serialisation metadata for <see cref="Manifest" />.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Manifest))]
public sealed partial class ManifestJSONContext : JsonSerializerContext;

/// <summary>
///     Serializable document representation for manifest json payloads.
/// </summary>
public sealed class ManifestDocument
{
    public string Version { get; set; } = string.Empty;
    public string Channel { get; set; } = "release";
    public Dictionary<string, ManifestEntry> Files { get; set; } = [];
}

/// <summary>
///     Helper parser service for deserializing manifests safely with AOT contexts.
/// </summary>
internal static class ManifestParserService
{
    public static Manifest? Parse(string json)
    {
        return JsonSerializer.Deserialize(json, ManifestJSONContext.Default.Manifest);
    }
}
