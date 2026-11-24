// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using ASPIRE.Common.Constants;
using ASPIRE.Common.Models.Supabase;

namespace ASPIRE.Common.Extensions;

public static class ConfigurationExtensions
{
    /// <summary>
    ///     Resolves the Master Server Address across User Secrets, configuration files, and environment variables.
    ///     Hierarchy:
    ///     1. "MasterServer:Address"
    ///     2. "master-server-address" (or "Parameters:master-server-address")
    ///     3. "MASTER_SERVER_ADDRESS"
    ///     4. "MASTER_SERVER_URL"
    ///     5. "Infrastructure:Gateway" / "INFRASTRUCTURE_GATEWAY"
    ///     6. Default: "http://localhost:55555"
    /// </summary>
    public static string GetMasterServerAddress(this IConfiguration configuration, string? fallback = null)
    {
        // 1. Direct Master Server Address in configuration (User Secrets / AppSettings / Environment)
        string? address = configuration[ConfigurationConstants.MasterServerAddressKey]
                          ?? configuration[ConfigurationConstants.MasterServerAddressParameter]
                          ?? configuration[$"Parameters:{ConfigurationConstants.MasterServerAddressParameter}"]
                          ?? configuration[ConfigurationConstants.MasterServerAddressEnv]
                          ?? configuration[ConfigurationConstants.MasterServerUrlEnv];

        if (!string.IsNullOrWhiteSpace(address)) return address.Trim();

        // 2. Infrastructure Gateway in configuration
        string? gateway = configuration[ConfigurationConstants.InfrastructureGatewayKey]
                          ?? configuration[ConfigurationConstants.InfrastructureGatewayEnv]
                          ?? configuration[ConfigurationConstants.GatewayHostEnv];

        if (!string.IsNullOrWhiteSpace(gateway))
        {
            string trimmedGateway = gateway.Trim();
            if (trimmedGateway.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmedGateway.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return trimmedGateway;

            return $"http://{trimmedGateway}";
        }

        return fallback ?? ConfigurationConstants.DefaultMasterServerAddress;
    }

    /// <summary>
    ///     Resolves the Infrastructure Gateway hostname (e.g. "api.kongor.net" or "localhost" or host/IP).
    /// </summary>
    public static string GetInfrastructureGateway(this IConfiguration configuration, string? fallback = null)
    {
        // 1. Direct Infrastructure Gateway in configuration
        string? gateway = configuration[ConfigurationConstants.InfrastructureGatewayKey];
        if (!string.IsNullOrWhiteSpace(gateway)) return NormalizeToHostOnly(gateway.Trim());

        // 2. Derive from Master Server Address in configuration
        string? address = configuration[ConfigurationConstants.MasterServerAddressKey]
                          ?? configuration[ConfigurationConstants.MasterServerAddressParameter]
                          ?? configuration[$"Parameters:{ConfigurationConstants.MasterServerAddressParameter}"]
                          ?? configuration[ConfigurationConstants.MasterServerAddressEnv]
                          ?? configuration[ConfigurationConstants.MasterServerUrlEnv];

        if (!string.IsNullOrWhiteSpace(address)) return NormalizeToHostOnly(address.Trim());

        // 3. Gateway from environment variables in configuration
        string? envGateway = configuration[ConfigurationConstants.InfrastructureGatewayEnv]
                             ?? configuration[ConfigurationConstants.GatewayHostEnv];

        if (!string.IsNullOrWhiteSpace(envGateway)) return NormalizeToHostOnly(envGateway.Trim());

        return fallback ?? ConfigurationConstants.DefaultLocalhostGateway;
    }

    /// <summary>
    ///     Resolves the active database provider ("PostgreSQL", "Supabase", "Sqlite", or legacy "SqlServer").
    /// </summary>
    public static string GetDatabaseProvider(this IConfiguration configuration,
        string defaultProvider = ConfigurationConstants.ProviderPostgreSql)
    {
        string? provider = configuration[ConfigurationConstants.DatabaseProviderKey]
                           ?? configuration[ConfigurationConstants.DatabaseProviderEnv];

        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase))
                return ConfigurationConstants.ProviderPostgreSql;

            if (provider.Equals("Supabase", StringComparison.OrdinalIgnoreCase))
                return ConfigurationConstants.ProviderSupabase;

            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                return ConfigurationConstants.ProviderSqlite;

            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("MSSQL", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Sql", StringComparison.OrdinalIgnoreCase))
                return ConfigurationConstants.ProviderSqlServer;

            return provider;
        }

