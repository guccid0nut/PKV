// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Core.Tests.Native;

public sealed class AobScannerTests
{
    [Fact]
    public void AobPattern_Parse_ValidSignatureWithWildcards_ParsesCorrectly()
    {
        // Arrange
        string sig = "48 89 5C 24 08 ?? ?? ?? 48 89 74 24";

        // Act
        var pattern = AobPattern.Parse(sig);

        // Assert
        Assert.Equal(12, pattern.Length);
        Assert.Equal(0x48, pattern.Pattern[0]);
        Assert.True(pattern.Mask[0]);
        Assert.Equal(0x08, pattern.Pattern[4]);
        Assert.True(pattern.Mask[4]);

        // Wildcards
        Assert.False(pattern.Mask[5]);
        Assert.False(pattern.Mask[6]);
        Assert.False(pattern.Mask[7]);

        Assert.Equal(0x48, pattern.Pattern[8]);
        Assert.True(pattern.Mask[8]);
        Assert.Equal(0, pattern.FirstExactIndex);
    }

    [Fact]
    public void AobPattern_Parse_InvalidHexToken_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => AobPattern.Parse("48 ZZ 5C"));
    }

    [Fact]
    public void FindPattern_ExactMatch_ReturnsCorrectOffset()
    {
        // Arrange
        byte[] buffer = [0x90, 0x90, 0x48, 0x89, 0x5C, 0x24, 0x08, 0xCC];
        var pattern = AobPattern.Parse("48 89 5C 24 08");

        // Act
        int offset = AobScanner.FindPattern(buffer, pattern);

        // Assert
        Assert.Equal(2, offset);
    }

    [Fact]
    public void FindPattern_WithWildcards_MatchesAnyWildcardByte()
    {
        // Arrange
        byte[] buffer = [0x00, 0x11, 0x48, 0x89, 0xAA, 0xBB, 0xCC, 0x48, 0x89, 0x22];
        var pattern = AobPattern.Parse("48 89 ?? ?? ?? 48 89");

        // Act
        int offset = AobScanner.FindPattern(buffer, pattern);

        // Assert
        Assert.Equal(2, offset);
    }

    [Fact]
    public void FindPattern_PatternAtStart_ReturnsZero()
    {
        byte[] buffer = [0x48, 0x89, 0x5C, 0x90];
        var pattern = AobPattern.Parse("48 89 5C");

        Assert.Equal(0, AobScanner.FindPattern(buffer, pattern));
    }

    [Fact]
    public void FindPattern_PatternAtEnd_ReturnsExpectedOffset()
    {
        byte[] buffer = [0x90, 0x90, 0x48, 0x89, 0x5C];
        var pattern = AobPattern.Parse("48 89 5C");

        Assert.Equal(2, AobScanner.FindPattern(buffer, pattern));
    }

    [Fact]
    public void FindPattern_NotFound_ReturnsMinusOne()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04];
        var pattern = AobPattern.Parse("FF FE");

        Assert.Equal(-1, AobScanner.FindPattern(buffer, pattern));
    }

    [Fact]
    public void FindAll_MultipleOccurrences_ReturnsAllIndices()
    {
        byte[] buffer = [0xAA, 0xBB, 0x00, 0xAA, 0xBB, 0x00, 0xAA, 0xBB];
        var pattern = AobPattern.Parse("AA BB");

        List<int> matches = AobScanner.FindAll(buffer, pattern);

        Assert.Equal(3, matches.Count);
        Assert.Equal(0, matches[0]);
        Assert.Equal(3, matches[1]);
        Assert.Equal(6, matches[2]);
    }

    [Fact]
    public void VerifyFastPathOrScan_FastPathHits_ReturnsFastPathSuccessImmediately()
    {
        // Arrange
        byte[] buffer = new byte[100];
        buffer[42] = 0x7C;
        buffer[43] = 0x96;

        var pattern = AobPattern.Parse("7C 96");
        byte[] expected = [0x7C, 0x96];

        // Act
        var result = AobScanner.VerifyFastPathOrScan(buffer, 42, pattern, expected);

        // Assert
        Assert.True(result.Found);
        Assert.Equal(42, result.Offset);
        Assert.True(result.WasFastPath);
    }

    [Fact]
    public void VerifyFastPathOrScan_FastPathMisses_FallsBackToScanAndFindsShiftedLocation()
    {
        // Arrange: Hint points to 42, but due to recompile, it is at 80
        byte[] buffer = new byte[100];
        buffer[80] = 0x7C;
        buffer[81] = 0x96;

        var pattern = AobPattern.Parse("7C 96");
        byte[] expected = [0x7C, 0x96];

        // Act
        var result = AobScanner.VerifyFastPathOrScan(buffer, 42, pattern, expected);

        // Assert
        Assert.True(result.Found);
        Assert.Equal(80, result.Offset);
        Assert.False(result.WasFastPath);
    }

    [Fact]
    public void VerifyFastPathOrScan_AmbiguousMatch_RejectsForSafety()
    {
        // Arrange: Pattern occurs twice, hint points to garbage
        byte[] buffer = new byte[100];
        buffer[20] = 0x7C;
        buffer[21] = 0x96;
        buffer[60] = 0x7C;
        buffer[61] = 0x96;

        var pattern = AobPattern.Parse("7C 96");
        byte[] expected = [0x7C, 0x96];

        // Act: Hint points to 0 (garbage)
        var result = AobScanner.VerifyFastPathOrScan(buffer, 0, pattern, expected);

        // Assert
        Assert.False(result.Found);
        Assert.Equal(-2, result.Offset);
        Assert.Equal(2, result.MatchCount);
        Assert.Contains("ambiguous", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanModuleAob_WithInvalidHandle_ReturnsNotFound()
    {
        var pattern = AobPattern.Parse("48 89 5C 24 08");
        var result = NativeProcessInjectionService.ScanModuleAob(nint.Zero, nint.Zero, 0, pattern);

        Assert.False(result.Found);
        Assert.Equal(-1, result.Offset);
    }

    [Fact]
    public void AobPattern_EngineCrashSiteSignatures_ParseAndMatchCorrectly()
    {
        // 1. VidD3D9 Site 1: Mode 0 2D quad/line sampler overflow signature
        string sig1 = "01 41 B8 07 00 00 00 41 8B D5 FF 90 28 02 00 00";
        var p1 = AobPattern.Parse(sig1);
        Assert.Equal(16, p1.Length);

        // 2. VidD3D9 Site 2: Mode 1 line sampler overflow signature
        string sig2 = "3B EA 8B 74 24 58 44 8B A5 E8 00 00 00";
        var p2 = AobPattern.Parse(sig2);
        Assert.Equal(13, p2.Length);

        // 3. VidD3D9 Site 4: Sampler 1 mask signature with wildcards
        string sig4 = "00 C6 00 01 8D 4E 01 FF 15 ?? ?? ?? ?? 48 8B 0D";
        var p4 = AobPattern.Parse(sig4);
        Assert.Equal(16, p4.Length);
        Assert.False(p4.Mask[9]);
        Assert.False(p4.Mask[10]);

        // Synthesize buffer containing sig4 and find it
        byte[] testBuffer = new byte[64];
        testBuffer[10] = 0x00;
        testBuffer[11] = 0xC6;
        testBuffer[12] = 0x00;
        testBuffer[13] = 0x01;
        testBuffer[14] = 0x8D;
        testBuffer[15] = 0x4E;
        testBuffer[16] = 0x01;
        testBuffer[17] = 0xFF;
        testBuffer[18] = 0x15;
        testBuffer[19] = 0xAA; // wildcard
        testBuffer[20] = 0xBB; // wildcard
        testBuffer[21] = 0xCC; // wildcard
        testBuffer[22] = 0xDD; // wildcard
        testBuffer[23] = 0x48;
        testBuffer[24] = 0x8B;
        testBuffer[25] = 0x0D;

        int foundOffset = AobScanner.FindPattern(testBuffer, p4);
        Assert.Equal(10, foundOffset);
    }
}
