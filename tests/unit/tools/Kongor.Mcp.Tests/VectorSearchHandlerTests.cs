// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
// ============================================================================
// File: VectorSearchHandlerTests.cs
// System: Kongor MCP Unit Tests - Native Garnet Vector Tooling
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Text.Json;
using ASPIRE.Common.Caching.Vectors;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class VectorSearchHandlerTests
{
    private readonly McpToolRegistry _registry;
    private readonly InProcessGarnetVectorClient _testClient;
    private readonly VectorSearchHandler _handler;

    public VectorSearchHandlerTests()
    {
        _testClient = new InProcessGarnetVectorClient();
        _handler = new VectorSearchHandler(_testClient);
        _registry = new McpToolRegistry(
            registerDefaults: true,
            vectorSearchHandler: _handler);
    }

    private static JsonElement ParseJson(ToolCallResult result)
    {
        string text = result.Content.Count > 0 && result.Content[0] is TextContentItem textItem
            ? textItem.Text
            : result.FirstText;

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task VectorSearch_AddAndSearch_RanksNearestNeighbors()
    {
        // Arrange
        string addJson1 = """
        {
            "action": "add",
            "key": "tactics:bot_combos",
            "element": "Pyromancer_Stun_Nuke",
            "vector": [1.0, 0.0, 0.0],
            "attributes": "{\"hero\":\"Hero_Pyromancer\",\"burst\":850}",
            "metric": "cosine"
        }
        """;

        string addJson2 = """
        {
            "action": "add",
            "key": "tactics:bot_combos",
            "element": "Magmus_LavaSurge_Ult",
            "vector": [0.0, 1.0, 0.0],
            "attributes": "{\"hero\":\"Hero_Magmus\",\"burst\":920}",
            "metric": "cosine"
        }
        """;

        // Act 1: Ingest vectors
        ToolCallResult addRes1 = await _handler.HandleAsync(JsonDocument.Parse(addJson1).RootElement);
        ToolCallResult addRes2 = await _handler.HandleAsync(JsonDocument.Parse(addJson2).RootElement);

        addRes1.IsError.Should().BeFalse();
        addRes2.IsError.Should().BeFalse();

        JsonElement addRoot1 = ParseJson(addRes1);
        addRoot1.GetProperty("success").GetBoolean().Should().BeTrue();
        addRoot1.GetProperty("dimensions").GetInt32().Should().Be(3);

        // Act 2: Similarity search
        string searchJson = """
        {
            "action": "search",
            "key": "tactics:bot_combos",
            "vector": [0.92, 0.08, 0.0],
            "count": 2,
            "with_scores": true,
            "with_attribs": true
        }
        """;

        ToolCallResult searchRes = await _handler.HandleAsync(JsonDocument.Parse(searchJson).RootElement);
        searchRes.IsError.Should().BeFalse();

        JsonElement searchRoot = ParseJson(searchRes);
        searchRoot.GetProperty("success").GetBoolean().Should().BeTrue();
        searchRoot.GetProperty("matchCount").GetInt32().Should().Be(2);

        JsonElement results = searchRoot.GetProperty("results");
        results[0].GetProperty("element").GetString().Should().Be("Pyromancer_Stun_Nuke");
        results[0].GetProperty("score").GetDouble().Should().BeLessThan(0.05);
        results[0].GetProperty("attributes").GetString().Should().Contain("Hero_Pyromancer");
    }

    [Fact]
    public async Task VectorSearch_SearchByElement_FindsSimilarRecords()
    {
        // Arrange
        await _testClient.AddAsync("replays:highlights", "highlight_101", new float[] { 0.7f, 0.3f });
        await _testClient.AddAsync("replays:highlights", "highlight_102", new float[] { 0.69f, 0.31f });

        string searchJson = """
        {
            "action": "search",
            "key": "replays:highlights",
            "element": "highlight_101",
            "count": 2
        }
        """;

        // Act
        ToolCallResult result = await _handler.HandleAsync(JsonDocument.Parse(searchJson).RootElement);

        // Assert
        result.IsError.Should().BeFalse();
        JsonElement root = ParseJson(result);
        root.GetProperty("matchCount").GetInt32().Should().Be(2);
        root.GetProperty("results")[0].GetProperty("element").GetString().Should().Be("highlight_101");
        root.GetProperty("results")[0].GetProperty("score").GetDouble().Should().Be(0.0);
    }

    [Fact]
    public async Task VectorSearch_InfoAndDimension_ReturnsAccurateMetadata()
    {
        // Arrange
        await _testClient.AddAsync("meta:index", "elem_1", new float[] { 1.0f, 2.0f, 3.0f, 4.0f });

        // Act 1: Info
        string infoJson = """{"action": "info", "key": "meta:index"}""";
        ToolCallResult infoRes = await _handler.HandleAsync(JsonDocument.Parse(infoJson).RootElement);
        infoRes.IsError.Should().BeFalse();
        JsonElement infoRoot = ParseJson(infoRes);
        infoRoot.GetProperty("dimensions").GetInt32().Should().Be(4);
        infoRoot.GetProperty("size").GetInt64().Should().Be(1);

        // Act 2: Dimension
        string dimJson = """{"action": "dimension", "key": "meta:index"}""";
        ToolCallResult dimRes = await _handler.HandleAsync(JsonDocument.Parse(dimJson).RootElement);
        dimRes.IsError.Should().BeFalse();
        JsonElement dimRoot = ParseJson(dimRes);
        dimRoot.GetProperty("dimensions").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task VectorSearch_RemoveAndList_ManagesIndices()
    {
        // Arrange
        await _testClient.AddAsync("test:cleanup", "remove_me", new float[] { 1.0f, 0.0f });

        // Act 1: List
        string listJson = """{"action": "list", "pattern": "test:*"}""";
        ToolCallResult listRes = await _handler.HandleAsync(JsonDocument.Parse(listJson).RootElement);
        listRes.IsError.Should().BeFalse();
        JsonElement listRoot = ParseJson(listRes);
        listRoot.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        // Act 2: Remove
        string removeJson = """{"action": "remove", "key": "test:cleanup", "element": "remove_me"}""";
        ToolCallResult removeRes = await _handler.HandleAsync(JsonDocument.Parse(removeJson).RootElement);
        removeRes.IsError.Should().BeFalse();
        JsonElement removeRoot = ParseJson(removeRes);
        removeRoot.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void McpToolRegistry_RegistersVectorSearchAndAliases()
    {
        // Assert tool registration
        _registry.HasTool("kongor_vector_search").Should().BeTrue();
        ToolDefinition? def = _registry.GetTool("kongor_vector_search");
        def.Should().NotBeNull();
        def!.Name.Should().Be("kongor_vector_search");
        def.InputSchema.Properties.Should().ContainKey("action");
        def.InputSchema.Properties.Should().ContainKey("key");
        def.InputSchema.Properties.Should().ContainKey("vector");

        // Assert aliases
        _registry.HasTool("kongor_vector").Should().BeTrue();
        _registry.GetTool("kongor_vector").Should().NotBeNull();

        _registry.HasTool("vector_search").Should().BeTrue();
        _registry.GetTool("vector_search").Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task LiveGarnet_VectorSearchHandler_ExecutesE2EFlow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        // Skip if live Garnet node with --enable-vector-set-preview is not running on 28298 (e.g. Valkey 8.1 default)
        StackExchange.Redis.ConfigurationOptions config = new()
        {
            EndPoints = { { "127.0.0.1", 28298 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 300,
            SyncTimeout = 300
        };

        StackExchange.Redis.IConnectionMultiplexer? muxer = null;
        try
        {
            muxer = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(config);
            if (!muxer.IsConnected)
            {
                muxer.Dispose();
                return;
            }
            await muxer.GetDatabase().ExecuteAsync("VINFO", $"probe:vector_cap:{Guid.NewGuid():N}");
        }
        catch (StackExchange.Redis.RedisServerException rse) when (rse.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            muxer?.Dispose();
            return;
        }
        catch (StackExchange.Redis.RedisServerException)
        {
            // Non-'unknown command' error (e.g. key not found) confirms Garnet vector-sets ARE supported on 28298
        }
        catch
        {
            muxer?.Dispose();
            return;
        }

        if (muxer is null) return;

        string key = $"test:mcp_live_{Guid.NewGuid():N}";
        try
        {
            GarnetVectorClient liveClient = new(muxer);
            VectorSearchHandler handler = new(liveClient);

            // 1. Add vector
            string addJson = $$"""
            {
                "action": "add",
                "key": "{{key}}",
                "element": "Pyromancer_DragonSlave_Nuke",
                "vector": [1.0, 0.0, 0.0, 0.0],
                "attributes": "{\"hero\":\"Pyromancer\",\"burst\":850}",
                "metric": "cosine"
            }
            """;
            ToolCallResult addRes = await handler.HandleAsync(JsonDocument.Parse(addJson).RootElement);
            addRes.IsError.Should().BeFalse();
            JsonElement addRoot = ParseJson(addRes);
            addRoot.GetProperty("success").GetBoolean().Should().BeTrue();
            addRoot.GetProperty("mode").GetString().Should().Be("Live Garnet");

            // 2. Search vector
            string searchJson = $$"""
            {
                "action": "search",
                "key": "{{key}}",
                "vector": [0.95, 0.05, 0.0, 0.0],
                "count": 1,
                "with_scores": true
            }
            """;
            ToolCallResult searchRes = await handler.HandleAsync(JsonDocument.Parse(searchJson).RootElement);
            searchRes.IsError.Should().BeFalse();
            JsonElement searchRoot = ParseJson(searchRes);
            searchRoot.GetProperty("matchCount").GetInt32().Should().Be(1);
            searchRoot.GetProperty("results")[0].GetProperty("element").GetString().Should().Be("Pyromancer_DragonSlave_Nuke");

            // 3. Info
            string infoJson = $$"""{"action": "info", "key": "{{key}}"}""";
            ToolCallResult infoRes = await handler.HandleAsync(JsonDocument.Parse(infoJson).RootElement);
            infoRes.IsError.Should().BeFalse();
            JsonElement infoRoot = ParseJson(infoRes);
            infoRoot.GetProperty("dimensions").GetInt32().Should().Be(4);
            infoRoot.GetProperty("size").GetInt64().Should().Be(1);
        }
        finally
        {
            StackExchange.Redis.IDatabase db = muxer.GetDatabase();
            await db.KeyDeleteAsync(key);
            muxer.Dispose();
        }
    }
}
