// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Xunit;

namespace Kongor.Cli.Tests;

/// <summary>
/// Value-Weighted Testing Gate for Phase 0 Location-Independent CLI Hardening:
/// - protects: Global --root/--workspace extraction, '?' URL non-hijack, fuzzy command suggestions,
///   KongorProcessOwnershipFilter sparing non-Kongor dotnet &amp; active MCP PIDs, Host-RID filtered CLI slot staging,
///   PATH prepending &amp; shadowing audit, and sensitive argument redaction in cli.ndjson telemetry.
/// - fails_when: URLs containing '?' route to AI search, unrelated dotnet processes are killed by 'kongor stop',
///   foreign OS runtimes (linux-x64/osx-arm64) leak into ~/.kongor/cli slots, or PATH append loses to ~/.dotnet/tools.
/// - seam: Real CliRuntimeContext, KongorProcessOwnershipFilter, CliSelfInstaller, and CliTelemetryLogger.
/// </summary>
public sealed class Phase0CliHardeningTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase0CliHardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kongor_cli_phase0_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void CliRuntimeContext_ExtractsGlobalRootFlags_WithoutCorruptingPassthroughArgs()
    {
        var (rem1, root1) = CliRuntimeContext.ExtractGlobalOptions(["--root", @"D:\Worktrees\PKV", "play", "--windowed"]);
        root1.Should().Be(@"D:\Worktrees\PKV");
        rem1.Should().Equal("play", "--windowed");

        var (rem2, root2) = CliRuntimeContext.ExtractGlobalOptions(["status", "--workspace=main"]);
        root2.Should().Be("main");
        rem2.Should().Equal("status");

        // Args after '--' must NOT be stripped as global options
        var (rem3, root3) = CliRuntimeContext.ExtractGlobalOptions(["play", "--", "--root", "passthrough-value"]);
        root3.Should().BeNull();
        rem3.Should().Equal("play", "--", "--root", "passthrough-value");
    }

    [Fact]
    public void CliRuntimeContext_AskRouting_NeverHijacksUrlsContainingQuestionMark()
    {
        // URL query string in a subcommand must NOT be hijacked into AskCommand!
        CliRuntimeContext.TryExtractAskQuestion(["doctor", "--url", "http://127.0.0.1:5200/api?status=ok"], out _).Should().BeFalse();
        CliRuntimeContext.TryExtractAskQuestion(["play", "--connect", "https://master.local/login?v=5"], out _).Should().BeFalse();
        CliRuntimeContext.TryExtractAskQuestion(["ask", "--help"], out _).Should().BeFalse();

        // Explicit leading ask/? triggers MUST still route cleanly
        CliRuntimeContext.TryExtractAskQuestion(["ask", "how", "does", "vfs", "work?"], out string[] q1).Should().BeTrue();
        q1.Should().Equal("how", "does", "vfs", "work?");

        CliRuntimeContext.TryExtractAskQuestion(["?", "where", "is", "compel"], out string[] q2).Should().BeTrue();
        q2.Should().Equal("where", "is", "compel");

        CliRuntimeContext.TryExtractAskQuestion(["?status"], out string[] q3).Should().BeTrue();
        q3.Should().Equal("status");
    }

    [Fact]
    public void CliRuntimeContext_SuggestClosestCommand_FindsTypoMatches()
    {
        string[] known = ["play", "status", "doctor", "workspace", "clean", "stop"];
        CliRuntimeContext.SuggestClosestCommand("paly", known).Should().Be("play");
        CliRuntimeContext.SuggestClosestCommand("stauts", known).Should().Be("status");
        CliRuntimeContext.SuggestClosestCommand("doctr", known).Should().Be("doctor");
        CliRuntimeContext.SuggestClosestCommand("zzzzzzzz", known).Should().BeNull();
    }

    [Fact]
    public void ProcessOwnershipFilter_SparesUnrelatedDotnet_ActiveMcp_OtherWorkspaces_AndAccessDenied()
    {
        string activeWs = @"C:\Repos\ProjectKongor";
        string otherWs = @"D:\Worktrees\ProjectKongor-Alt";

        string fakeCliHome = Path.Combine(_tempRoot, "synthetic-home", ".kongor");
        ProcessSnapshot[] snapshots =
        [
            new(100, "kongor", Path.Combine(fakeCliHome, "cli", "slot1", "kongor.exe"), "kongor stop"), // Current PID
            new(101, "kongor", Path.Combine(fakeCliHome, "bin", "kongor.exe"), "kongor stop"), // Parent shim PID
            new(200, "dotnet", @"C:\Program Files\dotnet\dotnet.exe", "dotnet MyOtherCompanyApi.dll --urls http://localhost:9000"), // Unrelated dotnet -> SPARE
            new(201, "dotnet", @"C:\Program Files\dotnet\dotnet.exe", @"dotnet C:\Repos\ProjectKongor\src\services\master-server\bin\Release\net11.0\KONGOR.MasterServer.dll"), // Active Kongor -> KILL
            new(202, "WILLOWMAKER", @"C:\Repos\ProjectKongor\src\apps\willowmaker\bin\Release\net11.0\WILLOWMAKER.exe", "WILLOWMAKER.exe"), // Active Kongor app -> KILL
            new(203, "WILLOWMAKER", @"D:\Worktrees\ProjectKongor-Alt\src\apps\willowmaker\bin\Release\net11.0\WILLOWMAKER.exe", "WILLOWMAKER.exe"), // Other workspace -> SPARE unless --global
            new(204, "kongor", Path.Combine(fakeCliHome, "cli", "slot1", "kongor.exe"), "kongor mcp"), // Active MCP -> SPARE unless --include-mcp
            new(205, "python", @"C:\Python\python.exe", @"python -u C:\Repos\ProjectKongor\src\tools\tao_mcp.py"), // Active Python MCP -> SPARE
            new(206, "SystemSvc", null, null, AccessDenied: true), // Elevated process -> SPARE + LOG
            new(207, "powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell.exe -NoLogo") // User's interactive terminal -> SPARE
        ];

        // Default workspace-scoped stop
        StopFilterDecision scoped = KongorProcessOwnershipFilter.SelectTargets(snapshots, new StopProcessFilterOptions
        {
            CurrentProcessId = 100,
            ParentShimProcessId = 101,
            ActiveWorkspaceRoot = activeWs,
            RegisteredWorkspaceRoots = [activeWs, otherWs],
            GlobalScope = false,
            IncludeMcp = false
        });

        scoped.Targets.Select(t => t.Process.ProcessId).Should().BeEquivalentTo([201, 202]);
        scoped.Skipped.Should().Contain(s => s.Process.ProcessId == 200 && s.Reason.Contains("unrelated dotnet"));
        scoped.Skipped.Should().Contain(s => s.Process.ProcessId == 203 && s.Reason.Contains("another Kongor workspace"));
        scoped.Skipped.Should().Contain(s => s.Process.ProcessId == 204 && s.Reason.Contains("MCP"));
        scoped.Skipped.Should().Contain(s => s.Process.ProcessId == 206 && s.Reason.Contains("access-denied"));

        // --global --include-mcp stop still spares unrelated dotnet PID 200 and user shell PID 207!
        StopFilterDecision global = KongorProcessOwnershipFilter.SelectTargets(snapshots, new StopProcessFilterOptions
        {
            CurrentProcessId = 100,
            ParentShimProcessId = 101,
            ActiveWorkspaceRoot = activeWs,
            RegisteredWorkspaceRoots = [activeWs, otherWs],
            GlobalScope = true,
            IncludeMcp = true
        });

        global.Targets.Select(t => t.Process.ProcessId).Should().Contain([201, 202, 203]);
        global.Targets.Select(t => t.Process.ProcessId).Should().NotContain([100, 101, 200, 206, 207]);
    }

    [Fact]
    public void CliSelfInstaller_FiltersForeignRids_PrependsPath_AndPrunesOldSlots()
    {
        string fakeHome = Path.Combine(_tempRoot, "home");
        string fakeRepo = Path.Combine(_tempRoot, "repo");
        string cliBin = Path.Combine(fakeRepo, "src", "tools", "kongor-cli", "bin", "Release", "net11.0");
        string shimBin = Path.Combine(fakeRepo, "src", "tools", "kongor-shim", "bin", "Release", "net11.0");

        Directory.CreateDirectory(cliBin);
        Directory.CreateDirectory(shimBin);
        File.WriteAllText(Path.Combine(fakeRepo, "ProjectKongor.slnx"), "<Solution />");

        // Seed host files + massive fake cross-OS bloat
        File.WriteAllText(Path.Combine(cliBin, "kongor.exe"), "cli-exe");
        File.WriteAllText(Path.Combine(cliBin, "kongor.dll"), "cli-dll");
        File.WriteAllText(Path.Combine(cliBin, "kongor.xml"), "xml-doc-should-skip");
        Directory.CreateDirectory(Path.Combine(cliBin, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(cliBin, "runtimes", "win-x64", "native", "duckdb.dll"), "win-native");
        Directory.CreateDirectory(Path.Combine(cliBin, "runtimes", "linux-x64", "native"));
        File.WriteAllText(Path.Combine(cliBin, "runtimes", "linux-x64", "native", "libduckdb.so"), "linux-bloat");
        Directory.CreateDirectory(Path.Combine(cliBin, "osx-arm64"));
        File.WriteAllText(Path.Combine(cliBin, "osx-arm64", "kongor"), "osx-bloat");
        Directory.CreateDirectory(Path.Combine(cliBin, "publish"));
        File.WriteAllText(Path.Combine(cliBin, "publish", "dup.dll"), "publish-bloat");

        File.WriteAllText(Path.Combine(shimBin, "kongor.exe"), "shim-exe");
        File.WriteAllText(Path.Combine(shimBin, "kongor.dll"), "shim-dll");

        // Seed legacy conflicting ~/.dotnet/tools/kongor.exe
        string legacyDotnetTools = Path.Combine(fakeHome, ".dotnet", "tools");
        Directory.CreateDirectory(legacyDotnetTools);
        File.WriteAllText(Path.Combine(legacyDotnetTools, "kongor.exe"), "legacy-global-tool");

        CliInstallResult res = CliSelfInstaller.InstallFromSourceDirectory(
            cliBinSourceDir: cliBin,
            shimBinSourceDir: shimBin,
            workspaceRoot: fakeRepo,
            workspaceKind: KongorWorkspaceKind.SourceWorkspace,
            versionStamp: "5.0.2",
            userHomeOverride: fakeHome,
            isWindowsOverride: true,
            updateUserPath: false,
            cleanLegacyDotnetTool: true);

        res.Success.Should().BeTrue(res.ErrorMessage);
        File.Exists(Path.Combine(res.ActiveSlotPath, "kongor.exe")).Should().BeTrue();
        File.Exists(Path.Combine(res.ActiveSlotPath, "runtimes", "win-x64", "native", "duckdb.dll")).Should().BeTrue();
        Directory.Exists(Path.Combine(res.ActiveSlotPath, "runtimes", "linux-x64")).Should().BeFalse("foreign linux-x64 runtime must be excluded");
        Directory.Exists(Path.Combine(res.ActiveSlotPath, "osx-arm64")).Should().BeFalse("nested osx-arm64 publish dir must be excluded");
        Directory.Exists(Path.Combine(res.ActiveSlotPath, "publish")).Should().BeFalse("nested publish dir must be excluded");
        File.Exists(Path.Combine(res.ActiveSlotPath, "kongor.xml")).Should().BeFalse("XML doc comments should be stripped from runtime slot");
        File.Exists(res.ShimPath).Should().BeTrue();
        File.Exists(Path.Combine(legacyDotnetTools, "kongor.exe")).Should().BeFalse("legacy dotnet tool shim should be cleaned in sandbox");

        // Verify PrependPathEntry puts ~/.kongor/bin BEFORE ~/.dotnet/tools without duplicates
        string updatedPath = CliSelfInstaller.PrependPathEntry(
            $@"C:\Windows\System32;{legacyDotnetTools};{res.BinDir}",
            res.BinDir,
            isWindows: true);
        updatedPath.Split(';').First().Should().Be(res.BinDir);
        updatedPath.Split(';').Count(p => p.Equals(res.BinDir, StringComparison.OrdinalIgnoreCase)).Should().Be(1);

        // Verify AuditPathShadowing detects clean precedence
        PathShadowAuditResult audit = CliSelfInstaller.AuditPathShadowing(updatedPath, fakeHome, isWindowsOverride: true);
        audit.IsExpectedFirst.Should().BeTrue();
        audit.HasShadowingConflict.Should().BeFalse();

        // Verify workspace auto-registered in ~/.kongor/config.json
        KongorUserConfig cfg = KongorUserConfigStore.Load(fakeHome);
        cfg.ActiveWorkspace.Should().NotBeNullOrWhiteSpace();
        cfg.ActiveCliSlot.Should().Be(res.ActiveSlotPath);
    }

    [Fact]
    public void CliTelemetryLogger_RedactsSecretsAndAppendsValidNdjson()
    {
        string fakeHome = Path.Combine(_tempRoot, "telemetry-home");
        string[] sensitiveArgs = ["secret", "set", "Discord:ClientSecret", "super-secret-value-123"];
        string[] redacted = CliTelemetryLogger.RedactSensitiveArgs(sensitiveArgs);
        redacted.Should().Equal("secret", "set", "Discord:ClientSecret", "***REDACTED***");

        string[] flagArgs = ["client", "launch", "--username", "tester", "--password", "hunter2", "--token=abc123"];
        CliTelemetryLogger.RedactSensitiveArgs(flagArgs)
            .Should().Equal("client", "launch", "--username", "tester", "--password", "***REDACTED***", "--token=***REDACTED***");

        CliTelemetryLogger.RecordInvocation(
            rawArgs: flagArgs,
            invocationCwd: _tempRoot,
            workspace: null,
            durationMs: 42,
            exitCode: 0,
            userHomeOverride: fakeHome);

        string logPath = Path.Combine(fakeHome, ".kongor", "logs", "cli.ndjson");
        File.Exists(logPath).Should().BeTrue();
        string line = File.ReadAllLines(logPath).Single();
        line.Should().NotContain("hunter2").And.NotContain("abc123");
        using JsonDocument doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("command").GetString().Should().Be("client");
        doc.RootElement.GetProperty("exitCode").GetInt32().Should().Be(0);
    }
}
