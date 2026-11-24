// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Point-in-time zero-allocation telemetry snapshot for <see cref="ZstdContextPool" />.
/// </summary>
public readonly record struct ZstdPoolDiagnostics(
    ulong GenerationId,
    int CapacityPerPool,
    int ActiveLeaseCount,
    long InvalidationCount,
    long OverflowDisposeCount,
    long StaleGenerationDisposeCount,
    long FaultedDisposeCount);

/// <summary>
///     High-performance, lock-free fixed-slot object pool for RFC 8878 Zstandard encoders and decoders.
///     Eliminates all per-packet heap allocations, segment growth, and cross-generation dictionary races
///     on the 60 Hz UDP tick loop.
/// </summary>
public static class ZstdContextPool
{
    private static readonly int MaxInstancesPerPool = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    private static volatile PoolGeneration? currentGeneration;
    private static ulong generationSequence;
    private static long invalidationCount;
    private static long overflowDisposeCount;
    private static long staleGenerationDisposeCount;
    private static long faultedDisposeCount;

    private static readonly ConditionalWeakTable<IDisposable, UnscopedLeaseEntry> unscopedLeaseOwners = new();

    public static int CapacityPerPool => MaxInstancesPerPool;

    internal readonly struct EncoderLease(PoolGeneration? generation, ZstandardEncoder encoder, int clampedLevel)
    {
        public ZstandardEncoder Encoder { get; } = encoder;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(bool isFaulted)
        {
            ReturnEncoderCore(generation, Encoder, clampedLevel, isFaulted);
        }
    }

    internal readonly struct DecoderLease(PoolGeneration? generation, ZstandardDecoder decoder, bool useDictionary)
    {
        public ZstandardDecoder Decoder { get; } = decoder;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(bool isFaulted)
        {
            ReturnDecoderCore(generation, Decoder, useDictionary, isFaulted);
        }
    }

    private sealed class UnscopedLeaseEntry(PoolGeneration? generation, bool useDictionary)
    {
        private int consumed;

        public bool UseDictionary { get; } = useDictionary;

        public bool TryConsume(out PoolGeneration? owner)
        {
            if (Interlocked.Exchange(ref consumed, 1) == 0)
            {
                owner = generation;
                return true;
            }

            owner = null;
            return false;
        }
    }

    private sealed class FixedSlotPool<T>(int capacity) where T : class, IDisposable
    {
        private T? fastSlot;
        private readonly T?[] slots = new T?[Math.Max(0, capacity - 1)];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRent(out T? item)
        {
            item = fastSlot;
            if (item != null && Interlocked.CompareExchange(ref fastSlot, null, item) == item)
            {
                return true;
            }

            T?[] items = slots;
            for (int i = 0; i < items.Length; i++)
            {
                item = items[i];
                if (item != null && Interlocked.CompareExchange(ref items[i], null, item) == item)
                {
                    return true;
                }
            }

            item = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReturn(T item)
        {
            if (fastSlot == null && Interlocked.CompareExchange(ref fastSlot, item, null) == null)
            {
                return true;
            }

            T?[] items = slots;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null && Interlocked.CompareExchange(ref items[i], item, null) == null)
                {
                    return true;
                }
            }

            return false;
        }

        public int CountOccupied()
        {
            int count = Volatile.Read(ref fastSlot) != null ? 1 : 0;
            T?[] items = slots;
            for (int i = 0; i < items.Length; i++)
            {
                if (Volatile.Read(ref items[i]) != null)
                {
                    count++;
                }
            }

            return count;
        }

