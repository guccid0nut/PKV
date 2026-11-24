// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Identifies the origin source of the loaded netcode dictionary.
/// </summary>
public enum ZstdDictionaryLoadSource
{
    None = 0,
    EnvironmentOverride = 1,
    EmbeddedResource = 2,
    RuntimeTrainedFallback = 3,
    DegradedDictionaryless = 4
}

/// <summary>
///     Singleton manager holding the 64KB RFC 8878 Zstandard static netcode dictionary (hon_netcode.zstd.dict)
///     in unmanaged/pinned memory for sub-millisecond UDP delta compression.
/// </summary>
public sealed class ZstdNetcodeDictionary : IDisposable
{
    public const uint ExpectedMagicNumber = 0xEC30A437;
    public const uint DefaultDictionaryId = 0x484F4E31; // "HON1"
    public const int ExpectedDictionarySize = 65536;    // 64 KB

    private static readonly Lazy<ZstdNetcodeDictionary> instance =
        new(() => new ZstdNetcodeDictionary(bindGlobalContextPool: true), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object syncRoot = new();
    private readonly bool bindGlobalContextPool;
    private readonly ZstandardCompressionOptions?[] compressionOptionsByLevel = new ZstandardCompressionOptions?[10];
    private byte[] dictionaryBytes;
    private uint dictionaryId;
    private int loadSource;
    private ZstandardDictionary? compiledDictionary;
    private ZstandardDecompressionOptions? decompressionOptions;

    public static ZstdNetcodeDictionary Instance => instance.Value;

    public ReadOnlyMemory<byte> DictionaryBytes => Volatile.Read(ref dictionaryBytes);

    public ReadOnlySpan<byte> DictionarySpan => Volatile.Read(ref dictionaryBytes);

    public uint DictionaryId => Volatile.Read(ref dictionaryId);

    public ZstdDictionaryLoadSource LoadSource => (ZstdDictionaryLoadSource)Volatile.Read(ref loadSource);

    public bool IsLoaded => Volatile.Read(ref dictionaryBytes) is { Length: > 0 } && Volatile.Read(ref compiledDictionary) != null;

    public ZstandardDictionary? CompiledDictionary => Volatile.Read(ref compiledDictionary);

    public ZstandardDecompressionOptions? DecompressionOptions => Volatile.Read(ref decompressionOptions);

    public ZstandardCompressionOptions? GetCompressionOptions(int level)
    {
        ZstandardDictionary? dict = Volatile.Read(ref compiledDictionary);
        if (dict == null) return null;

        int clamped = Math.Clamp(level, 1, 9);
        ZstandardCompressionOptions? existing = Volatile.Read(ref compressionOptionsByLevel[clamped]);
        if (existing != null && ReferenceEquals(existing.Dictionary, dict))
        {
            return existing;
        }

        ZstandardCompressionOptions created = new()
        {
            Dictionary = dict,
            Quality = clamped
        };

        return Interlocked.CompareExchange(ref compressionOptionsByLevel[clamped], created, existing) == existing
            ? created
            : Volatile.Read(ref compressionOptionsByLevel[clamped]) ?? created;
    }

    private ZstdNetcodeDictionary(bool bindGlobalContextPool)
    {
        this.bindGlobalContextPool = bindGlobalContextPool;
        dictionaryBytes = Array.Empty<byte>();
        LoadDefaultDictionary();
    }

    internal static ZstdNetcodeDictionary CreateIsolatedForTesting() => new(bindGlobalContextPool: false);

    private void LoadDefaultDictionary()
    {
        // 1. Check if an external file path override is specified via environment variable
        string? envPath = Environment.GetEnvironmentVariable("HON_NETCODE_DICT_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            try
            {
                byte[] fileBytes = File.ReadAllBytes(envPath);
                if (ValidateAndSet(fileBytes))
                {
                    Volatile.Write(ref loadSource, (int)ZstdDictionaryLoadSource.EnvironmentOverride);
                    Trace.TraceInformation("[ZSTD_DICT] Loaded external dictionary override from '{0}' ({1} bytes, ID=0x{2:X8}).", envPath, fileBytes.Length, DictionaryId);
                    return;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[ZSTD_DICT] Failed to load external dictionary from '{0}': {1}", envPath, ex);
                // Fallback to embedded resource
            }
        }

        // 2. Load from embedded assembly resource
        Assembly assembly = typeof(ZstdNetcodeDictionary).Assembly;
        string resourceName = "ASPIRE.Common.Resources.hon_netcode.zstd.dict";

        try
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using MemoryStream ms = new();
                stream.CopyTo(ms);
                byte[] resBytes = ms.ToArray();
                if (ValidateAndSet(resBytes))
                {
                    Volatile.Write(ref loadSource, (int)ZstdDictionaryLoadSource.EmbeddedResource);
                    Trace.TraceInformation("[ZSTD_DICT] Loaded embedded assembly dictionary ({0} bytes, ID=0x{1:X8}).", resBytes.Length, DictionaryId);
                    return;
                }
            }
            else
            {
                Trace.TraceWarning("[ZSTD_DICT] Embedded resource '{0}' not found in assembly {1}.", resourceName, assembly.FullName);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ZSTD_DICT] Failed to load embedded dictionary resource '{0}': {1}", resourceName, ex);
        }

        // 3. If resource is missing in development, generate standard 64KB dictionary on-the-fly
        try
        {
            Trace.TraceInformation("[ZSTD_DICT] Generating runtime fallback dictionary via ZDICT training...");
            var corpus = ZstdDictionaryTrainer.GenerateTrainingCorpus(200);
            byte[] generated = ZstdDictionaryTrainer.BuildStaticDictionary(corpus);
            if (ValidateAndSet(generated))
            {
                Volatile.Write(ref loadSource, (int)ZstdDictionaryLoadSource.RuntimeTrainedFallback);
                Trace.TraceInformation("[ZSTD_DICT] Runtime fallback dictionary generated successfully ({0} bytes, ID=0x{1:X8}).", generated.Length, DictionaryId);
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ZSTD_DICT] Runtime dictionary training failed; falling back to degraded dictionary-less operation: {0}", ex);
        }

        Volatile.Write(ref loadSource, (int)ZstdDictionaryLoadSource.DegradedDictionaryless);
        Trace.TraceWarning("[ZSTD_DICT] Running in degraded dictionary-less mode. Netcode delta compression will use standard non-dictionary Zstd.");
    }

