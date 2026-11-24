// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Models;

/// <summary>
///     Result Of A Complete Authentication Flow
/// </summary>
public sealed record JWTAuthenticationData(
    int UserID,
    string AccountName,
    string EmailAddress,
    string AuthenticationToken);
