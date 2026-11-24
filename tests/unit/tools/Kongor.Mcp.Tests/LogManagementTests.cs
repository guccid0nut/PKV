// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class LogManagementTests
{
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    [Fact]
    public async Task GameClientManage_GetClientLogsAsync_FiltersErrorLevel()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_client_" + Guid.NewGuid().ToString("N"));
        string logsDir = Path.Combine(tempDir, "userdataKONGOR", "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            string consoleLog = Path.Combine(logsDir, "console.log");
            string[] testLines =
            [
                "[INFO] Client initialization complete",
                "[WARN] Texture mipmap generation took 45ms",
                "[ERROR] Shader compile failed for hero_empath.material",
                "[FATAL] Crash inside K2 SceneRenderer"
            ];
            File.WriteAllLines(consoleLog, testLines);

            var handler = new GameClientManageHandler(_timeProvider);
            var result = await handler.GetClientLogsAsync(50, "console", "error", tempDir);

            Assert.False(result.IsError);
            Assert.Single(result.Content);

            var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
            using var doc = JsonDocument.Parse(textItem.Text);
            var root = doc.RootElement;
            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("logs", root.GetProperty("action").GetString());
            Assert.Equal("error", root.GetProperty("level").GetString());

            var lines = root.GetProperty("lines");
            Assert.Equal(2, lines.GetArrayLength()); // [ERROR] and [FATAL]
            Assert.Equal("ERROR", lines[0].GetProperty("level").GetString());
            Assert.Equal("FATAL", lines[1].GetProperty("level").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task GameClientManage_GetClientLogsAsync_ReturnsAllLines_WhenLevelIsAll()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_client_" + Guid.NewGuid().ToString("N"));
        string logsDir = Path.Combine(tempDir, "userdataKONGOR", "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            string consoleLog = Path.Combine(logsDir, "console.log");
            string[] testLines =
            [
                "[INFO] Client initialization complete",
                "[WARN] Texture mipmap generation took 45ms",
                "[ERROR] Shader compile failed for hero_empath.material"
            ];
            File.WriteAllLines(consoleLog, testLines);

            var handler = new GameClientManageHandler(_timeProvider);
            var result = await handler.GetClientLogsAsync(50, "console", "all", tempDir);

            Assert.False(result.IsError);
            var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
            using var doc = JsonDocument.Parse(textItem.Text);
            var lines = doc.RootElement.GetProperty("lines");
            Assert.Equal(3, lines.GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task GameClientManage_HandleAsync_DispatchesLogsAction()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var handler = new GameClientManageHandler(_timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"action\": \"logs\", \"logType\": \"console\", \"level\": \"all\", \"lines\": 10}");
        var result = await handler.HandleAsync(jsonDoc.RootElement);

        Assert.False(result.IsError);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("logs", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task GameServerManage_GetServerLogsAsync_FiltersWarningLevel()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_test_server_" + Guid.NewGuid().ToString("N"));
        string logsDir = Path.Combine(tempDir, "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            string consoleLog = Path.Combine(logsDir, "console.log");
            string[] testLines =
            [
                "[INFO] Dedicated worker listening on port 11235",
                "[WARN] High server frame time: 24ms",
                "[ERROR] Failed to load map 'caldavar_missing'",
                "[INFO] Heartbeat sent to MasterServer"
            ];
            File.WriteAllLines(consoleLog, testLines);

            var handler = new GameServerManageHandler(_timeProvider);
            var result = await handler.GetServerLogsAsync(11235, null, 50, "warn", tempDir);

            Assert.False(result.IsError);
            var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
            using var doc = JsonDocument.Parse(textItem.Text);
            var root = doc.RootElement;
            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("logs", root.GetProperty("action").GetString());

            var lines = root.GetProperty("lines");
            Assert.Equal(2, lines.GetArrayLength()); // [WARN] and [ERROR]
            Assert.Equal("WARN", lines[0].GetProperty("level").GetString());
            Assert.Equal("ERROR", lines[1].GetProperty("level").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task GameServerManage_HandleAsync_DispatchesLogsAction()
    {
        var handler = new GameServerManageHandler(_timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"action\": \"logs\", \"port\": 11235, \"level\": \"all\", \"lines\": 10}");
        var result = await handler.HandleAsync(jsonDoc.RootElement);

        Assert.False(result.IsError);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("logs", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task LifecycleService_GetRecentLogsAsync_FiltersErrorsCorrectly()
    {
        using var lifecycle = new LifecycleService(_timeProvider);
        var logs = await lifecycle.GetRecentLogsAsync(service: "all", maxLines: 50, level: "error");

        Assert.NotNull(logs);
        foreach (var log in logs)
        {
            Assert.True(log.IsError, $"Expected only error logs, but encountered level '{log.Level}': {log.Message}");
            Assert.True(log.Level is "ERROR" or "FATAL");
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameServerManage_HandleAsync_DispatchesCommandAction()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        var handler = new GameServerManageHandler(_timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"action\": \"command\", \"port\": 11235, \"command\": \"AddFakePlayer 2\"}");
        var result = await handler.HandleAsync(jsonDoc.RootElement);

        // When server is not running on 11235, it reports an error/warning result with action: command
        Assert.NotNull(result);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("command", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(11235, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("AddFakePlayer 2", doc.RootElement.GetProperty("command").GetString());
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_HandleAsync_RoutesToPort()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        var serverHandler = new GameServerManageHandler(_timeProvider);
        var commandHandler = new GameCommandHandler(null, serverHandler, _timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"command\": \"AddFakePlayer 2\", \"port\": 11235}");
        var result = await commandHandler.HandleAsync(jsonDoc.RootElement);

        Assert.NotNull(result);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("command", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(11235, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameClientManage_HandleAsync_DispatchesCommandAction()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        var handler = new GameClientManageHandler(_timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"action\": \"command\", \"command\": \"reloadui\"}");
        var result = await handler.HandleAsync(jsonDoc.RootElement);

        // When client IPC pipe is not active, returns error result with target: client
        Assert.NotNull(result);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("client", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal("reloadui", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task GameCommandHandler_HandleClientCommandAsync_Dispatches()
    {
        var handler = new GameCommandHandler(null, null, _timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"command\": \"reloadui\"}");
        var result = await handler.HandleClientCommandAsync(jsonDoc.RootElement);

        Assert.NotNull(result);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("client", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal("reloadui", doc.RootElement.GetProperty("command").GetString());
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommandHandler_HandleServerCommandAsync_Dispatches()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        var serverHandler = new GameServerManageHandler(_timeProvider);
        var handler = new GameCommandHandler(null, serverHandler, _timeProvider);
        using var jsonDoc = JsonDocument.Parse("{\"command\": \"AddFakePlayer 2\", \"port\": 11235}");
        var result = await handler.HandleServerCommandAsync(jsonDoc.RootElement);

        Assert.NotNull(result);
        var textItem = Assert.IsType<TextContentItem>(result.Content[0]);
        using var doc = JsonDocument.Parse(textItem.Text);
        Assert.Equal("command", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(11235, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("AddFakePlayer 2", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task ServerManageHandler_LogsAction_RanksCandidateFilesByLastWriteTimeUtcDescending()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_test_srvlogs_" + Guid.NewGuid().ToString("N"));
        string topLogs = Path.Combine(tempRoot, "logs");
        string rollingMasterLogs = Path.Combine(tempRoot, "src", "services", "master-server", "logs");
        string rollingChatLogs = Path.Combine(tempRoot, "src", "services", "chat-server", "logs");
        Directory.CreateDirectory(topLogs);
        Directory.CreateDirectory(rollingMasterLogs);
        Directory.CreateDirectory(rollingChatLogs);

        try
        {
            DateTime fiveDaysAgoUtc = DateTime.UtcNow.AddDays(-5);
            DateTime currentBootUtc = DateTime.UtcNow;

            string staleMasterRolling = Path.Combine(rollingMasterLogs, "master_server20261114.log");
            File.WriteAllLines(staleMasterRolling,
            [
                "[INFO] Stale 5-day-old MasterServer boot line 1",
                "[ERROR] Stale 5-day-old MasterServer error line 2",
                "[INFO] Stale 5-day-old MasterServer boot line 3"
            ]);
            File.SetLastWriteTimeUtc(staleMasterRolling, fiveDaysAgoUtc);

            string activeMasterLog = Path.Combine(topLogs, "master-server.log");
            File.WriteAllLines(activeMasterLog,
            [
                "[INFO] Active current-boot MasterServer listening on 127.0.0.1:55555",
                "[INFO] Active current-boot MasterServer health check GREEN"
            ]);
            File.SetLastWriteTimeUtc(activeMasterLog, currentBootUtc);

            string staleChatRolling = Path.Combine(rollingChatLogs, "chat_server20261114.log");
            File.WriteAllLines(staleChatRolling, ["[INFO] Stale 5-day-old ChatServer line"]);
            File.SetLastWriteTimeUtc(staleChatRolling, fiveDaysAgoUtc);

            string activeChatLog = Path.Combine(topLogs, "chat-server.log");
            File.WriteAllLines(activeChatLog,
            [
                "[INFO] Active current-boot ChatServer listening on 127.0.0.1:11031"
            ]);
            File.SetLastWriteTimeUtc(activeChatLog, currentBootUtc.AddSeconds(-1));

            using LifecycleService lifecycle = new(_timeProvider, customSolutionRoot: tempRoot);
            ServerManageHandler handler = new(lifecycle, _timeProvider);

            string payloadJson = JsonSerializer.Serialize(new
            {
                action = "logs",
                target = "master-server",
                lines = 2,
                level = "all"
            });
            using JsonDocument reqDoc = JsonDocument.Parse(payloadJson);
            ToolCallResult res = await handler.HandleAsync(reqDoc.RootElement);

            Assert.False(res.IsError);
            TextContentItem textItem = Assert.IsType<TextContentItem>(res.Content[0]);
            using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
            JsonElement recentLogs = respDoc.RootElement.GetProperty("recentLogs");
            Assert.Equal(2, recentLogs.GetArrayLength());
            foreach (JsonElement entry in recentLogs.EnumerateArray())
            {
                Assert.Equal("master-server.log", entry.GetProperty("sourceFile").GetString());
                Assert.Contains("Active current-boot MasterServer", entry.GetProperty("message").GetString());
                Assert.DoesNotContain("Stale 5-day-old", entry.GetProperty("message").GetString());
            }

            // Cluster target surfaces active current-boot logs across both master-server and chat-server
            string clusterPayload = JsonSerializer.Serialize(new
            {
                action = "logs",
                target = "cluster",
                lines = 3,
                level = "all"
            });
            using JsonDocument clusterReq = JsonDocument.Parse(clusterPayload);
            ToolCallResult clusterRes = await handler.HandleAsync(clusterReq.RootElement);
            TextContentItem clusterText = Assert.IsType<TextContentItem>(clusterRes.Content[0]);
            using JsonDocument clusterDoc = JsonDocument.Parse(clusterText.Text);
            JsonElement clusterLogs = clusterDoc.RootElement.GetProperty("recentLogs");
            Assert.Equal(3, clusterLogs.GetArrayLength());
            Assert.Contains(clusterLogs.EnumerateArray(), e => e.GetProperty("sourceFile").GetString() == "chat-server.log");
            Assert.Contains(clusterLogs.EnumerateArray(), e => e.GetProperty("sourceFile").GetString() == "master-server.log");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
