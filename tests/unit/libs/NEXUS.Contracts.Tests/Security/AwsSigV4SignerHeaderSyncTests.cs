// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using NEXUS.Contracts.Security;
using Xunit;

namespace NEXUS.Contracts.Tests.Security;

public sealed class AwsSigV4SignerHeaderSyncTests
{
    [Fact]
    public void Sign_WhenCacheControlPassedInAdditionalSignedHeaders_AttachesToRequestHeadersMatchingSignedHeaders()
    {
        // protects: AWS SigV4 wire header parity for request-scoped headers like Cache-Control that HttpContentHeaders silently drops.
        // fails_when: AwsSigV4Signer signs 'cache-control' in SignedHeaders without ensuring the header exists on request.Headers, causing S3 403 SignatureDoesNotMatch.
        // seam: Real AwsSigV4Signer.Sign over HttpRequestMessage + ByteArrayContent.
        using HttpRequestMessage request = new(HttpMethod.Put, "https://example.supabase.co/storage/v1/s3/match-analytics/match_1434.parquet")
        {
            Content = new ByteArrayContent("PAR1_PAYLOAD_PAR1"u8.ToArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        Dictionary<string, string> additionalHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cache-control"] = "private, max-age=0, no-store",
            ["content-type"] = "application/octet-stream"
        };

        AwsSigV4Signer.Sign(
            request,
            AwsSigV4Signer.EmptyPayloadHash,
            "us-east-1",
            "TESTACCESSKEY",
            "TESTSECRETKEY",
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            additionalHeaders);

        request.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? cacheControlValues).Should().BeTrue();
        string wireCacheControl = string.Join(",", cacheControlValues!);
        wireCacheControl.Should().Contain("private");
        wireCacheControl.Should().Contain("max-age=0");
        wireCacheControl.Should().Contain("no-store");
        request.Content.Headers.Contains("Cache-Control").Should().BeFalse();

        // Re-signing with the exact serialized wire value must produce the exact same signature (0 wire/canonical mismatch).
        using HttpRequestMessage verifyRequest = new(HttpMethod.Put, request.RequestUri)
        {
            Content = new ByteArrayContent("PAR1_PAYLOAD_PAR1"u8.ToArray())
        };
        verifyRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        verifyRequest.Headers.TryAddWithoutValidation("Cache-Control", wireCacheControl);
        AwsSigV4Signer.Sign(
            verifyRequest,
            AwsSigV4Signer.EmptyPayloadHash,
            "us-east-1",
            "TESTACCESSKEY",
            "TESTSECRETKEY",
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cache-control"] = wireCacheControl,
                ["content-type"] = "application/octet-stream"
            });

        string authHeader = string.Join(' ', request.Headers.GetValues("Authorization"));
        string verifyAuthHeader = string.Join(' ', verifyRequest.Headers.GetValues("Authorization"));
        authHeader.Should().Contain("SignedHeaders=cache-control;content-type;host;x-amz-content-sha256;x-amz-date");
        authHeader.Should().Be(verifyAuthHeader);
    }

    [Fact]
    public void Sign_WhenContentTypeAlreadyOnContentHeaders_PreservesContentHeaderWithoutCopyingToRequestHeaders()
    {
        // protects: Content-* header placement separation (HttpContentHeaders vs HttpRequestHeaders).
        // fails_when: Sign attempts to add content-type onto HttpRequestHeaders (throwing FormatException) or omits it from SignedHeaders.
        // seam: Real AwsSigV4Signer.Sign with content-type in additionalSignedHeaders.
        using HttpRequestMessage request = new(HttpMethod.Put, "https://example.supabase.co/storage/v1/s3/match-analytics/match_1435.parquet")
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };

        Dictionary<string, string> additionalHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/vnd.apache.parquet"
        };

        AwsSigV4Signer.Sign(
            request,
            AwsSigV4Signer.EmptyPayloadHash,
            "us-east-1",
            "TESTACCESSKEY",
            "TESTSECRETKEY",
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            additionalHeaders);

        request.Headers.Contains("Content-Type").Should().BeFalse();
        request.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        string authHeader = string.Join(' ', request.Headers.GetValues("Authorization"));
        authHeader.Should().Contain("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date");
    }
}
