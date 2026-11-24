// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: BinaryDeltaPatchEngineTests.cs
// System: Kongor Protocol Core Unit Tests - Binary Delta Engine
// ============================================================================

using System.IO;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Patching;
using Xunit;

namespace Kongor.Core.Tests.Patching;

public sealed class BinaryDeltaPatchEngineTests
{
    [Fact]
    public void CreateAndApplyDelta_SmallTextChanges_RoundtripsExactByteForByte()
    {
        // Arrange
        BinaryDeltaPatchEngine engine = new();
        byte[] source = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog. Version 4.10.1.0 release.");
        byte[] target = Encoding.UTF8.GetBytes("The quick brown fox jumps over the active dog. Version 4.10.2.0 release.");
        string expectedTargetSha = Convert.ToHexStringLower(SHA256.HashData(target));

        // Act
        byte[] delta = engine.CreateDelta(source, target);
        byte[] reconstructed = engine.ApplyDelta(source, delta, expectedTargetSha);

        // Assert
        Assert.NotEmpty(delta);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void CreateAndApplyDelta_LargeBinaryWithSmallChange_ReducesSizeByOver95Percent()
    {
        // Arrange: 512 KB simulated DLL binary
        BinaryDeltaPatchEngine engine = new();
        byte[] source = new byte[512 * 1024];
        Random.Shared.NextBytes(source);

        byte[] target = new byte[source.Length];
        source.CopyTo(target.AsSpan());

        // Modify 64 bytes at offset 120,000 (simulating balance tweak in game_x64.dll)
        for (int i = 0; i < 64; i++)
        {
            target[120_000 + i] = (byte)(target[120_000 + i] ^ 0xFF);
        }

        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(target));

        // Act
        byte[] delta = engine.CreateDelta(source, target);
        byte[] reconstructed = engine.ApplyDelta(source, delta, expectedSha);

        // Assert
        Assert.Equal(target, reconstructed);

        // Delta should be vastly smaller than the 512 KB payload (>95% reduction)
        double reduction = (1.0 - ((double)delta.Length / target.Length)) * 100.0;
        Assert.True(reduction > 95.0, $"Expected >95% reduction, but got {reduction:F1}% (delta size: {delta.Length} bytes)");
    }

    [Fact]
    public void ApplyDelta_CorruptDeltaPayload_ThrowsInvalidDataException()
    {
        // Arrange
        BinaryDeltaPatchEngine engine = new();
        byte[] source = "Initial source buffer with plenty of text."u8.ToArray();
        byte[] target = "Updated target buffer with plenty of new text."u8.ToArray();
        byte[] delta = engine.CreateDelta(source, target);

        // Corrupt the body
        delta[^5] = (byte)(delta[^5] ^ 0xFF);

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => engine.ApplyDelta(source, delta));
    }

    [Fact]
    public void ApplyDelta_SourceLengthMismatch_ThrowsInvalidDataException()
    {
        // Arrange
        BinaryDeltaPatchEngine engine = new();
        byte[] source = "Source buffer"u8.ToArray();
        byte[] target = "Target buffer"u8.ToArray();
        byte[] delta = engine.CreateDelta(source, target);

        byte[] mismatchedSource = "Short"u8.ToArray();

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => engine.ApplyDelta(mismatchedSource, delta));
    }

    [Fact]
    public async Task ApplyDeltaToFileAsync_ValidDelta_ReplacesTargetAtomically()
    {
        // Arrange
        BinaryDeltaPatchEngine engine = new();
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_patch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string sourceFile = Path.Combine(tempDir, "game_x64.dll");
            string targetFile = Path.Combine(tempDir, "game_x64.dll"); // in-place update

            byte[] sourceBytes = "Original game binary code section v4.10.1"u8.ToArray();
            byte[] targetBytes = "Modified game binary code section v4.10.2 (balanced)"u8.ToArray();
            await File.WriteAllBytesAsync(sourceFile, sourceBytes);

            byte[] deltaBytes = engine.CreateDelta(sourceBytes, targetBytes);
            using MemoryStream deltaStream = new(deltaBytes);

            string expectedSha = Convert.ToHexStringLower(SHA256.HashData(targetBytes));

            // Act
            await engine.ApplyDeltaToFileAsync(sourceFile, deltaStream, targetFile, expectedSha);

            // Assert
            Assert.True(File.Exists(targetFile));
            byte[] patchedFileBytes = await File.ReadAllBytesAsync(targetFile);
            Assert.Equal(targetBytes, patchedFileBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
