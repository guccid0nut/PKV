// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ASPIRE.Common.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using StackExchange.Redis;

namespace Kongor.Core.Services.Events;

/// <summary>
///     Configuration options for <see cref="RedisEventBus" />.
/// </summary>
public sealed class RedisEventBusOptions
{
    /// <summary>
    ///     Gets or sets the Redis channel name prefix for domain events. Default is "events:".
    /// </summary>
    public string ChannelPrefix { get; set; } = "events:";

    /// <summary>
    ///     Gets or sets the inbound message channel capacity. Default is 10,000.
    /// </summary>
    public int InboundQueueCapacity { get; set; } = 10_000;

    /// <summary>
    ///     Gets or sets the JSON serializer options.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

/// <summary>
///     Genuine distributed Redis Pub/Sub event bus backed by <see cref="IConnectionMultiplexer" /> and
///     <see cref="ISubscriber" />, resilient against transient failovers via Polly v8 pipelines.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Redis event bus message payload serialization")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Redis event bus message payload serialization")]
public sealed class RedisEventBus : IEventBus, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, Type> _channelToTypeMap = new();
    private readonly IConnectionMultiplexer? _connectionMultiplexer;
    private readonly CancellationTokenSource _cts = new();

    private readonly
        ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>>
        _handlers = new();

    private readonly Channel<(string Channel, string Body)> _inboundChannel;
    private readonly ILogger<RedisEventBus> _logger;
    private readonly RedisEventBusOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ConcurrentDictionary<string, ChannelMessageQueue> _subscribedQueues = new();
    private readonly ISubscriber? _subscriber;
    private bool _disposed;
    private Task? _inboundProcessingTask;

    /// <summary>
    ///     Initializes a new instance of <see cref="RedisEventBus" />.
    /// </summary>
    public RedisEventBus(
        IConnectionMultiplexer? connectionMultiplexer,
        RedisEventBusOptions? options = null,
        IServiceProvider? serviceProvider = null,
        ILogger<RedisEventBus>? logger = null,
        ResiliencePipeline? resiliencePipeline = null)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _subscriber = connectionMultiplexer?.GetSubscriber();
        _options = options ?? new RedisEventBusOptions();
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<RedisEventBus>.Instance;
        _resiliencePipeline = resiliencePipeline ?? NexusResiliencePipelines.CreateRedisRetryPipeline(_logger);

        BoundedChannelOptions boundedOptions = new(_options.InboundQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _inboundChannel = Channel.CreateBounded<(string Channel, string Body)>(boundedOptions);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _inboundChannel.Writer.TryComplete();

        if (_inboundProcessingTask != null)
            try
            {
                await _inboundProcessingTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RedisEventBus] Ignored inbound processing task error during DisposeAsync.");
            }

