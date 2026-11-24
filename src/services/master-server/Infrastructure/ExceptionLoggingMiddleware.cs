// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

namespace KONGOR.MasterServer.Infrastructure;

public class ExceptionLoggingMiddleware(RequestDelegate next, ILogger<ExceptionLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException oce)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    logger.LogDebug("Request {Method} {Path} was canceled by the client.", context.Request.Method,
                        context.Request.Path);
                }
                else
                {
                    logger.LogDebug("Request {Method} {Path} was canceled: {Message}", context.Request.Method,
                        context.Request.Path, oce.Message);
                }
            }
        }
        catch (BadHttpRequestException badReqEx)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(badReqEx, "Bad HTTP Request on {Method} {Path}: {Message}", context.Request.Method,
                    context.Request.Path, badReqEx.Message);

            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException ||
                exception.InnerException is OperationCanceledException ||
                context.RequestAborted.IsCancellationRequested)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Request {Method} {Path} was canceled or aborted.", context.Request.Method,
                        context.Request.Path);

                return;
            }

            // Log the exception message explicitly so it appears in the Structured Logs "Message" column
            // structure: [Error] {ExceptionMessage}
            if (logger.IsEnabled(LogLevel.Error)) logger.LogError(exception, "{ExceptionMessage}", exception.Message);

            throw;
        }
    }
}
