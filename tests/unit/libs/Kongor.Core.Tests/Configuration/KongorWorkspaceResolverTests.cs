// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Configuration;
using Xunit;

namespace Kongor.Core.Tests.Configuration;

/// <summary>
/// Value-Weighted Testing Gate for Location-Independent Workspace Resolution &amp; Config Persistence:
/// - protects: 6-tier precedence chain, unrelated .git/C# repo non-latching, ephemeral runtime exclusion,
///   cross-process config.lock concurrency safety (zero lost updates), actionable error diagnostics, and
///   zero Console.Out pollution (MCP stdio purity).
/// - fails_when: Resolver silently returns CWD, latches onto an unrelated .git directory, drops concurrent
///   config.json updates, or writes diagnostic output to stdout.
/// - seam: Real filesystem temp directories + KongorWorkspaceResolver &amp; KongorUserConfigStore.
/// </summary>
public sealed class KongorWorkspaceResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fakeHome;

    public KongorWorkspaceResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "KongorWsResolverTest_" + Guid.NewGuid().ToString("N"));
        _fakeHome = Path.Combine(_tempRoot, "home");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_fakeHome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { }
        }
    }

    private string CreateSourceWorkspace(string name)
    {
        string dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(dir, "src", "tools", "kongor-cli"));
        File.WriteAllText(Path.Combine(dir, "ProjectKongor.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "src", "tools", "kongor-cli", "Kongor.Cli.csproj"), "<Project />");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    private string CreateBundledInstall(string name)
    {
        string dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(dir, "games", "client"));
        File.WriteAllText(Path.Combine(dir, BundledLayoutResolver.PolicyFileName), "{\"isPlaytest\":true}");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    private string CreateUnrelatedGitAndCSharpRepo(string name)
    {
        string dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "sub", "nested"));
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    [Fact]
    public void TryResolve_RespectsStrictSixTierPrecedence_AndSupportsMultiWorktreeAutoSwitch()
    {
        string savedSource = CreateSourceWorkspace("main-repo");
        string worktreeB = CreateSourceWorkspace("worktree-b");
        string envBundle = CreateBundledInstall("env-bundle");
        string flagSource = CreateSourceWorkspace("flag-repo");
        string outsideDir = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outsideDir);

        KongorUserConfigStore.RegisterOrUpdateWorkspace(savedSource, KongorWorkspaceKind.SourceWorkspace, alias: "main", setActive: true, userHomeOverride: _fakeHome);
        KongorUserConfigStore.RegisterOrUpdateWorkspace(flagSource, KongorWorkspaceKind.SourceWorkspace, alias: "flag-alias", setActive: false, userHomeOverride: _fakeHome);

        // Tier 5: Outside any repo -> resolves ~/.kongor/config.json active workspace
        bool r5 = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws5, new WorkspaceResolveOptions
        {
            InvocationCwd = outsideDir,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "v5.0.2-slot"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = _ => null
        });
        r5.Should().BeTrue();
        ws5!.Root.Should().Be(savedSource);
        ws5.Source.Should().Be(WorkspaceResolutionSource.UserConfigActive);
        ws5.IsOutOfTree.Should().BeTrue();

        // Tier 3: Running inside worktree-b -> auto-switches to worktree-b over saved config!
        string worktreeSubDir = Path.Combine(worktreeB, "src", "tools");
        bool r3 = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws3, new WorkspaceResolveOptions
        {
            InvocationCwd = worktreeSubDir,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "v5.0.2-slot"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = _ => null
        });
        r3.Should().BeTrue();
        ws3!.Root.Should().Be(worktreeB);
        ws3.Source.Should().Be(WorkspaceResolutionSource.CwdAncestor);
        ws3.IsOutOfTree.Should().BeFalse();

        // Tier 2: KONGOR_ROOT beats KONGOR_BUNDLE_ROOT, KONGOR_REPO_ROOT, and CWD
        bool r2 = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws2, new WorkspaceResolveOptions
        {
            InvocationCwd = worktreeSubDir,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "v5.0.2-slot"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = key => key switch
            {
                KongorWorkspaceResolver.EnvKongorRoot => envBundle,
                KongorWorkspaceResolver.EnvRepoRoot => savedSource,
                _ => null
            }
        });
        r2.Should().BeTrue();
        ws2!.Root.Should().Be(envBundle);
        ws2.Kind.Should().Be(KongorWorkspaceKind.BundledInstall);
        ws2.Source.Should().Be(WorkspaceResolutionSource.EnvKongorRoot);
        ws2.BundledLayout.Should().NotBeNull();

        // Tier 1: --workspace alias beats KONGOR_ROOT env var and CWD
        bool r1 = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws1, new WorkspaceResolveOptions
        {
            ExplicitRootOrWorkspace = "flag-alias",
            InvocationCwd = worktreeSubDir,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "v5.0.2-slot"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = key => key == KongorWorkspaceResolver.EnvKongorRoot ? envBundle : null
        });
        r1.Should().BeTrue();
        ws1!.Root.Should().Be(flagSource);
        ws1.Source.Should().Be(WorkspaceResolutionSource.CliFlag);
        ws1.RegisteredAlias.Should().Be("flag-alias");
    }

    [Fact]
    public void TryResolve_InsideUnrelatedGitAndDotNetRepo_NeverLatchesOntoWrongRepo_FallsThroughToUserConfig()
    {
        string realKongor = CreateSourceWorkspace("ProjectKongor");
        string unrelatedRepo = CreateUnrelatedGitAndCSharpRepo("SomeOtherApp");
        string unrelatedNested = Path.Combine(unrelatedRepo, "sub", "nested");

        KongorUserConfigStore.RegisterOrUpdateWorkspace(realKongor, KongorWorkspaceKind.SourceWorkspace, setActive: true, userHomeOverride: _fakeHome);

        bool resolved = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws, new WorkspaceResolveOptions
        {
            InvocationCwd = unrelatedNested,
            BaseDirectory = Path.Combine(_fakeHome, ".dotnet", "tools", ".store", "kongor.cli"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = _ => null
        });

        resolved.Should().BeTrue();
        ws!.Root.Should().Be(realKongor, "bare .git and bare Directory.Build.props in an unrelated repo must never trick KongorWorkspaceResolver");
        ws.Source.Should().Be(WorkspaceResolutionSource.UserConfigActive);
        ws.IsOutOfTree.Should().BeTrue();
    }

    [Fact]
    public void RequireAnyWorkspace_WhenUnconfigured_ThrowsActionableException_NeverReturnsCwd()
    {
        string emptyDir = Path.Combine(_tempRoot, "totally-empty");
        Directory.CreateDirectory(emptyDir);

        Action act = () => KongorWorkspaceResolver.RequireAnyWorkspace(new WorkspaceResolveOptions
        {
            InvocationCwd = emptyDir,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "slot1"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = _ => null
        });

        act.Should().Throw<KongorWorkspaceNotFoundException>()
            .Which.Message.Should().Contain("kongor workspace use")
            .And.Contain("--root");
    }

    [Fact]
    public void RequireSourceWorkspace_WhenResolvedBundle_ThrowsActionableSourceRequiredError()
    {
        string bundle = CreateBundledInstall("playtest-bundle");
        KongorUserConfigStore.RegisterOrUpdateWorkspace(bundle, KongorWorkspaceKind.BundledInstall, setActive: true, userHomeOverride: _fakeHome);

        Action act = () => KongorWorkspaceResolver.RequireSourceWorkspace(new WorkspaceResolveOptions
        {
            InvocationCwd = _tempRoot,
            BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "slot1"),
            UserHomeOverride = _fakeHome,
            GetEnvironmentVariable = _ => null
        });

        act.Should().Throw<KongorWorkspaceNotFoundException>()
            .Which.RequiredSourceWorkspace.Should().BeTrue();
    }

    [Fact]
    public void ResolveAndConfigPersistence_NeverPollutesConsoleOut_ProtectingMcpStdio()
    {
        string realKongor = CreateSourceWorkspace("stdio-check-repo");
        List<string> diagnostics = [];

        TextWriter originalOut = Console.Out;
        using StringWriter capturedStdout = new();
        try
        {
            Console.SetOut(capturedStdout);
            KongorUserConfigStore.RegisterOrUpdateWorkspace(
                realKongor,
                KongorWorkspaceKind.SourceWorkspace,
                userHomeOverride: _fakeHome,
                diagnosticLogger: diagnostics.Add);

            bool ok = KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws, new WorkspaceResolveOptions
            {
                InvocationCwd = _tempRoot,
                BaseDirectory = Path.Combine(_fakeHome, ".kongor", "cli", "slot1"),
                UserHomeOverride = _fakeHome,
                GetEnvironmentVariable = _ => null,
                DiagnosticLogger = diagnostics.Add
            });

            ok.Should().BeTrue();
            ws!.Root.Should().Be(realKongor);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        capturedStdout.ToString().Should().BeEmpty("Kongor.Core configuration modules must NEVER write to Console.Out");
        diagnostics.Should().NotBeEmpty("verbose diagnostics should route to injected logger");
    }

    [Fact]
    public async Task ConfigStore_ConcurrentWriters_WithFileLock_NeverLoseUpdates()
    {
        const int writerCount = 16;
        string[] workspaceDirs = Enumerable.Range(0, writerCount)
            .Select(i => CreateSourceWorkspace($"concurrent-ws-{i:D2}"))
            .ToArray();

        Task[] tasks = workspaceDirs.Select((dir, i) => Task.Run(() =>
        {
            KongorUserConfigStore.RegisterOrUpdateWorkspace(
                dir,
                KongorWorkspaceKind.SourceWorkspace,
                alias: $"ws-{i:D2}",
                setActive: i == writerCount - 1,
                userHomeOverride: _fakeHome);
        })).ToArray();

        await Task.WhenAll(tasks);

        KongorUserConfig finalConfig = KongorUserConfigStore.Load(_fakeHome);
        finalConfig.Workspaces.Should().HaveCount(writerCount, "config.lock must serialize read-modify-write so zero updates are lost");
        for (int i = 0; i < writerCount; i++)
        {
            finalConfig.Workspaces.Should().Contain(w => w.Name == $"ws-{i:D2}");
        }
    }
}
