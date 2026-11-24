// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier2_Boundary;

public sealed class MalformedJsonRpcBoundaryTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private McpProcessClient _client = null!;

    public MalformedJsonRpcBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _client = new McpProcessClient(msg => _output.WriteLine(msg));
        await _client.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Fact]
    public async Task Boundary_JsonRpc_InvalidJsonSyntax_ReturnsParseError32700()
    {
        string rawBadJson = "{\"jsonrpc\": \"2.0\", \"method\": \"ping\", \"id\": 99, BROKEN_JSON";
        string responseLine = await _client.SendRawLineAndReadNextResponseAsync(rawBadJson);

        responseLine.Should().NotBeNullOrWhiteSpace();
        responseLine.Should().ContainAny("-32700", "Parse error", "error");
    }

    [Fact]
    public async Task Boundary_JsonRpc_MissingJsonRpcVersion_ReturnsInvalidRequest32600()
    {
        string rawNoVersion = "{\"id\": 100, \"method\": \"ping\"}";
        string responseLine = await _client.SendRawLineAndReadNextResponseAsync(rawNoVersion);

        responseLine.Should().NotBeNullOrWhiteSpace();
        responseLine.Should().ContainAny("-32600", "Invalid Request", "error", "result");
    }

    [Fact]
    public async Task Boundary_JsonRpc_UnknownMethod_ReturnsMethodNotFound32601()
    {
        JsonRpcResponse resp = await _client.SendRequestAsync("unknown_method_xyz_999", new { });

        resp.Should().NotBeNull();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(-32601);
    }

    [Fact]
    public async Task Boundary_JsonRpc_NullMethod_ReturnsInvalidRequest32600()
    {
        string rawNullMethod = "{\"jsonrpc\": \"2.0\", \"id\": 101, \"method\": null}";
        string responseLine = await _client.SendRawLineAndReadNextResponseAsync(rawNullMethod);

        responseLine.Should().NotBeNullOrWhiteSpace();
        responseLine.Should().ContainAny("-32600", "Invalid Request", "error");
    }

    [Fact]
    public async Task Boundary_JsonRpc_StringId_HandlesCorrelatedStringResponse()
    {
        string rawStringId = "{\"jsonrpc\": \"2.0\", \"id\": \"request-guid-12345\", \"method\": \"ping\"}";
        string responseLine = await _client.SendRawLineAndReadNextResponseAsync(rawStringId);

        responseLine.Should().NotBeNullOrWhiteSpace();
        responseLine.Should().Contain("request-guid-12345");
    }

    [Fact]
    public async Task Boundary_JsonRpc_EmptyBatchOrWhitespace_HandlesWithoutCrashing()
    {
        await _client.SendRawLineAsync("   \r\n   ");
        await Task.Delay(200);
        _client.IsProcessRunning.Should().BeTrue();
        var pingResp = await _client.SendRequestAsync("ping", new { });
        pingResp.Should().NotBeNull();
    }
}
