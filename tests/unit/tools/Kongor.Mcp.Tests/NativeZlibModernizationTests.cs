// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NativeZlibModernizationTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task DoctorCommand_ProbesNativeZlib_ReportsHealthyWhenModern()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        var doctor = new DoctorCommand();
        var settings = new DoctorCommandSettings
        {
            Subsystem = "Native",
            Json = true
        };

        int exitCode = await doctor.ExecuteAsync(null!, settings);
        Assert.Equal(0, exitCode);
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
    public async Task VfsResolveHandler_VerifyZlibAction_ReturnsStructuredSuccess()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        var handler = new VfsResolveHandler();
        var arguments = JsonElement.Parse("{\"action\": \"verify_zlib\"}");

        var result = await handler.HandleAsync(arguments);

        Assert.False(result.IsError, $"Handler returned error: {GetContentText(result)}");

        string jsonText = GetContentText(result);
        var root = JsonElement.Parse(jsonText);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("verify_zlib", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("zlibDiscovered").GetBoolean());

        var verification = root.GetProperty("verification");
        Assert.True(verification.GetProperty("success").GetBoolean());
        Assert.Equal("1.3.1", verification.GetProperty("version").GetString());
        Assert.True(verification.GetProperty("crc32Passed").GetBoolean());
        Assert.True(verification.GetProperty("roundTripPassed").GetBoolean());
        Assert.Contains("SECURE", verification.GetProperty("securityStatus").GetString());
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void EnginePatchService_GetEnginePatchStatus_IncludesNativeLibraries()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string root = FindRepoRoot();
        string clientDir = Path.Combine(root, "games", "client");

        if (!Directory.Exists(clientDir)) return;

        EnginePatchStatusReport report = EnginePatchService.GetEnginePatchStatus(clientDir);

        Assert.NotNull(report.NativeLibraries);
        Assert.NotEmpty(report.NativeLibraries);

        Assert.Contains(report.NativeLibraries, n => n.RelativePath.Equals("zlibwapi_x64.dll", StringComparison.OrdinalIgnoreCase) && n.Exists && n.IsAbiVerified);
        Assert.All(report.NativeLibraries.Where(n => n.Exists), n =>
        {
            Assert.StartsWith("1.3", n.FileVersion);
            Assert.True(n.IsAbiVerified);
            Assert.Contains("SECURE", n.SecurityStatus);
        });
    }

    [Fact]
    public void NativeBinaryManager_UpgradeWithRuntimeGate_RevertsOnBrokenCandidate()
    {
        string? realCandidate = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        if (realCandidate is null) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_RuntimeGate_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client");
        Directory.CreateDirectory(clientDir);

        try
        {
            string targetDll = Path.Combine(clientDir, "zlibwapi_x64.dll");
            File.Copy(realCandidate, targetDll);

            // Create invalid candidate DLL (dummy text)
            string brokenCandidate = Path.Combine(tempDir, "broken_zlib.dll");
            File.WriteAllBytes(brokenCandidate, "CORRUPTED_DLL_HEADER_NOT_A_PE"u8.ToArray());

            // Attempt upgrade with runtime check enabled (and export check bypassed to test runtime gate specifically)
            var ex = Assert.Throws<InvalidOperationException>(() =>
                NativeBinaryManager.UpgradeZlib(tempDir, brokenCandidate, skipProcessCheck: true, skipExportCheck: true, skipRuntimeCheck: false));

            Assert.Contains("runtime C-ABI verification", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Verify original target DLL was NOT corrupted
            Assert.Equal(new FileInfo(realCandidate).Length, new FileInfo(targetDll).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
