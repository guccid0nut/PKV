// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace NEXUS.Contracts.Cdn;

/// <summary>
/// The four top-level security roles defined by The Update Framework (TUF) v1.0 specification.
/// </summary>
public enum TufRole
{
    Root = 0,
    Timestamp = 1,
    Snapshot = 2,
    Targets = 3
}

/// <summary>
/// ECDSA P-256 verification key registered in a TUF v1.0 root metadata document.
/// </summary>
public sealed record TufKey(
    string KeyId,
    string PublicKeyXHex,
    string PublicKeyYHex,
    string KeyType = "ecdsa-sha2-nistp256")
{
    /// <summary>
    /// Computes a deterministic SHA-256 key identifier from canonical ECDSA P-256 public coordinates.
    /// </summary>
    public static string ComputeKeyId(string publicKeyXHex, string publicKeyYHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyXHex);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyYHex);

        string canonical = $"ecdsa-sha2-nistp256:{publicKeyXHex.Trim().ToLowerInvariant()}:{publicKeyYHex.Trim().ToLowerInvariant()}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Exports a <see cref="TufKey" /> from an <see cref="ECDsa" /> P-256 key instance.
    /// </summary>
    public static TufKey FromECDsa(ECDsa ecdsa, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is null || parameters.Q.Y is null || parameters.Q.X.Length != 32 || parameters.Q.Y.Length != 32)
        {
            throw new CryptographicException("TUF key export requires an ECDSA P-256 (32-byte X/Y) key.");
        }

        string xHex = Convert.ToHexStringLower(parameters.Q.X);
        string yHex = Convert.ToHexStringLower(parameters.Q.Y);
        string resolvedId = string.IsNullOrWhiteSpace(keyId) ? ComputeKeyId(xHex, yHex) : keyId.Trim();
        return new TufKey(resolvedId, xHex, yHex);
    }
}

/// <summary>
/// Authorizes a set of key IDs and minimum unique signature threshold (<c>m-of-n</c>) for a TUF role.
/// </summary>
public sealed record TufRoleDefinition(
    int Threshold,
    IReadOnlyList<string> KeyIds);

/// <summary>
/// Detached IEEE P1363 ECDSA P-256 signature bound to a specific TUF <see cref="KeyId" />.
/// </summary>
public sealed record TufSignature(
    string KeyId,
    string SignatureBase64);

/// <summary>
/// Signed TUF metadata envelope pairing a role payload with one or more detached signatures.
/// </summary>
public sealed record TufSignedEnvelope<TMetadata>(
    TMetadata Signed,
    IReadOnlyList<TufSignature> Signatures);

/// <summary>
/// Hash, byte length, and monotonic version reference to a downstream TUF role metadata file.
/// </summary>
public sealed record TufMetaFileInfo(
    int Version,
    long Length,
    string Sha256Hex);

/// <summary>
/// TUF v1.0 Root role metadata defining trusted keys and <c>m-of-n</c> thresholds for all four roles.
/// </summary>
public sealed record TufRootMetadata(
    int Version,
    DateTimeOffset ExpiresUtc,
    bool ConsistentSnapshot,
    IReadOnlyDictionary<string, TufKey> Keys,
    IReadOnlyDictionary<TufRole, TufRoleDefinition> Roles,
    string SpecVersion = "1.0.31");

/// <summary>
/// TUF v1.0 Timestamp role metadata providing short-lived freshness and pinning the latest Snapshot digest.
/// </summary>
public sealed record TufTimestampMetadata(
    int Version,
    DateTimeOffset ExpiresUtc,
    TufMetaFileInfo SnapshotMeta,
    string SpecVersion = "1.0.31");

/// <summary>
/// TUF v1.0 Snapshot role metadata preventing mix-and-match attacks by pinning all Targets metadata digests.
/// </summary>
public sealed record TufSnapshotMetadata(
    int Version,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, TufMetaFileInfo> Meta,
    string SpecVersion = "1.0.31");

/// <summary>
/// Signed target artifact metadata including optional delta patch hash-chain bindings (<c>BaseSha256Hex</c> -> <c>DeltaSha256Hex</c> -> <c>Sha256Hex</c>).
/// </summary>
public sealed record TufTargetFileMetadata(
    long Length,
    string Sha256Hex,
    string? BaseSha256Hex = null,
    string? DeltaSha256Hex = null,
    long? DeltaLength = null,
    string? DeltaPath = null);

/// <summary>
/// TUF v1.0 Targets role metadata listing cryptographically verified update targets and delta chains.
/// </summary>
public sealed record TufTargetsMetadata(
    int Version,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, TufTargetFileMetadata> Targets,
    string SpecVersion = "1.0.31");

