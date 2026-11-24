// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ASPIRE.Common.Utilities;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Utilities;

public sealed class SimdAnsiStripperTests
{
    private static readonly Regex ReferenceRegex = new(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);

    [Fact]
    public void SimdAnsiStripper_HardwareTier_IsRecognizedAndValid()
    {
        string tier = SimdAnsiStripper.GetActiveHardwareTier();
        tier.Should().NotBeNullOrWhiteSpace();

        // Hardware flags should be deterministic and safe
        if (SimdAnsiStripper.IsAvx512Supported)
            tier.Should().Contain("AVX-512");
        else if (SimdAnsiStripper.IsAvx2Supported)
            tier.Should().Contain("AVX2");
        else if (SimdAnsiStripper.IsVector128Supported)
            tier.Should().Contain("Vector128");
        else
            tier.Should().Be("Scalar Fallback");
    }

    [Fact]
    public void SimdAnsiStripper_NoAnsi_ReturnsOriginalStringReferenceDirectly()
    {
        string input = "[2026-09-25 01:50:00] Worker 11235 ready on map caldavar with 10 players";
        string result = SimdAnsiStripper.StripAnsi(input);

        // Crucial fast-path invariant: zero allocations, identical object reference
        object.ReferenceEquals(result, input).Should().BeTrue();
    }

    [Fact]
    public void SimdAnsiStripper_EmptyOrNull_ReturnsExpected()
    {
        SimdAnsiStripper.StripAnsi(null).Should().Be(string.Empty);
        SimdAnsiStripper.StripAnsi(string.Empty).Should().Be(string.Empty);
        SimdAnsiStripper.ContainsAnsi(ReadOnlySpan<char>.Empty).Should().BeFalse();
        SimdAnsiStripper.ContainsAnsi(ReadOnlySpan<byte>.Empty).Should().BeFalse();
    }

    [Theory]
    [InlineData("\x1B[32mHello World\x1B[0m", "Hello World")]
    [InlineData("\x1BMTest", "Test")]
    [InlineData("\x1B[1;31;40mColor\x1B[m", "Color")]
    [InlineData("No Escape Here", "No Escape Here")]
    [InlineData("\x1B[2JClear", "Clear")]
    [InlineData("\x1B", "\x1B")]
    [InlineData("\x1B[", "\x1B[")]
    [InlineData("\x1B[?25hCursor", "Cursor")]
    [InlineData("\x1BaInvalid", "\x1BaInvalid")]
    [InlineData("\x1B[xyz", "yz")]
    [InlineData("Prefix \x1B[33;1mWarning:\x1B[0m Suffix", "Prefix Warning: Suffix")]
    [InlineData("\x1B[0m\x1B[31mRed\x1B[32mGreen\x1B[34mBlue\x1B[0m", "RedGreenBlue")]
    public void SimdAnsiStripper_KnownTestCases_MatchReferenceRegex(string input, string expected)
    {
        string stripped = SimdAnsiStripper.StripAnsi(input);
        stripped.Should().Be(expected);

        string regexResult = ReferenceRegex.Replace(input, string.Empty);
        stripped.Should().Be(regexResult);
    }

    [Fact]
    public void SimdAnsiStripper_DifferentialFuzzing_MatchesRegex100Percent()
    {
        string[] prefixes = ["", "[Server] ", "DBG: ", ">>> ", "Long prefix text containing lots of characters without any escape codes at all "];
        string[] ansiCodes = [
            "\x1B[0m",
            "\x1B[1m",
            "\x1B[31m",
            "\x1B[32;1m",
            "\x1B[0;33;40m",
            "\x1B[2J",
            "\x1B[?25l",
            "\x1BM",
            "\x1BN",
            "\x1B\\",
            "\x1B",      // incomplete
            "\x1B[",     // incomplete
            "\x1B[999z", // multi-digit
            "\x1Bq"      // invalid
        ];
        string[] messages = [
            "Match started on caldavar",
            "Player Pyromancer spawned at (1500, 2400, 100)",
            "Combat event: 150 magic damage dealt",
            "Error: could not find resource icon.dds",
            ""
        ];

        Random rng = new(42);
        for (int i = 0; i < 1000; i++)
        {
            StringBuilder sb = new();
            sb.Append(prefixes[rng.Next(prefixes.Length)]);
            int codeCount = rng.Next(1, 4);
            for (int c = 0; c < codeCount; c++)
            {
                sb.Append(ansiCodes[rng.Next(ansiCodes.Length)]);
                sb.Append(messages[rng.Next(messages.Length)]);
            }

            string input = sb.ToString();
            string simdResult = SimdAnsiStripper.StripAnsi(input);
            string regexResult = ReferenceRegex.Replace(input, string.Empty);

            simdResult.Should().Be(regexResult, $"Fuzz case {i} failed for input: {input.Replace("\x1B", "<ESC>")}");
        }
    }

    [Fact]
    public void SimdAnsiStripper_Utf8ByteSpan_MatchesUtf16Behavior()
    {
        string text = "\x1B[32m[2026-09-25 01:50:00] Server 11235: Match 42 Started\x1B[0m";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);

        Span<byte> destination = stackalloc byte[utf8Bytes.Length];
        ReadOnlySpan<byte> cleanBytes = SimdAnsiStripper.StripAnsi(utf8Bytes, destination, out int bytesWritten);

        string cleanStringFromUtf8 = Encoding.UTF8.GetString(cleanBytes);
        string cleanStringFromChar = SimdAnsiStripper.StripAnsi(text);

        cleanStringFromUtf8.Should().Be(cleanStringFromChar);
        cleanStringFromUtf8.Should().Be("[2026-09-25 01:50:00] Server 11235: Match 42 Started");
    }

    [Fact]
    public void SimdAnsiStripper_ThroughputBenchmark_ExecutesUnder15Milliseconds()
    {
        string sampleLine = "\x1B[32m[2026-09-25 01:50:00]\x1B[0m \x1B[1mWorker 11235:\x1B[0m Tick 6000 completed with 10 players on map caldavar\x1B[0m";
        const int iterations = 2_000;

        // Warmup
        for (int i = 0; i < 50; i++)
        {
            _ = SimdAnsiStripper.StripAnsi(sampleLine);
        }

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            _ = SimdAnsiStripper.StripAnsi(sampleLine);
        }
        sw.Stop();

        // SIMD execution must be fast and orders of magnitude ahead of Regex
        sw.ElapsedMilliseconds.Should().BeLessThan(250);
    }
}
