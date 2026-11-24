// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DockerStackCommandTests.cs
// System: Kongor.Mcp.Tests - Turnkey Docker Stack CLI & Pre-Flight Gate Tests
// Protects: Fail-closed secret contract, placeholder rejection, <=15-char
//           Queue Arcade host derivation, loopback->host.docker.internal DB
//           rewriting, performance preset mapping, and CLI flag forwarding.
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Commands;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class DockerStackCommandTests
{
    [Fact]
    public void BuildPowerShellArguments_DefaultSettings_ProducesExpectedUpInvocation()
    {
        // Arrange
        DockerStackCommandSettings settings = new();

        // Act
        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments(@"C:\repo\scripts\launch_docker_stack.ps1", settings);

        // Assert
        args.Should().Equal(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            @"C:\repo\scripts\launch_docker_stack.ps1",
            "up",
            "-Performance",
            "Balanced");
    }

    [Fact]
    public void BuildPowerShellArguments_UseLauncherAndOverrides_ImpliesWithClientAndForwardsAllFlags()
    {
        // Arrange
        DockerStackCommandSettings settings = new()
        {
            Action = "up",
            Performance = "High",
            UseLauncher = true,
            LocalPostgres = true,
            Rebuild = true,
            StopHostConflict = true,
            StandaloneAuth = true,
            TestingDomain = "preview.example.test",
            MatchInstances = 4,
            ArcadeInstances = 2
        };

        // Act
        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments("/repo/scripts/launch_docker_stack.ps1", settings);

        // Assert
        args.Should().ContainInOrder("-File", "/repo/scripts/launch_docker_stack.ps1", "up", "-Performance", "High");
        args.Should().Contain("-WithClient");
        args.Should().Contain("-UseLauncher");
        args.Should().Contain("-LocalPostgres");
        args.Should().Contain("-Rebuild");
        args.Should().Contain("-StopHostConflict");
        args.Should().Contain("-StandaloneAuth");
        args.Should().ContainInOrder("-TestingDomain", "preview.example.test");
        args.Should().ContainInOrder("-MatchInstances", "4");
        args.Should().ContainInOrder("-ArcadeInstances", "2");
    }

    [Fact]
    public void LaunchDockerStackCheck_MissingAndPlaceholderSecrets_FailsClosedWithRemediationAndZeroSecretLeakage()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor-docker-stack-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string secretsPath = Path.Combine(tempDir, "secrets.json");
            string dotEnvPath = Path.Combine(tempDir, ".env");
            string outEnvPath = Path.Combine(tempDir, ".env.stack");

            // Populate strictly with disallowed built-in dev placeholders and a too-short JWT key
            Dictionary<string, string> badSecrets = new()
            {
                ["Database:Provider"] = "PostgreSQL",
                ["distributed-cache-password"] = "DistributedCachePassword123!",
                ["jwt-signing-key"] = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!",
                ["InternalServiceKey"] = "NEXUS-Internal-Service-Key-Dev-2026",
                ["Compel:AuthenticationToken"] = "YOUR_COMPEL_AUTH_TOKEN",
                ["HostAccount:UserName"] = "YOUR_KONGOR_USERNAME",
                ["HostAccount:Password"] = "YOUR_KONGOR_PASSWORD"
            };
            File.WriteAllText(secretsPath, JsonSerializer.Serialize(badSecrets));

            (int exitCode, string stdout) = RunStackCheck(scriptPath, secretsPath, dotEnvPath, outEnvPath, "Balanced", localPostgres: false);

            exitCode.Should().NotBe(0, "pre-flight gate must fail closed when secrets are missing or set to dev defaults");
            stdout.Should().Contain("PRE-FLIGHT CHECK FAILED");
            stdout.Should().Contain("Supabase:DatabaseConnectionString");
            stdout.Should().Contain("distributed-cache-password");
            stdout.Should().Contain("jwt-signing-key");
            stdout.Should().Contain("InternalServiceKey");
            stdout.Should().Contain("Compel:AuthenticationToken");
            stdout.Should().Contain("HostAccount:UserName");
            stdout.Should().Contain("HostAccount:Password");
            stdout.Should().Contain(@".\kongor.ps1 secret set");
            File.Exists(outEnvPath).Should().BeFalse("runtime .env.stack must not be emitted when pre-flight fails");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LaunchDockerStackCheck_ValidSecrets_NormalizesLoopbackDbDerives15CharArcadeHostAndAppliesHighPreset()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor-docker-stack-ok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        const string secretCachePass = "Cache$ecret_Unit_Test_991!";
        const string secretJwt = "UnitTest_Unique_32Byte_Minimum_Signing_Secret_2026!";
        const string secretInternal = "UnitTest_Internal_Service_Secret_2026!";
        const string secretCompel = "UnitTest_Compel_Bearer_Token_2026!";
        const string secretHostPass = "UnitTest_Host_Srp_Pass_2026!";

        try
        {
            string secretsPath = Path.Combine(tempDir, "secrets.json");
            string dotEnvPath = Path.Combine(tempDir, ".env");
            string outEnvPath = Path.Combine(tempDir, ".env.stack");

            Dictionary<string, string> validSecrets = new()
            {
                ["Database:Provider"] = "PostgreSQL",
                ["ConnectionStrings:MERRICK"] = "Host=localhost;Port=5432;Database=merrick;Username=postgres;Password=DbUnitPass$1",
                ["distributed-cache-password"] = secretCachePass,
                ["jwt-signing-key"] = secretJwt,
                ["InternalServiceKey"] = secretInternal,
                ["Compel:AuthenticationToken"] = secretCompel,
                ["HostAccount:UserName"] = "superlonghostname_docker",
                ["HostAccount:Password"] = secretHostPass
            };
            File.WriteAllText(secretsPath, JsonSerializer.Serialize(validSecrets));

            (int exitCode, string stdout) = RunStackCheck(scriptPath, secretsPath, dotEnvPath, outEnvPath, "High", localPostgres: false);

            exitCode.Should().Be(0, $"valid secrets should pass check cleanly, but got output:\n{stdout}");
            stdout.Should().NotContain(secretCachePass, "secret values must never be printed to console logs");
            stdout.Should().NotContain(secretJwt);
            stdout.Should().NotContain(secretHostPass);

            File.Exists(outEnvPath).Should().BeTrue();
            string envText = File.ReadAllText(outEnvPath);
            envText.Should().Contain("PERFORMANCE_PRESET='High'");
            envText.Should().Contain("DOTNET_GC_SERVER='1'");
            envText.Should().Contain("COMPEL_PRIORITY_MODE='HighPriority'");
            envText.Should().Contain("MATCH_INSTANCES='4'");
            envText.Should().Contain("Host=host.docker.internal;Port=5432;");
            envText.Should().Contain("PUBLIC_GAME_SERVER_IP='127.0.0.1'");
            envText.Should().Contain("ARCADE_HOST_USERNAME='superlongho_arc'", "derived _arc host login must never exceed HoN's 15-char limit");
            envText.Should().Contain("KONGOR_STANDALONE_WEB='false'", "Docker stack must match Start Cluster Discord OAuth mode and production coin economy by default");
            envText.Should().Contain("https://example.com");
            envText.Should().Contain("https://www.example.com");
            envText.Should().Contain("https://masterserver.example.com");
            envText.Should().Contain("https://test.example.com");
            File.Exists(Path.Combine(tempDir, "aspnetcore-devcert.pfx"))
                .Should().BeTrue("pre-flight check must provision a valid PFX certificate for Kestrel HTTPS :5557/:5201 and Aspire Dashboard :5550");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    ///     protects: TestingDomain CORS/SAN expansion and StandaloneAuth opt-in toggle in generated .env.stack.
    ///     fails_when: -TestingDomain fails to strip scheme/slashes or omit http+https CORS origins, or -StandaloneAuth fails to set KONGOR_STANDALONE_WEB='true'.
    ///     seam: Real PowerShell execution of scripts/launch_docker_stack.ps1 check against isolated temporary secrets vault.
    /// </summary>
    [Fact]
    public void LaunchDockerStackCheck_TestingDomainAndStandaloneAuth_EmitsTestingCorsOriginsAndStandaloneWebFlag()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor-docker-stack-td-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string secretsPath = Path.Combine(tempDir, "secrets.json");
            string dotEnvPath = Path.Combine(tempDir, ".env");
            string outEnvPath = Path.Combine(tempDir, ".env.stack");

            Dictionary<string, string> validSecrets = new()
            {
                ["Database:Provider"] = "PostgreSQL",
                ["ConnectionStrings:MERRICK"] = "Host=localhost;Port=5432;Database=merrick;Username=postgres;Password=DbUnitPass$1",
                ["distributed-cache-password"] = "Cache$ecret_Unit_Test_991!",
                ["jwt-signing-key"] = "UnitTest_Unique_32Byte_Minimum_Signing_Secret_2026!",
                ["InternalServiceKey"] = "UnitTest_Internal_Service_Secret_2026!",
                ["Compel:AuthenticationToken"] = "UnitTest_Compel_Bearer_Token_2026!",
                ["HostAccount:UserName"] = "kongor_host",
                ["HostAccount:Password"] = "UnitTest_Host_Srp_Pass_2026!",
                ["Authentication:Discord:ClientId"] = "123456789012345678",
                ["Authentication:Discord:ClientSecret"] = "Discord_Test_Secret_Value_99"
            };
            File.WriteAllText(secretsPath, JsonSerializer.Serialize(validSecrets));

            (int exitCode, string stdout) = RunStackCheck(
                scriptPath,
                secretsPath,
                dotEnvPath,
                outEnvPath,
                "Eco",
                localPostgres: false,
                testingDomain: "https://preview.custom-test.dev/",
                standaloneAuth: true);

            exitCode.Should().Be(0, $"testing-domain check should succeed, but got:\n{stdout}");
            string envText = File.ReadAllText(outEnvPath);
            envText.Should().Contain("KONGOR_STANDALONE_WEB='true'");
            envText.Should().Contain("TESTING_DOMAIN='preview.custom-test.dev'");
            envText.Should().Contain("https://preview.custom-test.dev");
            envText.Should().Contain("http://preview.custom-test.dev");
            envText.Should().Contain("DISCORD_CLIENT_ID='123456789012345678'");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DetectServiceName_ClassifiesDockerStackLinesAndDoesNotMisattributeToAspireHost()
    {
        string preflightLine = "[2026-11-10 09:22:14.841] [DOCKER-STACK] [ERROR] Aborting 'check' because 10 required pre-flight check(s) failed.";
        NEXUS.ControlPanel.GUI.Services.NexusOrchestratorService.DetectServiceName(preflightLine)
            .Should().Be("DOCKER STACK");

        NEXUS.ControlPanel.GUI.Services.NexusOrchestratorService.GetDisplayServiceName("docker-stack")
            .Should().Be("DOCKER STACK");
    }

    [Fact]
    public void BuildPowerShellArguments_InitSecretsAction_ForwardsInitSecretsAndLocalPostgres()
    {
        DockerStackCommandSettings settings = new()
        {
            Action = "init-secrets",
            LocalPostgres = true
        };

        IReadOnlyList<string> args = DockerStackCommand.BuildPowerShellArguments("/repo/scripts/launch_docker_stack.ps1", settings);

        args.Should().ContainInOrder("-File", "/repo/scripts/launch_docker_stack.ps1", "init-secrets", "-Performance", "Balanced");
        args.Should().Contain("-LocalPostgres");
    }

    [Fact]
    public void DockerStackProbe_ParseComposePsJson_SupportsBothNdjsonAndJsonArrayAndInfersArcadeService()
    {
        const string ndjson = """
            {"Name":"kongor-stack-master-server","Service":"master-server","State":"running","Health":"healthy","Status":"Up 2 minutes (healthy)"}
            {"Name":"kongor-stack-compel-arcade","Service":"","State":"running","Health":"healthy","Status":"Up 2 minutes (healthy)"}
            """;

        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot snapNdjson =
            Kongor.Cli.Mcp.Services.DockerStackProbe.ParseComposePsJson(ndjson, "Balanced");

        snapNdjson.IsStackActive.Should().BeTrue();
        snapNdjson.RunningContainerCount.Should().Be(2);
        snapNdjson.PerformancePreset.Should().Be("Balanced");
        snapNdjson.TryGetRunningService("master-server").Should().NotBeNull();
        snapNdjson.TryGetRunningService("compel-queue-arcade")
            .Should().NotBeNull("kongor-stack-compel-arcade container name must map to compose service compel-queue-arcade");

        const string jsonArray = """
            [
              {"Name":"kongor-stack-chat-server","Service":"chat-server","State":"running","Health":"healthy","Status":"Up 1m"},
              {"Name":"kongor-stack-postgres","Service":"postgres","State":"exited","Health":"","Status":"Exited (0)"}
            ]
            """;

        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot snapArray =
            Kongor.Cli.Mcp.Services.DockerStackProbe.ParseComposePsJson(jsonArray, "High");

        snapArray.IsStackActive.Should().BeTrue();
        snapArray.RunningContainerCount.Should().Be(1);
        snapArray.TotalContainerCount.Should().Be(2);
        snapArray.TryGetRunningService("chat-server").Should().NotBeNull();
        snapArray.TryGetRunningService("postgres").Should().BeNull();
    }

    [Fact]
    public void DockerStackProbe_ParseComposePsJson_ExcludesCompletedVolumeInitButPreservesFailedInit()
    {
        const string completedInitNdjson = """
            {"Name":"kongor-stack-volume-init","Service":"volume-init","State":"exited","Health":"","Status":"Exited (0) 4 seconds ago"}
            {"Name":"kongor-stack-master-server","Service":"master-server","State":"running","Health":"healthy","Status":"Up 1 minute (healthy)"}
            """;

        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot snapOk =
            Kongor.Cli.Mcp.Services.DockerStackProbe.ParseComposePsJson(completedInitNdjson, "Balanced");

        snapOk.RunningContainerCount.Should().Be(1);
        snapOk.TotalContainerCount.Should().Be(1, "completed one-shot volume-init (Exited (0)) must not inflate TotalContainerCount");
        snapOk.Services.Should().NotContainKey("volume-init");

        const string failedInitNdjson = """
            {"Name":"kongor-stack-volume-init","Service":"volume-init","State":"exited","Health":"","Status":"Exited (1) 2 seconds ago"}
            {"Name":"kongor-stack-master-server","Service":"master-server","State":"running","Health":"healthy","Status":"Up 1 minute (healthy)"}
            """;

        Kongor.Cli.Mcp.Services.DockerComposeStackSnapshot snapFail =
            Kongor.Cli.Mcp.Services.DockerStackProbe.ParseComposePsJson(failedInitNdjson, "Balanced");

        snapFail.TotalContainerCount.Should().Be(2, "failed volume-init (non-zero exit) must stay visible to operators");
        snapFail.Services.Should().ContainKey("volume-init");
    }

    [Fact]
    public void DockerStackNonRootContracts_VolumeInitSecretsAndWebUiConfigStayConsistent()
    {
        string repoRoot = FindRepoRoot();
        string composeText = File.ReadAllText(Path.Combine(repoRoot, "infra", "docker", "docker-compose.stack.yml"));
        string dedicatedDockerfile = File.ReadAllText(Path.Combine(repoRoot, "infra", "docker", "Dockerfile.dedicated"));
        string serviceDockerfile = File.ReadAllText(Path.Combine(repoRoot, "infra", "docker", "Dockerfile.service"));
        string entrypointText = File.ReadAllText(Path.Combine(repoRoot, "infra", "docker", "entrypoint.sh"));
        string webUiDevSettings = File.ReadAllText(Path.Combine(repoRoot, "src", "services", "web-ui", "appsettings.Development.json"));

        dedicatedDockerfile.Should().Contain("USER kongor:kongor");
        serviceDockerfile.Should().Contain("USER kongor:kongor");
        dedicatedDockerfile.Should().Contain("--uid 10001");
        serviceDockerfile.Should().Contain("--uid 10001");

        composeText.Should().Contain("volume-init:");
        composeText.Should().Contain("chown -R 10001:10001");
        composeText.Should().Contain("kongor-run-server:/vol/run-server");
        composeText.Should().Contain("kongor-stack-replays:/vol/replays");
        composeText.Should().Contain("kongor-dataprotection-keys:/vol/dataprotection-keys");
        composeText.Should().Contain("condition: service_completed_successfully");
        composeText.Should().NotContain("/root/.microsoft/usersecrets", "non-root uid=10001 containers cannot read /root and must receive explicit environment variables only");

        entrypointText.Should().Contain("assert_writable_dir");
        entrypointText.Should().Contain("[ ! -w ");
        entrypointText.Should().NotContain("\r\n", "Linux container entrypoint.sh must use LF line endings");

        webUiDevSettings.Should().NotContain("Trusted_Connection", "web-ui must not ship a legacy SQL Server LocalDB connection string that breaks Npgsql health checks");
        webUiDevSettings.Should().NotContain("(localdb)");
    }

    [Fact]
    public void DockerStackProbe_TryReadActivePerformancePreset_UnquotesRuntimeEnvValue()
    {
        string tempRepo = Path.Combine(Path.GetTempPath(), $"kongor-envstack-probe-{Guid.NewGuid():N}");
        string dockerDir = Path.Combine(tempRepo, "userdata", "docker");
        Directory.CreateDirectory(dockerDir);

        try
        {
            File.WriteAllText(
                Path.Combine(dockerDir, ".env.stack"),
                "# Generated by launch_docker_stack.ps1\nPERFORMANCE_PRESET='High'\nMATCH_HOST_USERNAME='kongor_host_dock'\n");

            Kongor.Cli.Mcp.Services.DockerStackProbe.TryReadActivePerformancePreset(tempRepo)
                .Should().Be("High");
            Kongor.Cli.Mcp.Services.DockerStackProbe.TryReadRuntimeEnvValue(tempRepo, "MATCH_HOST_USERNAME")
                .Should().Be("kongor_host_dock");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Fact]
    public void GameServerManageHandler_GetCandidateCompelControlPorts_PrioritizesArcadeAndDockerMatchTiers()
    {
        int[] arcadePorts = Kongor.Cli.Mcp.Handlers.GameServerManageHandler.GetCandidateCompelControlPorts(11485);
        arcadePorts.First().Should().Be(11484, "Queue Arcade UDP ports 11485..11534 belong to COMPEL control plane :11484");

        int[] dockerMatchPorts = Kongor.Cli.Mcp.Handlers.GameServerManageHandler.GetCandidateCompelControlPorts(11335);
        dockerMatchPorts.First().Should().Be(11334, "Docker Match UDP ports 11335..11434 belong to COMPEL control plane :11334");

        int[] hostMatchPorts = Kongor.Cli.Mcp.Handlers.GameServerManageHandler.GetCandidateCompelControlPorts(11235);
        hostMatchPorts.First().Should().Be(11234, "Native host Match UDP ports 11235..11333 belong to COMPEL control plane :11234");
    }

    [Fact]
    public async Task SecretsManagerService_ExternalDotEnvMutation_InvalidatesVaultStampCache()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor-secrets-stamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string secretsJson = Path.Combine(tempDir, "secrets.json");
            string dotEnv = Path.Combine(tempDir, ".env");
            await File.WriteAllTextAsync(secretsJson, """{"distributed-cache-password":"InitialVaultCachePass_1"}""");
            await File.WriteAllTextAsync(dotEnv, "DISTRIBUTED_CACHE_PASSWORD=InitialVaultCachePass_1\n");

            NEXUS.ControlPanel.GUI.Services.SecretsManagerService svc = new(
                customSecretsPath: secretsJson,
                envFilePath: dotEnv);

            NEXUS.Contracts.Configuration.NexusSecrets first = await svc.LoadSecretsAsync();
            first.DistributedCachePassword.Should().Be("InitialVaultCachePass_1");

            // Simulate external tool (e.g. kongor docker init-secrets) updating .env & secrets.json within the 2s cache window
            await File.WriteAllTextAsync(secretsJson, """{"distributed-cache-password":"RotatedExternalPass_2"}""");
            await File.WriteAllTextAsync(dotEnv, "DISTRIBUTED_CACHE_PASSWORD=RotatedExternalPass_2_WithExtraLength\n");

            NEXUS.Contracts.Configuration.NexusSecrets second = await svc.LoadSecretsAsync();
            second.DistributedCachePassword.Should().Be("RotatedExternalPass_2");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static (int ExitCode, string Stdout) RunStackCheck(
        string scriptPath,
        string secretsPath,
        string dotEnvPath,
        string outEnvPath,
        string performance,
        bool localPostgres,
        string? testingDomain = null,
        bool standaloneAuth = false)
    {
        string shellExe = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        ProcessStartInfo psi = new()
        {
            FileName = shellExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("check");
        psi.ArgumentList.Add("-Performance");
        psi.ArgumentList.Add(performance);
        psi.ArgumentList.Add("-SecretsFilePath");
        psi.ArgumentList.Add(secretsPath);
        psi.ArgumentList.Add("-DotEnvPath");
        psi.ArgumentList.Add(dotEnvPath);
        psi.ArgumentList.Add("-OutputEnvFilePath");
        psi.ArgumentList.Add(outEnvPath);
        psi.ArgumentList.Add("-IgnoreProcessEnv");
        psi.ArgumentList.Add("-SkipRuntimeChecks");
        string logPath = Path.Combine(Path.GetDirectoryName(secretsPath)!, "docker_stack.test.log");
        psi.ArgumentList.Add("-LogFilePath");
        psi.ArgumentList.Add(logPath);
        if (localPostgres)
        {
            psi.ArgumentList.Add("-LocalPostgres");
        }
        if (!string.IsNullOrWhiteSpace(testingDomain))
        {
            psi.ArgumentList.Add("-TestingDomain");
            psi.ArgumentList.Add(testingDomain);
        }
        if (standaloneAuth)
        {
            psi.ArgumentList.Add("-StandaloneAuth");
        }

        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        return (proc.ExitCode, $"{stdout}\n{stderr}");
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
