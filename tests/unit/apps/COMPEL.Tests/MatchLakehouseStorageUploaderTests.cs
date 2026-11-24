// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchLakehouseStorageUploaderTests.cs
// System: COMPEL Unit Tests - Match Parquet Lakehouse Storage Uploader
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using COMPEL.Services.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class MatchLakehouseStorageUploaderTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handlerFunc = handlerFunc;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handlerFunc(request));
        }
    }

    // Fake, non-secret test values: the uploader no longer ships with baked-in credentials.
    private static readonly LakehouseStorageSettings TestSettings = new(
        "https://test-project.supabase.co/storage/v1/s3", "us-east-1", "match-analytics", "TESTACCESSKEY", "TESTSECRETKEY");

    [Fact]
    public void EnqueueUpload_AddsJobToChannel_ReturnsTrue()
    {
        // Arrange
        MatchLakehouseStorageUploader uploader = new(null, NullLogger<MatchLakehouseStorageUploader>.Instance, TestSettings);

        // Act
        bool result = uploader.EnqueueUpload(1001, "data/analytics/replays/match_1001.parquet");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task UploadParquetFileAsync_ValidFile_SignsWithAwsSigV4Headers()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler handler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        HttpClient httpClient = new(handler);
        MatchLakehouseStorageUploader uploader = new(httpClient, NullLogger<MatchLakehouseStorageUploader>.Instance, TestSettings);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "PAR1_DUMMY_PAYLOAD_PAR1"u8.ToArray());

            // Act
            bool success = await uploader.UploadParquetFileAsync(998877, tempFile);

            // Assert
            Assert.True(success);
            Assert.NotNull(capturedRequest);
            Assert.Equal(HttpMethod.Put, capturedRequest.Method);
            Assert.Contains("/match-analytics/match_998877.parquet", capturedRequest.RequestUri?.AbsolutePath);

            Assert.True(capturedRequest.Headers.Contains("Authorization"));
            string authHeader = capturedRequest.Headers.GetValues("Authorization").First();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=", authHeader);
            Assert.Contains("/s3/aws4_request", authHeader);
            Assert.Contains("Signature=", authHeader);

            Assert.True(capturedRequest.Headers.Contains("x-amz-date"));
            Assert.True(capturedRequest.Headers.Contains("x-amz-content-sha256"));
            Assert.Equal("application/vnd.apache.parquet", capturedRequest.Content?.Headers.ContentType?.MediaType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UploadWithRetryAsync_TransientFailure_RetriesAndSucceeds()
    {
        // Arrange
        int attemptCount = 0;
        MockHttpMessageHandler handler = new(_ =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        HttpClient httpClient = new(handler);
        MatchLakehouseStorageUploader uploader = new(httpClient, NullLogger<MatchLakehouseStorageUploader>.Instance, TestSettings);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "PAR1_RETRY_TEST"u8.ToArray());

            // Act
            bool success = await uploader.UploadWithRetryAsync(554433, tempFile);

            // Assert
            Assert.True(success);
            Assert.Equal(2, attemptCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PruneLocalFifoCache_DeletesOldestFiles_RetainingMaxN()
    {
        // Arrange
        MatchLakehouseStorageUploader uploader = new(null, NullLogger<MatchLakehouseStorageUploader>.Instance, TestSettings);
        string tempDir = Path.Combine(Path.GetTempPath(), "FifoTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create 13 temporary match files with spaced timestamps
            List<string> createdFiles = [];
            for (int i = 1; i <= 13; i++)
            {
                string filePath = Path.Combine(tempDir, $"match_{i}.parquet");
                File.WriteAllText(filePath, $"dummy content {i}");
                File.SetLastWriteTimeUtc(filePath, TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(i));
                createdFiles.Add(filePath);
            }

            Assert.Equal(13, Directory.GetFiles(tempDir, "match_*.parquet").Length);

            // Act
            uploader.PruneLocalFifoCache(createdFiles.Last());

            // Assert - Should retain at most 10 files (the newest ones: 4 to 13)
            string[] remainingFiles = Directory.GetFiles(tempDir, "match_*.parquet");
            Assert.Equal(10, remainingFiles.Length);

            // Files 1, 2, and 3 should have been pruned
            Assert.False(File.Exists(createdFiles[0]));
            Assert.False(File.Exists(createdFiles[1]));
            Assert.False(File.Exists(createdFiles[2]));

            // File 13 should still exist
            Assert.True(File.Exists(createdFiles[12]));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UploadParquetFileAsync_PreservesEndpointPathPrefix()
    {
        // Regression: new Uri(base, "/bucket/key") used to drop "/storage/v1/s3" from the endpoint.
        HttpRequestMessage? capturedRequest = null;
        MockHttpMessageHandler handler = new(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        MatchLakehouseStorageUploader uploader = new(new HttpClient(handler),
            NullLogger<MatchLakehouseStorageUploader>.Instance, TestSettings);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "PAR1"u8.ToArray());
            Assert.True(await uploader.UploadParquetFileAsync(42, tempFile));
            Assert.NotNull(capturedRequest);
            Assert.Equal("/storage/v1/s3/match-analytics/match_42.parquet", capturedRequest.RequestUri?.AbsolutePath);
            Assert.Equal("test-project.supabase.co", capturedRequest.RequestUri?.Host);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UploadParquetFileAsync_Unconfigured_ReturnsFalseWithoutSendingRequest()
    {
        int requestCount = 0;
        MockHttpMessageHandler handler = new(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        MatchLakehouseStorageUploader uploader = new(new HttpClient(handler),
            NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "PAR1"u8.ToArray());

            Assert.False(uploader.IsConfigured);
            Assert.False(await uploader.UploadParquetFileAsync(7, tempFile));
            Assert.False(await uploader.UploadWithRetryAsync(7, tempFile));
            Assert.Equal(0, requestCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void LakehouseStorageSettings_Resolve_PrefersCanonicalVaultKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:S3Endpoint"] = "https://canonical.example/storage/v1/s3",
                ["SUPABASE_S3_ENDPOINT"] = "https://legacy.example/storage/v1/s3",
                ["Supabase:S3Region"] = "us-east-2",
                ["Supabase:LakehouseBucket"] = "custom-bucket",
                ["Supabase:S3AccessKey"] = "AK-CANONICAL",
                ["SUPABASE_S3_ACCESS_KEY"] = "AK-LEGACY",
                ["Supabase:S3SecretKey"] = "SK-CANONICAL"
            })
            .Build();

        LakehouseStorageSettings settings = LakehouseStorageSettings.Resolve(configuration);

        Assert.True(settings.IsConfigured);
        Assert.Equal("https://canonical.example/storage/v1/s3", settings.Endpoint);
        Assert.Equal("us-east-2", settings.Region);
        Assert.Equal("custom-bucket", settings.Bucket);
        Assert.Equal("AK-CANONICAL", settings.AccessKey);
        Assert.Equal("SK-CANONICAL", settings.SecretKey);
        Assert.DoesNotContain("SK-CANONICAL", settings.ToString());
    }

    [Fact]
    public void LakehouseStorageSettings_Resolve_DerivesEndpointFromProjectUrl()
    {
        // Skip on machines that set a legacy endpoint env var (it would legitimately take precedence).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUPABASE_S3_ENDPOINT")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONGOR_LAKEHOUSE_S3_ENDPOINT")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUPABASE_STORAGE_ENDPOINT")))
            return;

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://abc123.supabase.co/",
                ["Supabase:S3AccessKey"] = "AK",
                ["Supabase:S3SecretKey"] = "SK"
            })
            .Build();

        LakehouseStorageSettings settings = LakehouseStorageSettings.Resolve(configuration);

        Assert.Equal("https://abc123.supabase.co/storage/v1/s3", settings.Endpoint);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void LakehouseStorageSettings_MissingSettings_ListsNamesNotValues()
    {
        LakehouseStorageSettings settings = new("https://x.supabase.co/storage/v1/s3", "us-east-1", "b", "", "");

        IReadOnlyList<string> missing = settings.MissingSettings();

        Assert.False(settings.IsConfigured);
        Assert.Contains("Supabase:S3AccessKey", missing);
        Assert.Contains("Supabase:S3SecretKey", missing);
    }
}
