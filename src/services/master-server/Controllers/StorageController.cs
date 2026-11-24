// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.IO.Compression;
using ASPIRE.Common.Services.Supabase;
using KONGOR.MasterServer.Models.RequestResponse.Cloud;
using MediatR;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("master/storage")]
[Route("storage")]
[Route("master/cloud")]
[Route("cloud")]
[Route("api/storage")]
public class StorageController(
    IAccountRepository accountRepository,
    ISessionRepository sessionRepository,
    IDatabase distributedCache,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    SupabaseStorageClient? supabaseStorageClient = null,
    ILogger<StorageController>? logger = null,
    IMediator? mediator = null) : ControllerBase
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionRepository SessionRepository { get; } = sessionRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IWebHostEnvironment Environment { get; } = environment;
    private IConfiguration Configuration { get; } = configuration;
    private SupabaseStorageClient? SupabaseStorageClient { get; } = supabaseStorageClient;
    private ILogger<StorageController>? Logger { get; } = logger;
    private const long MaxCloudZipBytes = 5 * 1024 * 1024;
    private readonly IMediator? _mediator = mediator;


    [HttpPost("store")]
    public async Task<IActionResult> Store(
        [FromForm(Name = "cookie")] string? cookie,
        [FromForm(Name = "bucket")] string bucket,
        [FromForm(Name = "file_modify_time")] string? fileModifyTime,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        // Manual extraction for reliability given the specific client behavior
        if (string.IsNullOrEmpty(cookie)) cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie))
        {
            Logger?.LogWarning("[CloudStorage] Store request rejected: Missing Cookie");
            return Unauthorized("Missing Cookie");
        }

        (bool isValid, string? accountName) =
            await ValidateSession(cookie, cancellationToken);

        if (!isValid || string.IsNullOrEmpty(accountName))
        {
            Logger?.LogWarning("[CloudStorage] Store request rejected: Invalid Cookie");
            return Unauthorized("Invalid Cookie");
        }

        if (file == null && (!Request.HasFormContentType || Request.Form.Files.Count == 0))
        {
            Logger?.LogWarning("[CloudStorage] Store request rejected: No file uploaded in form for {AccountName}",
                accountName);
            return BadRequest("No file uploaded");
        }

        IFormFile uploadedFile = file ?? Request.Form.Files[0]; // Usually named "cloud.zip"
        if (uploadedFile.Length <= 0 || uploadedFile.Length > MaxCloudZipBytes)
        {
            Logger?.LogWarning(
                "[SECURITY-ALERT] [CloudStorage] Rejected cloud.zip upload with invalid/oversized Length={FileSize} bytes (Max={MaxBytes}) for Account '{AccountName}'.",
                uploadedFile.Length, MaxCloudZipBytes, accountName);
            return uploadedFile.Length <= 0
                ? BadRequest("Uploaded cloud configuration archive is empty.")
                : StatusCode(StatusCodes.Status413PayloadTooLarge, "Cloud configuration archive exceeds 5 MB limit.");
        }

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account == null)
        {
            Logger?.LogWarning("[CloudStorage] Store request rejected: Account not found for {AccountName}",
                accountName);
            return Unauthorized("Account Not Found");
        }

        Logger?.LogInformation(
            "[CloudStorage] Storing cloud config for Account ID: {AccountId} ({AccountName}), FileSize: {FileSize} bytes",
            account.ID, account.Name, uploadedFile.Length);

        try
        {
            byte[] fileBytes;
            await using (MemoryStream ms = new())
            {
                await using Stream uploadReadStream = uploadedFile.OpenReadStream();
                byte[] chunk = new byte[16 * 1024];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await uploadReadStream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > MaxCloudZipBytes)
                    {
                        Logger?.LogWarning(
                            "[SECURITY-ALERT] [CloudStorage] Aborted streaming cloud.zip upload exceeding {MaxBytes} bytes for Account ID: {AccountId} ({AccountName}).",
                            MaxCloudZipBytes, account.ID, account.Name);
                        return StatusCode(StatusCodes.Status413PayloadTooLarge, "Cloud configuration archive exceeds 5 MB limit.");
                    }

                    ms.Write(chunk, 0, bytesRead);
                }
                fileBytes = ms.ToArray();
            }

            if (_mediator is not null)
            {
                var result = await _mediator.Send(new StoreCloudConfigCommand(account.ID, bucket, fileModifyTime, fileBytes), cancellationToken);
                return new PhpResult(new StorageResponse { Success = result.Success });
            }

            // 1. Upload to Supabase Storage in 'configs' bucket: {account.ID}/cloud.zip
            if (SupabaseStorageClient != null)
                try
                {
                    using MemoryStream uploadStream = new(fileBytes);
                    await SupabaseStorageClient.UploadFileAsync(
                        SupabaseStorageClient.ConfigsBucket,
                        $"{account.ID}/cloud.zip",
                        uploadStream,
                        "application/zip",
                        true,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex,
                        "[CloudStorage] Supabase upload failed for Account ID: {AccountId}, continuing with local storage",
                        account.ID);
                }

            // 2. Also save to local disk fallback
            string relativePath = Configuration["Storage:CloudPath"] ?? "App_Data/Cloud";
            string storageRoot = Path.Combine(Environment.ContentRootPath, relativePath);
            string accountStoragePath = Path.Combine(storageRoot, account.ID.ToString());

            if (!Directory.Exists(accountStoragePath)) Directory.CreateDirectory(accountStoragePath);

            string filePath = Path.Combine(accountStoragePath, "cloud.zip");
            await System.IO.File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken);

            // 3. Update Account Entity in Database via repository
            DateTimeOffset backupTime;
            if (!string.IsNullOrEmpty(fileModifyTime) && DateTimeOffset.TryParseExact(fileModifyTime,
                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsedTime))
                backupTime = parsedTime;
            else
                backupTime = TimeProvider.System.GetUtcNow();

            await AccountRepository.UpdateCloudStoragePreferencesAsync(account.ID, useCloud: true, autoUpload: null, backupTime: backupTime, cancellationToken);

            Logger?.LogInformation(
                "[CloudStorage] Successfully stored cloud config for Account ID: {AccountId} ({AccountName})",
                account.ID, account.Name);

            return new PhpResult(new StorageResponse { Success = true });
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "[CloudStorage] Exception storing cloud config");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal Server Error" });

        }
    }

    [HttpGet("retrieve")]
    [HttpPost("retrieve")]
    public async Task<IActionResult> Retrieve(
        [FromForm(Name = "cookie")] string? cookie,
        [FromForm(Name = "bucket")] string? bucket,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cookie)) cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie))
        {
            Logger?.LogWarning("[CloudStorage] Retrieve request rejected: Missing Cookie");
            return Unauthorized("Missing Cookie");
        }

        (bool isValid, string? accountName) =
            await ValidateSession(cookie, cancellationToken);

        if (!isValid || string.IsNullOrEmpty(accountName))
        {
            Logger?.LogWarning("[CloudStorage] Retrieve request rejected: Invalid Cookie");
            return Unauthorized("Invalid Cookie");
        }

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account == null)
        {
            Logger?.LogWarning("[CloudStorage] Retrieve request rejected: Account not found for {AccountName}",
                accountName);
            return Unauthorized("Account Not Found");
        }

        if (_mediator is not null)
        {
            var result = await _mediator.Send(new RetrieveCloudConfigQuery(account.ID, bucket), cancellationToken);
            return File(result.ZipBytes, result.ContentType, result.FileName);
        }

        Logger?.LogInformation(
            "[CloudStorage] Retrieving cloud config for Account ID: {AccountId} ({AccountName}), RequestedBucket: {Bucket}",
            account.ID, account.Name, bucket);

        Stream? configStream = null;

        // 1. Attempt to download from Supabase Storage 'configs' bucket
        if (SupabaseStorageClient != null)
            try
            {
                List<string> existingFiles = await SupabaseStorageClient.ListFilesAsync(
                    SupabaseStorageClient.ConfigsBucket,
                    $"{account.ID}",
                    cancellationToken);

                Logger?.LogInformation(
                    "[CloudStorage] Supabase bucket '{Bucket}' folder '{AccountId}' contains: [{Files}]",
                    SupabaseStorageClient.ConfigsBucket, account.ID, string.Join(", ", existingFiles));

                List<string> candidatePaths = [];
                foreach (string f in existingFiles)
                    if (!string.IsNullOrWhiteSpace(f))
                        candidatePaths.Add($"{account.ID}/{f}");

                candidatePaths.Add($"{account.ID}/cloud.zip");
                candidatePaths.Add($"{account.ID}/startup.cfg");
                candidatePaths.Add($"{account.ID}/config.zip");
                candidatePaths.Add($"{account.ID}/{account.ID}.zip");
                candidatePaths.Add($"{account.ID}/game/startup.cfg");
                candidatePaths.Add($"{account.ID}/configuration/startup.cfg");

                foreach (string candidate in candidatePaths.Distinct())
                {
                    Stream? candidateStream = await SupabaseStorageClient.DownloadFileAsync(
                        SupabaseStorageClient.ConfigsBucket,
                        candidate,
                        cancellationToken);

                    if (candidateStream != null && candidateStream.Length > 0)
                    {
                        Logger?.LogInformation(
                            "[CloudStorage] Downloaded valid cloud asset from Supabase: {Path} ({Bytes} bytes)",
                            candidate, candidateStream.Length);

                        if (candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            configStream = candidateStream;
                            break;
                        }

                        // Convert single text config file (startup.cfg) to a valid zip archive stream in-memory
                        MemoryStream zipStream = new();
                        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create, true))
                        {
                            ZipArchiveEntry cloudEntry = archive.CreateEntry("cloud.cfg"); using (Stream cloudStream = cloudEntry.Open()) { candidateStream.Position = 0; await candidateStream.CopyToAsync(cloudStream, cancellationToken); } ZipArchiveEntry entry = archive.CreateEntry("startup.cfg");
                            using Stream entryStream = entry.Open();
                            candidateStream.Position = 0;
                            await candidateStream.CopyToAsync(entryStream, cancellationToken);
                        }

                        zipStream.Position = 0;
                        configStream = zipStream;
                        break;
                    }

                    candidateStream?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex,
                    "[CloudStorage] Supabase download failed for Account ID: {AccountId}, falling back to local storage",
                    account.ID);
            }

        // 2. Fallback to local storage
        if (configStream == null)
        {
            string relativePath = Configuration["Storage:CloudPath"] ?? "App_Data/Cloud";
            string storageRoot = Path.Combine(Environment.ContentRootPath, relativePath);
            string accountStoragePath = Path.Combine(storageRoot, account.ID.ToString());
            string filePath = Path.Combine(accountStoragePath, "cloud.zip");

            if (System.IO.File.Exists(filePath))
                try
                {
                    byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken);
                    configStream = new MemoryStream(fileBytes);
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex,
                        "[CloudStorage] Failed to read local fallback cloud storage file for Account ID: {AccountId}",
                        account.ID);
                }
        }

        // 3. Fallback to empty zip
        if (configStream == null)
        {
            Logger?.LogInformation(
                "[CloudStorage] No existing cloud backup found for Account ID: {AccountId}, returning empty zip archive",
                account.ID);

            MemoryStream emptyZipStream = new();
            try
            {
                using (ZipArchive archive = new(emptyZipStream, ZipArchiveMode.Create, true))
                {
                    // Empty archive created
                }

                emptyZipStream.Position = 0;
                configStream = emptyZipStream;
            }
            catch
            {
                emptyZipStream.Dispose();
                throw;
            }
        }

        try
        {
            // Downloading cloud config is read-only; preserve BackupLastUpdatedTime written at upload time.

            Logger?.LogInformation(
                "[CloudStorage] Returning cloud zip stream ({Bytes} bytes) for Account ID: {AccountId} ({AccountName})",
                configStream.Length, account.ID, account.Name);

            return File(configStream, "application/octet-stream", "cloud.zip");
        }
        catch
        {
            configStream?.Dispose();
            throw;
        }
    }

    [HttpPost("set")]
    [HttpPost("save")]
    public async Task<IActionResult> SaveSettings(
        [FromForm(Name = "cookie")] string? cookie,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cookie)) cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie)) return Unauthorized("Missing Cookie");

        (bool isValid, string? accountName) =
            await ValidateSession(cookie, cancellationToken);

        if (!isValid || string.IsNullOrEmpty(accountName)) return Unauthorized("Invalid Cookie");

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account == null) return Unauthorized("Account Not Found");

        if (Request.HasFormContentType)
        {
            bool? useCloud = null;
            bool? autoUpload = null;

            string useCloudStr = Request.Form["use_cloud"].ToString();
            if (string.IsNullOrEmpty(useCloudStr)) useCloudStr = Request.Form["useCloud"].ToString();

            if (!string.IsNullOrEmpty(useCloudStr))
            {
                if (useCloudStr == "1" || useCloudStr.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    useCloud = true;
                }
                else if (useCloudStr == "0" || useCloudStr.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    useCloud = false;
                }
            }

            string autoUploadStr = Request.Form["cloud_autoupload"].ToString();
            if (string.IsNullOrEmpty(autoUploadStr)) autoUploadStr = Request.Form["auto_upload"].ToString();

            if (string.IsNullOrEmpty(autoUploadStr)) autoUploadStr = Request.Form["automatic_cloud_upload"].ToString();

            if (!string.IsNullOrEmpty(autoUploadStr))
            {
                if (autoUploadStr == "1" || autoUploadStr.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    autoUpload = true;
                }
                else if (autoUploadStr == "0" || autoUploadStr.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    autoUpload = false;
                }
            }

            if (_mediator is not null)
            {
                if (useCloud.HasValue || autoUpload.HasValue)
                {
                    await _mediator.Send(new UpdateCloudPreferencesCommand(account.ID, useCloud, autoUpload), cancellationToken);
                }
                return new PhpResult(new StorageResponse { Success = true });
            }

            if (useCloud.HasValue || autoUpload.HasValue)
            {
                await AccountRepository.UpdateCloudStoragePreferencesAsync(account.ID, useCloud, autoUpload, null, cancellationToken);
            }
        }

        return new PhpResult(new StorageResponse { Success = true });
    }

    [HttpPost("set-user-enrollment")]
    public async Task<IActionResult> EnableConfigurationBackup(
        [FromForm(Name = "cookie")] string? cookie,
        [FromForm(Name = "enroll")] string? enroll,
        [FromForm(Name = "cloud_autoupload")] string? automaticUpload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cookie)) cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie)) return Unauthorized("Missing Cookie");

        (bool isValid, string? accountName) =
            await ValidateSession(cookie, cancellationToken);

        if (!isValid || string.IsNullOrEmpty(accountName)) return Unauthorized("Invalid Cookie");

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account == null) return Unauthorized("Account Not Found");

        bool? useCloud = null;
        bool? autoUpload = null;

        if (!string.IsNullOrEmpty(enroll))
            useCloud = enroll == "1" || enroll.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(automaticUpload))
            autoUpload = automaticUpload == "1" || automaticUpload.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (_mediator is not null)
        {
            if (useCloud.HasValue || autoUpload.HasValue)
            {
                await _mediator.Send(new UpdateCloudPreferencesCommand(account.ID, useCloud, autoUpload), cancellationToken);
            }

            var enrollment = await _mediator.Send(new GetUserEnrollmentQuery(account.ID), cancellationToken);
            var cloudInfo = new CloudStorageInformation
            {
                AccountID = enrollment?.AccountId ?? account.ID.ToString(),
                UseCloud = enrollment?.UseCloud ?? (account.UseCloud ? "1" : "0"),
                AutomaticCloudUpload = enrollment?.AutomaticCloudUpload ?? (account.AutomaticCloudUpload ? "1" : "0"),
                BackupLastUpdatedTime = enrollment?.BackupLastUpdatedTime ??
                                        DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
            };

            return new PhpResult(new EnableConfigurationBackupResponse { Data = cloudInfo });
        }

        if (useCloud.HasValue || autoUpload.HasValue)
        {
            await AccountRepository.UpdateCloudStoragePreferencesAsync(account.ID, useCloud, autoUpload, null, cancellationToken);
            if (useCloud.HasValue) account.UseCloud = useCloud.Value;
            if (autoUpload.HasValue) account.AutomaticCloudUpload = autoUpload.Value;
        }

        CloudStorageInformation legacyCloudInfo = new()
        {
            AccountID = account.ID.ToString(),
            UseCloud = account.UseCloud ? "1" : "0",
            AutomaticCloudUpload = account.AutomaticCloudUpload ? "1" : "0",
            BackupLastUpdatedTime = account.BackupLastUpdatedTime?.ToString("yyyy-MM-dd HH:mm:ss") ??
                                    DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
        };

        EnableConfigurationBackupResponse response = new() { Data = legacyCloudInfo };

        return new PhpResult(response);
    }

    [HttpGet("get-user-enrollment")]
    [HttpPost("get-user-enrollment")]
    public async Task<IActionResult> GetUserEnrollment(
        [FromForm(Name = "cookie")] string? cookie,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cookie)) cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie)) return Unauthorized("Missing Cookie");

        (bool isValid, string? accountName) =
            await ValidateSession(cookie, cancellationToken);

        if (!isValid || string.IsNullOrEmpty(accountName)) return Unauthorized("Invalid Cookie");

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account == null) return Unauthorized("Account Not Found");

        if (_mediator is not null)
        {
            var enrollment = await _mediator.Send(new GetUserEnrollmentQuery(account.ID), cancellationToken);
            var cloudInfo = new CloudStorageInformation
            {
                AccountID = enrollment?.AccountId ?? account.ID.ToString(),
                UseCloud = enrollment?.UseCloud ?? (account.UseCloud ? "1" : "0"),
                AutomaticCloudUpload = enrollment?.AutomaticCloudUpload ?? (account.AutomaticCloudUpload ? "1" : "0"),
                BackupLastUpdatedTime = enrollment?.BackupLastUpdatedTime ??
                                        DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
            };

            return new PhpResult(new EnableConfigurationBackupResponse { Data = cloudInfo });
        }

        CloudStorageInformation fallbackCloudInfo = new()
        {
            AccountID = account.ID.ToString(),
            UseCloud = account.UseCloud ? "1" : "0",
            AutomaticCloudUpload = account.AutomaticCloudUpload ? "1" : "0",
            BackupLastUpdatedTime = account.BackupLastUpdatedTime?.ToString("yyyy-MM-dd HH:mm:ss") ??
                                    DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
        };

        EnableConfigurationBackupResponse fallbackResponse = new() { Data = fallbackCloudInfo };

        return new PhpResult(fallbackResponse);
    }

    private async Task<(bool IsValid, string? AccountName)> ValidateSession(string cookie, CancellationToken ct)
    {
        string? accountName = await SessionRepository.GetAccountNameForSessionCookieAsync(cookie, ct);
        if (accountName is null)
        {
            (bool isValid, string? cacheAccountName) = await DistributedCache.ValidateAccountSessionCookie(cookie);
            if (isValid) accountName = cacheAccountName;
        }

        return (accountName is not null, accountName);
    }
}
