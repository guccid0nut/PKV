// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Constants;

public static class RateLimiterPolicies
{
    public const string Relaxed = "Relaxed Rate Limit";
    public const string Strict = "Strict Rate Limit";
    public const string PerIpTokenBucket = "PerIpTokenBucket";
}
