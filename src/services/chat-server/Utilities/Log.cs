// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TRANSMUTANSTEIN.ChatServer.Utilities;

[SuppressMessage("Logging", "CA2254:Template should be a static expression",
    Justification = "Static wrapper around ILogger calls dynamic log templates")]
[SuppressMessage("Performance", "CA1848:Use the LoggerMessageDelegates",
    Justification = "Static wrapper around ILogger calls dynamic log templates")]
public class Log
{
    private static ILogger? MaybeLogger { get; set; }

    public static void Initialise(ILogger logger)
    {
        MaybeLogger = logger;
    }

    private static ILogger Get()
    {
        return MaybeLogger ?? throw new InvalidOperationException("Logger Has Not Been Initialised");
    }

    private static string FormatLogMessage(string message, object?[]? args)
    {
        if (args is null || args.Length == 0) return message;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, message, args);
        }
        catch
        {
            return $"{message} [{string.Join(", ", args)}]";
        }
    }

    private static void LogInternal(LogLevel logLevel, Exception? exception, string? message, params object?[] args)
    {
        ILogger logger = Get();

        if (logger.IsEnabled(logLevel) && message is not null)
            logger.Log(logLevel, exception, "{Message}", FormatLogMessage(message, args));
    }

    public static void Debug(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Debug, exception, message, args);
    }

    public static void Debug(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Debug, null, message, args);
    }

    public static void Trace(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Trace, exception, message, args);
    }

    public static void Trace(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Trace, null, message, args);
    }

    public static void Information(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Information, exception, message, args);
    }

    public static void Information(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Information, null, message, args);
    }

    public static void Warning(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Warning, exception, message, args);
    }

    public static void Warning(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Warning, null, message, args);
    }

    public static void Error(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Error, exception, message, args);
    }

    public static void Error(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Error, null, message, args);
    }

    public static void Critical(Exception? exception, string? message, params object?[] args)
    {
        LogInternal(LogLevel.Critical, exception, message, args);
    }

    public static void Critical(string? message, params object?[] args)
    {
        LogInternal(LogLevel.Critical, null, message, args);
    }
}
