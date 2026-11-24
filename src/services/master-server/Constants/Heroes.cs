// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

global using Heroes = ASPIRE.Common.Constants.Heroes;

namespace KONGOR.MasterServer.Constants;

public static class Heroes
{
    public static readonly int TotalHeroCount = ASPIRE.Common.Constants.Heroes.TotalHeroCount;

    public static List<string> AllHeroIdentifiers()
    {
        return ASPIRE.Common.Constants.Heroes.AllHeroIdentifiers();
    }

    public static string? GetNameByIdentifier(string identifier)
    {
        return ASPIRE.Common.Constants.Heroes.GetNameByIdentifier(identifier);
    }

    public static string? GetIdentifierByName(string name)
    {
        return ASPIRE.Common.Constants.Heroes.GetIdentifierByName(name);
    }

    public static string ResolveHeroDisplayName(string? heroIdentifier)
    {
        return ASPIRE.Common.Constants.Heroes.ResolveHeroDisplayName(heroIdentifier);
    }
}
