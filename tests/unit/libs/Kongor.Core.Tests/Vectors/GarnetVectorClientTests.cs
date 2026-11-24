// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GarnetVectorClientTests.cs
// System: Kongor Core Unit Tests - Native Garnet Vector Sets Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using ASPIRE.Common.Caching.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Kongor.Core.Tests.Vectors;

public sealed class GarnetVectorClientTests
{
    [Fact]
    public void ToFp32Bytes_WithValidFloats_ProducesAccurateBinaryBlob()
    {
        // Arrange
        float[] vector = [1.0f, -0.5f, 0.0f, 3.14159f];

        // Act
        byte[] bytes = GarnetVectorClient.ToFp32Bytes(vector);

        // Assert
        bytes.Should().HaveCount(vector.Length * sizeof(float));

        float f0 = BitConverter.ToSingle(bytes, 0);
        float f1 = BitConverter.ToSingle(bytes, 4);
        float f2 = BitConverter.ToSingle(bytes, 8);
        float f3 = BitConverter.ToSingle(bytes, 12);

        f0.Should().Be(1.0f);
        f1.Should().Be(-0.5f);
        f2.Should().Be(0.0f);
        f3.Should().Be(3.14159f);
    }

    [Fact]
    public async Task InProcessClient_AddAndSearch_RanksByCosineSimilarity()
    {
        // Arrange
        InProcessGarnetVectorClient client = new();
        string key = "tactics:bot_decisions";

        float[] attackVector = [1.0f, 0.0f, 0.0f, 0.0f];
        float[] retreatVector = [0.0f, 1.0f, 0.0f, 0.0f];
        float[] pushVector = [0.7071f, 0.7071f, 0.0f, 0.0f];

        await client.AddAsync(key, "Tactics_AggressiveGank", attackVector, new VectorAddOptions { Attributes = "{\"target\":\"Kongor\"}" });
        await client.AddAsync(key, "Tactics_DefensiveRetreat", retreatVector, new VectorAddOptions { Attributes = "{\"target\":\"Fountain\"}" });
        await client.AddAsync(key, "Tactics_SplitPushLane", pushVector, new VectorAddOptions { Attributes = "{\"lane\":\"Top\"}" });

        // Act - Query vector very close to Attack
        float[] queryVector = [0.95f, 0.05f, 0.0f, 0.0f];
        IReadOnlyList<VectorSearchResult> results = await client.SearchAsync(
            key,
            queryVector,
            new VectorSearchOptions { Count = 3, WithScores = true, WithAttribs = true });

        // Assert
        results.Should().HaveCount(3);
        results[0].Element.Should().Be("Tactics_AggressiveGank");
        results[0].Score.Should().BeLessThan(0.05); // Close to 0 cosine distance
        results[0].Attributes.Should().Contain("Kongor");

        results[1].Element.Should().Be("Tactics_SplitPushLane");
        results[2].Element.Should().Be("Tactics_DefensiveRetreat");
    }

    [Fact]
    public async Task InProcessClient_DimensionMismatch_ThrowsArgumentException()
    {
        // Arrange
        InProcessGarnetVectorClient client = new();
        string key = "test:dimensions";

        await client.AddAsync(key, "item1", new float[] { 1.0f, 2.0f, 3.0f });

        // Act & Assert
        Func<Task> act = async () =>
        {
            await client.AddAsync(key, "item2", new float[] { 1.0f, 2.0f });
        };

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*dimension mismatch*");
    }

    [Fact]
    public async Task InProcessClient_SearchByElement_UsesExistingStoredVector()
    {
        // Arrange
        InProcessGarnetVectorClient client = new();
        string key = "replays:combat_situations";

        await client.AddAsync(key, "fight_101", new float[] { 0.8f, 0.2f, 0.1f });
        await client.AddAsync(key, "fight_102", new float[] { 0.79f, 0.21f, 0.09f });
        await client.AddAsync(key, "fight_103", new float[] { 0.0f, 0.9f, 0.1f });

        // Act
        IReadOnlyList<VectorSearchResult> results = await client.SearchByElementAsync(
            key,
            "fight_101",
            new VectorSearchOptions { Count = 2 });

        // Assert
        results.Should().HaveCount(2);
        results[0].Element.Should().Be("fight_101");
        results[0].Score.Should().Be(0.0);
        results[1].Element.Should().Be("fight_102");
    }

