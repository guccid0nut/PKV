// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Diagnostics;
using FluentAssertions;
using Kongor.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests.Docker;

public sealed class DockerStackCommandTests
{
    [Theory]
    [InlineData("rebuild")]
    [InlineData("build")]
    [InlineData("sync")]
    [InlineData("retest")]
    [InlineData("redeploy")]
    public void BuildPowerShellArguments_RebuildAndSyncActions_ImplyRebuildAndStopHostConflict(string action)
    {
        // protects: Single-command Docker rebuild and all-in-one sync CLI ergonomics so outdated images or host port locks never slip through.
        // fails_when: DockerStackCommand forgets to forward -Rebuild or -StopHostConflict for rebuild/sync/retest actions.
        // seam: DockerStackCommand.BuildPowerShellArguments contract against launch_docker_stack.ps1 flags.
        DockerStackCommandSettings settings = new()
        {
            Action = action,
            Performance = "Balanced",
            TestingDomain = "preview.newbs.io"
        };

        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments(
            @"C:\repo\scripts\launch_docker_stack.ps1",
            settings);

        args.Should().Contain(action);
        args.Should().Contain("-Performance");
        args.Should().Contain("Balanced");
        args.Should().Contain("-Rebuild");
        args.Should().Contain("-StopHostConflict");
        args.Should().Contain("-TestingDomain");
        args.Should().Contain("preview.newbs.io");
    }

