// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.Services.Supabase;
using Microsoft.Extensions.Configuration;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class SupabaseCloudConfigStorageTests
{
    [Fact]
    public async Task DownloadFileAsync_AuthenticatedEndpointSucceeds_ReturnsStream()
    {
        // Arrange
        byte[] expectedBytes = [1, 2, 3, 4, 5];
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:ServiceRoleKey"] = "service-role-test-key",
            ["Supabase:AnonKey"] = "anon-test-key"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        MockHttpMessageHandler mockHandler = new(req =>
        {
            if (req.RequestUri != null && req.RequestUri.ToString()
                    .Contains("/storage/v1/object/authenticated/configs/59/cloud.zip"))
            {
                HttpResponseMessage successResponse = new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
                return successResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        HttpClient httpClient = new(mockHandler);
        SupabaseStorageClient client = new(configuration, httpClient);

        // Act
        Stream? resultStream = await client.DownloadFileAsync(SupabaseStorageClient.ConfigsBucket, "59/cloud.zip");

        // Assert
        Assert.NotNull(resultStream);
        using MemoryStream ms = new();
        await resultStream.CopyToAsync(ms);
        Assert.Equal(expectedBytes, ms.ToArray());
    }

    [Fact]
    public async Task SupabaseCloudConfigStorage_DownloadFileAsync_FallsBackToPublicEndpointWhenAuthenticatedFails()
    {
        // Arrange
        byte[] expectedBytes = [10, 20, 30];
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:ServiceRoleKey"] = "service-role-test-key"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        MockHttpMessageHandler mockHandler = new(req =>
        {
            if (req.RequestUri != null &&
                req.RequestUri.ToString().Contains("/storage/v1/object/public/configs/59/cloud.zip"))
            {
                HttpResponseMessage successResponse = new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
                return successResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        HttpClient httpClient = new(mockHandler);
        SupabaseStorageClient client = new(configuration, httpClient);

        // Act
        Stream? resultStream = await client.DownloadFileAsync(SupabaseStorageClient.ConfigsBucket, "59/cloud.zip");

        // Assert
        Assert.NotNull(resultStream);
        using MemoryStream ms = new();
        await resultStream.CopyToAsync(ms);
        Assert.Equal(expectedBytes, ms.ToArray());
    }

    [Fact]
    public async Task SupabaseCloudConfigStorage_DownloadFileAsync_ReturnsNullWhenFileNotFound()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["Supabase:Url"] = "https://example.supabase.co",
            ["Supabase:ServiceRoleKey"] = "service-role-test-key"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        MockHttpMessageHandler mockHandler = new(req => { return new HttpResponseMessage(HttpStatusCode.NotFound); });

        HttpClient httpClient = new(mockHandler);
        SupabaseStorageClient client = new(configuration, httpClient);

        // Act
        Stream? resultStream =
            await client.DownloadFileAsync(SupabaseStorageClient.ConfigsBucket, "nonexistent/cloud.zip");

        // Assert
        Assert.Null(resultStream);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }
}
