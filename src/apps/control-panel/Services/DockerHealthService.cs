// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class DockerHealthService : IDockerHealthService, IDisposable
{
    private static (DateTimeOffset Timestamp, DockerStatus Status)? _cachedDockerStatus;
    private static readonly Lock _dockerStatusLock = new();

    private readonly IDockerClient _client;
    private readonly bool _ownsClient;

    public IDockerClient Client => _client;

    public DockerHealthService(IDockerClient? dockerClient = null)
    {
        if (dockerClient != null)
        {
            _client = dockerClient;
            _ownsClient = false;
            LogExtensive("Initialized with custom/injected IDockerClient.");
        }
        else
        {
            _client = CreateDefaultClient();
            _ownsClient = true;
            LogExtensive("Initialized with default DockerClient connected to daemon pipe/socket.");
        }
    }

    public static IDockerClient CreateDefaultClient()
    {
        // DockerClientConfiguration automatically resolves:
        // Windows: npipe://./pipe/docker_engine
        // Linux:   unix:///var/run/docker.sock
        return new DockerClientConfiguration().CreateClient();
    }

    private static void LogExtensive(string message)
    {
        string formatted = $"[DockerHealth] [{TimeProvider.System.GetUtcNow():HH:mm:ss.fff}] {message}";
        Console.WriteLine(formatted);
        Trace.WriteLine(formatted);
    }

    public static string ResolveDockerCliPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string desktopCli = Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe");
            if (File.Exists(desktopCli))
            {
                return desktopCli;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localCli = Path.Combine(localAppData, "Docker", "resources", "bin", "docker.exe");
            if (File.Exists(localCli))
            {
                return localCli;
            }
        }

        return "docker";
    }

    public static string ResolveDotnetCliPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localCli = Path.Combine(localAppData, "Microsoft", "dotnet", "dotnet.exe");
            if (File.Exists(localCli))
            {
                return localCli;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string userCli = Path.Combine(userProfile, ".dotnet", "dotnet.exe");
            if (File.Exists(userCli))
            {
                return userCli;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string systemCli = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (File.Exists(systemCli))
            {
                return systemCli;
            }
        }

        return "dotnet";
    }

    public static bool IsDockerDaemonRunningFast()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                bool pipeExists = File.Exists(@"\\.\pipe\docker_engine") ||
                                  File.Exists(@"\\.\pipe\dockerDesktopLinuxEngine") ||
                                  File.Exists(@"\\.\pipe\dockerDesktopEngine");

                bool processExists = Process.GetProcessesByName("Docker Desktop").Length > 0 ||
                                     Process.GetProcessesByName("com.docker.backend").Length > 0 ||
                                     Process.GetProcessesByName("dockerd").Length > 0;

                return pipeExists && processExists;
            }

            return File.Exists("/var/run/docker.sock") ||
                   Process.GetProcessesByName("dockerd").Length > 0;
        }
        catch (Exception ex)
        {
            LogExtensive($"Fast daemon check exception: {ex.Message}");
            return false;
        }
    }

    public Task<DockerStatus> CheckDockerStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_dockerStatusLock)
        {
            if (_cachedDockerStatus.HasValue &&
                TimeProvider.System.GetUtcNow() - _cachedDockerStatus.Value.Timestamp < TimeSpan.FromSeconds(8))
            {
                return Task.FromResult(_cachedDockerStatus.Value.Status);
            }
        }

        return Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            DockerStatus status;
            try
            {
                string cliPath = ResolveDockerCliPath();
                bool isDaemonRunning = IsDockerDaemonRunningFast();

                if (!isDaemonRunning)
                {
                    bool cliExists = File.Exists(cliPath) || (cliPath == "docker");
                    status = new DockerStatus
                    {
                        IsInstalled = cliExists,
                        IsRunning = false,
                        StatusMessage = "Docker Desktop / daemon is not running."
                    };
                    LogExtensive($"Daemon not running (CLI exists: {cliExists}, took {sw.ElapsedMilliseconds}ms).");
                    lock (_dockerStatusLock) _cachedDockerStatus = (TimeProvider.System.GetUtcNow(), status);
                    return status;
                }

                status = new DockerStatus
                {
                    IsInstalled = true,
                    IsRunning = true,
                    Version = "Active",
                    StatusMessage = "Docker Engine is running and ready."
                };

                // Fast client version enrichment using Docker.DotNet GetVersionAsync
                try
                {
                    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(800));

                    VersionResponse versionResponse = await _client.System.GetVersionAsync(timeoutCts.Token);
                    if (!string.IsNullOrWhiteSpace(versionResponse?.Version))
                    {
                        status.Version = versionResponse.Version;
                    }
                    LogExtensive($"Docker Engine version '{status.Version}' resolved in {sw.ElapsedMilliseconds}ms via Docker.DotNet.");
                }
                catch (OperationCanceledException)
                {
                    LogExtensive($"Version enrichment timed out after 800ms; daemon verified active via OS primitives ({sw.ElapsedMilliseconds}ms).");
                }
                catch (Exception ex)
                {
                    LogExtensive($"Non-fatal Docker version enrichment failure: {ex.Message} ({sw.ElapsedMilliseconds}ms).");
                }
            }
            catch (Exception ex)
            {
                LogExtensive($"Error checking Docker status: {ex.Message} ({sw.ElapsedMilliseconds}ms).");
                status = new DockerStatus
                {
                    IsInstalled = false,
                    IsRunning = false,
                    StatusMessage = $"Docker daemon unavailable ({ex.Message})."
                };
            }

            lock (_dockerStatusLock) _cachedDockerStatus = (TimeProvider.System.GetUtcNow(), status);
            return status;
        }, cancellationToken);
    }

    public async Task<bool> IsImageBuiltAsync(string imageName = "projectkongor/compel-dedicated:latest",
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            LogExtensive($"Inspecting image '{imageName}' via Docker.DotNet...");
            var inspect = await _client.Images.InspectImageAsync(imageName, cancellationToken);
            sw.Stop();
            bool exists = inspect != null && !string.IsNullOrWhiteSpace(inspect.ID);
            string idPreview = inspect?.ID != null ? inspect.ID[..Math.Min(12, inspect.ID.Length)] : "n/a";
            LogExtensive($"Image '{imageName}' found locally (ID: {idPreview}) in {sw.ElapsedMilliseconds}ms.");
            return exists;
        }
        catch (DockerImageNotFoundException)
        {
            sw.Stop();
            LogExtensive($"Image '{imageName}' not found locally ({sw.ElapsedMilliseconds}ms).");
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExtensive($"Docker.DotNet inspection failed for '{imageName}': {ex.Message} ({sw.ElapsedMilliseconds}ms).");
            return false;
        }
    }

    public async Task<bool> IsDedicatedImageStaleAsync(
        string workspaceRoot,
        string imageName = "projectkongor/compel-dedicated:latest",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var inspect = await _client.Images.InspectImageAsync(imageName, cancellationToken);
            if (inspect == null || string.IsNullOrWhiteSpace(inspect.ID) || inspect.Created == default)
            {
                return true;
            }

            DateTime imageBuiltUtc = inspect.Created.ToUniversalTime();
            string[] watchedPaths =
            [
                Path.Combine(workspaceRoot, "native", "proxies", "native_guard", "linux_native_guard.cpp"),
                Path.Combine(workspaceRoot, "native", "proxies", "native_guard", "linux_queue_arcade.h"),
                Path.Combine(workspaceRoot, "infra", "docker", "Dockerfile.dedicated"),
                Path.Combine(workspaceRoot, "infra", "docker", "entrypoint.sh"),
                Path.Combine(workspaceRoot, ".dockerignore"),
                Path.Combine(workspaceRoot, "artifacts", "publish", "compel", "COMPEL.dll")
            ];

            foreach (string path in watchedPaths)
            {
                if (File.Exists(path))
                {
                    DateTime modifiedUtc = File.GetLastWriteTimeUtc(path);
                    if (modifiedUtc > imageBuiltUtc.AddSeconds(30))
                    {
                        LogExtensive($"Image '{imageName}' (built {imageBuiltUtc:O}) is older than '{Path.GetFileName(path)}' ({modifiedUtc:O}) -> marking stale for rebuild.");
                        return true;
                    }
                }
            }

            string compelSrcDir = Path.Combine(workspaceRoot, "src", "apps", "compel");
            if (Directory.Exists(compelSrcDir))
            {
                foreach (string srcFile in Directory.EnumerateFiles(compelSrcDir, "*.cs", SearchOption.AllDirectories))
                {
                    DateTime srcModifiedUtc = File.GetLastWriteTimeUtc(srcFile);
                    if (srcModifiedUtc > imageBuiltUtc.AddSeconds(30))
                    {
                        LogExtensive($"Image '{imageName}' (built {imageBuiltUtc:O}) is older than COMPEL source '{Path.GetFileName(srcFile)}' ({srcModifiedUtc:O}) -> marking stale for rebuild.");
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            LogExtensive($"IsDedicatedImageStaleAsync inspection fallback for '{imageName}': {ex.Message}");
            return true;
        }
    }

    private static string? GetLegacyCounterpartContainerName(string containerName) =>
        containerName switch
        {
            ContainerLaunchCommandBuilder.QueueArcadeContainerName => ContainerLaunchCommandBuilder.LegacyQueueArcadeContainerName,
            ContainerLaunchCommandBuilder.RankedContainerName => ContainerLaunchCommandBuilder.LegacyRankedContainerName,
            ContainerLaunchCommandBuilder.LegacyQueueArcadeContainerName => ContainerLaunchCommandBuilder.QueueArcadeContainerName,
            ContainerLaunchCommandBuilder.LegacyRankedContainerName => ContainerLaunchCommandBuilder.RankedContainerName,
            _ => null
        };

    public async Task<(bool Success, string Output)> LaunchContainerAsync(
        CreateContainerParameters parameters,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string containerName = parameters.Name ?? ContainerLaunchCommandBuilder.RankedContainerName;
        try
        {
            LogExtensive($"Launching container '{containerName}' using image '{parameters.Image}' via Docker.DotNet...");

            // Pre-flight: Remove existing container with same name (and any legacy counterpart occupying the same ports)
            string? legacyCounterpart = GetLegacyCounterpartContainerName(containerName);
            string[] namesToRemove = legacyCounterpart is not null
                ? [containerName, legacyCounterpart]
                : [containerName];

            foreach (string targetToRemove in namesToRemove)
            {
                try
                {
                    LogExtensive($"Checking and removing pre-existing container '{targetToRemove}'...");
                    await _client.Containers.RemoveContainerAsync(targetToRemove,
                        new ContainerRemoveParameters { Force = true }, cancellationToken);
                    LogExtensive($"Pre-existing container '{targetToRemove}' removed successfully.");
                }
                catch (DockerContainerNotFoundException)
                {
                    LogExtensive($"No existing container '{targetToRemove}' found (clean state).");
                }
                catch (Exception ex)
                {
                    LogExtensive($"Pre-flight removal of '{targetToRemove}' noted: {ex.Message}");
                }
            }

            // Create container
            LogExtensive($"Creating container '{containerName}' with {parameters.Env?.Count ?? 0} environment variables and {parameters.HostConfig?.Binds?.Count ?? 0} binds...");
            var createResponse = await _client.Containers.CreateContainerAsync(parameters, cancellationToken);
            string containerId = createResponse.ID;
            string shortId = containerId[..Math.Min(12, containerId.Length)];
            LogExtensive($"Container created successfully: ID {shortId} (warnings: {string.Join(", ", createResponse.Warnings ?? Array.Empty<string>())})");

            // Start container
            LogExtensive($"Starting container {shortId}...");
            bool started = await _client.Containers.StartContainerAsync(containerId,
                new ContainerStartParameters(), cancellationToken);

            sw.Stop();
            if (started)
            {
                string msg = $"Container started successfully (ID: {shortId})";
                LogExtensive($"{msg} in {sw.ElapsedMilliseconds}ms.");
                return (true, msg);
            }

            LogExtensive($"StartContainerAsync returned false for container {shortId} ({sw.ElapsedMilliseconds}ms).");
            return (false, "Container created but failed to start.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            string errMsg = $"Error launching container: {ex.Message}";
            LogExtensive($"{errMsg} ({sw.ElapsedMilliseconds}ms)");
            return (false, errMsg);
        }
    }

    public async Task<(bool Success, string Output)> StopContainerAsync(string containerName = ContainerLaunchCommandBuilder.RankedContainerName,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string? legacyCounterpart = GetLegacyCounterpartContainerName(containerName);
        try
        {
            LogExtensive($"Stopping container '{containerName}' via Docker.DotNet (WaitBeforeKill: 10s)...");
            bool stopped = await _client.Containers.StopContainerAsync(containerName,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);

            if (legacyCounterpart is not null)
            {
                try
                {
                    await _client.Containers.StopContainerAsync(legacyCounterpart,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogExtensive($"Legacy counterpart '{legacyCounterpart}' stop skipped: {ex.Message}");
                }
            }

            sw.Stop();
            string msg = $"Container {containerName} stopped.";
            LogExtensive($"{msg} ({sw.ElapsedMilliseconds}ms)");
            return (true, msg);
        }
        catch (DockerContainerNotFoundException)
        {
            if (legacyCounterpart is not null)
            {
                try
                {
                    bool legacyStopped = await _client.Containers.StopContainerAsync(legacyCounterpart,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
                    if (legacyStopped)
                    {
                        sw.Stop();
                        string legacyMsg = $"Container {legacyCounterpart} stopped.";
                        LogExtensive($"{legacyMsg} ({sw.ElapsedMilliseconds}ms)");
                        return (true, legacyMsg);
                    }
                }
                catch (Exception ex)
                {
                    LogExtensive($"Legacy counterpart '{legacyCounterpart}' fallback stop skipped: {ex.Message}");
                }
            }

            sw.Stop();
            string msg = $"Container {containerName} not found (already stopped or removed).";
            LogExtensive($"{msg} ({sw.ElapsedMilliseconds}ms)");
            return (true, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string errMsg = $"Failed to stop container '{containerName}': {ex.Message}";
            LogExtensive($"{errMsg} ({sw.ElapsedMilliseconds}ms)");
            return (false, errMsg);
        }
    }

    public async Task<bool> StartContainerAsync(string containerName, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            LogExtensive($"Starting container '{containerName}' via Docker.DotNet...");
            bool started = await _client.Containers.StartContainerAsync(containerName,
                new ContainerStartParameters(), cancellationToken);
            sw.Stop();
            LogExtensive($"Container '{containerName}' started: {started} in {sw.ElapsedMilliseconds}ms.");
            return started;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExtensive($"Failed to start container '{containerName}' via Docker.DotNet: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            return false;
        }
    }

    public async Task<bool> EnsureImagePulledAsync(string imageName, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            LogExtensive($"Checking if image '{imageName}' exists locally...");
            try
            {
                var inspect = await _client.Images.InspectImageAsync(imageName, cancellationToken);
                if (inspect != null && !string.IsNullOrWhiteSpace(inspect.ID))
                {
                    LogExtensive($"Image '{imageName}' is already cached locally ({sw.ElapsedMilliseconds}ms).");
                    return true;
                }
            }
            catch (DockerImageNotFoundException)
            {
                LogExtensive($"Image '{imageName}' not found locally, initiating pull...");
            }

            var progress = new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrWhiteSpace(msg.Status))
                {
                    LogExtensive($"[Pull {imageName}] {msg.Status} {msg.ProgressMessage}");
                }
            });

            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = imageName },
                new AuthConfig(),
                progress,
                cancellationToken);

            sw.Stop();
            LogExtensive($"Successfully pulled image '{imageName}' in {sw.ElapsedMilliseconds}ms.");
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExtensive($"Failed to pull image '{imageName}' via Docker.DotNet: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            return false;
        }
    }

    public async Task<(bool Success, string Output)> BuildImageAsync(string dockerfilePath, string contextPath,
        string tag = "projectkongor/compel-dedicated:latest", CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string repoRoot = ResolveRepositoryRoot();

            string resolvedDockerfilePath = Path.IsPathRooted(dockerfilePath)
                ? dockerfilePath
                : Path.GetFullPath(Path.Combine(repoRoot, dockerfilePath));

            string resolvedContextPath = Path.IsPathRooted(contextPath)
                ? contextPath
                : (contextPath == "." ? repoRoot : Path.GetFullPath(Path.Combine(repoRoot, contextPath)));

            if (!File.Exists(resolvedDockerfilePath))
            {
                string missingMsg = $"Dockerfile not found at '{resolvedDockerfilePath}'.";
                LogExtensive(missingMsg);
                return (false, missingMsg);
            }

            // Step 1: Pre-publish self-contained COMPEL linux-x64 binary if missing or stale relative to src/apps/compel
            string publishDir = Path.Combine(repoRoot, "artifacts", "publish", "compel");
            string compelBinary = Path.Combine(publishDir, "COMPEL");
            bool needsCompelPublish = !File.Exists(compelBinary);
            if (!needsCompelPublish)
            {
                DateTime binaryUtc = File.GetLastWriteTimeUtc(compelBinary);
                string compelSrcRoot = Path.Combine(repoRoot, "src", "apps", "compel");
                if (Directory.Exists(compelSrcRoot))
                {
                    needsCompelPublish = Directory
                        .EnumerateFiles(compelSrcRoot, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        .Any(f => File.GetLastWriteTimeUtc(f) > binaryUtc);
                }
            }

            if (needsCompelPublish)
            {
                string dotnetCli = ResolveDotnetCliPath();
                LogExtensive($"Pre-publishing COMPEL binary (Release, linux-x64, self-contained) via '{dotnetCli}'...");
                using Process publishProcess = new();
                publishProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = dotnetCli,
                    Arguments = "publish src/apps/compel/COMPEL.csproj -c Release -r linux-x64 -o artifacts/publish/compel --self-contained",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (Path.IsPathRooted(dotnetCli) && Path.GetDirectoryName(dotnetCli) is { Length: > 0 } dotnetDir)
                {
                    publishProcess.StartInfo.Environment["DOTNET_ROOT"] = dotnetDir;
                }

                publishProcess.Start();
                Task<string> pOutTask = publishProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> pErrTask = publishProcess.StandardError.ReadToEndAsync(cancellationToken);
                await publishProcess.WaitForExitAsync(cancellationToken);
                string pOut = await pOutTask;
                string pErr = await pErrTask;

                if (publishProcess.ExitCode != 0)
                {
                    string failDetail = string.IsNullOrWhiteSpace(pErr) ? pOut.Trim() : pErr.Trim();
                    string failMsg = $"Publishing COMPEL binary failed: {failDetail}";
                    LogExtensive(failMsg);
                    return (false, failMsg);
                }
                LogExtensive("COMPEL binary pre-published successfully.");
            }

            // Step 2: Build Docker image using the CLI (BuildKit, local cache, and native .dockerignore filtering)
            LogExtensive($"Invoking docker build for tag '{tag}' at context '{resolvedContextPath}'...");
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ResolveDockerCliPath(),
                Arguments = $"build -f \"{resolvedDockerfilePath}\" -t {tag} \"{resolvedContextPath}\"",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            string stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            bool success = process.ExitCode == 0;
            sw.Stop();
            string resultMsg = success ? $"Image {tag} built successfully." : $"Build failed: {stdErr.Trim()}";
            LogExtensive($"docker build completed in {sw.ElapsedMilliseconds}ms (success: {success}). StdErr: {stdErr.Trim()}");
            return (success, resultMsg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string errMsg = $"Error building image: {ex.Message}";
            LogExtensive($"{errMsg} ({sw.ElapsedMilliseconds}ms)");
            return (false, errMsg);
        }
    }

    public static string ResolveRepositoryRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")) ||
                Directory.Exists(Path.Combine(current, "infra", "docker")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;

            current = parent.FullName;
        }

        string cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(cwd, "infra", "docker")))
            return cwd;

        return cwd;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            LogExtensive("Disposing owned IDockerClient.");
            _client.Dispose();
        }
    }
}
