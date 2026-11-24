// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions;
using MERRICK.DatabaseContext.Interceptors;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NEXUS.Contracts.Data;
using Npgsql;
using ConfigurationExtensions = ASPIRE.Common.Extensions.ConfigurationExtensions;

namespace MERRICK.DatabaseContext.Extensions;

public static class MerrickDatabaseExtensions
{
    /// <summary>
    ///     Adds and configures MerrickContext with multi-provider support (PostgreSQL/Supabase and SQL Server).
    ///     The provider is dynamically selected based on configuration (Database:Provider or connection strings).
    /// </summary>
    public static void AddMerrickDatabase(this IHostApplicationBuilder builder,
        string connectionName = "MERRICK",
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null)
    {
        IConfiguration configuration = builder.Configuration;
        bool isPostgres = configuration.IsPostgreSqlOrSupabase();

        if (isPostgres)
        {
            string? postgresConnStr = configuration[ConfigurationConstants.SupabaseDbConnectionStringKey]
                                      ?? configuration[ConfigurationConstants.SupabaseDbConnectionStringEnv]
                                      ?? configuration["Supabase:Database:ConnectionString"]
                                      ?? configuration["Database:PostgreSQL:ConnectionString"]
                                      ?? configuration["DATABASE_POSTGRESQL_CONNECTION_STRING"]
                                      ?? configuration["SUPABASE_DB_CONNECTION_STRING"]
                                      ?? configuration.GetConnectionString(connectionName);

            string effectiveConnStr =
                ConfigurationExtensions.NormalizePostgreSqlConnectionString(postgresConnStr ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(effectiveConnStr))
            {
                NpgsqlDataSourceBuilder dataSourceBuilder = new(effectiveConnStr);

                bool isPgBouncer = dataSourceBuilder.ConnectionStringBuilder.Port == 6543 ||
                                   dataSourceBuilder.ConnectionStringBuilder.NoResetOnClose ||
                                   string.Equals(configuration["Database:PostgreSQL:UsePgBouncer"], "true", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(configuration["SUPABASE_USE_PGBOUNCER"], "true", StringComparison.OrdinalIgnoreCase) ||
                                   postgresConnStr?.Contains("pgbouncer=true", StringComparison.OrdinalIgnoreCase) == true ||
                                   postgresConnStr?.Contains("pool_mode=transaction", StringComparison.OrdinalIgnoreCase) == true;

                bool isSupabaseSessionPooler = !isPgBouncer &&
                                               dataSourceBuilder.ConnectionStringBuilder.Port != 6543 &&
                                               dataSourceBuilder.ConnectionStringBuilder.Host?.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true;

                if (isPgBouncer)
                {
                    // PgBouncer Transaction Pooling Mode:
                    // 1. MaxPoolSize reduced to 25 per instance to prevent client virtual socket bloat across scaled nodes.
                    // 2. NoResetOnClose enabled to prevent DISCARD ALL roundtrips across recycled backend sockets.
                    dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 25;
                    dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 2;
                    dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
                    dataSourceBuilder.ConnectionStringBuilder.NoResetOnClose = true;
                }
                else if (isSupabaseSessionPooler)
                {
                    // Supabase Supavisor Session Mode (free/pro tier shared pool_size: 15 across 4 DB services):
                    // Keep 1 warm socket (4 * 1 = 4) with KeepAlive=30 to avoid cold TCP+TLS+SCRAM handshakes,
                    // while capping burst sockets at MaxPoolSize=3 (4 * 3 = 12 <= 15).
                    dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 3;
                    dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 1;
                    dataSourceBuilder.ConnectionStringBuilder.KeepAlive = 30;
                    dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
                    dataSourceBuilder.ConnectionStringBuilder.ConnectionPruningInterval = 10;
                }
                else
                {
                    // Direct PostgreSQL Defaults
                    dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 100;
                    dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 5;
                    dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 60;
                }

                NpgsqlDataSource dataSource = dataSourceBuilder.Build();

                // Replace any fallback NpgsqlDataSource registered by AddServiceDefaults() so only one pool exists per host.
                builder.Services.Replace(ServiceDescriptor.Singleton<NpgsqlDataSource>(_ => dataSource));

                builder.Services.AddDbContextPool<MerrickContext>(options =>
                {
                    options.UseNpgsql(dataSource, npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                        npgsqlOptions.EnableRetryOnFailure(3);
                    });

                    ConfigureCommonOptions(options, builder.Environment.IsDevelopment());
                    configureDbContextOptions?.Invoke(options);
                });
            }
            else
            {
                builder.Services.AddDbContextPool<MerrickContext>(options =>
                {
                    options.UseNpgsql(npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                        npgsqlOptions.EnableRetryOnFailure(3);
                    });

                    ConfigureCommonOptions(options, builder.Environment.IsDevelopment());
                    configureDbContextOptions?.Invoke(options);
                });
            }
        }
        else if (configuration.IsSqlite())
        {
            string sqliteConnStr = configuration.GetConnectionString(connectionName)
                                   ?? configuration["Database:Sqlite:ConnectionString"]
                                   ?? "Data Source=userdata/merrick_local.db";

            builder.Services.AddDbContextPool<MerrickContext>(options =>
            {
                options.UseSqlite(sqliteConnStr, sqliteOptions =>
                {
                    sqliteOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                    sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                });

                ConfigureCommonOptions(options, builder.Environment.IsDevelopment());
                configureDbContextOptions?.Invoke(options);
            });
        }
        else
        {
            Console.Error.WriteLine(
                "[DEPRECATED] [MERRICK] Database:Provider='SqlServer' is deprecated in Project KONGOR V; standardize on 'PostgreSQL' (Production/Docker/Aspire/Supabase) or 'Sqlite' (local/unit tests).");

            string? sqlConnStr = configuration.GetConnectionString(connectionName);

            builder.Services.AddDbContextPool<MerrickContext>(options =>
            {
                if (!string.IsNullOrWhiteSpace(sqlConnStr))
                    options.UseSqlServer(sqlConnStr, sqlServerOptions =>
                    {
                        sqlServerOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                        sqlServerOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                        sqlServerOptions.EnableRetryOnFailure(3);
                    });
                else
                    options.UseSqlServer(sqlServerOptions =>
                    {
                        sqlServerOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                        sqlServerOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                        sqlServerOptions.EnableRetryOnFailure(3);
                    });

                ConfigureCommonOptions(options, builder.Environment.IsDevelopment());
                configureDbContextOptions?.Invoke(options);
            });

            if (!string.IsNullOrWhiteSpace(sqlConnStr))
                builder.Services.AddHealthChecks()
                    .AddSqlServer(sqlConnStr, name: "merrick-db-sqlserver", tags: ["ready", "db"]);
        }

        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<DatabaseInitializer>());

        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>("merrick-db-initializer", tags: ["ready", "db"]);

        builder.Services.AddMerrickRepositories();
    }

    public static IServiceCollection AddMerrickRepositories(this IServiceCollection services)
    {
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IMatchHistoryRepository, MatchHistoryRepository>();
        services.AddScoped<ICustomMapStatsRepository, CustomMapStatsRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IClanRepository, ClanRepository>();
        services.AddScoped<IFriendshipRepository, FriendshipRepository>();
        services.AddScoped<IStoreRepository, StoreRepository>();
        services.AddScoped<IGuideRepository, HeroGuideRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddSingleton<SupabaseDuckDbLakehouseService>();
        services.AddScoped<IDuckDbAnalyticsService, DuckDbAnalyticsService>();
        return services;
    }

    private static void ConfigureCommonOptions(DbContextOptionsBuilder options, bool isDevelopment)
    {
        options.EnableDetailedErrors(isDevelopment);

        options.EnableSensitiveDataLogging(isDevelopment)
            .ConfigureWarnings(warnings =>
                warnings.Log((Id: CoreEventId.SensitiveDataLoggingEnabledWarning, Level: LogLevel.Trace))
                    .Ignore(RelationalEventId.PendingModelChangesWarning)
                    .Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));

        options.EnableThreadSafetyChecks();

        if (isDevelopment) options.AddInterceptors(new SlowQueryInterceptor());

        // Stream SQL warnings and connection failures to stdout
        options.LogTo(
            Console.WriteLine,
            [DbLoggerCategory.Database.Command.Name, DbLoggerCategory.Database.Connection.Name],
            LogLevel.Warning);
    }
}
