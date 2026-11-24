// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ASPIRE.Common.Compatibility;

#pragma warning disable CA1707
// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global
public static class Client
{
    public static string GetVersion()
# pragma warning disable CS8520
        => HOTFIX_VERSION is 0
            ? $"{MAJOR_VERSION}.{MINOR_VERSION}.{MICRO_VERSION}"
            : $"{MAJOR_VERSION}.{MINOR_VERSION}.{MICRO_VERSION}.{HOTFIX_VERSION}";
# pragma warning restore CS8520

    # region The Client Version, As Defined By "shell_common.h"

    public const int MAJOR_VERSION = 5;
    public const int MINOR_VERSION = 0;
    public const int MICRO_VERSION = 0;
    public const int HOTFIX_VERSION = 0;

    # endregion
}
