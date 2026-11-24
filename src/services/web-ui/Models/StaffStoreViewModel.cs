// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StaffStoreViewModel
{
    public List<StaffStoreItemDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 32;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));

    public string? SelectedCategory { get; set; }
    public string? MainCategory { get; set; }
    public string? SubCategory { get; set; }
    public string? SearchQuery { get; set; }

    // Summary Statistics
    public int TotalAvatars { get; set; }
    public int TotalWards { get; set; }
    public int TotalTaunts { get; set; }
    public int TotalCouriers { get; set; }
    public int TotalAnnouncers { get; set; }
    public int TotalBundles { get; set; }
    public int TotalHeroes { get; set; }
    public int TotalIcons { get; set; }
    public int TotalUpgrades { get; set; }

    // User Session & Balance
    public bool IsAuthenticated { get; set; }
    public bool IsStaff { get; set; }
    public bool IsUnlockAllItemsEnabled { get; set; } = true;
    public int UserGold { get; set; }
    public int UserSilver { get; set; }
    public HashSet<string> UserOwnedItemCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UserEquippedItemCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string OwnershipFilter { get; set; } = "all";
    public int TotalOwnedInFilter { get; set; }
    public int TotalUnownedInFilter { get; set; }
    public int TotalEquippedInFilter { get; set; }
}

public class StaffStoreItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Type { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public int GoldCost { get; set; }
    public int SilverCost { get; set; }
    public bool Purchasable { get; set; }
    public bool IsPremium { get; set; }
    public bool IsBundle { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsOwnedByUser { get; set; }
    public bool IsEquippedByUser { get; set; }
    public string HeroName { get; set; } = string.Empty;
    public string AltAvatarSuffix { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool CanPreview3D => !IsBundle &&
                                (Type == 3 || Type == 12 ||
                                 (Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                                  Code.Contains('.'))) && Type != 1 && Type != 0 && Type != 2 && Type != 4 &&
                                Type != 5 && Type != 6 && Type != 7 && Type != 8 && Type != 10 && Type != 11 &&
                                Type != 20 && !string.IsNullOrEmpty(HeroName);
}
