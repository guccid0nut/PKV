// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Value-Weighted Testing Gate:
/// - protects: Official C# ModelContextProtocol SDK v2.2.0 Streamable HTTP/SSE transport (/mcp, /healthz),
///   canonical tool list isolation (no hidden alias bloat), hidden alias execution, case-insensitive tool lookup,
///   multi-modal ImageContentItem -> ImageContentBlock transport, kongor://* resources/prompts, --read-only guard,
///   and [McpServerTool] attribute registration.
/// - fails_when: OfficialMcpHttpHost or KongorOfficialMcpAdapter drops tool arguments, leaks aliases into ListTools,
///   fails to decode base64 PNG ImageContentItems, bypasses read-only mode, or breaks JSON-RPC over Streamable HTTP.
/// - seam: Real Kestrel WebApplication + official ModelContextProtocol.Client.McpClient over HttpClientTransport.
/// </summary>
[Collection("McpHttpAuthEnvCollection")]
public sealed class OfficialMcpSdkIntegrationTests
{
    private static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [McpServerToolType]
    private sealed class SampleAttributedToolSet
    {
        [McpServerTool(Name = "kongor_sample_echo", ReadOnly = true)]
        [Description("Echoes a message and multiplier back as a structured payload for SDK verification.")]
        public static Task<object> EchoAsync(
            [Description("Input text message")] string message,
            [Description("Repeat count")] int count = 2,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<object>(new
            {
                status = "ok",
                echoed = string.Join("-", Enumerable.Repeat(message, Math.Max(1, count))),
                count
            });
        }
    }