        public void DrainAndDispose()
        {
            Interlocked.Exchange(ref fastSlot, null)?.Dispose();
            T?[] items = slots;
            for (int i = 0; i < items.Length; i++)
            {
                Interlocked.Exchange(ref items[i], null)?.Dispose();
            }
        }
    }

    internal sealed class PoolGeneration
    {
        public readonly ulong Id;
        public readonly ZstandardDictionary? Dictionary;

        private readonly FixedSlotPool<ZstandardEncoder>[] encoderPools = new FixedSlotPool<ZstandardEncoder>[10];
        private readonly ZstandardCompressionOptions?[] compressionOptionsByLevel = new ZstandardCompressionOptions?[10];
        private readonly FixedSlotPool<ZstandardDecoder> dictDecoderPool = new(MaxInstancesPerPool);
        private readonly FixedSlotPool<ZstandardDecoder> rawDecoderPool = new(MaxInstancesPerPool);

        private int activeReferences = 1; // 1 reference held by currentGeneration until Retire()
        private int isRetired;
        private ZstandardDictionary? dictionaryToDisposeOnFinalRelease;

        public PoolGeneration(ulong id, ZstandardDictionary? dictionary)
        {
            Id = id;
            Dictionary = dictionary;

            for (int level = 1; level < encoderPools.Length; level++)
            {
                encoderPools[level] = new FixedSlotPool<ZstandardEncoder>(MaxInstancesPerPool);
            }

            if (dictionary != null)
            {
                compressionOptionsByLevel[1] = new ZstandardCompressionOptions { Dictionary = dictionary, Quality = 1 };
                compressionOptionsByLevel[3] = new ZstandardCompressionOptions { Dictionary = dictionary, Quality = 3 };
            }
        }

        public bool IsRetired => Volatile.Read(ref isRetired) != 0;

        public int ActiveLeaseCount => Math.Max(0, Volatile.Read(ref activeReferences) - (IsRetired ? 0 : 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAcquireLease()
        {
            while (true)
            {
                if (Volatile.Read(ref isRetired) != 0)
                {
                    return false;
                }

                int current = Volatile.Read(ref activeReferences);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref activeReferences, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref activeReferences) == 0)
            {
                DrainPools();
                Interlocked.Exchange(ref dictionaryToDisposeOnFinalRelease, null)?.Dispose();
            }
        }

        public void Retire(ZstandardDictionary? retiredDictionary)
        {
            if (retiredDictionary != null)
            {
                Volatile.Write(ref dictionaryToDisposeOnFinalRelease, retiredDictionary);
            }

            if (Interlocked.Exchange(ref isRetired, 1) == 0)
            {
                DrainPools();
                ReleaseReference();
            }
        }

        public ZstandardEncoder RentOrCreateEncoder(int clampedLevel)
        {
            FixedSlotPool<ZstandardEncoder> pool = encoderPools[clampedLevel];
            while (pool.TryRent(out ZstandardEncoder? encoder))
            {
                try
                {
                    encoder!.Reset();
                    return encoder;
                }
                catch
                {
                    Interlocked.Increment(ref faultedDisposeCount);
                    encoder!.Dispose();
                }
            }

            ZstandardCompressionOptions? options = GetOrCreateCompressionOptions(clampedLevel);
            return options != null ? new ZstandardEncoder(options) : new ZstandardEncoder(clampedLevel);
        }

        public bool TryReturnEncoder(int clampedLevel, ZstandardEncoder encoder)
        {
            if (Volatile.Read(ref isRetired) != 0)
            {
                return false;
            }

            return encoderPools[clampedLevel].TryReturn(encoder);
        }

        public ZstandardDecoder RentOrCreateDecoder(bool useDictionary, out bool isDictionaryPrimed)
        {
            if (useDictionary && Dictionary != null)
            {
                isDictionaryPrimed = true;
                while (dictDecoderPool.TryRent(out ZstandardDecoder? decoder))
                {
                    try
                    {
                        decoder!.Reset();
                        return decoder;
                    }
                    catch
                    {
                        Interlocked.Increment(ref faultedDisposeCount);
                        decoder!.Dispose();
                    }
                }

                return new ZstandardDecoder(Dictionary);
            }

            isDictionaryPrimed = false;
            while (rawDecoderPool.TryRent(out ZstandardDecoder? rawDecoder))
            {
                try
                {
                    rawDecoder!.Reset();
                    return rawDecoder;
                }
                catch
                {
                    Interlocked.Increment(ref faultedDisposeCount);
                    rawDecoder!.Dispose();
                }
            }

            return new ZstandardDecoder();
        }

        public bool TryReturnDecoder(bool isDictionaryPrimed, ZstandardDecoder decoder)
        {
            if (Volatile.Read(ref isRetired) != 0)
            {
                return false;
            }

            return isDictionaryPrimed
                ? dictDecoderPool.TryReturn(decoder)
                : rawDecoderPool.TryReturn(decoder);
        }

        public int GetPooledEncoderCount(int clampedLevel) => encoderPools[clampedLevel].CountOccupied();

        public int GetPooledDecoderCount(bool useDictionary) =>
            useDictionary ? dictDecoderPool.CountOccupied() : rawDecoderPool.CountOccupied();

        private ZstandardCompressionOptions? GetOrCreateCompressionOptions(int clampedLevel)
        {
            if (Dictionary == null)
            {
                return null;
            }

            ZstandardCompressionOptions? existing = Volatile.Read(ref compressionOptionsByLevel[clampedLevel]);
            if (existing != null)
            {
                return existing;
            }

            ZstandardCompressionOptions created = new()
            {
                Dictionary = Dictionary,
                Quality = clampedLevel
            };

            return Interlocked.CompareExchange(ref compressionOptionsByLevel[clampedLevel], created, null) ?? created;
        }

        private void DrainPools()
        {
            for (int level = 1; level < encoderPools.Length; level++)
            {
                encoderPools[level].DrainAndDispose();
            }

            dictDecoderPool.DrainAndDispose();
            rawDecoderPool.DrainAndDispose();
        }
    }

    /// <summary>
    ///     Returns a zero-allocation snapshot of pool generation and disposal counters.
    /// </summary>
    public static ZstdPoolDiagnostics GetDiagnostics()
    {
        PoolGeneration? gen = currentGeneration;
        return new ZstdPoolDiagnostics(
            GenerationId: gen?.Id ?? 0,
            CapacityPerPool: MaxInstancesPerPool,
            ActiveLeaseCount: gen?.ActiveLeaseCount ?? 0,
            InvalidationCount: Volatile.Read(ref invalidationCount),
            OverflowDisposeCount: Volatile.Read(ref overflowDisposeCount),
            StaleGenerationDisposeCount: Volatile.Read(ref staleGenerationDisposeCount),
            FaultedDisposeCount: Volatile.Read(ref faultedDisposeCount));
    }

    internal static int GetPooledEncoderCount(int level = 3)
    {
        PoolGeneration? gen = currentGeneration;
        return gen?.GetPooledEncoderCount(Math.Clamp(level, 1, 9)) ?? 0;
    }

    internal static int GetPooledDecoderCount(bool useDictionary = true)
    {
        PoolGeneration? gen = currentGeneration;
        return gen?.GetPooledDecoderCount(useDictionary) ?? 0;
    }

    /// <summary>
    ///     Flushes existing pools and atomically transitions to a new generation when dictionary tables are reloaded.
    ///     In-flight contexts rented from the retiring generation remain valid until returned, at which point they are disposed.
    /// </summary>
    public static void Invalidate(ZstandardDictionary? dictionary = null) =>
        InvalidateCore(dictionary, retiredDictionary: null);

    internal static void InvalidateWithRetiredDictionary(
        ZstandardDictionary? dictionary,
        ZstandardDictionary? retiredDictionary) =>
        InvalidateCore(dictionary, retiredDictionary);

    private static void InvalidateCore(
        ZstandardDictionary? dictionary,
        ZstandardDictionary? retiredDictionary)
    {
        ulong nextId = Interlocked.Increment(ref generationSequence);
        PoolGeneration next = new(nextId, dictionary);
        PoolGeneration? old = Interlocked.Exchange(ref currentGeneration, next);
        Interlocked.Increment(ref invalidationCount);

        ZstandardDictionary? dictToDispose =
            retiredDictionary != null && !ReferenceEquals(retiredDictionary, dictionary)
                ? retiredDictionary
                : null;

        if (old != null)
        {
            if (dictToDispose != null && ReferenceEquals(old.Dictionary, dictToDispose))
            {
                old.Retire(dictToDispose);
                return;
            }

            old.Retire(retiredDictionary: null);
        }

        dictToDispose?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PoolGeneration GetActiveGeneration()
    {
        PoolGeneration? gen = currentGeneration;
        if (gen != null)
        {
            return gen;
        }

        return InitializeColdGeneration();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PoolGeneration InitializeColdGeneration()
    {
        ZstdNetcodeDictionary.Instance.EnsureLoaded();
        PoolGeneration? gen = currentGeneration;
        if (gen != null)
        {
            return gen;
        }

        ulong fallbackId = Interlocked.Increment(ref generationSequence);
        PoolGeneration fallback = new(fallbackId, ZstdNetcodeDictionary.Instance.CompiledDictionary);
        return Interlocked.CompareExchange(ref currentGeneration, fallback, null) ?? fallback;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EncoderLease RentEncoderLease(int level = 3)
    {
        int clampedLevel = Math.Clamp(level, 1, 9);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            PoolGeneration gen = GetActiveGeneration();
            if (gen.TryAcquireLease())
            {
                try
                {
                    ZstandardEncoder encoder = gen.RentOrCreateEncoder(clampedLevel);
                    return new EncoderLease(gen, encoder, clampedLevel);
                }
                catch
                {
                    gen.ReleaseReference();
                    throw;
                }
            }
        }

        ZstandardCompressionOptions? fallbackOptions = ZstdNetcodeDictionary.Instance.GetCompressionOptions(clampedLevel);
        ZstandardEncoder standalone = fallbackOptions != null
            ? new ZstandardEncoder(fallbackOptions)
            : new ZstandardEncoder(clampedLevel);
        return new EncoderLease(generation: null, standalone, clampedLevel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DecoderLease RentDecoderLease(bool useDictionary = true)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            PoolGeneration gen = GetActiveGeneration();
            if (gen.TryAcquireLease())
            {
                try
                {
                    ZstandardDecoder decoder = gen.RentOrCreateDecoder(useDictionary, out bool isDictionaryPrimed);
                    return new DecoderLease(gen, decoder, isDictionaryPrimed);
                }
                catch
                {
                    gen.ReleaseReference();
                    throw;
                }
            }
        }

        ZstandardDictionary? fallbackDict = useDictionary ? ZstdNetcodeDictionary.Instance.CompiledDictionary : null;
        ZstandardDecoder standalone = fallbackDict != null
            ? new ZstandardDecoder(fallbackDict)
            : new ZstandardDecoder();
        return new DecoderLease(generation: null, standalone, useDictionary: fallbackDict != null);
    }

    /// <summary>
    ///     Rents a ZstandardEncoder primed for the specified compression quality level.
    /// </summary>
    public static ZstandardEncoder RentEncoder(int level = 3)
    {
        int clampedLevel = Math.Clamp(level, 1, 9);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            PoolGeneration gen = GetActiveGeneration();
            if (gen.TryAcquireLease())
            {
                ZstandardEncoder? encoder = null;
                try
                {
                    encoder = gen.RentOrCreateEncoder(clampedLevel);
                    unscopedLeaseOwners.AddOrUpdate(encoder, new UnscopedLeaseEntry(gen, useDictionary: gen.Dictionary != null));
                    return encoder;
                }
                catch
                {
                    encoder?.Dispose();
                    gen.ReleaseReference();
                    throw;
                }
            }
        }

        ZstandardCompressionOptions? fallbackOptions = ZstdNetcodeDictionary.Instance.GetCompressionOptions(clampedLevel);
        return fallbackOptions != null ? new ZstandardEncoder(fallbackOptions) : new ZstandardEncoder(clampedLevel);
    }

    /// <summary>
    ///     Returns a healthy ZstandardEncoder to the pool, resetting its internal state.
    ///     If the encoder faulted, belongs to a retired dictionary generation, or the pool is full, it is disposed immediately.
    /// </summary>
    public static void ReturnEncoder(ZstandardEncoder encoder, int level = 3, bool isFaulted = false)
    {
        if (encoder == null) return;

        if (unscopedLeaseOwners.TryGetValue(encoder, out UnscopedLeaseEntry? entry))
        {
            unscopedLeaseOwners.Remove(encoder);
            if (entry.TryConsume(out PoolGeneration? ownerGen))
            {
                ReturnEncoderCore(ownerGen, encoder, Math.Clamp(level, 1, 9), isFaulted);
                return;
            }
        }

        if (isFaulted)
        {
            Interlocked.Increment(ref faultedDisposeCount);
        }

        encoder.Dispose();
    }

    /// <summary>
    ///     Rents a ZstandardDecoder (either dictionary-primed or raw fallback).
    /// </summary>
    public static ZstandardDecoder RentDecoder(bool useDictionary = true)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            PoolGeneration gen = GetActiveGeneration();
            if (gen.TryAcquireLease())
            {
                ZstandardDecoder? decoder = null;
                try
                {
                    decoder = gen.RentOrCreateDecoder(useDictionary, out bool isDictionaryPrimed);
                    unscopedLeaseOwners.AddOrUpdate(decoder, new UnscopedLeaseEntry(gen, isDictionaryPrimed));
                    return decoder;
                }
                catch
                {
                    decoder?.Dispose();
                    gen.ReleaseReference();
                    throw;
                }
            }
        }

        ZstandardDictionary? fallbackDict = useDictionary ? ZstdNetcodeDictionary.Instance.CompiledDictionary : null;
        return fallbackDict != null ? new ZstandardDecoder(fallbackDict) : new ZstandardDecoder();
    }

    /// <summary>
    ///     Returns a healthy ZstandardDecoder to the pool.
    ///     If the decoder faulted, belongs to a retired dictionary generation, or the pool is full, it is disposed immediately.
    /// </summary>
    public static void ReturnDecoder(ZstandardDecoder decoder, bool useDictionary = true, bool isFaulted = false)
    {
        if (decoder == null) return;

        if (unscopedLeaseOwners.TryGetValue(decoder, out UnscopedLeaseEntry? entry))
        {
            unscopedLeaseOwners.Remove(decoder);
            if (entry.TryConsume(out PoolGeneration? ownerGen))
            {
                ReturnDecoderCore(ownerGen, decoder, entry.UseDictionary, isFaulted);
                return;
            }
        }

        if (isFaulted)
        {
            Interlocked.Increment(ref faultedDisposeCount);
        }

        decoder.Dispose();
    }

    private static void ReturnEncoderCore(
        PoolGeneration? gen,
        ZstandardEncoder encoder,
        int clampedLevel,
        bool isFaulted)
    {
        if (gen == null)
        {
            if (isFaulted)
            {
                Interlocked.Increment(ref faultedDisposeCount);
            }

            encoder.Dispose();
            return;
        }

        try
        {
            if (isFaulted)
            {
                Interlocked.Increment(ref faultedDisposeCount);
                encoder.Dispose();
                return;
            }

            if (gen.IsRetired)
            {
                Interlocked.Increment(ref staleGenerationDisposeCount);
                encoder.Dispose();
                return;
            }

            try
            {
                encoder.Reset();
            }
            catch
            {
                Interlocked.Increment(ref faultedDisposeCount);
                encoder.Dispose();
                return;
            }

            if (!gen.TryReturnEncoder(clampedLevel, encoder))
            {
                if (gen.IsRetired)
                {
                    Interlocked.Increment(ref staleGenerationDisposeCount);
                }
                else
                {
                    Interlocked.Increment(ref overflowDisposeCount);
                }

                encoder.Dispose();
            }
        }
        finally
        {
            gen.ReleaseReference();
        }
    }

    private static void ReturnDecoderCore(
        PoolGeneration? gen,
        ZstandardDecoder decoder,
        bool isDictionaryPrimed,
        bool isFaulted)
    {
        if (gen == null)
        {
            if (isFaulted)
            {
                Interlocked.Increment(ref faultedDisposeCount);
            }

            decoder.Dispose();
            return;
        }

        try
        {
            if (isFaulted)
            {
                Interlocked.Increment(ref faultedDisposeCount);
                decoder.Dispose();
                return;
            }

            if (gen.IsRetired)
            {
                Interlocked.Increment(ref staleGenerationDisposeCount);
                decoder.Dispose();
                return;
            }

            try
            {
                decoder.Reset();
            }
            catch
            {
                Interlocked.Increment(ref faultedDisposeCount);
                decoder.Dispose();
                return;
            }

            if (!gen.TryReturnDecoder(isDictionaryPrimed, decoder))
            {
                if (gen.IsRetired)
                {
                    Interlocked.Increment(ref staleGenerationDisposeCount);
                }
                else
                {
                    Interlocked.Increment(ref overflowDisposeCount);
                }

                decoder.Dispose();
            }
        }
        finally
        {
            gen.ReleaseReference();
        }
    }
}
