// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class UserApiClient(HttpClient client, ILogger<UserApiClient> logger) : IUserApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<UserDto?> GetUserByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching user #{UserId}", id);
            return await client.GetFromJsonAsync<UserDto>($"api/users/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching user #{UserId}", id);
            return null;
        }
    }

    public async Task<UserDto?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching user by email '{Email}'", email);
            return await client.GetFromJsonAsync<UserDto>($"api/users/by-email?email={Uri.EscapeDataString(email)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching user by email '{Email}'", email);
            return null;
        }
    }

    public async Task<UserDto?> GetUserByDiscordIdAsync(string discordId, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching user by discord ID '{DiscordId}'", discordId);
            return await client.GetFromJsonAsync<UserDto>($"api/users/by-discord/{discordId}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching user by discord ID '{DiscordId}'", discordId);
            return null;
        }
    }

    public async Task<UserDto?> GetUserByAccountNameAsync(string accountName, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching user by account name '{AccountName}'", accountName);
            return await client.GetFromJsonAsync<UserDto>($"api/users/by-account/{Uri.EscapeDataString(accountName)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching user by account name '{AccountName}'", accountName);
            return null;
        }
    }

    public async Task<AccountDto?> GetAccountByNameAsync(string name, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching account by name '{Name}'", name);
            return await client.GetFromJsonAsync<AccountDto>($"api/users/accounts/by-name/{Uri.EscapeDataString(name)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching account by name '{Name}'", name);
            return null;
        }
    }

    public async Task<AccountDto?> GetAccountByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching account by ID #{AccountId}", id);
            return await client.GetFromJsonAsync<AccountDto>($"api/users/accounts/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching account by ID #{AccountId}", id);
            return null;
        }
    }

    public async Task<bool> ExistsAccountNameAsync(string name, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<bool>($"api/users/accounts/exists?name={Uri.EscapeDataString(name)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error checking if account name '{Name}' exists", name);
            return false;
        }
    }

    public async Task<bool> ExistsEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<bool>($"api/users/exists-email?email={Uri.EscapeDataString(email)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error checking if email '{Email}' exists", email);
            return false;
        }
    }

    public async Task<UserDto?> RegisterUserAsync(RegisterUserRequestDto payload, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Registering user with email '{Email}'", payload.EmailAddress);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/register", payload, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error registering user with email '{Email}'", payload.EmailAddress);
            return null;
        }
    }

    public async Task<AuthenticationTokenResponseDto?> LogInUserAsync(LogInUserRequestDto payload,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Logging in user '{Login}'", payload.Login);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/login", payload, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<AuthenticationTokenResponseDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error logging in user '{Login}'", payload.Login);
            return null;
        }
    }

    public async Task<UserDto?> RegisterOrLoginDiscordAsync(DiscordAuthRequestDto payload,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Registering or logging in Discord user '{DiscordId}'",
                payload.DiscordId);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/discord-auth", payload, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error in Discord auth for '{DiscordId}'", payload.DiscordId);
            return null;
        }
    }

    public async Task<bool> SetInGamePasswordAsync(int userId, string salt, string srpHash, string? pbkdf2Hash = null,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Setting in-game password for user #{UserId}", userId);
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/users/{userId}/in-game-password",
                new { Salt = salt, SrpHash = srpHash, Pbkdf2Hash = pbkdf2Hash }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error setting in-game password for user #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> LinkTwitchAsync(int userId, string twitchId, string twitchUsername,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Linking Twitch account '{TwitchUsername}' for user #{UserId}",
                twitchUsername, userId);
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/users/{userId}/link-twitch",
                new { TwitchId = twitchId, TwitchUsername = twitchUsername }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error linking Twitch for user #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> UnlinkTwitchAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Unlinking Twitch account for user #{UserId}", userId);
            HttpResponseMessage resp =
                await client.PostAsJsonAsync($"api/users/{userId}/unlink-twitch", new { }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error unlinking Twitch for user #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> ApplyHostAsync(ApplyHostRequestDto payload, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Submitting host application for prefix '{Prefix}'",
                payload.ServerNamePrefix);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/apply-host", payload, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error applying for host");
            return false;
        }
    }

    public async Task<bool> ApplyStreamerAsync(ApplyStreamerClientDto payload, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Submitting streamer application for user #{UserId}", payload.UserId);
            HttpResponseMessage resp =
                await client.PostAsJsonAsync("api/users/apply-streamer", payload, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error applying for streamer");
            return false;
        }
    }

    public async Task<StreamerApplicationDto?> GetStreamerApplicationAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching streamer application for user #{UserId}", userId);
            return await client.GetFromJsonAsync<StreamerApplicationDto>($"api/users/{userId}/streamer-app",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[UserApiClient] No streamer application found for user #{UserId}", userId);
            return null;
        }
    }

    public async Task<HostApplicationDto?> GetHostApplicationAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching host application for user #{UserId}", userId);
            return await client.GetFromJsonAsync<HostApplicationDto>($"api/users/{userId}/host-app", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[UserApiClient] No host application found for user #{UserId}", userId);
            return null;
        }
    }

    public async Task<IReadOnlyList<UserWebRewardDto>> GetUserWebRewardsAsync(int userId,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching web rewards for user #{UserId}", userId);
            List<UserWebRewardDto>? rewards =
                await client.GetFromJsonAsync<List<UserWebRewardDto>>($"api/users/{userId}/web-rewards", JsonOptions,
                    ct);
            return rewards ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching web rewards for user #{UserId}", userId);
            return [];
        }
    }

    public async Task<bool> EquipWebRewardAsync(int userId, string rewardType, string rewardCode,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation(
                "[UserApiClient] Equipping web reward '{RewardCode}' ({RewardType}) for user #{UserId}", rewardCode,
                rewardType, userId);
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/users/{userId}/equip-web-reward",
                new { RewardType = rewardType, RewardCode = rewardCode }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error equipping web reward for user #{UserId}", userId);
            return false;
        }
    }

    public async Task<HostTelemetryResultDto?> GetHostTelemetryAsync(string accountName, CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Fetching host telemetry for '{AccountName}'", accountName);
            return await client.GetFromJsonAsync<HostTelemetryResultDto>(
                $"api/users/accounts/{Uri.EscapeDataString(accountName)}/host-telemetry", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error fetching host telemetry for '{AccountName}'", accountName);
            return null;
        }
    }

    public async Task<TokenDto?> ValidateTokenAsync(string tokenString, TokenPurpose purpose,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogDebug("[UserApiClient] Validating token with purpose {Purpose}", purpose);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/tokens/validate",
                new { TokenString = tokenString, Purpose = (int)purpose }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<TokenDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error validating token");
            return null;
        }
    }

    public async Task<TokenDto> IssueTokenAsync(int userId, TokenPurpose purpose, TimeSpan ttl,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Issuing token for user #{UserId}, Purpose {Purpose}", userId,
                purpose);
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/tokens/issue",
                new { UserId = userId, Purpose = (int)purpose, TtlSeconds = (int)ttl.TotalSeconds }, JsonOptions, ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<TokenDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error issuing token for user #{UserId}", userId);
            throw;
        }
    }

    public async Task<bool> InvalidateTokenAsync(string tokenString, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[UserApiClient] Invalidating token");
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/users/tokens/invalidate",
                new { TokenString = tokenString }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error invalidating token");
            return false;
        }
    }

    public async Task<GenerateInviteResultDto?> GenerateInviteCodeAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/invites/generate?userId={userId}", null, ct);
            return await resp.Content.ReadFromJsonAsync<GenerateInviteResultDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error generating invite code for user #{UserId}", userId);
            return null;
        }
    }

    public async Task<GenerateInviteResultDto?> GetUserInviteQuotaAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<GenerateInviteResultDto>($"api/invites/quota/{userId}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error fetching invite quota for user #{UserId}", userId);
            return null;
        }
    }

    public async Task<IReadOnlyList<UserInviteDto>> GetMyInvitesAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            List<UserInviteDto>? list = await client.GetFromJsonAsync<List<UserInviteDto>>($"api/invites/my-invites/{userId}", JsonOptions, ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error fetching invites for user #{UserId}", userId);
            return [];
        }
    }

    public async Task<ValidateInviteResultDto> ValidateInviteCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            ValidateInviteResultDto? res = await client.GetFromJsonAsync<ValidateInviteResultDto>($"api/invites/validate?code={Uri.EscapeDataString(code)}", JsonOptions, ct);
            return res ?? new ValidateInviteResultDto(false, "Could not validate code.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserApiClient] Error validating invite code '{Code}'", code);
            return new ValidateInviteResultDto(false, "An error occurred while validating the invite code.");
        }
    }

    public async Task<bool> IsInviteOnlyModeEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await client.GetAsync("api/invites/mode", ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("isInviteOnlyEnabled", out var prop) && prop.GetBoolean();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserApiClient] Error querying invite-only mode.");
            return false;
        }
    }
}
