// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Fuzzing;

/// <summary>
///     Property-Based and Adversarial Mutation Protocol Fuzz Testing for <see cref="PhpDeserializer" /> and <see cref="PhpSerializer" />.
///     Ensures memory safety, recursion protection, boundary validation, and zero unhandled runtime crashes.
/// </summary>
public sealed class PhpDeserializerFuzzTests
{
    private const int RandomFuzzIterations = 10_000;
    private const int MutationFuzzIterations = 5_000;
    private const int RoundTripFuzzIterations = 1_000;

    [Trait("Category", "Manual")]
    [Fact]
    public void Fuzz_RandomByteSpans_NeverThrowsUnhandledMemorySafetyExceptions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Random rng = new(42_1337);
        byte[] buffer = new byte[4096];

        for (int i = 0; i < RandomFuzzIterations; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));
            ReadOnlySpan<byte> utf8Span = buffer.AsSpan(0, length);

            // 1. Fuzz UTF-8 Byte Span Overload
            try
            {
                object? result = PhpDeserializer.Deserialize(utf8Span);
                // Result can be null, primitive, or PhpHashtable
            }
            catch (FormatException ex) when (ex.Message.Contains("Exceeded maximum recursion depth of 32"))
            {
                // Expected recursion bomb protection
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception during UTF-8 span fuzzing at iteration {i} (len={length}): {ex.GetType().FullName} - {ex.Message}");
            }

            // 2. Fuzz String / Char Span Overload
            string stringPayload = Encoding.UTF8.GetString(utf8Span);
            try
            {
                object? result = PhpDeserializer.Deserialize(stringPayload);
            }
            catch (FormatException ex) when (ex.Message.Contains("Exceeded maximum recursion depth of 32"))
            {
                // Expected recursion bomb protection
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unhandled exception during string span fuzzing at iteration {i} (len={length}): {ex.GetType().FullName} - {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_AdversarialBoundaries_HandlesMaliciousPayloadsGracefully()
    {
        string[] adversarialPayloads =
        [
            "",
            "   ",
            "\0\0\0\0",
            "N;",
            "n;",
            "N",
            "b:1;",
            "b:0;",
            "b:;",
            "b:999;",
            "b:-1;",
            "b:true;",
            "i:0;",
            "i:1234567890;",
            "i:-1234567890;",
            "i:999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999;",
            "i:--12;",
            "i:++12;",
            "i:12abc;",
            "i:;",
            "i:",
            "d:0.0;",
            "d:123.456;",
            "d:-123.456;",
            "d:1.7976931348623157E+308;",
            "d:NaN;",
            "d:Infinity;",
            "d:-Infinity;",
            "d:12.34.56;",
            "d:;",
            "d:",
            "s:0:\"\";",
            "s:5:\"hello\";",
            "s:5:\"hello\"",
            "s:10:\"hello\";",
            "s:100:\"short\";",
            "s:2147483647:\"massive_length_attack\";",
            "s:999999999:\"overflow_attack\";",
            "s:-1:\"negative_length\";",
            "s:-2147483648:\"negative_min_int\";",
            "s:5:hello;",
            "s:0:",
            "s:",
            "s:3:\"\0\0\0\";",
            "a:0:{}",
            "a:1:{s:3:\"key\";s:3:\"val\";}",
            "a:1:{s:3:\"key\";",
            "a:1:{",
            "a:2:{i:0;i:1;}",
            "a:10:{i:0;i:1;}",
            "a:2147483647:{}",
            "a:999999999:{i:0;i:1;}",
            "a:-1:{}",
            "a:-2147483648:{}",
            "a:1:{i:0;a:1:{i:1;}}",
            "a:2:{i:0;i:1;i:2;i:3;i:4;}",
            "a:;",
            "a:",
            "O:8:\"stdClass\":0:{}",
            "x:10;",
            "Z:;",
            "??!@#$%^&*()_+|}{[]:\"?><",
            new string('a', 5000),
            new string('{', 1000),
            new string('}', 1000),
            new string(';', 1000),
            new string(':', 1000),
            new string('"', 1000)
        ];

        foreach (string payload in adversarialPayloads)
        {
            // Test char overload
            Action actChar = () => PhpDeserializer.Deserialize(payload);
            actChar.Should().NotThrow<NullReferenceException>();
            actChar.Should().NotThrow<IndexOutOfRangeException>();
            actChar.Should().NotThrow<ArgumentOutOfRangeException>();
            actChar.Should().NotThrow<AccessViolationException>();

            // Test UTF-8 byte span overload
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(payload);
            Action actUtf8 = () => PhpDeserializer.Deserialize(utf8Bytes.AsSpan());
            actUtf8.Should().NotThrow<NullReferenceException>();
            actUtf8.Should().NotThrow<IndexOutOfRangeException>();
            actUtf8.Should().NotThrow<ArgumentOutOfRangeException>();
            actUtf8.Should().NotThrow<AccessViolationException>();
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(50, false)]
    [InlineData(100, false)]
    public void Fuzz_RecursionDepthBomb_EnforcesMaxDepth32(int depth, bool shouldSucceed)
    {
        // Construct nested array: a:1:{i:0;a:1:{i:0;...s:4:"leaf";...}}
        StringBuilder sb = new();
        for (int i = 0; i < depth; i++)
        {
            sb.Append("a:1:{i:0;");
        }
        sb.Append("s:4:\"leaf\";");
        for (int i = 0; i < depth; i++)
        {
            sb.Append('}');
        }

        string payload = sb.ToString();
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(payload);

        if (shouldSucceed)
        {
            object? resultChar = PhpDeserializer.Deserialize(payload);
            resultChar.Should().NotBeNull();

            object? resultUtf8 = PhpDeserializer.Deserialize(utf8Bytes.AsSpan());
            resultUtf8.Should().NotBeNull();
        }
        else
        {
            Action actChar = () => PhpDeserializer.Deserialize(payload);
            actChar.Should().Throw<FormatException>()
                .WithMessage("*Exceeded maximum recursion depth of 32*");

            Action actUtf8 = () => PhpDeserializer.Deserialize(utf8Bytes.AsSpan());
            actUtf8.Should().Throw<FormatException>()
                .WithMessage("*Exceeded maximum recursion depth of 32*");
        }
    }

    [Fact]
    public void Fuzz_RoundTripInvariance_PreservesDataIntegrity()
    {
        Random rng = new(84_9021);

        for (int i = 0; i < RoundTripFuzzIterations; i++)
        {
            PhpHashtable originalTable = GenerateRandomHashtable(rng, currentDepth: 0, maxDepth: 4);

            // Serialize
            string serialized = PhpSerializer.Serialize(originalTable);
            byte[] serializedUtf8 = PhpSerializer.SerializeToUtf8Bytes(originalTable);

            // Deserialize
            object? deserializedFromString = PhpDeserializer.Deserialize(serialized);
            object? deserializedFromUtf8 = PhpDeserializer.Deserialize(serializedUtf8.AsSpan());

            deserializedFromString.Should().BeOfType<PhpHashtable>();
            deserializedFromUtf8.Should().BeOfType<PhpHashtable>();

            PhpHashtable tableFromString = (PhpHashtable)deserializedFromString!;
            PhpHashtable tableFromUtf8 = (PhpHashtable)deserializedFromUtf8!;

            AssertHashtablesEqual(originalTable, tableFromString);
            AssertHashtablesEqual(originalTable, tableFromUtf8);
        }
    }

    [Fact]
    public void Fuzz_MutationFuzzing_BitFlipsAndByteMutationsOnRealPackets()
    {
        Random rng = new(1337_2026);

        // Real baseline payloads
        ShowSimpleStatsResponse stats = new()
        {
            NameWithClanTag = "[HON]KongorMaster",
            ID = 998877,
            Level = 45,
            LevelExperience = 120500,
            NumberOfAvatarsOwned = 12,
            TotalMatchesPlayed = 450,
            TotalGamesPlayedLegacy = 450,
            AccountCreationDate = "01/15/2012",
            LastActivityDate = "08/30/2026",
            TotalDisconnects = 2,
            RankedWins = 280,
            RankedLosses = 170,
            FavHero1 = "Hero_Pyromancer",
            FavHero1Time = 36000,
            FavHero1_2 = "Hero_Pyromancer",
            FavHero2 = "Hero_Devourer",
            FavHero2Time = 24000,
            FavHero2_2 = "Hero_Devourer",
            FavHero3 = "Hero_Valkyrie",
            FavHero3Time = 18000,
            FavHero3_2 = "Hero_Valkyrie",
            FavHero4 = "Hero_Magmus",
            FavHero4Time = 12000,
            FavHero4_2 = "Hero_Magmus",
            FavHero5 = "Hero_Behemoth",
            FavHero5Time = 8000,
            FavHero5_2 = "Hero_Behemoth",
            CurrentSeason = 10,
            SimpleSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 280,
                RankedMatchesLost = 170,
                WinStreak = 7,
                CurrentRank = "Immortal",
                RankedRating = "1950.500",
                InPlacementPhase = 0
            },
            SimpleCasualSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 15,
                RankedMatchesLost = 5,
                WinStreak = 2,
                CurrentRank = "1500.000",
                RankedRating = "1500.000",
                InPlacementPhase = 0
            },
            SimpleMidWarsSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 40,
                RankedMatchesLost = 20,
                WinStreak = 4,
                CurrentRank = "Unranked",
                RankedRating = "1650.000",
                InPlacementPhase = 0
            },
            MVPAwardsCount = "35",
            Top4AwardNames = ["MVP", "GankMaster", "WardHero", "TripleKill"],
            Top4AwardCounts = ["35", "12", "50", "8"],
            CustomIconSlotID = "1",
            OwnedStoreItems = ["avatar_pyro_1", "taunt_dump"],
            SelectedStoreItems = ["avatar_pyro_1"],
            OwnedStoreItemsData = []
        };

        Dictionary<object, object> hybridDict = LegacyProtocolFormatter.Instance.CreateHybridSimpleStats(stats);
        string baselineSerialized = PhpSerializer.Serialize(hybridDict);
        byte[] baselineBytes = Encoding.UTF8.GetBytes(baselineSerialized);

        for (int i = 0; i < MutationFuzzIterations; i++)
        {
            byte[] mutatedBytes = (byte[])baselineBytes.Clone();
            int mutationType = rng.Next(0, 6);

            switch (mutationType)
            {
                case 0: // Bit-flip
                {
                    int targetIndex = rng.Next(0, mutatedBytes.Length);
                    int bitIndex = rng.Next(0, 8);
                    mutatedBytes[targetIndex] ^= (byte)(1 << bitIndex);
                    break;
                }
                case 1: // Byte corruption
                {
                    int targetIndex = rng.Next(0, mutatedBytes.Length);
                    mutatedBytes[targetIndex] = (byte)rng.Next(0, 256);
                    break;
                }
                case 2: // Truncation
                {
                    int newLength = rng.Next(0, mutatedBytes.Length);
                    Array.Resize(ref mutatedBytes, newLength);
                    break;
                }
                case 3: // Insertion of random junk
                {
                    int insertIndex = rng.Next(0, mutatedBytes.Length);
                    byte[] junk = new byte[rng.Next(1, 32)];
                    rng.NextBytes(junk);
                    byte[] newBuf = new byte[mutatedBytes.Length + junk.Length];
                    Array.Copy(mutatedBytes, 0, newBuf, 0, insertIndex);
                    Array.Copy(junk, 0, newBuf, insertIndex, junk.Length);
                    Array.Copy(mutatedBytes, insertIndex, newBuf, insertIndex + junk.Length, mutatedBytes.Length - insertIndex);
                    mutatedBytes = newBuf;
                    break;
                }
                case 4: // Deletion of chunk
                {
                    if (mutatedBytes.Length > 5)
                    {
                        int delStart = rng.Next(0, mutatedBytes.Length - 2);
                        int delLen = rng.Next(1, Math.Min(16, mutatedBytes.Length - delStart));
                        byte[] newBuf = new byte[mutatedBytes.Length - delLen];
                        Array.Copy(mutatedBytes, 0, newBuf, 0, delStart);
                        Array.Copy(mutatedBytes, delStart + delLen, newBuf, delStart, mutatedBytes.Length - (delStart + delLen));
                        mutatedBytes = newBuf;
                    }
                    break;
                }
                case 5: // Delimiter injection (';', ':', '{', '}', '"')
                {
                    char[] delimiters = [';', ':', '{', '}', '"', 'N', 'b', 'i', 'd', 's', 'a', '\0'];
                    int targetIndex = rng.Next(0, mutatedBytes.Length);
                    mutatedBytes[targetIndex] = (byte)delimiters[rng.Next(0, delimiters.Length)];
                    break;
                }
            }

            try
            {
                object? resultUtf8 = PhpDeserializer.Deserialize(mutatedBytes.AsSpan());
            }
            catch (FormatException)
            {
                // Expected recursion or format limit
            }
            catch (Exception ex)
            {
                Assert.Fail($"Mutation fuzzing iteration {i} crashed with unhandled exception {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_GenericPhpDeserializable_NeverThrowsMemorySafetyExceptions()
    {
        Random rng = new(2026_0901);
        byte[] buffer = new byte[2048];

        for (int i = 0; i < 2_000; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));
            ReadOnlySpan<byte> utf8Span = buffer.AsSpan(0, length);

            try
            {
                TestFuzzDto? result = PhpDeserializer.Deserialize<TestFuzzDto>(utf8Span);
            }
            catch (FormatException)
            {
                // Expected recursion or format errors
            }
            catch (Exception ex)
            {
                Assert.Fail($"Generic IPhpDeserializable deserialization crashed at iteration {i}: {ex.GetType().FullName} - {ex.Message}");
            }
        }
    }

    private sealed record TestFuzzDto(string Name, int Value) : IPhpDeserializable<TestFuzzDto>
    {
        public static TestFuzzDto DeserializePhp(PhpHashtable table)
        {
            string name = table["name"]?.ToString() ?? string.Empty;
            int value = table["value"] is int val ? val : 0;
            return new TestFuzzDto(name, value);
        }
    }

    [Fact]
    public void Fuzz_Utf8PhpWriter_StressAndCapacityHandling()
    {
        Random rng = new(777_888);

        for (int i = 0; i < 500; i++)
        {
            using PooledArrayBufferWriter writer = new(initialCapacity: rng.Next(16, 256));
            Utf8PhpWriter phpWriter = new(writer);

            int count = rng.Next(1, 20);
            phpWriter.WriteArrayHeader(count);

            for (int k = 0; k < count; k++)
            {
                phpWriter.WriteKey($"key_{k}");

                int valType = rng.Next(0, 5);
                switch (valType)
                {
                    case 0:
                        phpWriter.WriteNull();
                        break;
                    case 1:
                        phpWriter.WriteBool(rng.Next(0, 2) == 1);
                        break;
                    case 2:
                        phpWriter.WriteInt(rng.Next(-1000000, 1000000));
                        break;
                    case 3:
                        phpWriter.WriteDouble(Math.Round(rng.NextDouble() * 10000.0, 4));
                        break;
                    case 4:
                        string randomStr = new('x', rng.Next(0, 100));
                        phpWriter.WriteString(randomStr);
                        break;
                }
            }

            phpWriter.WriteArrayFooter();
            phpWriter.Flush();

            ReadOnlySpan<byte> written = writer.WrittenSpan;
            written.Length.Should().BeGreaterThan(0);

            // Assert deserializer can parse what Utf8PhpWriter emitted
            object? parsed = PhpDeserializer.Deserialize(written);
            parsed.Should().BeOfType<PhpHashtable>();
            ((PhpHashtable)parsed!).Count.Should().Be(count);
        }
    }

    private static PhpHashtable GenerateRandomHashtable(Random rng, int currentDepth, int maxDepth)
    {
        int elementCount = rng.Next(0, 8);
        PhpHashtable table = new(elementCount);

        for (int i = 0; i < elementCount; i++)
        {
            object key = rng.Next(0, 2) == 0 ? $"key_{i}_{rng.Next(100, 999)}" : (object)i;
            object? val = GenerateRandomValue(rng, currentDepth, maxDepth);
            table[key] = val;
        }

        return table;
    }

    private static object? GenerateRandomValue(Random rng, int currentDepth, int maxDepth)
    {
        if (currentDepth >= maxDepth)
        {
            int primitiveType = rng.Next(0, 5);
            return primitiveType switch
            {
                0 => null,
                1 => rng.Next(0, 2) == 1,
                2 => rng.Next(-100000, 100000),
                3 => Math.Round(rng.NextDouble() * 1000.0, 3),
                _ => $"str_val_{rng.Next(1000, 9999)}"
            };
        }

        int type = rng.Next(0, 6);
        return type switch
        {
            0 => null,
            1 => rng.Next(0, 2) == 1,
            2 => rng.Next(-100000, 100000),
            3 => Math.Round(rng.NextDouble() * 1000.0, 3),
            4 => $"str_val_{rng.Next(1000, 9999)}",
            _ => GenerateRandomHashtable(rng, currentDepth + 1, maxDepth)
        };
    }

    private static void AssertHashtablesEqual(PhpHashtable expected, PhpHashtable actual)
    {
        actual.Count.Should().Be(expected.Count);
        foreach (DictionaryEntry entry in expected)
        {
            object key = entry.Key;
            object? expectedVal = entry.Value;

            actual.ContainsKey(key).Should().BeTrue();
            object? actualVal = actual[key];

            if (expectedVal is null)
            {
                actualVal.Should().BeNull();
            }
            else if (expectedVal is PhpHashtable nestedExpected)
            {
                actualVal.Should().BeOfType<PhpHashtable>();
                AssertHashtablesEqual(nestedExpected, (PhpHashtable)actualVal!);
            }
            else if (expectedVal is double expectedDbl && actualVal is double actualDbl)
            {
                actualDbl.Should().BeApproximately(expectedDbl, 0.001);
            }
            else
            {
                actualVal.Should().Be(expectedVal);
            }
        }
    }
}
