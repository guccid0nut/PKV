// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     Cryptographic signature verification and canonical transcript builder for GEMINI/KONGOR/PKV CDN manifests.
///     Enforces ECDSA P-256 (SHA-256) signatures over a deterministic UTF-8 transcript to guarantee integrity
///     and origin authenticity across all platforms.
/// </summary>
public static class ManifestSignatureVerifier
{
    /// <summary>
    ///     The header prefix for the canonical manifest transcript under PKV v5.
    /// </summary>
    public const string TranscriptHeader = "PKV-CDN-MANIFEST-V5";

    /// <summary>
    ///     Default production ECDSA P-256 public key X-coordinate (32 bytes hex).
    /// </summary>
    public const string DefaultPublicKeyXHex = "05b1399c6fbd80f20c579b81489f6617d42e4d6ad41285e1c90dc4c1f2563536";

    /// <summary>
    ///     Default production ECDSA P-256 public key Y-coordinate (32 bytes hex).
    /// </summary>
    public const string DefaultPublicKeyYHex = "1d946704f9f2a943b2cb3772c0d044346a70872a2dada0cc31d56d2a69d76de5";

    /// <summary>
    ///     Offline recovery ECDSA P-256 public key X-coordinate (32 bytes hex).
    /// </summary>
    public const string RecoveryPublicKeyXHex = "4a13a468e8a367c5e9137e978556ab774f600164e29e2dab93ac0b14eee5503a";

    /// <summary>
    ///     Offline recovery ECDSA P-256 public key Y-coordinate (32 bytes hex).
    /// </summary>
    public const string RecoveryPublicKeyYHex = "d19ca58a1b0cd7bb6df49a8fc25aef53fa8f52d1f6d0fe3bec8e22b40a314bf8";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static void ValidateTranscriptSafePath(string path, string collectionName)
    {
        if (path.IndexOfAny(['\t', '\r', '\n', '\0', '|']) >= 0)
        {
            throw new SecurityException($"Control character or transcript delimiter forbidden in {collectionName} path: '{path}'");
        }
    }

    private static void ValidateTranscriptSafeScalar(string? value, string fieldName)
    {
        if (!string.IsNullOrEmpty(value) && value.IndexOfAny(['\t', '\r', '\n', '\0']) >= 0)
        {
            throw new SecurityException($"Control character forbidden in manifest {fieldName}.");
        }
    }

    /// <summary>
    ///     Builds the deterministic, line-based canonical UTF-8 transcript for a manifest.
    /// </summary>
    /// <param name="manifest">The manifest to canonicalize.</param>
    /// <returns>The canonical byte array representation.</returns>
    public static byte[] BuildCanonicalTranscript(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ValidateTranscriptSafeScalar(manifest.Protocol, nameof(manifest.Protocol));
        ValidateTranscriptSafeScalar(manifest.Version, nameof(manifest.Version));
        ValidateTranscriptSafeScalar(manifest.Mode, nameof(manifest.Mode));
        ValidateTranscriptSafeScalar(manifest.HashAlgorithm, nameof(manifest.HashAlgorithm));
        ValidateTranscriptSafeScalar(manifest.ObjectLayout, nameof(manifest.ObjectLayout));

        if (manifest.ExcludeFromSource is not null)
        {
            foreach (string p in manifest.ExcludeFromSource)
            {
                ValidateTranscriptSafePath(p, nameof(manifest.ExcludeFromSource));
            }
        }

        if (manifest.ExcludeFromTarget is not null)
        {
            foreach (string p in manifest.ExcludeFromTarget)
            {
                ValidateTranscriptSafePath(p, nameof(manifest.ExcludeFromTarget));
            }
        }

        if (manifest.ExecutablePaths is not null)
        {
            foreach (string p in manifest.ExecutablePaths)
            {
                ValidateTranscriptSafePath(p, nameof(manifest.ExecutablePaths));
            }
        }

        if (manifest.Remove is not null)
        {
            foreach (string p in manifest.Remove)
            {
                ValidateTranscriptSafePath(p, nameof(manifest.Remove));
            }
        }

        if (manifest.Files is not null)
        {
            foreach (string p in manifest.Files.Keys)
            {
                ValidateTranscriptSafePath(p, nameof(manifest.Files));
            }
        }

        StringBuilder sb = new();
        sb.Append(TranscriptHeader).Append('\n');
        sb.Append("protocol=").Append(manifest.Protocol ?? string.Empty).Append('\n');
        sb.Append("version=").Append(manifest.Version ?? string.Empty).Append('\n');
        sb.Append("sequence=").Append(manifest.Sequence).Append('\n');
        sb.Append("mode=").Append(string.IsNullOrWhiteSpace(manifest.Mode) ? "mirror" : manifest.Mode.Trim().ToLowerInvariant()).Append('\n');
        sb.Append("hashAlgorithm=").Append((manifest.HashAlgorithm ?? string.Empty).Trim().ToUpperInvariant()).Append('\n');
        sb.Append("objectLayout=").Append((manifest.ObjectLayout ?? string.Empty).Trim().ToLowerInvariant()).Append('\n');

        string excludeFromSource = string.Join("|", (manifest.ExcludeFromSource ?? [])
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal));
        sb.Append("excludeFromSource=").Append(excludeFromSource).Append('\n');

