// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Helpers;

public static class InMemoryHelpers
{
    public static MerrickContext GetInMemoryMerrickContext(string? identifier = null)
    {
        DbContextOptionsBuilder<MerrickContext> builder = new();

        builder.UseInMemoryDatabase(identifier ?? Guid.CreateVersion7().ToString());

        DbContextOptions<MerrickContext> options = builder.Options;

        MerrickContext context = new(options);

        context.Database.EnsureCreated();

        return context;
    }
}
