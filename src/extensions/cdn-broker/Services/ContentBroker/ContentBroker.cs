// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using ASPIRE.Common.Resilience;
using ASPIRE.Common.Services.Vfs;
using Microsoft.Extensions.FileSystemGlobbing;
using NEXUS.Contracts.Cdn;
using Polly;
using WILLOWMAKER.Core.Services.Update;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Synchronises a local installation directory with a content bucket described by a remote manifest.
///     The local tree becomes an exact mirror or additive overlay of the manifest's file list.
///     Files matching <see cref="Manifest.ExcludeFromSource" /> are remote-side paths that must not be downloaded (e.g.
///     the manifest itself).
///     Files matching <see cref="Manifest.ExcludeFromTarget" /> are local-side paths that must not be changed in any way
///     (e.g. the launcher's own files). They are neither overwritten by downloads nor removed by deletions.
/// </summary>
public static class ContentBroker
{
    private const string DefaultBaseURL = "https://cdn.newbs.io/";
    private const string ManifestFileName = "manifest.json";
    private const string PartialDownloadSuffix = ".partial";
    private const int DefaultParallelTransfers = 8;

    public static bool AllowKongorNetDomainForDev { get; set; } = false;

    public static IReadOnlyList<string> DefaultAllowedDomains =>
        AllowKongorNetDomainForDev
            ? ["kongor.net", "newbs.io", "localhost", "127.0.0.1", "::1"]
            : ["newbs.io", "localhost", "127.0.0.1", "::1"];

    private static readonly ResiliencePipeline CdnPipeline = NexusResiliencePipelines.CreateCdnDownloadPipeline();

    private static readonly SocketsHttpHandler SharedSocketsHttpHandler = QuicCdnTransportService.SharedSocketsHttpHandler;

    /// <summary>
    ///     Enforces SSRF and domain allowlist guards on CDN base URLs before any HTTP manifest or object request is issued.
    /// </summary>
    public static void ValidateAllowedCdnBaseUrl(
        string baseURL,
        IReadOnlyCollection<string>? allowedDomains = null,
        bool allowInMemoryTestLocal = false)
    {
        if (string.IsNullOrWhiteSpace(baseURL) || !Uri.TryCreate(baseURL, UriKind.Absolute, out Uri? uri))
        {
            System.Diagnostics.Trace.WriteLine($"[SECURITY-ALERT][CDN-ALLOWLIST] Rejected invalid or non-absolute CDN URL: '{baseURL}'.");
            throw new SecurityException($"[SECURITY-ALERT][CDN-ALLOWLIST] Invalid or non-absolute CDN base URL '{baseURL}'.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            System.Diagnostics.Trace.WriteLine($"[SECURITY-ALERT][CDN-ALLOWLIST] Rejected CDN URL containing embedded UserInfo credentials for host '{uri.Host}'.");
            throw new SecurityException($"[SECURITY-ALERT][CDN-ALLOWLIST] Embedded credentials in CDN base URL are forbidden for host '{uri.Host}'.");
        }

        string host = uri.Host.Trim('[', ']');
        bool isLoopback =
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

        bool isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp)
        {
            System.Diagnostics.Trace.WriteLine($"[SECURITY-ALERT][CDN-ALLOWLIST] Rejected unsupported URI scheme '{uri.Scheme}' for host '{host}'.");
            throw new SecurityException($"[SECURITY-ALERT][CDN-ALLOWLIST] Unsupported URI scheme '{uri.Scheme}' for CDN host '{host}'.");
        }

        if (isHttp && !isLoopback)
        {
            System.Diagnostics.Trace.WriteLine($"[SECURITY-ALERT][CDN-ALLOWLIST] Rejected plaintext HTTP CDN URL for non-loopback host '{host}'.");
            throw new SecurityException($"[SECURITY-ALERT][CDN-ALLOWLIST] Plaintext HTTP is forbidden for non-loopback CDN host '{host}'. HTTPS is mandatory.");
        }

        IReadOnlyCollection<string> effectiveDomains = allowedDomains ?? DefaultAllowedDomains;
        bool isAllowed = false;
        foreach (string rawDomain in effectiveDomains)
        {
            if (string.IsNullOrWhiteSpace(rawDomain))
            {
                continue;
            }

            string domain = rawDomain.Trim().Trim('[', ']').TrimStart('.');
            if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = true;
                break;
            }

            bool isIpOrLocalhost =
                string.Equals(domain, "localhost", StringComparison.OrdinalIgnoreCase) ||
                System.Net.IPAddress.TryParse(domain, out _);
            if (!isIpOrLocalhost && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = true;
                break;
            }
        }

        if (!isAllowed &&
            allowInMemoryTestLocal &&
            allowedDomains is null &&
            isHttps &&
            string.Equals(host, "cdn.test.local", StringComparison.OrdinalIgnoreCase))
        {
            isAllowed = true;
        }

