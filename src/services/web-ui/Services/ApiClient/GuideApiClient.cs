// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class GuideApiClient(HttpClient client, ILogger<GuideApiClient> logger) : IGuideApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<GuideSearchResultDto> GetGuidesAsync(GuideSearchQueryDto query, CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/guides?hero={Uri.EscapeDataString(query.Hero ?? "")}&map={Uri.EscapeDataString(query.Map ?? "")}&q={Uri.EscapeDataString(query.Query ?? "")}&sort={Uri.EscapeDataString(query.Sort ?? "featured")}&page={query.Page}&pageSize={query.PageSize}&currentAccountName={Uri.EscapeDataString(query.CurrentAccountName ?? "")}&isStaff={query.IsStaff}";
            GuideSearchResultDto? result = await client.GetFromJsonAsync<GuideSearchResultDto>(url, JsonOptions, ct);
            return result ?? new GuideSearchResultDto([], 0, 1, 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error searching guides");
            return new GuideSearchResultDto([], 0, 1, 1);
        }
    }

    public async Task<HeroGuideDto?> GetGuideByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<HeroGuideDto>($"api/guides/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GuideApiClient] Error fetching guide #{GuideId}", id);
            return null;
        }
    }

    public async Task<IReadOnlyList<HeroGuideDto>> GetGuidesForHeroAsync(string heroIdentifier,
        CancellationToken ct = default)
    {
        try
        {
            List<HeroGuideDto>? guides =
                await client.GetFromJsonAsync<List<HeroGuideDto>>(
                    $"api/guides/hero/{Uri.EscapeDataString(heroIdentifier)}", JsonOptions, ct);
            return guides ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GuideApiClient] Error fetching guides for hero '{Hero}'", heroIdentifier);
            return [];
        }
    }

    public async Task<SaveGuideResultDto> SaveGuideAsync(SaveHeroGuideRequestDto request, string authorAccountName,
        bool isStaff, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/guides/save",
                new { Request = request, AuthorAccountName = authorAccountName, IsStaff = isStaff }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return new SaveGuideResultDto(false, 0, "Failed to save guide");
            return (await resp.Content.ReadFromJsonAsync<SaveGuideResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error saving guide");
            return new SaveGuideResultDto(false, 0, ex.Message);
        }
    }

    public async Task<bool> DeleteGuideAsync(int id, string accountName, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/guides/{id}/delete",
                new { AccountName = accountName }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error deleting guide #{GuideId}", id);
            return false;
        }
    }

    public async Task<VoteGuideResultDto> VoteGuideAsync(int id, bool isUpvote, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/guides/{id}/vote?isUpvote={isUpvote}", null, ct);
            if (!resp.IsSuccessStatusCode) return new VoteGuideResultDto(false, 0, 0, 5.0f, "Vote failed");
            return (await resp.Content.ReadFromJsonAsync<VoteGuideResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error voting guide #{GuideId}", id);
            return new VoteGuideResultDto(false, 0, 0, 5.0f, ex.Message);
        }
    }

    public async Task<TogglePrivacyResultDto> TogglePrivacyAsync(int id, string accountName, bool isStaff,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/guides/{id}/toggle-privacy",
                new { AccountName = accountName, IsStaff = isStaff }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return new TogglePrivacyResultDto(false, false, "Toggle failed");
            return (await resp.Content.ReadFromJsonAsync<TogglePrivacyResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error toggling privacy for guide #{GuideId}", id);
            return new TogglePrivacyResultDto(false, false, ex.Message);
        }
    }

    public async Task<ToggleOfficialResultDto> ToggleOfficialAsync(int id, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/guides/{id}/toggle-official", null, ct);
            if (!resp.IsSuccessStatusCode) return new ToggleOfficialResultDto(false, false, "Toggle failed");
            return (await resp.Content.ReadFromJsonAsync<ToggleOfficialResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error toggling official status for guide #{GuideId}", id);
            return new ToggleOfficialResultDto(false, false, ex.Message);
        }
    }

    public async Task<ToggleDefaultGuideResultDto> ToggleDefaultGuideAsync(int id, string accountName,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/guides/{id}/toggle-default",
                new { AccountName = accountName }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode)
                return new ToggleDefaultGuideResultDto(false, false, id, null, "Toggle failed");
            return (await resp.Content.ReadFromJsonAsync<ToggleDefaultGuideResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error toggling default guide #{GuideId}", id);
            return new ToggleDefaultGuideResultDto(false, false, id, null, ex.Message);
        }
    }

    public async Task<ToggleFavoriteGuideResultDto> ToggleFavoriteGuideAsync(int id, string accountName,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/guides/{id}/toggle-favorite",
                new { AccountName = accountName }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return new ToggleFavoriteGuideResultDto(false, false, id, "Toggle failed");
            return (await resp.Content.ReadFromJsonAsync<ToggleFavoriteGuideResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error toggling favorite guide #{GuideId}", id);
            return new ToggleFavoriteGuideResultDto(false, false, id, ex.Message);
        }
    }

    public async Task<IReadOnlyList<HeroDifficultyVoteDto>> GetHeroDifficultyVotesAsync(string heroIdentifier,
        CancellationToken ct = default)
    {
        try
        {
            List<HeroDifficultyVoteDto>? votes = await client.GetFromJsonAsync<List<HeroDifficultyVoteDto>>(
                $"api/guides/hero/{Uri.EscapeDataString(heroIdentifier)}/difficulty-votes", JsonOptions, ct);
            return votes ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error getting difficulty votes for hero '{Hero}'", heroIdentifier);
            return [];
        }
    }

    public async Task<HeroDifficultyVoteResultDto> VoteHeroDifficultyAsync(string heroIdentifier, string ign,
        int difficulty, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/guides/hero/{Uri.EscapeDataString(heroIdentifier)}/vote-difficulty",
                new { Ign = ign, Difficulty = difficulty }, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode) return new HeroDifficultyVoteResultDto(false, 0, 0, "Vote failed");
            return (await resp.Content.ReadFromJsonAsync<HeroDifficultyVoteResultDto>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuideApiClient] Error voting hero difficulty for '{Hero}'", heroIdentifier);
            return new HeroDifficultyVoteResultDto(false, 0, 0, ex.Message);
        }
    }
}
