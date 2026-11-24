// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace NEXUS.Contracts.Store;

/// <summary>
///     Store category identifiers used by the game client UI.
/// </summary>
public enum StoreCategory
{
    New = 01,
    HeroAvatars = 02,
    AccountIcons = 03,
    ChatSymbols = 04,
    Announcers = 05,
    Miscellaneous = 06,
    Bundles = 07,
    NameColors = 16,
    Taunt = 27,
    Couriers = 57,
    EarlyAccessHeroes = 58,
    FlagSymbols = 59,
    HeroSymbols = 60,
    MiscellaneousSymbols = 61,
    FeaturedHeroAvatars = 68,
    Heroes = 71,
    Wards = 74,
    Enhancements = 75,
    Creeps = 76,
    TauntBadges = 77,
    TeleportationEffects = 78,
    SelectionCircles = 79
}

/// <summary>
///     Popup codes returned to the game client UI to trigger modal dialogs.
/// </summary>
public enum StorePopupCode
{
    POP_UP_ERROR_MESSAGE = 1,
    POP_UP_COIN_PURCHASE_SUCCESS = 2,
    POP_UP_PRODUCT_PURCHASE_SUCCESS = 3,
    POP_UP_FIRST_VISIT = 4,
    POP_UP_COIN_PURCHASE_GIFT_SUCCESS = 5,
    POP_UP_COIN_REDEEM_SUCCESS = 6
}

/// <summary>
///     Request codes dispatched by the game client store requester.
/// </summary>
public enum StoreRequestCode
{
    LIST_STORE_ITEMS_REQUEST = 01,
    LIST_VAULT_ITEMS_REQUEST = 02,
    ATTEMPT_PURCHASE_COINS_REQUEST = 03,
    ATTEMPT_PURCHASE_PRODUCT_REQUEST = 04,
    LIST_POINT_PACKAGE_REQUEST = 05,
    BUNDLE_CONTENT_REQUEST = 06,
    LIST_PRODUCT_IDS_SELECTED_REQUEST = 07,
    BUY_PRODUCT_GAME_LOBBY_REQUEST = 08,
    GET_QUANTITY_FOR_PRODUCT_REQUEST = 09,
    REDEEM_CODE_REQUEST = 10
}

/// <summary>
///     Response codes returned to the game client store requester.
/// </summary>
public enum StoreResponseCode
{
    NO_RESPONSE = 0,
    BASIC_ITEM_LIST_RESPONSE = 1,
    VAULT_ITEM_LIST_RESPONSE = 2,
    ATTEMPT_PURCHASE_COINS_RESPONSE = 3,
    ATTEMPT_PURCHASE_PRODUCT_RESPONSE = 4,
    POINT_PACKAGE_RESPONSE = 5,
    VAULT_AVATAR_LIST_RESPONSE = 6,
    UPDATE_SELECTED_RESPONSE = 7
}

/// <summary>
///     Error codes returned to the game client store requester.
/// </summary>
public enum StoreErrorCode
{
    STORE_INVALID_REQUEST_ERROR = 01,
    STORE_ACCOUNT_INFORMATION_ERROR = 02,
    STORE_ACCOUNT_LOAD_ERROR = 03,
    STORE_SECURE_CONNECTION_ERROR = 04,
    STORE_ITEMS_ERROR = 05,
    STORE_LOAD_VAULT_ITEMS_ERROR = 06,
    STORE_LOAD_POINT_PACKAGE_ERROR = 07,
    STORE_POINT_PACKAGE_INVALID_ERROR = 08,
    PURCHASE_INVALID_CCARD_ERROR = 09,
    PURCHASE_CARD_ERROR = 10,
    PURCHASE_REGION_ERROR = 11,
    INVALID_PROMO_CODE_ERROR = 12,
    PURCHASE_TRANSACTION_DECLINED_ERROR = 13,
    INVOICE_SYSTEM_ERROR = 14,
    PURCHASE_SYSTEM_CONNECTION_ERROR = 15,
    PURCHASE_TRANSACTION_FAILED_ERROR = 16,
    PURCHASE_SYSTEM_DOWN_ERROR = 17,
    PURCHASE_DISABLED_ACCOUNT_ERROR = 18,
    INVOICE_SYSTEM_DISTRIBUTE_ERROR = 19,
    STORE_SESSION_ERROR = 20,
    STORE_PURCHASE_POINT_ERROR = 21,
    STORE_PURCHASE_ERROR = 22,
    STORE_PURCHASE_ITEM_ERROR = 23,
    STORE_PURCHASE_ITEM_MISSING_ERROR = 24,
    STORE_PURCHASE_SPECIFY_ITEM_ERROR = 25,
    STORE_PURCHASE_FRIEND_ERROR = 26,
    STORE_PURCHASE_BUNDLE_ID_ERROR = 27,
    STORE_PURCHASE_BUNDLE_NO_ITEMS_ERROR = 28,
    STORE_PURCHASE_BUNDLE_FETCH_ERROR = 29,
    STORE_PURCHASE_SELECT_UPGRADES_LIST_ERROR = 30,
    STORE_PURCHASE_RETRIEVE_ITEM_ERROR = 31,
    STORE_PURCHASE_LOAD_ITEM_ERROR = 32,
    STORE_PURCHASE_ALREADY_OWNED = 33,
    STORE_PURCHASE_MUST_UNLOCK_ERROR = 34,
    STORE_PURCHASE_OWNED_AMBIGUITY_ERROR = 35,
    STORE_PURCHASE_ICON_LOCKED_ERROR = 36,
    STORE_PURCHASE_LOAD_LIST_ERROR = 37,
    STORE_PURCHASE_DISABLED_ERROR = 38,
    STORE_PURCHASE_VIP_ONLY_ERROR = 39,
    STORE_PURCHASE_SANTA_OWNED_PRODUCT_ERROR = 40,
    STORE_PURCHASE_SANTA_TAUNT_LOCK_ERROR = 41,
    STORE_PURCHASE_SANTA_GIFT_NOT_RECEIVED_ERROR = 42,
    STORE_PURCHASE_SANTA_NO_MORE_QTY_ERROR = 43,
    STORE_PURCHASE_SANTA_PRODUCT_UNAVAILABLE_ERROR = 44,
    STORE_PURCHASE_CODE_USED_ERROR = 45,
    STORE_PURCHASE_CODE_USED_ONCE_ERROR = 46,
    STORE_PURCHASE_CODE_INVALID_ERROR = 47
}
