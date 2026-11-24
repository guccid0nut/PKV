// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Kongor.Cli.Mcp.Services;

public sealed class VfsHydrationResult
{
    public int ExistingEntriesPreserved { get; set; }
    public int EntriesAdded { get; set; }
    public int EntriesModified { get; set; }
    public long OutputSizeBytes { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public sealed class VfsHydrationService
{
    private readonly Action<string> _logger;

    public VfsHydrationService(Action<string>? logger = null)
    {
        _logger = logger ?? Console.WriteLine;
    }

    public VfsHydrationResult Hydrate(string repoRoot, bool force = false)
    {
        Stopwatch sw = Stopwatch.StartNew();
        VfsHydrationResult result = new();

        string clientGameDir = Path.Combine(repoRoot, "games", "client", "game");
        string texturesArcPath = Path.Combine(clientGameDir, "textures.s2z");
        string res99Path = Path.Combine(clientGameDir, "resources99.s2z");
        string res99Tmp = Path.Combine(clientGameDir, "resources99.s2z.tmp");
        string res99Bak = Path.Combine(clientGameDir, "resources99.s2z.bak");

        if (!File.Exists(texturesArcPath))
        {
            result.Error = $"textures.s2z not found at {texturesArcPath}";
            _logger($"[ERROR] {result.Error}");
            return result;
        }

        if (!File.Exists(res99Path))
        {
            result.Error = $"resources99.s2z not found at {res99Path}";
            _logger($"[ERROR] {result.Error}");
            return result;
        }

        if (File.Exists(res99Tmp))
        {
            File.Delete(res99Tmp);
        }

        _logger($"[VfsHydration] Opening source archives...");
        _logger($"  textures.s2z: {texturesArcPath} ({new FileInfo(texturesArcPath).Length / 1024 / 1024:N0} MB)");
        _logger($"  resources99.s2z: {res99Path} ({new FileInfo(res99Path).Length / 1024 / 1024:N0} MB)");

        // XML overrides for Bubbles alt16 store models
        byte[] bubblesStoreLXml = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<model name=\"rig.max\" file=\"high.model\" type=\"K2\" >\r\n" +
            "\t<anim name=\"idle\" clip=\"clips/idle.clip\" fps=\"30\" loop=\"true\" loopbackframe=\"1\" />\r\n" +
            "</model>\r\n");

        byte[] bubblesStoreMXml = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<model name=\"rig.max\" file=\"high.model\" type=\"K2\" high=\"high.model\" >\r\n" +
            "\t<anim name=\"idle\" clip=\"clips/idle.clip\" minstartframe=\"0\" maxstartframe=\"-1\" loop=\"true\" fps=\"20\" />\r\n" +
            "</model>\r\n");

        HashSet<string> entriesInDest = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (FileStream destStream = new(res99Tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024))
            using (ZipArchive destArchive = new(destStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // 1. Copy all existing entries from resources99.s2z into tmp archive
                using (ZipArchive src99 = ZipFile.OpenRead(res99Path))
                {
                    _logger($"[VfsHydration] Ingesting {src99.Entries.Count:N0} existing entries from resources99.s2z...");
                    foreach (ZipArchiveEntry entry in src99.Entries)
                    {
                        string norm = entry.FullName.Replace('\\', '/');
                        if (entriesInDest.Contains(norm)) continue;

                        ZipArchiveEntry newEntry = destArchive.CreateEntry(norm, CompressionLevel.Fastest);

                        if (norm.Equals("heroes/bubbles/alt16/model_store_l.mdf", StringComparison.OrdinalIgnoreCase))
                        {
                            using Stream entryStream = newEntry.Open();
                            entryStream.Write(bubblesStoreLXml, 0, bubblesStoreLXml.Length);
                            result.EntriesModified++;
                            _logger("  [PATCH] Replaced heroes/bubbles/alt16/model_store_l.mdf with clips/idle.clip");
                        }
                        else if (norm.Equals("heroes/bubbles/alt16/model_store_m.mdf", StringComparison.OrdinalIgnoreCase))
                        {
                            using Stream entryStream = newEntry.Open();
                            entryStream.Write(bubblesStoreMXml, 0, bubblesStoreMXml.Length);
                            result.EntriesModified++;
                            _logger("  [PATCH] Replaced heroes/bubbles/alt16/model_store_m.mdf with clips/idle.clip");
                        }
                        else
                        {
                            using Stream sStream = entry.Open();
                            using Stream dStream = newEntry.Open();
                            sStream.CopyTo(dStream);
                        }

                        entriesInDest.Add(norm);
                        result.ExistingEntriesPreserved++;
                    }
                }

                // If Bubbles MDFs weren't in resources99, create them now
                if (!entriesInDest.Contains("heroes/bubbles/alt16/model_store_l.mdf"))
                {
                    ZipArchiveEntry lEntry = destArchive.CreateEntry("heroes/bubbles/alt16/model_store_l.mdf", CompressionLevel.Fastest);
                    using Stream s = lEntry.Open();
                    s.Write(bubblesStoreLXml, 0, bubblesStoreLXml.Length);
                    entriesInDest.Add("heroes/bubbles/alt16/model_store_l.mdf");
                    result.EntriesAdded++;
                }
                if (!entriesInDest.Contains("heroes/bubbles/alt16/model_store_m.mdf"))
                {
                    ZipArchiveEntry mEntry = destArchive.CreateEntry("heroes/bubbles/alt16/model_store_m.mdf", CompressionLevel.Fastest);
                    using Stream s = mEntry.Open();
                    s.Write(bubblesStoreMXml, 0, bubblesStoreMXml.Length);
                    entriesInDest.Add("heroes/bubbles/alt16/model_store_m.mdf");
                    result.EntriesAdded++;
                }

                // 2. Un-trap entries from textures.s2z across all numeric quality tiers
                using (ZipArchive texArchive = ZipFile.OpenRead(texturesArcPath))
                {
                    _logger($"[VfsHydration] Scanning textures.s2z for trapped UI, shared, sky, and icon textures across all tiers...");

                    // Process in order of quality: uncompressed/highest-res first (00000000, 00000001, 00000002, 00000009, etc.)
                    string[] tierPriority = [
                        "00000000/",
                        "00000001/",
                        "00000002/",
                        "00000009/",
                        "00000003/",
                        "00000005/",
                        "00000007/",
                        "00000010/",
                        "00000004/",
                        "00000006/",
                        "00000008/",
                        "00000011/"
                    ];

                    Dictionary<string, List<ZipArchiveEntry>> texEntriesByTier = texArchive.Entries
                        .GroupBy(e =>
                        {
                            string fn = e.FullName.Replace('\\', '/');
                            int slashIdx = fn.IndexOf('/');
                            return slashIdx == 8 ? fn[..9] : "other";
                        })
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                    foreach (string tier in tierPriority)
                    {
                        if (!texEntriesByTier.TryGetValue(tier, out List<ZipArchiveEntry>? tierEntries)) continue;

                        int tierLength = tier.Length;
                        foreach (ZipArchiveEntry entry in tierEntries)
                        {
                            string fullName = entry.FullName.Replace('\\', '/');
                            string unprefix = fullName[tierLength..];

                            // Filter for UI, shared, world/sky, items, hero icons/portraits/abilities
                            bool shouldHydrate =
                                unprefix.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ||
                                unprefix.StartsWith("shared/", StringComparison.OrdinalIgnoreCase) ||
                                unprefix.StartsWith("world/sky/", StringComparison.OrdinalIgnoreCase) ||
                                unprefix.StartsWith("items/", StringComparison.OrdinalIgnoreCase) ||
                                (unprefix.StartsWith("heroes/", StringComparison.OrdinalIgnoreCase) && (
                                    unprefix.Contains("/icon", StringComparison.OrdinalIgnoreCase) ||
                                    unprefix.Contains("/portrait", StringComparison.OrdinalIgnoreCase) ||
                                    unprefix.EndsWith("/hero.dds", StringComparison.OrdinalIgnoreCase) ||
                                    unprefix.Contains("/ability", StringComparison.OrdinalIgnoreCase)));

                            if (!shouldHydrate) continue;

                            if (entriesInDest.Contains(unprefix))
                            {
                                continue; // Higher-priority tier or existing entry already populated
                            }

                            ZipArchiveEntry newEntry = destArchive.CreateEntry(unprefix, CompressionLevel.Fastest);
                            using (Stream sStream = entry.Open())
                            using (Stream dStream = newEntry.Open())
                            {
                                sStream.CopyTo(dStream);
                            }

                            entriesInDest.Add(unprefix);
                            result.EntriesAdded++;

                            if (result.EntriesAdded % 1000 == 0)
                            {
                                _logger($"  Hydrated {result.EntriesAdded:N0} textures into resources99.s2z...");
                            }
                        }
                    }
                }
            } // Dest archive is closed, EOCD written

            _logger($"[VfsHydration] Verifying archive integrity of {res99Tmp}...");
            using (ZipArchive verify = ZipFile.OpenRead(res99Tmp))
            {
                int count = verify.Entries.Count;
                _logger($"[VfsHydration] Integrity confirmed: {count:N0} entries validly indexed in central directory.");
            }

            // Atomic file rotation
            _logger($"[VfsHydration] Creating backup {res99Bak}...");
            File.Copy(res99Path, res99Bak, overwrite: true);

            _logger($"[VfsHydration] Atomically replacing {res99Path}...");
            File.Move(res99Tmp, res99Path, overwrite: true);
            _logger($"[VfsHydration] Archive rotated: {res99Path} at {TimeProvider.System.GetUtcNow():o}");

            sw.Stop();
            result.OutputSizeBytes = new FileInfo(res99Path).Length;
            result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            result.Success = true;

            _logger($"[VfsHydration] Successfully hydrated resources99.s2z in {sw.ElapsedMilliseconds:N0}ms!");
            _logger($"  Preserved: {result.ExistingEntriesPreserved:N0} | Modified: {result.EntriesModified:N0} | Added: {result.EntriesAdded:N0} | Total: {entriesInDest.Count:N0} | Size: {result.OutputSizeBytes / 1024 / 1024:N0} MB");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger($"[FATAL] Hydration failed: {ex}");
            if (File.Exists(res99Tmp))
            {
                try
                {
                    File.Delete(res99Tmp);
                }
                catch (Exception delEx)
                {
                    _logger($"[WARN] Failed deleting temporary archive '{res99Tmp}': {delEx.Message}");
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Wave 4 (#228): TigerBeetle-inspired Gray et al. `ShuffledZipfian` generator (`theta`, `alpha`, `zeta_n`, `eta`)
/// paired with a coprime modular-multiplication bijective permutation `f(i) = (a * i + b) mod N` (`gcd(a, N) = 1`)
/// and exact Extended-Euclidean inverse `f^-1(y) = a^-1 * (y - b) mod N` that scatters hot-spot VFS/cache keys
/// across the keyspace in strict 1-step O(1) time and O(1) memory with zero shuffle-table allocations.
/// </summary>
public sealed class BijectiveShuffledZipfianGenerator
{
    public ulong ItemCount { get; }
    public double Theta { get; }
    public double Alpha { get; }
    public double ZetaN { get; }
    public double Eta { get; }
    public ulong CoprimeMultiplier => _coprimeMultiplier;
    public ulong InverseMultiplier => _inverseMultiplier;
    public ulong Offset => _offset;

    private readonly ulong _coprimeMultiplier;
    private readonly ulong _inverseMultiplier;
    private readonly ulong _offset;

    public BijectiveShuffledZipfianGenerator(ulong itemCount, double theta = 0.99, ulong seed = 0x51F1_71BE_CAFE_BEEFUL)
    {
        if (itemCount == 0UL)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "[BijectiveShuffledZipfianGenerator] itemCount must be at least 1.");
        }
        if (theta <= 0.0 || theta >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(theta), theta, "[BijectiveShuffledZipfianGenerator] theta must be in (0.0, 1.0).");
        }

        ItemCount = itemCount;
        Theta = theta;
        Alpha = 1.0 / (1.0 - theta);
        ZetaN = ComputeZeta(itemCount, theta);
        double zeta2 = ComputeZeta(2UL, theta);
        Eta = itemCount <= 1UL
            ? 0.0
            : (1.0 - Math.Pow(2.0 / (double)itemCount, 1.0 - theta)) / (1.0 - (zeta2 / ZetaN));

        if (itemCount <= 1UL)
        {
            _coprimeMultiplier = 1UL;
            _inverseMultiplier = 1UL;
            _offset = 0UL;
            return;
        }

        ulong mix = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;
        ulong candidateA = (SplitMix(ref mix) % (itemCount - 1UL)) + 1UL;
        if (itemCount > 2UL && candidateA == 1UL)
        {
            candidateA = 2UL;
        }

        while (Gcd(candidateA, itemCount) != 1UL)
        {
            candidateA++;
            if (candidateA >= itemCount)
            {
                candidateA = 1UL;
            }
        }

        _coprimeMultiplier = candidateA;
        _inverseMultiplier = ModInverse(candidateA, itemCount);
        _offset = (SplitMix(ref mix) % (itemCount - 1UL)) + 1UL;
    }

    public static double ComputeZeta(ulong count, double theta)
    {
        ulong exactLimit = Math.Min(count, 10_000UL);
        double sum = 0.0;
        for (ulong i = 1UL; i <= exactLimit; i++)
        {
            sum += Math.Pow((double)i, -theta);
        }

        if (count > exactLimit)
        {
            double oneMinusTheta = 1.0 - theta;
            sum += (Math.Pow((double)count, oneMinusTheta) - Math.Pow((double)exactLimit, oneMinusTheta)) / oneMinusTheta;
        }

        return sum;
    }

    /// <summary>
    /// Applies the O(1) coprime modular permutation `f(i) = (a * i + b) mod N` over `[0, ItemCount)`.
    /// </summary>
    public ulong PermuteIndex(ulong index)
    {
        if (index >= ItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"[BijectiveShuffledZipfianGenerator] Index must be < {ItemCount}.");
        }
        if (ItemCount == 1UL)
        {
            return 0UL;
        }

        UInt128 product = ((UInt128)_coprimeMultiplier * index) + _offset;
        return (ulong)(product % ItemCount);
    }

    /// <summary>
    /// Exact O(1) inverse of <see cref="PermuteIndex"/> over `[0, ItemCount)`: `f^-1(y) = a^-1 * (y - b) mod N`.
    /// </summary>
    public ulong UnpermuteIndex(ulong permuted)
    {
        if (permuted >= ItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(permuted), permuted, $"[BijectiveShuffledZipfianGenerator] Permuted index must be < {ItemCount}.");
        }
        if (ItemCount == 1UL)
        {
            return 0UL;
        }

        ulong diff = permuted >= _offset
            ? (permuted - _offset)
            : (ItemCount - (_offset - permuted));
        UInt128 product = (UInt128)diff * _inverseMultiplier;
        return (ulong)(product % ItemCount);
    }

    /// <summary>
    /// Samples an unshuffled Zipfian rank in `[0, ItemCount)` where rank `0` is the hottest key.
    /// </summary>
    public ulong NextUnshuffledRank(TigerBeetlePrng prng)
    {
        if (ItemCount <= 1UL)
        {
            return 0UL;
        }

        double u = (double)(prng.NextUInt64() >> 11) * (1.0 / (1UL << 53));
        double uz = u * ZetaN;
        if (uz < 1.0)
        {
            return 0UL;
        }
        if (uz < 1.0 + Math.Pow(0.5, Theta))
        {
            return 1UL;
        }

        double raw = (double)ItemCount * Math.Pow((Eta * u) - Eta + 1.0, Alpha);
        ulong rank = (ulong)Math.Clamp(raw, 0.0, (double)(ItemCount - 1UL));
        return rank;
    }

    /// <summary>
    /// Samples a bijectively shuffled Zipfian index in `[0, ItemCount)`.
    /// </summary>
    public ulong Next(TigerBeetlePrng prng)
    {
        ulong rank = NextUnshuffledRank(prng);
        return PermuteIndex(rank);
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0UL)
        {
            ulong t = b;
            b = a % b;
            a = t;
        }
        return a;
    }

    private static ulong ModInverse(ulong a, ulong m)
    {
        Int128 t = 0;
        Int128 newT = 1;
        Int128 r = m;
        Int128 newR = a;

        while (newR != 0)
        {
            Int128 quotient = r / newR;
            (t, newT) = (newT, t - (quotient * newT));
            (r, newR) = (newR, r - (quotient * newR));
        }

        if (t < 0)
        {
            t += (Int128)m;
        }

        return (ulong)t;
    }

    private static ulong SplitMix(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}


