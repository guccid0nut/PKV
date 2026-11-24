// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;

namespace KONGOR.MasterServer.Models.RequestResponse.GameData;

public partial class GuideListResponse
{
    public GuideListResponse(IEnumerable<HeroGuide> guides, Account? requestingAccount, int hostTime)
    {
        List<HeroGuide> guideList = [.. guides];

        // Find the single default guide ID for this hero:
        int defaultGuideId = -1;
        if (requestingAccount is { DefaultGuideIDs.Count: > 0 })
        {
            // Pick the single user chosen default guide for this hero if it exists in guideList
            HeroGuide? customDef = guideList.FirstOrDefault(g => requestingAccount.DefaultGuideIDs.Contains(g.ID));
            if (customDef != null) defaultGuideId = customDef.ID;
        }

        // If the user has not explicitly set a default guide for this hero, fallback to the featured guide or first guide
        if (defaultGuideId == -1)
        {
            HeroGuide? fallbackDef = guideList.FirstOrDefault(g => g.Featured) ?? guideList.FirstOrDefault();
            if (fallbackDef != null) defaultGuideId = fallbackDef.ID;
        }

        HashSet<int> favSet = requestingAccount?.FavoriteGuideIDs != null
            ? [.. requestingAccount.FavoriteGuideIDs]
            : [];

        IEnumerable<string> guidesForResponse = from guide in guideList
            // The Featured Guide Is Considered The Default Guide, So It Is Displayed Without An Author Name
            let authorName = guide.Featured ? string.Empty :
                guide.Author?.Clan is null ? guide.Author?.Name ?? "Anonymous" :
                $"[{guide.Author.Clan.Tag}]" + (guide.Author?.Name ?? "Anonymous")
            let cleanAuthor = authorName.Replace("'", "").Replace("`", "")
            let cleanGuideName = guide.Name.Replace("'", "").Replace("`", "")
            let isDef = guide.ID == defaultGuideId
            let isFav = favSet.Contains(guide.ID)
            let isYours = requestingAccount != null &&
                          ((guide.Author != null && guide.Author.ID == requestingAccount.ID) ||
                           string.Equals(guide.Author?.Name, requestingAccount.Name,
                               StringComparison.OrdinalIgnoreCase))
            let normalizedRating = guide.Rating <= 1.0f
                ? Math.Clamp(guide.Rating, 0.0f, 1.0f)
                : Math.Clamp(guide.Rating / 5.0f, 0.0f, 1.0f)
            let formattedRating = normalizedRating.ToString("0.00", CultureInfo.InvariantCulture)
            select new StringBuilder().Append($"{guide.ID}").Append('|')
                .Append((guide.TimestampLastUpdated ?? guide.TimestampCreated).ToString("dd MMMM yyyy HH:mm:ss"))
                .Append('|')
                .Append(cleanAuthor).Append('|')
                .Append(cleanGuideName).Append('|')
                .Append(isDef ? "is_def" : "not_def").Append('|')
                .Append(isFav ? "is_fav" : "not_fav").Append('|')
                .Append(formattedRating).Append('|')
                .Append(isYours ? "is_yours" : "not_yours").Append('|')
                .Append(guide.Featured ? 1.ToString() : 0.ToString())
            into guideStr
            select guideStr.ToString();

        GuideList = string.Join('`', guidesForResponse);
        HostTime = hostTime;
    }

    [PHPProperty("errors")] public string Errors => string.Empty;

    [PHPProperty("success")] public int Success => 1;

    [PHPProperty("guide_list")] public string GuideList { get; set; }

    [PHPProperty("hosttime")] public int HostTime { get; set; }

    [PHPProperty("vested_threshold")] public int VestedThreshold => 5;

    [PHPProperty(0)] public bool Zero => true;
}