    [Fact]
    public async Task InProcessClient_RemoveAndGetInfo_UpdatesIndexCorrectly()
    {
        // Arrange
        InProcessGarnetVectorClient client = new();
        string key = "index:lifecycle";

        await client.AddAsync(key, "element_A", new float[] { 1.0f, 0.0f });
        await client.AddAsync(key, "element_B", new float[] { 0.0f, 1.0f });

        // Act 1: Get Info
        VectorIndexInfo? infoBefore = await client.GetInfoAsync(key);
        infoBefore.Should().NotBeNull();
        infoBefore!.Size.Should().Be(2);
        infoBefore.Dimension.Should().Be(2);

        // Act 2: Remove element
        bool removed = await client.RemoveAsync(key, "element_A");
        removed.Should().BeTrue();

        // Act 3: Check Info after removal
        VectorIndexInfo? infoAfter = await client.GetInfoAsync(key);
        infoAfter.Should().NotBeNull();
        infoAfter!.Size.Should().Be(1);

        // Act 4: List indexes
        IReadOnlyList<string> indexList = await client.ListIndexesAsync("index:*");
        indexList.Should().Contain(key);
    }

    [Fact]
    public void ParseSearchResults_WithFlatRespArray_ParsesElementsAndScores()
    {
        // Arrange
        // Flat array: [ "element1", "0.045", "element2", "0.198" ]
        RedisResult[] respArray =
        [
            RedisResult.Create((RedisValue)"Tactics_Alpha"),
            RedisResult.Create((RedisValue)"0.045"),
            RedisResult.Create((RedisValue)"Tactics_Beta"),
            RedisResult.Create((RedisValue)"0.198")
        ];
        RedisResult result = RedisResult.Create(respArray);

        // Act
        IReadOnlyList<VectorSearchResult> parsed = GarnetVectorClient.ParseSearchResults(result, withScores: true, withAttribs: false);

        // Assert
        parsed.Should().HaveCount(2);
        parsed[0].Element.Should().Be("Tactics_Alpha");
        parsed[0].Score.Should().BeApproximately(0.045, 0.0001);
        parsed[1].Element.Should().Be("Tactics_Beta");
        parsed[1].Score.Should().BeApproximately(0.198, 0.0001);
    }

    [Fact]
    public void ParseSearchResults_WithEmptyOrNull_ReturnsEmptyList()
    {
        // Act & Assert
        GarnetVectorClient.ParseSearchResults(null, true, false).Should().BeEmpty();
        GarnetVectorClient.ParseSearchResults(RedisResult.Create(Array.Empty<RedisResult>()), true, false).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "LocalOnly")]
    public async Task LiveGarnet_AddAndSearch_ExecutesNativeVectorOps()
    {
        // Arrange
        ConfigurationOptions options = new()
        {
            EndPoints = { { "127.0.0.1", 28298 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 3000,
            SyncTimeout = 3000
        };

        IConnectionMultiplexer muxer;
        try
        {
            muxer = await ConnectionMultiplexer.ConnectAsync(options);
            if (!muxer.IsConnected) return; // Skip if live Garnet node is not running
        }
        catch
        {
            return; // Skip if offline
        }

        GarnetVectorClient client = new(muxer, NullLogger<GarnetVectorClient>.Instance);
        string key = $"test:live_vectors_{Guid.NewGuid():N}";

        try
        {
            float[] v1 = [1.0f, 0.0f, 0.0f, 0.0f];
            float[] v2 = [0.0f, 1.0f, 0.0f, 0.0f];

            // Act 1: VADD
            bool added1 = await client.AddAsync(key, "item_alpha", v1, new VectorAddOptions { Metric = VectorMetric.Cosine });
            bool added2 = await client.AddAsync(key, "item_beta", v2);

            if (!added1 && !added2)
            {
                // Cache node might be running standard Redis without preview flag
                return;
            }

            added1.Should().BeTrue();
            added2.Should().BeTrue();

            // Act 2: VSIM
            float[] query = [0.95f, 0.05f, 0.0f, 0.0f];
            IReadOnlyList<VectorSearchResult> searchResults = await client.SearchAsync(
                key,
                query,
                new VectorSearchOptions { Count = 2, WithScores = true });

            searchResults.Should().NotBeEmpty();
            searchResults[0].Element.Should().Be("item_alpha");

            // Act 3: VDIM
            int dim = await client.GetDimensionAsync(key);
            dim.Should().Be(4);

            // Act 4: VREM
            bool removed = await client.RemoveAsync(key, "item_alpha");
            removed.Should().BeTrue();
        }
        finally
        {
            IDatabase db = muxer.GetDatabase();
            await db.KeyDeleteAsync(key);
            muxer.Dispose();
        }
    }
}
