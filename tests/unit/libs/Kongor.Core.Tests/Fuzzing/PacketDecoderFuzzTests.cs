// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Text;
using ASPIRE.Common.IPC;
using ASPIRE.Common.K2;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Fuzzing;

/// <summary>
///     Property-Based and Mutation Fuzz Testing for Binary Packet Decoders and Game Archive Parsers.
///     Ensures robustness against arbitrary, truncated, and maliciously crafted byte streams.
/// </summary>
public sealed class PacketDecoderFuzzTests
{
    private const int FuzzIterations = 5_000;

    [Fact]
    public void Fuzz_WillowmakerIpcMessage_TryDeserialize_HandlesArbitraryStreams()
    {
        Random rng = new(99_1234);
        byte[] buffer = new byte[8192];

        for (int i = 0; i < FuzzIterations; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));
            ReadOnlySpan<byte> span = buffer.AsSpan(0, length);

            try
            {
                bool result = WillowmakerIpcMessage.TryDeserialize(span, out WillowmakerIpcMessage? message, out int consumed);
                if (result)
                {
                    message.Should().NotBeNull();
                    consumed.Should().BeGreaterThan(0);
                    consumed.Should().BeLessThanOrEqualTo(span.Length);
                }
            }
            catch (InvalidDataException)
            {
                // Expected when magic or length check fails
            }
            catch (Exception ex)
            {
                Assert.Fail($"WillowmakerIpcMessage.TryDeserialize crashed at iteration {i} (len={length}) with {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_WillowmakerIpcMessage_RoundTripInvariance()
    {
        Random rng = new(55_8899);

        for (int i = 0; i < 1_000; i++)
        {
            IpcOpCode[] opCodes = Enum.GetValues<IpcOpCode>();
            IpcOpCode opCode = opCodes[rng.Next(0, opCodes.Length)];

            int payloadLen = rng.Next(0, 1024);
            byte[] payload = new byte[payloadLen];
            rng.NextBytes(payload);

            WillowmakerIpcMessage original = WillowmakerIpcMessage.CreateBytes(opCode, payload);
            byte[] serialized = WillowmakerIpcMessage.Serialize(original);

            bool success = WillowmakerIpcMessage.TryDeserialize(serialized, out WillowmakerIpcMessage? deserialized, out int bytesConsumed);

            success.Should().BeTrue();
            bytesConsumed.Should().Be(serialized.Length);
            deserialized.Should().NotBeNull();
            deserialized!.OpCode.Should().Be(opCode);
            deserialized.Payload.Should().Equal(payload);
        }
    }

    [Fact]
    public void Fuzz_WillowmakerIpcMessage_AdversarialLengths()
    {
        // Magic header valid: 0x574D ('WM'), HeaderSize = 12 bytes
        byte[] header = new byte[WillowmakerIpcMessage.HeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), WillowmakerIpcMessage.HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), (ushort)IpcOpCode.Ping);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0); // RequestId

        int[] maliciousLengths = [-1, -9999, int.MinValue, WillowmakerIpcMessage.MaxPayloadSize + 1, int.MaxValue, 1_000_000];

