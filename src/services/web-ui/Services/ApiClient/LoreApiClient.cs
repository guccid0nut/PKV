// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class LoreApiClient(HttpClient client, ILogger<LoreApiClient> logger) : ILoreApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<LoreArcDto>> GetArcsAsync(bool includeUnpublished = false,
        CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/arcs?includeUnpublished={includeUnpublished}";
            List<LoreArcDto>? result = await client.GetFromJsonAsync<List<LoreArcDto>>(url, JsonOptions, ct);
            return result ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching story arcs");
            return [];
        }
    }

    public async Task<LoreArcDto?> GetArcBySlugAsync(string slug, bool includeUnpublished = false,
        CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/arcs/{Uri.EscapeDataString(slug)}?includeUnpublished={includeUnpublished}";
            return await client.GetFromJsonAsync<LoreArcDto>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching arc '{Slug}'", slug);
            return null;
        }
    }

    public async Task<IReadOnlyList<LoreComicSummaryDto>> GetComicsAsync(int? arcId = null,
        bool includeUnpublished = false, CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/comics?includeUnpublished={includeUnpublished}";
            if (arcId.HasValue) url += $"&arcId={arcId.Value}";
            List<LoreComicSummaryDto>? result =
                await client.GetFromJsonAsync<List<LoreComicSummaryDto>>(url, JsonOptions, ct);
            return result ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching comic list");
            return [];
        }
    }

    public async Task<LoreComicDetailDto?> GetComicBySlugAsync(string slug, int? currentAccountId = null,
        bool isStaff = false, CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/comics/{Uri.EscapeDataString(slug)}?isStaff={isStaff}";
            if (currentAccountId.HasValue) url += $"&currentAccountId={currentAccountId.Value}";
            return await client.GetFromJsonAsync<LoreComicDetailDto>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching comic '{Slug}'", slug);
            return null;
        }
    }

    public async Task<LoreComicDetailDto?> GetComicByIdAsync(int id, int? currentAccountId = null, bool isStaff = false,
        CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/comics/{id}?isStaff={isStaff}";
            if (currentAccountId.HasValue) url += $"&currentAccountId={currentAccountId.Value}";
            return await client.GetFromJsonAsync<LoreComicDetailDto>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching comic #{Id}", id);
            return null;
        }
    }

    public async Task<bool> RateComicAsync(int comicId, int accountId, int score, CancellationToken ct = default)
    {
        try
        {
            RateComicRequestDto payload = new(comicId, accountId, score);
            HttpResponseMessage response =
                await client.PostAsJsonAsync($"api/lore/comics/{comicId}/rate", payload, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error rating comic #{ComicId}", comicId);
            return false;
        }
    }

    public async Task<int?> GetUserRatingAsync(int comicId, int accountId, CancellationToken ct = default)
    {
        try
        {
            string url = $"api/lore/comics/{comicId}/my-rating?accountId={accountId}";
            JsonElement response = await client.GetFromJsonAsync<JsonElement>(url, JsonOptions, ct);
            if (response.TryGetProperty("score", out JsonElement scoreElem) &&
                scoreElem.ValueKind == JsonValueKind.Number) return scoreElem.GetInt32();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error getting user rating for comic #{ComicId}", comicId);
            return null;
        }
    }

    public async Task<LoreRatingMetricDto?> GetStaffRatingMetricsAsync(int comicId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<LoreRatingMetricDto>($"api/lore/comics/{comicId}/staff-metrics",
                JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error fetching staff metrics for comic #{ComicId}", comicId);
            return null;
        }
    }

    public async Task<LoreComicSummaryDto?> SaveComicAsync(SaveComicRequestDto request, bool isStaff,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response =
                await client.PostAsJsonAsync($"api/lore/comics?isStaff={isStaff}", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<LoreComicSummaryDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error saving comic '{Title}'", request.Title);
            return null;
        }
    }

    public async Task<bool> DeleteComicAsync(int id, bool isStaff, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response = await client.DeleteAsync($"api/lore/comics/{id}?isStaff={isStaff}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error deleting comic #{Id}", id);
            return false;
        }
    }

    public async Task<LoreArcDto?> SaveArcAsync(SaveArcRequestDto request, bool isStaff, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response =
                await client.PostAsJsonAsync($"api/lore/arcs?isStaff={isStaff}", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<LoreArcDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error saving arc '{Title}'", request.Title);
            return null;
        }
    }

    public async Task<bool> DeleteArcAsync(int id, bool isStaff, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response = await client.DeleteAsync($"api/lore/arcs/{id}?isStaff={isStaff}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoreApiClient] Error deleting arc #{Id}", id);
            return false;
        }
    }
}
