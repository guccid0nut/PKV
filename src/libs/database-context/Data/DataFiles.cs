// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Data;

public static class DataFiles
{
    private static readonly string BasePath =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

    public static readonly string HeroGuides = Path.Combine(BasePath, "Data", "HeroGuides.json");
    public static readonly string HeroItems = Path.Combine(BasePath, "Data", "HeroItems.json");
    public static readonly string HeroDetails = Path.Combine(BasePath, "Data", "HeroDetails.json");
}
