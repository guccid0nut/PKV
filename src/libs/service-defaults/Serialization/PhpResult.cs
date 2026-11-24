// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     Action result and Minimal API result that writes PHP wire protocol payloads
///     with explicit Content-Length, preventing chunked transfer encoding for the legacy game client.
/// </summary>
public class PhpResult(
    object? data,
    int statusCode = StatusCodes.Status200OK,
    string contentType = "text/plain; charset=utf-8") : IActionResult, IResult
{
    public object? Data { get; } = data;
    public object? Value => Data;
    public int StatusCode { get; } = statusCode;
    public string ContentType { get; } = contentType;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await WriteToResponseAsync(context.HttpContext);
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        await WriteToResponseAsync(httpContext);
    }

    private async Task WriteToResponseAsync(HttpContext httpContext)
    {
        HttpResponse response = httpContext.Response;
        response.StatusCode = StatusCode;
        response.ContentType = ContentType;

        if (Data is ReadOnlyMemory<byte> rom)
        {
            response.ContentLength = rom.Length;
            await response.Body.WriteAsync(rom, httpContext.RequestAborted);
            return;
        }

        if (Data is byte[] bytes)
        {
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, httpContext.RequestAborted);
            return;
        }

        using PooledArrayBufferWriter bufferWriter = new();

        if (Data is string str)
        {
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(str.Length);
            Span<byte> span = bufferWriter.GetSpan(maxByteCount);
            int written = Encoding.UTF8.GetBytes(str, span);
            bufferWriter.Advance(written);
        }
        else
        {
            PhpSerializer.Serialize(bufferWriter, Data);
        }

        response.ContentLength = bufferWriter.WrittenCount;
        await response.Body.WriteAsync(bufferWriter.WrittenMemory, httpContext.RequestAborted);
    }
}
