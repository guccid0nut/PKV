// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Services.Events;

/// <summary>
///     Configuration options for <see cref="ChannelsEventBus" />.
/// </summary>
public sealed class ChannelsEventBusOptions
{
    /// <summary>
    ///     Gets or sets whether the internal channel is bounded. Default is false (unbounded).
    /// </summary>
    public bool IsBounded { get; set; }

    /// <summary>
    ///     Gets or sets the bounded channel capacity when <see cref="IsBounded" /> is true. Default is 10,000.
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    ///     Gets or sets the full mode behavior when bounded capacity is reached. Default is
    ///     <see cref="BoundedChannelFullMode.Wait" />.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}

/// <summary>
///     Non-blocking in-memory event bus backed by <see cref="Channel{T}" /> with concurrent dispatch to registered
///     handlers.
/// </summary>
public sealed class ChannelsEventBus : IEventBus, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly Channel<IDomainEvent> _channel;
    private readonly CancellationTokenSource _cts = new();

    private readonly
        ConcurrentDictionary<Type, ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>>
        _handlers = new();

    private readonly ILogger<ChannelsEventBus> _logger;
    private readonly IServiceProvider? _serviceProvider;
    private readonly Lock _startLock = new();
    private bool _disposed;
    private bool _isStarted;
    private Task? _processingTask;

    /// <summary>
    ///     Initializes a new instance of <see cref="ChannelsEventBus" />.
    /// </summary>
    public ChannelsEventBus(
        ChannelsEventBusOptions? options = null,
        IServiceProvider? serviceProvider = null,
        ILogger<ChannelsEventBus>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<ChannelsEventBus>.Instance;

        options ??= new ChannelsEventBusOptions();

        if (options.IsBounded)
        {
            BoundedChannelOptions boundedOptions = new(options.Capacity)
            {
                FullMode = options.FullMode,
                SingleReader = false,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<IDomainEvent>(boundedOptions);
            _logger.LogDebug(
                "[ChannelsEventBus] Initialized bounded channel with capacity {Capacity}, full mode: {FullMode}",
                options.Capacity, options.FullMode);
        }
        else
        {
            UnboundedChannelOptions unboundedOptions = new()
            {
                SingleReader = false,
                SingleWriter = false
            };
            _channel = Channel.CreateUnbounded<IDomainEvent>(unboundedOptions);
            _logger.LogDebug("[ChannelsEventBus] Initialized unbounded channel");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try
        {
            if (!_cts.IsCancellationRequested) _cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] Ignored cancellation error during DisposeAsync.");
        }

        if (_processingTask != null)
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ChannelsEventBus] Ignored processing task error during DisposeAsync.");
            }

        try
        {
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] Ignored CTS disposal error during DisposeAsync.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try
        {
            if (!_cts.IsCancellationRequested) _cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] Ignored cancellation error during Dispose.");
        }

        try
        {
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] Ignored CTS disposal error during Dispose.");
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[ChannelsEventBus] Enqueuing event {EventType} (ID: {EventId})",
                typeof(TEvent).Name, @event.EventId);

        await _channel.Writer.WriteAsync(@event, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SubscribeAsync<TEvent>(IEventHandler<TEvent> handler, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        Type eventType = typeof(TEvent);
        ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>> handlerMap =
            _handlers.GetOrAdd(eventType,
                _ => new ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>());

        handlerMap[handler] = (@event, ct) => handler.HandleAsync((TEvent)@event, ct);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[ChannelsEventBus] Registered subscriber {HandlerType} for event {EventType}",
                handler.GetType().Name, eventType.Name);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync<TEvent>(IEventHandler<TEvent> handler, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        Type eventType = typeof(TEvent);
        if (_handlers.TryGetValue(eventType,
                out ConcurrentDictionary<object, Func<IDomainEvent, CancellationToken, Task>>? handlerMap))
        {
            handlerMap.TryRemove(handler, out _);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("[ChannelsEventBus] Unregistered subscriber {HandlerType} for event {EventType}",
                    handler.GetType().Name, eventType.Name);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isStarted)
            lock (_startLock)
            {
                if (!_isStarted)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("[ChannelsEventBus] Starting background event processing loop...");

                    _processingTask = Task.Run(() => ProcessEventsAsync(_cts.Token), CancellationToken.None);
                    _isStarted = true;

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "[ChannelsEventBus] Background event processing loop started successfully.");
                }
            }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[ChannelsEventBus] Stopping background event processing loop...");

        if (_disposed) return;

        _channel.Writer.TryComplete();
        try
        {
            if (!_cts.IsCancellationRequested) _cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] CTS already disposed during StopAsync.");
        }

        if (_processingTask != null)
            try
            {
                await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "[ChannelsEventBus] Processing task cancelled during StopAsync.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChannelsEventBus] Error waiting for event processor to terminate");
            }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[ChannelsEventBus] Background event processing loop stopped successfully.");
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        ChannelReader<IDomainEvent> reader = _channel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (reader.TryRead(out IDomainEvent? @event))
            {
                if (@event == null) continue;

                await DispatchEventAsync(@event, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "[ChannelsEventBus] Background event loop stopped via cancellation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChannelsEventBus] Fatal unhandled exception in background event processing loop");
        }
    }

    private async Task DispatchEventAsync(IDomainEvent @event, CancellationToken cancellationToken)
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
                "[ChannelsEventBus] Exception thrown by event handler for event {EventType} (ID: {EventId})",
                @event.GetType().Name, @event.EventId);
        }
    }
}
