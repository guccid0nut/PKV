// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

[Collection("OverlayPipeCollection")]
public class TaoPhase4OverlaySelfHealingTests
{
    [Fact]
    public async Task SelfHealHandler_OverlaySnapshot_WhenPipeOffline_ReturnsGracefulError()
    {
        var handler = new SelfHealHandler(overlayPipeName: "kongor-overlay-offline-" + Guid.NewGuid().ToString("N"));
        var args = JsonElement.Parse(@"{ ""action"": ""overlay_snapshot"" }");

        var result = await handler.HandleAsync(args);
        Assert.True(result.IsError);
        string err = ((TextContentItem)result.Content[0]).Text;
        Assert.Contains("Failed to retrieve snapshot", err);
    }

    [Fact]
    public async Task SelfHealHandler_OverlayHeal_WhenPipeOffline_ReturnsGracefulError()
    {
        var handler = new SelfHealHandler(overlayPipeName: "kongor-overlay-offline-" + Guid.NewGuid().ToString("N"));
        var args = JsonElement.Parse(@"{ ""action"": ""overlay_heal"" }");

        var result = await handler.HandleAsync(args);
        Assert.True(result.IsError);
        string err = ((TextContentItem)result.Content[0]).Text;
        Assert.Contains("Failed to connect to Vulkan overlay pipe", err);
    }

    [Fact]
    public async Task SelfHealHandler_OverlayActions_WithMockPipeServer_ExecuteTruthEnvelope()
    {
        string pipeName = "kongor-overlay-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var servers = new NamedPipeServerStream[4];
        var waitTasks = new Task[4];
        for (int i = 0; i < servers.Length; i++)
        {
            servers[i] = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            waitTasks[i] = servers[i].WaitForConnectionAsync(cts.Token);
        }

        // Start mock named pipe server simulating overlay responses (exactly 4 client connections: fault, heal, snapshot-after-heal, snapshot)
        var serverTask = Task.WhenAll(Enumerable.Range(0, servers.Length).Select(idx => Task.Run(async () =>
        {
            using var server = servers[idx];
            await waitTasks[idx];
            byte[] buffer = new byte[1024];
            int read = await server.ReadAsync(buffer.AsMemory(), cts.Token);
            string req = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            string resp;
            if (req.StartsWith("FAULT:"))
            {
                resp = "OK\n";
            }
            else if (req.StartsWith("HEAL"))
            {
                resp = "OK REC-12345678\n";
            }
            else if (req == "GET_SNAPSHOT")
            {
                resp = "{\"masterOnline\":true,\"chatOnline\":true,\"gameServerOnline\":true,\"faultSeverity\":0,\"activeFaultSource\":\"NONE\",\"activeFaultDescription\":\"NOMINAL\"}\n";
            }
            else
            {
                resp = "OK\n";
            }

            byte[] respBytes = Encoding.UTF8.GetBytes(resp);
            await server.WriteAsync(respBytes.AsMemory(), cts.Token);
            await server.FlushAsync(cts.Token);
            if (OperatingSystem.IsWindows())
            {
                try { server.WaitForPipeDrain(); } catch { }
            }
            server.Disconnect();
        }, cts.Token)));

        var handler = new SelfHealHandler(overlayPipeName: pipeName);

        // 1. Test overlay_simulate_fault
        var faultArgs = JsonElement.Parse(@"{
            ""action"": ""overlay_simulate_fault"",
            ""source"": ""MASTER_SERVER"",
            ""severity"": 2,
            ""description"": ""Simulated Master Server outage""
        }");
        var faultResult = await handler.HandleAsync(faultArgs);
        Assert.False(faultResult.IsError);
        string faultJson = ((TextContentItem)faultResult.Content[0]).Text;
        using var faultDoc = JsonDocument.Parse(faultJson);
        Assert.Equal("success", faultDoc.RootElement.GetProperty("status").GetString());

        await Task.Delay(30);

        // 2. Test overlay_heal
        var healArgs = JsonElement.Parse(@"{
            ""action"": ""overlay_heal"",
            ""recipe"": ""restart_master""
        }");
        var healResult = await handler.HandleAsync(healArgs);
        Assert.False(healResult.IsError);
        string healJson = ((TextContentItem)healResult.Content[0]).Text;
        using var healDoc = JsonDocument.Parse(healJson);
        Assert.Equal("success", healDoc.RootElement.GetProperty("status").GetString());

        await Task.Delay(30);

        // 3. Test overlay_snapshot
        var snapArgs = JsonElement.Parse(@"{ ""action"": ""overlay_snapshot"" }");
        var snapResult = await handler.HandleAsync(snapArgs);
        Assert.False(snapResult.IsError);
        string snapJson = ((TextContentItem)snapResult.Content[0]).Text;
        using var snapDoc = JsonDocument.Parse(snapJson);
        Assert.Equal("success", snapDoc.RootElement.GetProperty("status").GetString());

        await serverTask;
    }
}
