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
using Xunit;

namespace Kongor.Mcp.Tests;

[Collection("OverlayPipeCollection")]
public class TaoPhase5MultiTabTests
{
    [Fact]
    public async Task SelfHealHandler_OverlayTab_WhenPipeOffline_ReturnsGracefulError()
    {
        var handler = new SelfHealHandler(overlayPipeName: "kongor-overlay-offline-" + Guid.NewGuid().ToString("N"));
        var args = JsonElement.Parse(@"{ ""action"": ""overlay_tab"", ""tab"": ""tab-fleet"" }");

        var result = await handler.HandleAsync(args);
        Assert.True(result.IsError);
        string err = ((TextContentItem)result.Content[0]).Text;
        Assert.Contains("Failed to switch HUD tab", err);
    }

    [Fact]
    public async Task SelfHealHandler_OverlayExec_WhenPipeOffline_ReturnsGracefulError()
    {
        var handler = new SelfHealHandler(overlayPipeName: "kongor-overlay-offline-" + Guid.NewGuid().ToString("N"));
        var args = JsonElement.Parse(@"{ ""action"": ""overlay_exec"", ""command"": ""echo test"" }");

        var result = await handler.HandleAsync(args);
        Assert.True(result.IsError);
        string err = ((TextContentItem)result.Content[0]).Text;
        Assert.Contains("Failed to execute in-engine command", err);
    }

    [Fact]
    public async Task SelfHealHandler_OverlayExec_WhenCommandMissing_ReturnsError()
    {
        var handler = new SelfHealHandler(overlayPipeName: "kongor-overlay-offline-" + Guid.NewGuid().ToString("N"));
        var args = JsonElement.Parse(@"{ ""action"": ""overlay_exec"" }");

        var result = await handler.HandleAsync(args);
        Assert.True(result.IsError);
        string err = ((TextContentItem)result.Content[0]).Text;
        Assert.Contains("Missing required parameter 'command'", err);
    }

    [Fact]
    public async Task SelfHealHandler_OverlayMultiTabAndExec_WithMockPipeServer_Succeeds()
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

        // Start mock named pipe server simulating overlay responses for 4 calls:
        // 1. TAB:tab-fleet
        // 2. TAB:tab-console
        // 3. EXEC:echo Phase 5 Verified
        // 4. GET_TABS
        var serverTask = Task.WhenAll(Enumerable.Range(0, servers.Length).Select(idx => Task.Run(async () =>
        {
            using var server = servers[idx];
            await waitTasks[idx];
            byte[] buffer = new byte[1024];
            int read = await server.ReadAsync(buffer.AsMemory(), cts.Token);
            string req = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            string resp;
            if (req.StartsWith("TAB:"))
            {
                string target = req[4..].Trim();
                resp = "OK " + target + "\n";
            }
            else if (req.StartsWith("EXEC:"))
            {
                resp = "OK\n";
            }
            else if (req == "GET_TABS")
            {
                resp = "{\"active\":\"tab-console\",\"tabs\":[\"tab-overview\",\"tab-fleet\",\"tab-vulkan\",\"tab-vfs\",\"tab-console\"]}\n";
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

        // 1. Switch to tab-fleet
        var tabFleetArgs = JsonElement.Parse(@"{ ""action"": ""overlay_tab"", ""tab"": ""tab-fleet"" }");
        var res1 = await handler.HandleAsync(tabFleetArgs);
        Assert.False(res1.IsError);
        string text1 = ((TextContentItem)res1.Content[0]).Text;
        Assert.Contains("tab-fleet", text1);

        await Task.Delay(30);

        // 2. Switch to tab-console
        var tabConsoleArgs = JsonElement.Parse(@"{ ""action"": ""overlay_tab"", ""tab"": ""tab-console"" }");
        var res2 = await handler.HandleAsync(tabConsoleArgs);
        Assert.False(res2.IsError);
        string text2 = ((TextContentItem)res2.Content[0]).Text;
        Assert.Contains("tab-console", text2);

        await Task.Delay(30);

        // 3. Execute command through overlay
        var execArgs = JsonElement.Parse(@"{ ""action"": ""overlay_exec"", ""command"": ""echo Phase 5 Verified"" }");
        var res3 = await handler.HandleAsync(execArgs);
        Assert.False(res3.IsError);
        string text3 = ((TextContentItem)res3.Content[0]).Text;
        Assert.Contains("echo Phase 5 Verified", text3);

        await Task.Delay(30);

        // 4. Query available tabs
        var getTabsArgs = JsonElement.Parse(@"{ ""action"": ""overlay_tabs"" }");
        var res4 = await handler.HandleAsync(getTabsArgs);
        Assert.False(res4.IsError);
        string text4 = ((TextContentItem)res4.Content[0]).Text;
        Assert.Contains("tab-console", text4);
        Assert.Contains("tab-overview", text4);

        await serverTask;
    }
}
