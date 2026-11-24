// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Features.Storage;
using KONGOR.MasterServer.Models.RequestResponse.Cloud;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.CQRS;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class StorageMediatRTests
{
    private static Account CreateTestAccount(int id = 42, string name = "Chronos")
    {
        User user = new()
        {
            ID = 1,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = 5000,
            SilverCoins = 10000,
            InventoryItems = []
        };

        return new Account
        {
            ID = id,
            Name = name,
            User = user,
            IsMain = true,
            UseCloud = true,
            AutomaticCloudUpload = true,
            BackupLastUpdatedTime = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)
        };
    }

    [Fact]
    public void StoreCloudConfigValidator_ValidatesAccountIdBucketAndBytes()
    {
        StoreCloudConfigValidator validator = new();
        byte[] validBytes = [1, 2, 3];

        validator.Validate(new StoreCloudConfigCommand(0, "configs", null, validBytes)).IsValid.Should().BeFalse();
        validator.Validate(new StoreCloudConfigCommand(42, "", null, validBytes)).IsValid.Should().BeFalse();
        validator.Validate(new StoreCloudConfigCommand(42, "configs", null, [])).IsValid.Should().BeFalse();
        validator.Validate(new StoreCloudConfigCommand(42, "configs", null, validBytes)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void StoreCloudConfigCommand_Implements_ILockableRequest()
    {
        StoreCloudConfigCommand command = new(42, "configs", null, [1, 2, 3]);
        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:42:cloud_storage");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RetrieveCloudConfigValidator_ValidatesAccountId()
    {
        RetrieveCloudConfigValidator validator = new();
        validator.Validate(new RetrieveCloudConfigQuery(0, null)).IsValid.Should().BeFalse();
        validator.Validate(new RetrieveCloudConfigQuery(42, "configs")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void UpdateCloudPreferencesValidator_ValidatesAccountId()
    {
        UpdateCloudPreferencesValidator validator = new();
        validator.Validate(new UpdateCloudPreferencesCommand(0, true, false)).IsValid.Should().BeFalse();
        validator.Validate(new UpdateCloudPreferencesCommand(42, true, false)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void UpdateCloudPreferencesCommand_Implements_ILockableRequest()
    {
        UpdateCloudPreferencesCommand command = new(42, true, false);
        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:42:cloud_storage");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void GetUserEnrollmentValidator_ValidatesAccountId()
    {
        GetUserEnrollmentValidator validator = new();
        validator.Validate(new GetUserEnrollmentQuery(0)).IsValid.Should().BeFalse();
        validator.Validate(new GetUserEnrollmentQuery(42)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task StoreCloudConfigHandler_SavesToLocalFallbackAndUpdatesRepository()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorStorageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Mock<IAccountRepository> mockAccountRepo = new();
            Mock<IWebHostEnvironment> mockEnv = new();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);

            Mock<IConfiguration> mockConfig = new();
            mockConfig.Setup(c => c["Storage:CloudPath"]).Returns("App_Data/Cloud");

            byte[] dummyBytes = Encoding.UTF8.GetBytes("fake_zip_bytes");

            StoreCloudConfigHandler handler = new(
                mockAccountRepo.Object,
                mockEnv.Object,
                mockConfig.Object,
                supabaseStorageClient: null,
                logger: NullLogger<StoreCloudConfigHandler>.Instance);

            var result = await handler.Handle(
                new StoreCloudConfigCommand(42, "configs", "2026-09-28 12:00:00", dummyBytes),
                CancellationToken.None);

            result.Should().NotBeNull();
            result.Success.Should().BeTrue();

            string writtenFile = Path.Combine(tempDir, "App_Data", "Cloud", "42", "cloud.zip");
            File.Exists(writtenFile).Should().BeTrue();
            (await File.ReadAllBytesAsync(writtenFile)).Should().Equal(dummyBytes);

            mockAccountRepo.Verify(r => r.UpdateCloudStoragePreferencesAsync(
                42,
                true,
                null,
                It.Is<DateTimeOffset?>(dto => dto.HasValue && dto.Value.Year == 2026),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RetrieveCloudConfigHandler_WhenLocalFileExists_ReturnsZipBytes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorStorageTests_" + Guid.NewGuid().ToString("N"));
        string accountFolder = Path.Combine(tempDir, "App_Data", "Cloud", "42");
        Directory.CreateDirectory(accountFolder);

        try
        {
            byte[] fileBytes = Encoding.UTF8.GetBytes("persisted_cloud_config");
            await File.WriteAllBytesAsync(Path.Combine(accountFolder, "cloud.zip"), fileBytes);

            Mock<IAccountRepository> mockAccountRepo = new();
            Mock<IWebHostEnvironment> mockEnv = new();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);

            Mock<IConfiguration> mockConfig = new();
            mockConfig.Setup(c => c["Storage:CloudPath"]).Returns("App_Data/Cloud");

            RetrieveCloudConfigHandler handler = new(
                mockAccountRepo.Object,
                mockEnv.Object,
                mockConfig.Object,
                supabaseStorageClient: null,
                logger: NullLogger<RetrieveCloudConfigHandler>.Instance);

            var result = await handler.Handle(new RetrieveCloudConfigQuery(42, "configs"), CancellationToken.None);

            result.Should().NotBeNull();
            result.Found.Should().BeTrue();
            result.ZipBytes.Should().Equal(fileBytes);
            result.FileName.Should().Be("cloud.zip");

            mockAccountRepo.Verify(r => r.UpdateCloudStoragePreferencesAsync(
                42,
                null,
                null,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RetrieveCloudConfigHandler_WhenNoFileExists_GeneratesEmptyZip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorStorageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Mock<IAccountRepository> mockAccountRepo = new();
            Mock<IWebHostEnvironment> mockEnv = new();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);

            Mock<IConfiguration> mockConfig = new();
            mockConfig.Setup(c => c["Storage:CloudPath"]).Returns("App_Data/Cloud");

            RetrieveCloudConfigHandler handler = new(
                mockAccountRepo.Object,
                mockEnv.Object,
                mockConfig.Object,
                supabaseStorageClient: null,
                logger: NullLogger<RetrieveCloudConfigHandler>.Instance);

            var result = await handler.Handle(new RetrieveCloudConfigQuery(42, "configs"), CancellationToken.None);

            result.Should().NotBeNull();
            result.Found.Should().BeTrue();
            result.ZipBytes.Should().NotBeEmpty();

            // Verify it's a valid zip archive
            using MemoryStream ms = new(result.ZipBytes);
            using ZipArchive zip = new(ms, ZipArchiveMode.Read);
            zip.Entries.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UpdateCloudPreferencesHandler_CallsRepository()
    {
        Mock<IAccountRepository> mockAccountRepo = new();
        UpdateCloudPreferencesHandler handler = new(mockAccountRepo.Object, NullLogger<UpdateCloudPreferencesHandler>.Instance);

        var result = await handler.Handle(new UpdateCloudPreferencesCommand(42, true, false), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        mockAccountRepo.Verify(r => r.UpdateCloudStoragePreferencesAsync(42, true, false, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUserEnrollmentHandler_WhenAccountExists_ReturnsEnrollmentDto()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        GetUserEnrollmentHandler handler = new(mockAccountRepo.Object, NullLogger<GetUserEnrollmentHandler>.Instance);
        var result = await handler.Handle(new GetUserEnrollmentQuery(42), CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccountId.Should().Be("42");
        result.UseCloud.Should().Be("1");
        result.AutomaticCloudUpload.Should().Be("1");
        result.BackupLastUpdatedTime.Should().Be("2026-09-28 12:00:00");
    }

    [Fact]
    public async Task GetUserEnrollmentHandler_WhenAccountMissing_ReturnsNull()
    {
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Account?)null);

        GetUserEnrollmentHandler handler = new(mockAccountRepo.Object, NullLogger<GetUserEnrollmentHandler>.Instance);
        var result = await handler.Handle(new GetUserEnrollmentQuery(999), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task StorageController_Store_DelegatesToMediator()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(a => a.GetByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("valid_cookie", It.IsAny<CancellationToken>())).ReturnsAsync("Chronos");

        Mock<IDatabase> mockRedis = new();
        Mock<IWebHostEnvironment> mockEnv = new();
        Mock<IConfiguration> mockConfig = new();
        Mock<IMediator> mockMediator = new();

        mockMediator.Setup(m => m.Send(It.IsAny<StoreCloudConfigCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoreCloudConfigResultDto(true));

        StorageController controller = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            mockRedis.Object,
            mockEnv.Object,
            mockConfig.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        byte[] fileBytes = Encoding.UTF8.GetBytes("test config content");
        MemoryStream fileStream = new(fileBytes);
        IFormFile formFile = new FormFile(fileStream, 0, fileBytes.Length, "file", "cloud.zip");
        FormFileCollection files = [formFile];

        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "valid_cookie",
            ["bucket"] = "configs"
        }, files);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.Store("valid_cookie", "configs", null, formFile, CancellationToken.None);

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().BeOfType<StorageResponse>();
        ((StorageResponse)phpResult.Value).Success.Should().BeTrue();

        mockMediator.Verify(m => m.Send(
            It.Is<StoreCloudConfigCommand>(c => c.AccountId == 42 && c.Bucket == "configs" && c.FileBytes.Length == fileBytes.Length),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StorageController_Retrieve_DelegatesToMediator()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(a => a.GetByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("valid_cookie", It.IsAny<CancellationToken>())).ReturnsAsync("Chronos");

        Mock<IDatabase> mockRedis = new();
        Mock<IWebHostEnvironment> mockEnv = new();
        Mock<IConfiguration> mockConfig = new();
        Mock<IMediator> mockMediator = new();

        byte[] zipBytes = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        mockMediator.Setup(m => m.Send(It.IsAny<RetrieveCloudConfigQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrieveCloudConfigResultDto(true, zipBytes, "application/octet-stream", "cloud.zip"));

        StorageController controller = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            mockRedis.Object,
            mockEnv.Object,
            mockConfig.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "valid_cookie",
            ["bucket"] = "configs"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.Retrieve("valid_cookie", "configs", CancellationToken.None);

        actionResult.Should().BeOfType<FileContentResult>();
        var fileResult = (FileContentResult)actionResult;
        fileResult.FileContents.Should().Equal(zipBytes);
        fileResult.FileDownloadName.Should().Be("cloud.zip");

        mockMediator.Verify(m => m.Send(
            It.Is<RetrieveCloudConfigQuery>(q => q.AccountId == 42 && q.Bucket == "configs"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StorageController_SaveSettings_DelegatesToMediator()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(a => a.GetByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("valid_cookie", It.IsAny<CancellationToken>())).ReturnsAsync("Chronos");

        Mock<IDatabase> mockRedis = new();
        Mock<IWebHostEnvironment> mockEnv = new();
        Mock<IConfiguration> mockConfig = new();
        Mock<IMediator> mockMediator = new();

        mockMediator.Setup(m => m.Send(It.IsAny<UpdateCloudPreferencesCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateCloudPreferencesResultDto(true));

        StorageController controller = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            mockRedis.Object,
            mockEnv.Object,
            mockConfig.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = "valid_cookie",
            ["use_cloud"] = "1",
            ["cloud_autoupload"] = "0"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.SaveSettings("valid_cookie", CancellationToken.None);

        actionResult.Should().BeOfType<PhpResult>();
        mockMediator.Verify(m => m.Send(
            It.Is<UpdateCloudPreferencesCommand>(c => c.AccountId == 42 && c.UseCloud == true && c.AutoUpload == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StorageController_EnableConfigurationBackup_DelegatesToMediator()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(a => a.GetByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("valid_cookie", It.IsAny<CancellationToken>())).ReturnsAsync("Chronos");

        Mock<IDatabase> mockRedis = new();
        Mock<IWebHostEnvironment> mockEnv = new();
        Mock<IConfiguration> mockConfig = new();
        Mock<IMediator> mockMediator = new();

        mockMediator.Setup(m => m.Send(It.IsAny<UpdateCloudPreferencesCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateCloudPreferencesResultDto(true));
        mockMediator.Setup(m => m.Send(It.IsAny<GetUserEnrollmentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserEnrollmentResultDto("42", "1", "1", "2026-09-28 12:00:00"));

        StorageController controller = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            mockRedis.Object,
            mockEnv.Object,
            mockConfig.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.EnableConfigurationBackup("valid_cookie", "1", "1", CancellationToken.None);

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().BeOfType<EnableConfigurationBackupResponse>();
        var response = (EnableConfigurationBackupResponse)phpResult.Value;
        response.Data.AccountID.Should().Be("42");
        response.Data.UseCloud.Should().Be("1");

        mockMediator.Verify(m => m.Send(
            It.Is<UpdateCloudPreferencesCommand>(c => c.AccountId == 42 && c.UseCloud == true && c.AutoUpload == true),
            It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(
            It.Is<GetUserEnrollmentQuery>(q => q.AccountId == 42),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StorageController_GetUserEnrollment_DelegatesToMediator()
    {
        Account account = CreateTestAccount(42, "Chronos");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(a => a.GetByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo.Setup(s => s.GetAccountNameForSessionCookieAsync("valid_cookie", It.IsAny<CancellationToken>())).ReturnsAsync("Chronos");

        Mock<IDatabase> mockRedis = new();
        Mock<IWebHostEnvironment> mockEnv = new();
        Mock<IConfiguration> mockConfig = new();
        Mock<IMediator> mockMediator = new();

        mockMediator.Setup(m => m.Send(It.IsAny<GetUserEnrollmentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserEnrollmentResultDto("42", "1", "0", "2026-09-28 12:00:00"));

        StorageController controller = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            mockRedis.Object,
            mockEnv.Object,
            mockConfig.Object,
            supabaseStorageClient: null,
            logger: NullLogger<StorageController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.GetUserEnrollment("valid_cookie", CancellationToken.None);

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().BeOfType<EnableConfigurationBackupResponse>();
        var response = (EnableConfigurationBackupResponse)phpResult.Value;
        response.Data.AccountID.Should().Be("42");
        response.Data.UseCloud.Should().Be("1");
        response.Data.AutomaticCloudUpload.Should().Be("0");

        mockMediator.Verify(m => m.Send(
            It.Is<GetUserEnrollmentQuery>(q => q.AccountId == 42),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
