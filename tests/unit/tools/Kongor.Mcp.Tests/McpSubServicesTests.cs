// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public class McpSubServicesTests
{
    [Fact]
    public void ClientProcessService_ResolvesExecutable_WithSupervisor()
    {
        var supervisor = new ClientProcessSupervisor();
        var service = new ClientProcessService(supervisor);

        // Service is instantiated and wired
        service.Should().NotBeNull();
        bool isRunningZero = service.IsRunning(0);
        isRunningZero.Should().BeFalse();
    }

    [Fact]
    public void GameClientManageHandler_Exposes_ClientProcessService()
    {
        var handler = new GameClientManageHandler();
        handler.ProcessService.Should().NotBeNull();
        handler.ProcessService.Should().BeAssignableTo<IClientProcessService>();
    }

    [Fact]
    public void LuaExecService_EscapeLuaString_EscapesSpecialCharactersCorrectly()
    {
        var service = new LuaExecService();

        string raw = "Hello \"World\"\nLine 2\\Path";
        string escaped = service.EscapeLuaString(raw);

        escaped.Should().Be("Hello \\\"World\\\"\\nLine 2\\\\Path");
    }

    [Fact]
    public void LuaExecService_TryDetectSyntaxError_IdentifiesSyntaxErrors()
    {
        var service = new LuaExecService();

        string errorOutput = "Lua error: [string \"foo\"]:1: attempt to index a nil value (global 'missing_tbl')";
        bool detected = service.TryDetectSyntaxError(errorOutput, out string category, out string hint);

        detected.Should().BeTrue();
        category.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GameLuaHandler_Exposes_LuaExecService()
    {
        var handler = new GameLuaHandler();
        handler.LuaExecService.Should().NotBeNull();
        handler.LuaExecService.Should().BeAssignableTo<ILuaExecService>();
    }

    [Fact]
    public void NativeHookService_GetHookedSubsystems_ReturnsExpectedSubsystems()
    {
        var service = new NativeHookService();

        var subsystems = service.GetHookedSubsystems();
        subsystems.Should().NotBeEmpty();
        subsystems.Should().Contain(s => s.Contains("HeapAlloc", StringComparison.OrdinalIgnoreCase));
        subsystems.Should().Contain(s => s.Contains("WSASend", StringComparison.OrdinalIgnoreCase));
        subsystems.Should().Contain(s => s.Contains("STATUS_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeHookService_VerifyHookIntegrity_NonExistentFile_ReturnsFalse()
    {
        var service = new NativeHookService();

        bool valid = service.VerifyHookIntegrity("non_existent_dll_12345.dll");
        valid.Should().BeFalse();
    }

    [Fact]
    public void NativeHookService_GetHookDiagnostics_ReportsSubsystems()
    {
        var service = new NativeHookService();
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var diag = service.GetHookDiagnostics(tempDir);
            diag.Should().NotBeNull();
            diag.Subsystems.Should().HaveCount(4);
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
    public void NativeManageHandler_Exposes_NativeHookService()
    {
        var handler = new NativeManageHandler();
        handler.NativeHookService.Should().NotBeNull();
        handler.NativeHookService.Should().BeAssignableTo<INativeHookService>();
    }
}
