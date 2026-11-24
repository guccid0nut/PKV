// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
/// <summary>
///     Canonical store item codes and defaults shared across the NEXUS ecosystem.
/// </summary>
public static class StoreDefaults
{
    /// <summary>
    ///     The default account icon item code ("ai.Default Icon").
    /// </summary>
    public const string DefaultAccountIcon = "ai.Default Icon";

    /// <summary>
    ///     The custom account icon item code ("ai.custom_icon:1").
    /// </summary>
    public const string CustomAccountIcon = "ai.custom_icon:1";

    /// <summary>
    ///     The default chat name color item code ("cc.white").
    /// </summary>
    public const string DefaultChatColor = "cc.white";

    /// <summary>
    ///     The default taunt item code ("t.Standard").
    /// </summary>
    public const string DefaultTaunt = "t.Standard";

    /// <summary>
    ///     The Gordon Ramsay announcer item code ("av.Ramsay").
    /// </summary>
    public const string RamsayAnnouncer = "av.Ramsay";

    /// <summary>
    ///     The Seductive ASMR announcer item code ("av.ASMR").
    /// </summary>
    public const string AsmrAnnouncer = "av.ASMR";

    /// <summary>
    ///     The Unreal Tournament announcer item code ("av.Unreal").
    /// </summary>
    public const string UnrealAnnouncer = "av.Unreal";

    /// <summary>
    ///     The GLaDOS AI announcer item code ("av.GLaDOS").
    /// </summary>
    public const string GladosAnnouncer = "av.GLaDOS";

    /// <summary>
    ///     The Rick and Morty announcer item code ("av.RickMorty").
    /// </summary>
    public const string RickMortyAnnouncer = "av.RickMorty";

    /// <summary>
    ///     Default store items owned by new user accounts.
    /// </summary>
    public static readonly string[] DefaultOwnedItems =
        [DefaultAccountIcon, CustomAccountIcon, DefaultChatColor, DefaultTaunt, RamsayAnnouncer, AsmrAnnouncer, UnrealAnnouncer, GladosAnnouncer, RickMortyAnnouncer];

    /// <summary>
    ///     Default store items selected by new accounts.
    /// </summary>
    public static readonly string[] DefaultSelectedItems = [CustomAccountIcon, DefaultChatColor, DefaultTaunt];
}
