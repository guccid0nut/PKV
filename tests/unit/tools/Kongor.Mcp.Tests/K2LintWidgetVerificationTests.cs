// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: K2LintWidgetVerificationTests.cs
// System: Kongor.Mcp.Tests - K2 Lint Widget Reference & Invariant Verification
// ============================================================================

using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class K2LintWidgetVerificationTests : IDisposable
{
    private readonly string _testDir;
    private readonly K2LintService _linter;

    public K2LintWidgetVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "K2LintTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _linter = new K2LintService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); }
            catch { /* cleanup */ }
        }
    }

    [Fact]
    public async Task K2Lint_DetectsMistypedWidgetName_AndSuggestsCanonicalWidget()
    {
        // Arrange
        string testFile = Path.Combine(_testDir, "test_widget_typo.lua");
        string code = "local btn = UIManager.GetInterface('main'):GetWidget('practice_button'); btn:DoEvent()";
        await File.WriteAllTextAsync(testFile, code);

        // Act
        var report = await _linter.LintAsync(testFile);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "K2_UNKNOWN_WIDGET");
        var issue = report.Issues.First(i => i.Code == "K2_UNKNOWN_WIDGET");
        issue.RemediationHint.Should().Contain("sysbar_quickStart");
    }

    [Fact]
    public async Task K2Lint_DetectsCaseMismatchInWidgetName_AndSuggestsCorrectCasing()
    {
        // Arrange
        string testFile = Path.Combine(_testDir, "test_casing_typo.lua");
        string code = "local w = GetWidget('sysbar_quickstart'); if w then w:DoEvent() end";
        await File.WriteAllTextAsync(testFile, code);

        // Act
        var report = await _linter.LintAsync(testFile);

        // Assert
        report.Issues.Should().Contain(i => i.Code == "K2_UNKNOWN_WIDGET");
        var issue = report.Issues.First(i => i.Code == "K2_UNKNOWN_WIDGET");
        issue.RemediationHint.Should().Contain("sysbar_quickStart");
    }

    [Fact]
    public async Task K2Lint_CleanTypedScript_PassesWithZeroErrors()
    {
        // Arrange
        string testFile = Path.Combine(_testDir, "test_clean.lua");
        string code = @"
local iface = UIManager.GetInterface('main')
if iface then
    local btn = iface:GetWidget('sysbar_quickStart')
    if btn and btn.DoEvent then
        btn:DoEvent()
    end
end
";
        await File.WriteAllTextAsync(testFile, code);

        // Act
        var report = await _linter.LintAsync(testFile);

        // Assert
        report.ErrorCount.Should().Be(0);
        report.WarningCount.Should().Be(0);
    }
}
