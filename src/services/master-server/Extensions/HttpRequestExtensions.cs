// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Primitives;

namespace KONGOR.MasterServer.Extensions;

public static class HttpRequestExtensions
{
    public static string? GetCookie(this HttpRequest request)
    {
        string? cookie = null;
        bool found = false;

        if (request.QueryString.HasValue &&
            SpanFormUrlEncodedParser.TryExtractParameter(request.QueryString.Value.AsSpan(), "cookie",
                out ReadOnlySpan<char> queryCookieSpan))
        {
            cookie = queryCookieSpan.ToString();
            found = true;
        }

        if (!found)
            try
            {
                if (request.HasFormContentType)
                {
                    KeyValuePair<string, StringValues> match =
                        request.Form.FirstOrDefault(k =>
                            string.Equals(k.Key, "cookie", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(match.Key))
                    {
                        cookie = match.Value.FirstOrDefault();
                        found = true;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore form content type or stream reading issues and try query string fallback
            }

        if (!found && request.Query.Count > 0)
        {
            KeyValuePair<string, StringValues> match =
                request.Query.FirstOrDefault(k => string.Equals(k.Key, "cookie", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                cookie = match.Value.FirstOrDefault();
                found = true;
            }
        }

        return found ? (cookie ?? string.Empty).Trim('"', '\'') : null;
    }

    public static string? GetFormOrQueryValue(this HttpRequest request, string key)
    {
        if (request.HasFormContentType &&
            request.Form.TryGetValue(key, out StringValues formValues) &&
            formValues.Count > 0)
        {
            return formValues.FirstOrDefault();
        }

        if (request.Query.TryGetValue(key, out StringValues queryValues) &&
            queryValues.Count > 0)
        {
            return queryValues.FirstOrDefault();
        }

        return null;
    }

    public static string? GetQueryOrFormValue(this HttpRequest request, string key)
    {
        if (request.Query.TryGetValue(key, out StringValues queryValues) &&
            queryValues.Count > 0)
        {
            return queryValues.FirstOrDefault();
        }

        if (request.HasFormContentType &&
            request.Form.TryGetValue(key, out StringValues formValues) &&
            formValues.Count > 0)
        {
            return formValues.FirstOrDefault();
        }

        return null;
    }

    public static IFormFile? TryGetFormFile(this HttpRequest request, string key)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        return request.Form.Files[key];
    }
}
