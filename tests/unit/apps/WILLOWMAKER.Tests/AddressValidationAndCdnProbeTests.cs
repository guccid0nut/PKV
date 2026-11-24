// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Utilities;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class AddressValidationAndCdnProbeTests
{
    [Theory]
    [InlineData("localhost:55555/cdn", "http://localhost:55555/cdn/")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("[::1]:55555/cdn", "http://[::1]:55555/cdn/")]
    [InlineData("cdn.kongor.net", "https://cdn.kongor.net/")]
    [InlineData("https://custom-cdn.example.org/assets/", "https://custom-cdn.example.org/assets/")]
    [InlineData("http://custom-cdn.example.org:9000/cdn", "http://custom-cdn.example.org:9000/cdn/")]
    public void NormaliseCDNURL_ValidInputs_ReturnsExpectedUrl(string input, string expected)
    {
        // Act
        string actual = AddressValidation.NormaliseCDNURL(input);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("192.168.1.10:55555", "http://192.168.1.10:55555/")]
    [InlineData("192.168.1.10", "http://192.168.1.10/")]
    [InlineData("10.0.0.2", "http://10.0.0.2/")]
    [InlineData("172.16.5.4:8080", "http://172.16.5.4:8080/")]
    [InlineData("100.64.0.5:55555", "http://100.64.0.5:55555/")]
    [InlineData("100.127.255.1", "http://100.127.255.1/")]
    [InlineData("100.128.0.1", "https://100.128.0.1/")]
    [InlineData("169.254.10.20", "http://169.254.10.20/")]
    [InlineData("203.0.113.9:55555", "http://203.0.113.9:55555/")]
    [InlineData("203.0.113.9", "https://203.0.113.9/")]
    [InlineData("masterserver.newbs.io", "https://masterserver.newbs.io/")]
    [InlineData("https://10.0.0.2:55555", "https://10.0.0.2:55555/")]
    [InlineData("http://masterserver.newbs.io", "http://masterserver.newbs.io/")]
    [InlineData("myhost.example.com:55555", "http://myhost.example.com:55555/")]
    [InlineData("192.168.1.10:55555/path", "http://192.168.1.10:55555/path/")]
    [InlineData("[fc00::1]:8080", "http://[fc00::1]:8080/")]
    [InlineData("[fe80::1]", "http://[fe80::1]/")]
    [InlineData("[2001:db8::1]", "https://[2001:db8::1]/")]
    [InlineData("[2001:db8::1]:55555", "http://[2001:db8::1]:55555/")]
    public void NormaliseCDNURL_M2T3_SchemeDefaultingRules_ReturnsExpectedUrl(string input, string expected)
    {
        // protects: M2-T3 scheme defaulting contract for LAN, CGNAT, link-local, loopback, and explicit :55555 self-hosted endpoints.
        // fails_when: Self-hosted LAN/VPN/55555 endpoints erroneously default to HTTPS or public domains default to HTTP.
        // seam: AddressValidation.NormaliseCDNURL URL normalisation logic.
        string actual = AddressValidation.NormaliseCDNURL(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormaliseCDNURL_NullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // Act
        string actual = AddressValidation.NormaliseCDNURL(input);

        // Assert
        Assert.Equal(string.Empty, actual);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:55555")]
    [InlineData("127.0.0.1:55555/cdn")]
    [InlineData("api.kongor.net")]
    [InlineData("https://api.kongor.net")]
    [InlineData("http://localhost:55555/cdn")]
    [InlineData("cdn.kongor.net/patches")]
    public void IsValidAddress_ValidAddresses_ReturnsTrue(string address)
    {
        // Act
        bool isValid = AddressValidation.IsValidAddress(address);

        // Assert
        Assert.True(isValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://cdn.kongor.net")]
    [InlineData("https://api.kongor.net?query=injection")]
    [InlineData("cdn.kongor.net#fragment")]
    [InlineData("user:pass@cdn.kongor.net")]
    [InlineData("cdn space .net")]
    [InlineData("cdn;rm -rf")]
    public void IsValidAddress_InvalidAddresses_ReturnsFalse(string? address)
    {
        // Act
        bool isValid = AddressValidation.IsValidAddress(address);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void LauncherSettingsService_CdnAddress_RoundTripsCorrectly()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_CdnSettings_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                SelectedCDNAddress = "Custom",
                CustomCDNAddress = "http://localhost:55555/cdn"
            };

            // Act
            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            // Assert
            Assert.Equal("Custom", loaded.SelectedCDNAddress);
            Assert.Equal("http://localhost:55555/cdn", loaded.CustomCDNAddress);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ConnectivityProbe_WhenBareCdnPrefixReturns404_SucceedsOnlyWhenHostAuthorityRootResponds2xx()
    {
        // protects: Local/embedded Master Server CDN probes ('http://127.0.0.1:55555/cdn/') from emitting false-alarm HTTP 404 banners when '/cdn/' lacks directory indexing, while still failing when the host itself is down or 404.
        // fails_when: Bare '/cdn/' 404 marks CDNProbe.Failed == true despite healthy host root, or unhealthy host root erroneously passes.
        // seam: Real ConnectivityProbe.ProbeAsync HTTP pipeline with deterministic HttpMessageHandler responses.
        ConnectivityProbe healthyHostProbe = new(
            "CDN",
            _ => { },
            () => new StubRouteHandler(req =>
            {
                if (req.RequestUri?.AbsolutePath.Trim('/').Equals("cdn", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (req.RequestUri?.AbsolutePath == "/")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        await healthyHostProbe.ProbeAsync("http://127.0.0.1:55555/cdn/");
        Assert.True(healthyHostProbe.Succeeded);
        Assert.False(healthyHostProbe.Failed);
        Assert.Equal("CDN Is Online: Host Online (HTTP 200)", healthyHostProbe.StatusMessage);

        ConnectivityProbe deadHostProbe = new(
            "CDN",
            _ => { },
            () => new StubRouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await deadHostProbe.ProbeAsync("http://127.0.0.1:55555/cdn/");
        Assert.False(deadHostProbe.Succeeded);
        Assert.True(deadHostProbe.Failed);
        Assert.Equal("CDN Is Unreachable: HTTP 404 (NotFound)", deadHostProbe.StatusMessage);
    }

    private sealed class StubRouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
