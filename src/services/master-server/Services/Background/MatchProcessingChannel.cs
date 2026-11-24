// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ASPIRE.Common.Constants;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Configuration options for sharded in-memory match processing channels and Redis Streams.
/// </summary>
public sealed class MatchProcessingChannelOptions
{
    /// <summary>
    ///     Number of dedicated worker channel partitions.
    ///     Defaults to Environment.ProcessorCount clamped between 4 and 32.
    /// </summary>
    public int PartitionCount { get; set; } = Math.Clamp(Environment.ProcessorCount, 4, 32);

    /// <summary>
    ///     Bounded buffer capacity per individual partition channel.
    ///     Defaults to 1,000 jobs per partition.
    /// </summary>
    public int CapacityPerPartition { get; set; } = 1_000;

    /// <summary>
    ///     Full mode behavior when an individual partition buffer is saturated.
    ///     Defaults to Wait (asynchronous backpressure).
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    ///     Redis Stream key used for durable match ingestion.
    /// </summary>
    public string StreamKey { get; set; } = "kongor:stream:matches";

    /// <summary>
    ///     Redis Stream consumer group name.
    /// </summary>
    public string ConsumerGroup { get; set; } = "kongor_match_workers";

    /// <summary>
    ///     Whether Redis Streams is preferred when Redis is available.
    /// </summary>
    public bool UseRedisStreams { get; set; } = true;

    /// <summary>
    ///     When true (default in Production when a Redis/Valkey multiplexer is configured),
    ///     rejecting or losing Redis Streams durability marks the channel unhealthy and throws
    ///     instead of silently degrading match submissions to volatile process memory.
    /// </summary>
    public bool RequireDurableStreams { get; set; }
}

public interface IMatchProcessingChannel
{
    /// <summary>
    ///     Gets the total number of dedicated partition channels.
    /// </summary>
    int PartitionCount { get; }

    /// <summary>
    ///     Resolves the dedicated channel partition index for a given account ID.
    /// </summary>
    int GetPartition(int accountId);

    /// <summary>
    ///     Gets the current queue depth for a specific partition.
    /// </summary>
    int GetQueueDepth(int partitionIndex);

    /// <summary>
    ///     Gets the sum of current queue depths across all partitions.
    /// </summary>
    int TotalQueueDepth { get; }

    /// <summary>
    ///     Asynchronously enqueues a match submission payload to its designated partition.
    /// </summary>
    ValueTask<bool> EnqueueAsync(MatchSubmissionPayload payload, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously enqueues a match processing job to its designated partition.
    /// </summary>
    ValueTask<bool> EnqueueAsync(MatchProcessingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Attempts to synchronously write a match processing job to its designated partition.
    /// </summary>
    bool TryEnqueue(MatchProcessingJob job);

    /// <summary>
    ///     Acknowledges successful processing of a match job (XACK in Redis Streams).
    /// </summary>
    Task AcknowledgeAsync(MatchProcessingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads all incoming match jobs from a specific dedicated partition channel.
    /// </summary>
    IAsyncEnumerable<MatchSubmissionPayload> ReadAllAsync(int partitionIndex, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads incoming match jobs across all partition channels (fan-in multiplexer).
    /// </summary>
    IAsyncEnumerable<MatchSubmissionPayload> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets whether Redis Streams is actively supported and enabled for this channel.
    /// </summary>
    bool IsRedisStreamsSupported { get; }
    bool RequireDurableStreams { get; }
    string? DegradationReason { get; }
}

public sealed class MatchProcessingChannel : IMatchProcessingChannel
{
    private readonly Channel<MatchProcessingJob>[] _channels;
    private readonly int _partitionCount;
    private readonly ILogger<MatchProcessingChannel> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly string _streamKey;
    private readonly string _consumerGroup;
    private readonly bool _useRedisStreams;
    private readonly bool _requireDurableStreams;
    private volatile string? _degradationReason;
    private volatile bool _redisStreamsSupported = true;
    private int _redisStreamsDisabledLogged;
    private bool _groupInitialized;
    private readonly SemaphoreSlim _groupInitLock = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);
    private const byte DirectMemoryPackFormatTag = 0x02;

    public int PartitionCount => _partitionCount;

    public bool IsRedisStreamsSupported => _useRedisStreams && _redisStreamsSupported && (_redis == null || _redis.IsConnected);
    public bool RequireDurableStreams => _requireDurableStreams;
    public string? DegradationReason => _degradationReason;

    public int TotalQueueDepth
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _partitionCount; i++)
                total += _channels[i].Reader.Count;
            return total;
        }
    }