        foreach (int malLen in maliciousLengths)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), malLen);

            if (malLen is < 0 or > WillowmakerIpcMessage.MaxPayloadSize)
            {
                Action act = () => WillowmakerIpcMessage.TryDeserialize(header, out _, out _);
                act.Should().Throw<InvalidDataException>();
            }
            else
            {
                // Length is valid but payload is missing from buffer
                bool success = WillowmakerIpcMessage.TryDeserialize(header, out WillowmakerIpcMessage? msg, out int consumed);
                success.Should().BeFalse();
                msg.Should().BeNull();
                consumed.Should().Be(0);
            }
        }
    }

    [Fact]
    public void Fuzz_K2ClipReader_NeverThrowsUnhandledMemorySafetyExceptions()
    {
        Random rng = new(77_4411);
        byte[] buffer = new byte[2048];

        for (int i = 0; i < 2_000; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));

            // Occasionally inject valid CLIP magic to test deeper parser branches
            if (i % 3 == 0 && length >= 4)
            {
                Encoding.ASCII.GetBytes("CLIP").CopyTo(buffer, 0);
            }

            using MemoryStream ms = new(buffer, 0, length);
            try
            {
                K2Clip clip = K2ClipReader.Read(ms, "fuzz_clip");
                clip.Should().NotBeNull();
            }
            catch (InvalidDataException)
            {
                // Expected on invalid header magic
            }
            catch (EndOfStreamException)
            {
                // Expected when stream is truncated mid-chunk
            }
            catch (Exception ex)
            {
                Assert.Fail($"K2ClipReader.Read crashed at iteration {i} with {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_K2ModelReader_NeverThrowsUnhandledMemorySafetyExceptions()
    {
        Random rng = new(11_2233);
        byte[] buffer = new byte[2048];

        for (int i = 0; i < 2_000; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));

            // Occasionally inject valid SMDL magic to test deeper chunk parsing
            if (i % 3 == 0 && length >= 4)
            {
                Encoding.ASCII.GetBytes("SMDL").CopyTo(buffer, 0);
            }

            using MemoryStream ms = new(buffer, 0, length);
            try
            {
                K2Model model = K2ModelReader.Read(ms);
                model.Should().NotBeNull();
            }
            catch (InvalidDataException)
            {
                // Expected on invalid header magic
            }
            catch (EndOfStreamException)
            {
                // Expected when stream is truncated mid-chunk
            }
            catch (Exception ex)
            {
                Assert.Fail($"K2ModelReader.Read crashed at iteration {i} with {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_K2DdsReader_NeverThrowsUnhandledMemorySafetyExceptions()
    {
        Random rng = new(33_6699);
        byte[] buffer = new byte[2048];

        for (int i = 0; i < 2_000; i++)
        {
            int length = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer.AsSpan(0, length));

            // Inject valid DDS magic sometimes
            if (i % 3 == 0 && length >= 4)
            {
                Encoding.ASCII.GetBytes("DDS ").CopyTo(buffer, 0);
            }

            using MemoryStream ms = new(buffer, 0, length);
            try
            {
                (byte[] rgba, int w, int h) = K2DdsReader.DecodeDds(ms);
            }
            catch (InvalidDataException)
            {
                // Expected on invalid header magic
            }
            catch (EndOfStreamException)
            {
                // Expected when stream is truncated
            }
            catch (OverflowException)
            {
                // Expected if random header produces integer overflow for width*height
            }
            catch (ArgumentException)
            {
                // Expected if Seek or Array copy boundaries fail on truncated data
            }
            catch (Exception ex)
            {
                Assert.Fail($"K2DdsReader.DecodeDds crashed at iteration {i} with {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Fuzz_LegacyProtocolFormatter_CreateHybridSimpleStats_WithCorruptedOrExtremeStats()
    {
        Random rng = new(44_55_66);

        for (int i = 0; i < 1_000; i++)
        {
            ShowSimpleStatsResponse stats = new()
            {
                NameWithClanTag = rng.Next(0, 2) == 0 ? "" : $"[CLAN_{rng.Next(1, 99)}]Player_{rng.Next(100, 999)}",
                ID = rng.Next(-1000, 1000000),
                Level = rng.Next(-100, 500),
                LevelExperience = rng.Next(-10000, 5000000),
                NumberOfAvatarsOwned = rng.Next(0, 100),
                TotalMatchesPlayed = rng.Next(0, 10000),
                TotalGamesPlayedLegacy = rng.Next(0, 10000),
                AccountCreationDate = "01/01/2020",
                LastActivityDate = "08/30/2026",
                TotalDisconnects = rng.Next(0, 50),
                RankedWins = rng.Next(-50, 10000),
                RankedLosses = rng.Next(-50, 10000),
                FavHero1 = "Hero_Pyromancer",
                FavHero1Time = rng.Next(0, 50000),
                FavHero1_2 = "Hero_Pyromancer",
                FavHero2 = "Hero_Devourer",
                FavHero2Time = rng.Next(0, 50000),
                FavHero2_2 = "Hero_Devourer",
                FavHero3 = "Hero_Valkyrie",
                FavHero3Time = rng.Next(0, 50000),
                FavHero3_2 = "Hero_Valkyrie",
                FavHero4 = "Hero_Magmus",
                FavHero4Time = rng.Next(0, 50000),
                FavHero4_2 = "Hero_Magmus",
                FavHero5 = "Hero_Behemoth",
                FavHero5Time = rng.Next(0, 50000),
                FavHero5_2 = "Hero_Behemoth",
                CurrentSeason = rng.Next(-1, 50),
                SimpleSeasonStats = new SimpleSeasonStats
                {
                    RankedMatchesWon = rng.Next(-10, 500),
                    RankedMatchesLost = rng.Next(-10, 500),
                    WinStreak = rng.Next(-5, 50),
                    CurrentRank = rng.Next(0, 2) == 0 ? "Gold I" : $"Rank_{rng.Next(1, 10)}",
                    RankedRating = rng.Next(0, 2) == 0 ? "1500.000" : $"{rng.Next(1000, 2500)}.000",
                    InPlacementPhase = rng.Next(0, 2)
                },
                SimpleCasualSeasonStats = new SimpleSeasonStats
                {
                    RankedMatchesWon = rng.Next(-10, 500),
                    RankedMatchesLost = rng.Next(-10, 500),
                    WinStreak = 0,
                    CurrentRank = "1500.000",
                    RankedRating = "1500.000",
                    InPlacementPhase = 0
                },
                SimpleMidWarsSeasonStats = new SimpleSeasonStats
                {
                    RankedMatchesWon = rng.Next(-10, 500),
                    RankedMatchesLost = rng.Next(-10, 500),
                    WinStreak = 0,
                    CurrentRank = "Unranked",
                    RankedRating = "1500.000",
                    InPlacementPhase = 0
                },
                MVPAwardsCount = rng.Next(0, 100).ToString(),
                Top4AwardNames = [.. Enumerable.Range(0, rng.Next(0, 8)).Select(x => $"Award_{x}")],
                Top4AwardCounts = [.. Enumerable.Range(0, rng.Next(0, 8)).Select(x => rng.Next(0, 100).ToString())],
                CustomIconSlotID = rng.Next(0, 10).ToString(),
                OwnedStoreItems = [.. Enumerable.Range(0, rng.Next(0, 8)).Select(x => $"item_{x}")],
                SelectedStoreItems = [.. Enumerable.Range(0, rng.Next(0, 8)).Select(x => $"item_{x}")],
                OwnedStoreItemsData = []
            };

            try
            {
                Dictionary<object, object> result = LegacyProtocolFormatter.Instance.CreateHybridSimpleStats(stats);
                result.Should().NotBeNull();
                result.Should().ContainKey("rnk_wins");
                result.Should().ContainKey("rnk_losses");
                result.Should().ContainKey("wins");
                result.Should().ContainKey("losses");
            }
            catch (Exception ex)
            {
                Assert.Fail($"LegacyProtocolFormatter.CreateHybridSimpleStats crashed at iteration {i}: {ex.GetType().FullName} - {ex.Message}");
            }
        }
    }
}
