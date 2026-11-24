// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class CdnAndWarmInstancesTests
{
    private static string TemporaryPath()
    {
        return Path.Combine(Path.GetTempPath(), $"compel-cdn-test-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void CDNOptionsValidator_ValidOptions_Succeeds()
    {
        CDNOptionsValidator validator = new();
        CDNOptions options = new()
        {
            Host = "https://cdn.kongor.net/",
            ParallelTransfers = 8,
            WindowsVariant = "was",
            LinuxVariant = "las"
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CDNOptionsValidator_LocalhostHttpHost_Succeeds()
    {
        CDNOptionsValidator validator = new();
        CDNOptions options = new()
        {
            Host = "http://localhost:55555/cdn/",
            ParallelTransfers = 16,
            WindowsVariant = "was",
            LinuxVariant = "las"
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://cdn.kongor.net/")]
    [InlineData("not-a-valid-url")]
    public void CDNOptionsValidator_InvalidHost_Fails(string? host)
    {
        CDNOptionsValidator validator = new();
        CDNOptions options = new()
        {
            Host = host!,
            ParallelTransfers = 8,
            WindowsVariant = "was",
            LinuxVariant = "las"
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(100)]
    public void CDNOptionsValidator_InvalidParallelTransfers_Fails(int transfers)
    {
        CDNOptionsValidator validator = new();
        CDNOptions options = new()
        {
            Host = "https://cdn.kongor.net/",
            ParallelTransfers = transfers,
            WindowsVariant = "was",
            LinuxVariant = "las"
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void CompelConfigurationLoader_DefaultFile_IncludesCDNAndWarmInstancesTarget()
    {
        string path = TemporaryPath();

        try
        {
            CompelConfigurationLoader.CreateDefault(path);
            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.NotNull(file.CDN);
            Assert.Equal("https://cdn.newbs.io/", file.CDN.Value);
            Assert.NotNull(file.WarmInstancesTarget);
            Assert.Equal(0, file.WarmInstancesTarget.Value);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ValidateDedicatedServerBinary_DetectsClientK2DllAndAcceptsServerK2Dll()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"compel-bin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "hon_x64.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            byte[] peBytes = new byte[0x200];
            peBytes[0] = (byte)'M';
            peBytes[1] = (byte)'Z';
            BitConverter.TryWriteBytes(peBytes.AsSpan(0x3C, 4), 0x80);
            peBytes[0x80] = (byte)'P';
            peBytes[0x81] = (byte)'E';

            // 1. Misplaced K2_CLIENT PE SizeOfImage (0x01394000) -> rejected with actionable error
            BitConverter.TryWriteBytes(peBytes.AsSpan(0x80 + 0x50, 4), 0x01394000U);
            File.WriteAllBytes(k2Path, peBytes);
            bool clientOk = COMPEL.Services.Supervision.MatchServerManagerSupervisor.ValidateDedicatedServerBinary(exePath, out string? clientErr);
            Assert.False(clientOk);
            Assert.NotNull(clientErr);
            Assert.Contains("K2_CLIENT", clientErr);

            // 2. Genuine K2_SERVER PE SizeOfImage (0x01342000) -> accepted
            BitConverter.TryWriteBytes(peBytes.AsSpan(0x80 + 0x50, 4), 0x01342000U);
            File.WriteAllBytes(k2Path, peBytes);
            bool serverOk = COMPEL.Services.Supervision.MatchServerManagerSupervisor.ValidateDedicatedServerBinary(exePath, out string? serverErr);
            Assert.True(serverOk);
            Assert.Null(serverErr);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CompelConfigurationLoader_CustomCDNAndWarmInstances_RoundTrips()
    {
        string path = TemporaryPath();

        try
        {
            string customJson = """
            {
                "CDN": {
                    "Value": "http://localhost:55555/cdn"
                },
                "WarmInstancesTarget": {
                    "Value": 3
                },
                "Instances": {
                    "Value": 6
                }
            }
            """;
            File.WriteAllText(path, customJson);

            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.NotNull(file.CDN);
            Assert.Equal("http://localhost:55555/cdn", file.CDN.Value);
            Assert.NotNull(file.WarmInstancesTarget);
            Assert.Equal(3, file.WarmInstancesTarget.Value);
            Assert.Equal(6, file.Instances.Value);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
