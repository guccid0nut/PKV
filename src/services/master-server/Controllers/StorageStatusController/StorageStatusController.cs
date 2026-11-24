// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers.StorageStatusController;

[ApiController]
[Route("master/storage/status")]
[Route("storage/status")]
public class StorageStatusController(
    IAccountRepository accountRepository,
    ISessionRepository sessionRepository,
    IDatabase distributedCache) : ControllerBase
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionRepository SessionRepository { get; } = sessionRepository;
    private IDatabase DistributedCache { get; } = distributedCache;

    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> StorageStatus(CancellationToken cancellationToken = default)
    {
        string? cookie = ClientRequestHelper.GetCookie(Request);

        if (string.IsNullOrEmpty(cookie)) return Unauthorized("Missing Cookie");

        string? accountName = await SessionRepository.GetAccountNameForSessionCookieAsync(cookie, cancellationToken);
        if (accountName is null)
        {
            (bool isValid, string? cacheAccountName) = await DistributedCache.ValidateAccountSessionCookie(cookie);
            if (isValid) accountName = cacheAccountName;
        }

        if (string.IsNullOrEmpty(accountName)) return Unauthorized("Invalid Cookie");

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

            if (useCloud.HasValue || autoUpload.HasValue)
            {
                await AccountRepository.UpdateCloudStoragePreferencesAsync(account.ID, useCloud, autoUpload, null, cancellationToken);
                if (useCloud.HasValue) account.UseCloud = useCloud.Value;
                if (autoUpload.HasValue) account.AutomaticCloudUpload = autoUpload.Value;
            }
        }

        StorageStatusResponse response = new()
        {
            CloudStorageInformation = new CloudStorageInformation
            {
                AccountID = account.ID.ToString(),
                UseCloud = account.UseCloud ? "1" : "0",
                AutomaticCloudUpload = account.AutomaticCloudUpload ? "1" : "0",
                BackupLastUpdatedTime = account.BackupLastUpdatedTime?.ToString("yyyy-MM-dd HH:mm:ss") ??
                                        DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
            }
        };

        return Ok(PhpSerialization.Serialize(response));
    }
}