        string excludeFromTarget = string.Join("|", (manifest.ExcludeFromTarget ?? [])
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal));
        sb.Append("excludeFromTarget=").Append(excludeFromTarget).Append('\n');

        string executablePaths = string.Join("|", (manifest.ExecutablePaths ?? [])
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal));
        sb.Append("executablePaths=").Append(executablePaths).Append('\n');

        string remove = string.Join("|", (manifest.Remove ?? [])
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal));
        sb.Append("remove=").Append(remove).Append('\n');

        sb.Append("files=").Append('\n');

        if (manifest.Files is not null)
        {
            var orderedEntries = manifest.Files
                .OrderBy(kvp => kvp.Key.Replace('\\', '/'), StringComparer.Ordinal);

            foreach (var kvp in orderedEntries)
            {
                ValidateTranscriptSafeScalar(kvp.Value?.Hash, "Files[].Hash");
                string normKey = kvp.Key.Replace('\\', '/');
                long size = kvp.Value?.Size ?? 0;
                string hash = (kvp.Value?.Hash ?? string.Empty).Trim().ToLowerInvariant();
                sb.Append(normKey).Append('\t').Append(size).Append('\t').Append(hash).Append('\n');
            }
        }

        return Utf8NoBom.GetBytes(sb.ToString());
    }

    /// <summary>
    ///     Verifies the ECDSA P-256 signature of a manifest against its canonical transcript.
    /// </summary>
    /// <param name="manifest">The manifest containing the signature to verify.</param>
    /// <param name="failureReason">Outputs the reason for failure if verification fails.</param>
    /// <param name="publicKeyXHexOverride">Optional override for the public key X-coordinate hex string.</param>
    /// <param name="publicKeyYHexOverride">Optional override for the public key Y-coordinate hex string.</param>
    /// <returns><c>true</c> if the signature is valid; otherwise <c>false</c>.</returns>
    public static bool VerifyManifest(
        Manifest manifest,
        out string? failureReason,
        string? publicKeyXHexOverride = null,
        string? publicKeyYHexOverride = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Sequence < 0)
        {
            failureReason = "Manifest sequence must be non-negative.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            failureReason = "Manifest signature is missing or empty.";
            return false;
        }

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException ex)
        {
            failureReason = $"Manifest signature is not valid Base64: {ex.Message}";
            return false;
        }

        if (sigBytes.Length != 64)
        {
            failureReason = $"Invalid signature length: {sigBytes.Length} bytes. Expected 64 bytes IEEE P1363 (r || s).";
            return false;
        }

        byte[] transcript = BuildCanonicalTranscript(manifest);

        if (!string.IsNullOrWhiteSpace(publicKeyXHexOverride))
        {
            string overrideX = publicKeyXHexOverride.Trim();
            string overrideY = string.IsNullOrWhiteSpace(publicKeyYHexOverride) ? DefaultPublicKeyYHex : publicKeyYHexOverride.Trim();
            return TryVerifyWithCoordinates(transcript, sigBytes, overrideX, overrideY, out failureReason);
        }

        // Try primary production key first
        if (TryVerifyWithCoordinates(transcript, sigBytes, DefaultPublicKeyXHex, DefaultPublicKeyYHex, out failureReason))
        {
            return true;
        }

        // Fallback to offline recovery key
        if (TryVerifyWithCoordinates(transcript, sigBytes, RecoveryPublicKeyXHex, RecoveryPublicKeyYHex, out _))
        {
            Trace.WriteLine("[ManifestSignatureVerifier] Manifest verified using offline recovery key (recovery-2026).");
            failureReason = null;
            return true;
        }

        failureReason = "ECDSA P-256 signature verification failed over canonical transcript (tried primary and offline recovery keys).";
        return false;
    }

    private static bool TryVerifyWithCoordinates(
        byte[] transcript,
        byte[] sigBytes,
        string xHex,
        string yHex,
        out string? failureReason)
    {
        byte[] xBytes;
        byte[] yBytes;
        try
        {
            xBytes = Convert.FromHexString(xHex.Trim());
            yBytes = Convert.FromHexString(yHex.Trim());
        }
        catch (FormatException ex)
        {
            failureReason = $"Public key coordinates are not valid hex: {ex.Message}";
            return false;
        }

        if (xBytes.Length != 32 || yBytes.Length != 32)
        {
            failureReason = $"Invalid public key coordinates length: X={xBytes.Length}, Y={yBytes.Length}. Expected 32 bytes each.";
            return false;
        }

        try
        {
            using ECDsa ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = xBytes,
                    Y = yBytes
                }
            });

            bool verified = ecdsa.VerifyData(transcript, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified)
            {
                failureReason = "ECDSA P-256 signature verification failed over canonical transcript.";
                return false;
            }

            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Cryptographic verification threw an exception: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    ///     Signs the canonical transcript of a manifest using ECDSA P-256 and returns the Base64 signature.
    /// </summary>
    /// <param name="manifest">The manifest to sign.</param>
    /// <param name="privateKeyD">The 32-byte private key scalar D.</param>
    /// <param name="publicKeyX">The 32-byte public key X coordinate.</param>
    /// <param name="publicKeyY">The 32-byte public key Y coordinate.</param>
    /// <returns>Base64-encoded IEEE P1363 signature string.</returns>
    public static string SignManifest(
        Manifest manifest,
        ReadOnlySpan<byte> privateKeyD,
        ReadOnlySpan<byte> publicKeyX,
        ReadOnlySpan<byte> publicKeyY)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using ECDsa ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKeyD.ToArray(),
            Q = new ECPoint
            {
                X = publicKeyX.ToArray(),
                Y = publicKeyY.ToArray()
            }
        });

        return SignManifest(manifest, ecdsa);
    }

    /// <summary>
    ///     Signs the canonical transcript of a manifest using an existing <see cref="ECDsa" /> instance.
    /// </summary>
    /// <param name="manifest">The manifest to sign.</param>
    /// <param name="ecdsa">The ECDsa algorithm configured with private key.</param>
    /// <returns>Base64-encoded IEEE P1363 signature string.</returns>
    public static string SignManifest(Manifest manifest, ECDsa ecdsa)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ecdsa);

        byte[] transcript = BuildCanonicalTranscript(manifest);
        byte[] sigBytes = ecdsa.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(sigBytes);
    }
}
