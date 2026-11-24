// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Security.Cryptography;
using System.Text;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Provides version checking against GitHub releases, asset downloading, and self-update capabilities.
/// </summary>
public static partial class VersionChecker
{
    // The "/releases/latest" Endpoint Is Chosen Deliberately: GitHub Defines It As The Most Recent Non-Pre-Release, Non-Draft Release, So Stable Users Only Get Newer Stables And Pre-Release Users Only Get The Next Stable
    // See https://docs.github.com/en/rest/releases/releases#get-the-latest-release
    // Swapping To "/releases" Would Include Pre-Releases
    public const string LatestReleaseURL =
        "https://api.github.com/repos/guccidonut/PKV/releases/latest";

    /// <summary>
    ///     The current application version, parsed from the build-time generated version constant. Any Semantic Versioning
    ///     suffix is discarded because <see cref="Version" /> accepts only the numeric <c>Major.Minor[.Build[.Revision]]</c>
    ///     form.
    /// </summary>
    public static Version CurrentVersion { get; } = ParseNumericVersion(GeneratedVersionInformation.VersionString);

    /// <summary>
    ///     A display-friendly string for the current version (e.g. "v1.2.0").
    /// </summary>
    public static string CurrentVersionDisplay
        => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>
    ///     Matches a version string of the form <c>[v]Major.Minor.Patch[suffix]</c>, where the leading <c>v</c> and the
    ///     trailing suffix (e.g. a Semantic Versioning pre-release like <c>-alpha-2</c> or build metadata like <c>+abc123</c>)
    ///     are both optional.
    ///     Exposes the numeric <c>Major.Minor.Patch</c> portion through the <c>numeric</c> named group.
    /// </summary>
    [GeneratedRegex(@"^v?(?<numeric>\d+\.\d+\.\d+)(?<suffix>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    /// <summary>
    ///     Queries the GitHub releases API for the latest published version of WILLOWMAKER.
    /// </summary>
    public static async Task<VersionCheckResult> CheckForLatestVersion()
    {
        using HttpClient client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(10));

        client.DefaultRequestHeaders.UserAgent.ParseAdd(DeploymentManifest.ApplicationName);
        client.Timeout = TimeSpan.FromSeconds(10);

        string json = await GetWithTransientRetry(client, LatestReleaseURL);

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;

        string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string releasePageURL = root.GetProperty("html_url").GetString() ?? string.Empty;

        // Defensive Check In Case A Future API Change Breaks The "/releases/latest" Contract
        bool isPreRelease = root.TryGetProperty("prerelease", out JsonElement prereleaseElement) &&
                            prereleaseElement.GetBoolean();

        if (isPreRelease)
            return new VersionCheckResult(false, null, null, releasePageURL);

        if (TryParseNumericVersion(tagName, out Version? latestVersion) is false)
            return new VersionCheckResult(false, null, null, releasePageURL);

        bool updateIsAvailable = latestVersion > CurrentVersion;

        string? downloadURL = null;
        string? selectedAssetName = null;
        string? expectedSha256 = null;
        string? checksumsDownloadURL = null;

        if (updateIsAvailable && root.TryGetProperty("assets", out JsonElement assets))
        {
            string platformIdentifier = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "macos"
                : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException(
                    $@"Unsupported Operating System: {Environment.OSVersion.Platform}");

            string architectureIdentifier = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException(
                    $@"Unsupported Process Architecture: {RuntimeInformation.ProcessArchitecture}")
            };

            // Release Assets Are Named "{platform}-{architecture}-{tag}.zip" So This Identifier Helps Target The Correct Asset By Both Platform And Architecture
            string assetIdentifier = $"{platformIdentifier}-{architectureIdentifier}";

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? assetName = asset.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(assetName)) continue;

                if (assetName.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                    assetName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsDownloadURL = asset.GetProperty("browser_download_url").GetString();
                }

                if (assetName.Contains(assetIdentifier, StringComparison.OrdinalIgnoreCase) &&
                    (assetName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
                     assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    downloadURL = asset.GetProperty("browser_download_url").GetString();
                    selectedAssetName = assetName;
                    expectedSha256 = TryExtractNormalizedSha256(asset);

                    if (assetName.EndsWith(".zst",
                            StringComparison
                                .OrdinalIgnoreCase)) break; // Prioritize high-speed Zstandard compressed bundle
                }
            }

            if (string.IsNullOrEmpty(expectedSha256) &&
                !string.IsNullOrEmpty(selectedAssetName) &&
                !string.IsNullOrEmpty(checksumsDownloadURL))
            {
                try
                {
                    string checksumManifest = await GetWithTransientRetry(client, checksumsDownloadURL);
                    expectedSha256 = TryParseSha256SumsForAsset(checksumManifest, selectedAssetName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[VersionChecker] Warning: Failed fetching SHA256SUMS manifest from '{checksumsDownloadURL}': {ex.Message}");
                }
            }
        }

        return new VersionCheckResult(updateIsAvailable, latestVersion, downloadURL, releasePageURL, expectedSha256);
    }

