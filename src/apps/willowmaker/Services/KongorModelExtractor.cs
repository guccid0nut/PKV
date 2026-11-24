// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorModelExtractor.cs
// System: WILLOWMAKER Launcher - Dynamic Client Model Asset Extractor
// Policy: Strict Explicit Typing (NO 'var') - Zero Git Repo Storage
// ============================================================================

namespace WILLOWMAKER.Core.Services;

public static class KongorModelExtractor
{
    private const string CacheFileName = "willowmaker_kongor_avatar.bmp";
    private const int MinValidBmpBytes = 54;
    private static readonly Lock CacheLock = new();
    private static string? _cachedAvatarPath;

    /// <summary>
    ///     Searches game client .s2z archives for the Kongor character texture asset across candidate paths.
    ///     Decodes DDS texture directly into a temp BMP cache file (%TEMP%\willowmaker_kongor_avatar.bmp).
    ///     Returns the absolute path to the decoded BMP file.
    /// </summary>
    public static string? ExtractKongorModelToCacheFile(string clientRoot)
    {
        string? existingCache = Volatile.Read(ref _cachedAvatarPath);
        if (!string.IsNullOrEmpty(existingCache) && IsValidCachedBmp(existingCache))
            return existingCache;

        lock (CacheLock)
        {
            if (!string.IsNullOrEmpty(_cachedAvatarPath) && IsValidCachedBmp(_cachedAvatarPath))
                return _cachedAvatarPath;

            string deterministicCachePath = Path.Combine(Path.GetTempPath(), CacheFileName);
            if (IsValidCachedBmp(deterministicCachePath))
            {
                Volatile.Write(ref _cachedAvatarPath, deterministicCachePath);
                return deterministicCachePath;
            }

            List<string> candidateRoots = [];

            if (!string.IsNullOrWhiteSpace(clientRoot)) candidateRoots.Add(clientRoot);

            DirectoryInfo? dir = new(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidateClient = Path.Combine(dir.FullName, "games", "client");
                if (Directory.Exists(candidateClient) && !candidateRoots.Contains(candidateClient))
                    candidateRoots.Add(candidateClient);
                string candidateShared = Path.Combine(dir.FullName, "games", "shared");
                if (Directory.Exists(candidateShared) && !candidateRoots.Contains(candidateShared))
                    candidateRoots.Add(candidateShared);
                dir = dir.Parent;
            }

            string[] targetAssetPaths =
            [
                "00000000/npcs/kongor/color.dds",
                "00000000/shared/effects/taunts/kongor/kongor/color.dds",
                "00000000/npcs/kongor/hell_kongor/color.dds",
                "npcs/kongor/color.dds",
                "00000000/ui/fe2/store/icons/taunt_kongor.dds"
            ];

            // 1. Check loose files in candidate roots
            foreach (string root in candidateRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                foreach (string targetPath in targetAssetPaths)
                {
                    string loosePath = Path.Combine(root, targetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(loosePath))
                        try
                        {
                            using FileStream fs = File.OpenRead(loosePath);
                            byte[]? bmpBytes = DdsDecoder.DecodeDdsStreamToBmpBytes(fs);
                            if (bmpBytes != null && bmpBytes.Length > MinValidBmpBytes)
                                return PersistCachedBmp(deterministicCachePath, bmpBytes);
                        }
                        catch
                        {
                        }
                }
            }

            foreach (string root in candidateRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                string gameDir = Path.Combine(root, "game");
                if (!Directory.Exists(gameDir)) continue;

                string[] s2zFiles = Directory.GetFiles(gameDir, "*.s2z", SearchOption.TopDirectoryOnly);
                foreach (string s2zPath in s2zFiles)
                    try
                    {
                        using ZipArchive zip = ZipFile.OpenRead(s2zPath);
                        List<ZipArchiveEntry> entries = [.. zip.Entries];

                        foreach (string targetPath in targetAssetPaths)
                        foreach (ZipArchiveEntry entry in entries)
                        {
                            string normalizedEntry = entry.FullName.Replace('\\', '/');
                            if (normalizedEntry.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                                (normalizedEntry.Contains(targetPath, StringComparison.OrdinalIgnoreCase) &&
                                 normalizedEntry.Contains("kongor", StringComparison.OrdinalIgnoreCase)))
                                try
                                {
                                    using Stream stream = entry.Open();
                                    byte[]? bmpBytes = DdsDecoder.DecodeDdsStreamToBmpBytes(stream);
                                    if (bmpBytes != null && bmpBytes.Length > MinValidBmpBytes)
                                        return PersistCachedBmp(deterministicCachePath, bmpBytes);
                                }
                                catch
                                {
                                    // Individual entry read error, continue to next entry
                                }
                        }
                    }
                    catch
                    {
                        // Archive read error, continue to next s2z file
                    }
            }

            return null;
        }
    }

    private static bool IsValidCachedBmp(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileInfo info = new(path);
            return info.Length > MinValidBmpBytes;
        }
        catch
        {
            return false;
        }
    }

    private static string PersistCachedBmp(string deterministicCachePath, byte[] bmpBytes)
    {
        string stagePath = Path.Combine(Path.GetTempPath(), $"willowmaker_kongor_avatar_stage_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(stagePath, bmpBytes);
            File.Move(stagePath, deterministicCachePath, true);
            Volatile.Write(ref _cachedAvatarPath, deterministicCachePath);
            return deterministicCachePath;
        }
        catch
        {
            try
            {
                if (File.Exists(stagePath)) File.Delete(stagePath);
            }
            catch
            {
            }

            if (IsValidCachedBmp(deterministicCachePath))
            {
                Volatile.Write(ref _cachedAvatarPath, deterministicCachePath);
                return deterministicCachePath;
            }

            File.WriteAllBytes(deterministicCachePath, bmpBytes);
            Volatile.Write(ref _cachedAvatarPath, deterministicCachePath);
            return deterministicCachePath;
        }
    }
}
