// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StoreItemDetailViewModel
{
    public StaffStoreItemDto Item { get; set; } = new();
    public List<StaffStoreItemDto> RelatedItems { get; set; } = [];
    public bool IsAuthenticated { get; set; }
    public bool IsStaff { get; set; }
    public bool IsUnlockAllItemsEnabled { get; set; } = true;
    public int UserGold { get; set; }
    public int UserSilver { get; set; }
    public bool IsOwnedByUser { get; set; }
}
