// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class PlayerStatsApiClient(HttpClient client, ILogger<PlayerStatsApiClient> logger) : IPlayerStatsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<PlayerProfileSummaryDto?> GetPlayerProfileAsync(string ign, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<PlayerProfileSummaryDto>(
                $"api/stats/player/{Uri.EscapeDataString(ign)}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting player profile for '{IGN}'", ign);
            return null;
        }
    }

    public async Task<AccountStatisticsDto?> GetAccountStatisticsAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<AccountStatisticsDto>($"api/stats/account/{accountId}", JsonOptions,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting account stats for #{AccountId}", accountId);
            return null;
        }
    }

    public async Task<IReadOnlyList<AccountStatisticsDto>> GetTopRankedAccountsAsync(int count = 100,
        CancellationToken ct = default)
    {
        try
        {
            List<AccountStatisticsDto>? stats =
                await client.GetFromJsonAsync<List<AccountStatisticsDto>>("api/stats/top-ranked", JsonOptions, ct);
            return stats ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting top ranked accounts");
            return [];
        }
    }

    public async Task<LeaderboardResultDto> GetLeaderboardAsync(int page = 1, int pageSize = 50, string? search = null,
        string sort = "winrate", string? mode = null, string? map = null, CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/stats/leaderboard?page={page}&pageSize={pageSize}&search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort)}&mode={Uri.EscapeDataString(mode ?? "")}&map={Uri.EscapeDataString(map ?? "")}";
            logger.LogInformation("[PlayerStatsApiClient] Fetching leaderboard from {Url}", url);
            LeaderboardResultDto? result = await client.GetFromJsonAsync<LeaderboardResultDto>(url, JsonOptions, ct);
            logger.LogInformation("[PlayerStatsApiClient] Fetched leaderboard: {Count} entries, total {TotalCount}",
                result?.Entries?.Count ?? 0, result?.TotalCount ?? 0);
            return result ?? new LeaderboardResultDto([], 0, page, 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting leaderboard (mode: '{Mode}', map: '{Map}')", mode, map);
            return new LeaderboardResultDto([], 0, page, 1);
        }
    }

    public async Task<IReadOnlyList<HostLeaderboardEntryDto>> GetHostLeaderboardAsync(CancellationToken ct = default)
    {
        try
        {
            List<HostLeaderboardEntryDto>? list =
                await client.GetFromJsonAsync<List<HostLeaderboardEntryDto>>("api/stats/leaderboard/hosts", JsonOptions,
                    ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting host leaderboard");
            return [];
        }
    }

    public async Task<IReadOnlyList<MatchStatisticsDto>> GetRecentMatchesAsync(int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            List<MatchStatisticsDto>? matches =
                await client.GetFromJsonAsync<List<MatchStatisticsDto>>($"api/stats/matches/recent?limit={limit}",
                    JsonOptions, ct);
            return matches ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting recent matches");
            return [];
        }
    }

    public async Task<IReadOnlyList<MatchStatisticsDto>> GetMatchesForAccountAsync(int accountId, int limit = 20,
        CancellationToken ct = default)
    {
        try
        {
            List<MatchStatisticsDto>? matches =
                await client.GetFromJsonAsync<List<MatchStatisticsDto>>(
                    $"api/stats/accounts/{accountId}/matches?limit={limit}", JsonOptions, ct);
            return matches ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting matches for account #{AccountId}", accountId);
            return [];
        }
    }

    public async Task<MatchStatisticsDto?> GetMatchByIdAsync(int matchId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<MatchStatisticsDto>($"api/stats/matches/{matchId}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting match #{MatchId}", matchId);
            return null;
        }
    }

    public async Task<IReadOnlyList<PlayerStatisticsDto>> GetPlayerStatisticsForMatchAsync(int matchId,
        CancellationToken ct = default)
    {
        try
        {
            List<PlayerStatisticsDto>? list =
                await client.GetFromJsonAsync<List<PlayerStatisticsDto>>($"api/stats/matches/{matchId}/players",
                    JsonOptions, ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting player stats for match #{MatchId}", matchId);
            return [];
        }
    }

    public async Task<IReadOnlyList<PlayerStatisticsDto>> GetRecentPlayerStatisticsAsync(int accountId, int limit = 20,
        CancellationToken ct = default)
    {
        try
        {
            List<PlayerStatisticsDto>? list =
                await client.GetFromJsonAsync<List<PlayerStatisticsDto>>(
                    $"api/stats/accounts/{accountId}/player-stats?limit={limit}", JsonOptions, ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting recent player stats for account #{AccountId}",
                accountId);
            return [];
        }
    }

    public async Task<IReadOnlyList<int>> GetSubmittedMatchIdsAsync(IEnumerable<int> matchIds,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync("api/stats/matches/submitted-ids", matchIds, JsonOptions, ct);
            if (resp.IsSuccessStatusCode)
            {
                List<int>? ids = await resp.Content.ReadFromJsonAsync<List<int>>(JsonOptions, ct);
                return ids ?? [];
            }

            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting submitted match IDs");
            return [];
        }
    }

    public async Task<MatchEvidenceDto?> GetMatchEvidenceAsync(int matchId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<MatchEvidenceDto>($"api/stats/matches/{matchId}/evidence", JsonOptions,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting match evidence for match #{MatchId}", matchId);
            return null;
        }
    }

    public async Task<bool> SaveMatchEvidenceAsync(MatchEvidenceDto evidence, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/stats/matches/{evidence.MatchID}/evidence",
                evidence, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error saving match evidence for match #{MatchId}",
                evidence.MatchID);
            return false;
        }
    }

    public async Task<IReadOnlyList<ServerHostDto>> GetServerHostsAsync(CancellationToken ct = default)
    {
        try
        {
            List<ServerHostDto>? hosts =
                await client.GetFromJsonAsync<List<ServerHostDto>>("api/stats/hosts", JsonOptions, ct);
            return hosts ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting server hosts");
            return [];
        }
    }

    public async Task<IReadOnlyList<HeroDetailDto>> GetAllHeroDetailsAsync(CancellationToken ct = default)
    {
        try
        {
            List<HeroDetailDto>? list =
                await client.GetFromJsonAsync<List<HeroDetailDto>>("api/stats/heroes", JsonOptions, ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting all hero details");
            return [];
        }
    }

    public async Task<HeroDetailDto?> GetHeroDetailAsync(string heroId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<HeroDetailDto>($"api/stats/heroes/{Uri.EscapeDataString(heroId)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting hero detail for '{HeroId}'", heroId);
            return null;
        }
    }

    public async Task<IReadOnlyList<HeroItemDto>> GetAllHeroItemsAsync(CancellationToken ct = default)
    {
        try
        {
            List<HeroItemDto>? list =
                await client.GetFromJsonAsync<List<HeroItemDto>>("api/stats/items", JsonOptions, ct);
            return list ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting all hero items");
            return [];
        }
    }

    public async Task<bool> SubmitPlayerReportAsync(PlayerReportDto report, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/stats/reports", report, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error submitting player report");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> GetApprovedStreamerLoginsAsync(CancellationToken ct = default)
    {
        try
        {
            List<string>? streamers =
                await client.GetFromJsonAsync<List<string>>("api/stats/streamers/approved", JsonOptions, ct);
            return streamers ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting approved streamer logins");
            return [];
        }
    }

    public async Task<LiveTelemetryDataDto> GetLiveTelemetryAsync(CancellationToken ct = default)
    {
        try
        {
            LiveTelemetryDataDto? data =
                await client.GetFromJsonAsync<LiveTelemetryDataDto>("api/stats/telemetry/live", JsonOptions, ct);
            return data ?? new LiveTelemetryDataDto(0, 0, 1, ["US East"], TimeProvider.System.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting live telemetry");
            return new LiveTelemetryDataDto(0, 0, 1, ["US East"], TimeProvider.System.GetUtcNow());
        }
    }

    public async Task<IReadOnlyList<AccountDto>> SearchAccountsAsync(string? query, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/stats/accounts/search?query={Uri.EscapeDataString(query ?? "")}&page={page}&pageSize={pageSize}";
            List<AccountDto>? accounts = await client.GetFromJsonAsync<List<AccountDto>>(url, JsonOptions, ct);
            return accounts ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error searching accounts");
            return [];
        }
    }

    public async Task<AccountDto?> GetAccountByIgnAsync(string ign, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<AccountDto>($"api/stats/accounts/by-ign/{Uri.EscapeDataString(ign)}",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting account by IGN '{IGN}'", ign);
            return null;
        }
    }

    public async Task<int> GetTotalMatchesCountAsync(CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<int>("api/stats/matches/count", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting total matches count");
            return 0;
        }
    }

    public async Task<NEXUS.Contracts.DTOs.ClanDirectoryResultDto> GetClanDirectoryAsync(
        int page = 1,
        int pageSize = 24,
        string? search = null,
        string sort = "mmr",
        CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/stats/clans?page={page}&pageSize={pageSize}&search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort)}";
            logger.LogInformation("[PlayerStatsApiClient] Fetching clan directory from {Url}", url);
            NEXUS.Contracts.DTOs.ClanDirectoryResultDto? result =
                await client.GetFromJsonAsync<NEXUS.Contracts.DTOs.ClanDirectoryResultDto>(url, JsonOptions, ct);
            logger.LogInformation("[PlayerStatsApiClient] Fetched clan directory: {Count} items, total {TotalCount}",
                result?.Items?.Count ?? 0, result?.TotalCount ?? 0);
            return result ?? new NEXUS.Contracts.DTOs.ClanDirectoryResultDto([], 0, page, 1, 0, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlayerStatsApiClient] Error getting clan directory (page={Page}, sort='{Sort}')", page, sort);
            return new NEXUS.Contracts.DTOs.ClanDirectoryResultDto([], 0, page, 1, 0, null);
        }
    }

    public async Task<NEXUS.Contracts.DTOs.ClanDetailDto?> GetClanDetailAsync(int clanId, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[PlayerStatsApiClient] Fetching clan detail for Clan #{ClanId}", clanId);
            return await client.GetFromJsonAsync<NEXUS.Contracts.DTOs.ClanDetailDto>(
                $"api/stats/clans/{clanId}", JsonOptions, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("[PlayerStatsApiClient] Clan #{ClanId} not found (404)", clanId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayerStatsApiClient] Error getting clan detail for Clan #{ClanId}", clanId);
            return null;
        }
    }
}
