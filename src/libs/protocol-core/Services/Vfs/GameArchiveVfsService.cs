// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using ASPIRE.Common.K2;
using Microsoft.Extensions.Caching.Memory;
using NEXUS.Contracts.Vfs;

namespace Kongor.Core.Services.Vfs;

public sealed class GameArchiveVfsService : IGameArchiveVfsService, IDisposable
{
    private static readonly SemaphoreSlim s_diskWriteThrottle = new(4, 4);

    private static readonly ConcurrentDictionary<string, GamePathIndex> s_pathIndexCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IMemoryCache DefaultCache = new MemoryCache(new MemoryCacheOptions
    {
        SizeLimit = 512 * 1024 * 1024 // 512 MB memory limit
    });

    private static readonly FrozenDictionary<string, string> HeroFolderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["madman"] = "scar",
            ["witchslayer"] = "witch_slayer",
            ["witch_slayer"] = "witch_slayer",
            ["slayer"] = "witch_slayer",
            ["nighthound"] = "hunter",
            ["night_hound"] = "hunter",
            ["warbeast"] = "wolfman",
            ["war_beast"] = "wolfman",
            ["fayde"] = "fade",
            ["fade"] = "fade",
            ["nymphora"] = "fairy",
            ["blacksmith"] = "dwarf_magi",
            ["swiftblade"] = "hiro",
            ["moonqueen"] = "krixi",
            ["moon_queen"] = "krixi",
            ["thunderbringer"] = "kunas",
            ["glacius"] = "frosty",
            ["pharaoh"] = "mumra",
            ["succubus"] = "succubis",
            ["succubis"] = "succubis",
            ["bombardier"] = "bomb",
            ["corrupteddisciple"] = "taint",
            ["corrupted_disciple"] = "taint",
            ["wildsoul"] = "yogi",
            ["wild_soul"] = "yogi",
            ["sandwraith"] = "sand_wraith",
            ["sand_wraith"] = "sand_wraith",
            ["masterofarms"] = "master_of_arms",
            ["master_of_arms"] = "master_of_arms",
            ["forsakenarcher"] = "forsaken_archer",
            ["forsaken_archer"] = "forsaken_archer",
            ["sirbenzington"] = "sir_benzington",
            ["sir_benzington"] = "sir_benzington",
            ["emeraldwarden"] = "emerald_warden",
            ["emerald_warden"] = "emerald_warden",
            ["pebbles"] = "rocky",
            ["keeperoftheforest"] = "treant",
            ["keeper_of_the_forest"] = "treant",
            ["kotf"] = "treant",
            ["pollywog"] = "pollywogpriest",
            ["pollywog_priest"] = "pollywogpriest",
            ["pandamonium"] = "panda",
            ["jereziah"] = "jereziah",
            ["jeraziah"] = "jereziah",
            ["shadowblade"] = "shadow_blade",
            ["shadow_blade"] = "shadow_blade",
            ["doctorrepulsor"] = "doctor_repulsor",
            ["doctor_repulsor"] = "doctor_repulsor",
            ["drunkenmaster"] = "drunkenmaster",
            ["drunken_master"] = "drunkenmaster",
            ["flint"] = "flint_beastwood",
            ["flintbeastwood"] = "flint_beastwood",
            ["soulstealer"] = "soulstealer",
            ["soul_stealer"] = "soulstealer",
            ["nightmaremadman"] = "scar",
            ["nightmare_madman"] = "scar",
            ["slither"] = "ebulus",
            ["magebane"] = "javaras",
            ["bloodhunter"] = "hunter",
            ["blood_hunter"] = "hunter",
            ["fadepuppet"] = "scar",
            ["fadepuppet/alt"] = "scar/alt"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly SearchValues<char> s_pathSeparators = SearchValues.Create('/', '\\');
    private static readonly SearchValues<char> s_trimQuotes = SearchValues.Create('"', '\'');
    private static readonly SearchValues<char> s_digits = SearchValues.Create("0123456789");

    private readonly List<string> _archivePaths = [];
    private readonly IMemoryCache _cache;

    private readonly Dictionary<string, (int ArchiveIndex, string EntryFullName)> _fileIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _initLock = new();
    private readonly ILogger<GameArchiveVfsService> _logger;
    private readonly Dictionary<string, string> _looseFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPoolSize = Math.Max(Environment.ProcessorCount * 2, 8);
    private readonly string? _overridePath;
    private readonly string? _prebakedPath;
    private readonly ConcurrentBag<ZipArchiveReader> _readerPool = [];
    private readonly List<string> _supplementaryPaths = [];
    private IVfsPrecacheQueue? _precacheQueue;
    private bool _initialized;
    private bool _isEmpty;

    public IVfsPrecacheQueue? PrecacheQueue
    {
        get => _precacheQueue;
        set => _precacheQueue = value;
    }

    public GameArchiveVfsService(
        IConfiguration configuration,
        ILogger<GameArchiveVfsService> logger,
        IMemoryCache? memoryCache = null,
        IPrebakedEntityRegistry? prebakedRegistry = null,
        IVirtualFileSystem? virtualFileSystem = null,
        IVfsPrecacheQueue? precacheQueue = null)
    {
        _logger = logger;
        _cache = memoryCache ?? DefaultCache;
        PrebakedRegistry = prebakedRegistry;
        VirtualFileSystem = virtualFileSystem;
        _precacheQueue = precacheQueue;

        _prebakedPath = configuration["GameClient:PreBakedAssetPath"];
        if (string.IsNullOrWhiteSpace(_prebakedPath))
        {
            string defaultPrebaked = Path.Combine(AppContext.BaseDirectory, "prebaked_assets");
            if (Directory.Exists(defaultPrebaked)) _prebakedPath = defaultPrebaked;
        }

        _overridePath = configuration["GameClient:OverridePath"] ??
                        configuration["OverridePath"];
        if (string.IsNullOrWhiteSpace(_overridePath))
        {
            string defaultOverride = Path.Combine(AppContext.BaseDirectory, "override");
            if (Directory.Exists(defaultOverride)) _overridePath = defaultOverride;
        }

        string? configuredPath = configuration["GameClient:GamePath"] ??
                                 configuration["GameClient:Path"] ??
                                 configuration["GameClient:ClientPath"] ??
                                 configuration["HonClientPath"] ??
                                 configuration["HoN:GameDirectory"] ??
                                 configuration["HonGamePath"] ??
                                 configuration["Parameters:game-path"] ??
                                 configuration["game-path"] ??
                                 configuration["GamePath"] ??
                                 configuration["HON_GAME_PATH"] ??
                                 Environment.GetEnvironmentVariable("HON_GAME_PATH") ??
                                 Environment.GetEnvironmentVariable("HON_CLIENT_PATH") ??
                                 Environment.GetEnvironmentVariable("GAME_CLIENT_PATH") ??
                                 Environment.GetEnvironmentVariable("GAME_PATH");

        if (VirtualFileSystem != null)
        {
            GamePath = !string.IsNullOrWhiteSpace(configuredPath) ? ResolveGamePath(configuredPath) : "";
        }
        else if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            GamePath = ResolveGamePath(configuredPath);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[VFS] Using configured game client path: '{ConfiguredPath}' resolved to '{ResolvedPath}'",
                    configuredPath, GamePath);

