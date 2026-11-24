// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.EntityFrameworkCore.Design;

namespace MERRICK.DatabaseContext.Persistence;

public class DebugMerrickDbContextFactory : IDesignTimeDbContextFactory<MerrickContext>
{
    public MerrickContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();

        string connectionString = Environment.GetEnvironmentVariable("MERRICK_CONNECTION_STRING")
                                  ?? Environment.GetEnvironmentVariable("ConnectionStrings__MERRICK")
                                  ??
                                  "Server=(localdb)\\mssqllocaldb;Database=MerrickDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True;";

        optionsBuilder.UseSqlServer(connectionString);

        return new MerrickContext(optionsBuilder.Options);
    }
}