        // Auto-detection based on connection string or Supabase URL
        string? connStr = configuration.GetConnectionString("MERRICK")
                          ?? configuration[ConfigurationConstants.DatabaseConnectionStringEnv]
                          ?? configuration[ConfigurationConstants.SupabaseDbConnectionStringKey]
                          ?? configuration[ConfigurationConstants.SupabaseDbConnectionStringEnv];

        if (!string.IsNullOrWhiteSpace(connStr))
        {
            if (connStr.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) &&
                (connStr.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
                 connStr.Contains(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                 connStr.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                 connStr.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)))
                return ConfigurationConstants.ProviderSqlite;

            if (connStr.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
                (connStr.Contains("Server=", StringComparison.OrdinalIgnoreCase) &&
                 connStr.Contains("Port=5432", StringComparison.OrdinalIgnoreCase)) ||
                connStr.Contains("Port=6543", StringComparison.OrdinalIgnoreCase) ||
                connStr.Contains("supabase.com", StringComparison.OrdinalIgnoreCase))
                return ConfigurationConstants.ProviderSupabase;
        }

        return defaultProvider;
    }

    /// <summary>
    ///     Checks whether the active database provider is SQLite.
    /// </summary>
    public static bool IsSqlite(this IConfiguration configuration)
    {
        string provider = configuration.GetDatabaseProvider();
        return provider.Equals(ConfigurationConstants.ProviderSqlite, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Checks whether the active database provider is PostgreSQL or Supabase.
    /// </summary>
    public static bool IsPostgreSqlOrSupabase(this IConfiguration configuration)
    {
        string provider = configuration.GetDatabaseProvider();
        return provider.Equals(ConfigurationConstants.ProviderPostgreSql, StringComparison.OrdinalIgnoreCase) ||
               provider.Equals(ConfigurationConstants.ProviderSupabase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolves strongly typed Supabase settings across configuration, user secrets, and environment variables.
    /// </summary>
    public static SupabaseSettings GetSupabaseSettings(this IConfiguration configuration)
    {
        SupabaseSettings settings = new();

        string? url = configuration[ConfigurationConstants.SupabaseUrlKey]
                      ?? configuration[ConfigurationConstants.SupabaseUrlEnv];
        if (!string.IsNullOrWhiteSpace(url)) settings.Url = url.Trim();

        string? anonKey = configuration[ConfigurationConstants.SupabaseAnonKey]
                          ?? configuration[ConfigurationConstants.SupabaseAnonKeyEnv];
        if (!string.IsNullOrWhiteSpace(anonKey)) settings.AnonKey = anonKey.Trim();

        string? serviceKey = configuration[ConfigurationConstants.SupabaseServiceRoleKey]
                             ?? configuration[ConfigurationConstants.SupabaseServiceRoleKeyEnv];
        if (!string.IsNullOrWhiteSpace(serviceKey)) settings.ServiceRoleKey = serviceKey.Trim();

        string? dbConn = configuration[ConfigurationConstants.SupabaseDbConnectionStringKey]
                         ?? configuration[ConfigurationConstants.SupabaseDbConnectionStringEnv];
        if (!string.IsNullOrWhiteSpace(dbConn)) settings.DatabaseConnectionString = dbConn.Trim();

        string? bucket = configuration[ConfigurationConstants.SupabaseStorageBucketKey]
                         ?? configuration[ConfigurationConstants.SupabaseStorageBucketEnv];
        if (!string.IsNullOrWhiteSpace(bucket)) settings.StorageBucket = bucket.Trim();

        string? s3Access = configuration[ConfigurationConstants.SupabaseS3AccessKey]
                           ?? configuration[ConfigurationConstants.SupabaseS3AccessKeyEnv];
        if (!string.IsNullOrWhiteSpace(s3Access)) settings.S3AccessKey = s3Access.Trim();

        string? s3Secret = configuration[ConfigurationConstants.SupabaseS3SecretKey]
                           ?? configuration[ConfigurationConstants.SupabaseS3SecretKeyEnv];
        if (!string.IsNullOrWhiteSpace(s3Secret)) settings.S3SecretKey = s3Secret.Trim();

        string? s3Region = configuration[ConfigurationConstants.SupabaseS3Region]
                           ?? configuration[ConfigurationConstants.SupabaseS3RegionEnv];
        if (!string.IsNullOrWhiteSpace(s3Region)) settings.S3Region = s3Region.Trim();

        return settings;
    }

    /// <summary>
    ///     Normalizes a PostgreSQL or Supabase connection string (URI or ADO.NET format) into an Npgsql-compatible format with
    ///     SSL.
    /// </summary>
    public static string NormalizePostgreSqlConnectionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
            {
                string host = uri.Host;
                int port = uri.Port > 0 ? uri.Port : 5432;
                bool isExplicitPgBouncer = trimmed.Contains("pgbouncer=true", StringComparison.OrdinalIgnoreCase) ||
                                           trimmed.Contains("pool_mode=transaction", StringComparison.OrdinalIgnoreCase);

                if (host.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase) && port == 6543 && !isExplicitPgBouncer)
                    // Unless explicit transaction mode is requested, port 5432 is Supabase's Session Pooler default
                    port = 5432;

                bool isSupabaseSessionPooler =
                    host.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase) &&
                    port != 6543 &&
                    !isExplicitPgBouncer;

                string database = uri.AbsolutePath.TrimStart('/');
                if (string.IsNullOrWhiteSpace(database)) database = "postgres";

                string username = "postgres";
                string password = "";

                if (!string.IsNullOrWhiteSpace(uri.UserInfo))
                {
                    string[] parts = uri.UserInfo.Split(':', 2);
                    username = Uri.UnescapeDataString(parts[0]);
                    if (parts.Length > 1) password = Uri.UnescapeDataString(parts[1]);
                }

                bool allowUntrustedServerCert = string.Equals(
                    Environment.GetEnvironmentVariable("KONGOR_DB_TRUST_SERVER_CERTIFICATE"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                string trustCertClause = allowUntrustedServerCert ? ";Trust Server Certificate=true" : string.Empty;
                string poolSettings = isSupabaseSessionPooler
                    ? "Minimum Pool Size=1;Connection Idle Lifetime=30;Connection Pruning Interval=10;Maximum Pool Size=3"
                    : "Connection Idle Lifetime=30;Maximum Pool Size=50";
                string pgbouncerSuffix = (port == 6543 || isExplicitPgBouncer) ? ";No Reset On Close=true" : string.Empty;
                return
                    $"Host={host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require{trustCertClause};Multiplexing=false;Pooling=true;Keepalive=30;{poolSettings}{pgbouncerSuffix};";
            }

        if (trimmed.Contains("supabase.co", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!trimmed.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.TrimEnd(';') + ";SSL Mode=Require;";

            if (!trimmed.Contains("Trust Server Certificate", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Environment.GetEnvironmentVariable("KONGOR_DB_TRUST_SERVER_CERTIFICATE"), "true", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.TrimEnd(';') + ";Trust Server Certificate=true;";

            if (!trimmed.Contains("Multiplexing", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.TrimEnd(';') + ";Multiplexing=false;";

            if (!trimmed.Contains("Keepalive", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.TrimEnd(';') + ";Keepalive=30;";

            bool isPgBouncer = trimmed.Contains("6543") ||
                               trimmed.Contains("pgbouncer=true", StringComparison.OrdinalIgnoreCase) ||
                               trimmed.Contains("pool_mode=transaction", StringComparison.OrdinalIgnoreCase);

            if (isPgBouncer && !trimmed.Contains("No Reset On Close", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.TrimEnd(';') + ";No Reset On Close=true;";
            }

            bool isSupabaseSessionPooler =
                !isPgBouncer &&
                trimmed.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase);

            if (isSupabaseSessionPooler)
            {
                if (!trimmed.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.TrimEnd(';') + ";Maximum Pool Size=3;";
                if (!trimmed.Contains("Minimum Pool Size", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.TrimEnd(';') + ";Minimum Pool Size=1;";
                if (!trimmed.Contains("Connection Idle Lifetime", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.TrimEnd(';') + ";Connection Idle Lifetime=30;";
                if (!trimmed.Contains("Connection Pruning Interval", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.TrimEnd(';') + ";Connection Pruning Interval=10;";
            }
        }

        // Cleanse non-ADO.NET PgBouncer / Supavisor connection flags that NpgsqlConnectionStringBuilder rejects
        if (trimmed.Contains("pool_mode", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = Regex.Replace(trimmed, @"\bpool_mode\s*=[^;]*;?", "", RegexOptions.IgnoreCase);
            trimmed = Regex.Replace(trimmed, @"\bpgbouncer\s*=[^;]*;?", "", RegexOptions.IgnoreCase);
        }

        return trimmed;
    }

    private static string NormalizeToHostOnly(string raw)
    {
        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ? uri.Authority : raw;
    }
}
