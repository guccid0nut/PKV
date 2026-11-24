// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Telemetry;

namespace KONGOR.MasterServer.Extensions;

public static class HttpResponsePhpExtensions
{
    /// <summary>
    ///     Writes an object serialized to PHP wire protocol directly into the HTTP response stream with zero string
    ///     allocations.
    /// </summary>
    public static async Task WritePhpResponseAsync<T>(this HttpResponse response, T? data, int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = NexusTracing.StartPhpActivity(typeof(T).Name);
        NexusMetrics.MasterServerRequestsTotal.Add(1);

        try
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=UTF-8";

            PhpSerializer.Serialize(response.BodyWriter, data);
            await response.BodyWriter.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            NexusTracing.RecordException(activity, ex);
            throw;
        }
    }
}