    public bool ValidateAndSet(byte[] dictData)
    {
        if (dictData.Length < 8)
        {
            Trace.TraceWarning("[ZSTD_DICT] ValidateAndSet rejected buffer: length {0} is less than minimum 8-byte header.", dictData.Length);
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(dictData.AsSpan(0, 4));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(dictData.AsSpan(4, 4));

        if (magic != ExpectedMagicNumber)
        {
            Trace.TraceWarning("[ZSTD_DICT] ValidateAndSet rejected buffer: magic 0x{0:X8} does not match expected 0x{1:X8}.", magic, ExpectedMagicNumber);
            return false;
        }

        ZstandardDictionary newCompiled;
        ZstandardDecompressionOptions newDecompressionOptions;
        try
        {
            newCompiled = ZstandardDictionary.Create(dictData);
            newDecompressionOptions = new ZstandardDecompressionOptions
            {
                Dictionary = newCompiled
            };
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ZSTD_DICT] ZstandardDictionary.Create failed on {0}-byte buffer: {1}", dictData.Length, ex);
            return false;
        }

        lock (syncRoot)
        {
            ZstandardDictionary? previousDictionary = compiledDictionary;
            Volatile.Write(ref compiledDictionary, newCompiled);
            Volatile.Write(ref decompressionOptions, newDecompressionOptions);
            Array.Clear(compressionOptionsByLevel, 0, compressionOptionsByLevel.Length);
            Volatile.Write(ref dictionaryBytes, dictData);
            Volatile.Write(ref dictionaryId, id);

            if (bindGlobalContextPool)
            {
                ZstdContextPool.InvalidateWithRetiredDictionary(newCompiled, previousDictionary);
            }
            else
            {
                previousDictionary?.Dispose();
            }
        }

        return true;
    }

    /// <summary>
    ///     Pre-warms the static dictionary, compiles unmanaged tables, and primes JIT paths
    ///     to eliminate cold-start latency hitches on the UDP fast path.
    /// </summary>
    public void EnsureLoaded()
    {
        if (!IsLoaded)
        {
            LoadDefaultDictionary();
        }

        // Prime standard compression levels 1 and 3 options
        _ = GetCompressionOptions(1);
        _ = GetCompressionOptions(3);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            ZstandardDictionary? previousDictionary = compiledDictionary;
            Volatile.Write(ref compiledDictionary, null);
            Volatile.Write(ref decompressionOptions, null);
            Array.Clear(compressionOptionsByLevel, 0, compressionOptionsByLevel.Length);

            if (bindGlobalContextPool)
            {
                ZstdContextPool.InvalidateWithRetiredDictionary(dictionary: null, retiredDictionary: previousDictionary);
            }
            else
            {
                previousDictionary?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Reloads the dictionary from a custom binary buffer.
    /// </summary>
    public void LoadCustomDictionary(byte[] dictData)
    {
        ArgumentNullException.ThrowIfNull(dictData);
        if (!ValidateAndSet(dictData))
        {
            throw new ArgumentException(
                $"Invalid RFC 8878 Zstandard dictionary. Magic must be 0x{ExpectedMagicNumber:X8}.",
                nameof(dictData));
        }
    }

    /// <summary>
    ///     Validates whether a given buffer has a valid RFC 8878 Zstandard dictionary header.
    /// </summary>
    public static bool IsValidDictionary(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) return false;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        return magic == ExpectedMagicNumber;
    }
}
