// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.IO;

namespace ASPIRE.Common.Memory;

/// <summary>
///     High-performance memory stream manager leveraging RecyclableMemoryStreamManager
///     to eliminate Large Object Heap (LOH) fragmentation and Gen-2 GC pauses during concurrent match sessions,
///     game packet serialization, and replay chunking.
/// </summary>
public sealed class NexusMemoryStreamManager
{
    private static readonly Lazy<NexusMemoryStreamManager> _lazyShared = new(() => new NexusMemoryStreamManager());
    private readonly ILogger<NexusMemoryStreamManager>? _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NexusMemoryStreamManager" /> with gaming-tier pool settings.
    /// </summary>
    public NexusMemoryStreamManager(ILogger<NexusMemoryStreamManager>? logger = null)
    {
        _logger = logger;

        RecyclableMemoryStreamManager.Options options = new()
        {
            BlockSize = 128 * 1024, // 128 KB standard block size for game packets
            LargeBufferMultiple = 1024 * 1024, // 1 MB chunks for large replay blobs & sync packets
            MaximumBufferSize = 64 * 1024 * 1024, // 64 MB maximum buffer size
            MaximumSmallPoolFreeBytes = 64 * 1024 * 1024, // 64 MB pooled small blocks
            MaximumLargePoolFreeBytes = 256 * 1024 * 1024, // 256 MB pooled large buffers
            ThrowExceptionOnToArray = false, // Allow ToArray with warning
            AggressiveBufferReturn = true
        };

        UnderlyingManager = new RecyclableMemoryStreamManager(options);

        UnderlyingManager.BlockCreated += (sender, e) =>
        {
            _logger?.LogTrace("[MEMORY-POOL] New 128KB memory block allocated by RecyclableMemoryStreamManager.");
        };

        UnderlyingManager.LargeBufferCreated += (sender, e) =>
        {
            _logger?.LogDebug("[MEMORY-POOL] Large buffer allocated: RequiredSize={RequiredSize}, Pooled={Pooled}",
                e.RequiredSize, e.Pooled);
        };

        UnderlyingManager.UsageReport += (sender, e) =>
        {
            _logger?.LogTrace(
                "[MEMORY-POOL-REPORT] SmallPool: InUse={SmallInUse}B, Free={SmallFree}B | LargePool: InUse={LargeInUse}B, Free={LargeFree}B",
                e.SmallPoolInUseBytes, e.SmallPoolFreeBytes, e.LargePoolInUseBytes, e.LargePoolFreeBytes);
        };

        _logger?.LogInformation(
            "[MEMORY-POOL] NexusMemoryStreamManager initialized with 128KB blocks and 64MB max capacity.");
    }

    /// <summary>
    ///     Gets the global shared singleton instance for non-DI hot paths and high-frequency network encoders.
    /// </summary>
    public static NexusMemoryStreamManager Shared => _lazyShared.Value;

    /// <summary>
    ///     Underlying RecyclableMemoryStreamManager instance.
    /// </summary>
    public RecyclableMemoryStreamManager UnderlyingManager { get; }

    /// <summary>
    ///     Retrieves a recyclable memory stream with an empty payload.
    /// </summary>
    public MemoryStream GetStream()
    {
        return UnderlyingManager.GetStream();
    }

    /// <summary>
    ///     Retrieves a recyclable memory stream tagged with a designated operation name for profiling.
    /// </summary>
    public MemoryStream GetStream(string tag)
    {
        return UnderlyingManager.GetStream(tag);
    }

    /// <summary>
    ///     Retrieves a recyclable memory stream tagged with an operation name and initial capacity.
    /// </summary>
    public MemoryStream GetStream(string tag, int requiredSize)
    {
        return UnderlyingManager.GetStream(tag, requiredSize);
    }

    /// <summary>
    ///     Retrieves a recyclable memory stream initialized with the provided byte buffer span.
    /// </summary>
    public MemoryStream GetStream(ReadOnlySpan<byte> buffer, string tag = "PacketBuffer")
    {
        RecyclableMemoryStream stream = UnderlyingManager.GetStream(tag, buffer.Length);
        stream.Write(buffer);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    ///     Retrieves a recyclable memory stream initialized with the provided byte array.
    /// </summary>
    public MemoryStream GetStream(byte[] buffer, int offset, int count, string tag = "ByteBuffer")
    {
        return UnderlyingManager.GetStream(tag, buffer, offset, count);
    }
}

/// <summary>
///     Dependency injection extension methods for <see cref="NexusMemoryStreamManager" />.
/// </summary>
public static class NexusMemoryStreamServiceExtensions
{
    /// <summary>
    ///     Registers the <see cref="NexusMemoryStreamManager" /> as a singleton in the dependency injection container.
    /// </summary>
    public static IServiceCollection AddNexusMemoryStreamManager(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            ILogger<NexusMemoryStreamManager>? logger = sp.GetService<ILogger<NexusMemoryStreamManager>>();
            return new NexusMemoryStreamManager(logger);
        });
        return services;
    }
}