        foreach (KeyValuePair<string, ChannelMessageQueue> pair in _subscribedQueues)
            try
            {
                await pair.Value.UnsubscribeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RedisEventBus] Ignored unsubscribe error for '{Channel}' during DisposeAsync.", pair.Key);
            }

        _subscribedQueues.Clear();
        _cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _inboundChannel.Writer.TryComplete();
        _cts.Dispose();
    }

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        ObjectDisposedException.ThrowIf(_disposed, this);

        string channelName = GetChannelName(typeof(TEvent));
        string payload = JsonSerializer.Serialize(@event, _options.JsonSerializerOptions);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "[RedisEventBus] Publishing event {EventType} (ID: {EventId}) to Redis channel '{Channel}'",
                typeof(TEvent).Name, @event.EventId, channelName);

        if (_subscriber != null)
        {
            try
            {
                await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    await _subscriber.PublishAsync(RedisChannel.Literal(channelName), payload).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "[RedisEventBus] Failed to publish event {EventType} to Redis channel '{Channel}' after retries. Falling back to local dispatch.",
                    typeof(TEvent).Name, channelName);
                await DispatchLocalEventAsync(@event, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // If Redis subscriber is unavailable (e.g. standalone test mode), fallback to local dispatch
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "[RedisEventBus] ISubscriber is not configured. Dispatching event {EventType} locally.",
                    typeof(TEvent).Name);

            await DispatchLocalEventAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SubscribeAsync<TEvent>(IEventHandler<TEvent> handler,
        CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        EnsureInboundProcessingStarted();

        Type eventType = typeof(TEvent);
        ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>> handlerMap =
            _handlers.GetOrAdd(eventType,
                _ => new ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>());

        handlerMap[handler] = (@event, ct) => handler.HandleAsync((TEvent)@event, ct);

        string channelName = GetChannelName(eventType);
        _channelToTypeMap[channelName] = eventType;

        if (_subscriber != null && !_subscribedQueues.ContainsKey(channelName))
            try
            {
                RedisChannel redisChannel = RedisChannel.Literal(channelName);
                ChannelMessageQueue queue = await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    return await _subscriber.SubscribeAsync(redisChannel).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                _subscribedQueues[channelName] = queue;

                // Process inbound messages via bounded channel backpressure to avoid blocking Redis socket thread
                queue.OnMessage(channelMessage =>
                {
                    string messageBody = channelMessage.Message.ToString();
                    if (!_inboundChannel.Writer.TryWrite((channelName, messageBody)))
                    {
                        _logger.LogWarning(
                            "[RedisEventBus] Inbound queue full for Redis channel '{Channel}'. Writing asynchronously.",
                            channelName);
                        _ = _inboundChannel.Writer.WriteAsync((channelName, messageBody), _cts.Token).AsTask();
                    }
                });

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[RedisEventBus] Subscribed to Redis channel '{Channel}' for event {EventType}",
                        channelName, eventType.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RedisEventBus] Failed to subscribe to Redis channel '{Channel}' for {EventType}",
                    channelName, eventType.Name);
            }
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync<TEvent>(IEventHandler<TEvent> handler,
        CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        Type eventType = typeof(TEvent);
        string channelName = GetChannelName(eventType);

        if (_handlers.TryGetValue(eventType,
                out ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>? handlerMap))
        {
            handlerMap.TryRemove(handler, out _);

            if (handlerMap.IsEmpty && _subscriber != null)
                if (_subscribedQueues.TryRemove(channelName, out ChannelMessageQueue? queue))
                    try
                    {
                        await queue.UnsubscribeAsync().ConfigureAwait(false);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[RedisEventBus] Unsubscribed from Redis channel '{Channel}'",
                                channelName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RedisEventBus] Error unsubscribing from Redis channel '{Channel}'",
                            channelName);
                    }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureInboundProcessingStarted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _inboundChannel.Writer.TryComplete();

        if (_inboundProcessingTask != null)
            try
            {
                await _inboundProcessingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "[RedisEventBus] Inbound processor cancelled during StopAsync.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RedisEventBus] Error waiting for inbound message processor to terminate");
            }

        foreach (KeyValuePair<string, ChannelMessageQueue> pair in _subscribedQueues)
            try
            {
                await pair.Value.UnsubscribeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RedisEventBus] Ignored unsubscribe error for '{Channel}' during StopAsync.", pair.Key);
            }

        _subscribedQueues.Clear();
    }

    private void EnsureInboundProcessingStarted()
    {
        if (_inboundProcessingTask == null && !_disposed)
            lock (_cts)
            {
                if (_inboundProcessingTask == null && !_disposed)
                    _inboundProcessingTask = Task.Run(() => ProcessInboundMessagesAsync(_cts.Token));
            }
    }

    private string GetChannelName(Type eventType)
    {
        return $"{_options.ChannelPrefix}{eventType.Name}";
    }

    private async Task ProcessInboundMessagesAsync(CancellationToken cancellationToken)
    {
        ChannelReader<(string Channel, string Body)> reader = _inboundChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (reader.TryRead(out (string Channel, string Body) item))
                await OnRedisMessageReceivedAsync(item.Channel, item.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "[RedisEventBus] Inbound message loop stopped via cancellation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RedisEventBus] Fatal unhandled exception in inbound Redis message processing loop");
        }
    }

    private async Task OnRedisMessageReceivedAsync(string channelName, string messageBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageBody)) return;

        if (!_channelToTypeMap.TryGetValue(channelName, out Type? eventType))
        {
            _logger.LogWarning("[RedisEventBus] Received message on unmapped Redis channel '{Channel}'", channelName);
            return;
        }

        try
        {
            object? deserialized = JsonSerializer.Deserialize(messageBody, eventType, _options.JsonSerializerOptions);
            if (deserialized is IDomainEvent domainEvent)
                await DispatchLocalEventAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[RedisEventBus] Failed to deserialize or dispatch event from Redis channel '{Channel}'. Body: {Body}",
                channelName, messageBody);
        }
    }

    private async Task DispatchLocalEventAsync(IDomainEvent @event, CancellationToken cancellationToken)
    {
        Type eventType = @event.GetType();
        List<Task> executionTasks = [];

        // In-memory subscribed delegate handlers (including ServiceProviderEventHandlerProxy for DI-resolved handlers)
        if (_handlers.TryGetValue(eventType,
                out ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>? handlerMap))
            foreach (Func<IDomainEvent, CancellationToken, Task> handlerFunc in handlerMap.Values)
                executionTasks.Add(ExecuteHandlerSafelyAsync(handlerFunc, @event, cancellationToken));

        if (executionTasks.Count > 0) await Task.WhenAll(executionTasks).ConfigureAwait(false);
    }

    private async Task ExecuteHandlerSafelyAsync(
        Func<IDomainEvent, CancellationToken, Task> handlerFunc,
        IDomainEvent @event,
        CancellationToken cancellationToken)
    {
        try
        {
            await handlerFunc(@event, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[RedisEventBus] Exception thrown by event handler for event {EventType} (ID: {EventId})",
                @event.GetType().Name, @event.EventId);
        }
    }
}
