// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using NEXUS.Contracts.Security;
using Xunit;

namespace KONGOR.MasterServer.Tests.Security;

public sealed class ReplayAndCallbackSecurityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _uploadDir;

    public ReplayAndCallbackSecurityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "KongorReplaySecTests_" + Guid.NewGuid().ToString("N"));
        _uploadDir = Path.Combine(_tempRoot, "Resources", "Replays");
        Directory.CreateDirectory(_uploadDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup races in parallel test execution
        }
    }

    private LocalDiskStorageProvider CreateProvider()
    {
        Mock<IWebHostEnvironment> mockEnv = new();
        mockEnv.Setup(e => e.ContentRootPath).Returns(_tempRoot);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MASTER_SERVER_ADDRESS"] = "http://localhost:55555"
            })
            .Build();

        IOptions<ReplayStorageSettings> opts = Options.Create(new ReplayStorageSettings
        {
            LocalDisk = new LocalDiskSettings
            {
                UploadDirectory = _uploadDir
            }
        });

        return new LocalDiskStorageProvider(mockEnv.Object, config, opts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../evil.dll")]
    [InlineData("..\\..\\evil.honreplay")]
    [InlineData("nested/M100.honreplay")]
    [InlineData("nested\\M100.honreplay")]
    [InlineData("..")]
    [InlineData("M100.dll")]
    [InlineData("M100.exe")]
    [InlineData("M100.ps1")]
    public async Task LocalDiskStorageProvider_WithTraversalOrDisallowedExtension_RejectsBeforeDiskWrite(
        string maliciousFileName)
    {
        // protects: MasterServer filesystem integrity against arbitrary file write via replay upload
        // fails_when: LocalDiskStorageProvider uses raw Path.Combine(uploadDir, fileName) without basename/root guards
        // seam: LocalDiskStorageProvider.UploadReplayAsync against real temporary directory
        LocalDiskStorageProvider provider = CreateProvider();
        await using MemoryStream payload = new(Encoding.UTF8.GetBytes("malicious-bytes"));

        Func<Task> act = async () => await provider.UploadReplayAsync("100", payload, maliciousFileName);

        await act.Should().ThrowAsync<ArgumentException>();
        Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData("M1001.honreplay")]
    [InlineData("M1002.zip")]
    public async Task LocalDiskStorageProvider_WithValidBasenameReplay_WritesStrictlyInsideUploadDir(
        string validFileName)
    {
        LocalDiskStorageProvider provider = CreateProvider();
        byte[] content = Encoding.UTF8.GetBytes("valid-honreplay-stream");
        await using MemoryStream payload = new(content);

        string url = await provider.UploadReplayAsync("1001", payload, validFileName);

        url.Should().Be($"http://localhost:55555/replays/{validFileName}");
        string expectedPath = Path.Combine(_uploadDir, validFileName);
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void IsAuthorizedRenderCallback_EnforcesSharedKeyAndBlocksDefaultKeyInProduction()
    {
        // protects: /api/replays/render-callback against unauthenticated MatchStatistics.VideoUrl tampering
        // fails_when: Callback endpoint accepts missing/wrong X-Internal-Service-Key or default dev key in Production
        // seam: KONGOR.IsAuthorizedRenderCallback
        const string prodKey = "prod-internal-service-secret-2026-abcdef0123456789";

        KONGOR.IsAuthorizedRenderCallback(null, prodKey, isDevelopment: false).Should().BeFalse();
        KONGOR.IsAuthorizedRenderCallback("wrong-key", prodKey, isDevelopment: false).Should().BeFalse();
        KONGOR.IsAuthorizedRenderCallback(prodKey, prodKey, isDevelopment: false).Should().BeTrue();

        // Missing or default key in Production must fail-closed even if caller supplies DefaultInternalServiceKey
        KONGOR.IsAuthorizedRenderCallback(
                SecurityConstants.DefaultInternalServiceKey,
                configuredKey: null,
                isDevelopment: false)
            .Should().BeFalse();
        KONGOR.IsAuthorizedRenderCallback(
                SecurityConstants.DefaultInternalServiceKey,
                configuredKey: SecurityConstants.DefaultInternalServiceKey,
                isDevelopment: false)
            .Should().BeFalse();

        // Development mode allows DefaultInternalServiceKey when unconfigured for zero-friction local playtesting
        KONGOR.IsAuthorizedRenderCallback(
                SecurityConstants.DefaultInternalServiceKey,
                configuredKey: null,
                isDevelopment: true)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/videos/M999.mp4", true)]
    [InlineData("https://cdn.kongor.net/videos/M999.mp4", true)]
    [InlineData("http://localhost:55555/videos/M999.mp4", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("/videos/../../etc/passwd", false)]
    [InlineData("//evil.example.com/payload.mp4", false)]
    public void IsAllowedReplayVideoUrl_ValidatesSchemeAndBlocksTraversal(string? candidateUrl, bool expectedAllowed)
    {
        KONGOR.IsAllowedReplayVideoUrl(candidateUrl).Should().Be(expectedAllowed);
    }
}