    public MatchProcessingChannel(ILogger<MatchProcessingChannel> logger)
        : this(logger, (MatchProcessingChannelOptions?)null, null)
    {
    }

    public MatchProcessingChannel(
        ILogger<MatchProcessingChannel> logger,
        IOptions<MatchProcessingChannelOptions>? options)
        : this(logger, options?.Value, null)
    {
    }

    public MatchProcessingChannel(
        ILogger<MatchProcessingChannel> logger,
        MatchProcessingChannelOptions? options)
        : this(logger, options, null)
    {
    }

    public MatchProcessingChannel(
        ILogger<MatchProcessingChannel> logger,
        IOptions<MatchProcessingChannelOptions>? options,
        IConnectionMultiplexer? connectionMultiplexer)
        : this(logger, options?.Value, connectionMultiplexer)
    {
    }

    public MatchProcessingChannel(
        ILogger<MatchProcessingChannel> logger,
        MatchProcessingChannelOptions? options,
        IConnectionMultiplexer? connectionMultiplexer)
    {
        _logger = logger;
        _redis = connectionMultiplexer;
        MatchProcessingChannelOptions opts = options ?? new MatchProcessingChannelOptions();

        _partitionCount = Math.Max(1, opts.PartitionCount);
        _streamKey = string.IsNullOrWhiteSpace(opts.StreamKey) ? "kongor:stream:matches" : opts.StreamKey;
        _consumerGroup = string.IsNullOrWhiteSpace(opts.ConsumerGroup) ? "kongor_match_workers" : opts.ConsumerGroup;
        _useRedisStreams = opts.UseRedisStreams;
        _requireDurableStreams = opts.RequireDurableStreams;
        _channels = new Channel<MatchProcessingJob>[_partitionCount];

        for (int i = 0; i < _partitionCount; i++)
        {
            BoundedChannelOptions channelOptions = new(opts.CapacityPerPartition)
            {
                FullMode = opts.FullMode,
                SingleWriter = false,
                SingleReader = true
            };
            _channels[i] = Channel.CreateBounded<MatchProcessingJob>(channelOptions);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[MatchProcessingChannel] Initialized sharded match processing channel with {PartitionCount} partitions (RedisStreams: {RedisStreamsEnabled}, Key: '{StreamKey}', Group: '{ConsumerGroup}').",
                _partitionCount, _useRedisStreams && _redis != null, _streamKey, _consumerGroup);
        }
    }

    private sealed class DurableStreamsUnavailableException(string message, Exception? innerException)
        : InvalidOperationException(message, innerException);

    private static bool IsUnknownCommand(Exception? ex)
    {
        if (ex == null) return false;
        if (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("ERR unknown", StringComparison.OrdinalIgnoreCase))
            return true;
        return ex.InnerException != null && IsUnknownCommand(ex.InnerException);
    }

    private void DisableRedisStreams(Exception? ex = null)
    {
        _redisStreamsSupported = false;
        _groupInitialized = true;
        string reason = ex?.Message ?? "Command unsupported";
        _degradationReason = reason;

        if (_requireDurableStreams)
        {
            _logger.LogCritical(ex,
                "[SECURITY-ALERT] [MatchProcessingChannel] Target cache engine rejected Redis Streams ({Reason}) while RequireDurableStreams=true! Deploy Valkey 8.1+ (valkey/valkey:8.1-alpine).",
                reason);
            throw new DurableStreamsUnavailableException(
                $"MatchProcessingChannel requires durable Valkey/Redis Streams, but target cache rejected Streams ({reason}). Deploy valkey/valkey:8.1-alpine or set MatchProcessingChannel:RequireDurableStreams=false for volatile mode.",
                ex);
        }

        DisableRedisStreamsLegacyLog(ex);
    }

    private void DisableRedisStreamsLegacyLog(Exception? ex = null)
    {
        _redisStreamsSupported = false;
        _groupInitialized = true;

        if (Interlocked.CompareExchange(ref _redisStreamsDisabledLogged, 1, 0) == 0)
        {
            _logger.LogWarning(
                "[MatchProcessingChannel] Target cache engine does not support Redis Streams ({Reason}). Seamlessly degrading to internal high-throughput in-memory bounded channels.",
                ex?.Message ?? "Command unsupported");
        }
    }

    private async ValueTask EnsureConsumerGroupAsync()
    {
        if (!_redisStreamsSupported || _groupInitialized || _redis == null || !_redis.IsConnected) return;
        await _groupInitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_redisStreamsSupported || _groupInitialized) return;
            IDatabase db = _redis.GetDatabase();
            try
            {
                await db.StreamCreateConsumerGroupAsync(_streamKey, _consumerGroup, StreamPosition.NewMessages, createStream: true).ConfigureAwait(false);
                _groupInitialized = true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
            {
                // Group already exists
                _groupInitialized = true;
            }
            catch (RedisServerException ex) when (IsUnknownCommand(ex))
            {
                DisableRedisStreams(ex);
            }
        }
        catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
        {
            if (IsUnknownCommand(ex))
            {
                DisableRedisStreams(ex);
            }
            else
            {
                _logger.LogWarning(ex, "[MatchProcessingChannel] Error initializing Redis stream consumer group '{Group}'", _consumerGroup);
            }
        }
        finally
        {
            _groupInitLock.Release();
        }
    }

    public async Task AcknowledgeAsync(MatchProcessingJob job, CancellationToken cancellationToken = default)
    {
        if (!_redisStreamsSupported || string.IsNullOrEmpty(job.RedisStreamMessageId) || _redis == null || !_redis.IsConnected)
            return;

        try
        {
            IDatabase db = _redis.GetDatabase();
            await db.StreamAcknowledgeAsync(_streamKey, _consumerGroup, job.RedisStreamMessageId).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[MatchProcessingChannel] Acknowledged message {MessageId} for Match ID {MatchId}",
                    job.RedisStreamMessageId, job.MatchID);
            }
        }
        catch (RedisServerException ex) when (IsUnknownCommand(ex))
        {
            DisableRedisStreams(ex);
        }
        catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
        {
            if (IsUnknownCommand(ex))
            {
                DisableRedisStreams(ex);
            }
            else
            {
                _logger.LogWarning(ex, "[MatchProcessingChannel] Failed to acknowledge message {MessageId} for Match ID {MatchId}",
                    job.RedisStreamMessageId, job.MatchID);
            }
        }
    }

    public int GetPartition(int accountId)
    {
        return (int)(unchecked((uint)accountId) % (uint)_partitionCount);
    }

    public int GetQueueDepth(int partitionIndex)
    {
        if (partitionIndex < 0 || partitionIndex >= _partitionCount)
            return 0;

        return _channels[partitionIndex].Reader.Count;
    }

    public int ResolvePartition(MatchProcessingJob job)
    {
        if (job.AccountId > 0)
            return GetPartition(job.AccountId);

        if (job.MatchServer?.HostAccountID is > 0)
            return GetPartition(job.MatchServer.HostAccountID);

        if (job.Form?.MatchStats != null &&
            job.Form.MatchStats.TryGetValue(MatchStatsProtocolKeys.MostValuablePlayer, out string? mvpStr) &&
            int.TryParse(mvpStr, out int mvpId) && mvpId > 0)
            return GetPartition(mvpId);

        // Fallback to deterministic partitioning by MatchID
        return (int)(unchecked((uint)job.MatchID) % (uint)_partitionCount);
    }

    public async ValueTask<bool> EnqueueAsync(MatchProcessingJob job,
        CancellationToken cancellationToken = default)
    {
        int partition = ResolvePartition(job);

        if (_redis != null && _redis.IsConnected && _useRedisStreams && _redisStreamsSupported)
        {
            try
            {
                await EnsureConsumerGroupAsync().ConfigureAwait(false);
                if (_redisStreamsSupported)
                {
                    byte[] binaryEnvelope = SerializeStreamJob(job, partition);
                    IDatabase db = _redis.GetDatabase();
                    NameValueEntry[] entries =
                    [
                        new("matchId", job.MatchID),
                        new("accountId", job.AccountId),
                        new("partition", partition),
                        new("job", binaryEnvelope)
                    ];

                    RedisValue messageId = await db.StreamAddAsync(_streamKey, entries).ConfigureAwait(false);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "[MatchProcessingChannel] Enqueued Match ID: {MatchId} to Redis Stream '{StreamKey}' (MessageId: {MessageId}, Partition: {Partition})",
                            job.MatchID, _streamKey, messageId, partition);
                    }

                    return true;
                }
            }
            catch (RedisServerException ex) when (IsUnknownCommand(ex))
            {
                DisableRedisStreams(ex);
            }
            catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
            {
                if (IsUnknownCommand(ex))
                {
                    DisableRedisStreams(ex);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "[MatchProcessingChannel] Failed to enqueue Match ID: {MatchId} to Redis Stream. Falling back to in-memory channel.",
                        job.MatchID);
                }
            }
        }

        Channel<MatchProcessingJob> channel = _channels[partition];
        while (await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (channel.Writer.TryWrite(job))
            {
                _signal.Release();
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "[MatchProcessingChannel] Enqueued Match ID: {MatchId} (AccountId: {AccountId}, Partition: {Partition}/{PartitionCount}, Depth: {Depth})",
                        job.MatchID, job.AccountId, partition, _partitionCount, channel.Reader.Count);
                }

                return true;
            }
        }

        return false;
    }

    public ValueTask<bool> EnqueueAsync(MatchSubmissionPayload payload,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync((MatchProcessingJob)payload, cancellationToken);
    }

    public bool TryEnqueue(MatchProcessingJob job)
    {
        int partition = ResolvePartition(job);

        if (_redis != null && _redis.IsConnected && _useRedisStreams && _redisStreamsSupported)
        {
            try
            {
                byte[] binaryEnvelope = SerializeStreamJob(job, partition);
                IDatabase db = _redis.GetDatabase();
                NameValueEntry[] entries =
                [
                    new("matchId", job.MatchID),
                    new("accountId", job.AccountId),
                    new("partition", partition),
                    new("job", binaryEnvelope)
                ];

                db.StreamAdd(_streamKey, entries);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "[MatchProcessingChannel] Non-blocking Enqueued Match ID: {MatchId} to Redis Stream '{StreamKey}' (Partition: {Partition})",
                        job.MatchID, _streamKey, partition);
                }
                return true;
            }
            catch (RedisServerException ex) when (IsUnknownCommand(ex))
            {
                DisableRedisStreams(ex);
            }
            catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
            {
                if (IsUnknownCommand(ex))
                {
                    DisableRedisStreams(ex);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "[MatchProcessingChannel] Non-blocking Redis StreamAdd failed for Match {MatchId}. Falling back to in-memory channel.",
                        job.MatchID);
                }
            }
        }

        Channel<MatchProcessingJob> channel = _channels[partition];
        bool written = channel.Writer.TryWrite(job);
        if (written)
        {
            _signal.Release();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[MatchProcessingChannel] Non-blocking Enqueued Match ID: {MatchId} (AccountId: {AccountId}, Partition: {Partition}/{PartitionCount}, Depth: {Depth})",
                    job.MatchID, job.AccountId, partition, _partitionCount, channel.Reader.Count);
            }
        }
        else if (!written && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "[MatchProcessingChannel] Non-blocking Enqueue FAILED for Match ID: {MatchId} on Partition: {Partition}/{PartitionCount} (Buffer full, depth: {Depth})",
                job.MatchID, partition, _partitionCount, channel.Reader.Count);
        }

        return written;
    }

    public async IAsyncEnumerable<MatchSubmissionPayload> ReadAllAsync(
        int partitionIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (partitionIndex < 0 || partitionIndex >= _partitionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionIndex),
                $"Partition index {partitionIndex} is out of range [0, {_partitionCount - 1}].");
        }

        if (_redis == null || !_useRedisStreams || !_redisStreamsSupported)
        {
            await foreach (MatchProcessingJob job in _channels[partitionIndex].Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return AsPayload(job);
            }
            yield break;
        }

        string consumerName = $"worker_{partitionIndex}";
        bool checkedPending = false;

        while (!cancellationToken.IsCancellationRequested && _redisStreamsSupported)
        {
            while (_channels[partitionIndex].Reader.TryRead(out MatchProcessingJob? localJob))
            {
                yield return AsPayload(localJob);
            }

            if (_redis.IsConnected && _redisStreamsSupported)
            {
                await EnsureConsumerGroupAsync().ConfigureAwait(false);
                if (!_redisStreamsSupported)
                {
                    break;
                }

                IDatabase db = _redis.GetDatabase();

                StreamEntry[]? entries = null;
                try
                {
                    if (!checkedPending)
                    {
                        entries = await db.StreamReadGroupAsync(
                            _streamKey,
                            _consumerGroup,
                            consumerName,
                            StreamPosition.Beginning,
                            count: 10).ConfigureAwait(false);

                        if (entries.Length == 0)
                        {
                            checkedPending = true;
                        }
                    }

                    if (checkedPending || entries == null || entries.Length == 0)
                    {
                        entries = await db.StreamReadGroupAsync(
                            _streamKey,
                            _consumerGroup,
                            consumerName,
                            StreamPosition.NewMessages,
                            count: 5).ConfigureAwait(false);
                    }
                }
                catch (RedisServerException ex) when (IsUnknownCommand(ex))
                {
                    DisableRedisStreams(ex);
                    break;
                }
                catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
                {
                    if (IsUnknownCommand(ex))
                    {
                        DisableRedisStreams(ex);
                        break;
                    }
                    _logger.LogWarning(ex, "[MatchProcessingChannel] Failed to read from Redis Stream '{StreamKey}'.", _streamKey);
                }

                if (entries is { Length: > 0 })
                {
                    foreach (StreamEntry entry in entries)
                    {
                        MatchProcessingJob? job = DeserializeStreamEntry(entry);
                        if (job != null)
                        {
                            yield return AsPayload(job);
                        }
                    }

                    continue;
                }
            }

            if (!_redisStreamsSupported)
            {
                break;
            }

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                await _channels[partitionIndex].Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout elapsed, loop again
            }
        }

        // Seamless fallback to pure in-memory channel reader when Redis Streams is disabled/unsupported
        if (!cancellationToken.IsCancellationRequested)
        {
            await foreach (MatchProcessingJob job in _channels[partitionIndex].Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return AsPayload(job);
            }
        }
    }

    public async IAsyncEnumerable<MatchSubmissionPayload> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_partitionCount == 1)
        {
            await foreach (MatchProcessingJob job in _channels[0].Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return AsPayload(job);
            }
            yield break;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            bool yieldedAny = false;
            for (int i = 0; i < _partitionCount; i++)
            {
                while (_channels[i].Reader.TryRead(out MatchProcessingJob? item))
                {
                    yield return AsPayload(item);
                    yieldedAny = true;
                }
            }

            if (yieldedAny)
                continue;

            bool allCompleted = true;
            for (int i = 0; i < _partitionCount; i++)
            {
                if (!_channels[i].Reader.Completion.IsCompleted)
                {
                    allCompleted = false;
                    break;
                }
            }

            if (allCompleted)
                yield break;

            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static byte[] SerializeStreamJob(MatchProcessingJob job, int partition)
    {
        byte[] jobBytes = MemoryPackSerializer.Serialize(job);
        byte[] result = new byte[jobBytes.Length + 1];
        result[0] = DirectMemoryPackFormatTag;
        Buffer.BlockCopy(jobBytes, 0, result, 1, jobBytes.Length);
        return result;
    }

    private MatchProcessingJob? DeserializeStreamEntry(StreamEntry entry)
    {
        RedisValue jobVal = default;
        foreach (NameValueEntry nv in entry.Values)
        {
            if (nv.Name == "job")
            {
                jobVal = nv.Value;
                break;
            }
        }

        if (jobVal.IsNullOrEmpty) return null;

        byte[]? bytes = (byte[]?)jobVal;
        if (bytes is { Length: > 1 } && bytes[0] == DirectMemoryPackFormatTag)
        {
            try
            {
                MatchProcessingJob? directJob = MemoryPackSerializer.Deserialize<MatchProcessingJob>(bytes.AsSpan(1));
                if (directJob != null)
                {
                    directJob.RedisStreamMessageId = entry.Id;
                    return directJob;
                }
            }
            catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MatchProcessingChannel] Direct MemoryPack parsing failed for {MessageId}, falling back", entry.Id);
            }
        }

        if (bytes is { Length: > 0 } && bytes[0] != '{' && bytes[0] != '[')
        {
            try
            {
                MatchJobStreamEnvelope? envelope = MemoryPackSerializer.Deserialize<MatchJobStreamEnvelope>(bytes);
                if (envelope is { Payload.Length: > 0 })
                {
                    MatchProcessingJob? envJob = envelope.SerializationFormat == 1
                        ? MemoryPackSerializer.Deserialize<MatchProcessingJob>(envelope.Payload)
                        : JsonSerializer.Deserialize<MatchProcessingJob>(envelope.Payload);

                    if (envJob != null)
                    {
                        envJob.RedisStreamMessageId = entry.Id;
                        return envJob;
                    }
                }
            }
            catch (DurableStreamsUnavailableException) { throw; }
        catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MatchProcessingChannel] MemoryPack envelope parsing failed for {MessageId}, trying legacy JSON", entry.Id);
            }
        }

        string? jobJson = jobVal.ToString();
        if (!string.IsNullOrWhiteSpace(jobJson))
        {
            try
            {
                MatchProcessingJob? jsonJob = JsonSerializer.Deserialize<MatchProcessingJob>(jobJson);
                if (jsonJob != null)
                {
                    jsonJob.RedisStreamMessageId = entry.Id;
                    return jsonJob;
                }
            }
            catch (Exception jsonEx)
            {
                _logger.LogError(jsonEx, "[MatchProcessingChannel] Failed to deserialize match job from Stream Entry {MessageId}", entry.Id);
            }
        }

        return null;
    }

    private static MatchSubmissionPayload AsPayload(MatchProcessingJob job)
    {
        return job is MatchSubmissionPayload p
            ? p
            : new MatchSubmissionPayload
            {
                MatchID = job.MatchID,
                Form = job.Form,
                BinaryPayload = job.BinaryPayload,
                MatchServer = job.MatchServer,
                RemoteIp = job.RemoteIp,
                EnqueuedAt = job.EnqueuedAt,
                AccountId = job.AccountId,
                RedisStreamMessageId = job.RedisStreamMessageId,
                IsUnrankedListenServer = job.IsUnrankedListenServer
            };
    }
}
