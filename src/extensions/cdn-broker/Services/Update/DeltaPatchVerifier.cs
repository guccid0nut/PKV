// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using NEXUS.Contracts.Cdn;

namespace WILLOWMAKER.Core.Services.Update;

/// <summary>
/// Full TUF v1.0 signed metadata bundle passed into <see cref="DeltaPatchVerifier" />.
/// </summary>
internal sealed record TufDeltaUpdateBundle(
    TufTrustedState TrustedState,
    TufSignedEnvelope<TufTimestampMetadata> Timestamp,
    TufSignedEnvelope<TufSnapshotMetadata> Snapshot,
    TufSignedEnvelope<TufTargetsMetadata> Targets,
    IReadOnlyList<TufSignedEnvelope<TufRootMetadata>>? RootRotationChain = null);

/// <summary>
/// Structured outcome of a TUF-authenticated binary delta patch verification and atomic commit.
/// </summary>
internal sealed record DeltaPatchApplicationResult(
    bool Success,
    string? FailureCode,
    string? ErrorMessage,
    string? VerifiedBaseSha256,
    string? VerifiedDeltaSha256,
    string? VerifiedTargetSha256,
    long PatchedBytesWritten,
    TufTrustedState? UpdatedTufState)
{
    internal static DeltaPatchApplicationResult Failed(
        string code,
        string message,
        string? baseSha = null,
        string? deltaSha = null)
        => new(false, code, message, baseSha, deltaSha, null, 0, null);

    internal static DeltaPatchApplicationResult Succeeded(
        string baseSha,
        string deltaSha,
        string targetSha,
        long bytesWritten,
        TufTrustedState updatedState)
        => new(true, null, null, baseSha, deltaSha, targetSha, bytesWritten, updatedState);
}

/// <summary>
/// TUF v1.0-authenticated binary delta patch verifier and atomic applier for Project Kongor updates.
/// Verifies the full 4-role TUF chain (<see cref="TufMetadataVerifier" />), enforces BaseSha256 -> DeltaSha256 -> TargetSha256
/// cryptographic chaining, stages the patched output to a temporary file, and atomically commits via
/// <see cref="ShadowCopyUpdateService" /> only after full digest verification passes.
/// </summary>
internal static class DeltaPatchVerifier
{
    private static readonly byte[] PatchMagic = "PKVDELTA"u8.ToArray();
    private const uint FormatVersion = 1;
    private const byte OpCopyFromBase = 0x01;
    private const byte OpInsertLiteral = 0x02;
    private const int HeaderSizeBytes = 28; // 8 magic + 4 version + 8 baseLen + 8 targetLen
    private const long MaxSupportedTargetBytes = 2L * 1024 * 1024 * 1024; // 2 GiB bomb cap

    /// <summary>
    /// Builds a deterministic <c>PKVDELTA</c> v1 binary patch transforming <paramref name="baseBytes" /> into <paramref name="targetBytes" />
    /// using common prefix/suffix copy instructions plus literal insert for the changed interior span.
    /// </summary>
    internal static byte[] CreateDeltaPatch(ReadOnlySpan<byte> baseBytes, ReadOnlySpan<byte> targetBytes)
    {
        int prefixLen = 0;
        int minLen = Math.Min(baseBytes.Length, targetBytes.Length);
        while (prefixLen < minLen && baseBytes[prefixLen] == targetBytes[prefixLen])
        {
            prefixLen++;
        }

        int suffixLen = 0;
        int maxSuffix = Math.Min(baseBytes.Length - prefixLen, targetBytes.Length - prefixLen);
        while (suffixLen < maxSuffix &&
               baseBytes[baseBytes.Length - 1 - suffixLen] == targetBytes[targetBytes.Length - 1 - suffixLen])
        {
            suffixLen++;
        }

        int insertLen = targetBytes.Length - prefixLen - suffixLen;

        using MemoryStream ms = new();
        Span<byte> header = stackalloc byte[HeaderSizeBytes];
        PatchMagic.CopyTo(header[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..20], (ulong)baseBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..28], (ulong)targetBytes.Length);
        ms.Write(header);

        if (prefixLen > 0)
        {
            WriteCopyOp(ms, 0UL, (uint)prefixLen);
        }

        if (insertLen > 0)
        {
            WriteInsertOp(ms, targetBytes.Slice(prefixLen, insertLen));
        }

