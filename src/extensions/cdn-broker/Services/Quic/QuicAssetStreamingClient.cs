// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QuicAssetStreamingClient.cs
// System: NEXUS CDN Broker - QUIC Parallel Asset Streaming Engine
// Policy: Strict Explicit Typing, Sub-Second Execution, Zero Head-of-Line Blocking
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace WILLOWMAKER.Core.Services;

/// <summary>
/// Aggregated summary report of parallel asset downloads over QUIC / HTTP/3.
/// </summary>
public sealed record ParallelQuicSyncReport
{
    public int TotalFiles { get; init; }
    public int SucceededFiles { get; init; }
    public int FailedFiles { get; init; }
    public long TotalBytesTransferred { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public double AverageBytesPerSecond => TotalDuration.TotalSeconds > 0
        ? TotalBytesTransferred / TotalDuration.TotalSeconds
        : 0;
    public IReadOnlyDictionary<string, int> ProtocolBreakdown { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Individual asset download descriptor.
/// </summary>
public sealed record AssetStreamingRequest
{
    public string Url { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public long ExpectedSizeBytes { get; init; }
}

/// <summary>
/// Parallel client coordinator multiplexing CDN asset streaming over independent QUIC streams.
/// In HTTP/3, independent streams on the same UDP connection avoid TCP head-of-line blocking.
/// </summary>
public sealed class QuicAssetStreamingClient
{
    private readonly IQuicCdnTransportService _transportService;
    private readonly TimeProvider _timeProvider;

    public QuicAssetStreamingClient(
        IQuicCdnTransportService? transportService = null,
        TimeProvider? timeProvider = null)
    {
        _transportService = transportService ?? new QuicCdnTransportService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Downloads multiple assets in parallel using independent QUIC/HTTP streams with bounded concurrency.
    /// </summary>
    public async Task<ParallelQuicSyncReport> DownloadAssetsParallelAsync(
        IReadOnlyList<AssetStreamingRequest> requests,
        int maxConcurrency = 8,
        IProgress<long>? aggregateProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        if (requests.Count == 0)
        {
            return new ParallelQuicSyncReport
            {
                TotalFiles = 0,
                SucceededFiles = 0,
                FailedFiles = 0,
                TotalBytesTransferred = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        long startTimestamp = _timeProvider.GetTimestamp();
        using SemaphoreSlim semaphore = new(maxConcurrency, maxConcurrency);
        ConcurrentBag<string> errors = [];
        ConcurrentDictionary<string, int> protocolCounts = new(StringComparer.OrdinalIgnoreCase);
        long sharedBytesTransferred = 0;
        int successCount = 0;
        int failCount = 0;

        List<Task> tasks = new(requests.Count);

        foreach (AssetStreamingRequest req in requests)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string? dir = Path.GetDirectoryName(req.DestinationPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tempPath = req.DestinationPath + ".part";
                    long fileBytesRead = 0;

                    SynchronousProgress<long> fileProgress = new(bytes =>
                    {
                        long delta = bytes - Interlocked.Exchange(ref fileBytesRead, bytes);
                        if (delta > 0)
                        {
                            long newTotal = Interlocked.Add(ref sharedBytesTransferred, delta);
                            aggregateProgress?.Report(newTotal);
                        }
                    });

                    await using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CdnStreamingResult result = await _transportService.StreamAssetAsync(
                            req.Url,
                            fs,
                            fileProgress,
                            cancellationToken).ConfigureAwait(false);

                        if (result.Success)
                        {
                            long remaining = result.BytesTransferred - Interlocked.Read(ref fileBytesRead);
                            if (remaining > 0)
                            {
                                long newTotal = Interlocked.Add(ref sharedBytesTransferred, remaining);
                                aggregateProgress?.Report(newTotal);
                            }

                            protocolCounts.AddOrUpdate(result.ProtocolDescription, 1, (_, count) => count + 1);
                        }
                        else
                        {
                            errors.Add($"Failed to stream '{req.Url}': {result.ErrorMessage}");
                            Interlocked.Increment(ref failCount);
                            return;
                        }
                    }

                    if (File.Exists(req.DestinationPath))
                    {
                        File.Delete(req.DestinationPath);
                    }

                    File.Move(tempPath, req.DestinationPath);
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    errors.Add($"Exception streaming '{req.Url}': {ex.Message}");
                    Interlocked.Increment(ref failCount);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        TimeSpan totalDuration = _timeProvider.GetElapsedTime(startTimestamp);

        return new ParallelQuicSyncReport
        {
            TotalFiles = requests.Count,
            SucceededFiles = successCount,
            FailedFiles = failCount,
            TotalBytesTransferred = Volatile.Read(ref sharedBytesTransferred),
            TotalDuration = totalDuration,
            ProtocolBreakdown = protocolCounts,
            Errors = [.. errors]
        };
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