            // Also auto-discover repository fallback paths to register as supplementary paths (skip in test environments or temp dirs for performance)
            if (Environment.GetEnvironmentVariable("IS_TEST_ENVIRONMENT") != "true" &&
                !GamePath.Contains("VfsTest", StringComparison.OrdinalIgnoreCase) &&
                !GamePath.Contains("vfs", StringComparison.OrdinalIgnoreCase) &&
                !GamePath.Contains("Temp", StringComparison.OrdinalIgnoreCase) &&
                !GamePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                AutoDiscoverGamePath(out List<string> extraSupplementary);
                foreach (string extra in extraSupplementary)
                    if (!extra.Equals(GamePath, StringComparison.OrdinalIgnoreCase) &&
                        !_supplementaryPaths.Contains(extra))
                        _supplementaryPaths.Add(extra);

                // Register solution games/client/game and games/shared if custom path is external
                string baseDir = AppContext.BaseDirectory;
                DirectoryInfo? curr = new(baseDir);
                while (curr != null)
                {
                    string repoClientGame = Path.Combine(curr.FullName, "games", "client", "game");
                    if (Directory.Exists(repoClientGame) &&
                        !repoClientGame.Equals(GamePath, StringComparison.OrdinalIgnoreCase) &&
                        !_supplementaryPaths.Contains(repoClientGame))
                        _supplementaryPaths.Add(repoClientGame);

                    string repoShared = Path.Combine(curr.FullName, "games", "shared");
                    if (Directory.Exists(repoShared) &&
                        !repoShared.Equals(GamePath, StringComparison.OrdinalIgnoreCase) &&
                        !_supplementaryPaths.Contains(repoShared))
                        _supplementaryPaths.Add(repoShared);

                    curr = curr.Parent;
                }
            }
        }
        else
        {
            GamePath = AutoDiscoverGamePath(out List<string> discoveredSupplementary);
            _supplementaryPaths.AddRange(discoveredSupplementary);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[VFS] Auto-discovered game client path: '{GamePath}' with {Count} supplementary path(s)",
                    GamePath, _supplementaryPaths.Count);
        }

        EnsureInitialized();
    }

    public IPrebakedEntityRegistry? PrebakedRegistry { get; }

    public string GamePath { get; } = string.Empty;

    public IReadOnlyList<string> SupplementaryPaths => _supplementaryPaths;
    public IVirtualFileSystem? VirtualFileSystem { get; }

    public void Dispose()
    {
        while (_readerPool.TryTake(out ZipArchiveReader? reader)) reader.Dispose();
    }

    public bool FileExists(string vfsPath)
    {
        EnsureInitialized();
        string normalized = NormalizePath(vfsPath);
        if (VirtualFileSystem != null && VirtualFileSystem.FileExists(normalized)) return true;
        if (_looseFiles.ContainsKey(normalized)) return true;
        if (_isEmpty && _looseFiles.Count == 0 && VirtualFileSystem == null) return false;
        if (_fileIndex.ContainsKey(normalized)) return true;

        if (normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            string ogg = Path.ChangeExtension(normalized, ".ogg").Replace('\\', '/');
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(ogg)) return true;
            if (_looseFiles.ContainsKey(ogg) || _fileIndex.ContainsKey(ogg)) return true;
        }
        else if (normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            string wav = Path.ChangeExtension(normalized, ".wav").Replace('\\', '/');
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(wav)) return true;
            if (_looseFiles.ContainsKey(wav) || _fileIndex.ContainsKey(wav)) return true;
        }
        else if (normalized.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            string dds = Path.ChangeExtension(normalized, ".dds").Replace('\\', '/');
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(dds)) return true;
            if (_looseFiles.ContainsKey(dds) || _fileIndex.ContainsKey(dds)) return true;
        }

        return false;
    }

    public Stream? OpenFile(string vfsPath)
    {
        EnsureInitialized();
        string normalized = NormalizePath(vfsPath);

        // 0. Precached lock-free memory cache priority lookup (~0 ms)
        if (_precacheQueue != null && _precacheQueue.TryGetCached(normalized, out ReadOnlyMemory<byte> precached))
            return new MemoryStream(precached.ToArray());

        if (VirtualFileSystem != null && VirtualFileSystem.FileExists(normalized))
            return VirtualFileSystem.OpenRead(normalized);

        // 1. Loose files priority lookup
        if (_looseFiles.TryGetValue(normalized, out string? loosePath) && File.Exists(loosePath))
            return new FileStream(loosePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            string ogg = Path.ChangeExtension(normalized, ".ogg").Replace('\\', '/');
            if (_precacheQueue != null && _precacheQueue.TryGetCached(ogg, out ReadOnlyMemory<byte> precachedOgg))
                return new MemoryStream(precachedOgg.ToArray());
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(ogg)) return VirtualFileSystem.OpenRead(ogg);
            if (_looseFiles.TryGetValue(ogg, out string? oggLoose) && File.Exists(oggLoose))
                return new FileStream(oggLoose, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else if (normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            string wav = Path.ChangeExtension(normalized, ".wav").Replace('\\', '/');
            if (_precacheQueue != null && _precacheQueue.TryGetCached(wav, out ReadOnlyMemory<byte> precachedWav))
                return new MemoryStream(precachedWav.ToArray());
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(wav)) return VirtualFileSystem.OpenRead(wav);
            if (_looseFiles.TryGetValue(wav, out string? wavLoose) && File.Exists(wavLoose))
                return new FileStream(wavLoose, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else if (normalized.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            string dds = Path.ChangeExtension(normalized, ".dds").Replace('\\', '/');
            if (_precacheQueue != null && _precacheQueue.TryGetCached(dds, out ReadOnlyMemory<byte> precachedDds))
                return new MemoryStream(precachedDds.ToArray());
            if (VirtualFileSystem != null && VirtualFileSystem.FileExists(dds)) return VirtualFileSystem.OpenRead(dds);
            if (_looseFiles.TryGetValue(dds, out string? ddsLoose) && File.Exists(ddsLoose))
                return new FileStream(ddsLoose, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        if (_isEmpty) return null;

        // 2. Archive index lookup
        if (!_fileIndex.ContainsKey(normalized))
        {
            if (normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                string oggPath = Path.ChangeExtension(normalized, ".ogg").Replace('\\', '/');
                if (_fileIndex.ContainsKey(oggPath)) normalized = oggPath;
            }
            else if (normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                string wavPath = Path.ChangeExtension(normalized, ".wav").Replace('\\', '/');
                if (_fileIndex.ContainsKey(wavPath)) normalized = wavPath;
            }
            else if (normalized.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                string ddsPath = Path.ChangeExtension(normalized, ".dds").Replace('\\', '/');
                if (_fileIndex.ContainsKey(ddsPath)) normalized = ddsPath;
            }
        }

        if (_fileIndex.TryGetValue(normalized, out (int ArchiveIndex, string EntryFullName) target))
        {
            ZipArchiveReader reader = RentReader();
            try
            {
                ZipArchive archive = reader.Archives[target.ArchiveIndex];
                ZipArchiveEntry? entry = archive.GetEntry(target.EntryFullName);
                if (entry != null)
                {
                    MemoryStream ms = new((int)Math.Min(Math.Max(entry.Length, 64), int.MaxValue));
                    using Stream s = entry.Open();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    return ms;
                }
            }
            finally
            {
                ReturnReader(reader);
            }
        }

        return null;
    }

    public byte[]? ReadAllBytes(string vfsPath)
    {
        string normalized = NormalizePath(vfsPath);
        if (_precacheQueue != null && _precacheQueue.TryGetCached(normalized, out ReadOnlyMemory<byte> precachedBytes))
            return precachedBytes.ToArray();

        using Stream? stream = OpenFile(vfsPath);
        if (stream == null) return null;

        if (stream is MemoryStream ms) return ms.ToArray();

        using MemoryStream dest = new();
        stream.CopyTo(dest);
        return dest.ToArray();
    }

    public string? ReadAllText(string vfsPath)
    {
        byte[]? bytes = ReadAllBytes(vfsPath);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
    }

    public IEnumerable<string> FindFiles(string prefix)
    {
        EnsureInitialized();
        string normalizedPrefix = NormalizePath(prefix);
        List<string> matches = [];

        // Add loose files matching prefix
        foreach (string key in _looseFiles.Keys)
            if (key.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                matches.Add(key);

        // Add virtual file system files matching prefix
        if (VirtualFileSystem != null)
            foreach (string file in VirtualFileSystem.EnumerateFiles(string.Empty, "*", SearchOption.AllDirectories))
            {
                string norm = NormalizePath(file);
                if (norm.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) matches.Add(norm);
            }

        // Add archive files matching prefix
        if (!_isEmpty)
            foreach (string key in _fileIndex.Keys)
            {
                string normKey = key.StartsWith("00000000/", StringComparison.OrdinalIgnoreCase) ? key[9..] : key;
                if (normKey.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    matches.Add(normKey);
            }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public byte[]? GetHeroModelGlb(string heroName, string? altAvatar = null, string? resourcePath = null)
    {
        EnsureInitialized();
        if (_isEmpty) return null;
        if (string.IsNullOrWhiteSpace(heroName)) return null;
        string cleanHero = heroName.ToLowerInvariant().Replace("hero_", "").Trim();
        if (HeroFolderAliases.TryGetValue(cleanHero, out string? mappedHero)) cleanHero = mappedHero;

        string cacheKey =
            $"vfs:hero_glb:{cleanHero}|{altAvatar?.Trim().ToLowerInvariant() ?? ""}|{resourcePath?.Trim().ToLowerInvariant() ?? ""}";
        if (_cache.TryGetValue(cacheKey, out byte[]? cachedHeroGlb)) return cachedHeroGlb;

        string cleanAltKey = string.IsNullOrWhiteSpace(altAvatar) ? "default" : altAvatar.Trim().ToLowerInvariant();
        string prebakedFileName = $"{cleanHero}_{cleanAltKey}.glb";
        if (!string.IsNullOrEmpty(_prebakedPath))
        {
            string prebakedFile = Path.Combine(_prebakedPath, "models", prebakedFileName);
            if (File.Exists(prebakedFile))
                try
                {
                    byte[] prebakedBytes = File.ReadAllBytes(prebakedFile);
                    if (prebakedBytes.Length > 0)
                    {
                        MemoryCacheEntryOptions diskOpts = new()
                        {
                            Size = prebakedBytes.Length,
                            SlidingExpiration = TimeSpan.FromHours(4),
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                        };
                        _cache.Set(cacheKey, prebakedBytes, diskOpts);
                        return prebakedBytes;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "[VFS] Failed to read pre-baked GLB file {File}", prebakedFile);
                }
        }

        string? mdfPath = null;

        // 1. Direct Resource Path Resolution (e.g. /heroes/krixi/alt/icon.tga -> heroes/krixi/alt/model.mdf)
        if (!string.IsNullOrWhiteSpace(resourcePath))
        {
            string normRes = NormalizePath(resourcePath);
            string resDir = GetDirectoryName(normRes);
            if (!string.IsNullOrEmpty(resDir))
            {
                string[] candidates =
                [
                    $"{resDir}/model.mdf", $"{resDir}/model_store.mdf", $"{resDir}/model_store_m.mdf",
                    $"{resDir}/model_store_l.mdf", $"{resDir}/preview.mdf"
                ];
                foreach (string candidate in candidates)
                    if (FileExists(candidate))
                    {
                        mdfPath = candidate;
                        break;
                    }
            }
        }

        // 2. Alt Avatar Suffix Resolution
        if (mdfPath == null && !string.IsNullOrWhiteSpace(altAvatar))
        {
            string cleanAlt = altAvatar.ToLowerInvariant().Trim().Replace("hero_", "");
            string[] altCandidates =
            [
                $"heroes/{cleanHero}/{cleanAlt}/model.mdf",
                $"heroes/{cleanHero}/{cleanAlt}/preview.mdf",
                $"heroes/{cleanHero}/{cleanAlt}_skin/model.mdf",
                $"heroes/{cleanHero}/{cleanAlt}_skin/preview.mdf",
                $"heroes/{cleanHero}/trophy_skin/model.mdf",
                $"heroes/{cleanHero}/trophy_skin/preview.mdf",
                $"heroes/{cleanHero}/pog_skin/model.mdf",
                $"heroes/{cleanHero}/pog_skin/preview.mdf",
                $"heroes/{cleanHero}/alt/model.mdf",
                $"heroes/{cleanHero}/alt/preview.mdf",
                $"heroes/{cleanHero}/alt2/model.mdf",
                $"heroes/{cleanHero}/alt3/model.mdf",
                $"heroes/{cleanHero}/alt4/model.mdf",
                $"heroes/{cleanHero}/alt5/model.mdf",
                $"heroes/{cleanHero}/alt6/model.mdf",
                $"heroes/{cleanHero}/alt7/model.mdf",
                $"heroes/{cleanHero}/alt8/model.mdf",
                $"heroes/{cleanHero}/alt9/model.mdf",
                $"heroes/{cleanHero}/alt10/model.mdf",
                $"heroes/{cleanHero}/alt11/model.mdf",
                $"heroes/{cleanHero}/alt12/model.mdf",
                $"heroes/{cleanHero}/alt13/model.mdf",
                $"heroes/{cleanHero}/alt14/model.mdf",
                $"heroes/{cleanHero}/alt15/model.mdf"
            ];

            foreach (string candidate in altCandidates)
                if (FileExists(candidate))
                {
                    mdfPath = candidate;
                    break;
                }

            // Fuzzy match in file index for alt avatar folder
            if (mdfPath == null)
            {
                string altPrefix = $"heroes/{cleanHero}/{cleanAlt}";
                foreach (string key in _fileIndex.Keys)
                    if (key.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase) &&
                        (key.EndsWith("/model.mdf", StringComparison.OrdinalIgnoreCase) ||
                         key.EndsWith("/preview.mdf", StringComparison.OrdinalIgnoreCase)))
                    {
                        mdfPath = key;
                        break;
                    }
            }
        }

        // 3. Base Hero Fallback
        if (mdfPath == null)
        {
            if (FileExists($"heroes/{cleanHero}/model.mdf"))
                mdfPath = $"heroes/{cleanHero}/model.mdf";
            else if (FileExists($"heroes/{cleanHero}/preview.mdf")) mdfPath = $"heroes/{cleanHero}/preview.mdf";
        }

        if (string.IsNullOrEmpty(mdfPath))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "[VFS] Could not find any MDF model for hero '{Hero}', alt '{Alt}', resource '{Resource}'",
                    heroName, altAvatar ?? "none", resourcePath ?? "none");

            return null;
        }

        byte[]? glb = GetModelGlb(mdfPath);
        if (glb != null)
        {
            MemoryCacheEntryOptions options = new()
            {
                Size = glb.Length,
                SlidingExpiration = TimeSpan.FromHours(4),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
            };
            _cache.Set(cacheKey, glb, options);

            if (glb.Length > 0 && !string.IsNullOrEmpty(_prebakedPath))
                try
                {
                    string modelsDir = Path.Combine(_prebakedPath, "models");
                    if (!Directory.Exists(modelsDir)) Directory.CreateDirectory(modelsDir);

                    string prebakedFile = Path.Combine(modelsDir, prebakedFileName);
                    _ = Task.Run(async () =>
                    {
                        if (!await s_diskWriteThrottle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                        {
                            _logger.LogWarning("[VFS] Disk write throttled; skipping background cache write for {Path}",
                                prebakedFile);
                            return;
                        }

                        try
                        {
                            await File.WriteAllBytesAsync(prebakedFile, glb).ConfigureAwait(false);
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug(
                                    "[VFS] Successfully cached prebaked GLB to disk: {Path} ({Bytes} bytes)",
                                    prebakedFile, glb.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[VFS] Failed to write prebaked GLB cache file: {Path}", prebakedFile);
                        }
                        finally
                        {
                            s_diskWriteThrottle.Release();
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VFS] Failed to schedule prebaked GLB write");
                }
        }

        return glb;
    }

    public byte[]? GetModelGlb(string mdfPath)
    {
        EnsureInitialized();
        if (_isEmpty) return null;
        string normalizedMdf = NormalizePath(mdfPath);
        string cacheKey = $"vfs:glb:{normalizedMdf}";

        if (_cache.TryGetValue(cacheKey, out byte[]? cachedGlb)) return cachedGlb;

        K2Model? model = null;
        K2Mdf? mdf = null;
        string mdfDir = GetDirectoryName(normalizedMdf);

        if (normalizedMdf.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
        {
            using Stream? directModelStream = OpenFile(normalizedMdf);
            if (directModelStream == null) return null;

            model = K2ModelReader.Read(directModelStream);

            string companionMdf = normalizedMdf[..^6] + ".mdf";
            if (!FileExists(companionMdf)) companionMdf = CombineVfsPath(mdfDir, "model.mdf");

            if (FileExists(companionMdf))
            {
                using Stream? cMdfStream = OpenFile(companionMdf);
                if (cMdfStream != null) mdf = K2MdfReader.Read(cMdfStream);
            }

            mdf ??= new K2Mdf { ModelFile = Path.GetFileName(normalizedMdf) };
        }
        else
        {
            using Stream? mdfStream = OpenFile(normalizedMdf);
            if (mdfStream == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("[VFS] Could not find MDF file at '{Path}'", normalizedMdf);

                return null;
            }

            mdf = K2MdfReader.Read(mdfStream);
            string modelPath = CombineVfsPath(mdfDir, mdf.ModelFile);

            using Stream? modelStream = OpenFile(modelPath);
            if (modelStream == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("[VFS] Could not find model file at '{Path}' referenced by '{Mdf}'", modelPath,
                        normalizedMdf);

                return null;
            }

            model = K2ModelReader.Read(modelStream);
        }

        // Load primary animation clips
        List<K2Clip> clips = [];
        HashSet<string> loadedClipNames = new(StringComparer.OrdinalIgnoreCase);

        void LoadMdfClips(K2Mdf targetMdf, string targetDir)
        {
            foreach (K2MdfAnim anim in targetMdf.Animations)
            {
                if (string.IsNullOrEmpty(anim.ClipPath) || !loadedClipNames.Add(anim.Name)) continue;

                string clipPath = CombineVfsPath(targetDir, anim.ClipPath);
                using Stream? clipStream = OpenFile(clipPath);
                if (clipStream != null)
                    try
                    {
                        K2Clip clip = K2ClipReader.Read(clipStream, anim.Name);
                        clip.FrameRate = anim.Fps;
                        clips.Add(clip);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug(ex, "[VFS] Could not parse clip '{Clip}' for '{Mdf}'", clipPath,
                                normalizedMdf);
                    }
            }
        }

        LoadMdfClips(mdf, mdfDir);

        // If this is an alt avatar with limited animation clips, inherit remaining animations from parent hero folder
        if (mdfDir.Contains("/alt", StringComparison.OrdinalIgnoreCase) && clips.Count < 6)
        {
            int altIdx = mdfDir.IndexOf("/alt", StringComparison.OrdinalIgnoreCase);
            string baseHeroDir = mdfDir[..altIdx];
            string[] baseMdfCandidates =
            [
                $"{baseHeroDir}/model.mdf",
                $"{baseHeroDir}/preview.mdf"
            ];
            foreach (string baseMdfCandidate in baseMdfCandidates)
            {
                using Stream? baseMdfStream = OpenFile(baseMdfCandidate);
                if (baseMdfStream != null)
                    try
                    {
                        K2Mdf baseMdf = K2MdfReader.Read(baseMdfStream);
                        LoadMdfClips(baseMdf, baseHeroDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[VFS] Failed to read or load clips for base MDF: {HeroDir}", baseHeroDir);
                    }
            }
        }

        // Resolve materials and textures per mesh
        List<K2MeshMaterial> meshMaterials = [];
        Dictionary<string, byte[]> decodedTextureCache = new(StringComparer.OrdinalIgnoreCase);

        byte[]? LoadTexturePng(string texPath, bool forceOpaque = false)
        {
            if (string.IsNullOrWhiteSpace(texPath)) return null;
            string norm = NormalizePath(texPath);
            string cacheKey = (forceOpaque ? "opq:" : "raw:") + norm;
            if (decodedTextureCache.TryGetValue(cacheKey, out byte[]? cached)) return cached;

            List<string> candidates =
            [
                norm,
                CombineVfsPath(mdfDir, texPath),
                $"00000000/{norm}",
                $"00000000/{CombineVfsPath(mdfDir, texPath)}"
            ];

            string ext = Path.GetExtension(norm);
            string withoutExt = string.IsNullOrEmpty(ext) ? norm : norm[..^ext.Length];
            candidates.Add(withoutExt + ".dds");
            candidates.Add(withoutExt + ".tga");
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + ".dds"));
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + ".tga"));
            candidates.Add($"00000000/{withoutExt}.dds");
            candidates.Add($"00000000/{withoutExt}.tga");

            foreach (string cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using Stream? stream = OpenFile(cand);
                if (stream == null) continue;
                try
                {
                    (byte[] rgba, int width, int height) = cand.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                        ? K2DdsReader.DecodeTga(stream)
                        : K2DdsReader.DecodeDds(stream);

                    if (forceOpaque)
                        for (int p = 3; p < rgba.Length; p += 4)
                            rgba[p] = 255;

                    byte[] png = K2DdsReader.EncodeRgbaToPng(rgba, width, height, false);
                    if (png.Length > 20)
                    {
                        decodedTextureCache[cacheKey] = png;
                        return png;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VFS] Failed to decode texture candidate: {Candidate}", cand);
                }
            }

            return null;
        }

        byte[]? LoadNormalPng(string texPath)
        {
            if (string.IsNullOrWhiteSpace(texPath)) return null;
            string norm = NormalizePath(texPath);
            if (decodedTextureCache.TryGetValue("norm:" + norm, out byte[]? cached)) return cached;

            List<string> candidates =
            [
                norm,
                CombineVfsPath(mdfDir, texPath),
                $"00000000/{norm}",
                $"00000000/{CombineVfsPath(mdfDir, texPath)}"
            ];

            string ext = Path.GetExtension(norm);
            string withoutExt = string.IsNullOrEmpty(ext) ? norm : norm[..^ext.Length];
            candidates.Add(withoutExt + "_rxgb.dds");
            candidates.Add(withoutExt + ".dds");
            candidates.Add(withoutExt + ".tga");
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + "_rxgb.dds"));
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + ".dds"));
            candidates.Add($"00000000/{withoutExt}_rxgb.dds");
            candidates.Add($"00000000/{withoutExt}.dds");

            foreach (string cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using Stream? stream = OpenFile(cand);
                if (stream == null) continue;
                try
                {
                    (byte[] rgba, int width, int height) = K2DdsReader.DecodeRxgbNormal(stream);
                    byte[] png = K2DdsReader.EncodeRgbaToPng(rgba, width, height, false);
                    if (png.Length > 20)
                    {
                        decodedTextureCache["norm:" + norm] = png;
                        return png;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VFS] Failed to decode normal texture candidate: {Candidate}", cand);
                }
            }

            return null;
        }

        byte[]? LoadSpecularPng(string texPath)
        {
            if (string.IsNullOrWhiteSpace(texPath)) return null;
            string norm = NormalizePath(texPath);
            if (decodedTextureCache.TryGetValue("spec:" + norm, out byte[]? cached)) return cached;

            List<string> candidates =
            [
                norm,
                CombineVfsPath(mdfDir, texPath),
                $"00000000/{norm}",
                $"00000000/{CombineVfsPath(mdfDir, texPath)}"
            ];

            string ext = Path.GetExtension(norm);
            string withoutExt = string.IsNullOrEmpty(ext) ? norm : norm[..^ext.Length];
            candidates.Add(withoutExt + ".dds");
            candidates.Add(withoutExt + ".tga");
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + ".dds"));
            candidates.Add(CombineVfsPath(mdfDir, withoutExt + ".tga"));
            candidates.Add($"00000000/{withoutExt}.dds");

            foreach (string cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using Stream? stream = OpenFile(cand);
                if (stream == null) continue;
                try
                {
                    (byte[] rgba, int width, int height) = K2DdsReader.DecodeDds(stream);
                    (byte[] pbrRgba, int pbrW, int pbrH) = K2DdsReader.ConvertSpecularToPbr(rgba, width, height);
                    byte[] png = K2DdsReader.EncodeRgbaToPng(pbrRgba, pbrW, pbrH, false);
                    if (png.Length > 20)
                    {
                        decodedTextureCache["spec:" + norm] = png;
                        return png;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VFS] Failed to decode specular texture candidate: {Candidate}", cand);
                }
            }

            return null;
        }

        // Global fallback textures for this MDF folder
        byte[]? globalDiffusePng = null;
        string[] globalCandidates =
        [
            CombineVfsPath(mdfDir, "color.dds"),
            CombineVfsPath(mdfDir, "color.tga"),
            CombineVfsPath(mdfDir, "color_1.dds"),
            CombineVfsPath(mdfDir, "color_1.tga"),
            CombineVfsPath(mdfDir, "model.dds"),
            CombineVfsPath(mdfDir, "diffuse.dds"),
            CombineVfsPath(mdfDir, "texture.dds"),
            CombineVfsPath(mdfDir, "skin.dds")
        ];

        foreach (string glob in globalCandidates)
        {
            globalDiffusePng = LoadTexturePng(glob, true);
            if (globalDiffusePng != null) break;
        }

        byte[]? globalNormalPng = LoadNormalPng(CombineVfsPath(mdfDir, "normal_rxgb.dds")) ??
                                  LoadNormalPng(CombineVfsPath(mdfDir, "normal.dds"));

        byte[]? globalSpecularPng = LoadSpecularPng(CombineVfsPath(mdfDir, "spec_color.dds")) ??
                                    LoadSpecularPng(CombineVfsPath(mdfDir, "specular.dds"));

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            K2Mesh mesh = model.Meshes[i];
            K2MeshMaterial meshMat = new()
            {
                Name = string.IsNullOrEmpty(mesh.Name) ? $"Mesh_{i}" : mesh.Name,
                HasAlpha = false,
                Metallic = 0.0f,
                Roughness = 0.85f
            };

            List<string> matCandidates = [];
            if (!string.IsNullOrEmpty(mesh.DefaultMaterial))
            {
                string def = mesh.DefaultMaterial.Trim();
                matCandidates.Add(CombineVfsPath(mdfDir, def));
                matCandidates.Add(CombineVfsPath(mdfDir, $"{def}.material"));
                matCandidates.Add(def);
            }

            matCandidates.Add(CombineVfsPath(mdfDir, $"{mesh.Name.Trim()}.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "material.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "body.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "gear.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "pedestal.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "model.material"));
            matCandidates.Add(CombineVfsPath(mdfDir, "color.material"));

            string? diffuseSrc = null;
            string? normalSrc = null;
            string? specSrc = null;
            string? glowSrc = null;
            bool isAdditive = false;
            bool hasAlpha = false;

            foreach (string matPath in matCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using Stream? matStream = OpenFile(matPath);
                if (matStream == null) continue;
                try
                {
                    XDocument doc = XDocument.Load(matStream);
                    foreach (XElement phase in doc.Descendants("phase"))
                    {
                        string? vs = phase.Attribute("vs")?.Value;
                        string? ps = phase.Attribute("ps")?.Value;
                        string? srcblend = phase.Attribute("srcblend")?.Value;
                        string? dstblend = phase.Attribute("dstblend")?.Value;
                        string? alphatest = phase.Attribute("alphatest")?.Value;
                        string? translucent = phase.Attribute("translucent")?.Value;

                        if (string.Equals(translucent, "true", StringComparison.OrdinalIgnoreCase)) hasAlpha = true;

                        if ((vs != null && (vs.Contains("trophy", StringComparison.OrdinalIgnoreCase) ||
                                            vs.Contains("gold", StringComparison.OrdinalIgnoreCase))) ||
                            (ps != null && (ps.Contains("trophy", StringComparison.OrdinalIgnoreCase) ||
                                            ps.Contains("gold", StringComparison.OrdinalIgnoreCase))))
                        {
                            meshMat.Metallic = 0.85f;
                            meshMat.Roughness = 0.20f;
                        }

                        if ((vs != null && vs.Contains("unlit", StringComparison.OrdinalIgnoreCase)) ||
                            (ps != null && ps.Contains("unlit", StringComparison.OrdinalIgnoreCase)) ||
                            (srcblend == "BLEND_ONE" && dstblend == "BLEND_ONE") ||
                            (srcblend == "BLEND_SRC_ALPHA" && dstblend == "BLEND_ONE"))
                            isAdditive = true;
                    }

                    foreach (XElement sampler in doc.Descendants("sampler"))
                    {
                        string? name = sampler.Attribute("name")?.Value;
                        string? src = sampler.Attribute("texture")?.Value ?? sampler.Attribute("src")?.Value;
                        if (string.IsNullOrEmpty(src) || src.StartsWith('$')) continue;

                        if (diffuseSrc == null && (string.Equals(name, "diffuse", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(name, "color", StringComparison.OrdinalIgnoreCase)))
                            diffuseSrc = CombineVfsPath(mdfDir, src);
                        else if (normalSrc == null &&
                                 (string.Equals(name, "normalmap", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(name, "normal", StringComparison.OrdinalIgnoreCase)))
                            normalSrc = CombineVfsPath(mdfDir, src);
                        else if (specSrc == null &&
                                 (string.Equals(name, "specular", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(name, "spec", StringComparison.OrdinalIgnoreCase)))
                            specSrc = CombineVfsPath(mdfDir, src);
                        else if (glowSrc == null &&
                                 (string.Equals(name, "lightmap", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(name, "glow", StringComparison.OrdinalIgnoreCase)))
                            glowSrc = CombineVfsPath(mdfDir, src);
                    }

                    if (diffuseSrc != null) break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VFS] Failed to parse material candidate: {Candidate}", matPath);
                }
            }

            byte[]? meshDiffusePng = null;
            if (diffuseSrc != null) meshDiffusePng = LoadTexturePng(diffuseSrc, !isAdditive);

            meshMat.DiffusePngBytes = meshDiffusePng ?? globalDiffusePng;
            meshMat.NormalPngBytes = (normalSrc != null ? LoadNormalPng(normalSrc) : null) ?? globalNormalPng;
            meshMat.MetallicRoughnessPngBytes =
                (specSrc != null ? LoadSpecularPng(specSrc) : null) ?? globalSpecularPng;

            if (glowSrc != null)
                meshMat.GlowPngBytes = LoadTexturePng(glowSrc, false);
            else
                meshMat.GlowPngBytes = LoadTexturePng(CombineVfsPath(mdfDir, "glow2.dds"), false) ??
                                       LoadTexturePng(CombineVfsPath(mdfDir, "glow2.tga"), false) ??
                                       LoadTexturePng(CombineVfsPath(mdfDir, "glow.dds"), false) ??
                                       LoadTexturePng(CombineVfsPath(mdfDir, "glow.tga"), false) ??
                                       LoadTexturePng(CombineVfsPath(mdfDir, "lightmap.dds"), false);

            meshMat.HasAlpha = hasAlpha;
            meshMat.IsAdditive = isAdditive;
            meshMaterials.Add(meshMat);
        }

        byte[] glbBytes = K2ToGltfConverter.ConvertToGlb(model, clips, globalDiffusePng, meshMaterials);
        MemoryCacheEntryOptions cacheOptions = new()
        {
            Size = glbBytes.Length,
            SlidingExpiration = TimeSpan.FromHours(4),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
        };
        _cache.Set(cacheKey, glbBytes, cacheOptions);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "[VFS] Generated and cached .glb for '{Path}' ({SizeKb} KB, {Meshes} meshes, {Clips} clips)",
                normalizedMdf, glbBytes.Length / 1024, model.Meshes.Count, clips.Count);

        return glbBytes;
    }

    public bool PrebakeHeroModel(string heroName, string? altAvatar = null)
    {
        byte[]? glb = GetHeroModelGlb(heroName, altAvatar);
        if (_precacheQueue != null && !string.IsNullOrWhiteSpace(heroName))
        {
            string cleanHero = heroName.ToLowerInvariant().Replace("hero_", "").Trim();
            if (HeroFolderAliases.TryGetValue(cleanHero, out string? mappedHero)) cleanHero = mappedHero;
            _precacheQueue.Enqueue($"heroes/{cleanHero}/hero.entity", VfsAssetPriority.High);
            _precacheQueue.Enqueue($"heroes/{cleanHero}/icon.tga", VfsAssetPriority.High);
        }
        return glb != null && glb.Length > 0;
    }

    public byte[]? GetTextureBytes(string texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;

        string normalized = NormalizePath(texturePath);
        string cacheKey = $"vfs:tex:{normalized}";
        if (_cache.TryGetValue(cacheKey, out byte[]? cachedBytes)) return cachedBytes;

        byte[]? bytes = ReadAllBytes(texturePath);
        if (bytes != null)
        {
            MemoryCacheEntryOptions options = new()
            {
                Size = bytes.Length,
                SlidingExpiration = TimeSpan.FromHours(4),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
            };
            _cache.Set(cacheKey, bytes, options);
        }

        return bytes;
    }

    public byte[]? GetTexturePng(string texturePath)
    {
        EnsureInitialized();
        if (_isEmpty) return null;
        if (string.IsNullOrWhiteSpace(texturePath)) return null;

        string normalized = NormalizePath(texturePath);
        string cacheKey = $"vfs:png:{normalized}";
        if (_cache.TryGetValue(cacheKey, out byte[]? cachedPng)) return cachedPng;

        string ext = Path.GetExtension(normalized);
        string withoutExt = string.IsNullOrEmpty(ext) ? normalized : normalized[..^ext.Length];

        List<string> candidates =
        [
            normalized,
            $"00000000/{normalized}",
            withoutExt + ".dds",
            $"00000000/{withoutExt}.dds",
            withoutExt + ".tga",
            $"00000000/{withoutExt}.tga",
            withoutExt + ".png",
            $"00000000/{withoutExt}.png"
        ];

        if (normalized.Contains("gamefeature_taunt", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("ui/icons/gamefeature_taunt.dds");
            candidates.Add("shared/icons/gamefeature_taunt.dds");
            candidates.Add("ui/elements/gamefeature_taunt.dds");
        }

        if (normalized.StartsWith("ui/fe2/store/icons/", StringComparison.OrdinalIgnoreCase))
        {
            string sub = normalized["ui/fe2/store/icons/".Length..];
            string subWithoutExt = string.IsNullOrEmpty(ext) ? sub : sub[..^ext.Length];
            candidates.Add("ui/icons/" + subWithoutExt + ".dds");
            candidates.Add("ui/icons/" + sub);
            candidates.Add("shared/icons/" + subWithoutExt + ".dds");
            candidates.Add("ui/common/account_icons/" + subWithoutExt + ".dds");
            candidates.Add("ui/elements/" + subWithoutExt + ".dds");
        }

        if (normalized.StartsWith("ui/icons/flags/", StringComparison.OrdinalIgnoreCase))
        {
            string sub = normalized["ui/icons/flags/".Length..];
            string subWithoutExt = string.IsNullOrEmpty(ext) ? sub : sub[..^ext.Length];
            candidates.Add("ui/common/flags/" + subWithoutExt + ".dds");
            candidates.Add("ui/common/flags/" + sub);
        }

        if (normalized.Contains("/alt", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith("icon.tga", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(withoutExt + "/icon.dds");
            candidates.Add(withoutExt + "/hero.dds");
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using Stream? stream = OpenFile(candidate);
            if (stream == null) continue;

            try
            {
                if (candidate.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    (byte[] rgba, int width, int height) = K2DdsReader.DecodeDds(stream);
                    byte[] png = K2DdsReader.EncodeRgbaToPng(rgba, width, height, true);
                    if (png.Length > 20)
                    {
                        MemoryCacheEntryOptions options = new()
                        {
                            Size = png.Length,
                            SlidingExpiration = TimeSpan.FromHours(4),
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                        };
                        _cache.Set(cacheKey, png, options);
                        return png;
                    }
                }
                else if (candidate.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    (byte[] rgba, int width, int height) = K2DdsReader.DecodeTga(stream);
                    byte[] png = K2DdsReader.EncodeRgbaToPng(rgba, width, height, false);
                    if (png.Length > 20)
                    {
                        MemoryCacheEntryOptions options = new()
                        {
                            Size = png.Length,
                            SlidingExpiration = TimeSpan.FromHours(4),
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                        };
                        _cache.Set(cacheKey, png, options);
                        return png;
                    }
                }
                else if (candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    using MemoryStream ms = new();
                    stream.CopyTo(ms);
                    byte[] png = ms.ToArray();
                    MemoryCacheEntryOptions options = new()
                    {
                        Size = png.Length,
                        SlidingExpiration = TimeSpan.FromHours(4),
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                    };
                    _cache.Set(cacheKey, png, options);
                    return png;
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "[VFS] Could not convert texture candidate '{Candidate}' to PNG", candidate);
            }
        }

        return null;
    }

    public byte[]? GetHeroSound(string heroName, string? altAvatar = null, string? actionName = null)
    {
        EnsureInitialized();
        if (_isEmpty) return null;
        string cleanHero = heroName.Trim().ToLowerInvariant().Replace("hero_", "");
        if (HeroFolderAliases.TryGetValue(cleanHero, out string? mappedHero)) cleanHero = mappedHero;

        string cleanAlt = altAvatar?.Trim().ToLowerInvariant() ?? string.Empty;
        string cleanAction = actionName?.Trim().ToLowerInvariant() ?? "attack";

        // Direct path lookup
        if (cleanAction.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
            cleanAction.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
            cleanAction.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
            cleanAction.Contains('/'))
        {
            byte[]? direct = ReadAllBytes(cleanAction);
            if (direct != null) return direct;
        }

        List<string> dirCandidates = [];
        if (!string.IsNullOrEmpty(cleanAlt) && cleanAlt != "default" && cleanAlt != "base")
        {
            dirCandidates.Add($"heroes/{cleanHero}/{cleanAlt}/");
            dirCandidates.Add($"heroes/{cleanHero}/{cleanAlt}/sounds/");
            dirCandidates.Add($"heroes/{cleanHero}/{cleanAlt}/sounds/voice/");
            dirCandidates.Add($"heroes/{cleanHero}/alt/");
            dirCandidates.Add($"heroes/{cleanHero}/alt/sounds/");
            dirCandidates.Add($"heroes/{cleanHero}/alt/sounds/voice/");
        }

        dirCandidates.Add($"heroes/{cleanHero}/");
        dirCandidates.Add($"heroes/{cleanHero}/sounds/");
        dirCandidates.Add($"heroes/{cleanHero}/sounds/voice/");
        dirCandidates.Add("shared/");
        dirCandidates.Add("shared/sounds/");
        dirCandidates.Add("shared/sounds/attacks/");
        dirCandidates.Add("shared/sounds/footsteps/");

        List<string> fileCandidates = [];
        if (cleanAction.Contains("attack"))
        {
            if (cleanAction.Contains('1'))
                fileCandidates.AddRange([
                    "sounds/attack_1.ogg", "sounds/attack_1.wav", "attack_1.ogg", "attack_1.wav",
                    "sounds/attack_action_1.ogg", "sounds/attack_action_1.wav",
                    "sounds/attack_grunt_1.ogg", "sounds/attack_grunt_1.wav", "attack_grunt_1.ogg",
                    "attack_grunt_1.wav",
                    "sounds/impact_1.ogg", "sounds/impact_1.wav", "sounds/swing_1.wav", "sounds/whoosh_1.wav"
                ]);
            else if (cleanAction.Contains('2'))
                fileCandidates.AddRange([
                    "sounds/attack_2.ogg", "sounds/attack_2.wav", "attack_2.ogg", "attack_2.wav",
                    "sounds/attack_action_2.ogg", "sounds/attack_action_2.wav",
                    "sounds/attack_grunt_2.ogg", "sounds/attack_grunt_2.wav", "attack_grunt_2.ogg",
                    "attack_grunt_2.wav",
                    "sounds/impact_2.ogg", "sounds/impact_2.wav", "sounds/swing_2.wav", "sounds/whoosh_2.wav"
                ]);

            fileCandidates.AddRange([
                "sounds/attack_1.ogg", "sounds/attack_1.wav", "sounds/attack_2.ogg", "sounds/attack_2.wav",
                "attack_1.ogg", "attack_1.wav", "attack_2.ogg", "attack_2.wav",
                "sounds/attack_action_1.wav", "sounds/attack_action_2.wav",
                "attack_grunt_1.ogg", "attack_grunt_1.wav", "attack_grunt_2.ogg", "attack_grunt_2.wav",
                "sounds/impact_1.ogg", "sounds/impact_1.wav", "sounds/impact_2.ogg", "sounds/impact_2.wav",
                "sounds/swing_1.wav", "sounds/swing_2.wav", "sounds/whoosh_1.wav", "sounds/whoosh_2.wav",
                "attack_medium_1.wav", "attack_heavy_1.wav", "attack_light_1.wav", "slash_1.wav", "blunt_1.wav",
                "swipe_1.wav"
            ]);
        }
        else if (cleanAction.Contains("walk") || cleanAction.Contains("move") || cleanAction.Contains("run"))
        {
            fileCandidates.AddRange([
                "sounds/move_1.ogg", "sounds/move_1.wav", "sounds/move_2.ogg", "sounds/move_2.wav",
                "sounds/footstep_1.ogg", "sounds/footstep_1.wav", "sounds/footstep_2.ogg", "sounds/footstep_2.wav",
                "move_1.ogg", "move_1.wav", "move_2.ogg", "move_2.wav",
                "footstep_1.ogg", "footstep_1.wav", "footstep_2.ogg", "footstep_2.wav",
                "walk_1.ogg", "walk_1.wav"
            ]);
        }
        else if (cleanAction.Contains("ability") || cleanAction.Contains("cast") || cleanAction.Contains("spell"))
        {
            if (cleanAction.Contains('1'))
                fileCandidates.AddRange([
                    "ability_01/sounds/cast.ogg", "ability_01/sounds/cast.wav",
                    "ability_01/sounds/ability.ogg", "ability_01/sounds/ability.wav",
                    "ability_01/sounds/ability_01.ogg", "ability_01/sounds/ability_01.wav",
                    "ability_01/sounds/cast_1.ogg", "ability_01/sounds/cast_1.wav",
                    "ability_01/sounds/impact.ogg", "ability_01/sounds/impact.wav",
                    "ability_01/sounds/attack_1.ogg", "ability_01/sounds/attack_1.wav",
                    "ability_1/sounds/cast.ogg", "ability_1/sounds/cast.wav",
                    "sounds/ability_1.ogg", "sounds/ability_1.wav",
                    "sounds/cast_1.ogg", "sounds/cast_1.wav"
                ]);
            else if (cleanAction.Contains('2'))
                fileCandidates.AddRange([
                    "ability_02/sounds/impact.ogg", "ability_02/sounds/impact.wav",
                    "ability_02/sounds/cast.ogg", "ability_02/sounds/cast.wav",
                    "ability_02/sounds/ability.ogg", "ability_02/sounds/ability.wav",
                    "ability_02/sounds/ability_02.ogg", "ability_02/sounds/ability_02.wav",
                    "ability_02/sounds/revert.ogg", "ability_02/sounds/revert.wav",
                    "ability_2/sounds/cast.ogg", "ability_2/sounds/cast.wav",
                    "sounds/ability_2.ogg", "sounds/ability_2.wav",
                    "sounds/cast_2.ogg", "sounds/cast_2.wav"
                ]);
            else if (cleanAction.Contains('3'))
                fileCandidates.AddRange([
                    "ability_03/sounds/cast.ogg", "ability_03/sounds/cast.wav",
                    "ability_03/sounds/ability_03.ogg", "ability_03/sounds/ability_03.wav",
                    "ability_03/sounds/ability.ogg", "ability_03/sounds/ability.wav",
                    "ability_03/sounds/state.ogg", "ability_03/sounds/state.wav",
                    "ability_3/sounds/cast.ogg", "ability_3/sounds/cast.wav",
                    "sounds/ability_3.ogg", "sounds/ability_3.wav",
                    "sounds/cast_3.ogg", "sounds/cast_3.wav"
                ]);
            else if (cleanAction.Contains('4') || cleanAction.Contains("ult"))
                fileCandidates.AddRange([
                    "ability_04/sounds/ability_04_cast.ogg", "ability_04/sounds/ability_04_cast.wav",
                    "ability_04/sounds/cast.ogg", "ability_04/sounds/cast.wav",
                    "ability_04/sounds/ability_04_attack.ogg", "ability_04/sounds/ability_04_grunt.wav",
                    "ability_04/sounds/ability_04.ogg", "ability_04/sounds/ability_04.wav",
                    "ability_04/sounds/impact.ogg", "ability_04/sounds/impact.wav",
                    "ability_4/sounds/cast.ogg", "ability_4/sounds/cast.wav",
                    "sounds/ability_4.ogg", "sounds/ability_4.wav",
                    "sounds/cast_4.ogg", "sounds/cast_4.wav"
                ]);

            fileCandidates.AddRange([
                "ability_01/sounds/cast.ogg", "ability_01/sounds/cast.wav",
                "ability_02/sounds/cast.ogg", "ability_02/sounds/cast.wav",
                "ability_03/sounds/cast.ogg", "ability_03/sounds/cast.wav",
                "ability_04/sounds/ability_04_cast.ogg", "ability_04/sounds/cast.ogg",
                "sounds/cast.wav", "sounds/cast.ogg", "cast.wav", "cast.ogg", "cast_1.wav", "ability_1.wav",
                "ability_1.ogg"
            ]);
        }
        else if (cleanAction.Contains("taunt") || cleanAction.Contains("bored") || cleanAction.Contains("idle"))
        {
            fileCandidates.AddRange([
                "select_1.ogg", "select_2.ogg", "select_3.ogg", "select_4.ogg", "select_5.ogg",
                "select_flavour_1.ogg", "select_flavour_2.ogg", "select_flavour_3.ogg",
                "taunt_prior.ogg", "taunt_after.ogg",
                "taunt.wav", "taunt_1.wav", "bored_1.wav", "idle.wav", "idle_1.wav",
                "taunt.ogg", "taunt_1.ogg", "bored_1.ogg", "idle.ogg", "idle_1.ogg",
                "sounds/taunt.ogg", "sounds/taunt.wav", "sounds/bored_1.wav", "sounds/idle.wav",
                "sounds/voice/select_1.ogg", "sounds/voice/select_2.ogg", "sounds/voice/taunt_prior.ogg"
            ]);
        }
        else if (cleanAction.Contains("death"))
        {
            fileCandidates.AddRange([
                "death.wav", "death_1.wav", "death.ogg", "death_1.ogg",
                "death_grunt.wav", "death_grunt_1.wav", "death_grunt.ogg",
                "death_foley.ogg", "sounds/death.wav", "sounds/death.ogg"
            ]);
        }
        else
        {
            fileCandidates.AddRange([
                $"{cleanAction}.wav", $"{cleanAction}.ogg", $"sounds/{cleanAction}.wav", $"sounds/{cleanAction}.ogg"
            ]);
        }

        foreach (string dir in dirCandidates)
        foreach (string file in fileCandidates)
        {
            string path = dir + file;
            byte[]? bytes = ReadAllBytes(path);
            if (bytes is { Length: > 0 }) return bytes;
        }

        return null;
    }

    public HeroEntityInfoDto? GetHeroEntityInfo(string heroName)
    {
        EnsureInitialized();
        if (_isEmpty && VirtualFileSystem == null) return null;
        if (string.IsNullOrWhiteSpace(heroName)) return null;

        string cleanHero = heroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (HeroFolderAliases.TryGetValue(cleanHero, out string? mappedHero)) cleanHero = mappedHero;

        string cacheKey = $"vfs:hero_info:{cleanHero}";
        if (_cache.TryGetValue(cacheKey, out HeroEntityInfoDto? cachedInfo)) return cachedInfo;

        string displayName = cleanHero;
        string primaryAttr = "Intelligence";
        float difficulty = 2.0f;

        string[] entityCandidates =
        [
            $"heroes/{cleanHero}/hero.entity",
            $"heroes/{cleanHero}/hero_{cleanHero}.entity",
            $"heroes/{cleanHero}/entity.entity"
        ];

        foreach (string candidate in entityCandidates)
        {
            using Stream? stream = OpenFile(candidate);
            if (stream == null) continue;
            try
            {
                XDocument doc = XDocument.Load(stream);
                XElement? heroEl = doc.Element("hero");
                if (heroEl != null)
                {
                    displayName = heroEl.Attribute("displayname")?.Value ?? displayName;
                    primaryAttr = heroEl.Attribute("primaryattribute")?.Value ?? primaryAttr;
                    string diffStr = heroEl.Attribute("difficulty")?.Value ?? "2";
                    float.TryParse(diffStr, NumberStyles.Float, CultureInfo.InvariantCulture, out difficulty);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VFS] Failed to load hero definition XML from candidate: {Candidate}", candidate);
            }
        }

        string heroIcon = FileExists($"heroes/{cleanHero}/icons/hero.dds")
            ? $"/heroes/{cleanHero}/icons/hero.dds"
            : $"/heroes/{cleanHero}/icon.tga";

        List<string> abilityIcons = [];
        for (int i = 1; i <= 4; i++)
        {
            string abPath = FileExists($"heroes/{cleanHero}/icons/ability_{i}.dds")
                ? $"/heroes/{cleanHero}/icons/ability_{i}.dds"
                : $"/heroes/{cleanHero}/ability_0{i}/icon.tga";
            abilityIcons.Add(abPath);
        }

        string roles = primaryAttr switch
        {
            "Strength" => "Melee Tank Carry Initiator",
            "Agility" => "Ranged Carry Ganker Solo",
            "Intelligence" => "Support Nuker Disabler Pusher",
            _ => "Hero"
        };

        // Ability props discovery across hero folder and alt avatars
        List<HeroAbilityPropDto> abilityProps = [];
        List<string> allHeroFiles = [.. FindFiles($"heroes/{cleanHero}")];

        for (int i = 1; i <= 4; i++)
        {
            string slotPattern1 = $"/ability_0{i}/";
            string slotPattern2 = $"/ability_{i}/";

            List<string> matchingModels =
            [
                .. allHeroFiles
                    .Where(f => (f.Contains(slotPattern1, StringComparison.OrdinalIgnoreCase) ||
                                 f.Contains(slotPattern2, StringComparison.OrdinalIgnoreCase)) &&
                                (f.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase)))
            ];

            foreach (string modelFile in matchingModels)
            {
                string propName = Path.GetFileNameWithoutExtension(modelFile).ToLowerInvariant();
                if (propName is "model" or "high" or "med" or "low")
                {
                    string? parentDir = Path.GetDirectoryName(modelFile)?.Replace('\\', '/');
                    propName = !string.IsNullOrEmpty(parentDir)
                        ? Path.GetFileName(parentDir).ToLowerInvariant()
                        : propName;
                }

                bool isProjectile = modelFile.Contains("projectile", StringComparison.OrdinalIgnoreCase) ||
                                    modelFile.Contains("hook", StringComparison.OrdinalIgnoreCase) ||
                                    modelFile.Contains("spear", StringComparison.OrdinalIgnoreCase) ||
                                    modelFile.Contains("arrow", StringComparison.OrdinalIgnoreCase);

                string attachBone = isProjectile ? "_bone_weapon3" : "_bone_weapon1";

                abilityProps.Add(new HeroAbilityPropDto
                {
                    AbilitySlot = $"ability_{i}",
                    PropName = propName,
                    ModelPath = modelFile,
                    AttachBone = attachBone,
                    IsProjectile = isProjectile,
                    GlbUrl = $"/api/v1/assets/3d/model?path={Uri.EscapeDataString(modelFile)}"
                });
            }
        }

        // Animation detail and FrameEvents
        List<HeroAnimDetailDto> animDetails = [];
        string mdfPath = $"heroes/{cleanHero}/model.mdf";
        Stream? mdfStream = OpenFile(mdfPath);
        if (mdfStream != null)
            using (mdfStream)
            {
                K2Mdf parsedMdf = K2MdfReader.Read(mdfStream);
                foreach (K2MdfAnim anim in parsedMdf.Animations)
                {
                    List<HeroAnimFrameEventDto> frameEvents = [];
                    foreach (K2MdfFrameEvent fe in anim.FrameEvents)
                        frameEvents.Add(new HeroAnimFrameEventDto
                        {
                            Frame = fe.Frame,
                            SoundAction = fe.SoundPath,
                            EffectType = fe.EffectPath
                        });

                    animDetails.Add(new HeroAnimDetailDto
                    {
                        Name = anim.Name,
                        Fps = anim.Fps,
                        Loop = anim.Loop,
                        FrameEvents = frameEvents
                    });
                }
            }

        HeroEntityInfoDto info = new()
        {
            HeroName = cleanHero,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? cleanHero : displayName,
            IconPath = heroIcon,
            PrimaryAttribute = primaryAttr,
            Difficulty = difficulty > 0 ? difficulty : 2.0f,
            Roles = roles,
            AbilityIcons = abilityIcons,
            AbilityProps = abilityProps,
            Animations = animDetails
        };
        MemoryCacheEntryOptions heroInfoOptions = new()
        {
            Size = 1024,
            SlidingExpiration = TimeSpan.FromHours(4),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
        };
        _cache.Set(cacheKey, info, heroInfoOptions);
        return info;
    }

    private ZipArchiveReader RentReader()
    {
        if (_readerPool.TryTake(out ZipArchiveReader? reader)) return reader;

        return new ZipArchiveReader(GamePath, [.. _archivePaths]);
    }

    private void ReturnReader(ZipArchiveReader reader)
    {
        if (_readerPool.Count < _maxPoolSize)
            _readerPool.Add(reader);
        else
            reader.Dispose();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            if (string.IsNullOrEmpty(GamePath) || !Directory.Exists(GamePath))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "[VFS] Game directory path '{Path}' does not exist. VFS will operate in empty mode.",
                        GamePath);

                _isEmpty = true;
                _initialized = true;
                return;
            }

            if (s_pathIndexCache.TryGetValue(GamePath, out GamePathIndex? cachedIndex))
            {
                _archivePaths.Clear();
                _archivePaths.AddRange(cachedIndex.ArchivePaths);
                _fileIndex.Clear();
                foreach (KeyValuePair<string, (int ArchiveIndex, string EntryFullName)> kvp in cachedIndex.FileIndex)
                    _fileIndex[kvp.Key] = kvp.Value;

                _initialized = true;
                return;
            }

            while (_readerPool.TryTake(out ZipArchiveReader? pooledReader)) pooledReader.Dispose();

            _archivePaths.Clear();
            _fileIndex.Clear();
            _looseFiles.Clear();

            // 0. Collect all archive and loose file search directories (primary + supplementary)
            List<string> searchDirs = [GamePath];
            foreach (string supp in _supplementaryPaths)
                if (!string.IsNullOrWhiteSpace(supp) && Directory.Exists(supp) && !searchDirs.Contains(supp))
                    searchDirs.Add(supp);

            // Candidate directories for archives (including mods/ folders)
            List<string> allCandidateArchiveDirs = [];
            foreach (string dir in searchDirs)
            {
                allCandidateArchiveDirs.Add(dir);
                string modsDir = Path.Combine(dir, "mods");
                if (Directory.Exists(modsDir)) allCandidateArchiveDirs.Add(modsDir);
                string parentMods = Path.Combine(dir, "..", "mods");
                if (Directory.Exists(parentMods) && !allCandidateArchiveDirs.Contains(parentMods))
                    allCandidateArchiveDirs.Add(parentMods);
            }

            // 1. Discover and Index Loose Files (Highest priority loose files across override, gamePath, and supplementary paths)
            List<string> looseCandidateDirs = [];
            if (!string.IsNullOrWhiteSpace(_overridePath) && Directory.Exists(_overridePath))
                looseCandidateDirs.Add(_overridePath);

            string clientOverride = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "override");
            if (Directory.Exists(clientOverride) && !looseCandidateDirs.Contains(clientOverride))
                looseCandidateDirs.Add(clientOverride);

            // Standard subdirectories on disk
            string[] looseSubdirs = ["heroes", "textures", "ui", "items", "shared", "npcs", "pets", "buildings"];
            foreach (string baseDir in searchDirs)
            foreach (string sub in looseSubdirs)
            {
                string subPath = Path.Combine(baseDir, sub);
                if (Directory.Exists(subPath) && !looseCandidateDirs.Contains(subPath)) looseCandidateDirs.Add(subPath);
            }

            foreach (string dir in looseCandidateDirs)
                try
                {
                    string rootForRel = looseSubdirs.Any(s =>
                        dir.EndsWith(Path.DirectorySeparatorChar + s, StringComparison.OrdinalIgnoreCase) ||
                        dir.EndsWith("/" + s, StringComparison.OrdinalIgnoreCase))
                        ? Path.GetDirectoryName(dir)!
                        : dir;

                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(rootForRel, file);
                        string norm = NormalizePath(rel);
                        _looseFiles.TryAdd(norm, file);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "[VFS] Failed scanning loose directory '{Dir}'", dir);
                }

            if (_logger.IsEnabled(LogLevel.Information) && _looseFiles.Count > 0)
                _logger.LogInformation("[VFS] Indexed {Count} loose files across {DirCount} loose directories",
                    _looseFiles.Count, looseCandidateDirs.Count);

            // 2. Discover and order all .s2z and .resources archive files
            List<string> orderedArchiveFullPaths = [];

            // A. Pre-baked archive candidates first (resources0_prebaked.s2z)
            string[] prebakedS2zCandidates =
            [
                Path.Combine(_prebakedPath ?? "", "resources0_prebaked.s2z"),
                Path.Combine(GamePath, "resources0_prebaked.s2z"),
                Path.Combine(AppContext.BaseDirectory, "override", "resources0_prebaked.s2z"),
                Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "override", "resources0_prebaked.s2z")
            ];
            foreach (string pbCandidate in prebakedS2zCandidates)
                if (File.Exists(pbCandidate) && !orderedArchiveFullPaths.Contains(pbCandidate))
                {
                    orderedArchiveFullPaths.Add(pbCandidate);
                    break;
                }

            // B. Standard resources0..20 in order from primary to supplementary
            for (int r = 0; r <= 20; r++)
            {
                string rName = $"resources{r}.s2z";
                foreach (string dir in allCandidateArchiveDirs)
                {
                    string candidate = Path.Combine(dir, rName);
                    if (File.Exists(candidate) && !orderedArchiveFullPaths.Contains(candidate))
                        orderedArchiveFullPaths.Add(candidate);
                }
            }

            // C. Textures, UI, Game archives
            string[] standardExtraArchives = ["textures.s2z", "ui.s2z", "game.s2z"];
            foreach (string extra in standardExtraArchives)
            foreach (string dir in allCandidateArchiveDirs)
            {
                string candidate = Path.Combine(dir, extra);
                if (File.Exists(candidate) && !orderedArchiveFullPaths.Contains(candidate))
                    orderedArchiveFullPaths.Add(candidate);
            }

            // D. Custom avatar archives (e.g. resources_avatar_*.s2z) and other *.s2z / *.resources
            foreach (string dir in allCandidateArchiveDirs)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (string archiveFile in
                             Directory.EnumerateFiles(dir, "*.s2z", SearchOption.TopDirectoryOnly))
                        if (!orderedArchiveFullPaths.Contains(archiveFile))
                            orderedArchiveFullPaths.Add(archiveFile);

                    foreach (string archiveFile in Directory.EnumerateFiles(dir, "*.resources",
                                 SearchOption.TopDirectoryOnly))
                        if (!orderedArchiveFullPaths.Contains(archiveFile))
                            orderedArchiveFullPaths.Add(archiveFile);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "[VFS] Failed enumerating archives in directory '{Dir}'", dir);
                }
            }

            // 3. Load and Index all archives
            byte[] magic = new byte[4];
            foreach (string fullPath in orderedArchiveFullPaths)
            {
                if (!File.Exists(fullPath)) continue;

                try
                {
                    using FileStream fs = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < 4) continue;

                    int readBytes = fs.Read(magic, 0, 4);
                    if (readBytes < 4 || magic[0] != 0x50 || magic[1] != 0x4B)
                    {
                        // File is not a ZIP/S2Z archive (e.g., plain XML *.resources descriptor), skip indexing as archive
                        continue;
                    }
                    fs.Position = 0;

                    using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                    int archiveIndex = _archivePaths.Count;
                    _archivePaths.Add(fullPath);

                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string normalizedKey = NormalizePath(entry.FullName);
                        // Higher priority archive entries win
                        if (!_fileIndex.ContainsKey(normalizedKey))
                            _fileIndex[normalizedKey] = (archiveIndex, entry.FullName);

                        // Also index texture entries stripped of resolution prefix (e.g., 00000000/heroes/..., 00000003/...)
                        int slashIdx = normalizedKey.AsSpan().IndexOfAny(s_pathSeparators);
                        if (slashIdx == 8 && s_digits.Contains(normalizedKey[0]) && s_digits.Contains(normalizedKey[1]))
                        {
                            string strippedKey = normalizedKey[(slashIdx + 1)..];
                            if (!_fileIndex.ContainsKey(strippedKey))
                                _fileIndex[strippedKey] = (archiveIndex, entry.FullName);
                        }
                    }

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("[VFS] Loaded archive '{Archive}' ({SizeMB:F2} MB) with {Count} entries",
                            Path.GetFileName(fullPath), fs.Length / 1048576.0, zip.Entries.Count);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "[VFS] Failed to open archive '{Archive}'", fullPath);
                }
            }

            s_pathIndexCache[GamePath] = new GamePathIndex(
                [.. _archivePaths],
                new Dictionary<string, (int ArchiveIndex, string EntryFullName)>(_fileIndex,
                    StringComparer.OrdinalIgnoreCase));

            _initialized = true;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[VFS] Virtual File System initialized with {TotalFiles} total indexed entries across {ArchiveCount} archives and {LooseCount} loose files",
                    _fileIndex.Count, _archivePaths.Count, _looseFiles.Count);
        }
    }

    public static string ResolveGamePath(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return string.Empty;

        ReadOnlySpan<char> trimmed = inputPath.AsSpan().Trim();
        while (trimmed.Length > 0 && s_trimQuotes.Contains(trimmed[0])) trimmed = trimmed[1..];
        while (trimmed.Length > 0 && s_trimQuotes.Contains(trimmed[^1])) trimmed = trimmed[..^1];
        string resolved = trimmed.ToString();

        // If the path points to an executable file (e.g. hon_x64.exe, WILLOWMAKER.exe, hon.exe), extract its directory
        if (File.Exists(resolved) || resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrWhiteSpace(dir)) resolved = dir;
        }

        // Check if there is a 'game' subfolder containing .s2z archives or loose hero folders
        string nestedGame = Path.Combine(resolved, "game");
        if (Directory.Exists(nestedGame) &&
            (File.Exists(Path.Combine(nestedGame, "resources0.s2z")) ||
             File.Exists(Path.Combine(nestedGame, "resources1.s2z")) ||
             Directory.Exists(Path.Combine(nestedGame, "heroes"))))
            return nestedGame;

        // Check if there is a 'games/client/game' subfolder (e.g., if pointing to repo root)
        string nestedRepoClient = Path.Combine(resolved, "games", "client", "game");
        if (Directory.Exists(nestedRepoClient) &&
            (File.Exists(Path.Combine(nestedRepoClient, "resources0.s2z")) ||
             File.Exists(Path.Combine(nestedRepoClient, "resources1.s2z")) ||
             Directory.Exists(Path.Combine(nestedRepoClient, "heroes"))))
            return nestedRepoClient;

        // Check if there is a 'games/client' subfolder
        string nestedClient = Path.Combine(resolved, "games", "client");
        if (Directory.Exists(nestedClient))
        {
            string nestedClientGame = Path.Combine(nestedClient, "game");
            if (Directory.Exists(nestedClientGame) &&
                (File.Exists(Path.Combine(nestedClientGame, "resources0.s2z")) ||
                 File.Exists(Path.Combine(nestedClientGame, "resources1.s2z")) ||
                 Directory.Exists(Path.Combine(nestedClientGame, "heroes"))))
                return nestedClientGame;

            if (File.Exists(Path.Combine(nestedClient, "resources0.s2z")) ||
                Directory.Exists(Path.Combine(nestedClient, "heroes"))) return nestedClient;
        }

        return resolved;
    }

    public static string AutoDiscoverGamePath(out List<string> discoveredSupplementaryPaths)
    {
        discoveredSupplementaryPaths = [];
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? currentDir = new(baseDir);

        // Standard solution directories search (walk up from AppContext.BaseDirectory)
        while (currentDir != null)
        {
            // 1. Priority 1: Reorganized client assets directory with full package suite
            string candidateClientGame = Path.Combine(currentDir.FullName, "games", "client", "game");
            if (Directory.Exists(candidateClientGame) &&
                (File.Exists(Path.Combine(candidateClientGame, "resources0.s2z")) ||
                 File.Exists(Path.Combine(candidateClientGame, "resources1.s2z")) ||
                 Directory.Exists(Path.Combine(candidateClientGame, "heroes"))))
            {
                // Register shared folder as supplementary if it exists
                string candidateShared = Path.Combine(currentDir.FullName, "games", "shared");
                if (Directory.Exists(candidateShared) && !discoveredSupplementaryPaths.Contains(candidateShared))
                    discoveredSupplementaryPaths.Add(candidateShared);
                return candidateClientGame;
            }

            // 2. Priority 2: games/client
            string candidateClient = Path.Combine(currentDir.FullName, "games", "client");
            if (Directory.Exists(candidateClient))
            {
                string candidateNested = Path.Combine(candidateClient, "game");
                if (Directory.Exists(candidateNested) &&
                    (File.Exists(Path.Combine(candidateNested, "resources0.s2z")) ||
                     File.Exists(Path.Combine(candidateNested, "resources1.s2z"))))
                {
                    string candidateShared = Path.Combine(currentDir.FullName, "games", "shared");
                    if (Directory.Exists(candidateShared) && !discoveredSupplementaryPaths.Contains(candidateShared))
                        discoveredSupplementaryPaths.Add(candidateShared);
                    return candidateNested;
                }

                if (File.Exists(Path.Combine(candidateClient, "resources0.s2z"))) return candidateClient;
            }

            // 3. Priority 3: Root game directory
            string candidateDirect = Path.Combine(currentDir.FullName, "game");
            if (Directory.Exists(candidateDirect) && File.Exists(Path.Combine(candidateDirect, "resources0.s2z")))
                return candidateDirect;

            // 4. Priority 4: Shared directory
            string candidateSharedOnly = Path.Combine(currentDir.FullName, "games", "shared");
            if (Directory.Exists(candidateSharedOnly) &&
                File.Exists(Path.Combine(candidateSharedOnly, "resources0.s2z"))) return candidateSharedOnly;

            // 5. Priority 5: Server game directory
            string candidateServerGame = Path.Combine(currentDir.FullName, "games", "server", "game");
            if (Directory.Exists(candidateServerGame) &&
                File.Exists(Path.Combine(candidateServerGame, "resources0.s2z"))) return candidateServerGame;

            currentDir = currentDir.Parent;
        }

        // Standard Windows / System HoN installation paths fallback
        string[] standardSystemPaths =
        [
            @"C:\Program Files (x86)\Heroes of Newerth\game",
            @"C:\Program Files (x86)\Heroes of Newerth",
            @"C:\Program Files\Heroes of Newerth\game",
            @"C:\Program Files\Heroes of Newerth",
            @"C:\Games\Heroes of Newerth\game",
            @"C:\Games\Heroes of Newerth",
            @"C:\HoN\game",
            @"C:\HoN",
            @"D:\Games\Heroes of Newerth\game",
            @"D:\Games\Heroes of Newerth",
            @"D:\HoN\game",
            @"D:\HoN",
            @"E:\Games\Heroes of Newerth\game",
            @"E:\Games\Heroes of Newerth",
            @"E:\HoN\game",
            @"E:\HoN"
        ];

        foreach (string sysPath in standardSystemPaths)
            if (Directory.Exists(sysPath))
            {
                string resolved = ResolveGamePath(sysPath);
                if (Directory.Exists(resolved) && (File.Exists(Path.Combine(resolved, "resources0.s2z")) ||
                                                   Directory.Exists(Path.Combine(resolved, "heroes")))) return resolved;
            }

        return string.Empty;
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        ReadOnlySpan<char> span = path.AsSpan().Trim();
        while (span.Length > 0 && s_pathSeparators.Contains(span[0])) span = span[1..];
        while (span.Length > 0 && s_pathSeparators.Contains(span[^1])) span = span[..^1];
        if (span.IsEmpty) return string.Empty;

        if (!span.ContainsAny(s_pathSeparators) && !span.SequenceEqual(".") && !span.SequenceEqual(".."))
            return span.ToString().ToLowerInvariant();

        string clean = path.Replace('\\', '/').Trim('/');
        string[] parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> stack = [];
        foreach (string part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(part);
            }
        }

        return string.Join('/', stack).ToLowerInvariant();
    }

    private static string GetDirectoryName(string vfsPath)
    {
        string norm = NormalizePath(vfsPath);
        int lastSlash = norm.AsSpan().LastIndexOfAny(s_pathSeparators);
        return lastSlash >= 0 ? norm[..lastSlash] : "";
    }

    private static string CombineVfsPath(string dir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        string normRel = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(dir) ||
            normRel.StartsWith("heroes/", StringComparison.OrdinalIgnoreCase) ||
            normRel.StartsWith("shared/", StringComparison.OrdinalIgnoreCase) ||
            normRel.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ||
            normRel.StartsWith("npcs/", StringComparison.OrdinalIgnoreCase))
            return NormalizePath(normRel);

        return NormalizePath($"{dir}/{normRel}");
    }

    private sealed record GamePathIndex(
        string[] ArchivePaths,
        Dictionary<string, (int ArchiveIndex, string EntryFullName)> FileIndex);

    private sealed class ZipArchiveReader : IDisposable
    {
        public readonly ZipArchive[] Archives;
        public readonly string GamePath;
        private readonly FileStream[] _streams;

        public ZipArchiveReader(string gamePath, IReadOnlyList<string> archivePaths)
        {
            GamePath = gamePath;
            Archives = new ZipArchive[archivePaths.Count];
            _streams = new FileStream[archivePaths.Count];
            for (int i = 0; i < archivePaths.Count; i++)
            {
                FileStream fs = new(
                    archivePaths[i],
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
                _streams[i] = fs;
                Archives[i] = new ZipArchive(fs, ZipArchiveMode.Read, false);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < Archives.Length; i++)
            {
                Archives[i]?.Dispose();
                _streams[i]?.Dispose();
            }
        }
    }
}