        if (suffixLen > 0)
        {
            ulong suffixBaseOffset = (ulong)(baseBytes.Length - suffixLen);
            WriteCopyOp(ms, suffixBaseOffset, (uint)suffixLen);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Verifies a TUF v1.0 update bundle, validates <c>BaseSha256</c> against <paramref name="baseFilePath" />,
    /// validates signed <c>DeltaSha256</c> against <paramref name="deltaPatchPath" />, applies the delta to a staged file,
    /// verifies the reconstructed <c>TargetSha256</c> and length, and atomically commits to <paramref name="targetFilePath" />.
    /// </summary>
    internal static async Task<DeltaPatchApplicationResult> VerifyAndApplySignedDeltaAsync(
        TufDeltaUpdateBundle bundle,
        string targetPathInMetadata,
        string baseFilePath,
        string deltaPatchPath,
        string targetFilePath,
        IShadowCopyUpdateService? shadowCopyService = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPathInMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPatchPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        // Log TUF role anchor metadata for full traceability
        TufRootMetadata pinnedRoot = bundle.TrustedState.TrustedRoot.Signed;
        TufRoleDefinition? targetsRole = pinnedRoot.Roles.TryGetValue(TufRole.Targets, out TufRoleDefinition? def) ? def : null;
        TufMetaFileInfo snapRef = bundle.Timestamp.Signed.SnapshotMeta;
        int rootKeyCount = pinnedRoot.Keys.Count;
        string targetSignerSummary = string.Join(",", bundle.Targets.Signatures.Select((TufSignature s) => s.KeyId));
        Trace.WriteLine(
            $"[DELTA-VERIFIER] Starting TUF-authenticated delta verification for '{targetPathInMetadata}': " +
            $"rootKeys={rootKeyCount}, targetsThreshold={targetsRole?.Threshold ?? 0}, snapshotRef=v{snapRef.Version}, targetSigners=[{targetSignerSummary}].");

        // Ensure TufKey metadata lookup is audited when present
        if (targetsRole is not null && targetsRole.KeyIds.Count > 0 && pinnedRoot.Keys.TryGetValue(targetsRole.KeyIds[0], out TufKey? primaryTargetsKey))
        {
            Trace.WriteLine($"[DELTA-VERIFIER] Primary targets role key: {primaryTargetsKey.KeyId} ({primaryTargetsKey.KeyType}).");
        }

        // 1. Verify complete TUF v1.0 4-role chain (Root -> Timestamp -> Snapshot -> Targets)
        TufVerificationResult tufResult = TufMetadataVerifier.VerifyUpdateBundle(
            bundle.TrustedState,
            bundle.Timestamp,
            bundle.Snapshot,
            bundle.Targets,
            bundle.RootRotationChain,
            timeProvider);

        if (!tufResult.IsValid || tufResult.VerifiedTargets is null || tufResult.UpdatedState is null)
        {
            return DeltaPatchApplicationResult.Failed(
                tufResult.FailureCode ?? "TUF_VERIFICATION_FAILED",
                tufResult.FailureReason ?? "TUF v1.0 metadata chain verification failed.");
        }

        // 2. Locate target entry and ensure delta hash-chain fields are present
        string normalizedTargetKey = targetPathInMetadata.Replace('\\', '/');
        if (!tufResult.VerifiedTargets.Targets.TryGetValue(normalizedTargetKey, out TufTargetFileMetadata? targetMeta) || targetMeta is null)
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_TARGET_NOT_IN_TUF",
                $"Target '{normalizedTargetKey}' is not listed in TUF-verified targets v{tufResult.VerifiedTargets.Version}.");
        }

