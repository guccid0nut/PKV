// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Enumerations;

/// <summary>
///     Classifies the directory the application is running from according to whether it is a suitable place to perform
///     operations that modify its contents.
/// </summary>
public enum LocationSafetyVerdict
{
    Unverified,
    Safe,
    DevelopmentEnvironment,
    Unsafe
}
