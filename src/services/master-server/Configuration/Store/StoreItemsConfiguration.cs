// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Configuration.Store;

public class StoreItemsConfiguration
{
    public StoreItemsConfiguration(List<StoreItem> storeItems)
    {
        StoreItems = storeItems;
        StoreItemsByID = new Dictionary<int, StoreItem>(storeItems.Count);
        StoreItemsByCode = new Dictionary<string, StoreItem>(storeItems.Count, StringComparer.OrdinalIgnoreCase);
        StoreItemsByPrefixedCode =
            new Dictionary<string, StoreItem>(storeItems.Count, StringComparer.OrdinalIgnoreCase);
        StoreItemsByName = new Dictionary<string, StoreItem>(storeItems.Count, StringComparer.OrdinalIgnoreCase);
        EnabledItemsByType = [];

        foreach (StoreItem item in storeItems)
        {
            StoreItemsByID[item.ID] = item;
            StoreItemsByCode.TryAdd(item.Code, item);
            StoreItemsByPrefixedCode.TryAdd(item.PrefixedCode, item);
            StoreItemsByName.TryAdd(item.Name, item);

            if (item.IsEnabled)
            {
                if (EnabledItemsByType.TryGetValue(item.StoreItemType, out List<StoreItem>? list).Equals(false) ||
                    list is null)
                {
                    list = [];
                    EnabledItemsByType[item.StoreItemType] = list;
                }

                list.Add(item);
            }
        }
    }

    public List<StoreItem> StoreItems { get; }

    private Dictionary<int, StoreItem> StoreItemsByID { get; }

    private Dictionary<string, StoreItem> StoreItemsByCode { get; }

    private Dictionary<string, StoreItem> StoreItemsByPrefixedCode { get; }

    private Dictionary<string, StoreItem> StoreItemsByName { get; }

    private Dictionary<StoreItemType, List<StoreItem>> EnabledItemsByType { get; }

    /// <summary>
    ///     Looks up a store item by its numeric ID.
    /// </summary>
    public StoreItem? GetByID(int id)
    {
        return StoreItemsByID.GetValueOrDefault(id);
    }

    /// <summary>
    ///     Looks up a store item by its unprefixed code (e.g. "Hero_Pyromancer.Female").
    /// </summary>
    public StoreItem? GetByCode(string code)
    {
        return StoreItemsByCode.GetValueOrDefault(code);
    }

    /// <summary>
    ///     Looks up a store item by its prefixed code (e.g. "aa.Hero_Pyromancer.Female").
    /// </summary>
    public StoreItem? GetByPrefixedCode(string prefixedCode)
    {
        return StoreItemsByPrefixedCode.GetValueOrDefault(prefixedCode);
    }

    /// <summary>
    ///     Looks up a store item by its display name (e.g. "Wolf Time! Taunt").
    /// </summary>
    public StoreItem? GetByName(string name)
    {
        return StoreItemsByName.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Returns all enabled store items whose <see cref="StoreItem.StoreItemType" /> matches the given type.
    /// </summary>
    public IEnumerable<StoreItem> GetEnabledItemsByType(StoreItemType type)
    {
        return EnabledItemsByType.GetValueOrDefault(type) ?? [];
    }
}

public class StoreItem
{
    public required int ID { get; init; }

    public required string Name { get; init; }

    public required string Code { get; init; }

    public required int Type { get; init; }

    public required string Resource { get; init; }

    public required bool Purchasable { get; init; }

    public required int GoldCost { get; init; }

    public required int SilverCost { get; init; }

    public required bool IsPremium { get; init; }

    public required bool IsBundle { get; init; }

    public required bool IsEnabled { get; init; }

    public required bool IsDynamic { get; init; }

    public required int[] Required { get; init; }

    /// <summary>
    ///     The strongly-typed store item type derived from the numeric <see cref="Type" /> field.
    /// </summary>
    public StoreItemType StoreItemType => (StoreItemType)Type;

    /// <summary>
    ///     The type code used to construct type prefixes (e.g. "aa", "cc", "t").
    /// </summary>
    public string TypeCode => GetTypeCode(StoreItemType);

