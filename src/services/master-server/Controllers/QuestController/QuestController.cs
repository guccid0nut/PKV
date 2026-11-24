// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.Globalization;
using System.Text;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Quests;
using Microsoft.AspNetCore.Http;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers.QuestController;

// Intentionally omit [ApiController] so ModelStateInvalidFilter does not return HTTP 400
// on unpatched dedicated servers sending malformed non-UTF-8/NUL bytes in cookie forms.
[Route("master/quest")]
[Route("master/questserver")]
[Consumes("application/x-www-form-urlencoded")]
public class QuestController(
    ISessionRepository sessionRepository,
    ILogger<QuestController> logger,
    IDatabase? distributedCache = null,
    IAccountRepository? accountRepository = null,
    IWarEffortService? warEffortService = null)
    : ControllerBase
{
    private ISessionRepository SessionRepository { get; } = sessionRepository;

    [HttpPost("getcurrentquests")]
    public async Task<IActionResult> GetCurrentQuests(CancellationToken cancellationToken = default)
    {
        try
        {
            string? cookie = await ExtractFormParameterSafelyAsync("getcurrentquests", "cookie", cancellationToken);
            logger.LogGetCurrentQuests(cookie != null);

            int? resolvedAccountId = await ResolveAuthenticatedAccountIdAsync(cookie, "getcurrentquests", cancellationToken);
            if (!resolvedAccountId.HasValue)
            {
                // Fallback to account_ids[] when called by a validated dedicated server
                List<int> requestedIds = await ExtractAccountIdsSafelyAsync("getcurrentquests", cancellationToken);
                if (requestedIds.Count > 0)
                {
                    resolvedAccountId = requestedIds[0];
                }
            }

            if (warEffortService is null || !warEffortService.IsEnabled)
            {
                Dictionary<string, object> disabled = new()
                {
                    ["success"] = false,
                    ["error"] = "disabled",
                    ["quests"] = new Dictionary<string, object>(),
                    ["0"] = 1
                };
                return Ok(PhpSerialization.Serialize(disabled));
            }

            if (!resolvedAccountId.HasValue || resolvedAccountId.Value <= 0)
            {
                Dictionary<string, object> fallback = new()
                {
                    ["success"] = false,
                    ["error"] = "session_not_found",
                    ["quests"] = new Dictionary<string, object>(),
                    ["0"] = 1
                };
                return Ok(PhpSerialization.Serialize(fallback));
            }

            Dictionary<string, object?> payload = await warEffortService
                .BuildClientPayloadAsync(resolvedAccountId.Value, includeOnlyNativeTriggers: false, cancellationToken)
                .ConfigureAwait(false);
            payload["0"] = 1;

            string serialized = PhpSerialization.Serialize(payload);
            logger.LogInformation(
                "[WAR_EFFORT] Served getcurrentquests for AccountId={AccountId} (bytes={Bytes})",
                resolvedAccountId.Value, serialized.Length);
            return Ok(serialized);
        }
        catch (Exception ex)
        {
            logger.LogGetCurrentQuestsError(ex);
            return StatusCode(500, "Internal Server Error");
        }
    }

    [HttpPost("getplayerquests")]
    public async Task<IActionResult> GetPlayerQuests(CancellationToken cancellationToken = default)
    {
        try
        {
            string? cookie = await ExtractFormParameterSafelyAsync("getplayerquests", "cookie", cancellationToken);

            if (cookie is not null)
            {
                bool isValid = await ValidateSessionOrMatchServerCookieAsync(cookie, "getplayerquests", cancellationToken);
                if (!isValid) logger.LogUnauthorizedPlayerQuests(SecurityMasking.MaskToken(cookie));
                // Do not return HTTP 401: causes client logout loop
            }

            if (warEffortService is null || !warEffortService.IsEnabled)
            {
                Dictionary<string, object> disabled = new() { ["quests"] = new Dictionary<string, object>(), ["0"] = 1 };
                return Ok(PhpSerialization.Serialize(disabled));
            }

            List<int> accountIds = await ExtractAccountIdsSafelyAsync("getplayerquests", cancellationToken);
            if (accountIds.Count == 0)
            {
                int? sessionAccountId = await ResolveAuthenticatedAccountIdAsync(cookie, "getplayerquests", cancellationToken);
                if (sessionAccountId.HasValue)
                {
                    accountIds.Add(sessionAccountId.Value);
                }
            }

            if (accountIds.Count == 0)
            {
                Dictionary<string, object> empty = new() { ["quests"] = new Dictionary<string, object>(), ["0"] = 1 };
                return Ok(PhpSerialization.Serialize(empty));
            }

            // Dedicated game servers ONLY receive native engine triggers (EAchievementTrigger 1..18)
            Dictionary<string, object?> payload = await warEffortService
                .BuildDedicatedServerPlayerQuestsPayloadAsync(accountIds, cancellationToken)
                .ConfigureAwait(false);
            payload["0"] = 1;

            return Ok(PhpSerialization.Serialize(payload));
        }
        catch (Exception ex)
        {
            logger.LogGetPlayerQuestsError(ex);
            return StatusCode(500, "Internal Server Error");
        }
    }

    [HttpPost("resetquest")]
    public async Task<IActionResult> ResetQuest(CancellationToken cancellationToken = default)
    {
        try
        {
            string? cookie = await ExtractFormParameterSafelyAsync("resetquest", "cookie", cancellationToken);
            string? questIdRaw = await ExtractFormParameterSafelyAsync("resetquest", "quest_id", cancellationToken)
                                 ?? await ExtractFormParameterSafelyAsync("resetquest", "questid", cancellationToken);

            int? accountId = await ResolveAuthenticatedAccountIdAsync(cookie, "resetquest", cancellationToken);
            if (!accountId.HasValue)
            {
                logger.LogWarning("[WAR_EFFORT] ResetQuest denied: unauthenticated cookie.");
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "session_not_found"
                }));
            }

            if (!int.TryParse(questIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int questId) || questId <= 0)
            {
                logger.LogWarning("[WAR_EFFORT] ResetQuest invalid quest_id='{QuestIdRaw}' for AccountId={AccountId}", questIdRaw, accountId.Value);
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "invalid_quest_id"
                }));
            }

            if (warEffortService is null || !warEffortService.IsEnabled)
            {
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "disabled"
                }));
            }

            Dictionary<string, object?> result = await warEffortService
                .ResetQuestAsync(accountId.Value, questId, cancellationToken)
                .ConfigureAwait(false);
            result["0"] = 1;

            return Ok(PhpSerialization.Serialize(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WAR_EFFORT] Exception processing resetquest.");
            return StatusCode(500, "Internal Server Error");
        }
    }

    [HttpPost("openbag")]
    public async Task<IActionResult> OpenBag(CancellationToken cancellationToken = default)
    {
        try
        {
            string? cookie = await ExtractFormParameterSafelyAsync("openbag", "cookie", cancellationToken);
            string? bagIdRaw = await ExtractFormParameterSafelyAsync("openbag", "bag_id", cancellationToken)
                               ?? await ExtractFormParameterSafelyAsync("openbag", "bagid", cancellationToken);

            int? accountId = await ResolveAuthenticatedAccountIdAsync(cookie, "openbag", cancellationToken);
            if (!accountId.HasValue)
            {
                logger.LogWarning("[WAR_EFFORT] OpenBag denied: unauthenticated cookie.");
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "session_not_found"
                }));
            }

            if (!int.TryParse(bagIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bagId) || bagId <= 0)
            {
                logger.LogWarning("[WAR_EFFORT] OpenBag invalid bag_id='{BagIdRaw}' for AccountId={AccountId}", bagIdRaw, accountId.Value);
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "invalid_bag"
                }));
            }

            if (warEffortService is null || !warEffortService.IsEnabled)
            {
                return Ok(PhpSerialization.Serialize(new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "disabled"
                }));
            }

            Dictionary<string, object?> result = await warEffortService
                .OpenBagAsync(accountId.Value, bagId, cancellationToken)
                .ConfigureAwait(false);
            result["0"] = 1;

            return Ok(PhpSerialization.Serialize(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WAR_EFFORT] Exception processing openbag.");
            return StatusCode(500, "Internal Server Error");
        }
    }

    private async Task<int?> ResolveAuthenticatedAccountIdAsync(
        string? cookie,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Any(char.IsControl))
        {
            return null;
        }

        bool isValid = await ValidateSessionOrMatchServerCookieAsync(cookie, endpoint, cancellationToken);
        if (!isValid)
        {
            logger.LogUnauthorizedAccess(SecurityMasking.MaskToken(cookie));
            return null;
        }

        string? accountName = await SessionRepository.GetAccountNameForSessionCookieAsync(cookie, cancellationToken);
        if (string.IsNullOrWhiteSpace(accountName) || accountRepository is null)
        {
            return null;
        }

        Account? account = await accountRepository.GetByNicknameAsync(accountName, cancellationToken);
        return account?.ID;
    }

    private async Task<bool> ValidateSessionOrMatchServerCookieAsync(
        string cookie,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Any(char.IsControl))
        {
            return false;
        }

        string? accountName = await SessionRepository.GetAccountNameForSessionCookieAsync(cookie, cancellationToken);
        bool isPlayerValid = accountName != null;

        if (endpoint == "getcurrentquests")
        {
            logger.LogCookieValidationResult(isPlayerValid, accountName);
        }

        if (isPlayerValid)
        {
            return true;
        }

        if (distributedCache != null)
        {
            MatchServer? matchServer = await distributedCache.GetMatchServerBySessionCookie(cookie)
                                    ?? await distributedCache.ResurrectMatchServerBySession(cookie);
            if (matchServer != null)
            {
                logger.LogQuestMatchServerValidated(SecurityMasking.MaskToken(cookie), matchServer.ID, endpoint);
                return true;
            }
        }

        return false;
    }

    private async Task<string?> ExtractFormParameterSafelyAsync(
        string endpoint,
        string key,
        CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            if (string.Equals(key, "cookie", StringComparison.OrdinalIgnoreCase))
            {
                return Request.GetCookie();
            }
            return Request.Query.TryGetValue(key, out var qv) ? qv.FirstOrDefault() : null;
        }

        Request.EnableBuffering();
        if (Request.Body.CanSeek)
        {
            Request.Body.Position = 0;
        }

        try
        {
            IFormCollection form = await Request.ReadFormAsync(cancellationToken);
            if (form.TryGetValue(key, out var values))
            {
                string? val = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(val))
                {
                    return val;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            logger.LogQuestMalformedFormFallback(endpoint, ex.Message);

            if (Request.Body.CanSeek)
            {
                Request.Body.Position = 0;
                using StreamReader reader = new(Request.Body, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string rawBody = await reader.ReadToEndAsync(cancellationToken);
                Request.Body.Position = 0;

                string? recovered = TryExtractRawFormParameter(rawBody, key);
                if (!string.IsNullOrEmpty(recovered))
                {
                    return recovered;
                }
            }
        }

        return string.Equals(key, "cookie", StringComparison.OrdinalIgnoreCase)
            ? Request.GetCookie()
            : (Request.Query.TryGetValue(key, out var fallbackQv) ? fallbackQv.FirstOrDefault() : null);
    }

    private async Task<List<int>> ExtractAccountIdsSafelyAsync(string endpoint, CancellationToken cancellationToken)
    {
        List<int> ids = [];
        if (!Request.HasFormContentType)
        {
            return ids;
        }

        Request.EnableBuffering();
        if (Request.Body.CanSeek)
        {
            Request.Body.Position = 0;
        }

        try
        {
            IFormCollection form = await Request.ReadFormAsync(cancellationToken);
            foreach (var kvp in form)
            {
                if (!string.Equals(kvp.Key, "account_ids[]", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kvp.Key, "account_ids", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kvp.Key, "account_id", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kvp.Key, "aid", StringComparison.OrdinalIgnoreCase) &&
                    !kvp.Key.StartsWith("account_ids[", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string? raw in kvp.Value)
                {
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            logger.LogQuestMalformedFormFallback(endpoint, ex.Message);
        }

        return ids.Distinct().ToList();
    }

    private static string? TryExtractRawFormParameter(string rawBody, string key)
    {
        if (string.IsNullOrEmpty(rawBody))
        {
            return null;
        }

        foreach (string pair in rawBody.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            string paramName = pair[..eqIdx];
            if (!string.Equals(paramName, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawVal = pair[(eqIdx + 1)..];
            try
            {
                return Uri.UnescapeDataString(rawVal.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return rawVal;
            }
        }

        return null;
    }
}
