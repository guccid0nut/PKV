// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AutomatedCrashSymbolizerTests.cs
// System: COMPEL Unit Tests - Strategic Recommendation SR-03
// Subsystem: Automated Minidump Symbolization & Ghidra MCP Triangulation
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Configuration;
using COMPEL.Endpoints;
using COMPEL.Services.Diagnostics;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Kongor.Core.Services.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class AutomatedCrashSymbolizerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _docsDir;
    private readonly string _crashesDir;
    private readonly string _issuesFilePath;

    public AutomatedCrashSymbolizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Kongor_CrashSymbolizerTests_" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        _crashesDir = Path.Combine(_tempDir, "logs", "crashes");

        Directory.CreateDirectory(_docsDir);
        Directory.CreateDirectory(_crashesDir);

        _issuesFilePath = Path.Combine(_docsDir, "ISSUES.md");
        File.WriteAllText(_issuesFilePath, """
# Living Engine & Cluster Issues Backlog

- **Total Tracked**: 254
- **Resolved**: 254
- **Investigating**: 0

## Active Issues

*None. All 254 tracked issues are resolved.*

## Resolved Issues Backlog
### [ISSUE-254] Compel Worker Crash Pacing
""");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task SymbolizeAndRecordCrashAsync_GeneratesReport_AndAppendsToIssuesLog()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableAutomatedCrashSymbolization = true,
            AutoAppendToIssuesLog = true,
            CrashReportsDirectory = Path.Combine(_tempDir, "logs", "crashes")
        });

        var service = new AutomatedCrashSymbolizerService(
            options: options,
            logger: NullLogger<AutomatedCrashSymbolizerService>.Instance,
            repoRoot: _tempDir);

        var worker = new DedicatedServerWorker(1, 11235, 11435, 11235, 11435, "KONGOR Dedicated #1");
        List<DedicatedServerLogLine> logs =
        [
            new(TimeProvider.System.GetUtcNow(), "[DedicatedServer:11235] Initializing engine Host_Init...", false, "server"),
            new(TimeProvider.System.GetUtcNow(), "Access violation 0xC0000005 at k2_x64.dll+0x3A20", true, "server")
        ];

        // Act
        var result = await service.SymbolizeAndRecordCrashAsync(
            worker,
            exitCode: unchecked((int)0xC0000005),
            recentLogs: logs,
            explicitDumpPath: null,
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Symbolized.Should().BeTrue();
        result.IssueId.Should().Be(255);
        result.FaultingModule.Should().Be("k2_x64.dll");
        result.FaultingRva.Should().Be("0x3A20");
        result.ExceptionName.Should().Be("STATUS_ACCESS_VIOLATION");
        result.AppendedToIssuesLog.Should().BeTrue();
        result.ElapsedMs.Should().BeLessThan(3000);

        // Verify report file on disk
        File.Exists(result.ReportFilePath).Should().BeTrue();
        string reportContent = await File.ReadAllTextAsync(result.ReportFilePath);
        reportContent.Should().Contain("[ISSUE-255]");
        reportContent.Should().Contain("k2_x64.dll+0x3A20");
        reportContent.Should().Contain("STATUS_ACCESS_VIOLATION");
        reportContent.Should().Contain("Ghidra Triangulation");

        // Verify docs/ISSUES.md updated
        string issuesContent = await File.ReadAllTextAsync(_issuesFilePath);
        issuesContent.Should().Contain("### [ISSUE-255] Dedicated Server Crash (Port 11235): STATUS_ACCESS_VIOLATION in k2_x64.dll+0x3A20");
        issuesContent.Should().Contain("- **Total Tracked**: 255");
        issuesContent.Should().Contain("- **Investigating**: 1");
    }

    [Fact]
    public async Task SymbolizeAndRecordCrashAsync_DoesNotAppend_WhenAutoAppendIsDisabled()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableAutomatedCrashSymbolization = true,
            AutoAppendToIssuesLog = false,
            CrashReportsDirectory = Path.Combine(_tempDir, "logs", "crashes")
        });

        var service = new AutomatedCrashSymbolizerService(
            options: options,
            logger: NullLogger<AutomatedCrashSymbolizerService>.Instance,
            repoRoot: _tempDir);

        var worker = new DedicatedServerWorker(2, 11236, 11436, 11236, 11436, "KONGOR Dedicated #2");
        List<DedicatedServerLogLine> logs =
        [
            new(TimeProvider.System.GetUtcNow(), "Crash at cgame_x64.dll+0x1e5de0", true, "server")
        ];

        // Act
        var result = await service.SymbolizeAndRecordCrashAsync(
            worker,
            exitCode: unchecked((int)0xC0000005),
            recentLogs: logs,
            explicitDumpPath: null,
            cancellationToken: CancellationToken.None);

        // Assert
        result.Symbolized.Should().BeTrue();
        result.AppendedToIssuesLog.Should().BeFalse();
        File.Exists(result.ReportFilePath).Should().BeTrue();

        string issuesContent = await File.ReadAllTextAsync(_issuesFilePath);
        issuesContent.Should().NotContain("[ISSUE-255]");
    }

    [Fact]
    public async Task SymbolizeAndRecordCrashAsync_GracefullyHandlesUnknownCrash_WithoutCrashingSupervisor()
    {
        // Arrange
        var options = Options.Create(new MatchServerManagerOptions
        {
            EnableAutomatedCrashSymbolization = true,
            AutoAppendToIssuesLog = false,
            CrashReportsDirectory = Path.Combine(_tempDir, "logs", "crashes")
        });

        var service = new AutomatedCrashSymbolizerService(
            options: options,
            logger: NullLogger<AutomatedCrashSymbolizerService>.Instance,
            repoRoot: _tempDir);

        var worker = new DedicatedServerWorker(3, 11237, 11437, 11237, 11437, "KONGOR Dedicated #3");

        // Act - empty logs, arbitrary exit code
        var result = await service.SymbolizeAndRecordCrashAsync(
            worker,
            exitCode: 137,
            recentLogs: Array.Empty<DedicatedServerLogLine>(),
            explicitDumpPath: null,
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Symbolized.Should().BeTrue();
        result.ElapsedMs.Should().BeLessThan(5000);
        File.Exists(result.ReportFilePath).Should().BeTrue();
    }

    [Fact]
    public void SymbolCorrelatorService_ResolvesKnownEngineOffsets_ToSourceCode()
    {
        // Arrange
        string enginePatchPath = Path.Combine(_tempDir, "src", "extensions", "modding", "Services");
        Directory.CreateDirectory(enginePatchPath);
        File.WriteAllText(Path.Combine(enginePatchPath, "EnginePatchService.cs"), "// stub");

        string cgamePath = Path.Combine(_tempDir, "games", "shared", "types");
        Directory.CreateDirectory(cgamePath);
        File.WriteAllText(Path.Combine(cgamePath, "k2_api.lua"), "-- stub");

        var correlator = new SymbolCorrelatorService(_tempDir);

        // Act
        var k2Result = correlator.Correlate("k2_x64.dll", null, "0x3A20");
        var cgameResult = correlator.Correlate("cgame_x64.dll", null, "0x1e5de0");

        // Assert
        k2Result.Should().NotBeNull();
        k2Result.CandidateFiles.Should().NotBeEmpty();
        k2Result.PrimarySourceFile.Should().NotBeNull();
        k2Result.ClickableLink.Should().Contain("EnginePatchService.cs");

        cgameResult.Should().NotBeNull();
        cgameResult.PrimarySourceFile.Should().NotBeNull();
    }
}
