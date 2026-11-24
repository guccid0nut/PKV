// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PatchManifestModels.cs
// System: Kongor Protocol Core - CDN Client Patch Manifest Data Contracts
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Text.Json.Serialization;

namespace ASPIRE.Common.Patching;

/// <summary>
///     Represents an individual file entry in a game version release manifest.
/// </summary>
public sealed record PatchFileEntry(
    [property: JsonPropertyName("path")] string RelativePath,
    [property: JsonPropertyName("size")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("url")] string? DownloadUrl = null);

/// <summary>
///     Represents a precomputed binary delta patch bridging a specific source file version to a target file version.
/// </summary>
public sealed record PatchDeltaEntry(
    [property: JsonPropertyName("path")] string RelativePath,
    [property: JsonPropertyName("src_sha256")] string SourceSha256,
    [property: JsonPropertyName("tgt_sha256")] string TargetSha256,
    [property: JsonPropertyName("patch_size")] long PatchSizeBytes,
    [property: JsonPropertyName("patch_sha256")] string PatchSha256,
    [property: JsonPropertyName("delta_url")] string DeltaUrl,
    [property: JsonPropertyName("compression")] string Compression = "zstd");

/// <summary>
///     Cryptographically signed game release manifest enumerating all current game files
///     and pre-calculated differential delta patch routes.
/// </summary>
public sealed record PatchManifest(
    [property: JsonPropertyName("version")] string GameVersion,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("files")] IReadOnlyList<PatchFileEntry> Files,
    [property: JsonPropertyName("deltas")] IReadOnlyList<PatchDeltaEntry> Deltas,
    [property: JsonPropertyName("signature")] string? Signature = null,
    [property: JsonPropertyName("signer_public_key")] string? SignerPublicKey = null);
