// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NEXUS.Contracts.Security;

/// <summary>
///     Minimal, reflection-free AWS Signature Version 4 signer for S3-compatible object storage (Supabase Storage,
///     Cloudflare R2, MinIO). Shared by the COMPEL lakehouse uploader and the Control Panel "Test S3 Connection" button.
/// </summary>
public static class AwsSigV4Signer
{
    /// <summary>SHA-256 of an empty payload (used for GET/HEAD).</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    ///     Builds the request URI for <paramref name="bucket" />/<paramref name="objectKey" /> under an endpoint that may
    ///     itself carry a path prefix (e.g. <c>https://x.supabase.co/storage/v1/s3</c>). Path-style addressing.
    /// </summary>
    public static Uri BuildObjectUri(string endpoint, string bucket, string? objectKey = null)
    {
        Uri baseUri = new(endpoint.TrimEnd('/'));
        string prefix = baseUri.AbsolutePath.TrimEnd('/');
        string path = $"{prefix}/{Uri.EscapeDataString(bucket)}";
        if (!string.IsNullOrEmpty(objectKey))
            path += "/" + string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));

        return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.IsDefaultPort ? -1 : baseUri.Port, path).Uri;
    }

    /// <summary>
    ///     Signs <paramref name="request" /> in place (adds <c>x-amz-date</c>, <c>x-amz-content-sha256</c> and
    ///     <c>Authorization</c>). <paramref name="additionalSignedHeaders" /> are added to the request (content headers
    ///     go on <see cref="HttpRequestMessage.Content" />) and included in the signature.
    /// </summary>
    public static void Sign(
        HttpRequestMessage request,
        string payloadHash,
        string region,
        string accessKey,
        string secretKey,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? additionalSignedHeaders = null,
        string service = "s3")
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required for SigV4 signing.");
        string amzDate = now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        SortedDictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate
        };

        if (additionalSignedHeaders is not null)
        {
            foreach (KeyValuePair<string, string> header in additionalSignedHeaders)
            {
                string lowerKey = header.Key.ToLowerInvariant();
                string trimmedValue = header.Value.Trim();
                headers[lowerKey] = trimmedValue;

                if (lowerKey.StartsWith("content-", StringComparison.Ordinal))
                {
                    if (request.Content is not null)
                    {
                        if (!request.Content.Headers.Contains(header.Key))
                        {
                            request.Content.Headers.TryAddWithoutValidation(header.Key, trimmedValue);
                        }
                        if (request.Content.Headers.TryGetValues(header.Key, out IEnumerable<string>? wireContentValues))
                        {
                            headers[lowerKey] = string.Join(",", wireContentValues).Trim();
                        }
                    }
                }
                else
                {
                    if (!string.Equals(lowerKey, "host", StringComparison.Ordinal) &&
                        !request.Headers.Contains(header.Key))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, trimmedValue);
                    }
                    if (!string.Equals(lowerKey, "host", StringComparison.Ordinal) &&
                        request.Headers.TryGetValues(header.Key, out IEnumerable<string>? wireReqValues))
                    {
                        headers[lowerKey] = string.Join(",", wireReqValues).Trim();
                    }
                }
            }
        }

        string canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n"));
        string signedHeaders = string.Join(';', headers.Keys);
        string canonicalQuery = BuildCanonicalQuery(uri.Query);

        string canonicalRequest =
            $"{request.Method.Method}\n{uri.AbsolutePath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        string stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        byte[] signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp), region), service), "aws4_request");
        string signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="payload" />.</summary>
    public static string HashPayload(byte[] payload)
    {
        return Hex(SHA256.HashData(payload));
    }

    private static string BuildCanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;

        IEnumerable<(string Key, string Value)> pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                int equals = part.IndexOf('=');
                string key = Uri.UnescapeDataString(equals < 0 ? part : part[..equals]);
                string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(part[(equals + 1)..]);
                return (Key: Uri.EscapeDataString(key), Value: Uri.EscapeDataString(value));
            });

        return string.Join('&', pairs.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    }

    private static string Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }
}
