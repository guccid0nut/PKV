// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Services.Update;
using Xunit;

namespace NEXUS.CdnBroker.Tests;

/// <summary>
/// Value-Weighted Testing Gate (Wave 4 Track 4 — TUF-Authenticated Signed Delta Patch Verifier):
/// - protects: End-to-end BaseSha256 -> signed DeltaSha256 -> patched TargetSha256 hash-chain enforcement,
///   atomic shadow-copy commit, staged file cleanup on failure, and bounds-checked PKVDELTA application.
/// - fails_when: Drifted base file, tampered delta patch, mismatched target digest, expired TUF bundle,
///   or out-of-bounds COPY delta mutates the target file or passes verification.
/// - seam: WILLOWMAKER.Core.Services.Update.DeltaPatchVerifier + NEXUS.Contracts.Cdn.TufMetadataVerifier + ShadowCopyUpdateService.
/// </summary>
public sealed class Wave4DeltaPatchVerifierTests : IDisposable
{
    private readonly string _tempDir;

    public Wave4DeltaPatchVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kongor_delta_w4_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static TufDeltaUpdateBundle BuildSignedDeltaBundle(
        DateTimeOffset nowUtc,
        string targetLogicalPath,
        byte[] baseBytes,
        byte[] deltaBytes,
        byte[] expectedTargetBytes,
        DateTimeOffset? timestampExpiresUtc = null,
        string? overrideExpectedTargetSha = null,
        int version = 3)
    {
        using ECDsa rootKey1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa rootKey2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa tsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa snapKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa targetsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Dictionary<string, TufKey> keys = new()
        {
            ["r1"] = TufKey.FromECDsa(rootKey1, "r1"),
            ["r2"] = TufKey.FromECDsa(rootKey2, "r2"),
            ["ts"] = TufKey.FromECDsa(tsKey, "ts"),
            ["sn"] = TufKey.FromECDsa(snapKey, "sn"),
            ["tg"] = TufKey.FromECDsa(targetsKey, "tg")
        };

        Dictionary<TufRole, TufRoleDefinition> roles = new()
        {
            [TufRole.Root] = new TufRoleDefinition(2, ["r1", "r2"]),
            [TufRole.Timestamp] = new TufRoleDefinition(1, ["ts"]),
            [TufRole.Snapshot] = new TufRoleDefinition(1, ["sn"]),
            [TufRole.Targets] = new TufRoleDefinition(1, ["tg"])
        };

        TufRootMetadata root = new(1, nowUtc.AddDays(30), ConsistentSnapshot: true, keys, roles);
        TufSignedEnvelope<TufRootMetadata> signedRoot = TufMetadataVerifier.SignRoot(root, ("r1", rootKey1), ("r2", rootKey2));

        string baseSha = Convert.ToHexStringLower(SHA256.HashData(baseBytes));
        string deltaSha = Convert.ToHexStringLower(SHA256.HashData(deltaBytes));
        string targetSha = overrideExpectedTargetSha ?? Convert.ToHexStringLower(SHA256.HashData(expectedTargetBytes));

        TufTargetsMetadata targets = new(
            Version: version,
            ExpiresUtc: nowUtc.AddDays(7),
            Targets: new Dictionary<string, TufTargetFileMetadata>
            {
                [targetLogicalPath] = new(
                    Length: expectedTargetBytes.LongLength,
                    Sha256Hex: targetSha,
                    BaseSha256Hex: baseSha,
                    DeltaSha256Hex: deltaSha,
                    DeltaLength: deltaBytes.LongLength,
                    DeltaPath: targetLogicalPath + ".pkvdelta")
            });
        TufSignedEnvelope<TufTargetsMetadata> signedTargets = TufMetadataVerifier.SignTargets(targets, ("tg", targetsKey));

        TufSnapshotMetadata snapshot = new(
            Version: version,
            ExpiresUtc: nowUtc.AddDays(3),
            Meta: new Dictionary<string, TufMetaFileInfo>
            {
                [TufMetadataVerifier.TargetsMetaFileName] = TufMetadataVerifier.ComputeTargetsMetaInfo(targets)
            });
        TufSignedEnvelope<TufSnapshotMetadata> signedSnapshot = TufMetadataVerifier.SignSnapshot(snapshot, ("sn", snapKey));

        TufTimestampMetadata timestamp = new(
            Version: version,
            ExpiresUtc: timestampExpiresUtc ?? nowUtc.AddHours(6),
            SnapshotMeta: TufMetadataVerifier.ComputeSnapshotMetaInfo(snapshot));
        TufSignedEnvelope<TufTimestampMetadata> signedTimestamp = TufMetadataVerifier.SignTimestamp(timestamp, ("ts", tsKey));

        return new TufDeltaUpdateBundle(
            new TufTrustedState(signedRoot),
            signedTimestamp,
            signedSnapshot,
            signedTargets);
    }

