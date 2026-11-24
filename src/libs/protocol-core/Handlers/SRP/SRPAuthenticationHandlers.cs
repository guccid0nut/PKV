// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Handlers.SRP;

// ReSharper disable once InconsistentNaming
public static class SRPAuthenticationHandlers
{
    private const string ChatServerAuthenticationSalt =
        "8roespiemlasToUmiuglEhOaMiaSWlesplUcOAniupr2esPOeBRiudOEphiutOuJ";

    public static string ComputeChatServerCookieHash(int accountId, string remoteIpAddress, string cookie)
    {
        string chatServerCookie = accountId + remoteIpAddress + cookie + ChatServerAuthenticationSalt;

        // The uppercase hash (C# default) needs to be lowercased, to match the lowercase hash (C++ default) that the game client generates.
        // Note: Legacy HoN game client protocol requires SHA1.
#pragma warning disable CA5350
        string chatServerCookieHash =
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(chatServerCookie))).ToLowerInvariant();
#pragma warning restore CA5350

        return chatServerCookieHash;
    }

    // ReSharper disable once UnusedMember.Global
    public static string ComputeMatchServerChatAuthenticationHash(string key, string cookie)
    {
        // Note: Legacy HoN game client protocol requires SHA1.
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key + cookie + ChatServerAuthenticationSalt)))
            .ToLowerInvariant();
#pragma warning restore CA5350
    }
}
