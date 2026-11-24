// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

public static class ConfigurationConstants
{
    // Master Server Address Configuration Keys
    public const string MasterServerAddressKey = "MasterServer:Address";
    public const string MasterServerAddressParameter = "master-server-address";
    public const string MasterServerAddressEnv = "MASTER_SERVER_ADDRESS";
    public const string MasterServerUrlEnv = "MASTER_SERVER_URL";

    // Infrastructure Gateway Configuration Keys
    public const string InfrastructureGatewayKey = "Infrastructure:Gateway";
    public const string InfrastructureGatewayEnv = "INFRASTRUCTURE_GATEWAY";
    public const string GatewayHostEnv = "GATEWAY_HOST";

    // Database Provider Configuration Keys
    public const string DatabaseProviderKey = "Database:Provider";
    public const string DatabaseProviderEnv = "DATABASE_PROVIDER";
    public const string DatabaseConnectionStringKey = "ConnectionStrings:MERRICK";
    public const string DatabaseConnectionStringEnv = "MERRICK_CONNECTION_STRING";

    public const string ProviderSqlServer = "SqlServer";
    public const string ProviderPostgreSql = "PostgreSQL";
    public const string ProviderSupabase = "Supabase";
    public const string ProviderSqlite = "Sqlite";

    // Supabase Configuration Keys
    public const string SupabaseUrlKey = "Supabase:Url";
    public const string SupabaseUrlEnv = "SUPABASE_URL";
    public const string SupabaseAnonKey = "Supabase:AnonKey";
    public const string SupabaseAnonKeyEnv = "SUPABASE_ANON_KEY";
    public const string SupabaseServiceRoleKey = "Supabase:ServiceRoleKey";
    public const string SupabaseServiceRoleKeyEnv = "SUPABASE_SERVICE_ROLE_KEY";
    public const string SupabaseDbConnectionStringKey = "Supabase:DatabaseConnectionString";
    public const string SupabaseDbConnectionStringEnv = "SUPABASE_DB_CONNECTION_STRING";
    public const string SupabaseStorageBucketKey = "Supabase:StorageBucket";
    public const string SupabaseStorageBucketEnv = "SUPABASE_STORAGE_BUCKET";
    public const string SupabaseS3AccessKey = "Supabase:S3AccessKey";
    public const string SupabaseS3AccessKeyEnv = "SUPABASE_S3_ACCESS_KEY";
    public const string SupabaseS3SecretKey = "Supabase:S3SecretKey";
    public const string SupabaseS3SecretKeyEnv = "SUPABASE_S3_SECRET_KEY";
    public const string SupabaseS3Region = "Supabase:S3Region";
    public const string SupabaseS3RegionEnv = "SUPABASE_S3_REGION";

    // Replay Storage Provider Values
    public const string StorageProviderLocal = "Local";
    public const string StorageProviderLocalDisk = "LocalDisk";
    public const string StorageProviderSupabase = "Supabase";
    public const string StorageProviderCloud = "Cloud";
    public const string StorageProviderS3 = "S3";

    // Defaults
    public const string DefaultMasterServerAddress = "http://localhost:55555";
    public const string DefaultInfrastructureGateway = "masterserver.newbs.io";
    public const string DefaultLocalhostGateway = "localhost";
    public const string DefaultSupabaseReplayBucket = "replays";
    public const string DefaultSupabaseEvidenceBucket = "evidence";
}
