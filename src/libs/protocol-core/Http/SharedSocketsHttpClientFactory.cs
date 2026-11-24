// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http;

namespace ASPIRE.Common.Http;

/// <summary>
/// Provides a shared, high-performance <see cref="SocketsHttpHandler"/> instance with pooled connection lifetime
/// to prevent socket exhaustion and DNS staleness across fallback HTTP client calls where <see cref="IHttpClientFactory"/>
/// is unavailable or in non-DI contexts.
/// </summary>
public static class SharedSocketsHttpClientFactory
{
    private static readonly SocketsHttpHandler s_sharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        EnableMultipleHttp2Connections = true
    };

    /// <summary>
    /// Gets the shared singleton <see cref="HttpMessageHandler"/> instance.
    /// </summary>
    public static HttpMessageHandler SharedHandler => s_sharedHandler;

    /// <summary>
    /// Creates a new <see cref="HttpClient"/> backed by the shared <see cref="SocketsHttpHandler"/>
    /// with <c>disposeHandler: false</c> so disposing the returned client does not close the shared connection pool.
    /// </summary>
    /// <param name="timeout">Optional request timeout.</param>
    /// <returns>A configured <see cref="HttpClient"/> instance.</returns>
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        HttpClient client = new(s_sharedHandler, disposeHandler: false);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }
        return client;
    }
}
