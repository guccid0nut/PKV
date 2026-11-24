// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class JsonRpcProtocolTests
{
    [Fact]
    public void JsonRpcId_Numeric_SerializesAndDeserializesCorrectly()
    {
        JsonRpcId id = new(42L);
        string json = JsonRpcSerializer.Serialize(id);
        json.Should().Be("42");

        JsonRpcId deserialized = JsonRpcSerializer.Deserialize<JsonRpcId>(json);
        deserialized.Value.Should().Be(42L);
        deserialized.IsNumber.Should().BeTrue();
        deserialized.IsString.Should().BeFalse();
        deserialized.IsNull.Should().BeFalse();
    }

    [Fact]
    public void JsonRpcId_String_SerializesAndDeserializesCorrectly()
    {
        JsonRpcId id = new("test-id-123");
        string json = JsonRpcSerializer.Serialize(id);
        json.Should().Be("\"test-id-123\"");

        JsonRpcId deserialized = JsonRpcSerializer.Deserialize<JsonRpcId>(json);
        deserialized.Value.Should().Be("test-id-123");
        deserialized.IsString.Should().BeTrue();
        deserialized.IsNumber.Should().BeFalse();
    }

    [Fact]
    public void JsonRpcRequest_Serialization_IncludesStandardFields()
    {
        JsonRpcRequest request = new()
        {
            Id = 1,
            Method = "initialize",
            Params = JsonElement.Parse("{\"protocolVersion\":\"2024-11-05\"}")
        };

        string json = JsonRpcSerializer.Serialize(request);
        json.Should().Contain("\"jsonrpc\":\"2.0\"");
        json.Should().Contain("\"id\":1");
        json.Should().Contain("\"method\":\"initialize\"");
        json.Should().Contain("\"protocolVersion\":\"2024-11-05\"");
    }

    [Fact]
    public void JsonRpcNotification_WhenInvoked_HasNoId()
    {
        JsonRpcNotification notification = JsonRpcNotification.Create("notifications/initialized");
        string json = JsonRpcSerializer.Serialize(notification);
        json.Should().Contain("\"method\":\"notifications/initialized\"");
        json.Should().NotContain("\"id\"");
    }

    [Fact]
    public void JsonRpcResponse_Success_SerializesResult()
    {
        var resultObj = new { status = "ok", count = 5 };
        JsonRpcResponse response = JsonRpcResponse.Success(10, resultObj);

        string json = JsonRpcSerializer.Serialize(response);
        json.Should().Contain("\"jsonrpc\":\"2.0\"");
        json.Should().Contain("\"id\":10");
        json.Should().Contain("\"status\":\"ok\"");
        json.Should().Contain("\"count\":5");
        json.Should().NotContain("\"error\"");
    }

    [Fact]
    public void JsonRpcResponse_Error_SerializesErrorCodeAndMessage()
    {
        JsonRpcResponse response = JsonRpcResponse.CreateError(
            "req-99",
            JsonRpcErrorCode.MethodNotFound,
            "Method not found",
            new { hint = "Check tools/list" }
        );

        string json = JsonRpcSerializer.Serialize(response);
        json.Should().Contain("\"code\":-32601");
        json.Should().Contain("\"message\":\"Method not found\"");
        json.Should().Contain("\"id\":\"req-99\"");
        json.Should().NotContain("\"result\"");
    }

    [Fact]
    public void ContentItem_PolymorphicSerialization_HandlesTextAndImage()
    {
        ToolCallResult textResult = ToolCallResult.Text("Operation succeeded");
        string textJson = JsonRpcSerializer.Serialize(textResult);
        textJson.Should().Contain("\"type\":\"text\"");
        textJson.Should().Contain("\"text\":\"Operation succeeded\"");
        textJson.Should().Contain("\"isError\":false");

        ToolCallResult imageResult = ToolCallResult.Image("iVBORw0KGgoAAAANSUhEUg==", "Window Capture");
        string imageJson = JsonRpcSerializer.Serialize(imageResult);
        imageJson.Should().Contain("\"type\":\"image\"");
        imageJson.Should().Contain("\"data\":\"iVBORw0KGgoAAAANSUhEUg==\"");
        imageJson.Should().Contain("\"mimeType\":\"image/png\"");
        imageJson.Should().Contain("\"text\":\"Window Capture\"");
    }

    [Fact]
    public void InitializeResult_OnConstruction_MatchesMcpSpecification()
    {
        InitializeResult initResult = new()
        {
            ProtocolVersion = McpProtocolVersion.Current,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Logging = new LoggingCapability()
            },
            ServerInfo = new ImplementationInfo
            {
                Name = "kongor",
                Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion
            }
        };

        string json = JsonRpcSerializer.Serialize(initResult);
        json.Should().Contain("\"protocolVersion\":\"2024-11-05\"");
        json.Should().Contain("\"name\":\"kongor\"");
        json.Should().Contain($"\"version\":\"{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}\"");
        json.Should().Contain("\"tools\":{\"listChanged\":false}");
        json.Should().Contain("\"logging\":{}");
    }

    [Fact]
    public async System.Threading.Tasks.Task McpServer_InitializeAndAboutResource_ExposeAuthorLicenseAndCorrespondingSource()
    {
        var registry = new McpToolRegistry(registerDefaults: false);
        using var server = new McpServer(registry);

        string? initRespJson = await server.ProcessMessageAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
        initRespJson.Should().NotBeNull();
        initRespJson.Should().Contain("\"author\":\"guccidonut\"");
        initRespJson.Should().Contain("\"license\":\"AGPL-3.0-or-later\"");
        initRespJson.Should().Contain("\"repositoryUrl\":\"https://github.com/guccidonut/PKV\"");

        string? aboutRespJson = await server.ProcessMessageAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://about\"}}");
        aboutRespJson.Should().NotBeNull();
        aboutRespJson.Should().Contain("guccidonut");
        aboutRespJson.Should().Contain("AGPL-3.0-or-later");
        aboutRespJson.Should().Contain("https://github.com/guccidonut/PKV");
        aboutRespJson.Should().Contain("Section 7(b)");
    }
}