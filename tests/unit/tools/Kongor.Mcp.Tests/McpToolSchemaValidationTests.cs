// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpToolSchemaValidationTests
{
    // =========================================================================
    // 1. JSON Schema & Catalog Conformance
    // =========================================================================

    [Fact]
    public void ToolSchemas_AllSixTools_MatchStandardJsonSchemaStructure()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        tools.Should().HaveCountGreaterThanOrEqualTo(6);

        string[] validJsonSchemaTypes = ["string", "number", "integer", "boolean", "object", "array", "null"];

        foreach (ToolDefinition tool in tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace($"Tool name must not be empty");
            tool.Description.Should().NotBeNullOrWhiteSpace($"Tool description must not be empty for {tool.Name}");

            ToolInputSchema inputSchema = tool.InputSchema;
            inputSchema.Should().NotBeNull($"InputSchema must exist for {tool.Name}");
            inputSchema.Type.Should().Be("object", $"InputSchema root type must be 'object' for {tool.Name}");
            inputSchema.Properties.Should().NotBeNull($"Properties must exist for {tool.Name}");

            // Verify all required properties exist in properties dictionary
            foreach (string requiredProp in inputSchema.Required)
            {
                inputSchema.Properties.Should().ContainKey(requiredProp,
                    $"Required property '{requiredProp}' must be defined in properties dictionary for {tool.Name}");
            }

            // Verify all property definitions adhere to JSON schema types
            foreach (KeyValuePair<string, ToolPropertySchema> prop in inputSchema.Properties)
            {
                prop.Key.Should().NotBeNullOrWhiteSpace();
                prop.Value.Type.Should().BeOneOf(validJsonSchemaTypes,
                    $"Property '{prop.Key}' type '{prop.Value.Type}' must be a valid JSON Schema type in {tool.Name}");
                prop.Value.Description.Should().NotBeNullOrWhiteSpace(
                    $"Property '{prop.Key}' in tool '{tool.Name}' should have a description");

                if (prop.Value.Enum is { Count: > 0 } enumValues)
                {
                    enumValues.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v),
                        $"Enum values in '{prop.Key}' of tool '{tool.Name}' must be non-empty strings");
                }
            }
        }
    }

    [Fact]
    public void AntigravitySchemas_OnDisk_ParseAndValidateAsStandardJsonSchemas()
    {
        string catalogDir = Path.Combine(Path.GetTempPath(), $"kongor_agy_schema_test_{Guid.NewGuid():N}");
        try
        {
        AntigravityRegistrar.Register(catalogDir);
        Directory.Exists(catalogDir).Should().BeTrue($"Catalog directory {catalogDir} must exist");

        string[] expectedTools =
        [
            "kongor_server_manage",
            "kongor_game_manage",
            "kongor_game_command",
            "kongor_game_telemetry",
            "kongor_capture_window",
            "kongor_ui_render_view"
        ];

        foreach (string toolName in expectedTools)
        {
            string filePath = Path.Combine(catalogDir, $"{toolName}.json");
            File.Exists(filePath).Should().BeTrue($"Schema file '{toolName}.json' must exist in {catalogDir}");

            string jsonContent = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(jsonContent);
            JsonElement root = doc.RootElement;

            root.TryGetProperty("name", out JsonElement nameElem).Should().BeTrue();
            nameElem.GetString().Should().Be(toolName);

            root.TryGetProperty("description", out JsonElement descElem).Should().BeTrue();
            descElem.GetString().Should().NotBeNullOrWhiteSpace();

            root.TryGetProperty("parameters", out JsonElement paramsElem).Should().BeTrue();
            paramsElem.ValueKind.Should().Be(JsonValueKind.Object);

            paramsElem.TryGetProperty("type", out JsonElement typeElem).Should().BeTrue();
            typeElem.GetString().Should().Be("object");

            paramsElem.TryGetProperty("properties", out JsonElement propsElem).Should().BeTrue();
            propsElem.ValueKind.Should().Be(JsonValueKind.Object);

            paramsElem.TryGetProperty("required", out JsonElement reqElem).Should().BeTrue();
            reqElem.ValueKind.Should().Be(JsonValueKind.Array);
        }

        string instructionsFile = Path.Combine(catalogDir, "instructions.md");
        File.Exists(instructionsFile).Should().BeTrue("instructions.md must exist in catalog");
        string mdText = File.ReadAllText(instructionsFile);
        foreach (string toolName in expectedTools)
        {
            mdText.Should().Contain(toolName);
        }
        }
        finally
        {
            try { if (Directory.Exists(catalogDir)) Directory.Delete(catalogDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // 2. Adversarial Tool Execution Dispatch (Unknown Tools, Malformed Names)
    // =========================================================================

    [Theory]
    [InlineData("non_existent_tool_123")]
    [InlineData("kongor_fake_tool")]
    [InlineData("'; DROP TABLE tools; --")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("🎮🚀_tool")]
    public async Task ProcessMessageAsync_UnknownToolName_ReturnsToolCallResultWithIsErrorTrue(string unknownName)
    {
        McpServer server = new();
        var requestObj = new
        {
            jsonrpc = "2.0",
            id = 501,
            method = "tools/call",
            @params = new
            {
                name = unknownName,
                arguments = new { }
            }
        };

        string reqJson = JsonSerializer.Serialize(requestObj);
        string? respJson = await server.ProcessMessageAsync(reqJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Id!.Value.Value.Should().Be(501L);
        response.Error.Should().BeNull("Tool execution failure should be reported in tool result envelope, not RPC error");
        response.Result.Should().NotBeNull();

        ToolCallResult toolResult = JsonRpcSerializer.Deserialize<ToolCallResult>(JsonRpcSerializer.Serialize(response.Result))!;
        toolResult.IsError.Should().BeTrue("isError must be true for unknown tool");
        toolResult.Content.Should().ContainSingle();
        ((TextContentItem)toolResult.Content[0]).Text.Should().Contain("Unknown tool");
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolNameCaseInsensitive_ExecutesSuccessfully()
    {
        McpServer server = new();
        var requestObj = new
        {
            jsonrpc = "2.0",
            id = 502,
            method = "tools/call",
            @params = new
            {
                name = "KONGOR_SERVER_MANAGE",
                arguments = new { action = "status" }
            }
        };

        string reqJson = JsonSerializer.Serialize(requestObj);
        string? respJson = await server.ProcessMessageAsync(reqJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().BeNull();

        ToolCallResult toolResult = JsonRpcSerializer.Deserialize<ToolCallResult>(JsonRpcSerializer.Serialize(response.Result))!;
        toolResult.IsError.Should().BeFalse();
    }

    // =========================================================================
    // 3. Adversarial Invalid Parameter Types & Missing Required Fields
    // =========================================================================

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":601,\"method\":\"tools/call\",\"params\":\"string_instead_of_object\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":602,\"method\":\"tools/call\",\"params\":[1, 2, 3]}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":603,\"method\":\"tools/call\",\"params\":12345}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":604,\"method\":\"tools/call\",\"params\":true}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":605,\"method\":\"tools/call\",\"params\":null}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":606,\"method\":\"tools/call\"}")]
    public async Task ProcessMessageAsync_ToolsCall_NonObjectParams_ReturnsInvalidParamsRpcError(string rawJson)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":610,\"method\":\"tools/call\",\"params\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":611,\"method\":\"tools/call\",\"params\":{\"name\":\"\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":612,\"method\":\"tools/call\",\"params\":{\"name\":\"   \"}}")]
    public async Task ProcessMessageAsync_ToolsCall_MissingOrEmptyName_ReturnsInvalidParamsRpcError(string rawJson)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":620,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_command\",\"arguments\":{}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":621,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_command\",\"arguments\":{\"command\":\"\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":622,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_command\",\"arguments\":{\"command\":\"   \"}}}")]
    public async Task ProcessMessageAsync_GameCommand_MissingRequiredCommand_ReturnsIsErrorTrue(string rawJson)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().BeNull();

        ToolCallResult toolResult = JsonRpcSerializer.Deserialize<ToolCallResult>(JsonRpcSerializer.Serialize(response.Result))!;
        toolResult.IsError.Should().BeTrue();
        ((TextContentItem)toolResult.Content[0]).Text.Should().Contain("Missing required parameter 'command'");
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":630,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_ui_render_view\",\"arguments\":{}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":631,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_ui_render_view\",\"arguments\":{\"view_name\":\"\"}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":632,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_ui_render_view\",\"arguments\":{\"view_name\":\"   \"}}}")]
    public async Task ProcessMessageAsync_UiRenderView_MissingRequiredViewName_ReturnsIsErrorTrue(string rawJson)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().BeNull();

        ToolCallResult toolResult = JsonRpcSerializer.Deserialize<ToolCallResult>(JsonRpcSerializer.Serialize(response.Result))!;
        toolResult.IsError.Should().BeTrue();
        ((TextContentItem)toolResult.Content[0]).Text.Should().Contain("Missing required parameter 'view_name'");
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolsCall_WithExtraUnknownArguments_FailsClosedWithUnknownArgument()
    {
        McpServer server = new();
        string rawJson = "{\"jsonrpc\":\"2.0\",\"id\":640,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_server_manage\",\"arguments\":{\"action\":\"status\",\"unexpected_field\":12345,\"arbitrary_payload\":{\"foo\":\"bar\"}}}}";

        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().BeNull();

        ToolCallResult toolResult = JsonRpcSerializer.Deserialize<ToolCallResult>(JsonRpcSerializer.Serialize(response.Result))!;
        toolResult.IsError.Should().BeTrue();
        ((TextContentItem)toolResult.Content[0]).Text.Should().Contain("unknown_argument");
    }

    // =========================================================================
    // 4. JSON-RPC Protocol Framing, Error Codes, and Id Variety
    // =========================================================================

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":701}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":702,\"method\":\"\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":703,\"method\":\"   \"}")]
    public async Task ProcessMessageAsync_MissingOrEmptyMethod_ReturnsInvalidRequestRpcError(string rawJson)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(rawJson);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidRequest);
    }

    [Theory]
    [InlineData("{ bad json}")]
    [InlineData("not a json object at all")]
    [InlineData("{\"jsonrpc\": \"2.0\", \"method\": unquoted}")]
    public async Task ProcessMessageAsync_MalformedJson_ReturnsParseErrorRpcError(string malformed)
    {
        McpServer server = new();
        string? respJson = await server.ProcessMessageAsync(malformed);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Id.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.ParseError);
    }

    [Fact]
    public async Task ProcessMessageAsync_IdRepresentations_PreservesExactTypeAndValue()
    {
        McpServer server = new();

        // 1. Numeric Long Id
        string resp1 = (await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":9223372036854775800,\"method\":\"ping\"}"))!;
        resp1.Should().Contain("\"id\":9223372036854775800");

        // 2. String Id
        string resp2 = (await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":\"urn:kongor:req:42\",\"method\":\"ping\"}"))!;
        resp2.Should().Contain("\"id\":\"urn:kongor:req:42\"");

        // 3. Floating Point Id
        string resp3 = (await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":3.14159,\"method\":\"ping\"}"))!;
        resp3.Should().Contain("\"id\":3.14159");
    }

    [Fact]
    public async Task ProcessMessageAsync_LeadingByteOrderMarkAndWhitespace_HandledGracefully()
    {
        McpServer server = new();
        string requestWithBom = "\uFEFF   \r\n   {\"jsonrpc\":\"2.0\",\"id\":801,\"method\":\"ping\"}   \r\n";

        string? respJson = await server.ProcessMessageAsync(requestWithBom);

        respJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(respJson!)!;
        response.Id!.Value.Value.Should().Be(801L);
        response.Error.Should().BeNull();
    }

    // =========================================================================
    // 5. Concurrency & High Load Stress
    [Fact]
    public async Task Registry_ConcurrentToolExecutions_HandlesHighParallelLoad()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        for (int i = 0; i < 6; i++)
        {
            string name = $"concurrent_tool_{i}";
            registry.RegisterTool(new ToolDefinition { Name = name, Description = "Concurrent test tool" },
                (args, ct) => Task.FromResult(ToolCallResult.Text($"Executed {name}")));
        }

        const int iterations = 100;
        Task<ToolCallResult>[] tasks = new Task<ToolCallResult>[iterations];

        for (int i = 0; i < iterations; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(async () =>
            {
                string toolName = $"concurrent_tool_{idx % 6}";
                return await registry.ExecuteToolAsync(toolName, JsonElement.Parse("{}"));
            });
        }

        ToolCallResult[] results = await Task.WhenAll(tasks);
        results.Should().HaveCount(iterations);
        results.Should().OnlyContain(r => !r.IsError);
    }

    // =========================================================================
    // 6. Strict Cross-IDE Schema & Protocol Negotiation Gate (H2-1a)
    // protects: Gemini/OpenAI/Claude/Cursor/Zed strict function-declaration compatibility across ALL registered tools & disk JSONs
    // fails_when: Any tool emits 'type: array' without 'items', nested 'type: object' without 'properties', 'additionalProperties', or 'outputSchema'
    // seam: McpToolRegistry.ListTools("all"), JsonRpcSerializer, AntigravityRegistrar.GenerateToolSchemaJson, and McpServer.ProcessMessageAsync
    // =========================================================================

    [Fact]
    public void AllRegisteredTools_StrictCrossIdeFunctionDeclarationSchema_PassesGeminiAndOpenAiRules()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.SetDefaultToolsets("core");
        IReadOnlyList<ToolDefinition> coreTools = registry.ListTools();
        IReadOnlyList<ToolDefinition> allTools = registry.ListTools(toolsets: "all");

        coreTools.Count.Should().BeLessThanOrEqualTo(20, "Core advertisement must stay well below Cursor's ~40-tool ceiling");
        allTools.Count.Should().BeGreaterThanOrEqualTo(90, "All canonical registered tools must be covered by the strict validator");

        string[] validTypes = ["string", "number", "integer", "boolean", "object", "array"];

        foreach (ToolDefinition tool in allTools)
        {
            tool.Annotations.Should().NotBeNull($"Tool '{tool.Name}' must populate MCP ToolAnnotations");

            // 1. Validate MCP wire JSON (tools/list)
            string wireJson = JsonRpcSerializer.Serialize(tool);
            using JsonDocument wireDoc = JsonDocument.Parse(wireJson);
            wireDoc.RootElement.TryGetProperty("outputSchema", out _).Should().BeFalse(
                $"Tool '{tool.Name}' must not advertise outputSchema without structuredContent support");
            JsonElement wireInputSchema = wireDoc.RootElement.GetProperty("inputSchema");
            ValidateSchemaNodeRecursive(wireInputSchema, isRootInputSchema: true, $"{tool.Name}.inputSchema", validTypes);

            // 2. Validate Antigravity on-disk JSON (AntigravityRegistrar.GenerateToolSchemaJson)
            string diskJson = AntigravityRegistrar.GenerateToolSchemaJson(tool);
            using JsonDocument diskDoc = JsonDocument.Parse(diskJson);
            JsonElement diskParams = diskDoc.RootElement.GetProperty("parameters");
            ValidateSchemaNodeRecursive(diskParams, isRootInputSchema: true, $"{tool.Name}.parameters", validTypes);
        }
    }

    private static void ValidateSchemaNodeRecursive(JsonElement node, bool isRootInputSchema, string path, string[] validTypes)
    {
        node.TryGetProperty("additionalProperties", out _).Should().BeFalse(
            $"'{path}' must not emit additionalProperties (rejected by strict Gemini function declarations)");

        node.TryGetProperty("type", out JsonElement typeEl).Should().BeTrue($"'{path}' must specify 'type'");
        string typeStr = typeEl.GetString() ?? string.Empty;
        typeStr.Should().BeOneOf(validTypes, $"'{path}' has unsupported JSON Schema type '{typeStr}'");

        if (typeStr == "array")
        {
            node.TryGetProperty("items", out JsonElement itemsEl).Should().BeTrue(
                $"Array schema at '{path}' MUST specify 'items' for Gemini/OpenAI compatibility");
            itemsEl.ValueKind.Should().Be(JsonValueKind.Object, $"'{path}.items' must be a schema object");
            ValidateSchemaNodeRecursive(itemsEl, isRootInputSchema: false, $"{path}.items", validTypes);
        }
        else if (typeStr == "object")
        {
            node.TryGetProperty("properties", out JsonElement propsEl).Should().BeTrue(
                $"Object schema at '{path}' must specify 'properties'");
            propsEl.ValueKind.Should().Be(JsonValueKind.Object);

            var propList = propsEl.EnumerateObject().ToList();
            if (!isRootInputSchema)
            {
                propList.Should().NotBeEmpty(
                    $"Nested object schema at '{path}' MUST declare at least one property (Gemini rejects bare empty objects)");
            }

            foreach (JsonProperty prop in propList)
            {
                ValidateSchemaNodeRecursive(prop.Value, isRootInputSchema: false, $"{path}.{prop.Name}", validTypes);
            }
        }
    }

    private sealed class SampleAttributedToolHost
    {
        public sealed class SampleFilterSpec
        {
            public string? Hero { get; set; }
            public int MinLevel { get; set; }
        }

        [ModelContextProtocol.Server.McpServerTool(Name = "kongor_test_attributed_array", ReadOnly = true)]
        [System.ComponentModel.Description("Test attributed tool with array and object parameters.")]
        public static string InspectSample(string[] tags, SampleFilterSpec filter) => $"{tags.Length}:{filter.Hero}";
    }

    [Fact]
    public void OfficialSdkAttributedTools_ArrayAndObjectParameters_EmitValidItemsAndProperties()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        int count = registry.RegisterAttributedToolsFromType(typeof(SampleAttributedToolHost));
        count.Should().Be(1);

        ToolDefinition? tool = registry.GetTool("kongor_test_attributed_array");
        tool.Should().NotBeNull();

        string[] validTypes = ["string", "number", "integer", "boolean", "object", "array"];
        using JsonDocument doc = JsonDocument.Parse(JsonRpcSerializer.Serialize(tool!));
        ValidateSchemaNodeRecursive(doc.RootElement.GetProperty("inputSchema"), true, "kongor_test_attributed_array.inputSchema", validTypes);
    }

    [Theory]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2099-01-01", "2025-06-18")]
    public async Task Initialize_ProtocolVersionNegotiation_EchoesSupportedAndFallsBackToLatest(string requested, string expected)
    {
        using McpServer server = new();
        string req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 901,
            method = "initialize",
            @params = new
            {
                protocolVersion = requested,
                capabilities = new { },
                clientInfo = new { name = "cross-ide-test", version = "1.0.0" }
            }
        });

        string? resp = await server.ProcessMessageAsync(req);
        resp.Should().NotBeNull();
        using JsonDocument doc = JsonDocument.Parse(resp!);
        string actual = doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString()!;
        actual.Should().Be(expected);
    }
}
