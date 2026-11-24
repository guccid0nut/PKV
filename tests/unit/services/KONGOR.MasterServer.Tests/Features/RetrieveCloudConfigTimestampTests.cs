// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using FluentAssertions;
using KONGOR.MasterServer.Features.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;
using Xunit;

namespace KONGOR.MasterServer.Tests.Features;

public sealed class RetrieveCloudConfigTimestampTests
{
    [Fact]
    public void StoreCloudConfigCommand_EnforcesSub3SecondK2HttpClientLockBounds()
    {
        // Protects: K2 native PostCloudConfig 3.0s HTTP client timeout (k2_x64.dll + 0x7320dd).
        // Fails when: LockTimeout or LockExpiry exceeds safe single-upload bounds and causes alternating timeouts.
        ILockableRequest cmd = new StoreCloudConfigCommand(59, "cloud", "2026-11-03 21:26:58", [0x50, 0x4B]);

        cmd.LockKey.Should().Be("lock:account:59:cloud_storage");
        cmd.LockExpiry.Should().Be(TimeSpan.FromSeconds(5));
        cmd.LockTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RetrieveCloudConfigHandler_DoesNotOverwriteBackupLastUpdatedTimeWithServerUtc()
    {
        // Protects: Client local BackupLastUpdatedTime written during StoreCloudConfigHandler upload.
        // Fails when: Downloading cloud.zip mutates BackupLastUpdatedTime to server UTC (+6h jump).
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_cloud_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "App_Data", "Cloud", "59"));

        try
        {
            string zipPath = Path.Combine(tempRoot, "App_Data", "Cloud", "59", "cloud.zip");
            await using (FileStream fs = File.Create(zipPath))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("cloud.cfg");
                await using StreamWriter writer = new(entry.Open());
                await writer.WriteLineAsync("SetSave \"sound_masterVolume\" \"0.75\"");
            }

            Mock<IAccountRepository> mockAccountRepo = new(MockBehavior.Strict);
            Mock<IWebHostEnvironment> mockEnv = new();
            mockEnv.SetupGet(e => e.ContentRootPath).Returns(tempRoot);

            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:CloudPath"] = "App_Data/Cloud" })
                .Build();

            RetrieveCloudConfigHandler handler = new(
                mockAccountRepo.Object,
                mockEnv.Object,
                config,
                supabaseStorageClient: null,
                logger: NullLogger<RetrieveCloudConfigHandler>.Instance);

            RetrieveCloudConfigResultDto result = await handler.Handle(
                new RetrieveCloudConfigQuery(59, "cloud"),
                CancellationToken.None);

            result.Found.Should().BeTrue();
            result.FileName.Should().Be("cloud.zip");
            result.ZipBytes.Should().NotBeEmpty();

            // Strict mock proves UpdateCloudStoragePreferencesAsync was NEVER invoked during download
            mockAccountRepo.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