    /// <summary>
    ///     Downloads a release asset from the specified URL to a temporary file and returns its path.
    ///     Reports download progress as a percentage of <c>Content-Length</c>.
    ///     If the server does not advertise a response content length, the callback is not invoked.
    /// </summary>
    public static async Task<string> DownloadUpdate(string downloadURL, IProgress<double>? progress = null)
    {
        string extension = downloadURL.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? ".tar.zst" : ".zip";
        string archivePath =
            Path.Combine(Path.GetTempPath(), $"{DeploymentManifest.ApplicationName}-Update{extension}");

        using HttpClient client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(10));

        client.DefaultRequestHeaders.UserAgent.ParseAdd(DeploymentManifest.ApplicationName);

        // ResponseHeadersRead Returns As Soon As The Headers Are In, So Content-Length Is Available Before The Body Streams
        // Without It, The Whole Response Would Be Buffered Before The Method Returned, Defeating The Purpose Of Progress Reporting
        using HttpResponseMessage response =
            await client.GetAsync(downloadURL, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        await using Stream downloadStream = await response.Content.ReadAsStreamAsync();
        await using FileStream fileStream = File.Create(archivePath);

        byte[] buffer = new byte[81920];
        long bytesDownloaded = 0;
        int bytesRead;

        while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));

            bytesDownloaded += bytesRead;

            if (progress is not null && totalBytes is > 0)
                progress.Report((double)bytesDownloaded / totalBytes.Value * 100);
        }

        return archivePath;
    }

    /// <summary>
    ///     Verifies the SHA-256 cryptographic digest of a downloaded update archive before extraction.
    ///     Fails closed unless <c>KONGOR_ALLOW_UNSIGNED_SELF_UPDATE=1</c> is explicitly configured in local dev testing.
    /// </summary>
    public static async Task VerifyUpdateArchiveIntegrityAsync(
        string archivePath,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Self-update archive not found at '{archivePath}'.", archivePath);
        }

        string? normalizedExpected = NormalizeSha256Hex(expectedSha256);
        if (string.IsNullOrEmpty(normalizedExpected))
        {
            string sidecarShaPath = archivePath + ".sha256";
            if (File.Exists(sidecarShaPath))
            {
                string sidecarRaw = await File.ReadAllTextAsync(sidecarShaPath, cancellationToken).ConfigureAwait(false);
                normalizedExpected = NormalizeSha256Hex(sidecarRaw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            }
        }

        if (string.IsNullOrEmpty(normalizedExpected))
        {
            bool allowUnsigned = string.Equals(
                Environment.GetEnvironmentVariable("KONGOR_ALLOW_UNSIGNED_SELF_UPDATE"),
                "1",
                StringComparison.OrdinalIgnoreCase);
            if (!allowUnsigned)
            {
                throw new CryptographicException(
                    $"[SECURITY-ALERT] Self-update aborted for '{archivePath}': release asset is missing a cryptographic SHA-256 digest. " +
                    "Ensure GitHub release assets include SHA256SUMS / digest metadata, or set KONGOR_ALLOW_UNSIGNED_SELF_UPDATE=1 for isolated local testing.");
            }

            Console.Error.WriteLine($"[VersionChecker] WARNING: Proceeding with unsigned self-update '{archivePath}' because KONGOR_ALLOW_UNSIGNED_SELF_UPDATE=1 is set.");
            return;
        }

        byte[] actualHashBytes;
        await using (FileStream stream = File.OpenRead(archivePath))
        {
            actualHashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        string actualHex = Convert.ToHexStringLower(actualHashBytes);

        byte[] expectedBytes = Encoding.ASCII.GetBytes(normalizedExpected);
        byte[] actualBytes = Encoding.ASCII.GetBytes(actualHex);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception deleteEx)
            {
                Console.Error.WriteLine($"[VersionChecker] Warning: Could not delete tampered archive '{archivePath}': {deleteEx.Message}");
            }

            throw new CryptographicException(
                $"[SECURITY-ALERT] Self-update SHA-256 integrity check failed for '{archivePath}': expected '{normalizedExpected}', computed '{actualHex}'. Quarantined/deleted archive.");
        }

        Console.Error.WriteLine($"[VersionChecker] Verified SHA-256 integrity of update archive '{archivePath}' ({actualHex}).");
    }

    /// <summary>
    ///     Extracts a ZIP archive into <paramref name="destinationDirectory" /> while blocking ZipSlip path traversal entries.
    /// </summary>
    public static void ExtractZipSafely(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        string normalizedRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string candidatePath = Path.GetFullPath(Path.Combine(normalizedRoot, entry.FullName));
            if (!candidatePath.StartsWith(normalizedRoot, comparison))
            {
                throw new InvalidOperationException(
                    $"[SECURITY-ALERT] Blocked ZipSlip path traversal entry '{entry.FullName}' in update archive '{archivePath}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(candidatePath);
                continue;
            }

            string? parentDir = Path.GetDirectoryName(candidatePath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(candidatePath, overwrite: true);
        }
    }

    private static string? TryExtractNormalizedSha256(JsonElement asset)
    {
        if (asset.TryGetProperty("digest", out JsonElement digestProp) &&
            digestProp.ValueKind == JsonValueKind.String)
        {
            return NormalizeSha256Hex(digestProp.GetString());
        }

        return null;
    }

    public static string? TryParseSha256SumsForAsset(string manifestContent, string assetName)
    {
        if (string.IsNullOrWhiteSpace(manifestContent) || string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        foreach (string rawLine in manifestContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;

            int sep = line.IndexOfAny([' ', '\t']);
            if (sep <= 0) continue;

            string hashCandidate = line[..sep].Trim();
            string fileCandidate = line[sep..].Trim().TrimStart('*', '.', '/');
            if (string.Equals(Path.GetFileName(fileCandidate), assetName, StringComparison.OrdinalIgnoreCase))
            {
                string? normalized = NormalizeSha256Hex(hashCandidate);
                if (!string.IsNullOrEmpty(normalized))
                {
                    return normalized;
                }
            }
        }

        return null;
    }

    private static string? NormalizeSha256Hex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["sha256:".Length..].Trim();
        }

        if (trimmed.Length != 64) return null;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i])) return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    ///     Extracts the downloaded archive, spawns a platform-appropriate update script, and exits the current process.
    /// </summary>
    public static void ApplyUpdateAndRestart(string archivePath, string? expectedSha256 = null)
    {
#pragma warning disable RS0030 // Justification: Synchronous wrapper for process exit update script launcher
        ApplyUpdateAndRestartAsync(archivePath, expectedSha256).GetAwaiter().GetResult();
#pragma warning restore RS0030
    }

    /// <summary>
    ///     Asynchronously extracts the downloaded archive, spawns a platform-appropriate update script, and exits the current process.
    /// </summary>
    public static Task ApplyUpdateAndRestartAsync(string archivePath, CancellationToken cancellationToken)
        => ApplyUpdateAndRestartAsync(archivePath, expectedSha256: null, cancellationToken: cancellationToken);

    /// <summary>
    ///     Asynchronously verifies SHA-256 integrity, extracts the downloaded archive, spawns a platform-appropriate update script, and exits the current process.
    /// </summary>
    public static async Task ApplyUpdateAndRestartAsync(
        string archivePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        await VerifyUpdateArchiveIntegrityAsync(archivePath, expectedSha256, cancellationToken).ConfigureAwait(false);

        string targetDirectory = AppContext.BaseDirectory;
        string tempDirectory = Path.Combine(Path.GetTempPath(), DeploymentManifest.UpdateExtractDirectoryName);

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);

        ZstdArchiveService zstdService = new();
        if (archivePath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
            zstdService.IsZstandardArchive(archivePath))
            await zstdService.DecompressTarZstToDirectoryAsync(archivePath, tempDirectory, token: cancellationToken).ConfigureAwait(false);
        else
            ExtractZipSafely(archivePath, tempDirectory);

        string[] topLevelEntries = Directory.GetFileSystemEntries(tempDirectory);

        if (topLevelEntries.Length is 1 && Directory.Exists(topLevelEntries[0]))
            tempDirectory = topLevelEntries[0];

        string executablePath = Environment.ProcessPath ??
                                Path.Combine(targetDirectory, DeploymentManifest.ApplicationExecutableFileName);

        // Enumerate The Files The New Release Ships So The Update Script Can Force-Delete Each Pre-Existing Counterpart (Including Read-Only Ones) Before Copying The New Files Into Place
        string[] relativePathsToReplace =
        [
            .. Directory
                .EnumerateFiles(tempDirectory, "*", SearchOption.AllDirectories)
                .Select(absolutePath => Path.GetRelativePath(tempDirectory, absolutePath))
        ];

        if (OperatingSystem.IsWindows())
        {
            // Strategic Initiative [SR-06]: Transactional Shadow-Copy Self-Updater
            // Renames currently executing binary/loaded DLLs to .old, applies atomic update in-process,
            // registers .old for deletion, and directly restarts without external PowerShell scripts.
            var txResult = await WILLOWMAKER.Core.Services.Update.ShadowCopyUpdateService.Instance.ApplyDirectoryUpdateAsync(
                tempDirectory, targetDirectory, cancellationToken).ConfigureAwait(false);

            if (!txResult.Success)
            {
                throw new InvalidOperationException($"Shadow-copy update failed: {txResult.ErrorMessage}");
            }

            try
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine($"[VersionChecker] Non-fatal temp archive cleanup warning for '{archivePath}': {cleanupEx.Message}");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            });

            Environment.Exit(0);
            return;
        }

        if (OperatingSystem.IsMacOS())
            SpawnMacOSUpdateScript(archivePath, tempDirectory, targetDirectory, executablePath, relativePathsToReplace);
        else
            SpawnLinuxUpdateScript(archivePath, tempDirectory, targetDirectory, executablePath, relativePathsToReplace);

        Environment.Exit(0);
    }

    /// <summary>
    ///     Cleans up any leftover *.old shadow files from previous updates upon application startup.
    ///     If a file is currently locked, registers it for deletion upon the next system reboot.
    /// </summary>
    public static int CleanupPendingOldFiles(string? targetDirectory = null)
    {
        string dir = targetDirectory ?? AppContext.BaseDirectory;
        return WILLOWMAKER.Core.Services.Update.ShadowCopyUpdateService.Instance.PurgePendingOldFiles(dir);
    }

    /// <summary>
    ///     Performs an HTTP GET against the specified URL, retrying transient failures with exponential backoff before
    ///     surfacing the final exception.
    ///     Connection-level errors, request timeouts, HTTP 429 (rate limited by GitHub's secondary limiter), and 5xx server
    ///     errors are treated as transient; every other failure is rethrown immediately because retrying would not change the
    ///     outcome.
    /// </summary>
    private static async Task<string> GetWithTransientRetry(HttpClient client, string url)
    {
        TimeSpan[] backoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

        for (int attemptIndex = 0;; attemptIndex++)
            try
            {
                return await client.GetStringAsync(url);
            }

            catch (Exception exception) when (attemptIndex < backoffs.Length && IsTransientFailure(exception))
            {
                await Task.Delay(backoffs[attemptIndex]);
            }
    }

    /// <summary>
    ///     Classifies whether an exception thrown by an HTTP request is transient (worth retrying) or terminal (worth
    ///     surfacing).
    /// </summary>
    private static bool IsTransientFailure(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => true,
            HttpRequestException httpException => httpException.StatusCode is null or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError,
            _ => false
        };
    }

    /// <summary>
    ///     Parses the numeric <c>Major.Minor.Patch</c> portion of a version string, tolerating an optional leading <c>v</c>
    ///     and any trailing Semantic Versioning suffix.
    ///     Throws <see cref="FormatException" /> if the input does not contain a parseable numeric version.
    /// </summary>
    private static Version ParseNumericVersion(string versionString)
    {
        if (TryParseNumericVersion(versionString, out Version? version) is false)
            throw new FormatException(
                $@"Version String ""{versionString}"" Does Not Match The Expected Format: 1) Optional ""v"" Prefix, 2) Then Major.Minor.Patch, 3) Then Optional Suffix");

        return version;
    }

    /// <summary>
    ///     Tries to parse the numeric <c>Major.Minor.Patch</c> portion of a version string, tolerating an optional leading
    ///     <c>v</c> and any trailing Semantic Versioning suffix.
    ///     Returns <see langword="false" /> and sets <paramref name="version" /> to <see langword="null" /> on failure.
    /// </summary>
    private static bool TryParseNumericVersion(string versionString, [NotNullWhen(true)] out Version? version)
    {
        Match match = VersionRegex().Match(versionString);

        if (match.Success is false)
        {
            version = null;

            return false;
        }

        return Version.TryParse(match.Groups["numeric"].Value, out version);
    }

    private static void SpawnWindowsUpdateScript(string archivePath, string sourceDirectory, string targetDirectory,
        string executablePath, string[] relativePathsToReplace)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "WILLOWMAKER.update.ps1");

        // The Relative Paths Are Embedded As A PowerShell Single-Quoted Array Literal So Each One Can Be Force-Deleted Before The New Files Are Copied In
        string pathArrayLiteral = string.Join(", ", relativePathsToReplace.Select(relativePath => $"'{relativePath}'"));

        string script =
            $$"""
                  Start-Sleep -Milliseconds 3500
                  $relativePathsToReplace = @({{pathArrayLiteral}})
                  foreach ($relativePath in $relativePathsToReplace) {
                      $stalePath = Join-Path '{{targetDirectory}}' $relativePath
                      if (Test-Path -LiteralPath $stalePath) {
                          $staleItem = Get-Item -LiteralPath $stalePath -Force
                          if ($staleItem.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
                              $staleItem.Attributes = $staleItem.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                          }
                          Remove-Item -LiteralPath $stalePath -Force
                      }
                  }
                  Copy-Item -Path '{{sourceDirectory}}\*' -Destination '{{targetDirectory}}' -Recurse -Force
                  Start-Process -FilePath '{{executablePath}}'
                  Remove-Item -Path '{{sourceDirectory}}' -Recurse -Force
                  Remove-Item -Path '{{archivePath}}' -Force
                  Remove-Item -Path $MyInvocation.MyCommand.Source -Force
              """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $@"-ExecutionPolicy Bypass -NoProfile -File ""{scriptPath}""",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void SpawnMacOSUpdateScript(string archivePath, string sourceDirectory, string targetDirectory,
        string executablePath, string[] relativePathsToReplace)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "WILLOWMAKER.update.sh");

        // The Relative Paths Are Embedded As A Bash Single-Quoted Word List So Each One Can Be Force-Deleted Before The New Files Are Copied In
        string pathWordList = string.Join(" ", relativePathsToReplace.Select(relativePath => $"'{relativePath}'"));

        string script =
            $"""
                 #!/bin/bash
                 sleep 3.5
                 for relativePath in {pathWordList}; do
                     stalePath="{targetDirectory}/$relativePath"
                     if [ -e "$stalePath" ]; then
                         chmod u+w "$stalePath" 2>/dev/null || true
                         rm -f "$stalePath"
                     fi
                 done
                 cp -Rf "{sourceDirectory}/." "{targetDirectory}/"
                 chmod +x "{executablePath}"
                 open "{executablePath}"
                 rm -rf "{sourceDirectory}"
                 rm "{archivePath}"
                 rm "$0"
             """;

        File.WriteAllText(scriptPath, script);

        Process.Start("chmod", $@"+x ""{scriptPath}""")?.WaitForExit();

        Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = scriptPath,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void SpawnLinuxUpdateScript(string archivePath, string sourceDirectory, string targetDirectory,
        string executablePath, string[] relativePathsToReplace)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "WILLOWMAKER.update.sh");

        // The Relative Paths Are Embedded As A Bash Single-Quoted Word List So Each One Can Be Force-Deleted Before The New Files Are Copied In
        string pathWordList = string.Join(" ", relativePathsToReplace.Select(relativePath => $"'{relativePath}'"));

        string script =
            $"""
                 #!/bin/bash
                 sleep 3.5
                 for relativePath in {pathWordList}; do
                     stalePath="{targetDirectory}/$relativePath"
                     if [ -e "$stalePath" ]; then
                         chmod u+w "$stalePath" 2>/dev/null || true
                         rm -f "$stalePath"
                     fi
                 done
                 cp -rf "{sourceDirectory}/." "{targetDirectory}/"
                 chmod +x "{executablePath}"
                 "{executablePath}" &
                 rm -rf "{sourceDirectory}"
                 rm "{archivePath}"
                 rm "$0"
             """;

        File.WriteAllText(scriptPath, script);

        Process.Start("chmod", $@"+x ""{scriptPath}""")?.WaitForExit();

        Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = scriptPath,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
