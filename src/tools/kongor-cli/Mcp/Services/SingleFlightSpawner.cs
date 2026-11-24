// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Single-flight spawner ensuring that concurrent requests to launch a game client
/// or dedicated server worker with the same key (e.g. "client" or "server:11235")
/// coalesce onto a single asynchronous execution task rather than spawning duplicate processes.
/// </summary>
public static class SingleFlightSpawner
{
    private static readonly ConcurrentDictionary<string, Task<object?>> s_inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Executes a spawner factory task under single-flight semantics.
    /// If an operation for <paramref name="key"/> is already in flight, all callers await
    /// that exact same task with caller-specific cancellation support.
    /// </summary>
    public static async Task<T> ExecuteSingleFlightAsync<T>(string key, Func<Task<T>> spawner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(spawner);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (s_inFlight.TryGetValue(key, out Task<object?>? existingTask))
            {
                object? result = await existingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return (T)result!;
            }

            TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (s_inFlight.TryAdd(key, tcs.Task))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    T result = await spawner().WaitAsync(cancellationToken).ConfigureAwait(false);
                    tcs.TrySetResult(result);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                finally
                {
                    s_inFlight.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a spawn operation is currently in flight for <paramref name="key"/>.
    /// </summary>
    public static bool IsInFlight(string key) => s_inFlight.ContainsKey(key);
}
