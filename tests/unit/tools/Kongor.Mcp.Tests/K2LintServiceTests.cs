// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: K2LintServiceTests.cs
// System: Kongor.Mcp.Tests - K2 Lua & XML Static Linting Verification
// Policy: Strict Explicit Typing, Sub-Second Execution, EmmyLua Static Validation
// ============================================================================

using System.Text;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class K2LintServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly K2LintService _lintService;

    public K2LintServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Kongor_K2LintTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _lintService = new K2LintService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in temp
        }
    }

    [Fact]
    public async Task LintAsync_DetectsAndFixesUtf8Bom()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "bom_test.interface");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] content = Encoding.UTF8.GetBytes("<interface name=\"test\"><panel name=\"p1\" /></interface>");
        byte[] withBom = [.. bom, .. content];
        await File.WriteAllBytesAsync(filePath, withBom);

        // Act 1: Lint without fix
        K2LintReport reportWithoutFix = await _lintService.LintAsync(filePath, fixBom: false);

        // Assert 1
        reportWithoutFix.Issues.Should().Contain(i => i.Code == "K2_BOM_PRESENT");

        // Act 2: Lint with fix
        K2LintReport reportWithFix = await _lintService.LintAsync(filePath, fixBom: true);

        // Assert 2
        reportWithFix.FixedBomFiles.Should().Contain(filePath);
        byte[] fixedBytes = await File.ReadAllBytesAsync(filePath);
        fixedBytes[0].Should().NotBe(0xEF);
    }

    [Fact]
    public async Task LintAsync_FlagsForbiddenWindowsGlobalReference()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "forbidden_windows.lua");
        string code = "function OnClick()\n    Windows.main:GetWidget('btn'):DoEvent()\nend";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.ErrorCount.Should().BeGreaterThan(0);
        report.Issues.Should().Contain(i => i.Code == "K2_FORBIDDEN_GLOBAL" && i.Message.Contains("UIManager"));
    }

    [Fact]
    public async Task LintAsync_FlagsInvalidDoEventStringArgument()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "invalid_doevent.lua");
        string code = "local btn = UIManager.GetInterface('main'):GetWidget('btn')\nbtn:DoEvent('onclick')";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.ErrorCount.Should().BeGreaterThan(0);
        report.Issues.Should().Contain(i => i.Code == "K2_INVALID_DOEVENT_SIGNATURE");
    }

    [Fact]
    public async Task LintAsync_FlagsInvalidOperators()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "invalid_ops.lua");
        string code = "if a != b && c || d then\n    Echo('hello')\nend";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "LUA_INVALID_OPERATOR" && i.Message.Contains("~="));
        report.Issues.Should().Contain(i => i.Code == "LUA_INVALID_OPERATOR" && i.Message.Contains("and"));
        report.Issues.Should().Contain(i => i.Code == "LUA_INVALID_OPERATOR" && i.Message.Contains("or"));
    }

    [Fact]
    public async Task LintAsync_FlagsUnbalancedBlocks()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "unbalanced.lua");
        string code = "function OnClick()\n    if true then\n        Echo('missing end')\nend";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "LUA_UNBALANCED_BLOCK");
    }

    [Fact]
    public async Task LintAsync_DetectsEmmyLuaMisspelledTags()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "misspelled_tag.lua");
        string code = "---@parm heroId integer\nfunction SelectHero(heroId) end";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "EMMYLUA_UNKNOWN_TAG" && i.RemediationHint!.Contains("---@param"));
    }

    [Fact]
    public async Task LintAsync_DetectsEmmyLuaParameterSignatureMismatch()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "param_mismatch.lua");
        string code = "---@param wrongParamName integer\nfunction SelectHero(playerId)\n    Echo(playerId)\nend";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "EMMYLUA_PARAM_MISMATCH" && i.Message.Contains("wrongParamName"));
    }

    [Fact]
    public async Task LintAsync_DetectsUndefinedGlobals()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "undefined_global.lua");
        string code = "function OnTick()\n    NonExistentEngineService.Execute()\nend";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "K2_UNDEFINED_GLOBAL" && i.Message.Contains("NonExistentEngineService"));
    }

    [Fact]
    public async Task LintAsync_AuthoritativeK2ApiStubs_PassWithZeroErrors()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        // Act: Lint the authoritative games/shared/types/k2_api.lua
        K2LintReport report = await _lintService.LintAsync("games/shared/types/k2_api.lua");

        // Assert: 0 errors
        string issuesList = string.Join("; ", report.Issues.Select(i => $"{i.Severity}:{i.Code} line {i.Line}: {i.Message}"));
        report.ErrorCount.Should().Be(0, $"k2_api.lua must be valid EmmyLua stubs. Issues: {issuesList}");
        report.Status.Should().Be("success", $"Issues: {issuesList}");
    }

    [Fact]
    public async Task LintAsync_DocsK2ApiStubs_PassWithZeroErrors()
    {
        // Act: Lint the canonical docs/types/k2_api.d.lua
        K2LintReport report = await _lintService.LintAsync("docs/types/k2_api.d.lua");

        // Assert: 0 errors
        string issuesList = string.Join("; ", report.Issues.Select(i => $"{i.Severity}:{i.Code} line {i.Line}: {i.Message}"));
        report.ErrorCount.Should().Be(0, $"docs/types/k2_api.d.lua must be valid EmmyLua definitions. Issues: {issuesList}");
        report.Status.Should().Be("success", $"Issues: {issuesList}");
    }

    [Fact]
    public async Task LintAsync_FlagsFunctionArithmeticWithoutCall()
    {
        // Arrange (Issue-242 regression test: HostTime arithmetic without invocation)
        string filePath = Path.Combine(_tempDirectory, "function_arithmetic.lua");
        string code = "local endTime = (HostTime or 0) + 3200\nlocal bad2 = HostTime + 100\n";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert
        report.ErrorCount.Should().BeGreaterThan(0);
        report.Issues.Should().Contain(i => i.Code == "LUA_FUNCTION_ARITHMETIC");
    }

    [Fact]
    public async Task LintAsync_ValidFunctionCall_PassesArithmeticCheck()
    {
        // Arrange: Valid invocation of HostTime()
        string filePath = Path.Combine(_tempDirectory, "valid_arithmetic.lua");
        string code = "local endTime = HostTime() + 3200\nlocal now = (type(HostTime) == 'function' and HostTime()) or 0\n";
        await File.WriteAllTextAsync(filePath, code);

        // Act
        K2LintReport report = await _lintService.LintAsync(filePath);

        // Assert: No function arithmetic errors
        report.Issues.Should().NotContain(i => i.Code == "LUA_FUNCTION_ARITHMETIC");
    }
}