        if (string.IsNullOrWhiteSpace(targetMeta.BaseSha256Hex) || string.IsNullOrWhiteSpace(targetMeta.DeltaSha256Hex))
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_METADATA_INCOMPLETE",
                $"TUF target '{normalizedTargetKey}' is missing required BaseSha256Hex or DeltaSha256Hex delta chain digests.");
        }

        if (targetMeta.Length < 0 || targetMeta.Length > MaxSupportedTargetBytes)
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_TARGET_LENGTH_INVALID",
                $"TUF target '{normalizedTargetKey}' declares invalid length {targetMeta.Length}.");
        }

        // 3. Validate local base file exists and matches TUF-pinned BaseSha256
        if (!File.Exists(baseFilePath))
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_BASE_MISSING",
                $"Base file not found at '{baseFilePath}'.");
        }

        string actualBaseSha = await ComputeFileSha256HexAsync(baseFilePath, cancellationToken).ConfigureAwait(false);
        if (!TufMetadataVerifier.FixedTimeHexEquals(actualBaseSha, targetMeta.BaseSha256Hex))
        {
            Trace.WriteLine(
                $"[DELTA-VERIFIER][REJECT] Base SHA-256 mismatch for '{baseFilePath}': actual='{actualBaseSha}', expected='{targetMeta.BaseSha256Hex}'.");
            return DeltaPatchApplicationResult.Failed(
                "DELTA_BASE_HASH_MISMATCH",
                $"Base file '{baseFilePath}' SHA-256 mismatch (actual='{actualBaseSha}', expected='{targetMeta.BaseSha256Hex}').",
                baseSha: actualBaseSha);
        }

        // 4. Validate delta patch file exists, matches optional DeltaLength, and matches TUF-pinned DeltaSha256
        if (!File.Exists(deltaPatchPath))
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_PATCH_MISSING",
                $"Delta patch file not found at '{deltaPatchPath}'.",
                baseSha: actualBaseSha);
        }

        FileInfo deltaInfo = new(deltaPatchPath);
        if (targetMeta.DeltaLength.HasValue && deltaInfo.Length != targetMeta.DeltaLength.Value)
        {
            return DeltaPatchApplicationResult.Failed(
                "DELTA_PATCH_LENGTH_MISMATCH",
                $"Delta patch '{deltaPatchPath}' byte length {deltaInfo.Length} != TUF-pinned {targetMeta.DeltaLength.Value}.",
                baseSha: actualBaseSha);
        }

        string actualDeltaSha = await ComputeFileSha256HexAsync(deltaPatchPath, cancellationToken).ConfigureAwait(false);
        if (!TufMetadataVerifier.FixedTimeHexEquals(actualDeltaSha, targetMeta.DeltaSha256Hex))
        {
            Trace.WriteLine(
                $"[DELTA-VERIFIER][REJECT] Delta SHA-256 mismatch for '{deltaPatchPath}': actual='{actualDeltaSha}', expected='{targetMeta.DeltaSha256Hex}'.");
            return DeltaPatchApplicationResult.Failed(
                "DELTA_PATCH_HASH_MISMATCH",
                $"Delta patch '{deltaPatchPath}' SHA-256 mismatch (actual='{actualDeltaSha}', expected='{targetMeta.DeltaSha256Hex}').",
                baseSha: actualBaseSha,
                deltaSha: actualDeltaSha);
        }

        // 5. Apply binary delta to isolated staged file while computing SHA-256 incrementally
        string? targetDir = Path.GetDirectoryName(Path.GetFullPath(targetFilePath));
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string stagedPath = $"{targetFilePath}.delta.staged.{Guid.NewGuid():N}";
        try
        {
            (long bytesWritten, string patchedSha) = await ApplyDeltaFileToStagedAsync(
                baseFilePath,
                deltaPatchPath,
                stagedPath,
                targetMeta.Length,
                cancellationToken).ConfigureAwait(false);

            // 6. Verify reconstructed output length + constant-time TargetSha256 against TUF-signed target metadata
            if (bytesWritten != targetMeta.Length)
            {
                TryDeleteQuietly(stagedPath);
                return DeltaPatchApplicationResult.Failed(
                    "DELTA_TARGET_LENGTH_MISMATCH",
                    $"Patched output length {bytesWritten} != TUF-pinned target length {targetMeta.Length}.",
                    baseSha: actualBaseSha,
                    deltaSha: actualDeltaSha);
            }

            if (!TufMetadataVerifier.FixedTimeHexEquals(patchedSha, targetMeta.Sha256Hex))
            {
                TryDeleteQuietly(stagedPath);
                Trace.WriteLine(
                    $"[DELTA-VERIFIER][REJECT] Patched target SHA-256 mismatch: actual='{patchedSha}', expected='{targetMeta.Sha256Hex}'. Staged artifact deleted; target untouched.");
                return DeltaPatchApplicationResult.Failed(
                    "DELTA_TARGET_HASH_MISMATCH",
                    $"Reconstructed target SHA-256 '{patchedSha}' != TUF-pinned '{targetMeta.Sha256Hex}'.",
                    baseSha: actualBaseSha,
                    deltaSha: actualDeltaSha);
            }

            // 7. Atomically commit verified target via [SR-06] ShadowCopyUpdateService
            IShadowCopyUpdateService committer = shadowCopyService ?? ShadowCopyUpdateService.Instance;
            committer.AtomicReplaceFile(stagedPath, targetFilePath);

            Trace.WriteLine(
                $"[DELTA-VERIFIER] Committed TUF-verified delta target '{targetFilePath}' ({bytesWritten:N0} bytes, sha256={patchedSha}, tufTargets=v{tufResult.VerifiedTargets.Version}).");

            return DeltaPatchApplicationResult.Succeeded(
                actualBaseSha,
                actualDeltaSha,
                patchedSha,
                bytesWritten,
                tufResult.UpdatedState);
        }
        catch (OperationCanceledException)
        {
            TryDeleteQuietly(stagedPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteQuietly(stagedPath);
            Trace.WriteLine($"[DELTA-VERIFIER][ERROR] Delta patch application failed for '{targetPathInMetadata}': {ex.Message}");
            return DeltaPatchApplicationResult.Failed(
                "DELTA_PATCH_APPLY_ERROR",
                $"Failed applying delta patch: {ex.Message}",
                baseSha: actualBaseSha,
                deltaSha: actualDeltaSha);
        }
    }

    private static async Task<(long BytesWritten, string Sha256Hex)> ApplyDeltaFileToStagedAsync(
        string baseFilePath,
        string deltaPatchPath,
        string stagedPath,
        long expectedTargetLength,
        CancellationToken cancellationToken)
    {
        await using FileStream baseStream = new(baseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using FileStream patchStream = new(deltaPatchPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using FileStream stagedStream = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] header = new byte[HeaderSizeBytes];
        await ReadExactAsync(patchStream, header, cancellationToken).ConfigureAwait(false);

        if (!header.AsSpan(0, 8).SequenceEqual(PatchMagic))
        {
            throw new InvalidDataException("Invalid delta patch header magic (expected 'PKVDELTA').");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported delta patch format version {version} (expected {FormatVersion}).");
        }

        ulong declaredBaseLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12, 8));
        ulong declaredTargetLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20, 8));

        if (declaredBaseLength != (ulong)baseStream.Length)
        {
            throw new InvalidDataException($"Delta header base length {declaredBaseLength} != actual base file length {baseStream.Length}.");
        }

        if (declaredTargetLength != (ulong)expectedTargetLength)
        {
            throw new InvalidDataException($"Delta header target length {declaredTargetLength} != TUF-pinned target length {expectedTargetLength}.");
        }

        byte[] opCodeBuf = new byte[1];
        byte[] copyArgsBuf = new byte[12];
        byte[] lenBuf = new byte[4];
        byte[] copyChunk = ArrayPool<byte>.Shared.Rent(65536);
        long totalWritten = 0;

        try
        {
            while (true)
            {
                int opRead = await patchStream.ReadAsync(opCodeBuf.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (opRead == 0)
                {
                    break;
                }

                byte op = opCodeBuf[0];
                if (op == OpCopyFromBase)
                {
                    await ReadExactAsync(patchStream, copyArgsBuf, cancellationToken).ConfigureAwait(false);
                    ulong baseOffset = BinaryPrimitives.ReadUInt64LittleEndian(copyArgsBuf.AsSpan(0, 8));
                    uint copyLen = BinaryPrimitives.ReadUInt32LittleEndian(copyArgsBuf.AsSpan(8, 4));

                    if (baseOffset > (ulong)baseStream.Length || copyLen > (ulong)baseStream.Length - baseOffset)
                    {
                        throw new InvalidDataException($"Delta COPY operation out of bounds (offset={baseOffset}, len={copyLen}, baseLen={baseStream.Length}).");
                    }

                    if (totalWritten + copyLen > expectedTargetLength)
                    {
                        throw new InvalidDataException("Delta COPY operation exceeds declared target length.");
                    }

                    baseStream.Position = (long)baseOffset;
                    uint remaining = copyLen;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, (uint)copyChunk.Length);
                        await ReadExactAsync(baseStream, copyChunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        await stagedStream.WriteAsync(copyChunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        hasher.AppendData(copyChunk, 0, toRead);
                        totalWritten += toRead;
                        remaining -= (uint)toRead;
                    }
                }
                else if (op == OpInsertLiteral)
                {
                    await ReadExactAsync(patchStream, lenBuf, cancellationToken).ConfigureAwait(false);
                    uint insertLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

                    if (totalWritten + insertLen > expectedTargetLength)
                    {
                        throw new InvalidDataException("Delta INSERT operation exceeds declared target length.");
                    }

                    uint remaining = insertLen;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, (uint)copyChunk.Length);
                        await ReadExactAsync(patchStream, copyChunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        await stagedStream.WriteAsync(copyChunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        hasher.AppendData(copyChunk, 0, toRead);
                        totalWritten += toRead;
                        remaining -= (uint)toRead;
                    }
                }
                else
                {
                    throw new InvalidDataException($"Unknown delta patch opcode 0x{op:X2}.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyChunk);
        }

        await stagedStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        string shaHex = Convert.ToHexStringLower(hasher.GetHashAndReset());
        return (totalWritten, shaHex);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream while reading {destination.Length} bytes (read {offset}).");
            }

            offset += read;
        }
    }

    private static async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static void WriteCopyOp(Stream stream, ulong baseOffset, uint length)
    {
        Span<byte> buf = stackalloc byte[13];
        buf[0] = OpCopyFromBase;
        BinaryPrimitives.WriteUInt64LittleEndian(buf[1..9], baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[9..13], length);
        stream.Write(buf);
    }

    private static void WriteInsertOp(Stream stream, ReadOnlySpan<byte> literal)
    {
        Span<byte> buf = stackalloc byte[5];
        buf[0] = OpInsertLiteral;
        BinaryPrimitives.WriteUInt32LittleEndian(buf[1..5], (uint)literal.Length);
        stream.Write(buf);
        stream.Write(literal);
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DELTA-VERIFIER] Warning: could not delete staged delta file '{path}': {ex.Message}");
        }
    }
}
