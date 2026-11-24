// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security.Cryptography;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Harvester.Resolvers;

namespace Kongor.Modding.Harvester.Downloader;

/// <summary>
///     High-performance streaming download manager with SHA-256 verification, local caching,
///     retry policies, and archive integrity validation.
/// </summary>
public class StreamingDownloadManager
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly HttpClient _httpClient;

    private readonly MultiHostDownloadResolver _resolver;
    private static readonly SocketsHttpHandler s_sharedDownloadHandler = new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    };

    public StreamingDownloadManager(MultiHostDownloadResolver? resolver = null, HttpClient? httpClient = null)
    {
        _resolver = resolver ?? new MultiHostDownloadResolver();
        _httpClient = httpClient ?? new HttpClient(s_sharedDownloadHandler, disposeHandler: false);
#if false
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15)
#endif
    }

    /// <summary>
    ///     Downloads or retrieves from cache a single community mod package.
    /// </summary>
    public async Task<DownloadedModInfo> DownloadModAsync(
        CommunityModEntry entry,
        string outputDirectory,
        bool overwriteExisting = false,
        Action<string>? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(outputDirectory);

        if (string.IsNullOrWhiteSpace(entry.PrimaryDownloadUrl))
            throw new InvalidOperationException($"Mod '{entry.Name}' ({entry.Id}) has no download URL.");

        logger?.Invoke($"[Harvester] Resolving host for '{entry.Name}': {entry.PrimaryDownloadUrl}");
        ResolvedDownloadTarget resolvedTarget = await _resolver.ResolveAsync(entry.PrimaryDownloadUrl, _httpClient, ct);
        entry.ResolvedDownloadUrl = resolvedTarget.DirectDownloadUrl;
        entry.ResolvedHost = resolvedTarget.HostType;

        // Determine destination file path
        string sanitizedName = SanitizeFileName(resolvedTarget.SuggestedFileName);
        if (string.IsNullOrWhiteSpace(sanitizedName) ||
            sanitizedName.Equals(".honmod", StringComparison.OrdinalIgnoreCase))
            sanitizedName = SanitizeFileName(entry.Name) + ".honmod";

        string destinationPath = Path.Combine(outputDirectory, sanitizedName);

        // Check local cache
        if (!overwriteExisting && File.Exists(destinationPath))
        {
            FileInfo fi = new(destinationPath);
            if (fi.Length > 0)
            {
                logger?.Invoke(
                    $"[Harvester] Cache hit for '{entry.Name}' at '{destinationPath}' ({fi.Length:N0} bytes)");
                string cachedSha256 = await ComputeFileSha256Async(destinationPath, ct);
                bool isValidZip = ValidateArchive(destinationPath, out string validationMsg);

                entry.FileSizeBytes = fi.Length;
                entry.Sha256Hash = cachedSha256;

                return new DownloadedModInfo
                {
                    ModId = entry.Id,
                    ModName = entry.Name,
                    FileName = sanitizedName,
                    LocalPath = destinationPath,
                    FileSizeBytes = fi.Length,
                    Sha256Hash = cachedSha256,
                    DownloadUrl = resolvedTarget.DirectDownloadUrl,
                    ResolvedHost = resolvedTarget.HostType,
                    WasCached = true,
                    IsValidHonMod = isValidZip,
                    ValidationNotes = validationMsg
                };
            }
        }

        // Execute streaming download with retry logic
        string tempPath = destinationPath + ".downloading";
        int maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
            try
            {
                logger?.Invoke(
                    $"[Harvester] Downloading '{entry.Name}' from '{resolvedTarget.DirectDownloadUrl}' (Attempt {attempt}/{maxRetries})...");

                using HttpRequestMessage request = new(HttpMethod.Get, resolvedTarget.DirectDownloadUrl);
                request.Headers.Add("User-Agent", "ProjectKongor-ModHarvester/1.0");

                foreach (KeyValuePair<string, string> kvp in resolvedTarget.CustomHeaders)
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);

                using HttpResponseMessage response =
                    await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                using Stream networkStream = await response.Content.ReadAsStreamAsync(ct);
                using FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
                    true);
                using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                byte[] buffer = new byte[65536];
                int bytesRead;
                long totalDownloaded = 0;

                while ((bytesRead = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    hasher.AppendData(buffer, 0, bytesRead);
                    totalDownloaded += bytesRead;
                }

                await fileStream.FlushAsync(ct);
                fileStream.Close();

                byte[] hashBytes = hasher.GetHashAndReset();
                string sha256 = Convert.ToHexStringLower(hashBytes);

                // Move temp to destination
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(tempPath, destinationPath);

                FileInfo finalInfo = new(destinationPath);
                bool isValid = ValidateArchive(destinationPath, out string validationMsg);

                entry.FileSizeBytes = finalInfo.Length;
                entry.Sha256Hash = sha256;

                logger?.Invoke(
                    $"[Harvester] ✓ Downloaded '{entry.Name}' successfully ({finalInfo.Length:N0} bytes, SHA256: {sha256[..8]}...)");

                return new DownloadedModInfo
                {
                    ModId = entry.Id,
                    ModName = entry.Name,
                    FileName = sanitizedName,
                    LocalPath = destinationPath,
                    FileSizeBytes = finalInfo.Length,
                    Sha256Hash = sha256,
                    DownloadUrl = resolvedTarget.DirectDownloadUrl,
                    ResolvedHost = resolvedTarget.HostType,
                    WasCached = false,
                    IsValidHonMod = isValid,
                    ValidationNotes = validationMsg
                };
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastException = ex;
                logger?.Invoke(
                    $"[Harvester] ⚠ Transient failure downloading '{entry.Name}' (Attempt {attempt}): {ex.Message}. Retrying...");
                if (File.Exists(tempPath))
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        /* ignore */
                    }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (File.Exists(tempPath))
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        /* ignore */
                    }

                break;
            }

        throw new InvalidOperationException(
            $"Failed to download mod '{entry.Name}' after {maxRetries} attempts: {lastException?.Message}",
            lastException);
    }

    /// <summary>
    ///     Computes the SHA-256 hash of an existing file.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using SHA256 sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    ///     Validates whether the file is a valid ZIP/honmod archive containing readable entries.
    /// </summary>
    public static bool ValidateArchive(string filePath, out string message)
    {
        try
        {
            using FileStream fs = File.OpenRead(filePath);
            if (fs.Length < 4)
            {
                message = "File is too small (< 4 bytes).";
                return false;
            }

            byte[] header = new byte[4];
            int read = fs.Read(header, 0, 4);
            fs.Position = 0;

            // Check PK ZIP signature 0x50 0x4B 0x03 0x04 or 0x50 0x4B 0x05 0x06
            if (header[0] == 0x50 && header[1] == 0x4B)
            {
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                bool hasModXml = zip.Entries.Any(e => e.FullName.Equals("mod.xml", StringComparison.OrdinalIgnoreCase));
                message = hasModXml
                    ? $"Valid .honmod archive with mod.xml ({zip.Entries.Count} entries)"
                    : $"Valid ZIP/S2Z archive ({zip.Entries.Count} entries, no root mod.xml)";
                return true;
            }

            message =
                $"Not a valid ZIP/honmod archive (Magic header: 0x{header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2})";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Corrupted archive: {ex.Message}";
            return false;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "mod.honmod";

        string clean = string.Join("_", fileName.Split(InvalidFileNameChars, StringSplitOptions.RemoveEmptyEntries));
        return clean.Trim();
    }
}
