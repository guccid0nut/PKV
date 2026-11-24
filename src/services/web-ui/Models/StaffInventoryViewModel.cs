// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StaffInventoryViewModel
{
    public string AccountName { get; set; } = string.Empty;
    public int AccountId { get; set; }
    public List<string> OwnedItems { get; set; } = [];
    public List<StaffInventoryItemDto> OwnedItemDetails { get; set; } = [];
    public List<StaffStoreItemDto> CatalogItems { get; set; } = [];
    public List<string> RecentAccounts { get; set; } = [];

    public int TotalAvatarsOwned { get; set; }
    public int TotalAnnouncersOwned { get; set; }
    public int TotalTauntsOwned { get; set; }
    public int TotalIconsOwned { get; set; }
    public int TotalColorsOwned { get; set; }
    public int TotalWardsCouriersOwned { get; set; }

    public bool IsUnlockAllItemsEnabled { get; set; } = true;
    public int TotalCatalogItems { get; set; }

    public string Message { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public class StaffInventoryItemDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = "Cosmetic";
    public string Resource { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string AltAvatarSuffix { get; set; } = string.Empty;
    public bool CanPreview3D { get; set; }
}
