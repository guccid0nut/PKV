// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using Xunit;

namespace NEXUS.Contracts.Tests;

/// <summary>
/// Value-Weighted Testing Gate (Wave 4 Track 4 — TUF v1.0 4-Role Metadata Verifier):
/// - protects: Native-AOT-safe TUF v1.0 Root -> Timestamp -> Snapshot -> Targets verification using ECDSA P-256,
///   m-of-n unique-key threshold enforcement, dual-signed sequential root rotation, monotonic version rollback protection,
///   UTC expiry freeze-attack protection, and mix-and-match hash chaining.
/// - fails_when: Duplicate key signature bypasses m-of-n threshold, rolled-back or expired metadata is accepted,
///   cross-role key signs Targets, or tampered Snapshot/Targets escapes hash pinning.
/// - seam: NEXUS.Contracts.Cdn.TufMetadataVerifier.
/// </summary>
public sealed class Wave4TufMetadataVerifierTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TufTestKeyring : IDisposable
    {
        public ECDsa Root1 { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Root2 { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Root3 { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Timestamp { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Snapshot { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Targets1 { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Targets2 { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public TufRootMetadata CreateRoot(int version, DateTimeOffset expiresUtc, int rootThreshold = 2, int targetsThreshold = 2)
        {
            Dictionary<string, TufKey> keys = new()
            {
                ["root-1"] = TufKey.FromECDsa(Root1, "root-1"),
                ["root-2"] = TufKey.FromECDsa(Root2, "root-2"),
                ["root-3"] = TufKey.FromECDsa(Root3, "root-3"),
                ["ts-1"] = TufKey.FromECDsa(Timestamp, "ts-1"),
                ["snap-1"] = TufKey.FromECDsa(Snapshot, "snap-1"),
                ["tgt-1"] = TufKey.FromECDsa(Targets1, "tgt-1"),
                ["tgt-2"] = TufKey.FromECDsa(Targets2, "tgt-2")
            };

            Dictionary<TufRole, TufRoleDefinition> roles = new()
            {
                [TufRole.Root] = new TufRoleDefinition(rootThreshold, ["root-1", "root-2", "root-3"]),
                [TufRole.Timestamp] = new TufRoleDefinition(1, ["ts-1"]),
                [TufRole.Snapshot] = new TufRoleDefinition(1, ["snap-1"]),
                [TufRole.Targets] = new TufRoleDefinition(targetsThreshold, ["tgt-1", "tgt-2"])
            };

            return new TufRootMetadata(version, expiresUtc, ConsistentSnapshot: true, keys, roles);
        }

        public (
            TufTrustedState State,
            TufSignedEnvelope<TufTimestampMetadata> Ts,
            TufSignedEnvelope<TufSnapshotMetadata> Snap,
            TufSignedEnvelope<TufTargetsMetadata> Targets,
            byte[] SamplePayload)
            BuildValidBundle(DateTimeOffset nowUtc, int version = 1)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"kongor-client-update-v{version}");
            string payloadSha = Convert.ToHexStringLower(SHA256.HashData(payload));

            TufRootMetadata root = CreateRoot(version: 1, expiresUtc: nowUtc.AddDays(30));
            TufSignedEnvelope<TufRootMetadata> signedRoot = TufMetadataVerifier.SignRoot(root, ("root-1", Root1), ("root-2", Root2));

            TufTargetsMetadata targets = new(
                Version: version,
                ExpiresUtc: nowUtc.AddDays(7),
                Targets: new Dictionary<string, TufTargetFileMetadata>
                {
                    ["games/client/override/ui/patch.lua"] = new(payload.LongLength, payloadSha)
                });
            TufSignedEnvelope<TufTargetsMetadata> signedTargets = TufMetadataVerifier.SignTargets(targets, ("tgt-1", Targets1), ("tgt-2", Targets2));

            TufSnapshotMetadata snapshot = new(
                Version: version,
                ExpiresUtc: nowUtc.AddDays(3),
                Meta: new Dictionary<string, TufMetaFileInfo>
                {
                    [TufMetadataVerifier.TargetsMetaFileName] = TufMetadataVerifier.ComputeTargetsMetaInfo(targets)
                });
            TufSignedEnvelope<TufSnapshotMetadata> signedSnapshot = TufMetadataVerifier.SignSnapshot(snapshot, ("snap-1", Snapshot));

            TufTimestampMetadata timestamp = new(
                Version: version,
                ExpiresUtc: nowUtc.AddHours(12),
                SnapshotMeta: TufMetadataVerifier.ComputeSnapshotMetaInfo(snapshot));
            TufSignedEnvelope<TufTimestampMetadata> signedTimestamp = TufMetadataVerifier.SignTimestamp(timestamp, ("ts-1", Timestamp));

            TufTrustedState state = new(signedRoot, TimestampVersion: 0, SnapshotVersion: 0, TargetsVersion: 0);
            return (state, signedTimestamp, signedSnapshot, signedTargets, payload);
        }

        public void Dispose()
        {
            Root1.Dispose();
            Root2.Dispose();
            Root3.Dispose();
            Timestamp.Dispose();
            Snapshot.Dispose();
            Targets1.Dispose();
            Targets2.Dispose();
        }
    }

    [Fact]
    public void VerifyUpdateBundle_ValidFourRoleChainWithMofNThresholds_SucceedsAndVerifiesTargetDigest()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);
        using TufTestKeyring keys = new();

        var (state, ts, snap, targets, payload) = keys.BuildValidBundle(now, version: 4);

        TufVerificationResult res = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, targets, timeProvider: clock);

        res.IsValid.Should().BeTrue(res.FailureReason);
        res.FailureCode.Should().BeNull();
        res.UpdatedState.Should().NotBeNull();
        res.UpdatedState!.TimestampVersion.Should().Be(4);
        res.UpdatedState.SnapshotVersion.Should().Be(4);
        res.UpdatedState.TargetsVersion.Should().Be(4);
        res.VerifiedTargets.Should().NotBeNull();

        TufTargetFileMetadata targetMeta = res.VerifiedTargets!.Targets["games/client/override/ui/patch.lua"];
        TufMetadataVerifier.VerifyTargetBytes(payload, targetMeta, out string? err).Should().BeTrue(err);

        byte[] tampered = [.. payload];
        tampered[0] ^= 0xFF;
        TufMetadataVerifier.VerifyTargetBytes(tampered, targetMeta, out string? tamperErr).Should().BeFalse();
        tamperErr.Should().Contain("SHA-256 mismatch");
    }

    [Fact]
    public void VerifyUpdateBundle_DuplicateKeySignatureReplayAndCrossRoleKey_AreRejected()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);
        using TufTestKeyring keys = new();

        var (state, ts, snap, targets, _) = keys.BuildValidBundle(now, version: 1);

        // Attempt to satisfy 2-of-2 Targets threshold by repeating tgt-1 signature twice
        byte[] canonicalTargets = TufMetadataVerifier.BuildCanonicalTargetsBytes(targets.Signed);
        TufSignature sig1a = TufMetadataVerifier.CreateSignature(canonicalTargets, "tgt-1", keys.Targets1);
        TufSignature sig1b = TufMetadataVerifier.CreateSignature(canonicalTargets, "tgt-1", keys.Targets1);
        TufSignedEnvelope<TufTargetsMetadata> replayedTargets = new(targets.Signed, [sig1a, sig1b]);

        TufVerificationResult replayRes = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, replayedTargets, timeProvider: clock);
        replayRes.IsValid.Should().BeFalse();
        replayRes.FailureCode.Should().Be("TUF_TARGETS_SIGNATURE_INVALID");

        // Attempt cross-role key confusion: Timestamp key signs Targets alongside tgt-1
        TufSignature wrongRoleSig = TufMetadataVerifier.CreateSignature(canonicalTargets, "ts-1", keys.Timestamp);
        TufSignedEnvelope<TufTargetsMetadata> crossRoleTargets = new(targets.Signed, [sig1a, wrongRoleSig]);

        TufVerificationResult crossRoleRes = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, crossRoleTargets, timeProvider: clock);
        crossRoleRes.IsValid.Should().BeFalse();
        crossRoleRes.FailureCode.Should().Be("TUF_TARGETS_SIGNATURE_INVALID");
    }

    [Fact]
    public void VerifyUpdateBundle_MonotonicVersionRollback_IsRejectedForTimestampSnapshotAndTargets()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);
        using TufTestKeyring keys = new();

        var (state, tsV2, snapV2, targetsV2, _) = keys.BuildValidBundle(now, version: 2);

        // Floor already at Timestamp v5 -> reject v2
        TufTrustedState highTsState = state with { TimestampVersion = 5 };
        TufVerificationResult tsRollback = TufMetadataVerifier.VerifyUpdateBundle(highTsState, tsV2, snapV2, targetsV2, timeProvider: clock);
        tsRollback.IsValid.Should().BeFalse();
        tsRollback.FailureCode.Should().Be("TUF_TIMESTAMP_ROLLBACK");

        // Floor already at Snapshot v5 -> reject v2
        TufTrustedState highSnapState = state with { TimestampVersion = 2, SnapshotVersion = 5 };
        TufVerificationResult snapRollback = TufMetadataVerifier.VerifyUpdateBundle(highSnapState, tsV2, snapV2, targetsV2, timeProvider: clock);
        snapRollback.IsValid.Should().BeFalse();
        snapRollback.FailureCode.Should().Be("TUF_SNAPSHOT_ROLLBACK");

        // Floor already at Targets v5 -> reject v2
        TufTrustedState highTargetsState = state with { TimestampVersion = 2, SnapshotVersion = 2, TargetsVersion = 5 };
        TufVerificationResult tgtRollback = TufMetadataVerifier.VerifyUpdateBundle(highTargetsState, tsV2, snapV2, targetsV2, timeProvider: clock);
        tgtRollback.IsValid.Should().BeFalse();
        tgtRollback.FailureCode.Should().Be("TUF_TARGETS_ROLLBACK");
    }

    [Fact]
    public void VerifyUpdateBundle_FreezeAttackExpiredMetadata_IsRejectedAcrossAllRoles()
    {
        DateTimeOffset baseTime = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        using TufTestKeyring keys = new();
        var (state, ts, snap, targets, _) = keys.BuildValidBundle(baseTime, version: 1);

        // +13 hours -> Timestamp (12h TTL) expired
        TufVerificationResult tsExpired = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, targets, timeProvider: new FixedTimeProvider(baseTime.AddHours(13)));
        tsExpired.IsValid.Should().BeFalse();
        tsExpired.FailureCode.Should().Be("TUF_TIMESTAMP_EXPIRED");

        // +31 days -> Root (30d TTL) expired
        TufVerificationResult rootExpired = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, targets, timeProvider: new FixedTimeProvider(baseTime.AddDays(31)));
        rootExpired.IsValid.Should().BeFalse();
        rootExpired.FailureCode.Should().Be("TUF_ROOT_EXPIRED");

        // Targets expired while Timestamp & Snapshot remain valid
        TufTargetsMetadata expiredTargetsMeta = targets.Signed with { ExpiresUtc = baseTime.AddMinutes(-1) };
        TufSignedEnvelope<TufTargetsMetadata> signedExpiredTargets = TufMetadataVerifier.SignTargets(expiredTargetsMeta, ("tgt-1", keys.Targets1), ("tgt-2", keys.Targets2));
        TufSnapshotMetadata freshSnapMeta = snap.Signed with
        {
            Meta = new Dictionary<string, TufMetaFileInfo> { [TufMetadataVerifier.TargetsMetaFileName] = TufMetadataVerifier.ComputeTargetsMetaInfo(expiredTargetsMeta) }
        };
        TufSignedEnvelope<TufSnapshotMetadata> signedFreshSnap = TufMetadataVerifier.SignSnapshot(freshSnapMeta, ("snap-1", keys.Snapshot));
        TufTimestampMetadata freshTsMeta = ts.Signed with { SnapshotMeta = TufMetadataVerifier.ComputeSnapshotMetaInfo(freshSnapMeta) };
        TufSignedEnvelope<TufTimestampMetadata> signedFreshTs = TufMetadataVerifier.SignTimestamp(freshTsMeta, ("ts-1", keys.Timestamp));

        TufVerificationResult tgtExpired = TufMetadataVerifier.VerifyUpdateBundle(state, signedFreshTs, signedFreshSnap, signedExpiredTargets, timeProvider: new FixedTimeProvider(baseTime));
        tgtExpired.IsValid.Should().BeFalse();
        tgtExpired.FailureCode.Should().Be("TUF_TARGETS_EXPIRED");
    }

    [Fact]
    public void VerifyUpdateBundle_MixAndMatchHashTampering_IsRejected()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);
        using TufTestKeyring keys = new();

        var (state, ts, snap, targets, _) = keys.BuildValidBundle(now, version: 1);

        // Validly sign a DIFFERENT targets payload whose hash does NOT match what Snapshot pinned
        TufTargetsMetadata swappedTargets = targets.Signed with
        {
            Targets = new Dictionary<string, TufTargetFileMetadata>
            {
                ["games/client/override/ui/patch.lua"] = new(999, new string('b', 64))
            }
        };
        TufSignedEnvelope<TufTargetsMetadata> signedSwappedTargets = TufMetadataVerifier.SignTargets(swappedTargets, ("tgt-1", keys.Targets1), ("tgt-2", keys.Targets2));

        TufVerificationResult res = TufMetadataVerifier.VerifyUpdateBundle(state, ts, snap, signedSwappedTargets, timeProvider: clock);
        res.IsValid.Should().BeFalse();
        res.FailureCode.Should().BeOneOf("TUF_TARGETS_LENGTH_MISMATCH", "TUF_TARGETS_HASH_MISMATCH");
    }

    [Fact]
    public void VerifyUpdateBundle_DualSignedRootRotation_RotatesKeysAndBlocksUnsignedOrGappedRotation()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);
        using TufTestKeyring oldKeys = new();
        using ECDsa newRootA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa newRootB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa newTargetsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var (stateV1, _, _, _, _) = oldKeys.BuildValidBundle(now, version: 1);

        // Root v2 rotates Root keys to {new-root-a, new-root-b} (2-of-2) and Targets key to {new-tgt-1} (1-of-1)
        Dictionary<string, TufKey> v2Keys = new()
        {
            ["new-root-a"] = TufKey.FromECDsa(newRootA, "new-root-a"),
            ["new-root-b"] = TufKey.FromECDsa(newRootB, "new-root-b"),
            ["ts-1"] = TufKey.FromECDsa(oldKeys.Timestamp, "ts-1"),
            ["snap-1"] = TufKey.FromECDsa(oldKeys.Snapshot, "snap-1"),
            ["new-tgt-1"] = TufKey.FromECDsa(newTargetsKey, "new-tgt-1")
        };
        Dictionary<TufRole, TufRoleDefinition> v2Roles = new()
        {
            [TufRole.Root] = new TufRoleDefinition(2, ["new-root-a", "new-root-b"]),
            [TufRole.Timestamp] = new TufRoleDefinition(1, ["ts-1"]),
            [TufRole.Snapshot] = new TufRoleDefinition(1, ["snap-1"]),
            [TufRole.Targets] = new TufRoleDefinition(1, ["new-tgt-1"])
        };

        TufRootMetadata rootV2 = new(2, now.AddDays(60), ConsistentSnapshot: true, v2Keys, v2Roles);

        // Dual-sign Root v2 by BOTH old root threshold (root-1, root-2) AND new root threshold (new-root-a, new-root-b)
        TufSignedEnvelope<TufRootMetadata> dualSignedRootV2 = TufMetadataVerifier.SignRoot(
            rootV2,
            ("root-1", oldKeys.Root1),
            ("root-2", oldKeys.Root2),
            ("new-root-a", newRootA),
            ("new-root-b", newRootB));

        byte[] newPayload = Encoding.UTF8.GetBytes("rotated-targets-payload");
        TufTargetsMetadata targetsV2 = new(
            Version: 2,
            ExpiresUtc: now.AddDays(7),
            Targets: new Dictionary<string, TufTargetFileMetadata>
            {
                ["bin/kongor.dll"] = new(newPayload.LongLength, Convert.ToHexStringLower(SHA256.HashData(newPayload)))
            });
        TufSignedEnvelope<TufTargetsMetadata> signedTargetsV2 = TufMetadataVerifier.SignTargets(targetsV2, ("new-tgt-1", newTargetsKey));
        TufSnapshotMetadata snapV2 = new(2, now.AddDays(3), new Dictionary<string, TufMetaFileInfo> { [TufMetadataVerifier.TargetsMetaFileName] = TufMetadataVerifier.ComputeTargetsMetaInfo(targetsV2) });
        TufSignedEnvelope<TufSnapshotMetadata> signedSnapV2 = TufMetadataVerifier.SignSnapshot(snapV2, ("snap-1", oldKeys.Snapshot));
        TufTimestampMetadata tsV2 = new(2, now.AddHours(12), TufMetadataVerifier.ComputeSnapshotMetaInfo(snapV2));
        TufSignedEnvelope<TufTimestampMetadata> signedTsV2 = TufMetadataVerifier.SignTimestamp(tsV2, ("ts-1", oldKeys.Timestamp));

        // 1. Valid dual-signed rotation succeeds
        TufVerificationResult rotated = TufMetadataVerifier.VerifyUpdateBundle(
            stateV1, signedTsV2, signedSnapV2, signedTargetsV2, rootRotationChain: [dualSignedRootV2], timeProvider: clock);
        rotated.IsValid.Should().BeTrue(rotated.FailureReason);
        rotated.UpdatedState!.TrustedRoot.Signed.Version.Should().Be(2);

        // 2. Root v2 signed ONLY by new keys (missing old root threshold) -> REJECTED
        TufSignedEnvelope<TufRootMetadata> rogueRootV2 = TufMetadataVerifier.SignRoot(rootV2, ("new-root-a", newRootA), ("new-root-b", newRootB));
        TufVerificationResult rogueRes = TufMetadataVerifier.VerifyUpdateBundle(
            stateV1, signedTsV2, signedSnapV2, signedTargetsV2, rootRotationChain: [rogueRootV2], timeProvider: clock);
        rogueRes.IsValid.Should().BeFalse();
        rogueRes.FailureCode.Should().Be("TUF_ROOT_ROTATION_SIGNATURE_INVALID");

        // 3. Version gap v1 -> v3 -> REJECTED
        TufRootMetadata gappedRootV3 = rootV2 with { Version = 3 };
        TufSignedEnvelope<TufRootMetadata> gappedSigned = TufMetadataVerifier.SignRoot(
            gappedRootV3, ("root-1", oldKeys.Root1), ("root-2", oldKeys.Root2), ("new-root-a", newRootA), ("new-root-b", newRootB));
        TufVerificationResult gapRes = TufMetadataVerifier.VerifyUpdateBundle(
            stateV1, signedTsV2, signedSnapV2, signedTargetsV2, rootRotationChain: [gappedSigned], timeProvider: clock);
        gapRes.IsValid.Should().BeFalse();
        gapRes.FailureCode.Should().Be("TUF_ROOT_ROLLBACK_OR_GAP");
    }
}
