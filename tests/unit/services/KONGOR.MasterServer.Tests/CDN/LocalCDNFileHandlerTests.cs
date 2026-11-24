// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text.Json;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.CDN;
using KONGOR.MasterServer.Models.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NEXUS.Contracts.Cdn;
using Xunit;

namespace KONGOR.MasterServer.Tests.CDN;

public sealed class LocalCDNFileHandlerTests
{
    [Theory]
    [InlineData(null, "some/path")]
    [InlineData("", "some/path")]
    [InlineData("   ", "some/path")]
    [InlineData("/cdn", null)]
    [InlineData("/cdn", "")]
    [InlineData("/cdn", "   ")]
    public void Use_WithMissingConfiguration_DoesNotThrow(string? serveUrl, string? localDir)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        WebApplication app = builder.Build();
        OperationalConfigurationCDN config = new()
        {
            Host = "http://localhost:55555",
            PrimaryPatchURL = "http://localhost:55555/patch",
            SecondaryPatchURL = "http://localhost:55555/patch",
            ServeFromLocalDirectoryURL = serveUrl,
            LocalDirectory = localDir
        };

        // Act & Assert
        Action act = () => LocalCDNFileHandler.Use(app, config);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("/cdn?query=1")]
    [InlineData("/cdn#frag")]
    [InlineData("http://invalid/cdn")]
    [InlineData("/cdn:port")]
    public void Use_WithInvalidUrlCharacters_ThrowsInvalidOperationException(string invalidUrl)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        WebApplication app = builder.Build();
        OperationalConfigurationCDN config = new()
        {
            Host = "http://localhost:55555",
            PrimaryPatchURL = "http://localhost:55555/patch",
            SecondaryPatchURL = "http://localhost:55555/patch",
            ServeFromLocalDirectoryURL = invalidUrl,
            LocalDirectory = Path.Combine(Path.GetTempPath(), "kongor_cdn_invalid_url_test")
        };

        // Act & Assert
        Action act = () => LocalCDNFileHandler.Use(app, config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{invalidUrl}*");
    }

    [Fact]
    public void Use_WithValidConfiguration_MapsSuccessfully()
    {
        // Arrange
        string tempCdnDir = Path.Combine(Path.GetTempPath(), "kongor_cdn_map_test_" + Guid.NewGuid().ToString("N"));
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddRateLimiter(_ => { });
        WebApplication app = builder.Build();
        OperationalConfigurationCDN config = new()
        {
            Host = "http://localhost:55555",
            PrimaryPatchURL = "http://localhost:55555/patch",
            SecondaryPatchURL = "http://localhost:55555/patch",
            ServeFromLocalDirectoryURL = "/cdn",
            LocalDirectory = tempCdnDir
        };

        try
        {
            // Act & Assert
            Action act = () => LocalCDNFileHandler.Use(app, config);
            act.Should().NotThrow();
        }
        finally
        {
            if (Directory.Exists(tempCdnDir))
            {
                Directory.Delete(tempCdnDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Use_EmbeddedAndStaticDirectoryModes_Return200OnBareCdnProbeAndServeConfiguredFiles()
    {
        // protects: WILLOWMAKER '/cdn' and '/cdn/' HEAD/GET readiness probes against HTTP 404 false alarms in both unconfigured (embedded) and static-directory Master Server modes.
        // fails_when: HEAD/GET '/cdn' or '/cdn/' returns 404 NotFound, or static file serving under '/cdn/<file>' breaks.
        // seam: Real Kestrel loopback HTTP pipeline executing LocalCDNFileHandler middleware.
        string tempCdnDir = Path.Combine(Path.GetTempPath(), "kongor_local_cdn_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCdnDir);
        await File.WriteAllTextAsync(Path.Combine(tempCdnDir, "manifest.txt"), "kongor-cdn-ok");

        try
        {
            // 1. Embedded mode (ServeFromLocalDirectoryURL is null/unconfigured, matching default local appsettings.json)
            WebApplicationBuilder embeddedBuilder = WebApplication.CreateBuilder();
            embeddedBuilder.Configuration.Sources.Clear();
            embeddedBuilder.Configuration.AddInMemoryCollection();
            embeddedBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using (WebApplication embeddedApp = embeddedBuilder.Build())
            {
                LocalCDNFileHandler.Use(embeddedApp, new OperationalConfigurationCDN
                {
                    Host = "http://127.0.0.1:55555",
                    PrimaryPatchURL = "http://127.0.0.1:55555/patch",
                    SecondaryPatchURL = "http://127.0.0.1:55555/patch",
                    ServeFromLocalDirectoryURL = null,
                    LocalDirectory = null
                });
                await embeddedApp.StartAsync();
                string baseUrl = embeddedApp.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

                using HttpClient client = new();
                using HttpRequestMessage headNoSlash = new(HttpMethod.Head, $"{baseUrl}/cdn");
                using HttpResponseMessage headNoSlashResp = await client.SendAsync(headNoSlash);
                headNoSlashResp.StatusCode.Should().Be(HttpStatusCode.OK);

                using HttpRequestMessage headWithSlash = new(HttpMethod.Head, $"{baseUrl}/cdn/");
                using HttpResponseMessage headWithSlashResp = await client.SendAsync(headWithSlash);
                headWithSlashResp.StatusCode.Should().Be(HttpStatusCode.OK);

                await embeddedApp.StopAsync();
            }

            // 2. Static directory mode (ServeFromLocalDirectoryURL = "/cdn")
            WebApplicationBuilder staticBuilder = WebApplication.CreateBuilder();
            staticBuilder.Configuration.Sources.Clear();
            staticBuilder.Configuration.AddInMemoryCollection();
            staticBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            staticBuilder.Services.AddRateLimiter(_ => { });
            await using (WebApplication staticApp = staticBuilder.Build())
            {
                LocalCDNFileHandler.Use(staticApp, new OperationalConfigurationCDN
                {
                    Host = "http://127.0.0.1:55555",
                    PrimaryPatchURL = "http://127.0.0.1:55555/patch",
                    SecondaryPatchURL = "http://127.0.0.1:55555/patch",
                    ServeFromLocalDirectoryURL = "/cdn",
                    LocalDirectory = tempCdnDir
                });
                await staticApp.StartAsync();
                string baseUrl = staticApp.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

                using HttpClient client = new();
                using HttpRequestMessage headSlash = new(HttpMethod.Head, $"{baseUrl}/cdn/");
                using HttpResponseMessage headSlashResp = await client.SendAsync(headSlash);
                headSlashResp.StatusCode.Should().Be(HttpStatusCode.OK);

                using HttpResponseMessage fileResp = await client.GetAsync($"{baseUrl}/cdn/manifest.txt");
                fileResp.StatusCode.Should().Be(HttpStatusCode.OK);
                (await fileResp.Content.ReadAsStringAsync()).Should().Be("kongor-cdn-ok");

                using HttpResponseMessage missingResp = await client.GetAsync($"{baseUrl}/cdn/does_not_exist.bin");
                missingResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

                await staticApp.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempCdnDir))
            {
                Directory.Delete(tempCdnDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Use_AutoSeedsManifestJson_Serves200OnWacAndCdnManifest_AndPreservesExistingFile()
    {
        // protects: auto-seeding of CDN_Content/manifest.json and wac/manifest.json so GET /wac/manifest.json and /cdn/wac/manifest.json return 200 OK instead of 404 spam
        // fails_when: LocalCDNFileHandler fails to seed manifest.json, overwrites an existing manifest, or returns 404 on /wac/manifest.json
        // seam: Real Kestrel loopback HTTP pipeline executing LocalCDNFileHandler.Use + NEXUS.Contracts.Cdn.ManifestJSONContext deserialization
        string tempCdnDir = Path.Combine(Path.GetTempPath(), "kongor_local_cdn_seed_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddRateLimiter(_ => { });

            await using (WebApplication app = builder.Build())
            {
                LocalCDNFileHandler.Use(app, new OperationalConfigurationCDN
                {
                    Host = "http://127.0.0.1:55555",
                    PrimaryPatchURL = "http://127.0.0.1:55555/patch",
                    SecondaryPatchURL = "http://127.0.0.1:55555/patch",
                    ServeFromLocalDirectoryURL = "/cdn",
                    LocalDirectory = tempCdnDir
                });

                string rootManifestPath = Path.Combine(tempCdnDir, "manifest.json");
                string wacManifestPath = Path.Combine(tempCdnDir, "wac", "manifest.json");
                File.Exists(rootManifestPath).Should().BeTrue();
                File.Exists(wacManifestPath).Should().BeTrue();

                await app.StartAsync();
                string baseUrl = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

                using HttpClient client = new();
                foreach (string route in new[] { "/wac/manifest.json", "/cdn/wac/manifest.json", "/cdn/manifest.json" })
                {
                    using HttpResponseMessage resp = await client.GetAsync($"{baseUrl}{route}");
                    resp.StatusCode.Should().Be(HttpStatusCode.OK, $"route {route} must serve auto-seeded manifest.json");
                    resp.Headers.GetValues(Manifest.PkvHeaderName).Should().Contain(Manifest.PkvProtocolV5);

                    string body = await resp.Content.ReadAsStringAsync();
                    Manifest? parsed = JsonSerializer.Deserialize(body, ManifestJSONContext.Default.Manifest);
                    parsed.Should().NotBeNull();
                    parsed!.Version.Should().Be("5.0.2");
                    parsed.Protocol.Should().Be("pkv-v5");
                    parsed.Files.Should().BeEmpty();
                }

                await app.StopAsync();
            }

            // Re-running Use() must preserve an existing custom manifest without overwriting it
            string customWacJson = """{"version":"5.9.9","protocol":"pkv-v5","sequence":42,"hashAlgorithm":"SHA-256","mode":"overlay","objectLayout":"sha256","excludeFromSource":["manifest.json"],"excludeFromTarget":[],"files":{}}""";
            await File.WriteAllTextAsync(Path.Combine(tempCdnDir, "wac", "manifest.json"), customWacJson);

            WebApplicationBuilder secondBuilder = WebApplication.CreateBuilder();
            secondBuilder.Configuration.Sources.Clear();
            secondBuilder.Configuration.AddInMemoryCollection();
            secondBuilder.Services.AddRateLimiter(_ => { });
            await using WebApplication secondApp = secondBuilder.Build();
            LocalCDNFileHandler.Use(secondApp, new OperationalConfigurationCDN
            {
                Host = "http://127.0.0.1:55555",
                PrimaryPatchURL = "http://127.0.0.1:55555/patch",
                SecondaryPatchURL = "http://127.0.0.1:55555/patch",
                ServeFromLocalDirectoryURL = "/cdn",
                LocalDirectory = tempCdnDir
            });

            string preserved = await File.ReadAllTextAsync(Path.Combine(tempCdnDir, "wac", "manifest.json"));
            preserved.Should().Be(customWacJson);
        }
        finally
        {
            if (Directory.Exists(tempCdnDir))
            {
                Directory.Delete(tempCdnDir, recursive: true);
            }
        }
    }
}
