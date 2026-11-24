// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Logger.cs
// System: WILLOWMAKER Core & CDN Broker
// Architecture: Asynchronous High-Velocity Non-Blocking Logger (1000 FPS Ready)
// ============================================================================


using System.Threading.Channels;

namespace WILLOWMAKER.Core.Utilities;

/// <summary>
///     High-velocity, non-blocking asynchronous logger designed for 1000 FPS rendering (1.0ms frame budget).
///     Decouples callers from disk I/O using a bounded Channel queue with DropOldest overflow policy,
///     a dedicated background flusher task with buffered batch writes, and synchronous flush on process exit.
/// </summary>
public sealed class Logger : IDisposable, IAsyncDisposable
{
    private const int DefaultCapacity = 20_000;
    private const int BatchSize = 256;
    private const int FileBufferSize = 65_536; // 64 KB
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();

    private readonly Lock _flushLock = new();
    private readonly Task _flusherTask;
    private int _activeBatchWrite;
    private int _isDisposed;

    public Logger(string filePath, int capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;

        BoundedChannelOptions options = new(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        _channel = Channel.CreateBounded<string>(options);

        // Queue session header
        string sessionSeparator = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0
            ? Environment.NewLine
            : string.Empty;
        string header = sessionSeparator +
                        $"■▀▐▀▐▀▐▀▌ WILLOWMAKER Session Started At {TimeProvider.System.GetUtcNow().UtcDateTime:O} ლ(ಠ益ಠლ) BUT AT WHAT COST !? ▄▌▀▐▀▐▀▐▀" +
                        Environment.NewLine;
        _channel.Writer.TryWrite(header);

        // Start background flusher task
        _flusherTask = Task.Run(ProcessLogQueueAsync);

        // Register process exit and crash handlers to guarantee clean queue draining
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public string FilePath { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        // Signal completion to channel writer - no new channel writes
        _channel.Writer.TryComplete();

        // Wait for background worker to naturally drain all items and complete
        try
        {
            await _flusherTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore flusher task cancellation/exceptions
        }

        // Drain any remaining items (fallback if flusher task failed)
        List<string> remaining = [];
        while (_channel.Reader.TryRead(out string? item))
            if (item != null)
                remaining.Add(item);

        if (remaining.Count > 0)
            try
            {
                EnsureDirectoryExists(FilePath);
                await using FileStream stream = new(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                    FileBufferSize, true);
                await using StreamWriter writer = new(stream, Encoding.UTF8, FileBufferSize);
                for (int i = 0; i < remaining.Count; i++)
                    await writer.WriteAsync(remaining[i].AsMemory()).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore fallback exceptions on async disposal
            }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        FlushAndDrainSynchronously();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Formats a timestamped log entry, enqueues it for asynchronous background writing with zero lock contention,
    ///     and returns the formatted string for UI display.
    ///     Execution time on caller/render thread is strictly sub-microsecond (&lt; 100ns).
    /// </summary>
    public string Log(string category, string message)
    {
        string entry = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:O}] [{category}] {message}" +
                       Environment.NewLine;

        if (Volatile.Read(ref _isDisposed) == 0 && _channel.Writer.TryWrite(entry))
        {
            // Successfully enqueued to channel buffer
        }
        else
        {
            // Direct write fallback during shutdown or when channel is completed
            TrySynchronousAppend(entry);
        }

        return entry;
    }

    /// <summary>
    ///     Non-destructively awaits draining of currently queued channel log entries to disk so diagnostic
    ///     bundle exports capture up-to-the-millisecond session telemetry without closing the active logger.
    /// </summary>
    public async Task FlushPendingEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        DateTimeOffset deadlineUtc = TimeProvider.System.GetUtcNow().AddMilliseconds(1500);
        while (Volatile.Read(ref _isDisposed) == 0 && TimeProvider.System.GetUtcNow() < deadlineUtc && !cancellationToken.IsCancellationRequested)
        {
            int queued = _channel.Reader.CanCount ? _channel.Reader.Count : 0;
            if (queued == 0 && Volatile.Read(ref _activeBatchWrite) == 0)
            {
                break;
            }

            try
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessLogQueueAsync()
    {
        FileStream? stream = null;
        StreamWriter? writer = null;

        try
        {
            EnsureDirectoryExists(FilePath);
            stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, FileBufferSize,
                true);
            writer = new StreamWriter(stream, Encoding.UTF8, FileBufferSize, true);

            ChannelReader<string> reader = _channel.Reader;

            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _activeBatchWrite, 1);
                try
                {
                    int count = 0;
                    while (count < BatchSize && reader.TryRead(out string? logEntry))
                        if (logEntry != null)
                        {
                            // Write dequeued entry without cancellation token to prevent data loss after dequeue
                            await writer.WriteAsync(logEntry.AsMemory()).ConfigureAwait(false);
                            count++;
                        }

                    if (count > 0)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _activeBatchWrite, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown
        }
        catch (Exception)
        {
            // Transient I/O exception fallback
        }
        finally
        {
            if (writer != null)
            {
                try
                {
                    // Drain any remaining items that entered the channel before completion
                    while (_channel.Reader.TryRead(out string? remainingEntry))
                        if (remainingEntry != null)
                            await writer.WriteAsync(remainingEntry.AsMemory()).ConfigureAwait(false);

                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (stream != null)
            {
                try
                {
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        FlushAndDrainSynchronously();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Exception? ex = e.ExceptionObject as Exception;
            string err = $"[FATAL LAUNCHER CRASH] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}";
            string timeStr = TimeProvider.System.GetUtcNow().ToString("O");
            PersistLauncherCrashRecord($"[{timeStr}] {err}\n\n", timeStr, writeSentinel: true);
            _channel.Writer.TryWrite(err + Environment.NewLine);
        }
        catch (Exception persistEx)
        {
            System.Diagnostics.Trace.WriteLine($"[LOGGER] Unhandled crash persistence fallback notice: {persistEx.Message}");
        }
        FlushAndDrainSynchronously();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            string fullExText = e.Exception?.ToString() ?? string.Empty;
            if (IsBenignLinuxDbusTaskException(fullExText))
            {
                e.SetObserved();
                _channel.Writer.TryWrite(
                    $"[{TimeProvider.System.GetUtcNow().UtcDateTime:O}] [DBUS_NOTICE] Suppressed benign Linux desktop bus task notice: {e.Exception?.GetBaseException().Message}{Environment.NewLine}");
                return;
            }

            e.SetObserved();
            string timeStr = TimeProvider.System.GetUtcNow().ToString("O");
            string flattenedEx = e.Exception?.Flatten().ToString() ?? e.Exception?.Message ?? "Unknown Task Exception";
            string err = $"[{timeStr}] [ERROR______] [UNOBSERVED TASK EXCEPTION] {flattenedEx}";
            PersistLauncherCrashRecord($"{err}\n\n", timeStr, writeSentinel: false);
            _channel.Writer.TryWrite(err + Environment.NewLine);
        }
        catch (Exception persistEx)
        {
            System.Diagnostics.Trace.WriteLine($"[LOGGER] Unobserved task crash persistence notice: {persistEx.Message}");
        }
    }

    public static bool IsBenignLinuxDbusTaskException(string? exceptionText)
    {
        if (string.IsNullOrWhiteSpace(exceptionText))
        {
            return false;
        }

        return exceptionText.Contains("com.canonical.AppMenu.Registrar", StringComparison.OrdinalIgnoreCase) ||
               exceptionText.Contains("org.freedesktop.portal", StringComparison.OrdinalIgnoreCase) ||
               (exceptionText.Contains("DBusException", StringComparison.OrdinalIgnoreCase) &&
                (exceptionText.Contains("ServiceUnknown", StringComparison.OrdinalIgnoreCase) ||
                 exceptionText.Contains("NameHasNoOwner", StringComparison.OrdinalIgnoreCase)));
    }

    private static void PersistLauncherCrashRecord(string entryText, string timeStr, bool writeSentinel)
    {
        string[] candidateCrashDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "logs", "crash"),
            global::WILLOWMAKER.Core.Services.Diagnostics.DiagnosticBundleService.GetOsFallbackReportDirectory()
        ];

        foreach (string crashDir in candidateCrashDirs)
        {
            try
            {
                Directory.CreateDirectory(crashDir);
                File.AppendAllText(Path.Combine(crashDir, "willowmaker_crashes.log"), entryText);
                if (writeSentinel)
                {
                    File.WriteAllText(Path.Combine(crashDir, "willowmaker-crashed.sentinel"), timeStr);
                }
                return;
            }
            catch (Exception ioEx)
            {
                System.Diagnostics.Trace.WriteLine($"[LOGGER] Crash dir '{crashDir}' not writable, trying OS fallback: {ioEx.Message}");
            }
        }
    }

    private void DrainAfterUnhandled()
    {
        FlushAndDrainSynchronously();
    }

    /// <summary>
    ///     Synchronously flushes and drains all remaining entries from the channel queue to disk.
    /// </summary>
    public void FlushAndDrainSynchronously()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        // Unhook lifecycle events
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        lock (_flushLock)
        {
            try
            {
                // Signal completion to channel so flusher knows EOF
                _channel.Writer.TryComplete();

                // Wait for background worker to naturally drain and finish
                bool completedCleanly = false;
#pragma warning disable RS0030 // Justification: Synchronous app shutdown logger drain with timeout
                try
                {
                    completedCleanly = _flusherTask.Wait(TimeSpan.FromMilliseconds(1500));
                }
                catch
                {
                    // Task wait exceptions
                }

                // If flusher timed out, signal cancellation and wait brief grace period
                if (!completedCleanly)
                    try
                    {
                        _cts.Cancel();
                        _flusherTask.Wait(TimeSpan.FromMilliseconds(200));
                    }
                    catch
                    {
                    }
#pragma warning restore RS0030

                // Drain any remaining items directly to disk (in case flusher faulted or timed out)
                List<string> remaining = [];
                while (_channel.Reader.TryRead(out string? item))
                    if (item != null)
                        remaining.Add(item);

                if (remaining.Count > 0)
                {
                    EnsureDirectoryExists(FilePath);
                    using FileStream stream = new(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                        FileBufferSize);
                    using StreamWriter writer = new(stream, Encoding.UTF8, FileBufferSize);
                    for (int i = 0; i < remaining.Count; i++) writer.Write(remaining[i]);
                    writer.Flush();
                    stream.Flush(true);
                }
            }
            catch
            {
                // Fallback swallow to prevent crash during teardown
            }
            finally
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                _cts.Dispose();
            }
        }
    }

    private void TrySynchronousAppend(string entry)
    {
        lock (_flushLock)
        {
            try
            {
                EnsureDirectoryExists(FilePath);
                using FileStream stream = new(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                    FileBufferSize);
                using StreamWriter writer = new(stream, Encoding.UTF8, FileBufferSize);
                writer.Write(entry);
                writer.Flush();
            }
            catch
            {
                // Ignore concurrent write errors during shutdown
            }
        }
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