        if (!isAllowed)
        {
            System.Diagnostics.Trace.WriteLine($"[SECURITY-ALERT][CDN-ALLOWLIST] Blocked untrusted CDN host '{host}' (baseURL='{baseURL}').");
            throw new SecurityException(
                $"[SECURITY-ALERT][CDN-ALLOWLIST] Untrusted CDN host '{host}' is not in the allowed CDN domain list ({string.Join(", ", effectiveDomains)}).");
        }
    }

    // The Deletion Pass Must Mirror The Filesystem's Own Case Sensitivity: A Fixed Case-Insensitive Comparer Would Wrongly Keep A Stale File That Differs From A Manifest Entry Only In Case On A Case-Sensitive Filesystem
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase
        : OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase
        : OperatingSystem.IsLinux() ? StringComparer.Ordinal
        : throw new PlatformNotSupportedException($@"Unsupported Operating System: {Environment.OSVersion.Platform}");

    /// <summary>
    ///     Downloads and parses the manifest for the given <paramref name="variant" /> from the CDN.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Preserves optional parameter backwards compatibility for existing callers")]
    public static async Task<Manifest> FetchManifest(string variant, string baseURL = DefaultBaseURL,
        CancellationToken cancellationToken = default, HttpMessageHandler? httpHandlerOverride = null,
        IReadOnlyCollection<string>? allowedDomains = null)
    {
        ValidateAllowedCdnBaseUrl(baseURL, allowedDomains, allowInMemoryTestLocal: httpHandlerOverride is not null);
        string manifestURL = BuildManifestURL(baseURL, variant);

        using HttpClient client = CreateClient(TimeSpan.FromSeconds(10), httpHandlerOverride);

        ResiliencePipeline pipeline = httpHandlerOverride is not null
            ? ResiliencePipeline.Empty
            : CdnPipeline;

        return await pipeline.ExecuteAsync(async ct =>
        {
            using HttpRequestMessage request = QuicCdnTransportService.CreateQuicRequest(HttpMethod.Get, manifestURL);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                Manifest? manifest = await JsonSerializer
                    .DeserializeAsync(stream, ManifestJSONContext.Default.Manifest, ct)
                    .ConfigureAwait(false);

                return manifest ??
                       throw new InvalidOperationException($@"Manifest At ""{manifestURL}"" Deserialised To NULL");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Inspects the manifest against the local <paramref name="targetDirectory" /> and calculates the
    ///     <see cref="SynchronisationPlan" /> in memory without performing any downloads, file deletions, or directory mutations.
    /// </summary>
    public static async Task<SynchronisationPlan> InspectUpdatePlan
    (
        Manifest manifest,
        string variant,
        string targetDirectory,
        bool allowDeletions = true,
        bool requireSignature = false,
        string? publicKeyXHexOverride = null,
        string? publicKeyYHexOverride = null,
        bool enforceEmergencyBundleAllowlist = false,
        string? installLedgerPath = null,
        IProgress<SynchronisationEvent>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        PlanComputationResult result = await ComputePlanAsync
        (
            manifest,
            variant,
            targetDirectory,
            allowDeletions,
            requireSignature,
            publicKeyXHexOverride,
            publicKeyYHexOverride,
            enforceEmergencyBundleAllowlist,
            installLedgerPath,
            progress,
            cancellationToken
        ).ConfigureAwait(false);

        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.PlanReady, result.Plan.ToString(),
            result.Plan.TotalBytesToDownload, result.Plan));

        return result.Plan;
    }

    /// <summary>
    ///     Mirrors or overlays the manifest's file list into <paramref name="targetDirectory" />:
    ///     downloads missing or mismatched files, and handles deletions according to the manifest mode and install ledger.
    ///     Leftover partial files from an interrupted run are removed up-front, before any downloads begin.
    ///     Downloads run in parallel and are written atomically via a <c>.partial</c> temporary file that is renamed on success.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Preserves optional parameter backwards compatibility for existing callers")]
    public static async Task<SynchronisationSummary> Synchronise
    (
        Manifest manifest,
        string variant,
        string targetDirectory,
        string baseURL = DefaultBaseURL,
        int parallelTransfers = DefaultParallelTransfers,
        IProgress<SynchronisationEvent>? progress = null,
        Func<SynchronisationPlan, Task<bool>>? confirmPlan = null,
        bool allowDeletions = true,
        CancellationToken cancellationToken = default,
        bool requireSignature = false,
        string? publicKeyXHexOverride = null,
        string? publicKeyYHexOverride = null,
        bool enforceEmergencyBundleAllowlist = false,
        string? installLedgerPath = null,
        HttpMessageHandler? httpHandlerOverride = null,
        bool requireExplicitConfirmation = false,
        IReadOnlyCollection<string>? allowedDomains = null
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ValidateAllowedCdnBaseUrl(baseURL, allowedDomains, allowInMemoryTestLocal: httpHandlerOverride is not null);

        if (parallelTransfers < 1)
            throw new ArgumentOutOfRangeException(nameof(parallelTransfers), parallelTransfers,
                "At Least One Parallel Transfer Is Required");

        PlanComputationResult computation = await ComputePlanAsync
        (
            manifest,
            variant,
            targetDirectory,
            allowDeletions,
            requireSignature,
            publicKeyXHexOverride,
            publicKeyYHexOverride,
            enforceEmergencyBundleAllowlist,
            installLedgerPath,
            progress,
            cancellationToken
        ).ConfigureAwait(false);

        Directory.CreateDirectory(targetDirectory);

        List<PendingDownload> pendingDownloads = computation.PendingDownloads;
        List<string> pendingDeletions = computation.PendingDeletions;
        List<string> leftoverPartials = computation.LeftoverPartials;
        SynchronisationPlan plan = computation.Plan;
        long totalBytesToDownload = plan.TotalBytesToDownload;
        int filesUpToDate = computation.FilesUpToDate;
        ConcurrentDictionary<string, bool> existedBeforeDownload = computation.ExistedBeforeDownload;

        int filesDeleted = 0;
        int filesFailed = 0;
        ConcurrentBag<SynchronisationFailure> failures = [];

        // Disk-space pre-check (1.1x total bytes + 16MB buffer)
        if (totalBytesToDownload > 0)
        {
            try
            {
                string root = Path.GetPathRoot(computation.TargetDirFullPath) ?? computation.TargetDirFullPath;
                DriveInfo drive = new(root);
                long requiredBytes = (long)(totalBytesToDownload * 1.1) + (16L * 1024 * 1024);
                if (drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new IOException($"Insufficient disk space on target volume '{drive.Name}'. Required: {requiredBytes:N0} bytes (including 10% safety margin + 16MB), Available: {drive.AvailableFreeSpace:N0} bytes.");
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ContentBroker] DriveInfo free-space query skipped for '{computation.TargetDirFullPath}': {ex.Message}");
            }
        }

        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.PlanReady, plan.ToString(),
            totalBytesToDownload, plan));

        if (plan.FilesToDownload > 0 || plan.FilesToDelete > 0)
        {
            if (requireExplicitConfirmation && confirmPlan is null)
            {
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, "Synchronisation blocked: explicit confirmation callback required when requireExplicitConfirmation is true.", 0));
                return new SynchronisationSummary(0, 0, filesUpToDate, 0, 0, []);
            }

            if (confirmPlan is not null)
            {
                bool confirmed = await confirmPlan(plan).ConfigureAwait(false);
                if (!confirmed)
                {
                    progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, "Synchronisation aborted by user.", 0));
                    return new SynchronisationSummary(0, 0, filesUpToDate, 0, 0, []);
                }
            }
        }

        // Now that the plan has been confirmed, clean up leftover partial files from interrupted runs
        foreach (string partialPath in leftoverPartials)
        {
            try
            {
                ForceDelete(partialPath);
                filesDeleted++;
                string rel = NormaliseRelativePath(Path.GetRelativePath(targetDirectory, partialPath));
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Deleted, rel, 0));
            }
            catch (Exception exception)
            {
                filesFailed++;
                failures.Add(new SynchronisationFailure(partialPath, exception.Message));
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.DeletionFailed,
                    $@"{partialPath}: {exception.Message}", 0));
            }
        }

        using HttpClient client = CreateClient(Timeout.InfiniteTimeSpan, httpHandlerOverride);
        using SemaphoreSlim semaphore = new(parallelTransfers, parallelTransfers);
        ResiliencePipeline downloadPipeline = httpHandlerOverride is not null
            ? ResiliencePipeline.Empty
            : CdnPipeline;

        int filesDownloaded = 0;
        long bytesDownloaded = 0;

        long totalBytesDownloaded = 0;
        long lastReportTicks = 0;

        void reportBytesRead(int bytesRead)
        {
            long currentTotal = Interlocked.Add(ref totalBytesDownloaded, bytesRead);
            long now = Environment.TickCount64;
            long last = Volatile.Read(ref lastReportTicks);

            if (now - last > 100)
                if (Interlocked.CompareExchange(ref lastReportTicks, now, last) == last)
                    progress?.Report(new SynchronisationEvent(SynchronisationEventKind.ProgressUpdated, string.Empty,
                        currentTotal));
        }

        ConcurrentBag<PendingDownload> succeededDownloads = [];

        IEnumerable<Task> downloadTasks = pendingDownloads.Select(async download =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.DownloadStarted,
                    download.RelativePath, download.Entry.Size));

                await DownloadOne(client, baseURL, variant, download, manifest.HashAlgorithm, reportBytesRead,
                    cancellationToken, manifest.UsesSha256ObjectLayout, manifest.ExecutablePaths, downloadPipeline).ConfigureAwait(false);

                succeededDownloads.Add(download);

                Interlocked.Add(ref bytesDownloaded, download.Entry.Size);
                Interlocked.Increment(ref filesDownloaded);

                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Downloaded, download.RelativePath,
                    download.Entry.Size));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref filesFailed);
                failures.Add(new SynchronisationFailure(download.RelativePath, exception.Message));

                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.DownloadFailed,
                    $@"{download.RelativePath}: {exception.Message}", download.Entry.Size));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);

        // Report The Final Absolute Total Downloaded Bytes
        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.ProgressUpdated, string.Empty,
            bytesDownloaded));

        HashSet<string> successfullyDeleted = [];

        foreach (string fullPath in pendingDeletions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ForceDelete(fullPath);

                filesDeleted++;
                string rel = NormaliseRelativePath(Path.GetRelativePath(targetDirectory, fullPath));
                successfullyDeleted.Add(rel);

                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Deleted, rel, 0));
            }
            catch (Exception exception)
            {
                filesFailed++;

                failures.Add(new SynchronisationFailure(fullPath, exception.Message));

                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.DeletionFailed,
                    $@"{fullPath}: {exception.Message}", 0));
            }
        }

        if (manifest.IsOverlay)
        {
            PruneEmptyParentDirectories(targetDirectory, pendingDeletions);
        }
        else
        {
            RemoveEmptyDirectories(targetDirectory);
        }

        // Ledger Update: persist installed/removed files atomically
        if (!string.IsNullOrWhiteSpace(installLedgerPath))
        {
            CdnInstallLedger existingLedger = CdnInstallLedger.LoadOrEmpty(installLedgerPath);
            bool shouldPersist = !succeededDownloads.IsEmpty ||
                                 successfullyDeleted.Count > 0 ||
                                 (filesFailed == 0 && (File.Exists(installLedgerPath) || manifest.Sequence > 1) && manifest.Sequence > existingLedger.MaxSequenceSeen);

            if (shouldPersist)
            {
                CdnInstallLedger ledger = existingLedger;
                ledger.UpdatedAtUtc = TimeProvider.System.GetUtcNow().ToString("O");

                foreach (var download in succeededDownloads)
                {
                    if (!ledger.InstalledFiles.ContainsKey(download.RelativePath) &&
                        existedBeforeDownload.TryGetValue(download.RelativePath, out bool existed) && existed)
                    {
                        ledger.PreExistingPaths.Add(download.RelativePath);
                    }
                    ledger.InstalledFiles[download.RelativePath] = download.Entry.Hash.ToLowerInvariant();
                }

                foreach (string delRel in successfullyDeleted)
                {
                    ledger.InstalledFiles.Remove(delRel);
                }

                if (filesFailed == 0)
                {
                    ledger.Version = manifest.Version ?? string.Empty;
                    if (manifest.Sequence > ledger.MaxSequenceSeen)
                    {
                        ledger.MaxSequenceSeen = manifest.Sequence;
                    }
                }

                CdnInstallLedger.SaveIfChangedAtomically(installLedgerPath, ledger);
            }
        }

        SynchronisationSummary summary = new
        (
            filesDownloaded,
            filesDeleted,
            filesUpToDate,
            filesFailed,
            bytesDownloaded,
            [.. failures]
        );

        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Completed, summary.ToString(),
            bytesDownloaded));

        return summary;
    }

    private static async Task DownloadOne(HttpClient client, string baseURL, string variant, PendingDownload download,
        string hashAlgorithm, Action<int>? progressCallback, CancellationToken cancellationToken,
        bool usesSha256ObjectLayout = false, IReadOnlyList<string>? executablePaths = null,
        ResiliencePipeline? pipeline = null)
    {
        string fileURL = usesSha256ObjectLayout
            ? BuildObjectURL(baseURL, download.Entry.Hash)
            : BuildFileURL(baseURL, variant, download.RelativePath);
        string partialPath = download.LocalPath + PartialDownloadSuffix;

        string? parentDirectory = Path.GetDirectoryName(download.LocalPath);

        if (string.IsNullOrEmpty(parentDirectory) is false)
            Directory.CreateDirectory(parentDirectory);

        ResiliencePipeline activePipeline = pipeline ?? CdnPipeline;
        await activePipeline.ExecuteAsync(async ct =>
        {
            long bytesWritten = 0;
            string actualHashHex;

            using (IncrementalHash incrementalHash = CreateIncrementalHash(hashAlgorithm))
            {
                FileStream fileStream = new(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

                await using (fileStream.ConfigureAwait(false))
                {
                    using HttpRequestMessage request = QuicCdnTransportService.CreateQuicRequest(HttpMethod.Get, fileURL);
                    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    Stream downloadStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                    await using (downloadStream.ConfigureAwait(false))
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);

                        try
                        {
                            while (true)
                            {
                                int bytesRead = await downloadStream.ReadAsync(buffer.AsMemory(), ct)
                                    .ConfigureAwait(false);

                                if (bytesRead is 0)
                                    break;

                                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct)
                                    .ConfigureAwait(false);

                                incrementalHash.AppendData(buffer, 0, bytesRead);

                                bytesWritten += bytesRead;

                                progressCallback?.Invoke(bytesRead);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                }

                actualHashHex = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
            }

            if (bytesWritten != download.Entry.Size)
            {
                TryDeleteQuietly(partialPath);

                throw new IOException(
                    $@"Size Mismatch For ""{download.RelativePath}"" (Expected {download.Entry.Size:N0}, Got {bytesWritten:N0})");
            }

            if (string.Equals(actualHashHex, download.Entry.Hash, StringComparison.OrdinalIgnoreCase) is false)
            {
                TryDeleteQuietly(partialPath);

                throw new IOException(
                    $@"Hash Mismatch For ""{download.RelativePath}"" (Expected ""{download.Entry.Hash}"", Got ""{actualHashHex}"")");
            }

            // S2Z / ZIP Security Validation: verify package archive integrity & zip-slip/bomb immunity before commit
            if (download.RelativePath.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) ||
                download.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                (bool isSafe, string? failureReason) = S2zPackageSecurityValidator.VerifyArchiveStructure(partialPath);
                if (!isSafe)
                {
                    TryDeleteQuietly(partialPath);
                    throw new IOException(
                        $@"Archive Security Verification Failed For ""{download.RelativePath}"": {failureReason}");
                }
            }

            // Strategic Initiative [SR-06]: Atomic shadow-copy file replacement
            // If the local file is locked or in-use by a running client/tool, atomically renames it to .old
            // and places the new file without sharing violation errors.
            WILLOWMAKER.Core.Services.Update.ShadowCopyUpdateService.Instance.AtomicReplaceFile(partialPath, download.LocalPath);

            // Strategic Initiative [SR-06]: POSIX executable permissions (+x / 0755)
            if (!OperatingSystem.IsWindows())
            {
                bool isExplicitExecutable = executablePaths is not null &&
                    executablePaths.Any(p => string.Equals(NormaliseRelativePath(p), download.RelativePath, StringComparison.OrdinalIgnoreCase));

                string fileName = Path.GetFileName(download.RelativePath);
                string ext = Path.GetExtension(download.RelativePath).ToLowerInvariant();

                bool isVersionedSharedObj = fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase);
                bool isNativeExecutable = ext is ".sh" or ".command" or ".so" or ".dylib" or ".bin" ||
                    isVersionedSharedObj ||
                    fileName.Equals("hon-x86_64", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("hon-x86", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("HoN64", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("hon-x86_64-server", StringComparison.OrdinalIgnoreCase);

                if (isExplicitExecutable || isNativeExecutable)
                {
                    try
                    {
                        File.SetUnixFileMode(download.LocalPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[ContentBroker] SetUnixFileMode(0755) skipped for '{download.LocalPath}': {ex.Message}");
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static Task<bool> LocalFileMatchesManifestEntry(string localPath, ManifestEntry entry,
        CancellationToken cancellationToken) =>
        LocalFileMatchesManifestEntry(localPath, entry, "SHA-256", cancellationToken);

    public static async Task<bool> LocalFileMatchesManifestEntry(string localPath, ManifestEntry entry,
        string hashAlgorithm, CancellationToken cancellationToken)
    {
        FileInfo info = new(localPath);

        if (info.Exists is false)
            return false;

        if (info.Length != entry.Size)
            return false;

        ValidateSecureHashAlgorithm(hashAlgorithm);
        string normalized = hashAlgorithm.ToUpperInvariant();

        FileStreamOptions options = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 131072
        };

        FileStream fileStream = new(localPath, options);

        await using (fileStream.ConfigureAwait(false))
        {
            byte[] hashBytes = normalized switch
            {
                "SHA256" or "SHA-256" => await SHA256.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false),
                "SHA384" or "SHA-384" => await SHA384.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false),
                "SHA512" or "SHA-512" => await SHA512.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unsupported Manifest Hash Algorithm \"{hashAlgorithm}\"")
            };

            string actualHashHex = Convert.ToHexStringLower(hashBytes);

            return string.Equals(actualHashHex, entry.Hash, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void ValidateSecureHashAlgorithm(string hashAlgorithm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashAlgorithm);
        string normalized = hashAlgorithm.ToUpperInvariant();
        if (normalized is "MD5" or "MD-5" or "SHA1" or "SHA-1")
        {
            throw new NotSupportedException(
                $@"Insecure Manifest Hash Algorithm ""{hashAlgorithm}"" is rejected by the CDN security policy. Minimum requirement is SHA-256.");
        }

        if (normalized is not ("SHA256" or "SHA-256" or "SHA384" or "SHA-384" or "SHA512" or "SHA-512"))
        {
            throw new NotSupportedException($@"Unsupported Manifest Hash Algorithm ""{hashAlgorithm}""");
        }
    }

    private static IncrementalHash CreateIncrementalHash(string hashAlgorithm)
    {
        ValidateSecureHashAlgorithm(hashAlgorithm);
        return hashAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" or "SHA-256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "SHA384" or "SHA-384" => IncrementalHash.CreateHash(HashAlgorithmName.SHA384),
            "SHA512" or "SHA-512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => throw new NotSupportedException($@"Unsupported Manifest Hash Algorithm ""{hashAlgorithm}""")
        };
    }

    private static HttpClient CreateClient(TimeSpan timeout, HttpMessageHandler? httpHandlerOverride = null)
    {
        HttpClient client = httpHandlerOverride is not null
            ? new(httpHandlerOverride, false)
            : new(SharedSocketsHttpHandler, false);

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WILLOWMAKER/{VersionChecker.CurrentVersionDisplay}");
        client.Timeout = timeout;

        return client;
    }

    /// <summary>
    ///     Validates whether a relative path conforms to the emergency bundle allowlist rules.
    ///     Protects against accidental mutation of base game archives and user configuration.
    /// </summary>
    /// <param name="relativePath">The relative bundle path to validate.</param>
    /// <param name="reason">Outputs the failure reason if disallowed.</param>
    /// <returns><c>true</c> if allowed; otherwise <c>false</c>.</returns>
    public static bool IsAllowedEmergencyBundlePath(string relativePath, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            reason = "Path cannot be null or empty.";
            return false;
        }

        string norm = relativePath.Replace('\\', '/').Trim();

        if (norm.IndexOfAny(['\t', '\r', '\n', '\0', '|']) >= 0)
        {
            reason = "Control characters and pipe delimiters are forbidden in manifest paths.";
            return false;
        }

        if (norm.StartsWith('/'))
        {
            reason = "Leading slash is not permitted in bundle paths.";
            return false;
        }

        if (norm.Contains(':'))
        {
            reason = "Colon character is not permitted in bundle paths.";
            return false;
        }

        string[] segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            reason = "Directory traversal '..' is not permitted.";
            return false;
        }

        // Hard-rejected extensions: S2Z and ZIP archives are strictly forbidden across all emergency overlay paths
        if (norm.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) ||
            norm.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            reason = norm.StartsWith("games/client/override/", StringComparison.OrdinalIgnoreCase)
                ? "S2Z archives are forbidden under games/client/override/ for zero-churn emergency updates. Only loose files are permitted."
                : $"Archive file '{norm}' is strictly forbidden in emergency CDN overlay updates. Only loose files and native binaries are permitted.";
            return false;
        }

        // Hard-rejected prefixes: base archives, shared archives, and user configs
        if (norm.StartsWith("games/client/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/base/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/shared/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/server/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/server_p2p/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/windows-x64/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/windows-x64/base/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/linux-x64/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/linux-x64/base/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/macos-x64/game/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("games/client/macos-x64/base/", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith("userdata/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/userdata/", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("userdata", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Access to '{norm}' is strictly forbidden by emergency CDN policy.";
            return false;
        }

        // 1. games/client/override/** (loose files only, .s2z archives already rejected above)
        if (norm.StartsWith("games/client/override/", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        // 2. games/client/<leaf> where extension is .dll, .exe, .so (including versioned .so.N), .dylib
        if (norm.StartsWith("games/client/", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = norm["games/client/".Length..];
            if (!remainder.Contains('/'))
            {
                string ext = Path.GetExtension(remainder);
                if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                    remainder.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }
        }

        // 3. games/client/windows-x64/**, games/client/linux-x64/** (non-game/), games/client/macos-x64/** (non-game/)
        if (norm.StartsWith("games/client/windows-x64/", StringComparison.OrdinalIgnoreCase) &&
            !norm.StartsWith("games/client/windows-x64/game/", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        if (norm.StartsWith("games/client/linux-x64/", StringComparison.OrdinalIgnoreCase) &&
            !norm.StartsWith("games/client/linux-x64/game/", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        if (norm.StartsWith("games/client/macos-x64/", StringComparison.OrdinalIgnoreCase) &&
            !norm.StartsWith("games/client/macos-x64/game/", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        // 4. games/server/<leaf> and games/server/libs-x86_64/**
        if (norm.StartsWith("games/server/", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = norm["games/server/".Length..];
            if (!remainder.Contains('/'))
            {
                string ext = Path.GetExtension(remainder);
                if (remainder.Equals("hon-x86_64-server", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                    remainder.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }

            if (norm.StartsWith("games/server/libs-x86_64/", StringComparison.OrdinalIgnoreCase))
            {
                reason = null;
                return true;
            }
        }

        // 5. games/server_p2p/<leaf> and games/server_p2p/libs-x86_64/**
        if (norm.StartsWith("games/server_p2p/", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = norm["games/server_p2p/".Length..];
            if (!remainder.Contains('/'))
            {
                string ext = Path.GetExtension(remainder);
                if (remainder.Equals("hon-x86_64-server", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                    remainder.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }

            if (norm.StartsWith("games/server_p2p/libs-x86_64/", StringComparison.OrdinalIgnoreCase))
            {
                reason = null;
                return true;
            }
        }

        reason = $"Path '{norm}' is outside the emergency bundle allowlist.";
        return false;
    }

    /// <summary>
    ///     Validates whether a relative path conforms to the emergency bundle Remove rules.
    ///     Ensures destructive Remove operations cannot delete shipped native binaries or loose HLSL shaders.
    /// </summary>
    /// <param name="relativePath">The relative bundle path to validate for removal.</param>
    /// <param name="reason">Outputs the failure reason if disallowed.</param>
    /// <returns><c>true</c> if allowed; otherwise <c>false</c>.</returns>
    public static bool IsAllowedEmergencyRemovePath(string relativePath, out string? reason)
    {
        if (!IsAllowedEmergencyBundlePath(relativePath, out reason))
        {
            return false;
        }

        string norm = relativePath.Replace('\\', '/').Trim();
        if (norm.StartsWith("games/client/override/", StringComparison.OrdinalIgnoreCase) &&
            !norm.StartsWith("games/client/override/core/shaders/", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        reason = $"Destructive Remove is forbidden for '{norm}': shipped native binaries and loose HLSL shaders cannot be deleted via Remove. Republish the previous known-good file in Files instead.";
        return false;
    }

    /// <summary>
    ///     Builds the content-addressable storage (CAS) URL for an object stored under objects/ab/{sha256}.
    /// </summary>
    public static string BuildObjectURL(string baseURL, string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseURL);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hex);

        string normalisedBase = baseURL.EndsWith('/') ? baseURL : baseURL + '/';
        string hashLower = sha256Hex.Trim().ToLowerInvariant();
        if (hashLower.Length < 2)
            throw new ArgumentException("SHA256 hash must be at least 2 characters.", nameof(sha256Hex));

        return $"{normalisedBase}objects/{hashLower[..2]}/{hashLower}";
    }

    private static string BuildManifestURL(string baseURL, string variant)
    {
        string normalisedBase = baseURL.EndsWith('/') ? baseURL : baseURL + '/';

        return $"{normalisedBase}{Uri.EscapeDataString(variant)}/{ManifestFileName}";
    }

    private static string BuildFileURL(string baseURL, string variant, string relativePath)
    {
        string normalisedBase = baseURL.EndsWith('/') ? baseURL : baseURL + '/';
        string escapedVariant = Uri.EscapeDataString(variant);
        string escapedRelative = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

        return $"{normalisedBase}{escapedVariant}/{escapedRelative}";
    }

    private static string NormaliseRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static bool MatchesAny(string relativePath, Matcher matcher)
    {
        return matcher.Match(relativePath).HasMatches;
    }

    private static Matcher BuildMatcher(IReadOnlyList<string> patterns)
    {
        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);

        foreach (string pattern in patterns)
            matcher.AddInclude(pattern);

        return matcher;
    }

    private static void RemoveEmptyDirectories(string targetDirectory)
    {
        if (Directory.Exists(targetDirectory) is false)
            return;

        // Walk Bottom-Up So Inner Empty Directories Are Removed Before Their Now-Empty Parents
        foreach (string directory in Directory.EnumerateDirectories(targetDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any() is false)
                    Directory.Delete(directory, false);
            }

            catch
            {
                // Best-Effort Only: A Failure To Remove An Empty Directory Is Not A Synchronisation Failure
            }
    }

    private static void PruneEmptyParentDirectories(string targetDirectory, IEnumerable<string> deletedFilePaths)
    {
        if (Directory.Exists(targetDirectory) is false)
            return;

        string targetDirFull = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string overrideBoundary = Path.Combine(targetDirFull, "games", "client", "override");
        StringComparison pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (string filePath in deletedFilePaths)
        {
            string? dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                string dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (dirFull.Equals(targetDirFull, pathComparison) ||
                    dirFull.Equals(overrideBoundary, pathComparison) ||
                    !dirFull.StartsWith(targetDirFull, pathComparison))
                {
                    break;
                }

                try
                {
                    if (Directory.Exists(dirFull) && !Directory.EnumerateFileSystemEntries(dirFull).Any())
                    {
                        Directory.Delete(dirFull, false);
                        dir = Path.GetDirectoryName(dirFull);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[ContentBroker] PruneEmptyParentDirectories stopped at '{dirFull}': {ex.Message}");
                    break;
                }
            }
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        catch
        {
            // Swallowed Deliberately: Cleanup Of A Failed Download's Partial File Must Not Mask The Original Error
        }
    }

    private static void ForceDelete(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        // "File.Delete" Throws "UnauthorizedAccessException" When The Target Carries The Read-Only Attribute, So We Clear The Attribute First
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        File.Delete(path);
    }

    private sealed record PlanComputationResult(
        SynchronisationPlan Plan,
        List<PendingDownload> PendingDownloads,
        List<string> PendingDeletions,
        List<string> LeftoverPartials,
        ConcurrentDictionary<string, bool> ExistedBeforeDownload,
        int FilesUpToDate,
        int FilesToSkip,
        string TargetDirFullPath
    );

    private static async Task<PlanComputationResult> ComputePlanAsync(
        Manifest manifest,
        string variant,
        string targetDirectory,
        bool allowDeletions,
        bool requireSignature,
        string? publicKeyXHexOverride,
        string? publicKeyYHexOverride,
        bool enforceEmergencyBundleAllowlist,
        string? installLedgerPath,
        IProgress<SynchronisationEvent>? progress,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        ValidateSecureHashAlgorithm(manifest.HashAlgorithm);

        // Security Guard: Manifest PKV v5 validation & signature verification
        PkvValidationResult pkvValidation = PkvManifestValidator.Verify(
            manifest,
            requireSignature: requireSignature,
            publicKeyXHexOverride: publicKeyXHexOverride,
            publicKeyYHexOverride: publicKeyYHexOverride);

        if (!pkvValidation.IsValid)
        {
            throw new SecurityException($"[PKV-GUARD] Manifest rejected: {pkvValidation.Reason} (Remediation: {pkvValidation.RemediationCommand})");
        }

        CdnInstallLedger? preflightLedger = !string.IsNullOrWhiteSpace(installLedgerPath)
            ? CdnInstallLedger.LoadOrEmpty(installLedgerPath)
            : null;

        if (preflightLedger is not null && preflightLedger.MaxSequenceSeen > 0 && manifest.Sequence < preflightLedger.MaxSequenceSeen)
        {
            throw new SecurityException($"Manifest sequence downgrade rejected: manifest sequence {manifest.Sequence} is older than installed sequence {preflightLedger.MaxSequenceSeen}.");
        }

        // Security Guard: Validate that no manifest entries attempt directory traversal or violate allowlist
        StringComparison fsPathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        string targetDirFullPath = Path.GetFullPath(targetDirectory);
        if (!targetDirFullPath.EndsWith(Path.DirectorySeparatorChar))
            targetDirFullPath += Path.DirectorySeparatorChar;

        foreach (string rawPath in manifest.Files.Keys)
        {
            string norm = NormaliseRelativePath(rawPath);
            string full = Path.GetFullPath(Path.Combine(targetDirectory, norm.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(targetDirFullPath, fsPathComparison))
            {
                throw new SecurityException($"Directory traversal attempt detected in manifest entry: '{rawPath}'");
            }

            if (enforceEmergencyBundleAllowlist && !IsAllowedEmergencyBundlePath(norm, out string? reason))
            {
                throw new SecurityException($"Disallowed emergency bundle path in manifest files: '{rawPath}'. Reason: {reason}");
            }
        }

        if (manifest.Remove is not null)
        {
            foreach (string remPath in manifest.Remove)
            {
                string norm = NormaliseRelativePath(remPath);
                string full = Path.GetFullPath(Path.Combine(targetDirectory, norm.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(targetDirFullPath, fsPathComparison))
                {
                    throw new SecurityException($"Directory traversal attempt detected in manifest remove entry: '{remPath}'");
                }

                if (enforceEmergencyBundleAllowlist && !IsAllowedEmergencyRemovePath(norm, out string? reason))
                {
                    throw new SecurityException($"Disallowed emergency bundle path in manifest remove list: '{remPath}'. Reason: {reason}");
                }
            }
        }

        Matcher sourceExclusions = BuildMatcher(manifest.ExcludeFromSource);
        Matcher targetExclusions = BuildMatcher(manifest.ExcludeFromTarget);

        // First Pass: Decide Which Files Listed In The Manifest Need To Be Downloaded
        ConcurrentBag<PendingDownload> pendingDownloadsBag = [];
        ConcurrentDictionary<string, bool> existedBeforeDownload = new(StringComparer.OrdinalIgnoreCase);

        long totalBytesToDownload = 0;
        int filesUpToDate = 0;
        int filesToSkip = 0;

        ParallelOptions parallelVerificationOptions = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(manifest.Files, parallelVerificationOptions, async (fileKvp, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            string rawRelativePath = fileKvp.Key;
            ManifestEntry entry = fileKvp.Value;
            string relativePath = NormaliseRelativePath(rawRelativePath);

            // Decision Gate One (Remote Side): May We Download This File From The Bucket?
            if (MatchesAny(relativePath, sourceExclusions))
            {
                Interlocked.Increment(ref filesToSkip);
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, relativePath, entry.Size));
                return;
            }

            // Decision Gate Two (Local Side): Would Writing It Change A Local File That Must Not Be Changed?
            if (MatchesAny(relativePath, targetExclusions))
            {
                Interlocked.Increment(ref filesToSkip);
                progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, relativePath, entry.Size));
                return;
            }

            string localPath = Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            existedBeforeDownload[relativePath] = File.Exists(localPath);

            if (await LocalFileMatchesManifestEntry(localPath, entry, manifest.HashAlgorithm, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref filesUpToDate);
                return;
            }

            // Hardlink Guard (FN-014): never overwrite hardlinked target files
            if (File.Exists(localPath))
            {
                uint linkCount = NativeFileOperations.GetHardLinkCount(localPath);
                if (linkCount > 1)
                {
                    throw new IOException($"Refusing to overwrite hardlinked target file (link count {linkCount}): '{localPath}'. Replacing hardlinks breaks shared client/server assets (FN-014).");
                }
            }

            pendingDownloadsBag.Add(new PendingDownload(relativePath, localPath, entry));
            Interlocked.Add(ref totalBytesToDownload, entry.Size);
        }).ConfigureAwait(false);

        List<PendingDownload> pendingDownloads = [.. pendingDownloadsBag];

        // Second Pass: Decide Which Local Files Are No Longer In The Manifest And Should Be Deleted
        HashSet<string> expectedRelativePaths = new(manifest.Files.Keys.Select(NormaliseRelativePath), PathComparer);

        List<string> pendingDeletions = [];
        List<string> leftoverPartials = [];

        bool effectiveAllowDeletions = allowDeletions && !manifest.IsOverlay;

        if (manifest.IsOverlay)
        {
            // In overlay mode, avoid scanning the whole directory tree.
            // Check leftover partials ONLY for declared manifest files and remove entries.
            foreach (string rawPath in manifest.Files.Keys)
            {
                string rel = NormaliseRelativePath(rawPath);
                string partialPath = Path.Combine(targetDirectory, rel.Replace('/', Path.DirectorySeparatorChar)) + PartialDownloadSuffix;
                if (File.Exists(partialPath))
                {
                    leftoverPartials.Add(partialPath);
                }
            }

            if (manifest.Remove is not null)
            {
                foreach (string remRaw in manifest.Remove)
                {
                    string rel = NormaliseRelativePath(remRaw);
                    string partialPath = Path.Combine(targetDirectory, rel.Replace('/', Path.DirectorySeparatorChar)) + PartialDownloadSuffix;
                    if (File.Exists(partialPath))
                    {
                        leftoverPartials.Add(partialPath);
                    }
                }
            }

            // Process manifest.Remove via CdnInstallLedger
            if (manifest.Remove is not null && manifest.Remove.Count > 0)
            {
                CdnInstallLedger? ledger = !string.IsNullOrWhiteSpace(installLedgerPath)
                    ? CdnInstallLedger.LoadOrEmpty(installLedgerPath)
                    : null;

                foreach (string remRaw in manifest.Remove)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string remRel = NormaliseRelativePath(remRaw);
                    string remFullPath = Path.Combine(targetDirectory, remRel.Replace('/', Path.DirectorySeparatorChar));

                    if (MatchesAny(remRel, targetExclusions))
                    {
                        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, remRel, 0));
                        continue;
                    }

                    if (!File.Exists(remFullPath))
                    {
                        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, $"File not present for removal: '{remRel}'", 0));
                        continue;
                    }

                    uint linkCount = NativeFileOperations.GetHardLinkCount(remFullPath);
                    if (linkCount > 1)
                    {
                        throw new IOException($"Refusing to remove hardlinked target file (link count {linkCount}): '{remFullPath}'. Hardlink deletion breaks shared client/server assets (FN-014).");
                    }

                    if (ledger is null || !ledger.InstalledFiles.TryGetValue(remRel, out string? recordedHash))
                    {
                        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, $"File '{remRel}' is not tracked in CDN install ledger. Refusing unverified deletion.", 0));
                        continue;
                    }

                    if (ledger.PreExistingPaths.Contains(remRel))
                    {
                        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, $"File '{remRel}' existed on disk prior to CDN installation; refusing deletion.", 0));
                        continue;
                    }

                    FileInfo remInfo = new(remFullPath);
                    ManifestEntry recordedEntry = new() { Size = remInfo.Length, Hash = recordedHash };
                    bool matches = await LocalFileMatchesManifestEntry(remFullPath, recordedEntry, "SHA-256", cancellationToken).ConfigureAwait(false);

                    if (!matches)
                    {
                        progress?.Report(new SynchronisationEvent(SynchronisationEventKind.Skipped, $"File '{remRel}' has been modified locally since CDN installation (hash mismatch). Refusing deletion.", 0));
                        continue;
                    }

                    pendingDeletions.Add(remFullPath);
                }
            }
        }
        else
        {
            if (Directory.Exists(targetDirectory))
                foreach (string fullPath in Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string relativePath = NormaliseRelativePath(Path.GetRelativePath(targetDirectory, fullPath));

                    // Leftover partials are recorded to be purged only after plan confirmation
                    if (relativePath.EndsWith(PartialDownloadSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        leftoverPartials.Add(fullPath);
                        continue;
                    }

                    if (expectedRelativePaths.Contains(relativePath))
                        continue;

                    if (MatchesAny(relativePath, targetExclusions))
                        continue;

                    if (effectiveAllowDeletions)
                    {
                        pendingDeletions.Add(fullPath);
                    }
                }
        }

        SynchronisationPlan plan = new
        (
            pendingDownloads.Count,
            leftoverPartials.Count + pendingDeletions.Count,
            filesToSkip,
            filesUpToDate,
            totalBytesToDownload
        );

        return new PlanComputationResult(
            plan,
            pendingDownloads,
            pendingDeletions,
            leftoverPartials,
            existedBeforeDownload,
            filesUpToDate,
            filesToSkip,
            targetDirFullPath
        );
    }

    private sealed record PendingDownload(string RelativePath, string LocalPath, ManifestEntry Entry);
}