/// <summary>
/// Client-pinned trust state storing the current trusted Root envelope and highest verified role versions
/// for strict monotonic rollback protection.
/// </summary>
public sealed record TufTrustedState(
    TufSignedEnvelope<TufRootMetadata> TrustedRoot,
    int TimestampVersion = 0,
    int SnapshotVersion = 0,
    int TargetsVersion = 0);

/// <summary>
/// Structured verification outcome returned by <see cref="TufMetadataVerifier.VerifyUpdateBundle" />.
/// </summary>
public sealed record TufVerificationResult(
    bool IsValid,
    string? FailureCode,
    string? FailureReason,
    TufTrustedState? UpdatedState,
    TufTargetsMetadata? VerifiedTargets)
{
    public static TufVerificationResult Failed(string code, string reason)
        => new(false, code, reason, null, null);

    public static TufVerificationResult Succeeded(TufTrustedState updatedState, TufTargetsMetadata verifiedTargets)
        => new(true, null, null, updatedState, verifiedTargets);
}

/// <summary>
/// Native-AOT-safe The Update Framework (TUF) v1.0 4-role metadata verifier using ECDSA P-256 (SHA-256).
/// Enforces <c>m-of-n</c> unique-key thresholds, dual-signed sequential root rotation, monotonic version
/// rollback protection, UTC expiry freeze-attack protection, and snapshot/targets hash chaining.
/// </summary>
public static class TufMetadataVerifier
{
    public const string TargetsMetaFileName = "targets.json";
    public const string TranscriptPrefix = "PKV-TUF-V1";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Creates an ECDSA P-256 IEEE P1363 signature over canonical role transcript bytes.
    /// </summary>
    public static TufSignature CreateSignature(byte[] canonicalPayload, string keyId, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(signingKey);

        byte[] sig = signingKey.SignData(canonicalPayload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new TufSignature(keyId.Trim(), Convert.ToBase64String(sig));
    }

    public static TufSignedEnvelope<TufRootMetadata> SignRoot(
        TufRootMetadata root,
        params (string KeyId, ECDsa Key)[] signers)
    {
        byte[] canonical = BuildCanonicalRootBytes(root);
        return new TufSignedEnvelope<TufRootMetadata>(root, [.. signers.Select(s => CreateSignature(canonical, s.KeyId, s.Key))]);
    }

    public static TufSignedEnvelope<TufTimestampMetadata> SignTimestamp(
        TufTimestampMetadata timestamp,
        params (string KeyId, ECDsa Key)[] signers)
    {
        byte[] canonical = BuildCanonicalTimestampBytes(timestamp);
        return new TufSignedEnvelope<TufTimestampMetadata>(timestamp, [.. signers.Select(s => CreateSignature(canonical, s.KeyId, s.Key))]);
    }

    public static TufSignedEnvelope<TufSnapshotMetadata> SignSnapshot(
        TufSnapshotMetadata snapshot,
        params (string KeyId, ECDsa Key)[] signers)
    {
        byte[] canonical = BuildCanonicalSnapshotBytes(snapshot);
        return new TufSignedEnvelope<TufSnapshotMetadata>(snapshot, [.. signers.Select(s => CreateSignature(canonical, s.KeyId, s.Key))]);
    }

    public static TufSignedEnvelope<TufTargetsMetadata> SignTargets(
        TufTargetsMetadata targets,
        params (string KeyId, ECDsa Key)[] signers)
    {
        byte[] canonical = BuildCanonicalTargetsBytes(targets);
        return new TufSignedEnvelope<TufTargetsMetadata>(targets, [.. signers.Select(s => CreateSignature(canonical, s.KeyId, s.Key))]);
    }

    /// <summary>
    /// Computes <see cref="TufMetaFileInfo" /> (version, byte length, lowercase SHA-256) for a snapshot payload.
    /// </summary>
    public static TufMetaFileInfo ComputeSnapshotMetaInfo(TufSnapshotMetadata snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] canonical = BuildCanonicalSnapshotBytes(snapshot);
        return new TufMetaFileInfo(snapshot.Version, canonical.LongLength, Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    /// <summary>
    /// Computes <see cref="TufMetaFileInfo" /> (version, byte length, lowercase SHA-256) for a targets payload.
    /// </summary>
    public static TufMetaFileInfo ComputeTargetsMetaInfo(TufTargetsMetadata targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        byte[] canonical = BuildCanonicalTargetsBytes(targets);
        return new TufMetaFileInfo(targets.Version, canonical.LongLength, Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    /// <summary>
    /// Builds deterministic, reflection-free canonical UTF-8 bytes for <see cref="TufRootMetadata" />.
    /// </summary>
    public static byte[] BuildCanonicalRootBytes(TufRootMetadata root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateSafeScalar(root.SpecVersion, nameof(root.SpecVersion));

        StringBuilder sb = new(512);
        sb.Append(TranscriptPrefix).Append("|root\n");
        sb.Append("spec=").Append(root.SpecVersion).Append('\n');
        sb.Append("version=").Append(root.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("expires=").Append(FormatUtc(root.ExpiresUtc)).Append('\n');
        sb.Append("consistent_snapshot=").Append(root.ConsistentSnapshot ? "true" : "false").Append('\n');

        sb.Append("keys=\n");
        if (root.Keys is not null)
        {
            foreach (var kvp in root.Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                ValidateSafeScalar(kvp.Key, "Root.Keys.Key");
                ValidateSafeScalar(kvp.Value.KeyId, "Root.Key.KeyId");
                ValidateSafeScalar(kvp.Value.KeyType, "Root.Key.KeyType");
                ValidateSafeScalar(kvp.Value.PublicKeyXHex, "Root.Key.X");
                ValidateSafeScalar(kvp.Value.PublicKeyYHex, "Root.Key.Y");
                sb.Append(kvp.Key)
                  .Append('\t').Append(kvp.Value.KeyId)
                  .Append('\t').Append(kvp.Value.KeyType.Trim().ToLowerInvariant())
                  .Append('\t').Append(kvp.Value.PublicKeyXHex.Trim().ToLowerInvariant())
                  .Append('\t').Append(kvp.Value.PublicKeyYHex.Trim().ToLowerInvariant())
                  .Append('\n');
            }
        }

        sb.Append("roles=\n");
        if (root.Roles is not null)
        {
            foreach (var kvp in root.Roles.OrderBy(r => (int)r.Key))
            {
                string roleName = kvp.Key.ToString().ToLowerInvariant();
                string joinedKeyIds = string.Join(",", (kvp.Value.KeyIds ?? [])
                    .Select(id =>
                    {
                        ValidateSafeScalar(id, "Root.Role.KeyId");
                        return id.Trim();
                    })
                    .OrderBy(id => id, StringComparer.Ordinal));
                sb.Append(roleName)
                  .Append('\t').Append(kvp.Value.Threshold.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(joinedKeyIds)
                  .Append('\n');
            }
        }

        return Utf8NoBom.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds deterministic, reflection-free canonical UTF-8 bytes for <see cref="TufTimestampMetadata" />.
    /// </summary>
    public static byte[] BuildCanonicalTimestampBytes(TufTimestampMetadata timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(timestamp.SnapshotMeta);
        ValidateSafeScalar(timestamp.SpecVersion, nameof(timestamp.SpecVersion));
        ValidateSafeScalar(timestamp.SnapshotMeta.Sha256Hex, "Timestamp.SnapshotMeta.Sha256Hex");

        StringBuilder sb = new(256);
        sb.Append(TranscriptPrefix).Append("|timestamp\n");
        sb.Append("spec=").Append(timestamp.SpecVersion).Append('\n');
        sb.Append("version=").Append(timestamp.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("expires=").Append(FormatUtc(timestamp.ExpiresUtc)).Append('\n');
        sb.Append("snapshot.json\t")
          .Append(timestamp.SnapshotMeta.Version.ToString(CultureInfo.InvariantCulture)).Append('\t')
          .Append(timestamp.SnapshotMeta.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
          .Append(timestamp.SnapshotMeta.Sha256Hex.Trim().ToLowerInvariant()).Append('\n');
        return Utf8NoBom.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds deterministic, reflection-free canonical UTF-8 bytes for <see cref="TufSnapshotMetadata" />.
    /// </summary>
    public static byte[] BuildCanonicalSnapshotBytes(TufSnapshotMetadata snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSafeScalar(snapshot.SpecVersion, nameof(snapshot.SpecVersion));

        StringBuilder sb = new(256);
        sb.Append(TranscriptPrefix).Append("|snapshot\n");
        sb.Append("spec=").Append(snapshot.SpecVersion).Append('\n');
        sb.Append("version=").Append(snapshot.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("expires=").Append(FormatUtc(snapshot.ExpiresUtc)).Append('\n');
        sb.Append("meta=\n");
        if (snapshot.Meta is not null)
        {
            foreach (var kvp in snapshot.Meta.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                ValidateSafeScalar(kvp.Key, "Snapshot.Meta.Key");
                ValidateSafeScalar(kvp.Value.Sha256Hex, "Snapshot.Meta.Sha256Hex");
                sb.Append(kvp.Key)
                  .Append('\t').Append(kvp.Value.Version.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(kvp.Value.Length.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(kvp.Value.Sha256Hex.Trim().ToLowerInvariant())
                  .Append('\n');
            }
        }

        return Utf8NoBom.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds deterministic, reflection-free canonical UTF-8 bytes for <see cref="TufTargetsMetadata" />.
    /// </summary>
    public static byte[] BuildCanonicalTargetsBytes(TufTargetsMetadata targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ValidateSafeScalar(targets.SpecVersion, nameof(targets.SpecVersion));

        StringBuilder sb = new(512);
        sb.Append(TranscriptPrefix).Append("|targets\n");
        sb.Append("spec=").Append(targets.SpecVersion).Append('\n');
        sb.Append("version=").Append(targets.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("expires=").Append(FormatUtc(targets.ExpiresUtc)).Append('\n');
        sb.Append("targets=\n");
        if (targets.Targets is not null)
        {
            foreach (var kvp in targets.Targets.OrderBy(k => k.Key.Replace('\\', '/'), StringComparer.Ordinal))
            {
                string normPath = kvp.Key.Replace('\\', '/');
                ValidateSafeScalar(normPath, "Targets.Path");
                ValidateSafeScalar(kvp.Value.Sha256Hex, "Targets.Sha256Hex");
                ValidateSafeScalar(kvp.Value.BaseSha256Hex, "Targets.BaseSha256Hex");
                ValidateSafeScalar(kvp.Value.DeltaSha256Hex, "Targets.DeltaSha256Hex");
                ValidateSafeScalar(kvp.Value.DeltaPath, "Targets.DeltaPath");

                sb.Append(normPath)
                  .Append('\t').Append(kvp.Value.Length.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(kvp.Value.Sha256Hex.Trim().ToLowerInvariant())
                  .Append('\t').Append((kvp.Value.BaseSha256Hex ?? string.Empty).Trim().ToLowerInvariant())
                  .Append('\t').Append((kvp.Value.DeltaSha256Hex ?? string.Empty).Trim().ToLowerInvariant())
                  .Append('\t').Append((kvp.Value.DeltaLength ?? 0).ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append((kvp.Value.DeltaPath ?? string.Empty).Replace('\\', '/'))
                  .Append('\n');
            }
        }

        return Utf8NoBom.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Executes the complete TUF v1.0 4-role client verification workflow:
    /// Root (with optional sequential key rotation chain) -> Timestamp -> Snapshot -> Targets.
    /// </summary>
    public static TufVerificationResult VerifyUpdateBundle(
        TufTrustedState trustedState,
        TufSignedEnvelope<TufTimestampMetadata> timestamp,
        TufSignedEnvelope<TufSnapshotMetadata> snapshot,
        TufSignedEnvelope<TufTargetsMetadata> targets,
        IReadOnlyList<TufSignedEnvelope<TufRootMetadata>>? rootRotationChain = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(trustedState);
        ArgumentNullException.ThrowIfNull(trustedState.TrustedRoot);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targets);

        DateTimeOffset nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        TufSignedEnvelope<TufRootMetadata> activeRootEnvelope = trustedState.TrustedRoot;
        TufRootMetadata activeRoot = activeRootEnvelope.Signed;

        // 1. Validate pinned root structure and self-threshold signature
        if (!ValidateRootStructure(activeRoot, out string? rootStructErr))
        {
            return Fail("TUF_ROOT_INVALID", rootStructErr!);
        }

        byte[] pinnedRootBytes = BuildCanonicalRootBytes(activeRoot);
        if (!VerifyThresholdSignatures(activeRoot, TufRole.Root, pinnedRootBytes, activeRootEnvelope.Signatures, out int pinnedValidCount, out string? pinnedSigErr))
        {
            return Fail("TUF_ROOT_SIGNATURE_INVALID", $"Pinned root v{activeRoot.Version} failed threshold signature check: {pinnedSigErr}");
        }

        Trace.WriteLine($"[TUF-VERIFY] Pinned root v{activeRoot.Version} self-verified ({pinnedValidCount} unique valid signer(s)).");

        int minTimestampVersion = trustedState.TimestampVersion;
        int minSnapshotVersion = trustedState.SnapshotVersion;
        int minTargetsVersion = trustedState.TargetsVersion;

        // 2. Process sequential root rotation chain (vN -> vN+1 ... -> vM)
        if (rootRotationChain is { Count: > 0 })
        {
            foreach (TufSignedEnvelope<TufRootMetadata> nextRootEnvelope in rootRotationChain)
            {
                if (nextRootEnvelope?.Signed is null)
                {
                    return Fail("TUF_ROOT_INVALID", "Root rotation chain contains a null root envelope.");
                }

                TufRootMetadata nextRoot = nextRootEnvelope.Signed;
                if (nextRoot.Version != activeRoot.Version + 1)
                {
                    return Fail(
                        "TUF_ROOT_ROLLBACK_OR_GAP",
                        $"Root rotation requires sequential version {activeRoot.Version + 1}, but received v{nextRoot.Version}.");
                }

                if (!ValidateRootStructure(nextRoot, out string? nextStructErr))
                {
                    return Fail("TUF_ROOT_INVALID", $"Rotated root v{nextRoot.Version} invalid: {nextStructErr}");
                }

                byte[] nextRootBytes = BuildCanonicalRootBytes(nextRoot);

                // TUF 1.0 §5.3.4: New root MUST satisfy BOTH old root threshold AND new root threshold.
                if (!VerifyThresholdSignatures(activeRoot, TufRole.Root, nextRootBytes, nextRootEnvelope.Signatures, out int oldRootValid, out string? oldErr))
                {
                    return Fail("TUF_ROOT_ROTATION_SIGNATURE_INVALID", $"Rotated root v{nextRoot.Version} failed prior root v{activeRoot.Version} threshold: {oldErr}");
                }

                if (!VerifyThresholdSignatures(nextRoot, TufRole.Root, nextRootBytes, nextRootEnvelope.Signatures, out int newRootValid, out string? newErr))
                {
                    return Fail("TUF_ROOT_ROTATION_SIGNATURE_INVALID", $"Rotated root v{nextRoot.Version} failed self-threshold: {newErr}");
                }

                // TUF 1.0 §5.3.11: If timestamp or snapshot keys changed, reset those version counters to 0.
                if (!RolesEquivalent(activeRoot, nextRoot, TufRole.Timestamp) || !RolesEquivalent(activeRoot, nextRoot, TufRole.Snapshot))
                {
                    minTimestampVersion = 0;
                    minSnapshotVersion = 0;
                    Trace.WriteLine($"[TUF-VERIFY] Root rotation v{activeRoot.Version} -> v{nextRoot.Version} rotated timestamp/snapshot keys; resetting freshness floor.");
                }

                Trace.WriteLine($"[TUF-VERIFY] Root rotated v{activeRoot.Version} -> v{nextRoot.Version} (oldSigners={oldRootValid}, newSigners={newRootValid}).");
                activeRootEnvelope = nextRootEnvelope;
                activeRoot = nextRoot;
            }
        }

        // TUF 1.0 §5.3.10: Final root MUST NOT be expired (freeze-attack protection).
        if (activeRoot.ExpiresUtc <= nowUtc)
        {
            return Fail("TUF_ROOT_EXPIRED", $"Final root v{activeRoot.Version} expired at {FormatUtc(activeRoot.ExpiresUtc)} (now={FormatUtc(nowUtc)}).");
        }

        // 3. Verify Timestamp metadata
        TufTimestampMetadata tsMeta = timestamp.Signed;
        if (tsMeta is null || tsMeta.SnapshotMeta is null)
        {
            return Fail("TUF_TIMESTAMP_INVALID", "Timestamp metadata or snapshot reference is null.");
        }

        if (tsMeta.Version < 1 || tsMeta.Version < minTimestampVersion)
        {
            return Fail("TUF_TIMESTAMP_ROLLBACK", $"Timestamp rollback detected: got v{tsMeta.Version}, trusted floor is v{minTimestampVersion}.");
        }

        if (tsMeta.SnapshotMeta.Version < 1 || tsMeta.SnapshotMeta.Version < minSnapshotVersion)
        {
            return Fail("TUF_SNAPSHOT_ROLLBACK", $"Timestamp references rolled-back snapshot v{tsMeta.SnapshotMeta.Version} (trusted floor v{minSnapshotVersion}).");
        }

        byte[] tsBytes = BuildCanonicalTimestampBytes(tsMeta);
        if (!VerifyThresholdSignatures(activeRoot, TufRole.Timestamp, tsBytes, timestamp.Signatures, out int tsValid, out string? tsSigErr))
        {
            return Fail("TUF_TIMESTAMP_SIGNATURE_INVALID", $"Timestamp v{tsMeta.Version} signature check failed: {tsSigErr}");
        }

        if (tsMeta.ExpiresUtc <= nowUtc)
        {
            return Fail("TUF_TIMESTAMP_EXPIRED", $"Timestamp v{tsMeta.Version} expired at {FormatUtc(tsMeta.ExpiresUtc)} (now={FormatUtc(nowUtc)}).");
        }

        Trace.WriteLine($"[TUF-VERIFY] Timestamp v{tsMeta.Version} verified ({tsValid} signer(s), pins snapshot v{tsMeta.SnapshotMeta.Version}).");

        // 4. Verify Snapshot metadata against Timestamp digest + length + version
        TufSnapshotMetadata snapMeta = snapshot.Signed;
        if (snapMeta is null || snapMeta.Meta is null)
        {
            return Fail("TUF_SNAPSHOT_INVALID", "Snapshot metadata is null.");
        }

        byte[] snapBytes = BuildCanonicalSnapshotBytes(snapMeta);
        if (tsMeta.SnapshotMeta.Length > 0 && snapBytes.LongLength != tsMeta.SnapshotMeta.Length)
        {
            return Fail("TUF_SNAPSHOT_LENGTH_MISMATCH", $"Snapshot byte length {snapBytes.LongLength} != timestamp-pinned length {tsMeta.SnapshotMeta.Length}.");
        }

        string actualSnapSha = Convert.ToHexStringLower(SHA256.HashData(snapBytes));
        if (!FixedTimeHexEquals(actualSnapSha, tsMeta.SnapshotMeta.Sha256Hex))
        {
            return Fail("TUF_SNAPSHOT_HASH_MISMATCH", $"Snapshot SHA-256 '{actualSnapSha}' != timestamp-pinned '{tsMeta.SnapshotMeta.Sha256Hex}'.");
        }

        if (!VerifyThresholdSignatures(activeRoot, TufRole.Snapshot, snapBytes, snapshot.Signatures, out int snapValid, out string? snapSigErr))
        {
            return Fail("TUF_SNAPSHOT_SIGNATURE_INVALID", $"Snapshot v{snapMeta.Version} signature check failed: {snapSigErr}");
        }

        if (snapMeta.Version != tsMeta.SnapshotMeta.Version)
        {
            return Fail("TUF_SNAPSHOT_VERSION_MISMATCH", $"Snapshot version v{snapMeta.Version} != timestamp-pinned v{tsMeta.SnapshotMeta.Version}.");
        }

        if (snapMeta.Version < minSnapshotVersion)
        {
            return Fail("TUF_SNAPSHOT_ROLLBACK", $"Snapshot rollback detected: got v{snapMeta.Version}, trusted floor v{minSnapshotVersion}.");
        }

        if (snapMeta.ExpiresUtc <= nowUtc)
        {
            return Fail("TUF_SNAPSHOT_EXPIRED", $"Snapshot v{snapMeta.Version} expired at {FormatUtc(snapMeta.ExpiresUtc)} (now={FormatUtc(nowUtc)}).");
        }

        if (!snapMeta.Meta.TryGetValue(TargetsMetaFileName, out TufMetaFileInfo? pinnedTargetsInfo) || pinnedTargetsInfo is null)
        {
            return Fail("TUF_SNAPSHOT_MISSING_TARGETS", $"Snapshot v{snapMeta.Version} does not contain required '{TargetsMetaFileName}' entry.");
        }

        if (pinnedTargetsInfo.Version < 1 || pinnedTargetsInfo.Version < minTargetsVersion)
        {
            return Fail("TUF_TARGETS_ROLLBACK", $"Snapshot references rolled-back targets v{pinnedTargetsInfo.Version} (trusted floor v{minTargetsVersion}).");
        }

        Trace.WriteLine($"[TUF-VERIFY] Snapshot v{snapMeta.Version} verified ({snapValid} signer(s), pins targets v{pinnedTargetsInfo.Version}).");

        // 5. Verify Targets metadata against Snapshot digest + length + version
        TufTargetsMetadata targetsMeta = targets.Signed;
        if (targetsMeta is null || targetsMeta.Targets is null)
        {
            return Fail("TUF_TARGETS_INVALID", "Targets metadata is null.");
        }

        byte[] targetsBytes = BuildCanonicalTargetsBytes(targetsMeta);
        if (pinnedTargetsInfo.Length > 0 && targetsBytes.LongLength != pinnedTargetsInfo.Length)
        {
            return Fail("TUF_TARGETS_LENGTH_MISMATCH", $"Targets byte length {targetsBytes.LongLength} != snapshot-pinned length {pinnedTargetsInfo.Length}.");
        }

        string actualTargetsSha = Convert.ToHexStringLower(SHA256.HashData(targetsBytes));
        if (!FixedTimeHexEquals(actualTargetsSha, pinnedTargetsInfo.Sha256Hex))
        {
            return Fail("TUF_TARGETS_HASH_MISMATCH", $"Targets SHA-256 '{actualTargetsSha}' != snapshot-pinned '{pinnedTargetsInfo.Sha256Hex}'.");
        }

        if (!VerifyThresholdSignatures(activeRoot, TufRole.Targets, targetsBytes, targets.Signatures, out int targetsValid, out string? targetsSigErr))
        {
            return Fail("TUF_TARGETS_SIGNATURE_INVALID", $"Targets v{targetsMeta.Version} signature check failed: {targetsSigErr}");
        }

        if (targetsMeta.Version != pinnedTargetsInfo.Version)
        {
            return Fail("TUF_TARGETS_VERSION_MISMATCH", $"Targets version v{targetsMeta.Version} != snapshot-pinned v{pinnedTargetsInfo.Version}.");
        }

        if (targetsMeta.Version < minTargetsVersion)
        {
            return Fail("TUF_TARGETS_ROLLBACK", $"Targets rollback detected: got v{targetsMeta.Version}, trusted floor v{minTargetsVersion}.");
        }

        if (targetsMeta.ExpiresUtc <= nowUtc)
        {
            return Fail("TUF_TARGETS_EXPIRED", $"Targets v{targetsMeta.Version} expired at {FormatUtc(targetsMeta.ExpiresUtc)} (now={FormatUtc(nowUtc)}).");
        }

        Trace.WriteLine(
            $"[TUF-VERIFY] Targets v{targetsMeta.Version} verified ({targetsValid} signer(s), targetCount={targetsMeta.Targets.Count}). Full 4-role TUF chain ACCEPTED.");

        TufTrustedState updatedState = new(
            TrustedRoot: activeRootEnvelope,
            TimestampVersion: tsMeta.Version,
            SnapshotVersion: snapMeta.Version,
            TargetsVersion: targetsMeta.Version);

        return TufVerificationResult.Succeeded(updatedState, targetsMeta);
    }

    /// <summary>
    /// Constant-time SHA-256 + length verification of raw bytes against a TUF-verified <see cref="TufTargetFileMetadata" />.
    /// </summary>
    public static bool VerifyTargetBytes(
        ReadOnlySpan<byte> payload,
        TufTargetFileMetadata expectedTarget,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);

        if (payload.Length != expectedTarget.Length)
        {
            failureReason = $"Target byte length mismatch: actual={payload.Length}, expected={expectedTarget.Length}.";
            return false;
        }

        string actualHex = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!FixedTimeHexEquals(actualHex, expectedTarget.Sha256Hex))
        {
            failureReason = $"Target SHA-256 mismatch: actual='{actualHex}', expected='{expectedTarget.Sha256Hex}'.";
            return false;
        }

        failureReason = null;
        return true;
    }

    /// <summary>
    /// Constant-time SHA-256 hex digest comparison (case-insensitive, normalized to 64 lowercase hex characters).
    /// </summary>
    public static bool FixedTimeHexEquals(string? actualHex, string? expectedHex)
    {
        string? normActual = NormalizeSha256Hex(actualHex);
        string? normExpected = NormalizeSha256Hex(expectedHex);
        if (normActual is null || normExpected is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(normActual),
            Encoding.ASCII.GetBytes(normExpected));
    }

    public static string? NormalizeSha256Hex(string? rawHex)
    {
        if (string.IsNullOrWhiteSpace(rawHex))
        {
            return null;
        }

        string trimmed = rawHex.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["sha256:".Length..].Trim();
        }

        if (trimmed.Length != 64)
        {
            return null;
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool ValidateRootStructure(TufRootMetadata root, out string? error)
    {
        if (root.Version < 1)
        {
            error = $"Root version must be >= 1 (got {root.Version}).";
            return false;
        }

        if (root.Keys is null || root.Keys.Count == 0)
        {
            error = "Root metadata defines zero keys.";
            return false;
        }

        if (root.Roles is null)
        {
            error = "Root metadata defines zero roles.";
            return false;
        }

        foreach (TufRole requiredRole in new[] { TufRole.Root, TufRole.Timestamp, TufRole.Snapshot, TufRole.Targets })
        {
            if (!root.Roles.TryGetValue(requiredRole, out TufRoleDefinition? roleDef) || roleDef is null)
            {
                error = $"Root metadata missing required role '{requiredRole}'.";
                return false;
            }

            if (roleDef.Threshold < 1)
            {
                error = $"Role '{requiredRole}' threshold must be >= 1 (got {roleDef.Threshold}).";
                return false;
            }

            if (roleDef.KeyIds is null || roleDef.KeyIds.Count < roleDef.Threshold)
            {
                error = $"Role '{requiredRole}' authorizes {roleDef.KeyIds?.Count ?? 0} key(s), below threshold {roleDef.Threshold}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool RolesEquivalent(TufRootMetadata oldRoot, TufRootMetadata newRoot, TufRole role)
    {
        TufRoleDefinition oldRole = oldRoot.Roles[role];
        TufRoleDefinition newRole = newRoot.Roles[role];
        if (oldRole.Threshold != newRole.Threshold)
        {
            return false;
        }

        HashSet<string> oldKeys = new(oldRole.KeyIds, StringComparer.Ordinal);
        HashSet<string> newKeys = new(newRole.KeyIds, StringComparer.Ordinal);
        if (!oldKeys.SetEquals(newKeys))
        {
            return false;
        }

        foreach (string keyId in oldKeys)
        {
            if (!oldRoot.Keys.TryGetValue(keyId, out TufKey? k1) ||
                !newRoot.Keys.TryGetValue(keyId, out TufKey? k2) ||
                !string.Equals(k1.PublicKeyXHex, k2.PublicKeyXHex, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(k1.PublicKeyYHex, k2.PublicKeyYHex, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool VerifyThresholdSignatures(
        TufRootMetadata trustRoot,
        TufRole role,
        byte[] canonicalPayload,
        IReadOnlyList<TufSignature>? signatures,
        out int validUniqueSignerCount,
        out string? failureReason)
    {
        validUniqueSignerCount = 0;
        if (!trustRoot.Roles.TryGetValue(role, out TufRoleDefinition? roleDef) || roleDef is null)
        {
            failureReason = $"Trust root does not define role '{role}'.";
            return false;
        }

        if (roleDef.Threshold < 1)
        {
            failureReason = $"Role '{role}' has invalid threshold {roleDef.Threshold}.";
            return false;
        }

        if (signatures is null || signatures.Count == 0)
        {
            failureReason = $"Role '{role}' requires at least {roleDef.Threshold} valid signature(s), but received 0.";
            return false;
        }

        HashSet<string> authorizedKeyIds = new(roleDef.KeyIds, StringComparer.Ordinal);
        HashSet<string> verifiedKeyIds = new(StringComparer.Ordinal);

        foreach (TufSignature sig in signatures)
        {
            if (sig is null || string.IsNullOrWhiteSpace(sig.KeyId) || string.IsNullOrWhiteSpace(sig.SignatureBase64))
            {
                continue;
            }

            string keyId = sig.KeyId.Trim();
            // Deduplicate by KeyId so replaying one key's signature twice can NEVER satisfy threshold >= 2.
            if (verifiedKeyIds.Contains(keyId) || !authorizedKeyIds.Contains(keyId))
            {
                continue;
            }

            if (!trustRoot.Keys.TryGetValue(keyId, out TufKey? key) || key is null)
            {
                continue;
            }

            if (VerifySingleEcdsaSignature(canonicalPayload, sig.SignatureBase64, key))
            {
                verifiedKeyIds.Add(keyId);
            }
        }

        validUniqueSignerCount = verifiedKeyIds.Count;
        if (validUniqueSignerCount < roleDef.Threshold)
        {
            failureReason = $"Role '{role}' threshold not met: {validUniqueSignerCount}/{roleDef.Threshold} unique authorized signature(s) verified.";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool VerifySingleEcdsaSignature(byte[] payload, string signatureBase64, TufKey key)
    {
        if (!string.Equals(key.KeyType, "ecdsa-sha2-nistp256", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] sigBytes;
        byte[] xBytes;
        byte[] yBytes;
        try
        {
            sigBytes = Convert.FromBase64String(signatureBase64.Trim());
            xBytes = Convert.FromHexString(key.PublicKeyXHex.Trim());
            yBytes = Convert.FromHexString(key.PublicKeyYHex.Trim());
        }
        catch (FormatException ex)
        {
            Trace.WriteLine($"[TUF-VERIFY] Malformed key/signature encoding for key '{key.KeyId}': {ex.Message}");
            return false;
        }

        if (sigBytes.Length != 64 || xBytes.Length != 32 || yBytes.Length != 32)
        {
            return false;
        }

        try
        {
            using ECDsa ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = xBytes, Y = yBytes }
            });
            return ecdsa.VerifyData(payload, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException ex)
        {
            Trace.WriteLine($"[TUF-VERIFY] ECDSA verification exception for key '{key.KeyId}': {ex.Message}");
            return false;
        }
    }

    private static TufVerificationResult Fail(string code, string reason)
    {
        Trace.WriteLine($"[TUF-VERIFY][REJECT] {code}: {reason}");
        return TufVerificationResult.Failed(code, reason);
    }

    private static string FormatUtc(DateTimeOffset dto)
        => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateSafeScalar(string? value, string fieldName)
    {
        if (!string.IsNullOrEmpty(value) && value.IndexOfAny(['\t', '\r', '\n', '\0']) >= 0)
        {
            throw new SecurityException($"Control character forbidden in TUF canonical field '{fieldName}'.");
        }
    }
}