    [Fact]
    public async Task OfficialSdk_StreamableHttp_EndToEnd_Tools_Aliases_Images_Resources_Prompts()
    {
        int port = GetAvailablePort();
        McpToolRegistry registry = new(registerDefaults: true);

        // Register a multi-modal image test tool and an attributed tool
        byte[] samplePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        string sampleBase64 = Convert.ToBase64String(samplePng);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "kongor_test_image_capture",
                Description = "Returns a multi-modal PNG ImageContentItem and TextContentItem.",
                InputSchema = new ToolInputSchema { Type = "object" }
            },
            (_, _) => Task.FromResult(new ToolCallResult
            {
                IsError = false,
                Content =
                [
                    new TextContentItem("Captured test frame"),
                    new ImageContentItem(sampleBase64, "image/png")
                ]
            }),
            McpToolCategory.ReadOnly);
        registry.RegisterAlias("test_image_alias", "kongor_test_image_capture");

        SampleAttributedToolSet attributedTools = new();
        int registeredAttrCount = registry.RegisterAttributedToolsFromInstance(attributedTools);
        registeredAttrCount.Should().Be(1);

        using Kongor.Cli.Mcp.Protocol.McpServer kongorServer = new(registry, TextWriter.Null);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(kongorServer, "127.0.0.1", port, requireAuth: false);
        await app.StartAsync();

        try
        {
            // 1. Verify /healthz endpoint
            using HttpClient httpClient = new();
            HttpResponseMessage healthResp = await httpClient.GetAsync($"http://127.0.0.1:{port}/healthz");
            healthResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string healthJson = await healthResp.Content.ReadAsStringAsync();
            using JsonDocument healthDoc = JsonDocument.Parse(healthJson);
            healthDoc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
            healthDoc.RootElement.GetProperty("transport").GetString().Should().Be("streamable-http");
            healthDoc.RootElement.GetProperty("canonicalToolCount").GetInt32().Should().Be(registry.ListTools().Count);

            // 2. Connect official McpClient over Streamable HTTP (negotiating 2025-06-18 for resources/subscribe support)
            HttpClientTransport transport = new(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp")
            });

            await using McpClient client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ProtocolVersion = "2025-06-18"
                });

            // 3. ListTools should return canonical tools and exclude hidden aliases
            IList<McpClientTool> tools = await client.ListToolsAsync();
            tools.Select(t => t.Name).Should().Contain("kongor_rate_limit_status");
            tools.Select(t => t.Name).Should().Contain("kongor_sample_echo");
            tools.Select(t => t.Name).Should().NotContain("test_image_alias");
            tools.Select(t => t.Name).Should().NotContain("kongor_navigate");

            // 4. Call [McpServerTool] attributed tool via official McpClient
            CallToolResult echoResult = await client.CallToolAsync(
                "kongor_sample_echo",
                new Dictionary<string, object?>
                {
                    ["message"] = "kongor",
                    ["count"] = 3
                });
            echoResult.IsError.Should().NotBeTrue();
            TextContentBlock? echoText = echoResult.Content.OfType<TextContentBlock>().FirstOrDefault();
            echoText.Should().NotBeNull();
            echoText!.Text.Should().Contain("kongor-kongor-kongor");

            // 5. Call hidden alias and verify multi-modal ImageContentBlock preservation
            CallToolResult imgResult = await client.CallToolAsync("test_image_alias");
            imgResult.IsError.Should().NotBeTrue();
            imgResult.Content.OfType<TextContentBlock>().Should().ContainSingle(b => b.Text.Contains("Captured test frame"));
            ImageContentBlock? imgBlock = imgResult.Content.OfType<ImageContentBlock>().FirstOrDefault();
            imgBlock.Should().NotBeNull();
            imgBlock!.MimeType.Should().Be("image/png");
            imgBlock.DecodedData.ToArray().Should().Equal(samplePng);

            // 6. Call case-insensitive tool name
            CallToolResult upperResult = await client.CallToolAsync("KONGOR_RATE_LIMIT_STATUS");
            upperResult.IsError.Should().NotBeTrue();

            // 7. Unknown tool returns CallToolResult { IsError = true } instead of throwing protocol exception
            CallToolResult unknownResult = await client.CallToolAsync("kongor_nonexistent_tool_xyz");
            unknownResult.IsError.Should().BeTrue();

            // 8. Resources list, read, subscribe, and unsubscribe
            IList<McpClientResource> resources = await client.ListResourcesAsync();
            resources.Select(r => r.Uri).Should().Contain("kongor://cluster/metrics");

            var readRes = await client.ReadResourceAsync("kongor://cluster/metrics");
            readRes.Contents.OfType<TextResourceContents>().First().Text.Should().Contain("clusterServices");

            await client.SubscribeToResourceAsync("kongor://client/console");
            kongorServer.IsSubscribed("kongor://client/console").Should().BeTrue();

            await client.UnsubscribeFromResourceAsync("kongor://client/console");
            kongorServer.IsSubscribed("kongor://client/console").Should().BeFalse();

            // 9. Prompts list and get
            IList<McpClientPrompt> prompts = await client.ListPromptsAsync();
            prompts.Select(p => p.Name).Should().Contain("debug-k2-ui-blackscreen");

            var promptRes = await client.GetPromptAsync(
                "debug-k2-ui-blackscreen",
                new Dictionary<string, object?> { ["interface"] = "main_v2" });
            promptRes.Messages.Should().NotBeEmpty();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OfficialSdk_StreamableHttp_ReadOnlyMode_BlocksMutatingTools()
    {
        int port = GetAvailablePort();
        McpToolRegistry registry = new(registerDefaults: true);
        using Kongor.Cli.Mcp.Protocol.McpServer kongorServer = new(registry, TextWriter.Null, readOnlyMode: true);
        await using WebApplication app = OfficialMcpHttpHost.BuildHttpApp(kongorServer, "127.0.0.1", port, requireAuth: false);
        await app.StartAsync();

        try
        {
            HttpClientTransport transport = new(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp")
            });

            await using McpClient client = await McpClient.CreateAsync(transport);

            // ReadOnly tool should succeed
            CallToolResult allowedResult = await client.CallToolAsync("kongor_rate_limit_status");
            allowedResult.IsError.Should().NotBeTrue();

            // Mutating action on ServerManagement tool should be blocked with clear actionable error
            CallToolResult blockedResult = await client.CallToolAsync(
                "kongor_server_manage",
                new Dictionary<string, object?> { ["action"] = "stop" });
            blockedResult.IsError.Should().BeTrue();
            blockedResult.Content.OfType<TextContentBlock>().First().Text.Should().Contain("--read-only");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