    /// <summary>
    ///     The type prefix used to construct prefixed codes (e.g. "aa.", "cc.", "t.").
    /// </summary>
    public string Prefix => GetPrefix(StoreItemType);

    /// <summary>
    ///     The code prefixed with the type identifier (e.g. "aa.Hero_Pyromancer.Female").
    /// </summary>
    public string PrefixedCode => Prefix + Code;

    /// <summary>
    ///     Returns the type code (e.g. "aa", "cc", "t") for the given store item type.
    /// </summary>
    public static string GetTypeCode(StoreItemType type)
    {
        return type switch
        {
            StoreItemType.ChatNameColour => "cc",
            StoreItemType.ChatSymbol => "cs",
            StoreItemType.AccountIcon => "ai",
            StoreItemType.AlternativeAvatar => "aa",
            StoreItemType.AnnouncerVoice => "av",
            StoreItemType.Taunt => "t",
            StoreItemType.Courier => "c",
            StoreItemType.Hero => "h",
            StoreItemType.EarlyAccessProduct => "eap",
            StoreItemType.Status => "s",
            StoreItemType.Miscellaneous => "m",
            StoreItemType.Ward => "w",
            StoreItemType.Enhancement => "en",
            StoreItemType.Coupon => "cp",
            StoreItemType.Mastery => "ma",
            StoreItemType.Creep => "cr",
            StoreItemType.Building => "bu",
            StoreItemType.TauntBadge => "tb",
            StoreItemType.TeleportEffect => "te",
            StoreItemType.SelectionCircle => "sc",
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Returns the type prefix (e.g. "aa.", "cc.", "t.") for the given store item type.
    /// </summary>
    public static string GetPrefix(StoreItemType type)
    {
        string code = GetTypeCode(type);

        return code == string.Empty ? code : code + ".";
    }

    /// <summary>
    ///     Returns the long-form type name (e.g. "Alternative Avatar", "Chat Name Colour", "Taunt") for the given store item
    ///     type.
    /// </summary>
    public static string GetTypeName(StoreItemType type)
    {
        return type switch
        {
            // INFO: These Values Must Match The Keys Of The "ProductTypeToPrefix" Table In "global_main.lua" Exactly, Because They Are Verbatim Client-Side Dictionary Keys
            // INFO: A Mismatch Causes The Chest Reward UI To Render The Unresolved "general_product_type_" Localisation Key As Raw Text
            // INFO: So Updating These Values Should Be Done Alongside A Corresponding Update To The "ProductTypeToPrefix" Table In "global_main.lua" In The Client/Server Resource Files

            StoreItemType.ChatNameColour => "Chat Color",
            StoreItemType.ChatSymbol => "Chat Symbol",
            StoreItemType.AccountIcon => "Account Icon",
            StoreItemType.AlternativeAvatar => "Alt Avatar",
            StoreItemType.AnnouncerVoice => "Alt Announcement",
            StoreItemType.Taunt => "Taunt",
            StoreItemType.Courier => "Couriers",
            StoreItemType.Hero => "Hero",
            StoreItemType.EarlyAccessProduct => "EAP",
            StoreItemType.Status => "Status",
            StoreItemType.Miscellaneous => "Misc",
            StoreItemType.Ward => "Ward",
            StoreItemType.Enhancement => "Enhancement",
            StoreItemType.Bundle => "Bundle",
            _ => string.Empty
        };
    }
}

/// <summary>
///     Numeric types for store items, corresponding to the database "type" column in the products table.
/// </summary>
public enum StoreItemType
{
    ChatNameColour = 00,
    ChatSymbol = 01,
    AccountIcon = 02,
    AlternativeAvatar = 03,
    AnnouncerVoice = 04,
    Taunt = 05,
    Courier = 06,
    Hero = 07,
    EarlyAccessProduct = 08,
    Status = 09,
    Miscellaneous = 10,
    Ward = 11,
    Enhancement = 12,
    Coupon = 13,
    Mastery = 14,
    Creep = 15,
    Building = 16,
    TauntBadge = 17,
    TeleportEffect = 18,
    SelectionCircle = 19,
    Bundle = 20
}
