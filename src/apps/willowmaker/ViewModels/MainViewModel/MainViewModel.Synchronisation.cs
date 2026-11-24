// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using NEXUS.Contracts.Cdn;
using Polly.CircuitBreaker;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronisationIsIdle))]
    [NotifyPropertyChangedFor(nameof(CanCheckForCdnUpdates))]
    [NotifyPropertyChangedFor(nameof(CanDownloadCdnUpdate))]
    [NotifyPropertyChangedFor(nameof(DownloadCdnButtonText))]
    [NotifyPropertyChangedFor(nameof(MasterServerInputIsEnabled))]
    [NotifyPropertyChangedFor(nameof(CanLaunchMapEditor))]
    [NotifyPropertyChangedFor(nameof(CanLaunchGameClient))]
    public partial bool SynchronisationIsActive { get; set; } = false;

    [ObservableProperty] public partial bool SynchronisationIsScheduled { get; set; } = false;

    [ObservableProperty] public partial double SynchronisationProgressPercent { get; set; } = 0;

    [ObservableProperty] public partial string SynchronisationStatusMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial bool SynchronisationIsFailed { get; set; } = false;

    [ObservableProperty] public partial string DownloadedFilesDisplay { get; set; } = string.Empty;

    [ObservableProperty] public partial string RemovedFilesDisplay { get; set; } = string.Empty;

    [ObservableProperty] public partial string SkippedFilesDisplay { get; set; } = string.Empty;

    [ObservableProperty] public partial string UpToDateFilesDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadCdnButtonText))]
    public partial string InstalledBundleVersion { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadCdnButtonText))]
    [NotifyPropertyChangedFor(nameof(CanDownloadCdnUpdate))]
    public partial string AvailableCdnVersion { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadCdnUpdate))]
    [NotifyPropertyChangedFor(nameof(DownloadCdnButtonText))]
    public partial bool HasAvailableCdnUpdate { get; set; } = false;

    public bool SynchronisationIsIdle => SynchronisationIsActive is false;

    public bool CanDownloadCdnUpdate =>
        HasAvailableCdnUpdate &&
        SynchronisationIsIdle &&
        !LaunchIsInProgress &&
        LocationSafetyVerdict is LocationSafetyVerdict.Safe;

    public string DownloadCdnButtonText =>
        HasAvailableCdnUpdate
            ? (!string.IsNullOrWhiteSpace(AvailableCdnVersion) ? $"Download Update (v{AvailableCdnVersion})" : "Download Update")
            : (!string.IsNullOrWhiteSpace(InstalledBundleVersion) ? $"Up to Date (v{InstalledBundleVersion})" : "Up to Date");

    /// <summary>
    ///     Determines if the manual CDN update check button should be enabled.
    ///     Requires a valid master server address to be selected, synchronisation to be idle, and no launch in progress.
    /// </summary>
    public bool CanCheckForCdnUpdates =>
        MasterServerAddressIsValid &&
        !string.IsNullOrWhiteSpace(TryGetMasterServerComboText() ?? LoadedMasterServerAddress) &&
        !IsOfflineModeSelection(TryGetMasterServerComboText() ?? LoadedMasterServerAddress) &&
        !string.Equals(SelectedCDNAddressItem?.TargetURL, "offline", StringComparison.OrdinalIgnoreCase) &&
        SynchronisationIsIdle &&
        !LaunchIsInProgress;

    /// <summary>
    ///     Manually checks the CDN for game updates in a strictly read-only inspection pass.
    ///     Per user directive, update checks and probes NEVER download or modify files automatically;
    ///     if updates exist, the 'Download Update' button becomes active for the player to explicitly trigger.
    /// </summary>
    [RelayCommand]
    public async Task CheckForCdnUpdates()
    {
        if (!CanCheckForCdnUpdates)
            return;

        Log(LogCategory.Synchronise, "INIT: Manual CDN update check initiated by user (read-only)...");
        await InspectCdnUpdateAsync(silent: false);
    }

    /// <summary>
    ///     Prompts the user to confirm downloading the pending CDN update.
    ///     Per user directive, this is the ONLY action that downloads files or touches disk.
    /// </summary>
    [RelayCommand]
    public async Task DownloadCdnUpdate()
    {
        if (!CanDownloadCdnUpdate)
            return;

        Log(LogCategory.Synchronise, $"INIT: User clicked Download Update (Target: v{AvailableCdnVersion})...");
        bool success = await SynchroniseContent(skipSynchronisation: false, manualCheck: true, requireExplicitConfirmation: true);
        if (success)
        {
            await InspectCdnUpdateAsync(silent: true);
        }
    }

    /// <summary>
    ///     Optional test or custom delegate to prompt for update confirmation before downloading files.
    ///     If null, defaults to showing the interactive <see cref="UpdateDialog"/> on desktop MainWindow.
    /// </summary>
    public Func<SynchronisationPlan, Task<bool>>? ConfirmUpdatePrompt { get; set; }

    public HttpMessageHandler? CdnHttpHandlerOverride { get; set; }
    public string? BundleRootOverride { get; set; }
    public Func<string, IReadOnlyList<string>>? RunningBundleProcessDetectorOverride { get; set; }
    public string PrimaryEmergencyCdnBaseUrl { get; set; } = "https://cdn.newbs.io/";
    public string SecondaryEmergencyCdnBaseUrl { get; set; } = "https://masterserver.newbs.io/cdn/";
    public bool AllowUnsignedLoopbackCdnManifestsForDev { get; set; } = false;
    public IReadOnlyCollection<string>? AllowedCdnDomainsOverride { get; set; }

    public static bool IsLoopbackCdnEndpoint(string cdnBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(cdnBaseUrl) || !Uri.TryCreate(cdnBaseUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLocalGameClientFiles(string? bundleRoot)
    {
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            string bundleClient = Path.Combine(bundleRoot, "games", "client");
            if (Directory.Exists(bundleClient))
                return true;
        }

        string? resolved = LocationGuard.ResolveGameClientDirectory();
        return !string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved);
    }
    public void SetLocationSafetyVerdictForTesting(LocationSafetyVerdict verdict) => LocationSafetyVerdict = verdict;
    public string? CdnPublicKeyXHexOverride { get; set; }
    public string? CdnPublicKeyYHexOverride { get; set; }

    private static readonly HashSet<string> KnownBundleProcessStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "hon_x64",
        "hon-x86_64",
        "hon-x86",
        "HoN64",
        "hon_server",
        "hon-x86_64-server",
        "hon-x86_64-server.bin",
        "COMPEL",
        "COMPEL.GUI"
    };

    /// <summary>
    ///     Resolves the unified bundle root (the parent directory of 'games/') whether WILLOWMAKER
    ///     runs from Windows root, a launcher subdirectory, or inside the client folder.
    /// </summary>
    public static string? ResolveBundleRoot(string? startingDirectory = null)
    {
        string start = !string.IsNullOrWhiteSpace(startingDirectory)
            ? startingDirectory
            : (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory) ? AppContext.BaseDirectory : Environment.CurrentDirectory);

        string? cur = Path.GetFullPath(start);
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
        {
            string candidateGamesClient = Path.Combine(cur, "games", "client");
            if (Directory.Exists(candidateGamesClient))
            {
                return Path.GetFullPath(cur);
            }

            DirectoryInfo dirInfo = new(cur);
            if (dirInfo.Name.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo? gamesParent = dirInfo.Parent;
                if (gamesParent != null && gamesParent.Name.Equals("games", StringComparison.OrdinalIgnoreCase))
                {
                    if (gamesParent.Parent != null)
                        return Path.GetFullPath(gamesParent.Parent.FullName);
                }
            }
            else if (dirInfo.Name.Equals("windows-x64", StringComparison.OrdinalIgnoreCase) ||
                     dirInfo.Name.Equals("linux-x64", StringComparison.OrdinalIgnoreCase) ||
                     dirInfo.Name.Equals("macos-x64", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo? clientParent = dirInfo.Parent;
                if (clientParent != null && clientParent.Name.Equals("client", StringComparison.OrdinalIgnoreCase))
                {
                    DirectoryInfo? gamesGrandparent = clientParent.Parent;
                    if (gamesGrandparent != null && gamesGrandparent.Name.Equals("games", StringComparison.OrdinalIgnoreCase))
                    {
                        if (gamesGrandparent.Parent != null)
                            return Path.GetFullPath(gamesGrandparent.Parent.FullName);
                    }
                }
            }

            string? parent = Directory.GetParent(cur)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent.Equals(cur, StringComparison.OrdinalIgnoreCase))
                break;
            cur = parent;
        }

        if (string.IsNullOrWhiteSpace(startingDirectory))
        {
            string? clientDir = LocationGuard.ResolveGameClientDirectory();
            if (!string.IsNullOrEmpty(clientDir))
            {
                DirectoryInfo clientDirInfo = new(clientDir);
                if (clientDirInfo.Name.Equals("client", StringComparison.OrdinalIgnoreCase) &&
                    clientDirInfo.Parent?.Name.Equals("games", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (clientDirInfo.Parent.Parent != null)
                        return Path.GetFullPath(clientDirInfo.Parent.Parent.FullName);
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Detects any active game client, game server, or editor processes running from within the bundle root.
    /// </summary>
    public static IReadOnlyList<string> DetectRunningBundleProcesses(
        string bundleRoot,
        IEnumerable<(int Pid, string ProcessName, string? ExecutablePath)>? processSnapshotOverride = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot))
            return [];

        string bundleRootFull = Path.GetFullPath(bundleRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        StringComparison comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        List<string> results = [];

        if (processSnapshotOverride is not null)
        {
            foreach (var (pid, pName, exePath) in processSnapshotOverride)
            {
                if (string.IsNullOrWhiteSpace(pName) || string.IsNullOrWhiteSpace(exePath))
                    continue;

                if (!KnownBundleProcessStems.Contains(pName) &&
                    !KnownBundleProcessStems.Contains(Path.GetFileNameWithoutExtension(pName)))
                {
                    continue;
                }

                string fullExe = Path.GetFullPath(exePath);
                if (fullExe.StartsWith(bundleRootFull, comparison))
                {
                    results.Add($"{pName} (PID {pid})");
                }
            }
        }
        else
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                string msg = $"[MainViewModel] DetectRunningBundleProcesses failed to enumerate system processes: {ex.Message}";
                log?.Invoke(msg);
                Trace.WriteLine(msg);
                return [];
            }

            try
            {
                foreach (Process proc in processes)
                {
                    try
                    {
                        string pName = proc.ProcessName;
                        if (!KnownBundleProcessStems.Contains(pName) &&
                            !KnownBundleProcessStems.Contains(Path.GetFileNameWithoutExtension(pName)))
                        {
                            continue;
                        }

                        string? exePath = null;
                        try
                        {
                            exePath = proc.MainModule?.FileName;
                        }
                        catch (Exception ex)
                        {
                            string msg = $"[MainViewModel] MainModule query skipped for '{pName}' (PID {proc.Id}): {ex.Message}";
                            log?.Invoke(msg);
                            Trace.WriteLine(msg);
                        }

                        if (string.IsNullOrWhiteSpace(exePath) && OperatingSystem.IsLinux())
                        {
                            try
                            {
                                exePath = File.ResolveLinkTarget($"/proc/{proc.Id}/exe", returnFinalTarget: true)?.FullName;
                            }
                            catch (Exception ex)
                            {
                                string msg = $"[MainViewModel] /proc/{proc.Id}/exe symlink query skipped for '{pName}': {ex.Message}";
                                log?.Invoke(msg);
                                Trace.WriteLine(msg);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            string fullExe = Path.GetFullPath(exePath);
                            if (fullExe.StartsWith(bundleRootFull, comparison))
                            {
                                if (TryClassifyAndReapKernelZombieProcess(proc, pName, log))
                                {
                                    continue;
                                }

                                results.Add($"{pName} (PID {proc.Id})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string msg = $"[MainViewModel] Process inspection skipped during bundle scan: {ex.Message}";
                        log?.Invoke(msg);
                        Trace.WriteLine(msg);
                    }
                }
            }
            finally
            {
                foreach (Process proc in processes)
                {
                    try
                    {
                        proc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        string msg = $"[MainViewModel] Process handle disposal warning: {ex.Message}";
                        log?.Invoke(msg);
                        Trace.WriteLine(msg);
                    }
                }
            }
        }

        return results.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Detects unkillable 1-thread windowless kernel zombie game processes (e.g., stuck waiting on a graphics/audio driver IRP)
    ///     by checking for MainWindowHandle == 0, Threads.Count &lt;= 1, attempting a logged Kill(), and verifying zero CPU progress
    ///     over a 500ms WaitForExit probe. Returns true only when the process terminated or is proven to be a dead kernel zombie.
    /// </summary>
    private static bool TryClassifyAndReapKernelZombieProcess(Process proc, string processName, Action<string>? log)
    {
        try
        {
            if (proc.HasExited)
            {
                return true;
            }

            if (proc.MainWindowHandle != IntPtr.Zero || proc.Threads.Count > 1)
            {
                return false;
            }

            TimeSpan cpuBefore;
            try
            {
                cpuBefore = proc.TotalProcessorTime;
            }
            catch (Exception ex)
            {
                string cpuMsg = $"[ZOMBIE-CHECK] Could not read initial CPU time for '{processName}' (PID {proc.Id}): {ex.Message}";
                log?.Invoke(cpuMsg);
                Trace.WriteLine(cpuMsg);
                return false;
            }

            try
            {
                proc.Kill(entireProcessTree: false);
                log?.Invoke($"[ZOMBIE-CHECK] Issued Kill() to windowless 1-thread candidate '{processName}' (PID {proc.Id}).");
            }
            catch (Exception killEx)
            {
                string killMsg = $"[ZOMBIE-CHECK] Kill() on candidate '{processName}' (PID {proc.Id}) threw {killEx.GetType().Name}: {killEx.Message}";
                log?.Invoke(killMsg);
                Trace.WriteLine(killMsg);
            }

            bool exited = false;
            try
            {
                exited = proc.WaitForExit(500);
            }
            catch (Exception waitEx)
            {
                string waitMsg = $"[ZOMBIE-CHECK] WaitForExit(500) on '{processName}' (PID {proc.Id}) threw {waitEx.GetType().Name}: {waitEx.Message}";
                log?.Invoke(waitMsg);
                Trace.WriteLine(waitMsg);
            }

            if (exited || proc.HasExited)
            {
                log?.Invoke($"[ZOMBIE-CHECK] Windowless 1-thread '{processName}' (PID {proc.Id}) terminated cleanly after Kill().");
                return true;
            }

            proc.Refresh();
            if (proc.MainWindowHandle != IntPtr.Zero || proc.Threads.Count > 1)
            {
                return false;
            }

            TimeSpan cpuAfter = proc.TotalProcessorTime;
            if ((cpuAfter - cpuBefore) == TimeSpan.Zero)
            {
                string zombieWarn =
                    $"[ZOMBIE-PROCESS] Ignoring unkillable 1-thread windowless kernel zombie '{processName}' (PID {proc.Id}, 0ms CPU delta over 500ms after Kill). " +
                    "Reboot Windows when convenient to release lingering kernel file handles.";
                log?.Invoke(zombieWarn);
                Trace.WriteLine(zombieWarn);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            string msg = $"[ZOMBIE-CHECK] Unexpected error inspecting '{processName}': {ex.Message}";
            log?.Invoke(msg);
            Trace.WriteLine(msg);
            return false;
        }
    }

    /// <summary>
    ///     Returns true only if the given master server address is an official CDN-backed master server.
    ///     Localhost, loopback/LAN addresses, newbs.io (direct community launch), and custom endpoints return false.
    /// </summary>
    public static bool IsOfficialCdnMasterServer(string? serverAddress)
    {
        if (!EndpointPairRegistry.IsKongorNetPkvEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(serverAddress))
            return false;

        string trimmed = serverAddress.Trim();

        // Bypass local and loopback addresses
        if (trimmed.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("::1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Bypass newbs.io (fast direct community server)
        if (trimmed.Contains("newbs.io", StringComparison.OrdinalIgnoreCase))
            return false;

        // Custom Address placeholder
        if (trimmed.Contains("Custom Address", StringComparison.OrdinalIgnoreCase))
            return false;

        // Whitelist official CDN-enabled master server endpoints
        return trimmed.Contains("api.kongor.net", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("masterserver.kongor.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolves the currently installed bundle version.
    ///     Checks the local CDN install ledger, the playtest build manifest, or falls back to the platform version.
    /// </summary>
    public string ResolveInstalledBundleVersion()
    {
        string? bundleRoot = BundleRootOverride ?? ResolveBundleRoot();
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            string ledgerPath = Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json");
            if (File.Exists(ledgerPath))
            {
                try
                {
                    CdnInstallLedger ledger = CdnInstallLedger.LoadOrEmpty(ledgerPath);
                    if (ledger.MaxSequenceSeen > 0 && !string.IsNullOrWhiteSpace(ledger.Version))
                    {
                        return ledger.Version.TrimStart('v', 'V');
                    }
                }
                catch (Exception ex)
                {
                    Log(LogCategory.Synchronise, $"[InstallLedger] Warning: Failed reading ledger '{ledgerPath}': {ex.Message}");
                }
            }

            BuildManifestDto? buildManifest = PlaytestPolicyService.TryReadBuildManifest(bundleRoot);
            if (buildManifest is not null)
            {
                if (!string.IsNullOrWhiteSpace(buildManifest.Version))
                    return buildManifest.Version.TrimStart('v', 'V');

                if (!string.IsNullOrWhiteSpace(buildManifest.BuildId))
                {
                    string stem = buildManifest.BuildId.TrimStart('v', 'V');
                    int dash = stem.IndexOf('-');
                    if (dash > 0)
                        return stem[..dash];
                    return stem;
                }
            }
        }

        return NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion;
    }

    /// <summary>
    ///     Resets CDN update indicators and buttons to offline / up-to-date state.
    /// </summary>
    public void ResetCdnUpdateStateToOffline()
    {
        SynchronizationContext? callerSyncCtx = SynchronizationContext.Current ?? _uiSyncContext;
        void Apply()
        {
            InstalledBundleVersion = ResolveInstalledBundleVersion();
            AvailableCdnVersion = InstalledBundleVersion;
            SynchronisationIsFailed = false;
            SynchronisationStatusMessage = string.Empty;
            HasAvailableCdnUpdate = false;
        }

        RunOnUiThread(Apply, callerSyncCtx);
    }

    /// <summary>
    ///     Performs a strictly read-only inspection of the CDN manifest.
    ///     Downloads 0 CAS objects, performs 0 disk writes, and compares local installed version
    ///     against the available CDN version. Updates <see cref="HasAvailableCdnUpdate"/> and
    ///     <see cref="CDNProbe.StatusMessage"/> accordingly.
    /// </summary>
    public async Task<SynchronisationPlan?> InspectCdnUpdateAsync(bool silent = false)
    {
        if (SynchronisationIsActive || LaunchIsInProgress)
            return null;

        CDNProbe.CancelPending();

        string currentServer = TryGetMasterServerComboText() ?? LoadedMasterServerAddress ?? string.Empty;
        string? targetUrl = SelectedCDNAddressItem?.TargetURL;
        string? displayText = SelectedCDNAddressItem?.DisplayText;

        bool isOffline = string.Equals(targetUrl, "offline", StringComparison.OrdinalIgnoreCase) ||
            (displayText?.Contains("Offline Mode", StringComparison.OrdinalIgnoreCase) ?? false) ||
            IsOfflineModeSelection(currentServer);

        if (isOffline)
        {
            ResetCdnUpdateStateToOffline();
            return null;
        }

        InstalledBundleVersion = ResolveInstalledBundleVersion();

        string? bundleRoot = BundleRootOverride ?? ResolveBundleRoot();
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            IReadOnlyList<string> runningProcesses = RunningBundleProcessDetectorOverride is not null
                ? RunningBundleProcessDetectorOverride(bundleRoot)
                : DetectRunningBundleProcesses(bundleRoot, log: silent ? null : msg => Log(LogCategory.Synchronise, msg));

            if (runningProcesses.Count > 0)
            {
                if (silent)
                {
                    // Silent background probe during active gameplay / tool session:
                    // Gracefully skip background inspection without emitting error logs or failing status.
                    return null;
                }

                Log(LogCategory.Synchronise, $"FAIL: Active game/tool process(es) detected in bundle '{bundleRoot}': {string.Join(", ", runningProcesses)}. Close Heroes of Newerth / COMPEL before checking for updates.");
                SynchronisationIsFailed = true;
                SynchronisationStatusMessage = "Close Heroes of Newerth / COMPEL before checking for updates";
                return null;
            }
        }

        try
        {
            string variant = ResolveDefaultClientVariant();

            // Locate CDN endpoint
            string cdnBaseUrl;
            Manifest manifest;

            bool isLocalAuto = IsLocalOfflineAutoSelection(currentServer) ||
                (displayText?.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) ?? false);

            if (isLocalAuto)
            {
                bool localListening = await LocalMasterServerPortProbe(55555, 250);
                List<string> candidateRawList = [];
                if (localListening)
                {
                    string localCdn = BuildCDNAddress();
                    if (!string.IsNullOrWhiteSpace(localCdn))
                        candidateRawList.Add(localCdn);
                    candidateRawList.Add(PrimaryEmergencyCdnBaseUrl);
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                        candidateRawList.Add(SecondaryEmergencyCdnBaseUrl);
                }
                else
                {
                    candidateRawList.Add(PrimaryEmergencyCdnBaseUrl);
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                        candidateRawList.Add(SecondaryEmergencyCdnBaseUrl);
                }

                List<string> candidates = [];
                foreach (string raw in candidateRawList)
                {
                    string norm = AddressValidation.NormaliseCDNURL(raw);
                    if (!string.IsNullOrWhiteSpace(norm) && !candidates.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(norm);
                    }
                }

                manifest = null!;
                cdnBaseUrl = candidates.Count > 0 ? candidates[0] : PrimaryEmergencyCdnBaseUrl;

                using HttpClient preflightClient = CdnHttpHandlerOverride is not null
                    ? new HttpClient(CdnHttpHandlerOverride, disposeHandler: false)
                    : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
                preflightClient.Timeout = TimeSpan.FromSeconds(5);

                for (int i = 0; i < candidates.Count; i++)
                {
                    string candidate = candidates[i];
                    try
                    {
                        ContentBroker.ValidateAllowedCdnBaseUrl(
                            candidate,
                            AllowedCdnDomainsOverride,
                            allowInMemoryTestLocal: CdnHttpHandlerOverride is not null);
                        string candidateManifestUrl = $"{(candidate.EndsWith('/') ? candidate : candidate + '/')}{Uri.EscapeDataString(variant)}/manifest.json";
                        using HttpRequestMessage preflightReq = new(HttpMethod.Get, candidateManifestUrl);
                        using HttpResponseMessage preflightResp = await preflightClient.SendAsync(preflightReq, HttpCompletionOption.ResponseHeadersRead);
                        preflightResp.EnsureSuccessStatusCode();

                        await using Stream manifestStream = await preflightResp.Content.ReadAsStreamAsync();
                        manifest = await JsonSerializer.DeserializeAsync(manifestStream, ManifestJSONContext.Default.Manifest)
                            ?? throw new InvalidOperationException($@"Manifest at ""{candidateManifestUrl}"" deserialised to null.");

                        cdnBaseUrl = candidate;
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or BrokenCircuitException or TaskCanceledException)
                    {
                        if (i == candidates.Count - 1)
                        {
                            throw;
                        }
                    }
                }
            }
            else
            {
                cdnBaseUrl = BuildCDNAddress();
                if (string.IsNullOrWhiteSpace(cdnBaseUrl))
                {
                    ResetCdnUpdateStateToOffline();
                    return null;
                }

                bool isNewbsPrimaryCdn = cdnBaseUrl.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase);
                if (isNewbsPrimaryCdn)
                {
                    List<string> newbsCandidates = [AddressValidation.NormaliseCDNURL(cdnBaseUrl)];
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                    {
                        string secondaryNorm = AddressValidation.NormaliseCDNURL(SecondaryEmergencyCdnBaseUrl);
                        if (!string.IsNullOrWhiteSpace(secondaryNorm) &&
                            !newbsCandidates.Contains(secondaryNorm, StringComparer.OrdinalIgnoreCase))
                        {
                            newbsCandidates.Add(secondaryNorm);
                        }
                    }

                    manifest = null!;
                    using HttpClient preflightClient = CdnHttpHandlerOverride is not null
                        ? new HttpClient(CdnHttpHandlerOverride, disposeHandler: false)
                        : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
                    preflightClient.Timeout = TimeSpan.FromSeconds(5);

                    for (int i = 0; i < newbsCandidates.Count; i++)
                    {
                        string candidate = newbsCandidates[i];
                        try
                        {
                            ContentBroker.ValidateAllowedCdnBaseUrl(
                                candidate,
                                AllowedCdnDomainsOverride,
                                allowInMemoryTestLocal: CdnHttpHandlerOverride is not null);
                            string candidateManifestUrl = $"{(candidate.EndsWith('/') ? candidate : candidate + '/')}{Uri.EscapeDataString(variant)}/manifest.json";
                            using HttpRequestMessage preflightReq = new(HttpMethod.Get, candidateManifestUrl);
                            using HttpResponseMessage preflightResp = await preflightClient.SendAsync(preflightReq, HttpCompletionOption.ResponseHeadersRead);
                            preflightResp.EnsureSuccessStatusCode();

                            await using Stream manifestStream = await preflightResp.Content.ReadAsStreamAsync();
                            manifest = await JsonSerializer.DeserializeAsync(manifestStream, ManifestJSONContext.Default.Manifest)
                                ?? throw new InvalidOperationException($@"Manifest at ""{candidateManifestUrl}"" deserialised to null.");

                            cdnBaseUrl = candidate;
                            break;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or BrokenCircuitException or TaskCanceledException)
                        {
                            if (i < newbsCandidates.Count - 1)
                            {
                                string nextCandidate = newbsCandidates[i + 1];
                                Log(LogCategory.Synchronise, $"FALLBACK: '{candidate}' unreachable ({ex.Message}); retrying '{nextCandidate}'");
                            }
                            else if (HasLocalGameClientFiles(bundleRoot))
                            {
                                Log(LogCategory.Synchronise, $"[CDN FALLBACK] Remote Newbs CDNs unreachable ({ex.Message}); degrading gracefully to local client files (v{InstalledBundleVersion}) with zero circuit-breaker trip.");
                                ResetCdnUpdateStateToOffline();
                                SynchronisationIsFailed = false;
                                SynchronisationStatusMessage = string.Empty;
                                CDNProbe.SetReadyStatus($"Local Files Ready (v{InstalledBundleVersion}) — Remote CDN Unavailable");
                                return null;
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
                else
                {
                    manifest = await ContentBroker.FetchManifest(
                        variant,
                        baseURL: cdnBaseUrl,
                        httpHandlerOverride: CdnHttpHandlerOverride,
                        allowedDomains: AllowedCdnDomainsOverride);
                }
            }

            bool isLoopbackCdn = IsLoopbackCdnEndpoint(cdnBaseUrl);
            bool requireSignature = !isLoopbackCdn || !AllowUnsignedLoopbackCdnManifestsForDev;

            PlaytestPolicyDto playtestPolicy = PlaytestPolicyService.Resolve(
                bundleRoot ?? Environment.CurrentDirectory,
                msg => Log(LogCategory.Synchronise, msg));

            PkvValidationResult pkvValidation = PkvManifestValidator.Verify(
                manifest,
                requireSignature: requireSignature,
                publicKeyXHexOverride: CdnPublicKeyXHexOverride,
                publicKeyYHexOverride: CdnPublicKeyYHexOverride);

            if (!pkvValidation.IsValid)
            {
                if (isLoopbackCdn && !playtestPolicy.IsPlaytest && manifest.Files.Count == 0)
                {
                    Log(LogCategory.Synchronise,
                        $"[PKV-DEV] Local loopback CDN '{cdnBaseUrl}' returned empty unsigned manifest (v{manifest.Version}, 0 files); keeping Local Dev manual update status.");
                    RunOnUiThread(() =>
                    {
                        HasAvailableCdnUpdate = false;
                        SynchronisationIsFailed = false;
                        SynchronisationStatusMessage = "Local Dev Server (Manual Updates)";
                        CDNProbe.SetReadyStatus("Local Dev Server (Manual Updates)");
                    }, SynchronizationContext.Current ?? _uiSyncContext);
                    return null;
                }

                string warnMsg = $"[PKV-GUARD] Blocked update from '{cdnBaseUrl}': {pkvValidation.Reason} (Remediation: {pkvValidation.RemediationCommand})";
                Log(LogCategory.Synchronise, warnMsg);
                HasAvailableCdnUpdate = false;
                SynchronisationIsFailed = true;
                SynchronisationStatusMessage = "PKV Guard — Updates Blocked (Non-PKV Server)";
                CDNProbe.SetReadyStatus("PKV Guard — Updates Blocked (Non-PKV Server)");
                return null;
            }

            string syncTargetDirectory = manifest.IsOverlay && !string.IsNullOrWhiteSpace(bundleRoot)
                ? bundleRoot
                : (BundleRootOverride ?? Environment.CurrentDirectory);

            bool enforceAllowlist = true;
            string? ledgerPath = !string.IsNullOrWhiteSpace(bundleRoot)
                ? Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json")
                : null;

            SynchronisationPlan plan = await Task.Run(() => ContentBroker.InspectUpdatePlan
            (
                manifest,
                variant,
                syncTargetDirectory,
                allowDeletions: false,
                requireSignature: requireSignature,
                publicKeyXHexOverride: CdnPublicKeyXHexOverride,
                publicKeyYHexOverride: CdnPublicKeyYHexOverride,
                enforceEmergencyBundleAllowlist: enforceAllowlist,
                installLedgerPath: ledgerPath
            ));

            string installedVer = InstalledBundleVersion;
            string remoteVer = !string.IsNullOrWhiteSpace(manifest.Version) ? manifest.Version : installedVer;
            AvailableCdnVersion = remoteVer;

            bool updateAvailable = plan.FilesToDownload > 0 || plan.FilesToDelete > 0;
            HasAvailableCdnUpdate = updateAvailable;

            void ApplyInspectionResult()
            {
                SynchronisationIsFailed = false;
                SynchronisationStatusMessage = string.Empty;

                if (updateAvailable)
                {
                    string sizeStr = plan.TotalBytesToDownload > (1024 * 1024)
                        ? $"{plan.TotalBytesToDownload / (1024.0 * 1024.0):F1} MB"
                        : $"{Math.Max(1, plan.TotalBytesToDownload / 1024.0):F0} KB";

                    string status = $"Update Available — Installed v{installedVer} → CDN v{remoteVer} ({plan.FilesToDownload} files · {sizeStr})";
                    CDNProbe.SetReadyStatus(status);
                    Log(LogCategory.Synchronise, $"[CDN INSPECT] {status}");
                }
                else
                {
                    string status = $"Up to Date — Installed v{installedVer} · Latest v{remoteVer}";
                    CDNProbe.SetReadyStatus(status);
                    if (!silent)
                    {
                        Log(LogCategory.Synchronise, $"[CDN INSPECT] {status} (0 updates pending).");
                    }
                }
            }

            RunOnUiThread(ApplyInspectionResult, SynchronizationContext.Current ?? _uiSyncContext);

            return plan;
        }
        catch (Exception ex)
        {
            HasAvailableCdnUpdate = false;
            SynchronisationIsFailed = true;
            SynchronisationStatusMessage = ex.Message;
            if (!silent)
            {
                Log(LogCategory.Synchronise, $"[CDN INSPECT FAIL] Error checking CDN updates: {ex.Message}");
            }
            return null;
        }
    }

    private async Task<bool> SynchroniseContent(bool skipSynchronisation = false, bool manualCheck = false, bool requireExplicitConfirmation = false)
    {
        LauncherSettingsModel settings = LauncherSettingsService.Load();
        if (!manualCheck && (!EnableCdnSync || !settings.EnableCdnSync))
        {
            Log(LogCategory.Synchronise, "SKIP: CDN Content Synchronisation is disabled in launcher settings");
            return true;
        }

        string currentServer = TryGetMasterServerComboText() ?? LoadedMasterServerAddress ?? string.Empty;
        if (skipSynchronisation)
        {
            Log(LogCategory.Synchronise, "SKIP: Synchronisation Skipped (Fast Direct Launch)");
            return true;
        }

        if (LocationSafetyVerdict is not LocationSafetyVerdict.Safe)
        {
            Log(LogCategory.Synchronise, "SKIP: Synchronisation Skipped (Unsafe Location)");

            return true;
        }

        SynchronisationIsActive = true;
        SynchronisationIsScheduled = false;
        SynchronisationIsFailed = false;
        SynchronisationProgressPercent = 0;
        SynchronisationStatusMessage = string.Empty;
        DownloadedFilesDisplay = string.Empty;
        RemovedFilesDisplay = string.Empty;
        SkippedFilesDisplay = string.Empty;
        UpToDateFilesDisplay = string.Empty;

        try
        {
            string? bundleRoot = BundleRootOverride ?? ResolveBundleRoot();
            if (!string.IsNullOrWhiteSpace(bundleRoot))
            {
                Log(LogCategory.Synchronise, $"INFO: Resolved bundle root: '{bundleRoot}'");
                IReadOnlyList<string> runningProcesses = RunningBundleProcessDetectorOverride is not null
                    ? RunningBundleProcessDetectorOverride(bundleRoot)
                    : DetectRunningBundleProcesses(bundleRoot, log: msg => Log(LogCategory.Synchronise, msg));

                if (runningProcesses.Count > 0)
                {
                    Log(LogCategory.Synchronise, $"FAIL: Active game/tool process(es) detected in bundle '{bundleRoot}': {string.Join(", ", runningProcesses)}. Close Heroes of Newerth / COMPEL and retry 'Check for Updates'.");
                    SynchronisationIsFailed = true;
                    SynchronisationStatusMessage = "Close Heroes of Newerth / COMPEL before checking for updates";
                    return false;
                }
            }

            string variant = ResolveDefaultClientVariant();
            string cdnBaseUrl;
            Manifest manifest;

            bool isLocalAuto = IsLocalOfflineAutoSelection(currentServer) ||
                (SelectedCDNAddressItem?.DisplayText?.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) ?? false);

            if (manualCheck && isLocalAuto)
            {
                bool localListening = await LocalMasterServerPortProbe(55555, 250);
                List<string> candidateRawList = [];
                if (localListening)
                {
                    string localCdn = BuildCDNAddress();
                    if (!string.IsNullOrWhiteSpace(localCdn))
                        candidateRawList.Add(localCdn);
                    candidateRawList.Add(PrimaryEmergencyCdnBaseUrl);
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                        candidateRawList.Add(SecondaryEmergencyCdnBaseUrl);
                }
                else
                {
                    Log(LogCategory.Synchronise, $"INFO: Local Master Server (:55555) is not running; checking remote emergency CDN '{PrimaryEmergencyCdnBaseUrl}' without changing launcher mode.");
                    candidateRawList.Add(PrimaryEmergencyCdnBaseUrl);
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                        candidateRawList.Add(SecondaryEmergencyCdnBaseUrl);
                }

                List<string> candidates = [];
                foreach (string raw in candidateRawList)
                {
                    string norm = AddressValidation.NormaliseCDNURL(raw);
                    if (!string.IsNullOrWhiteSpace(norm) && !candidates.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(norm);
                    }
                }

                manifest = null!;
                cdnBaseUrl = candidates.Count > 0 ? candidates[0] : PrimaryEmergencyCdnBaseUrl;

                using HttpClient preflightClient = CdnHttpHandlerOverride is not null
                    ? new HttpClient(CdnHttpHandlerOverride, disposeHandler: false)
                    : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
                preflightClient.Timeout = TimeSpan.FromSeconds(5);

                for (int i = 0; i < candidates.Count; i++)
                {
                    string candidate = candidates[i];
                    try
                    {
                        Log(LogCategory.Synchronise, $@"INIT: Fetching Manifest For Variant ""{variant}"" From CDN At ""{candidate}""");

                        ContentBroker.ValidateAllowedCdnBaseUrl(
                            candidate,
                            AllowedCdnDomainsOverride,
                            allowInMemoryTestLocal: CdnHttpHandlerOverride is not null);

                        // Single-GET fetch on candidate endpoint: avoids tripping the static ContentBroker CdnPipeline
                        // circuit breaker on expected local :55555 404s AND avoids fetching manifest.json twice on 200 OK.
                        string candidateManifestUrl = $"{(candidate.EndsWith('/') ? candidate : candidate + '/')}{Uri.EscapeDataString(variant)}/manifest.json";
                        using HttpRequestMessage preflightReq = new(HttpMethod.Get, candidateManifestUrl);
                        using HttpResponseMessage preflightResp = await preflightClient.SendAsync(preflightReq, HttpCompletionOption.ResponseHeadersRead);
                        preflightResp.EnsureSuccessStatusCode();

                        await using (Stream manifestStream = await preflightResp.Content.ReadAsStreamAsync())
                        {
                            manifest = await System.Text.Json.JsonSerializer
                                .DeserializeAsync(manifestStream, ManifestJSONContext.Default.Manifest)
                                ?? throw new InvalidOperationException($@"Manifest At ""{candidateManifestUrl}"" Deserialised To NULL");
                        }

                        cdnBaseUrl = candidate;
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or BrokenCircuitException or TaskCanceledException)
                    {
                        if (i < candidates.Count - 1)
                        {
                            string nextCandidate = candidates[i + 1];
                            Log(LogCategory.Synchronise, $"FALLBACK: '{candidate}' unreachable ({ex.Message}); retrying '{nextCandidate}'");
                        }
                        else
                        {
                            if (ex is HttpRequestException)
                                throw;
                            throw new HttpRequestException($"All candidate CDNs unreachable: {ex.Message}", ex);
                        }
                    }
                }
            }
            else
            {
                cdnBaseUrl = BuildCDNAddress();
                Log(LogCategory.Synchronise, $@"INIT: Fetching Manifest For Variant ""{variant}"" From CDN At ""{cdnBaseUrl}""");
                if (cdnBaseUrl.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase))
                {
                    List<string> newbsCandidates = [AddressValidation.NormaliseCDNURL(cdnBaseUrl)];
                    if (!string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl))
                    {
                        string secondaryNorm = AddressValidation.NormaliseCDNURL(SecondaryEmergencyCdnBaseUrl);
                        if (!string.IsNullOrWhiteSpace(secondaryNorm) &&
                            !newbsCandidates.Contains(secondaryNorm, StringComparer.OrdinalIgnoreCase))
                        {
                            newbsCandidates.Add(secondaryNorm);
                        }
                    }

                    manifest = null!;
                    using HttpClient preflightClient = CdnHttpHandlerOverride is not null
                        ? new HttpClient(CdnHttpHandlerOverride, disposeHandler: false)
                        : ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create();
                    preflightClient.Timeout = TimeSpan.FromSeconds(5);

                    for (int i = 0; i < newbsCandidates.Count; i++)
                    {
                        string candidate = newbsCandidates[i];
                        try
                        {
                            ContentBroker.ValidateAllowedCdnBaseUrl(
                                candidate,
                                AllowedCdnDomainsOverride,
                                allowInMemoryTestLocal: CdnHttpHandlerOverride is not null);
                            string candidateManifestUrl = $"{(candidate.EndsWith('/') ? candidate : candidate + '/')}{Uri.EscapeDataString(variant)}/manifest.json";
                            using HttpRequestMessage preflightReq = new(HttpMethod.Get, candidateManifestUrl);
                            using HttpResponseMessage preflightResp = await preflightClient.SendAsync(preflightReq, HttpCompletionOption.ResponseHeadersRead);
                            preflightResp.EnsureSuccessStatusCode();

                            await using (Stream manifestStream = await preflightResp.Content.ReadAsStreamAsync())
                            {
                                manifest = await JsonSerializer.DeserializeAsync(manifestStream, ManifestJSONContext.Default.Manifest)
                                    ?? throw new InvalidOperationException($@"Manifest At ""{candidateManifestUrl}"" Deserialised To NULL");
                            }

                            cdnBaseUrl = candidate;
                            break;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or BrokenCircuitException or TaskCanceledException)
                        {
                            if (i < newbsCandidates.Count - 1)
                            {
                                string nextCandidate = newbsCandidates[i + 1];
                                Log(LogCategory.Synchronise, $"FALLBACK: '{candidate}' unreachable ({ex.Message}); retrying same-ecosystem fallback '{nextCandidate}'");
                            }
                            else if (!requireExplicitConfirmation && HasLocalGameClientFiles(bundleRoot))
                            {
                                Log(LogCategory.Synchronise, $"[CDN FALLBACK] Primary and fallback Newbs CDNs unreachable ({ex.Message}); proceeding with local client files.");
                                ResetCdnUpdateStateToOffline();
                                SynchronisationIsFailed = false;
                                SynchronisationStatusMessage = string.Empty;
                                CDNProbe.SetReadyStatus($"Local Files Ready (v{InstalledBundleVersion}) — Remote CDN Unavailable");
                                return true;
                            }
                            else
                            {
                                if (ex is HttpRequestException)
                                    throw;
                                throw new HttpRequestException($"All Newbs candidate CDNs unreachable: {ex.Message}", ex);
                            }
                        }
                    }
                }
                else
                {
                    manifest = await ContentBroker.FetchManifest(
                        variant,
                        baseURL: cdnBaseUrl,
                        httpHandlerOverride: CdnHttpHandlerOverride,
                        allowedDomains: AllowedCdnDomainsOverride);
                }
            }

            Log(LogCategory.Synchronise,
                $"INIT: Manifest Version {manifest.Version} Lists {manifest.Files.Count} File(s)");

            int filesDownloaded = 0;
            int filesDeleted = 0;
            int filesSkipped = 0;
            long bytesDownloaded = 0;

            SynchronisationPlan? plan = null;

            int upToDateTotal = 0;

            Progress<SynchronisationEvent> progress = new(synchronisationEvent =>
            {
                switch (synchronisationEvent.Kind)
                {
                    case SynchronisationEventKind.PlanReady:
                    {
                        plan = synchronisationEvent.Plan;

                        if (plan is not null)
                        {
                            Log(LogCategory.Synchronise, $"PLAN: {plan}");

                            // The Up-To-Date Column Includes Local Deletions So All Four Columns Reach 100% Together When The Synchronisation Completes
                            upToDateTotal = manifest.Files.Count + plan.FilesToDelete;

                            // Skipped Files Are Reported During The First Pass And Leftover Partial Files Are Deleted During The Local Scan, Both Before The Plan Is Reported, So The Skipped And Removed Columns Start From The Counts Already Accumulated By Those Early Events
                            DownloadedFilesDisplay = FormatSynchronisationColumn("DOWNLOADED", 0, plan.FilesToDownload);
                            RemovedFilesDisplay =
                                FormatSynchronisationColumn("REMOVED", filesDeleted, plan.FilesToDelete);
                            SkippedFilesDisplay =
                                FormatSynchronisationColumn("SKIPPED", filesSkipped, plan.FilesToSkip);
                            UpToDateFilesDisplay = FormatSynchronisationColumn("UP-TO-DATE",
                                plan.FilesUpToDate + filesDeleted + filesSkipped, upToDateTotal);

                            // Drives The Progress Bar's Visibility: A Plan With No Downloads And No Deletions Means There Is Nothing To Show Progress Of
                            SynchronisationIsScheduled = plan.FilesToDownload > 0 || plan.FilesToDelete > 0;
                        }

                        break;
                    }

                    case SynchronisationEventKind.ProgressUpdated:
                    {
                        bytesDownloaded = synchronisationEvent.Size;

                        if (plan is not null && plan.TotalBytesToDownload > 0)
                            SynchronisationProgressPercent = Math.Min(100,
                                (double)bytesDownloaded / plan.TotalBytesToDownload * 100);

                        break;
                    }

                    case SynchronisationEventKind.Downloaded:
                    {
                        Log(LogCategory.Synchronise, $"PULL: {synchronisationEvent.Detail}");

                        filesDownloaded++;

                        if (plan is not null)
                        {
                            DownloadedFilesDisplay =
                                FormatSynchronisationColumn("DOWNLOADED", filesDownloaded, plan.FilesToDownload);
                            UpToDateFilesDisplay = FormatSynchronisationColumn("UP-TO-DATE",
                                plan.FilesUpToDate + filesDownloaded + filesDeleted + filesSkipped, upToDateTotal);
                        }

                        break;
                    }

                    case SynchronisationEventKind.Deleted:
                    {
                        Log(LogCategory.Synchronise, $"NUKE: {synchronisationEvent.Detail}");

                        filesDeleted++;

                        if (plan is not null)
                        {
                            RemovedFilesDisplay =
                                FormatSynchronisationColumn("REMOVED", filesDeleted, plan.FilesToDelete);
                            UpToDateFilesDisplay = FormatSynchronisationColumn("UP-TO-DATE",
                                plan.FilesUpToDate + filesDownloaded + filesDeleted + filesSkipped, upToDateTotal);
                        }

                        break;
                    }

                    case SynchronisationEventKind.DownloadFailed:
                    case SynchronisationEventKind.DeletionFailed:
                    {
                        Log(LogCategory.Synchronise, $"FAIL: {synchronisationEvent.Detail}");

                        break;
                    }

                    case SynchronisationEventKind.Skipped:
                    {
                        Log(LogCategory.Synchronise, $"SKIP: {synchronisationEvent.Detail}");

                        filesSkipped++;

                        if (plan is not null)
                        {
                            SkippedFilesDisplay =
                                FormatSynchronisationColumn("SKIPPED", filesSkipped, plan.FilesToSkip);
                            UpToDateFilesDisplay = FormatSynchronisationColumn("UP-TO-DATE",
                                plan.FilesUpToDate + filesDownloaded + filesDeleted + filesSkipped, upToDateTotal);
                        }

                        break;
                    }

                    case SynchronisationEventKind.Completed:
                    {
                        SynchronisationProgressPercent = 100;

                        Log(LogCategory.Synchronise, $"DONE: {synchronisationEvent.Detail}");

                        break;
                    }
                }
            });

            Func<SynchronisationPlan, Task<bool>> confirmPlan = async p =>
            {
                if (p.FilesToDownload == 0 && p.FilesToDelete == 0)
                    return true;

                if (ConfirmUpdatePrompt is not null)
                {
                    return await ConfirmUpdatePrompt(p).ConfigureAwait(false);
                }

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow is not null)
                {
                    double mb = p.TotalBytesToDownload / (1024.0 * 1024.0);
                    string sizeStr = mb >= 1.0 ? $"{mb:F1} MB" : $"{Math.Max(1, p.TotalBytesToDownload / 1024.0):F0} KB";
                    string currentVer = !string.IsNullOrWhiteSpace(InstalledBundleVersion) ? InstalledBundleVersion : ResolveInstalledBundleVersion();
                    string targetVer = !string.IsNullOrWhiteSpace(AvailableCdnVersion) ? AvailableCdnVersion : manifest.Version;

                    string message = $"An update is available for Project Kongor.\n\n" +
                                     $"Installed Version: v{currentVer}\n" +
                                     $"Available Version: v{targetVer}\n\n" +
                                     $"Files to download: {p.FilesToDownload} ({sizeStr})\n" +
                                     $"Files to remove: {p.FilesToDelete}\n\n" +
                                     $"Do you want to download and install this update now?";

                    bool confirmed = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        UpdateDialog dialog = new(message);
                        bool? result = await dialog.ShowDialog<bool?>(desktop.MainWindow);
                        return result == true;
                    });

                    return confirmed;
                }

                // In headless / test / non-desktop environments, default to safe decline (no silent downloads)
                return false;
            };

            string syncTargetDirectory;
            if (manifest.IsOverlay)
            {
                if (string.IsNullOrWhiteSpace(bundleRoot))
                {
                    throw new InvalidOperationException("Cannot apply overlay CDN update: bundle root ('games/client') could not be located. Ensure WILLOWMAKER is inside your ProjectKongor folder.");
                }
                syncTargetDirectory = bundleRoot;
            }
            else
            {
                syncTargetDirectory = BundleRootOverride ?? Environment.CurrentDirectory;
            }

            bool isLoopbackCdn = IsLoopbackCdnEndpoint(cdnBaseUrl);
            bool requireSignature = !isLoopbackCdn || !AllowUnsignedLoopbackCdnManifestsForDev;

            PkvValidationResult pkvValidation = PkvManifestValidator.Verify(
                manifest,
                requireSignature: requireSignature,
                publicKeyXHexOverride: CdnPublicKeyXHexOverride,
                publicKeyYHexOverride: CdnPublicKeyYHexOverride);

            if (!pkvValidation.IsValid)
            {
                string warnMsg = $"[PKV-GUARD] Aborted synchronisation from '{cdnBaseUrl}': {pkvValidation.Reason}";
                Log(LogCategory.Synchronise, warnMsg);
                SynchronisationIsFailed = true;
                SynchronisationStatusMessage = "PKV Guard — Updates Blocked (Non-PKV Server)";
                throw new SecurityException(warnMsg);
            }

            bool enforceAllowlist = true;
            string? ledgerPath = !string.IsNullOrWhiteSpace(bundleRoot)
                ? Path.Combine(bundleRoot, "games", "client", "KONGOR", "cdn_install_ledger.json")
                : null;

            PlaytestPolicyDto playtestPolicy = PlaytestPolicyService.Resolve(
                bundleRoot ?? Environment.CurrentDirectory,
                msg => Log(LogCategory.Synchronise, msg));
            if (playtestPolicy.DisableCdnDeletions)
            {
                Log(LogCategory.Synchronise,
                    "[PlaytestPolicy] Destructive CDN file deletions (NUKE) disabled to protect local playtest archives and overrides.");
            }

            SynchronisationSummary summary = await Task.Run(() => ContentBroker.Synchronise
            (
                manifest,
                variant,
                syncTargetDirectory,
                baseURL: cdnBaseUrl,
                progress: progress,
                confirmPlan: confirmPlan,
                allowDeletions: false,
                requireSignature: requireSignature,
                publicKeyXHexOverride: CdnPublicKeyXHexOverride,
                publicKeyYHexOverride: CdnPublicKeyYHexOverride,
                enforceEmergencyBundleAllowlist: enforceAllowlist,
                installLedgerPath: ledgerPath,
                httpHandlerOverride: CdnHttpHandlerOverride,
                requireExplicitConfirmation: requireExplicitConfirmation,
                allowedDomains: AllowedCdnDomainsOverride
            ));

            if (plan is not null && plan.FilesToDownload == 0 && plan.FilesToDelete == 0)
            {
                Log(LogCategory.Synchronise, "DONE: Project KONGOR client installation is 100% up to date. No updates needed.");
            }

            if (summary.FilesFailed == 0 && summary.FilesDownloaded == 0 && plan is not null && plan.FilesToDownload > 0)
            {
                Log(LogCategory.Synchronise, "SKIP: CDN update was not approved by user. Proceeding with existing local installation.");
                return true;
            }

            if (summary.FilesFailed == 0)
            {
                InstalledBundleVersion = ResolveInstalledBundleVersion();
                HasAvailableCdnUpdate = false;
                CDNProbe.SetReadyStatus($"Up to Date — Installed v{InstalledBundleVersion} · Latest v{manifest.Version}");
            }

            if (summary.FilesFailed > 0)
            {
                SynchronisationIsFailed = true;
                SynchronisationStatusMessage =
                    $"Synchronisation Failed: {summary.FilesFailed} File(s) Could Not Be Transferred";

                Log(LogCategory.Synchronise,
                    $"FAIL: {summary.FilesFailed} File(s) Failed To Be Transferred :: Launch Aborted");

                // Query Locking Processes For Failed Files
                await CheckForLockingProcesses(summary.Failures, syncTargetDirectory);

                return false;
            }

            return true;
        }

        catch (Exception exception) when (exception is HttpRequestException or BrokenCircuitException)
        {
            string detail = exception is HttpRequestException httpEx && httpEx.StatusCode is not null
                ? $"HTTP {(int)httpEx.StatusCode} ({httpEx.StatusCode})"
                : exception is BrokenCircuitException
                    ? "Circuit Breaker Active (CDN Host Unreachable)"
                    : exception.Message;

            Log(LogCategory.Synchronise, $"FAIL: CDN Unreachable :: {detail}");

            SynchronisationIsFailed = true;
            SynchronisationStatusMessage = "CDN Unreachable; Synchronisation Aborted";

            return false;
        }

        catch (Exception exception)
        {
            Log(LogCategory.Synchronise, $"FAIL: {exception.GetType().Name} :: {exception.Message}");

            SynchronisationIsFailed = true;
            SynchronisationStatusMessage = $"Synchronisation Error: {exception.Message}";

            return false;
        }

        finally
        {
            SynchronisationIsActive = false;
        }
    }

    /// <summary>
    ///     Returns the bucket variant that matches the current client operating system.
    ///     The GEMINI pipeline publishes <c>wac</c>, <c>lac</c>, and <c>mac</c> variants for the Windows, Linux, and macOS
    ///     client distributions.
    /// </summary>
    private static string ResolveDefaultClientVariant()
    {
        return OperatingSystem.IsWindows() ? "wac"
            : OperatingSystem.IsLinux() ? "lac"
            : OperatingSystem.IsMacOS() ? "mac"
            : throw new PlatformNotSupportedException(
                $@"Unsupported Operating System: {Environment.OSVersion.Platform}");
    }

    /// <summary>
    ///     Formats a single status-row column as <c>LABEL XXX/YYY (ZZZ%)</c> with three-digit zero-padded counters for
    ///     steady-width display.
    ///     A zero <paramref name="total" /> is treated as 100% complete (nothing to do is fully done).
    /// </summary>
    private static string FormatSynchronisationColumn(string label, int current, int total)
    {
        int percent = total > 0 ? (int)((double)current / total * 100) : 100;

        return $"{label} {current:D3}/{total:D3} ({percent}%)";
    }

    private async Task CheckForLockingProcesses(IReadOnlyList<SynchronisationFailure> failures, string? baseDirectory = null)
    {
        const string unidentifiedProcessGroup = "Unidentified Process";
        string rootDir = baseDirectory ?? Environment.CurrentDirectory;

        // Lock Scanning Touches The Restart Manager And The Filesystem, So It Runs Off The UI Thread To Keep The Window Responsive
        Dictionary<string, LockGroup> lockGroups = await Task.Run(() =>
        {
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

            void AssignToGroup(LockGroup group, string filePath)
            {
                if (group.FilePaths.Contains(filePath) is false)
                    group.FilePaths.Add(filePath);
            }

            foreach (SynchronisationFailure failure in failures)
            {
                // Resolve The Full Path For Lock Scanning
                string absolutePath = Path.IsPathRooted(failure.Path)
                    ? failure.Path
                    : Path.GetFullPath(Path.Combine(rootDir, failure.Path));

                if (File.Exists(absolutePath) is false)
                    continue;

                // Relativise The Path For UI Display
                string displayPath = Path.GetRelativePath(rootDir, absolutePath);

                List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(absolutePath);

                if (lockingProcesses.Count is 0)
                {
                    // No Locking Process Was Identified, So The File Is Only Surfaced When It Is Genuinely Still Locked; This Filters Out Failures Caused By Other Reasons (Such As A Hash Mismatch Or An Unreachable CDN) While Still Reporting A Lock Whose Owner Could Not Be Determined
                    if (FileIsLocked(absolutePath))
                        AssignToGroup(GroupFor(unidentifiedProcessGroup), displayPath);

                    continue;
                }

                foreach (FileLockingProcess lockingProcess in lockingProcesses)
                {
                    // Every Instance Of The Same Executable Is Collapsed Into One Group Keyed By Its Application Name; Its Distinct Process IDs Are Counted So The User Knows How Many Instances Need To Be Closed
                    LockGroup group = GroupFor(lockingProcess.ApplicationName);

                    group.ProcessIDs.Add(lockingProcess.ProcessID);

                    AssignToGroup(group, displayPath);
                }
            }

            return groups;
        });

        if (lockGroups.Count > 0)
        {
            List<LockGroupDisplay> groups =
            [
                .. lockGroups.Select(pair => new LockGroupDisplay
                {
                    // A Process Count Is Appended Only When More Than One Instance Of The Application Is Holding The File, So The User Closes Every One Of Them
                    ProcessName = pair.Value.ProcessIDs.Count > 1
                        ? $"{pair.Key} ({pair.Value.ProcessIDs.Count} Processes)"
                        : pair.Key,
                    FilePaths = pair.Value.FilePaths
                })
            ];

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is not null)
            {
                FileLockDialog dialog = new(groups);

                await dialog.ShowDialog(desktop.MainWindow);
            }
        }
    }

    private static bool FileIsLocked(string path)
    {
        try
        {
            // Opening With No Sharing Fails When Any Other Handle To The File Is Already Open, Which Is The Defining Symptom Of A Lock Held By Another Process; Read Access Is Requested So The Read-Only Attribute Does Not Interfere
            // This Only Acquires A Handle And Never Reads The Contents, So The File's Size Has No Bearing On The Application's Performance; The Probe Is A Single Open/Close Regardless Of Whether The File Is A Few Bytes Or Many Gigabytes
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

            return false;
        }

        catch (IOException)
        {
            return true;
        }

        catch
        {
            return
                false; // Swallowed Deliberately: An Inability To Open The File For Reasons Other Than Sharing (Such As Insufficient Permissions) Must Not Be Misreported As A Lock
        }
    }

    /// <summary>
    ///     Accumulates the distinct locking process IDs and the locked file paths for a single application while the locking
    ///     processes are being scanned.
    /// </summary>
    private sealed class LockGroup
    {
        public HashSet<int> ProcessIDs { get; } = [];

        public List<string> FilePaths { get; } = [];
    }
}