    [Fact]
    public void BuildPowerShellArguments_DefaultUpAction_DoesNotForceRebuildOrStopHostConflictWhenUnset()
    {
        // protects: Fast cached 'kongor docker up' startup path when user did not request rebuild or conflict killing.
        // fails_when: Default 'up' action erroneously enables -Rebuild or -StopHostConflict unconditionally.
        // seam: DockerStackCommand.BuildPowerShellArguments default flags.
        DockerStackCommandSettings settings = new()
        {
            Action = "up",
            Performance = "Balanced"
        };

        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments(
            @"C:\repo\scripts\launch_docker_stack.ps1",
            settings);

        args.Should().Contain("up");
        args.Should().NotContain("-Rebuild");
        args.Should().NotContain("-StopHostConflict");
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("rebuild")]
    public void BuildPowerShellArguments_BaseSettingsWithSyncAndRebuild_ForwardsActionRebuildStopHostConflictAndAllFlags(string action)
    {
        // protects: Top-level DockerSyncCommand and DockerRebuildCommand sharing DockerStackBaseSettings without positional [action] collisions.
        // fails_when: BuildPowerShellArguments(scriptPath, action, baseSettings) drops -Rebuild, -StopHostConflict, or optional flags.
        // seam: DockerStackCommand.BuildPowerShellArguments overload taking DockerStackBaseSettings.
        DockerStackBaseSettings settings = new()
        {
            Performance = "High",
            LocalPostgres = true,
            UseLauncher = true,
            StandaloneAuth = true,
            TestingDomain = "preview.newbs.io",
            MatchInstances = 3,
            ArcadeInstances = 2
        };

        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments(
            @"C:\repo\scripts\launch_docker_stack.ps1",
            action,
            settings);

        args.Should().ContainInOrder("-File", @"C:\repo\scripts\launch_docker_stack.ps1", action, "-Performance", "High");
        args.Should().Contain("-WithClient");
        args.Should().Contain("-UseLauncher");
        args.Should().Contain("-LocalPostgres");
        args.Should().Contain("-Rebuild");
        args.Should().Contain("-StopHostConflict");
        args.Should().Contain("-StandaloneAuth");
        args.Should().ContainInOrder("-TestingDomain", "preview.newbs.io");
        args.Should().ContainInOrder("-MatchInstances", "3");
        args.Should().ContainInOrder("-ArcadeInstances", "2");
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("docker-sync")]
    [InlineData("sync-docker")]
    [InlineData("retest-docker")]
    [InlineData("docker-rebuild")]
    [InlineData("rebuild-docker")]
    public void ConfigureCommands_TopLevelSyncAndDockerRebuildAliases_ParseCleanlyWithoutUnknownCommand(string verb)
    {
        // protects: Global 'kongor sync', 'kongor docker-sync', and 'kongor docker-rebuild' CLI registration without colliding with 'build --alias rebuild'.
        // fails_when: Top-level aliases are unregistered or fail Spectre.Console parsing on '--help'.
        // seam: Kongor.Cli.Program.ConfigureCommands with isolated IAnsiConsole output.
        using StringWriter outputWriter = new();
        IAnsiConsole isolatedConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(outputWriter)
        });

        CommandApp app = new();
        app.Configure(config =>
        {
            config.ConfigureConsole(isolatedConsole);
            Kongor.Cli.Program.ConfigureCommands(config);
        });

        int exitCode = app.Run([verb, "--help"]);

        exitCode.Should().Be(0, $"command '{verb} --help' must resolve and render help cleanly");
        outputWriter.ToString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LaunchDockerStackScript_StatusProbeAndSyncPipeline_EnforcesOutHostScalarReturnAndBuildBeforeDown()
    {
        // protects: (1) Zero PowerShell array pollution in Invoke-DockerStackStatusProbe, (2) build-before-down sync ordering,
        //           (3) 1.27 GB prebaked .glb bind-mount bypass, and (4) apt-get cache preservation across rebuilds.
        // fails_when: PowerShell scripts have syntax errors, docker compose ps lacks Out-Host, sync tears down before build, or prebaked/cache guards regress.
        // seam: PowerShell Language Parser AST + deployment scripts/Dockerfiles/Compose contracts.
        string repoRoot = FindRepoRoot();
        string launchScriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");
        string kongorPs1Path = Path.Combine(repoRoot, "kongor.ps1");
        string dockerIgnorePath = Path.Combine(repoRoot, ".dockerignore");
        string composePath = Path.Combine(repoRoot, "infra", "docker", "docker-compose.stack.yml");
        string serviceDockerfilePath = Path.Combine(repoRoot, "infra", "docker", "Dockerfile.service");
        string dedicatedDockerfilePath = Path.Combine(repoRoot, "infra", "docker", "Dockerfile.dedicated");

        string psCommand =
            $"foreach ($f in @('{launchScriptPath.Replace("'", "''")}', '{kongorPs1Path.Replace("'", "''")}')) {{ " +
            "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$e); " +
            "if ($e.Count -gt 0) { $e | ForEach-Object { $_.Message }; exit 1 } }";
        string encodedPsCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psCommand));

        ProcessStartInfo psi = new()
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh",
            Arguments = $"-NoProfile -EncodedCommand {encodedPsCommand}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        proc.ExitCode.Should().Be(0, $"PowerShell AST parse errors detected: {stdout}\n{stderr}");

        string launchText = File.ReadAllText(launchScriptPath);
        int probeStart = launchText.IndexOf("function Invoke-DockerStackStatusProbe", StringComparison.Ordinal);
        int statusHandlerIdx = launchText.IndexOf("if ($NormalizedAction -eq \"status\")", probeStart, StringComparison.Ordinal);
        probeStart.Should().BeGreaterThanOrEqualTo(0);
        statusHandlerIdx.Should().BeGreaterThan(probeStart);

        string probeBody = launchText[probeStart..statusHandlerIdx];
        probeBody.Should().Contain("| Out-Host", "docker compose ps stdout must not pollute Invoke-DockerStackStatusProbe's return value");
        probeBody.Should().Contain("Garnet Cache (TCP)");
        probeBody.Should().Contain("--format\", \"json\"");
        probeBody.Should().Contain("return [int]0");

        int buildLoopIdx = launchText.IndexOf("foreach ($buildSvc in $uniqueImageServices)", StringComparison.Ordinal);
        int cutoverDownIdx = launchText.IndexOf("[STAGE 2/4 -> STAGE 3/4]", StringComparison.Ordinal);
        int stage4Idx = launchText.IndexOf("[STAGE 4/4] Full Docker sync & verification PASSED!", StringComparison.Ordinal);
        int liveBannerIdx = launchText.IndexOf("PROJECT KONGOR DOCKER STACK IS LIVE", StringComparison.Ordinal);

        buildLoopIdx.Should().BeGreaterThan(0);
        cutoverDownIdx.Should().BeGreaterThan(buildLoopIdx, "sync must build images before tearing down running containers");
        stage4Idx.Should().BeGreaterThan(cutoverDownIdx);
        stage4Idx.Should().BeLessThan(liveBannerIdx, "sync Stage 4 status check must run before printing the green LIVE banner");

        File.ReadAllText(dockerIgnorePath).Should().Contain("artifacts/publish/web-ui/wwwroot/assets/prebaked/**");
        launchText.Should().Contain("WEB_UI_PREBAKED_MOUNT_PATH");
        File.ReadAllText(composePath).Should().Contain("${WEB_UI_PREBAKED_MOUNT_PATH:-../..}:/app/wwwroot/assets/prebaked:ro");

        string serviceDockerText = File.ReadAllText(serviceDockerfilePath);
        serviceDockerText.IndexOf("RUN apt-get update", StringComparison.Ordinal)
            .Should().BeLessThan(serviceDockerText.IndexOf("ARG KONGOR_SOURCE_STAMP", StringComparison.Ordinal));

        string dedicatedDockerText = File.ReadAllText(dedicatedDockerfilePath);
        dedicatedDockerText.IndexOf("libtinfo5", StringComparison.Ordinal)
            .Should().BeLessThan(dedicatedDockerText.LastIndexOf("ARG KONGOR_SOURCE_STAMP", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPowerShellArguments_ForwardsSkipAutoRebuildDryRunAndRestartCleanup()
    {
        // protects: CLI argument forwarding for 'kongor docker restart|cleanup --skip-auto-rebuild --dry-run' and kongor.ps1 help text.
        // fails_when: DockerStackCommand drops -SkipAutoRebuild/-DryRun forwarding or kongor.ps1 omits restart|cleanup.
        // seam: DockerStackCommand.BuildPowerShellArguments contract and kongor.ps1 dispatcher help string.
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");

        DockerStackCommandSettings upSkipSettings = new()
        {
            Action = "up",
            Performance = "Eco",
            SkipAutoRebuild = true,
            StopHostConflict = true
        };
        IReadOnlyList<string> upArgs = DockerStackCommand.BuildPowerShellArguments(scriptPath, upSkipSettings);
        upArgs.Should().Contain("-SkipAutoRebuild");
        upArgs.Should().Contain("Eco");
        upArgs.Should().Contain("-StopHostConflict");

        DockerStackCommandSettings cleanupDrySettings = new()
        {
            Action = "cleanup",
            DryRun = true
        };
        IReadOnlyList<string> cleanupArgs = DockerStackCommand.BuildPowerShellArguments(scriptPath, cleanupDrySettings);
        cleanupArgs.Should().Contain("cleanup");
        cleanupArgs.Should().Contain("-DryRun");

        DockerStackCommandSettings restartSettings = new()
        {
            Action = "restart",
            Service = "web-ui"
        };
        IReadOnlyList<string> restartArgs = DockerStackCommand.BuildPowerShellArguments(scriptPath, restartSettings);
        restartArgs.Should().Contain("restart");
        restartArgs.Should().Contain("-Service");
        restartArgs.Should().Contain("web-ui");
        restartArgs.Should().NotContain("-Performance", "restart must omit -Performance when --perf is unspecified so the active .env.stack preset (e.g. Eco) is preserved");

        IReadOnlyList<string> defaultUpArgs = DockerStackCommand.BuildPowerShellArguments(
            scriptPath,
            new DockerStackCommandSettings { Action = "up" });
        defaultUpArgs.Should().ContainInOrder("-Performance", "Balanced");

        string kongorPs1Text = File.ReadAllText(Path.Combine(repoRoot, "kongor.ps1"));
        kongorPs1Text.Should().Contain("restart|cleanup");
    }

    [Fact]
    public void LaunchDockerStackScript_ContainsEcoCeilingsSingletonLockInPlaceRestartAndScopedCleanup()
    {
        // protects: Eco container memory ceilings, kongor_docker_stack.lock finally-release (exit 75), in-place restart preserving Redis, and strictly scoped cleanup.
        // fails_when: Eco reverts to 128M/384M OOM ceilings, lock release is missing from finally, restart tears down garnet, or cleanup runs broad system prune.
        // seam: Parsed scripts/launch_docker_stack.ps1 operational contracts and preset table.
        string repoRoot = FindRepoRoot();
        string launchText = File.ReadAllText(Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1"));

        string normalizedLaunchText = launchText.Replace("\r\n", "\n");
        normalizedLaunchText.Should().Contain("kongor_docker_stack.lock");
        normalizedLaunchText.Should().Contain("exit 75");
        normalizedLaunchText.Should().Contain("finally {\n    Release-DockerStackLock");
        normalizedLaunchText.Should().Contain("DashboardMemLimit    = \"256M\"");
        normalizedLaunchText.Should().Contain("ClusterMemLimit      = \"768M\"");
        normalizedLaunchText.Should().Contain("function Resolve-DockerServiceTarget");
        normalizedLaunchText.Should().Contain("function Invoke-DockerLegacyCleanup");
        normalizedLaunchText.Should().Contain("preserving Garnet Redis state & Aspire Dashboard");
        normalizedLaunchText.Should().Contain("[WARN] Cached Docker images");
        normalizedLaunchText.Should().NotContain("docker system prune");
        normalizedLaunchText.Should().NotContain("docker volume prune");
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string callerPath = "")
    {
        foreach (string seed in new[] { Directory.GetCurrentDirectory(), Path.GetDirectoryName(callerPath) ?? string.Empty, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(seed)) continue;
            DirectoryInfo? dir = new(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "kongor.ps1")) &&
                    File.Exists(Path.Combine(dir.FullName, "scripts", "docker", "launch_docker_stack.ps1")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        throw new InvalidOperationException("Could not locate repository root from test directory or caller file path.");
    }
}
