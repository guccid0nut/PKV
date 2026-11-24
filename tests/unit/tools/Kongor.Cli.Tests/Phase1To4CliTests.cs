// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Cli.Tests;

/// <summary>
/// Value-Weighted Testing Gate for Phases 1–4 Location-Independent CLI Hardening:
/// - protects: FastAppLauncher staleness check &amp; Host-RID dual-slot shadow copy rollover,
///   CliPathResolver caller-CWD vs. workspace/VFS fallback &amp; export output routing,
///   Phase 4 kongor.ps1 command parity in Program.ConfigureCommands, KongorLayoutGateInterceptor
///   blocking source-only commands in BundledInstall mode, and static &lt;2ms shell tab-completion
///   generation + idempotent profile block management.
/// - fails_when: stale binaries launch without rebuild, foreign RIDs bloat shadow cache,
///   locked slot-a fails to roll over to slot-b, ported kongor.ps1 verbs fail to resolve,
///   BundledInstall runs source-only commands, or profile injection duplicates markers.
/// - seam: Real FastAppLauncher, CliPathResolver, CommandApp + Program.ConfigureCommands,
///   KongorLayoutGateInterceptor, and ShellCompletionGenerator.
/// </summary>
public sealed class Phase1To4CliTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase1To4CliTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kongor_cli_p14_tests_" + Guid.NewGuid().ToString("N"));
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
    public void FastAppLauncher_CheckStaleness_DetectsMissingAndNewerSourceAndIgnoresBinObj()
    {
        string repo = Path.Combine(_tempRoot, "repo");
        string projDir = Path.Combine(repo, "src", "apps", "willowmaker");
        string binDir = Path.Combine(projDir, "bin", "Release", "net11.0");
        string objDir = Path.Combine(projDir, "obj", "Release");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(objDir);

        string projFile = Path.Combine(projDir, "WILLOWMAKER.csproj");
        string sourceFile = Path.Combine(projDir, "Main.cs");
        string objFile = Path.Combine(objDir, "Generated.cs");
        string exeFile = Path.Combine(binDir, "WILLOWMAKER.exe");

        File.WriteAllText(Path.Combine(repo, "Directory.Build.props"), "<Project />");
        File.WriteAllText(projFile, "<Project />");
        File.WriteAllText(sourceFile, "// v1");

        // 1. Missing binary -> stale
        FastAppLauncher.CheckStaleness(projFile, exeFile, repo).IsStale.Should().BeTrue();

        // 2. Binary newer than sources -> up-to-date
        File.WriteAllText(exeFile, "MZ");
        DateTime baseUtc = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(repo, "Directory.Build.props"), baseUtc.AddMinutes(-5));
        File.SetLastWriteTimeUtc(projFile, baseUtc.AddMinutes(-4));
        File.SetLastWriteTimeUtc(sourceFile, baseUtc.AddMinutes(-3));
        File.SetLastWriteTimeUtc(exeFile, baseUtc);

        // Even if obj/ has a newer generated file, CheckStaleness must ignore obj/ and bin/
        File.WriteAllText(objFile, "// generated");
        File.SetLastWriteTimeUtc(objFile, baseUtc.AddMinutes(10));

        StalenessCheckResult fresh = FastAppLauncher.CheckStaleness(projFile, exeFile, repo);
        fresh.IsStale.Should().BeFalse();

        // 3. Advancing .cs timestamp past binary triggers stale detection
        File.SetLastWriteTimeUtc(sourceFile, baseUtc.AddSeconds(5));
        StalenessCheckResult stale = FastAppLauncher.CheckStaleness(projFile, exeFile, repo);
        stale.IsStale.Should().BeTrue();
        stale.Reason.Should().Contain("Main.cs");
    }

    [Fact]
    public void FastAppLauncher_SyncToShadowSlot_FiltersForeignRids_IsIncremental_AndRollsOverOnLock()
    {
        string sourceBin = Path.Combine(_tempRoot, "source-bin");
        string cacheBase = Path.Combine(_tempRoot, "cache");
        Directory.CreateDirectory(Path.Combine(sourceBin, "runtimes", "win-x64", "native"));
        Directory.CreateDirectory(Path.Combine(sourceBin, "runtimes", "win", "lib"));
        Directory.CreateDirectory(Path.Combine(sourceBin, "runtimes", "linux-x64", "native"));
        Directory.CreateDirectory(Path.Combine(sourceBin, "linux-x64"));
        Directory.CreateDirectory(Path.Combine(sourceBin, "osx-arm64"));
        Directory.CreateDirectory(Path.Combine(sourceBin, "publish"));

        string exeSrc = Path.Combine(sourceBin, "WILLOWMAKER.exe");
        string dllSrc = Path.Combine(sourceBin, "WILLOWMAKER.dll");
        File.WriteAllText(exeSrc, "EXE-V1");
        File.WriteAllText(dllSrc, "DLL-V1");
        File.WriteAllText(Path.Combine(sourceBin, "WILLOWMAKER.xml"), "<doc />");
        File.WriteAllText(Path.Combine(sourceBin, "runtimes", "win-x64", "native", "vulkan.dll"), "WIN-NATIVE");
        File.WriteAllText(Path.Combine(sourceBin, "runtimes", "win", "lib", "helper.dll"), "WIN-LIB");
        File.WriteAllText(Path.Combine(sourceBin, "runtimes", "linux-x64", "native", "libfoo.so"), "LINUX-BLOAT");
        File.WriteAllText(Path.Combine(sourceBin, "linux-x64", "publish.bin"), "LINUX-PUB");
        File.WriteAllText(Path.Combine(sourceBin, "osx-arm64", "publish.bin"), "OSX-PUB");
        File.WriteAllText(Path.Combine(sourceBin, "publish", "bundle.bin"), "PUB-BLOAT");

        // 1. Initial sync to slot-a strips foreign RIDs, publish dirs, and .xml docs
        ShadowSyncResult first = FastAppLauncher.SyncToShadowSlot(
            sourceBin, "willowmaker", "WILLOWMAKER",
            hostRidOverride: "win-x64",
            isWindowsOverride: true,
            cacheBaseDirOverride: cacheBase);

        Path.GetFileName(first.SlotDirectory).Should().Be("slot-a");
        first.RolledOverDueToLock.Should().BeFalse();
        first.CopiedFiles.Should().Be(4); // exe, dll, win-x64/vulkan.dll, win/helper.dll
        File.Exists(Path.Combine(first.SlotDirectory, "WILLOWMAKER.exe")).Should().BeTrue();
        File.Exists(Path.Combine(first.SlotDirectory, "runtimes", "win-x64", "native", "vulkan.dll")).Should().BeTrue();
        File.Exists(Path.Combine(first.SlotDirectory, "WILLOWMAKER.xml")).Should().BeFalse();
        Directory.Exists(Path.Combine(first.SlotDirectory, "linux-x64")).Should().BeFalse();
        Directory.Exists(Path.Combine(first.SlotDirectory, "osx-arm64")).Should().BeFalse();
        Directory.Exists(Path.Combine(first.SlotDirectory, "publish")).Should().BeFalse();
        Directory.Exists(Path.Combine(first.SlotDirectory, "runtimes", "linux-x64")).Should().BeFalse();

        // 2. Second sync with unchanged files copies 0 bytes
        ShadowSyncResult second = FastAppLauncher.SyncToShadowSlot(
            sourceBin, "willowmaker", "WILLOWMAKER",
            hostRidOverride: "win-x64",
            isWindowsOverride: true,
            cacheBaseDirOverride: cacheBase);

        Path.GetFileName(second.SlotDirectory).Should().Be("slot-a");
        second.CopiedFiles.Should().Be(0);
        second.SkippedUnchangedFiles.Should().Be(4);
        second.RolledOverDueToLock.Should().BeFalse();

        // 3. Simulate a locked running binary in slot-a while source binary changes -> must roll over cleanly to slot-b
        string lockedExeInSlotA = Path.Combine(first.SlotDirectory, "WILLOWMAKER.exe");
        File.WriteAllText(exeSrc, "EXE-V2-UPDATED-LONGER");
        using (FileStream _ = new(lockedExeInSlotA, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ShadowSyncResult third = FastAppLauncher.SyncToShadowSlot(
                sourceBin, "willowmaker", "WILLOWMAKER",
                hostRidOverride: "win-x64",
                isWindowsOverride: true,
                cacheBaseDirOverride: cacheBase);

            Path.GetFileName(third.SlotDirectory).Should().Be("slot-b");
            third.RolledOverDueToLock.Should().BeTrue();
            File.ReadAllText(third.ExecutablePath).Should().Be("EXE-V2-UPDATED-LONGER");
        }
    }

    [Fact]
    public void CliPathResolver_PrefersCallerCwd_FallsBackToWorkspaceAndClient_AndRoutesOutputsCorrectly()
    {
        string callerCwd = Path.Combine(_tempRoot, "Downloads");
        string wsRoot = Path.Combine(_tempRoot, "ProjectKongor");
        string overrideUi = Path.Combine(wsRoot, "games", "client", "override", "ui");
        Directory.CreateDirectory(callerCwd);
        Directory.CreateDirectory(overrideUi);

        string callerReplay = Path.Combine(callerCwd, "match.honreplay");
        string wsReplay = Path.Combine(wsRoot, "match.honreplay");
        string clientDll = Path.Combine(wsRoot, "games", "client", "cgame_x64.dll");
        File.WriteAllText(callerReplay, "caller");
        File.WriteAllText(wsReplay, "ws");
        File.WriteAllText(clientDll, "dll");

        // 1. Caller CWD wins over workspace when present in both
        CliPathResolver.ResolveInputPath("match.honreplay", wsRoot, callerCwd)
            .Should().Be(Path.GetFullPath(callerReplay));

        // 2. Workspace fallback works from outside repo
        CliPathResolver.ResolveInputPath("games/client/override/ui", wsRoot, callerCwd)
            .Should().Be(Path.GetFullPath(overrideUi));

        // 3. games/client fallback works for VFS/DLL shorthands
        CliPathResolver.ResolveInputPath("cgame_x64.dll", wsRoot, callerCwd)
            .Should().Be(Path.GetFullPath(clientDll));

        // 4. Output routing: explicit -o -> caller CWD; omitted export -> caller CWD; omitted internal -> ws/artifacts
        CliPathResolver.ResolveOutputPath("out/clip.mp4", wsRoot, "default.mp4", isExplicitExportCommand: false, callerCwd)
            .Should().Be(Path.GetFullPath(Path.Combine(callerCwd, "out", "clip.mp4")));

        CliPathResolver.ResolveOutputPath(null, wsRoot, "capture.png", isExplicitExportCommand: true, callerCwd)
            .Should().Be(Path.GetFullPath(Path.Combine(callerCwd, "capture.png")));

        CliPathResolver.ResolveOutputPath(null, wsRoot, "audit.json", isExplicitExportCommand: false, callerCwd)
            .Should().Be(Path.GetFullPath(Path.Combine(wsRoot, "artifacts", "audit.json")));

        // 5. Forwarded arg normalization promotes relative inputs and -o targets while leaving flags & URLs alone
        string[] forwarded = CliPathResolver.NormalizeForwardedPathArgs(
            ["highlight", "match.honreplay", "-o", "clip.mp4", "--url", "https://example.com/a?b=1"],
            wsRoot,
            callerCwd);
        forwarded[0].Should().Be("highlight");
        forwarded[1].Should().Be(Path.GetFullPath(callerReplay));
        forwarded[2].Should().Be("-o");
        forwarded[3].Should().Be(Path.GetFullPath(Path.Combine(callerCwd, "clip.mp4")));
        forwarded[5].Should().Be("https://example.com/a?b=1");
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("oracle")]
    [InlineData("practice")]
    [InlineData("inject")]
    [InlineData("decompile")]
    [InlineData("sanitize")]
    [InlineData("sync-lua")]
    [InlineData("sbom")]
    [InlineData("scan")]
    [InlineData("soak")]
    [InlineData("health")]
    [InlineData("migrate")]
    [InlineData("tailwind")]
    [InlineData("install-hooks")]
    [InlineData("audit-all")]
    [InlineData("test-e2e")]
    [InlineData("docgen")]
    [InlineData("coverage")]
    [InlineData("where")]
    [InlineData("workspace")]
    [InlineData("completion")]
    public async Task RouterParity_AllPortedKongorPs1Verbs_ResolveCleanlyWithHelp(string commandVerb)
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            Program.ConfigureCommands(config);
            config.PropagateExceptions();
        });

        int exitCode = await app.RunAsync([commandVerb, "--help"]);
        exitCode.Should().Be(0, $"command '{commandVerb}' must be registered in Program.ConfigureCommands");
    }

    [Fact]
    public void LayoutGateInterceptor_BlocksSourceOnlyCommandsInBundledInstall_AndAllowsPlayStatusHealth()
    {
        string bundleRoot = Path.Combine(_tempRoot, "StandaloneBundle");
        Directory.CreateDirectory(Path.Combine(bundleRoot, "games", "client"));
        File.WriteAllText(Path.Combine(bundleRoot, "playtest_policy.json"), "{}");

        string fakeHome = Path.Combine(_tempRoot, "home");
        Directory.CreateDirectory(fakeHome);

        // Save previous state and initialize against the BundledInstall
        string prevCwd = CliRuntimeContext.InvocationCwd;
        try
        {
            CliRuntimeContext.Initialize(["--root", bundleRoot, "gate"], invocationCwdOverride: _tempRoot, userHomeOverride: fakeHome);
            KongorLayoutGateInterceptor gate = new();

            // Source-only commands MUST be blocked with actionable KongorWorkspaceNotFoundException
            foreach (string blocked in new[] { "build", "test", "gate", "oracle", "inject", "clean", "migrate", "sbom" })
            {
                KongorLayoutGateInterceptor.RequiresSourceWorkspace(blocked).Should().BeTrue();
                CommandContext ctx = new([], new EmptyRemainingArgs(), blocked, KongorLayoutSupport.SourceWorkspaceOnly);
                Action act = () => gate.Intercept(ctx, new EmptyCommandSettings());
                act.Should().Throw<KongorWorkspaceNotFoundException>()
                    .WithMessage("*BundledInstall*");
            }

            // All-layout commands MUST pass the gate cleanly in BundledInstall mode
            foreach (string allowed in new[] { "play", "host", "status", "doctor", "where", "health", "completion" })
            {
                KongorLayoutGateInterceptor.RequiresSourceWorkspace(allowed).Should().BeFalse();
                CommandContext ctx = new([], new EmptyRemainingArgs(), allowed, KongorLayoutSupport.AllLayouts);
                Action act = () => gate.Intercept(ctx, new EmptyCommandSettings());
                act.Should().NotThrow();
            }
        }
        finally
        {
            CliRuntimeContext.Initialize([], invocationCwdOverride: prevCwd);
        }
    }

    [Fact]
    public void ShellCompletionGenerator_EmitsZeroSpawnStaticScripts_AndIdempotentProfileMarkers()
    {
        string fakeHome = Path.Combine(_tempRoot, "home-comp");
        Directory.CreateDirectory(fakeHome);

        string psScript = ShellCompletionGenerator.GenerateScript(
            CompletionShell.PowerShell,
            Program.AllPrimaryCommands,
            ["main-repo", "playtest-bundle"]);
        psScript.Should().Contain("Register-ArgumentCompleter");
        psScript.Should().Contain("'gate'");
        psScript.Should().Contain("'completion'");
        psScript.Should().Contain("'main-repo'");
        psScript.Should().Contain("'playtest-bundle'");
        psScript.Should().NotContain("& kongor"); // zero runtime process spawn on <Tab>

        string bashScript = ShellCompletionGenerator.GenerateScript(CompletionShell.Bash, Program.AllPrimaryCommands, ["main-repo"]);
        bashScript.Should().Contain("complete -F _kongor_completions kongor");
        bashScript.Should().Contain("main-repo");

        string zshScript = ShellCompletionGenerator.GenerateScript(CompletionShell.Zsh, Program.AllPrimaryCommands, ["main-repo"]);
        zshScript.Should().Contain("compdef _kongor kongor");

        string fishScript = ShellCompletionGenerator.GenerateScript(CompletionShell.Fish, Program.AllPrimaryCommands, ["main-repo"]);
        fishScript.Should().Contain("complete -c kongor");

        // Write completion script & verify idempotent profile markers
        string writtenPath = ShellCompletionGenerator.WriteCompletionScript(CompletionShell.PowerShell, Program.AllPrimaryCommands, fakeHome);
        File.Exists(writtenPath).Should().BeTrue();

        string profilePath = Path.Combine(fakeHome, "Microsoft.PowerShell_profile.ps1");
        File.WriteAllText(profilePath, "# Existing user profile content\nSet-Alias ll Get-ChildItem\n");
        string snippet = ShellCompletionGenerator.BuildProfileSnippet(CompletionShell.PowerShell, writtenPath);

        ShellCompletionGenerator.EnsureProfileBlock(profilePath, snippet).Should().BeTrue();
        ShellCompletionGenerator.EnsureProfileBlock(profilePath, snippet).Should().BeFalse("second call must be idempotent");

        string profileContent = File.ReadAllText(profilePath);
        profileContent.Should().Contain("Set-Alias ll Get-ChildItem");
        profileContent.Split(ShellCompletionGenerator.ProfileBeginMarker).Length.Should().Be(2, "begin marker must appear exactly once");
        profileContent.Split(ShellCompletionGenerator.ProfileEndMarker).Length.Should().Be(2, "end marker must appear exactly once");
    }

    private sealed class EmptyRemainingArgs : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } = Enumerable.Empty<KeyValuePair<string, string?>>().ToLookup(x => x.Key, x => x.Value);
        public IReadOnlyList<string> Raw { get; } = [];
    }
}
