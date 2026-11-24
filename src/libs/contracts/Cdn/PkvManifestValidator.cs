// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     Identifies the specific failure code when a manifest fails PKV v5 validation.
/// </summary>
public enum PkvValidationFailureCode
{
    None = 0,
    NullManifest,
    MissingOrUnsupportedProtocol,
    LegacyPreV5Version,
    LegacyMirrorModeForbidden,
    NonCasObjectLayout,
    LegacyHashAlgorithm,
    MissingPkvSequence,
    UntrustedOrMissingSignature
}

/// <summary>
///     Represents the result of a PKV v5 manifest validation attempt with actionable diagnostics.
/// </summary>
public sealed record PkvValidationResult(
    bool IsValid,
    PkvValidationFailureCode FailureCode,
    string? Reason,
    string? RemediationCommand)
{
    public static PkvValidationResult Success() =>
        new(true, PkvValidationFailureCode.None, null, null);

    public static PkvValidationResult Fail(PkvValidationFailureCode code, string reason, string? remediation = null) =>
        new(false, code, reason, remediation);
}

/// <summary>
///     Pure, fail-closed validator enforcing the PKV v5 manifest and distribution contract.
///     Guarantees that only content-addressed, additive, cryptographically signed v5 updates
///     from authorized Project KONGOR PKV publishers are processed.
/// </summary>
public static class PkvManifestValidator
{
    /// <summary>
    ///     Validates a manifest against all PKV v5 contract invariants.
    /// </summary>
    /// <param name="manifest">The candidate manifest to inspect.</param>
    /// <param name="requireSignature">Whether an ECDSA P-256 signature is strictly required.</param>
    /// <param name="publicKeyXHexOverride">Optional test override for ECDSA P-256 public key X.</param>
    /// <param name="publicKeyYHexOverride">Optional test override for ECDSA P-256 public key Y.</param>
    /// <returns>A <see cref="PkvValidationResult" /> indicating success or failure reason.</returns>
    public static PkvValidationResult Verify(
        Manifest? manifest,
        bool requireSignature = true,
        string? publicKeyXHexOverride = null,
        string? publicKeyYHexOverride = null)
    {
        if (manifest is null)
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.NullManifest,
                "Manifest is null.",
                "Verify CDN endpoint or manifest payload URL.");
        }

        if (!manifest.IsPkvV5Protocol)
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.MissingOrUnsupportedProtocol,
                $"Manifest protocol '{manifest.Protocol}' is not valid PKV v5 (expected '{Manifest.PkvProtocolV5}'). Legacy or foreign update protocol blocked.",
                "Ensure updates are published via 'scripts/release/publish_cdn_update.ps1 -Version <version>'.");
        }

        if (!IsSupportedMajorVersion(manifest.Version))
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.LegacyPreV5Version,
                $"Manifest version '{manifest.Version}' is pre-v5 (major version < 5). Legacy 4.x updates are blocked.",
                "Upgrade target server/package to Project KONGOR v5+.");
        }

        if (!manifest.IsOverlay)
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.LegacyMirrorModeForbidden,
                $"Manifest distribution mode '{manifest.Mode}' is forbidden. Only 'overlay' mode is allowed under PKV v5 (mirror mode and full-tree deletions are blocked).",
                "Publish package with mode=overlay via 'scripts/release/publish_cdn_update.ps1'.");
        }

        if (!manifest.UsesSha256ObjectLayout)
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.NonCasObjectLayout,
                $"Manifest object layout '{manifest.ObjectLayout}' is invalid. PKV v5 requires content-addressable storage ('sha256').",
                "Ensure objects are published to objects/ab/{sha256} via 'scripts/release/publish_cdn_update.ps1'.");
        }

        if (!string.Equals(manifest.HashAlgorithm, "SHA-256", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(manifest.HashAlgorithm, "SHA256", StringComparison.OrdinalIgnoreCase))
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.LegacyHashAlgorithm,
                $"Manifest hash algorithm '{manifest.HashAlgorithm}' is unsupported. Expected 'SHA-256'.",
                "Use SHA-256 content hashes.");
        }

        if (manifest.Sequence < 1)
        {
            return PkvValidationResult.Fail(
                PkvValidationFailureCode.MissingPkvSequence,
                $"Manifest sequence '{manifest.Sequence}' is invalid. PKV v5 requires monotonic sequence >= 1.",
                "Pass -Sequence or allow sequence auto-increment in 'scripts/release/publish_cdn_update.ps1'.");
        }

        if (requireSignature)
        {
            if (!ManifestSignatureVerifier.VerifyManifest(manifest, out string? failureReason, publicKeyXHexOverride, publicKeyYHexOverride))
            {
                return PkvValidationResult.Fail(
                    PkvValidationFailureCode.UntrustedOrMissingSignature,
                    $"Manifest signature verification failed: {failureReason}",
                    "Sign manifest using authorized PKV ECDSA P-256 keypair.");
            }
        }

        return PkvValidationResult.Success();
    }

    /// <summary>
    ///     Checks whether a version string denotes a supported Project KONGOR major version (&gt;= 5).
    /// </summary>
    public static bool IsSupportedMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string trimmed = version.Trim().TrimStart('v', 'V');
        int dotIdx = trimmed.IndexOf('.');
        string majorStr = dotIdx > 0 ? trimmed[..dotIdx] : trimmed;
        if (int.TryParse(majorStr, out int major))
        {
            return major >= 5;
        }

        return trimmed.StartsWith("5.", StringComparison.OrdinalIgnoreCase);
    }
}
