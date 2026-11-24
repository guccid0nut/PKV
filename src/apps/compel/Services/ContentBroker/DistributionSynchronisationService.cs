// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Hosting;
using Kongor.Core.Vfs;

namespace COMPEL.Services.ContentBroker;

/// <summary>
///     Keeps the local match server distribution synchronised with the CDN.
///     On startup it performs an initial synchronisation (retried with backoff, or skipped if a local copy already exists,
///     or skipped entirely when <see cref="CDNOptions.Synchronisation" /> is disabled) and exposes
///     <see cref="SynchroniseNow" /> for the control plane to trigger a re-synchronisation on demand.
///     The supervisor awaits <see cref="WaitUntilReady" /> before launching the manager.
/// </summary>
public sealed class DistributionSynchronisationService : ResilientBackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    // COMPEL Installs The Distribution Alongside Its Own Executable, So Its Own Files (The Binary, "COMPEL.json", "COMPEL.log", "COMPEL.lock", And Any Build Artefacts) Are Protected From Being Overwritten Or Deleted By The Mirror, Regardless Of What The Manifest Declares.
    private static readonly string[] OwnFileProtectionPatterns = ["COMPEL*"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<DistributionSynchronisationService> logger;

    private readonly CDNOptions options;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long announcedTotalBytes;
    private long lastProgressLogTicks;

    private volatile bool synchronising;

    public DistributionSynchronisationService(IOptions<CDNOptions> options,
        ILogger<DistributionSynchronisationService> logger)
        : base(logger, nameof(DistributionSynchronisationService))
    {
        this.options = options.Value;
        this.logger = logger;

        Variant = ResolveServerVariant(this.options);

        InstallationDirectory = ResolveInstallationDirectory(this.options.InstallationDirectory);
        logger.LogInformation("[DistributionSynchronisationService] Resolved Installation Directory: {Directory} (Manager Executable: {ManagerPath})",
            InstallationDirectory, ManagerExecutablePath);
    }

    public string InstallationDirectory { get; }

    public string Variant { get; }

    public string? DistributionVersion { get; private set; }

    public string SynchronisationState { get; private set; } = "Pending";

    /// <summary>
    ///     Whether a synchronisation is currently rewriting the installation directory. The supervisor consults this so it
    ///     never launches the manager against a half-rewritten distribution.
    /// </summary>
    public bool IsSynchronising => synchronising;

    /// <summary>
    ///     The path to the manager executable within the installed distribution.
    /// </summary>
    public string ManagerExecutablePath => Path.Combine(InstallationDirectory, HeroesOfNewerthExecutable.FileName);

    /// <summary>
    ///     On Windows, HoN dedicated match servers require at least one whitespace character in the launch path.
    ///     If the directory does not contain whitespace, auto-resolves to a sibling "hon server" junction/folder
    ///     or creates one if missing.
    /// </summary>
    public static string EnsureWhitespacePathOnWindows(string dir)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dir))
            return dir;


        string normalizedDir;
        try
        {
            normalizedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DistributionSynchronisationService] Path normalization failed for '{dir}': {ex.Message}");
            normalizedDir = dir;
        }

        if (HeroesOfNewerthExecutable.CanLaunchInstancesFrom(normalizedDir))
            return normalizedDir;


        try
        {
            string? parent = Path.GetDirectoryName(normalizedDir);
            if (!string.IsNullOrEmpty(parent))
            {
                string whitespaceSibling = Path.Combine(parent, "hon server");
                if (Directory.Exists(whitespaceSibling) && IsValidGameDirectory(whitespaceSibling))
                    return whitespaceSibling;

                try
                {
                    SharedClientAssetLinker.TryCreateDirectoryJunction(whitespaceSibling, normalizedDir, msg => Console.Error.WriteLine($"[Standalone] {msg}"));
                    if (Directory.Exists(whitespaceSibling) && IsValidGameDirectory(whitespaceSibling))
                        return whitespaceSibling;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DistributionSynchronisationService] TryCreateDirectoryJunction '{whitespaceSibling}' -> '{normalizedDir}' failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DistributionSynchronisationService] EnsureWhitespacePathOnWindows failed: {ex.Message}");
        }

        return normalizedDir;
    }

    public static bool IsValidGameDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return false;

        string exePath = Path.Combine(dir, HeroesOfNewerthExecutable.FileName);
        if (!File.Exists(exePath))
            return false;

        // Must contain base or game resources or game binaries to be a runnable installation
        return Directory.Exists(Path.Combine(dir, "base")) ||
               Directory.Exists(Path.Combine(dir, "game")) ||
               File.Exists(Path.Combine(dir, "k2_x64.dll")) ||
               File.Exists(Path.Combine(dir, "k2_x86_64.so")) ||
               File.Exists(Path.Combine(dir, "libk2-x86_64-server.so"));
    }

    // The Distribution Installs Alongside The COMPEL Executable By Default (An Empty Configured Directory), So It Sits Beside The Binary Rather Than In A Peer Folder. A Relative Path Is Resolved Against The Executable's Directory, And A Fully Qualified Path Is Honoured As-Is.
    public static string ResolveInstallationDirectory(string configured)
    {


        string platformSubdir = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

        if (!string.IsNullOrWhiteSpace(configured))
        {
            string resolved = Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
            if (!File.Exists(Path.Combine(resolved, HeroesOfNewerthExecutable.FileName)) &&
                File.Exists(Path.Combine(resolved, platformSubdir, HeroesOfNewerthExecutable.FileName)))
            {
                resolved = Path.Combine(resolved, platformSubdir);
            }

            return EnsureWhitespacePathOnWindows(resolved);
        }



        // 0. Check current working directory first (e.g. launched by orchestrator with WorkingDirectory = games/server)
        string workingDir = Directory.GetCurrentDirectory();
        if (IsValidGameDirectory(workingDir))
            return EnsureWhitespacePathOnWindows(workingDir);

        if (IsValidGameDirectory(Path.Combine(workingDir, platformSubdir)))
            return EnsureWhitespacePathOnWindows(Path.Combine(workingDir, platformSubdir));

        // 1. Check if AppContext.BaseDirectory itself is a valid game directory (contains executable & base/game assets)
        if (IsValidGameDirectory(AppContext.BaseDirectory))
            return EnsureWhitespacePathOnWindows(AppContext.BaseDirectory);

        // 2. Check platform subdirectory inside AppContext.BaseDirectory if it contains full assets
        string basePlatformDir = Path.Combine(AppContext.BaseDirectory, platformSubdir);
        if (IsValidGameDirectory(basePlatformDir))
            return EnsureWhitespacePathOnWindows(basePlatformDir);

        // 3. Check workspace hierarchy for games/server and games/hon server
        string current = AppContext.BaseDirectory;

        // Auto-normalize any user-added *server* folder in parent hierarchy
        string? normalizedServer = ServerFolderNormalizer.NormalizeServerDirectory(current);
        if (!string.IsNullOrEmpty(normalizedServer))
        {
            if (IsValidGameDirectory(normalizedServer))
                return EnsureWhitespacePathOnWindows(normalizedServer);
            if (IsValidGameDirectory(Path.Combine(normalizedServer, platformSubdir)))
                return EnsureWhitespacePathOnWindows(Path.Combine(normalizedServer, platformSubdir));
            if (File.Exists(Path.Combine(normalizedServer, HeroesOfNewerthExecutable.FileName)))
                return EnsureWhitespacePathOnWindows(normalizedServer);
        }

        string[] serverFolderCandidates = OperatingSystem.IsWindows()
            ? ["hon server", "server"]
            : ["server", "hon server"];

        for (int i = 0; i < 10; i++)
        {
            foreach (string folderName in serverFolderCandidates)
            {
                string candidate = Path.Combine(current, "games", folderName);
                if (Directory.Exists(candidate))
                {
                    if (IsValidGameDirectory(candidate))
                        return EnsureWhitespacePathOnWindows(candidate);
                    if (IsValidGameDirectory(Path.Combine(candidate, platformSubdir)))
                        return EnsureWhitespacePathOnWindows(Path.Combine(candidate, platformSubdir));
                    if (File.Exists(Path.Combine(candidate, HeroesOfNewerthExecutable.FileName)))
                        return EnsureWhitespacePathOnWindows(candidate);
                }
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        // 4. Search upwards from working directory as well
        current = workingDir;
        for (int i = 0; i < 10; i++)
        {
            foreach (string folderName in serverFolderCandidates)
            {
                string candidate = Path.Combine(current, "games", folderName);
                if (Directory.Exists(candidate))
                {
                    if (IsValidGameDirectory(candidate))
                        return EnsureWhitespacePathOnWindows(candidate);
                    if (IsValidGameDirectory(Path.Combine(candidate, platformSubdir)))
                        return EnsureWhitespacePathOnWindows(Path.Combine(candidate, platformSubdir));
                    if (File.Exists(Path.Combine(candidate, HeroesOfNewerthExecutable.FileName)))
                        return EnsureWhitespacePathOnWindows(candidate);
                }
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        // 5. Fallback: if executable exists at base even without full subdirs
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, HeroesOfNewerthExecutable.FileName)))
            return EnsureWhitespacePathOnWindows(AppContext.BaseDirectory);

        if (File.Exists(Path.Combine(basePlatformDir, HeroesOfNewerthExecutable.FileName)))
            return EnsureWhitespacePathOnWindows(basePlatformDir);

        return EnsureWhitespacePathOnWindows(AppContext.BaseDirectory);
    }

    /// <summary>
    ///     Completes once the distribution has been synchronised, or once an existing local copy has been accepted when the
    ///     CDN is unreachable or synchronisation is disabled. The supervisor awaits this before launching the manager.
    /// </summary>
    public Task WaitUntilReady(CancellationToken cancellationToken)
    {
        return ready.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        // The Initial Synchronisation Can Be Disabled For Development And Testing: The Existing Local Distribution Is Used, And On-Demand Synchronisation Via The Control Plane Still Works.
        if (options.Synchronisation is false)
        {
            logger.LogInformation(
                "Initial CDN Synchronisation Is Disabled; Proceeding With The Existing Local Distribution");

            SynchronisationState = "Disabled";

            ready.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                logger.LogDebug(ex, "[DistributionSynchronisationService] Disabled hold loop canceled.");
            }

            return;
        }

        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                SynchronisationSummary summary = await SynchroniseNow(stoppingToken).ConfigureAwait(false);

                // A Synchronisation That Reports No Exception Can Still Have Failed To Fetch Individual Files, Which Would Leave A Mixed-Version Tree; Only Stop Retrying Once Every File Transferred And The Manager Executable Is Present.
                if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                    break;

                logger.LogWarning(
                    "Synchronisation Did Not Fully Complete ({Failures} File(s) Failed); Retrying In {Seconds} Seconds",
                    summary.FilesFailed, RetryDelay.TotalSeconds);
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception exception)
            {
                logger.LogError(exception, "Distribution Synchronisation Failed");

                // If A Previous Synchronisation Already Installed The Manager, Proceed With It Rather Than Blocking The Manager Launch On A Transient CDN Outage.
                if (File.Exists(ManagerExecutablePath))
                {
                    logger.LogWarning("Proceeding With The Existing Local Distribution At {InstallationDirectory}",
                        InstallationDirectory);

                    ready.TrySetResult();

                    break;
                }

                logger.LogWarning("No Local Distribution Is Present; Retrying Synchronisation In {Seconds} Seconds",
                    RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // Remain Alive So The Control Plane Can Resolve This Singleton For On-Demand Synchronisations And Status Reporting.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "[DistributionSynchronisationService] Singleton hold loop canceled.");
        }
    }

    /// <summary>
    ///     Fetches the manifest and synchronises the installation directory. Safe to call concurrently; calls are serialised.
    /// </summary>
    public async Task<SynchronisationSummary> SynchroniseNow(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        synchronising = true;

        try
        {
            SynchronisationState = "Synchronising";

            logger.LogInformation("Synchronising Match Server Distribution {Variant} From {Host} Into {Directory}",
                Variant, options.Host, InstallationDirectory);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken)
                .ConfigureAwait(false);

            PkvValidationResult pkvValidation = PkvManifestValidator.Verify(manifest);
            if (!pkvValidation.IsValid)
            {
                logger.LogError("[PKV-GUARD] Match server manifest from {Host} rejected: {Reason}. Remediation: {Remediation}",
                    options.Host, pkvValidation.Reason, pkvValidation.RemediationCommand);
                SynchronisationState = $"Failed: {pkvValidation.Reason}";
                throw new InvalidOperationException($"[PKV-GUARD] Match server manifest rejected: {pkvValidation.Reason}");
            }

            DistributionVersion = manifest.Version;

            Progress<SynchronisationEvent> progress = new(LogSynchronisationEvent);

            SynchronisationSummary summary = await ContentBroker.Synchronise(manifest, Variant, InstallationDirectory,
                    options.Host, options.ParallelTransfers, OwnFileProtectionPatterns, progress, cancellationToken)
                .ConfigureAwait(false);

            SynchronisationState = summary.FilesFailed is 0
                ? "Up To Date"
                : $"Completed With {summary.FilesFailed} Failure(s)";

            if (summary.FilesFailed > 0)
            {
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);

                // The Lock Scan Is Diagnostic Only, So A Failure Inside It Must Not Turn A Partial Synchronisation Into An Exception
                try { LogLockingProcesses(summary.Failures); }
                catch (Exception exception) { logger.LogWarning("FAIL: The Lock Scan Failed :: {ExceptionType} :: {Message}", exception.GetType().Name, exception.Message); }
            }

            // Release Consumers Only Once Every File Transferred And The Manager Executable Is Present. A Synchronisation That Failed To Fetch Some Files Would Leave A Mixed-Version Tree, So The Manager Must Not Be Launched Against It.
            if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                ready.TrySetResult();

            return summary;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SynchronisationState = $"Failed: {exception.Message}";

            throw;
        }

        finally
        {
            synchronising = false;

            gate.Release();
        }
    }

    private void LogSynchronisationEvent(SynchronisationEvent synchronisationEvent)
    {
        switch (synchronisationEvent.Kind)
        {
            case SynchronisationEventKind.PlanReady:
                announcedTotalBytes = synchronisationEvent.Plan?.TotalBytesToDownload ?? 0;
                lastProgressLogTicks = 0;
                logger.LogInformation("Synchronisation Plan: {Plan}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.ProgressUpdated:
                LogProgress(synchronisationEvent.Size);
                break;

            case SynchronisationEventKind.Downloaded:
                logger.LogDebug("Downloaded {Path} ({Size:N0} Bytes)", synchronisationEvent.Detail,
                    synchronisationEvent.Size);
                break;

            case SynchronisationEventKind.Deleted:
                logger.LogDebug("Deleted {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.DownloadFailed:
            case SynchronisationEventKind.DeletionFailed:
                logger.LogWarning("{Detail}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Completed:
                logger.LogInformation("Synchronisation Complete: {Summary}", synchronisationEvent.Detail);
                break;
        }
    }

    // Logs A Throttled Progress Line During A Long Initial Synchronisation So The Operator Sees Movement Between The Plan And The Completion Lines, Without Flooding The Log.
    private void LogProgress(long bytesDownloaded)
    {
        if (announcedTotalBytes <= 0)
            return;

        long now = Environment.TickCount64;

        if (lastProgressLogTicks is not 0 && now - lastProgressLogTicks < 10_000)
            return;

        lastProgressLogTicks = now;

        double percentage = Math.Min(100.0, bytesDownloaded * 100.0 / announcedTotalBytes);

        logger.LogInformation("Synchronising: {Downloaded:N0} Of {Total:N0} Bytes ({Percentage:F0}%)", bytesDownloaded,
            announcedTotalBytes, percentage);
    }

    private void LogLockingProcesses(IReadOnlyList<SynchronisationFailure> failures)
    {
        const string unidentifiedProcessGroup = "Unidentified Process";

        Dictionary<string, LockGroup> groups = new(StringComparer.OrdinalIgnoreCase);

        LockGroup GroupFor(string applicationName)
        {
            if (groups.TryGetValue(applicationName, out LockGroup? group) is false)
            {
                group = new LockGroup();
                groups.Add(applicationName, group);
            }

            return group;
        }

        foreach (SynchronisationFailure failure in failures)
        {
            string absolutePath = Path.IsPathRooted(failure.Path)
                ? failure.Path
                : Path.GetFullPath(Path.Combine(InstallationDirectory, failure.Path));

            if (File.Exists(absolutePath) is false)
                continue;

            string displayPath = Path.GetRelativePath(InstallationDirectory, absolutePath);

            List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(absolutePath);

            if (lockingProcesses.Count is 0)
            {
                if (FileIsLocked(absolutePath))
                    GroupFor(unidentifiedProcessGroup).FilePaths.Add(displayPath);

                continue;
            }

            foreach (FileLockingProcess lockingProcess in lockingProcesses)
            {
                LockGroup group = GroupFor(lockingProcess.ApplicationName);
                group.ProcessIDs.Add(lockingProcess.ProcessID);
                group.FilePaths.Add(displayPath);
            }
        }

        foreach ((string applicationName, LockGroup group) in groups)
        {
            string processName = group.ProcessIDs.Count > 1
                ? $"{applicationName} ({group.ProcessIDs.Count} Processes)"
                : applicationName;

            logger.LogWarning("FAIL: {ProcessName} Holds {Files}", processName, string.Join(", ", group.FilePaths));
        }
    }

    private static bool FileIsLocked(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class LockGroup
    {
        public HashSet<int> ProcessIDs { get; } = [];
        public HashSet<string> FilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns the distribution variant code that matches the current operating system.
    /// </summary>
    public static string ResolveServerVariant(CDNOptions options)
    {
        return OperatingSystem.IsWindows() ? options.WindowsVariant
            : OperatingSystem.IsLinux() ? options.LinuxVariant
            : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");
    }
}
