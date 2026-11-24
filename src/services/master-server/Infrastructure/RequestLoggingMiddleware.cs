// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Infrastructure;

public partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    private const string RedactedMarker = "[REDACTED]";
    private const string TruncatedSuffix = "... [TRUNCATED]";

    public async Task Invoke(HttpContext context)
    {
        context.Request.EnableBuffering();

        string body = string.Empty;

        try
        {
            if (context.Request.ContentLength > 0 && (context.Request.ContentType?.Contains("form", StringComparison.OrdinalIgnoreCase) == true ||
                                                      context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true))
            {
                const int maxLogLength = 4096;
                using StreamReader reader = new(context.Request.Body, Encoding.UTF8, false, leaveOpen: true);
                char[] buffer = new char[(int)Math.Min(context.Request.ContentLength.Value, maxLogLength)];
                int readCount = await reader.ReadAsync(buffer, 0, buffer.Length);
                body = new string(buffer, 0, readCount);

                if (context.Request.ContentLength > maxLogLength) body += TruncatedSuffix;

                context.Request.Body.Position = 0;
            }
        }
        catch (Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(exception, "Failed to read request body for logging.");

            body = "[Error reading body]";
        }

        string rawQuery = context.Request.QueryString.HasValue ? context.Request.QueryString.Value ?? string.Empty : string.Empty;
        LogLevel level = ResolveLogLevel(context.Request, rawQuery, body);

        if (logger.IsEnabled(level))
        {
            string redactedQuery = RedactQueryString(rawQuery);
            string redactedBody = string.IsNullOrWhiteSpace(body)
                ? "[Empty/Binary]"
                : RedactBody(body, context.Request.ContentType);

            logger.Log(level, "Incoming Request: {Method} {Path}{QueryString} Body: {Body}",
                context.Request.Method,
                context.Request.Path,
                redactedQuery,
                redactedBody);
        }

        await next(context);
    }

    internal static bool IsVerboseHeartbeatsEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("KONGOR_VERBOSE_HEARTBEATS");
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal) ||
               string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static LogLevel ResolveLogLevel(HttpRequest request, string rawQuery, string rawBody, bool? verboseHeartbeatsOverride = null)
    {
        bool verboseHeartbeats = verboseHeartbeatsOverride ?? IsVerboseHeartbeatsEnabled();
        if (verboseHeartbeats)
        {
            return LogLevel.Information;
        }

        PathString path = request.Path;
        if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/alive", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        string? functionName = ExtractParameter(request.Query, rawQuery, rawBody, "f");
        if (string.Equals(functionName, "set_online", StringComparison.OrdinalIgnoreCase))
        {
            string? newParam = ExtractParameter(request.Query, rawQuery, rawBody, "new");
            if (newParam is null)
            {
                return LogLevel.Debug;
            }
        }

        return LogLevel.Information;
    }

    internal static string RedactQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        bool hasLeadingQuestionMark = queryString.StartsWith('?');
        string raw = hasLeadingQuestionMark ? queryString[1..] : queryString;
        string redacted = RedactFormEncoded(raw);
        return hasLeadingQuestionMark ? "?" + redacted : redacted;
    }

    internal static string RedactBody(string? body, string? contentType)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        bool hasTruncatedSuffix = body.EndsWith(TruncatedSuffix, StringComparison.Ordinal);
        string core = hasTruncatedSuffix ? body[..^TruncatedSuffix.Length] : body;

        bool looksJson = (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) ||
                         core.TrimStart().StartsWith('{') ||
                         core.TrimStart().StartsWith('[');

        string redactedCore = looksJson
            ? RedactJson(core)
            : RedactFormEncoded(core);

        return hasTruncatedSuffix ? redactedCore + TruncatedSuffix : redactedCore;
    }

    private static string RedactFormEncoded(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        string[] pairs = raw.Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            string pair = pairs[i];
            int eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            string rawKey = pair[..eqIdx];
            string rawValue = pair[(eqIdx + 1)..];
            string decodedKey = SafeUrlDecode(rawKey);
            if (ShouldFullRedact(decodedKey))
            {
                pairs[i] = $"{rawKey}={RedactedMarker}";
            }
            else if (ShouldMaskToken(decodedKey))
            {
                string decodedVal = SafeUrlDecode(rawValue);
                pairs[i] = $"{rawKey}={SecurityMasking.MaskToken(decodedVal)}";
            }
        }

        return string.Join('&', pairs);
    }

    private static string RedactJson(string json)
    {
        return JsonStringPropertyRegex().Replace(json, static match =>
        {
            string key = match.Groups["key"].Value;
            string value = match.Groups["val"].Value;
            if (ShouldFullRedact(key))
            {
                return $"\"{key}\":\"{RedactedMarker}\"";
            }

            if (ShouldMaskToken(key))
            {
                return $"\"{key}\":\"{SecurityMasking.MaskToken(value)}\"";
            }

            return match.Value;
        });
    }

    internal static string FormatSensitiveValue(string key, string value)
    {
        if (ShouldFullRedact(key))
            return RedactedMarker;
        if (ShouldMaskToken(key))
            return SecurityMasking.MaskToken(value);
        return value;
    }

    private static bool ShouldFullRedact(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string trimmed = key.Trim();
        return trimmed.Equals("password", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("pass", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("old_password", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("new_password", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("key", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_password", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_secret", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldMaskToken(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string trimmed = key.Trim();
        return trimmed.Equals("session", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("token", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("auth", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_session", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_cookie", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_token", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeUrlDecode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch
        {
            return value;
        }
    }

    private static string? ExtractParameter(IQueryCollection query, string rawQuery, string rawBody, string targetKey)
    {
        if (query.TryGetValue(targetKey, out StringValues queryValues) && queryValues.Count > 0)
        {
            return queryValues[0];
        }

        string? fromQuery = FindFormParameter(rawQuery.TrimStart('?'), targetKey);
        if (fromQuery is not null)
        {
            return fromQuery;
        }

        string cleanBody = rawBody.EndsWith(TruncatedSuffix, StringComparison.Ordinal)
            ? rawBody[..^TruncatedSuffix.Length]
            : rawBody;
        return FindFormParameter(cleanBody, targetKey);
    }

    private static string? FindFormParameter(string encoded, string targetKey)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        foreach (string pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            string key = SafeUrlDecode(pair[..eqIdx]);
            if (string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                return SafeUrlDecode(pair[(eqIdx + 1)..]);
            }
        }

        return null;
    }

    [GeneratedRegex("\"(?<key>[^\"\\\\]+)\"\\s*:\\s*\"(?<val>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex JsonStringPropertyRegex();
}
