// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.KONGOR.MasterServer.Services;

public sealed class SupabaseStorageProviderTests
{
    [Fact]
    public async Task UploadReplayAsync_StreamsToSupabaseRestApi_WithUpsertHeader()
    {
        // Arrange
        HttpResponseMessage mockResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Key\":\"replays/M100.honreplay\"}", Encoding.UTF8, "application/json")
        };
        MockHttpMessageHandler handler = new(mockResponse);
        HttpClient mockHttpClient = new(handler);

        ReplayStorageSettings settings = new()
        {
            Provider = "Supabase",
            Supabase = new SupabaseStorageSettings
            {
                ProjectUrl = "https://customproject.supabase.co",
                BucketName = "replays",
                ServiceRoleKey = "mock_service_role_key_999"
            }
        };
        IOptions<ReplayStorageSettings> options = Options.Create(settings);
        IConfiguration config = new ConfigurationBuilder().Build();
        SupabaseStorageProvider provider = new(options, config, NullLogger<SupabaseStorageProvider>.Instance,
            mockHttpClient);

        byte[] replayBytes = [.. "HON_REPLAY_DUMMY_BINARY_DATA"u8];
        using MemoryStream stream = new(replayBytes);

        // Act
        string resultUrl = await provider.UploadReplayAsync("100", stream, "M100.honreplay");

        // Assert
        Assert.Equal("https://customproject.supabase.co/storage/v1/object/public/replays/M100.honreplay", resultUrl);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://customproject.supabase.co/storage/v1/object/replays/M100.honreplay",
            handler.LastRequest.RequestUri?.ToString());
        Assert.True(handler.LastRequest.Headers.Contains("x-upsert"));
        Assert.Equal("true", handler.LastRequest.Headers.GetValues("x-upsert").FirstOrDefault());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("mock_service_role_key_999", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task UploadReplayAsync_FallsBackToMasterServerAddress_WhenSupabaseUrlMissing()
    {
        // Arrange
        ReplayStorageSettings settings = new()
        {
            Provider = "Supabase",
            Supabase = new SupabaseStorageSettings { ProjectUrl = "", BucketName = "replays" }
        };
        IOptions<ReplayStorageSettings> options = Options.Create(settings);
        Dictionary<string, string?> inMemory = new() { ["MasterServer:Address"] = "http://localhost:55555" };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        HttpClient httpClient = new();
        SupabaseStorageProvider provider = new(options, config, NullLogger<SupabaseStorageProvider>.Instance,
            httpClient);

        byte[] replayBytes = [.. "DATA"u8];
        using MemoryStream stream = new(replayBytes);

        // Act
        string resultUrl = await provider.UploadReplayAsync("101", stream, "M101.honreplay");

        // Assert
        Assert.Equal("http://localhost:55555/replays/M101.honreplay", resultUrl);
    }

    [Fact]
    public async Task UploadReplayAsync_ReturnsPublicCdnUrl_EvenOnUploadWarning()
    {
        // Arrange
        HttpResponseMessage mockResponse = new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"Duplicate\",\"message\":\"The resource already exists\"}",
                Encoding.UTF8, "application/json")
        };
        MockHttpMessageHandler handler = new(mockResponse);
        HttpClient mockHttpClient = new(handler);

        ReplayStorageSettings settings = new()
        {
            Provider = "Supabase",
            Supabase = new SupabaseStorageSettings
            {
                ProjectUrl = "https://customproject.supabase.co",
                BucketName = "replays",
                ServiceRoleKey = "service_key"
            }
        };
        IOptions<ReplayStorageSettings> options = Options.Create(settings);
        IConfiguration config = new ConfigurationBuilder().Build();
        SupabaseStorageProvider provider = new(options, config, NullLogger<SupabaseStorageProvider>.Instance,
            mockHttpClient);

        byte[] replayBytes = [.. "DATA"u8];
        using MemoryStream stream = new(replayBytes);

        // Act
        string resultUrl = await provider.UploadReplayAsync("102", stream, "M102.honreplay");

        // Assert
        Assert.Equal("https://customproject.supabase.co/storage/v1/object/public/replays/M102.honreplay", resultUrl);
    }

    private sealed class MockHttpMessageHandler(HttpResponseMessage responseToReturn) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseToReturn);
        }
    }
}
