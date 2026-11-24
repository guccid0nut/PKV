// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeVideoModernizationTests.cs
// System: ProjectKongor MCP Tests - TAO (Together as One)
// Subsystem: Phase 9 Hardware-Accelerated Video Decoding & Dedicated Server Purge
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NativeVideoModernizationTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "games", "server")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string GetContentText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return string.Empty;
    }

    [Fact]
    public async Task NativeManageHandler_VideoStatusAction_ReturnsValidTelemetryAndSafeLoginScreen()
    {
        string repoRoot = FindRepoRoot();
        var handler = new NativeManageHandler();
        var args = JsonElement.Parse($"{{\"action\": \"video_status\", \"repoRoot\": {JsonSerializer.Serialize(repoRoot)}}}");

        var result = await handler.HandleAsync(args);
        Assert.False(result.IsError);

        string json = GetContentText(result);
        var root = JsonElement.Parse(json);

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("video_status", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("loginScreenPreserved").GetBoolean());
        Assert.True(root.GetProperty("isServerPurged").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("hardwareAccelerationBackend").GetString()));
    }

    [Fact]
    public async Task NativeManageHandler_VideoDeployAndRollback_InTempEnvironment_OperatesCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_McpVideoTest_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client", "windows-x64");
        string stagedDir = Path.Combine(tempDir, "artifacts", "native-binaries", "windows-x64", "video");
        Directory.CreateDirectory(clientDir);
        Directory.CreateDirectory(stagedDir);

        try
        {
            string avcodecPath = Path.Combine(clientDir, "avcodec-x64-58.dll");
            File.WriteAllBytes(avcodecPath, new byte[2 * 1024 * 1024]); // Genuine 2MB DLL
            File.WriteAllBytes(Path.Combine(stagedDir, "avcodec-x64-58.dll"), new byte[29696]); // Shim

            var handler = new NativeManageHandler();

            // 1. Deploy
            var deployArgs = JsonElement.Parse($"{{\"action\": \"video_deploy\", \"repoRoot\": {JsonSerializer.Serialize(tempDir)}, \"skipProcessCheck\": true}}");
            var deployResult = await handler.HandleAsync(deployArgs);
            Assert.False(deployResult.IsError);

            string deployJson = GetContentText(deployResult);
            var deployDoc = JsonElement.Parse(deployJson);
            Assert.True(deployDoc.GetProperty("success").GetBoolean());
            Assert.Equal("video_deploy", deployDoc.GetProperty("action").GetString());

            // 2. Rollback
            var rollbackArgs = JsonElement.Parse($"{{\"action\": \"video_rollback\", \"repoRoot\": {JsonSerializer.Serialize(tempDir)}, \"skipProcessCheck\": true}}");
            var rollbackResult = await handler.HandleAsync(rollbackArgs);
            Assert.False(rollbackResult.IsError);

            string rollbackJson = GetContentText(rollbackResult);
            var rollbackDoc = JsonElement.Parse(rollbackJson);
            Assert.True(rollbackDoc.GetProperty("success").GetBoolean());
            Assert.Equal("video_rollback", rollbackDoc.GetProperty("action").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