    [Fact]
    public async Task VerifyAndApplySignedDeltaAsync_ValidTufChainAndDelta_AtomicallyProducesExactTargetFile()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 30, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);

        byte[] baseBytes = Encoding.UTF8.GetBytes("HEADER::KONGOR-V5.0.1::" + new string('A', 4096) + "::FOOTER-COMMON");
        byte[] targetBytes = Encoding.UTF8.GetBytes("HEADER::KONGOR-V5.0.2-DELTA-PATCHED::" + new string('A', 4096) + "::FOOTER-COMMON");
        byte[] deltaBytes = DeltaPatchVerifier.CreateDeltaPatch(baseBytes, targetBytes);

        // Delta should be dramatically smaller than full 4KB+ target payload
        deltaBytes.Length.Should().BeLessThan(targetBytes.Length / 4);

        string basePath = Path.Combine(_tempDir, "client_base.bin");
        string deltaPath = Path.Combine(_tempDir, "client_patch.pkvdelta");
        string targetPath = Path.Combine(_tempDir, "client_target.bin");
        await File.WriteAllBytesAsync(basePath, baseBytes);
        await File.WriteAllBytesAsync(deltaPath, deltaBytes);
        await File.WriteAllBytesAsync(targetPath, baseBytes);

        TufDeltaUpdateBundle bundle = BuildSignedDeltaBundle(now, "bin/client.bin", baseBytes, deltaBytes, targetBytes);

        DeltaPatchApplicationResult res = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            bundle,
            "bin/client.bin",
            basePath,
            deltaPath,
            targetPath,
            timeProvider: clock);

        res.Success.Should().BeTrue(res.ErrorMessage);
        res.FailureCode.Should().BeNull();
        res.PatchedBytesWritten.Should().Be(targetBytes.LongLength);
        res.VerifiedTargetSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(targetBytes)));
        res.UpdatedTufState!.TargetsVersion.Should().Be(3);

        byte[] committed = await File.ReadAllBytesAsync(targetPath);
        committed.Should().Equal(targetBytes);

        // Zero leftover staged temporary files
        Directory.GetFiles(_tempDir, "*.delta.staged.*").Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAndApplySignedDeltaAsync_DriftedBaseOrTamperedDelta_FailsClosedWithoutTouchingTarget()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 30, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);

        byte[] baseBytes = Encoding.UTF8.GetBytes("ORIGINAL-BASE-PAYLOAD-V1");
        byte[] targetBytes = Encoding.UTF8.GetBytes("PATCHED-TARGET-PAYLOAD-V2");
        byte[] deltaBytes = DeltaPatchVerifier.CreateDeltaPatch(baseBytes, targetBytes);

        string basePath = Path.Combine(_tempDir, "base_drift.bin");
        string deltaPath = Path.Combine(_tempDir, "delta_ok.pkvdelta");
        string targetPath = Path.Combine(_tempDir, "target_untouched.bin");
        byte[] sentinelTarget = Encoding.UTF8.GetBytes("EXISTING-GOOD-BINARY");

        await File.WriteAllBytesAsync(basePath, Encoding.UTF8.GetBytes("DRIFTED-LOCAL-BASE-V1"));
        await File.WriteAllBytesAsync(deltaPath, deltaBytes);
        await File.WriteAllBytesAsync(targetPath, sentinelTarget);

        TufDeltaUpdateBundle bundle = BuildSignedDeltaBundle(now, "bin/hon.dll", baseBytes, deltaBytes, targetBytes);

        // 1. Drifted base file rejected
        DeltaPatchApplicationResult baseFail = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            bundle, "bin/hon.dll", basePath, deltaPath, targetPath, timeProvider: clock);
        baseFail.Success.Should().BeFalse();
        baseFail.FailureCode.Should().Be("DELTA_BASE_HASH_MISMATCH");
        (await File.ReadAllBytesAsync(targetPath)).Should().Equal(sentinelTarget);

        // 2. Restore true base, tamper 1 byte in delta -> rejected
        await File.WriteAllBytesAsync(basePath, baseBytes);
        byte[] tamperedDelta = [.. deltaBytes];
        tamperedDelta[^1] ^= 0x01;
        await File.WriteAllBytesAsync(deltaPath, tamperedDelta);

        DeltaPatchApplicationResult deltaFail = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            bundle, "bin/hon.dll", basePath, deltaPath, targetPath, timeProvider: clock);
        deltaFail.Success.Should().BeFalse();
        deltaFail.FailureCode.Should().Be("DELTA_PATCH_HASH_MISMATCH");
        (await File.ReadAllBytesAsync(targetPath)).Should().Equal(sentinelTarget);
    }

    [Fact]
    public async Task VerifyAndApplySignedDeltaAsync_TargetHashMismatchOrExpiredTuf_DeletesStagedAndLeavesTargetUntouched()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 30, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);

        byte[] baseBytes = Encoding.UTF8.GetBytes("BASE-CONTENT-12345");
        byte[] targetBytes = Encoding.UTF8.GetBytes("TARGET-CONTENT-67890");
        byte[] deltaBytes = DeltaPatchVerifier.CreateDeltaPatch(baseBytes, targetBytes);
        byte[] sentinel = Encoding.UTF8.GetBytes("SENTINEL-UNMODIFIED");

        string basePath = Path.Combine(_tempDir, "base2.bin");
        string deltaPath = Path.Combine(_tempDir, "delta2.pkvdelta");
        string targetPath = Path.Combine(_tempDir, "target2.bin");
        await File.WriteAllBytesAsync(basePath, baseBytes);
        await File.WriteAllBytesAsync(deltaPath, deltaBytes);
        await File.WriteAllBytesAsync(targetPath, sentinel);

        // 1. TUF target claims different TargetSha256 than what delta reconstructs -> reject + delete staged
        TufDeltaUpdateBundle mismatchedTargetBundle = BuildSignedDeltaBundle(
            now, "bin/k2.dll", baseBytes, deltaBytes, targetBytes, overrideExpectedTargetSha: new string('f', 64));

        DeltaPatchApplicationResult mismatchRes = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            mismatchedTargetBundle, "bin/k2.dll", basePath, deltaPath, targetPath, timeProvider: clock);
        mismatchRes.Success.Should().BeFalse();
        mismatchRes.FailureCode.Should().Be("DELTA_TARGET_HASH_MISMATCH");
        (await File.ReadAllBytesAsync(targetPath)).Should().Equal(sentinel);
        Directory.GetFiles(_tempDir, "*.delta.staged.*").Should().BeEmpty();

        // 2. Expired TUF timestamp -> reject before touching filesystem
        TufDeltaUpdateBundle expiredBundle = BuildSignedDeltaBundle(
            now, "bin/k2.dll", baseBytes, deltaBytes, targetBytes, timestampExpiresUtc: now.AddMinutes(-5));

        DeltaPatchApplicationResult expiredRes = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            expiredBundle, "bin/k2.dll", basePath, deltaPath, targetPath, timeProvider: clock);
        expiredRes.Success.Should().BeFalse();
        expiredRes.FailureCode.Should().Be("TUF_TIMESTAMP_EXPIRED");
        (await File.ReadAllBytesAsync(targetPath)).Should().Equal(sentinel);
    }

    [Fact]
    public async Task VerifyAndApplySignedDeltaAsync_OutOfBoundsCopyInstruction_IsBlockedSafely()
    {
        DateTimeOffset now = new(2026, 11, 19, 21, 30, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(now);

        byte[] baseBytes = Encoding.UTF8.GetBytes("SHORT-BASE");
        byte[] expectedTarget = Encoding.UTF8.GetBytes("SHORT-BASE");

        // Craft malformed PKVDELTA with COPY offset beyond base length
        using MemoryStream ms = new();
        Span<byte> header = stackalloc byte[28];
        "PKVDELTA"u8.CopyTo(header[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..20], (ulong)baseBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..28], (ulong)expectedTarget.Length);
        ms.Write(header);
        Span<byte> badCopy = stackalloc byte[13];
        badCopy[0] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(badCopy[1..9], 9999UL); // OOB offset!
        BinaryPrimitives.WriteUInt32LittleEndian(badCopy[9..13], 4U);
        ms.Write(badCopy);
        byte[] badDelta = ms.ToArray();

        string basePath = Path.Combine(_tempDir, "oob_base.bin");
        string deltaPath = Path.Combine(_tempDir, "oob.pkvdelta");
        string targetPath = Path.Combine(_tempDir, "oob_target.bin");
        await File.WriteAllBytesAsync(basePath, baseBytes);
        await File.WriteAllBytesAsync(deltaPath, badDelta);

        TufDeltaUpdateBundle bundle = BuildSignedDeltaBundle(now, "bin/oob.bin", baseBytes, badDelta, expectedTarget);

        DeltaPatchApplicationResult res = await DeltaPatchVerifier.VerifyAndApplySignedDeltaAsync(
            bundle, "bin/oob.bin", basePath, deltaPath, targetPath, timeProvider: clock);

        res.Success.Should().BeFalse();
        res.FailureCode.Should().Be("DELTA_PATCH_APPLY_ERROR");
        res.ErrorMessage.Should().Contain("out of bounds");
        File.Exists(targetPath).Should().BeFalse();
        Directory.GetFiles(_tempDir, "*.delta.staged.*").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Ignore transient file handle release lag in temp directory cleanup
            }
        }
    }
}
