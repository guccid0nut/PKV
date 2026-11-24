// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using ASPIRE.Common.Hosting;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MERRICK.DatabaseContext.Tests")]

namespace MERRICK.DatabaseContext.Services;

public partial class DatabaseInitializer(IServiceProvider serviceProvider, ILogger<DatabaseInitializer> logger)
    : ResilientBackgroundService(logger, nameof(DatabaseInitializer))
{
    public const string ActivitySourceName = "Migrations";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);

    public bool IsCompleted { get; internal set; }
    public string? LastError { get; internal set; }

    internal void SetCompletedForTesting(bool completed = true, string? lastError = null)
    {
        IsCompleted = completed;
        LastError = lastError;
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = serviceProvider.CreateScope();

            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            await InitializeDatabaseAsync(context, stoppingToken);
        }
        catch (Exception ex)
        {
            try
            {
                LastError = ex.Message;
                logger.LogError(ex, "[DatabaseInitializer] Startup database initialization encountered an exception.");
            }
            catch (Exception logEx)
            {
                Console.Error.WriteLine($"[DatabaseInitializer] Critical logging failure: {logEx.Message}. Original exception: {ex.Message}");
            }
        }
    }

    private async Task InitializeDatabaseAsync(MerrickContext context, CancellationToken cancellationToken)
    {
        const string activityName = "Initializing MERRICK Database";

        bool isPostgres = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        string dbSystem = isPostgres ? "postgresql" : "mssql";

        using Activity? activity = _activitySource.StartActivity(activityName);
        if (activity != null)
        {
            activity.SetTag("db.system", dbSystem);
            activity.SetTag("db.name", "MERRICK");
            activity.SetTag("code.namespace", "MERRICK.DatabaseContext.Services");
            activity.SetTag("code.function", "InitializeDatabaseAsync");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        bool skippedDueToConcurrentInit = false;
        if (isPostgres)
        {
            logger.LogInformation("Initializing PostgreSQL / Supabase schema for MERRICK");

            await strategy.ExecuteAsync(async () =>
            {
                const long lockId = 742918235891234192L;
                bool lockAcquired = false;

                try
                {
                    try
                    {
                        await using DbCommand lockCmd = context.Database.GetDbConnection().CreateCommand();
                        lockCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId});";
                        if (context.Database.GetDbConnection().State != ConnectionState.Open)
                            await context.Database.OpenConnectionAsync(cancellationToken);

                        object? lockResult = await lockCmd.ExecuteScalarAsync(cancellationToken);
                        lockAcquired = lockResult is true or 1 or "t" or "true" or "True";
                    }
                    catch (Exception lockEx)
                    {
                        logger.LogDebug(lockEx, "Advisory lock check notice");
                        lockAcquired = true; // Fallback to proceed if advisory lock is unavailable
                    }

                    if (!lockAcquired)
                    {
                        skippedDueToConcurrentInit = true;
                        logger.LogInformation(
                            "Another service instance is currently executing DatabaseInitializer. Skipping concurrent initialization.");
                        return;
                    }

                    // 1. Check if tables and schema are already created
                    bool tablesExist = false;
                    try
                    {
                        await using DbCommand checkCmd = context.Database.GetDbConnection().CreateCommand();
                        checkCmd.CommandText =
                            "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'Users');";
                        if (context.Database.GetDbConnection().State != ConnectionState.Open)
                            await context.Database.OpenConnectionAsync(cancellationToken);

                        object? result = await checkCmd.ExecuteScalarAsync(cancellationToken);
                        tablesExist = result is true or 1 or "t" or "true" or "True";
                    }
                    catch (Exception checkEx)
                    {
                        logger.LogDebug(checkEx, "Table existence check notice");
                        tablesExist = false;
                    }

                    logger.LogInformation("Ensuring PostgreSQL schemas and extensions exist...");

                    // Ensure required custom schemas and PostgreSQL extensions exist in dedicated extensions schema
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync(@"
                        CREATE SCHEMA IF NOT EXISTS extensions;
                        CREATE SCHEMA IF NOT EXISTS meta;
                        CREATE SCHEMA IF NOT EXISTS data;
                        CREATE SCHEMA IF NOT EXISTS core;
                        CREATE SCHEMA IF NOT EXISTS misc;
                        CREATE SCHEMA IF NOT EXISTS stat;
                        CREATE SCHEMA IF NOT EXISTS nexus_auth;
                        SET search_path TO ""$user"", public, extensions;
                        CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                        CREATE EXTENSION IF NOT EXISTS ""pg_trgm"" WITH SCHEMA extensions;
                        CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"" WITH SCHEMA extensions;
                        CREATE EXTENSION IF NOT EXISTS ""pgcrypto"" WITH SCHEMA extensions;
                        DO $$
                        BEGIN
                            EXECUTE format('ALTER DATABASE %I SET search_path TO ""$user"", public, extensions', current_database());
                            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm' AND extnamespace = 'public'::regnamespace) THEN
                                ALTER EXTENSION pg_trgm SET SCHEMA extensions;
                            END IF;
                            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'uuid-ossp' AND extnamespace = 'public'::regnamespace) THEN
                                ALTER EXTENSION ""uuid-ossp"" SET SCHEMA extensions;
                            END IF;
                            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgcrypto' AND extnamespace = 'public'::regnamespace) THEN
                                ALTER EXTENSION pgcrypto SET SCHEMA extensions;
                            END IF;
                        EXCEPTION WHEN OTHERS THEN
                            NULL;
                        END $$;", cancellationToken);
                    }
                    catch (Exception extEx)
                    {
                        logger.LogWarning(extEx, "[DatabaseInitializer] Notice: Extension/schema verification encountered an exception, proceeding with table checks: {Message}", extEx.Message);
                    }

                    if (!tablesExist)
                    {
                        logger.LogInformation(
                            "First-time setup: Creating baseline schema and tables from EF Core Model...");

                        // Generate DDL creation script from EF Core model
                        string ddlScript = context.Database.GenerateCreateScript();
                        ddlScript = DoBlockRegex().Replace(ddlScript, string.Empty);
                        ddlScript = CreateTableRegex().Replace(ddlScript, "CREATE TABLE IF NOT EXISTS \"$1\".\"$2\"");
                        ddlScript = CreateIndexRegex().Replace(ddlScript, "CREATE $1INDEX IF NOT EXISTS \"$2\"");
                        ddlScript = CreateCollationRegex().Replace(ddlScript, "CREATE COLLATION IF NOT EXISTS $1");
                        ddlScript = InsertRegex()
                            .Replace(ddlScript, "INSERT INTO $1 VALUES $2 ON CONFLICT DO NOTHING;");
                        ddlScript = "SET search_path TO \"$user\", public, extensions;\n" + ddlScript;

                        try
                        {
#pragma warning disable EF1003
                            await context.Database.ExecuteSqlRawAsync(ddlScript, cancellationToken);
#pragma warning restore EF1003
                            logger.LogInformation("Successfully executed baseline DDL creation script in Supabase.");
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[DatabaseInitializer] Baseline DDL execution failed: {Message}", ex.Message);
                            throw;
                        }
                    }
                    else
                    {
                        logger.LogInformation(
                            "Existing schema detected. Running incremental DDL updates and security hardening...");
                    }

                    // 2. Comprehensive DDL for custom tables, columns, indexes, and constraints
                    logger.LogInformation("Executing DDL migrations for StoreItems and normalized tables...");

                    // 1. data.StoreItems
                    await context.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""data"".""StoreItems"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""Name"" character varying(255) NOT NULL,
                        ""Code"" character varying(255) NOT NULL,
                        ""UpgradeId"" integer NOT NULL DEFAULT 0,
                        ""UpgradeType"" integer NOT NULL DEFAULT 0,
                        ""LocalContent"" text NOT NULL DEFAULT '',
                        ""Purchasable"" boolean NOT NULL DEFAULT true,
                        ""Price"" integer NOT NULL DEFAULT 0,
                        ""PriceInMMP"" integer NOT NULL DEFAULT 0,
                        ""Premium"" boolean NOT NULL DEFAULT false,
                        ""Enabled"" boolean NOT NULL DEFAULT true,
                        ""IsBundle"" boolean NOT NULL DEFAULT false,
                        ""ProductsRequired"" text
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_StoreItems_Code"" ON ""data"".""StoreItems"" (""Code"");
                    CREATE INDEX IF NOT EXISTS ""IX_StoreItems_UpgradeId"" ON ""data"".""StoreItems"" (""UpgradeId"");
                    CREATE INDEX IF NOT EXISTS ""IX_StoreItems_Enabled_Purchasable"" ON ""data"".""StoreItems"" (""Enabled"", ""Purchasable"");",
                        cancellationToken);

                    // 2. core.EconomyTransactions
                    await context.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""core"".""EconomyTransactions"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""UserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE,
                        ""AccountID"" integer REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL,
                        ""GoldDelta"" integer NOT NULL DEFAULT 0,
                        ""SilverDelta"" integer NOT NULL DEFAULT 0,
                        ""PlinkoDelta"" integer NOT NULL DEFAULT 0,
                        ""GoldBalanceAfter"" integer NOT NULL DEFAULT 0,
                        ""SilverBalanceAfter"" integer NOT NULL DEFAULT 0,
                        ""PlinkoBalanceAfter"" integer NOT NULL DEFAULT 0,
                        ""TransactionType"" character varying(64) NOT NULL,
                        ""ReferenceId"" character varying(128),
                        ""IdempotencyKey"" character varying(128),
                        ""Description"" character varying(255),
                        ""Timestamp"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );", cancellationToken);

                    logger.LogInformation("Creating normalized tables and schema modifications...");
                    await context.Database.ExecuteSqlRawAsync(@"
                    -- 3. core.Friendships
                    CREATE TABLE IF NOT EXISTS ""core"".""Friendships"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE,
                        ""FriendAccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE RESTRICT,
                        ""FriendGroup"" character varying(32) NOT NULL DEFAULT 'Default',
                        ""Status"" integer NOT NULL DEFAULT 1,
                        ""TimestampCreated"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampUpdated"" timestamp with time zone,
                        CONSTRAINT ""UQ_Friendships_AccountID_FriendAccountID"" UNIQUE (""AccountID"", ""FriendAccountID"")
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_Friendships_AccountID"" ON ""core"".""Friendships"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_Friendships_FriendAccountID"" ON ""core"".""Friendships"" (""FriendAccountID"");

                    -- 4. core.UserInventory
                    CREATE TABLE IF NOT EXISTS ""core"".""UserInventory"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""UserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE,
                        ""AccountID"" integer REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL,
                        ""ItemCode"" character varying(255) NOT NULL,
                        ""StoreItemId"" integer REFERENCES ""data"".""StoreItems""(""Id"") ON DELETE SET NULL,
                        ""Quantity"" integer NOT NULL DEFAULT 1,
                        ""IsPermanent"" boolean NOT NULL DEFAULT true,
                        ""TimestampAcquired"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampExpires"" timestamp with time zone,
                        ""Source"" character varying(64) NOT NULL DEFAULT 'Store',
                        CONSTRAINT ""UQ_UserInventory_UserID_ItemCode"" UNIQUE (""UserID"", ""ItemCode"")
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_UserInventory_UserID"" ON ""core"".""UserInventory"" (""UserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInventory_AccountID"" ON ""core"".""UserInventory"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInventory_ItemCode"" ON ""core"".""UserInventory"" (""ItemCode"");

                    -- 5. core.AccountSessions
                    CREATE TABLE IF NOT EXISTS ""core"".""AccountSessions"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE,
                        ""UserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL,
                        ""IPAddress"" character varying(45) NOT NULL,
                        ""MACAddress"" character varying(64),
                        ""HWID"" character varying(128),
                        ""SystemInformation"" text,
                        ""OperatingSystem"" character varying(128),
                        ""Processor"" character varying(256),
                        ""VideoCard"" character varying(256),
                        ""RAM"" character varying(64),
                        ""ClientVersion"" character varying(128),
                        ""UserAgent"" character varying(256),
                        ""SessionCookie"" character varying(128),
                        ""TimestampLogin"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampLastSeen"" timestamp with time zone DEFAULT CURRENT_TIMESTAMP,
                        ""IsActive"" boolean NOT NULL DEFAULT true
                    );
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""UserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL;
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""OperatingSystem"" character varying(128);
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""Processor"" character varying(256);
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""VideoCard"" character varying(256);
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""RAM"" character varying(64);
                    ALTER TABLE ""core"".""AccountSessions"" ADD COLUMN IF NOT EXISTS ""UserAgent"" character varying(256);
                    ALTER TABLE ""core"".""AccountSessions"" ALTER COLUMN ""ClientVersion"" TYPE character varying(128);
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""Argon2idPasswordHash"" character varying(256);
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""TotalLevel"" integer NOT NULL DEFAULT 1;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""TotalExperience"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""GoldCoins"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""SilverCoins"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""PlinkoTickets"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""LastPlinkoDailyClaimUtc"" timestamp with time zone;
                    ALTER TABLE ""core"".""Accounts"" ADD COLUMN IF NOT EXISTS ""AscensionLevel"" integer NOT NULL DEFAULT 0;
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_AccountID"" ON ""core"".""AccountSessions"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_UserID"" ON ""core"".""AccountSessions"" (""UserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_IPAddress"" ON ""core"".""AccountSessions"" (""IPAddress"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_HWID"" ON ""core"".""AccountSessions"" (""HWID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_TimestampLogin"" ON ""core"".""AccountSessions"" (""TimestampLogin"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSessions_SessionCookie"" ON ""core"".""AccountSessions"" (""SessionCookie"");

                    -- 5b. Decomposed 1NF Account Child Tables
                    CREATE TABLE IF NOT EXISTS ""core"".""AccountSelectedStoreItems"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL,
                        ""ItemCode"" character varying(100) NOT NULL,
                        CONSTRAINT ""UQ_AccountSelectedStoreItems_AccountID_ItemCode"" UNIQUE (""AccountID"", ""ItemCode""),
                        CONSTRAINT ""FK_AccountSelectedStoreItems_Accounts_AccountID"" FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSelectedStoreItems_AccountID"" ON ""core"".""AccountSelectedStoreItems"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountSelectedStoreItems_ItemCode"" ON ""core"".""AccountSelectedStoreItems"" (""ItemCode"");

                    CREATE TABLE IF NOT EXISTS ""core"".""AccountIpAddresses"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL,
                        ""IPAddress"" character varying(50) NOT NULL,
                        CONSTRAINT ""UQ_AccountIpAddresses_AccountID_IPAddress"" UNIQUE (""AccountID"", ""IPAddress""),
                        CONSTRAINT ""FK_AccountIpAddresses_Accounts_AccountID"" FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_AccountIpAddresses_AccountID"" ON ""core"".""AccountIpAddresses"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountIpAddresses_IPAddress"" ON ""core"".""AccountIpAddresses"" (""IPAddress"");

                    CREATE TABLE IF NOT EXISTS ""core"".""AccountMacAddresses"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL,
                        ""MACAddress"" character varying(50) NOT NULL,
                        CONSTRAINT ""UQ_AccountMacAddresses_AccountID_MACAddress"" UNIQUE (""AccountID"", ""MACAddress""),
                        CONSTRAINT ""FK_AccountMacAddresses_Accounts_AccountID"" FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_AccountMacAddresses_AccountID"" ON ""core"".""AccountMacAddresses"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountMacAddresses_MACAddress"" ON ""core"".""AccountMacAddresses"" (""MACAddress"");

                    CREATE TABLE IF NOT EXISTS ""core"".""AccountBlockedPhrases"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL,
                        ""Phrase"" character varying(200) NOT NULL,
                        CONSTRAINT ""UQ_AccountBlockedPhrases_AccountID_Phrase"" UNIQUE (""AccountID"", ""Phrase""),
                        CONSTRAINT ""FK_AccountBlockedPhrases_Accounts_AccountID"" FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_AccountBlockedPhrases_AccountID"" ON ""core"".""AccountBlockedPhrases"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountBlockedPhrases_Phrase"" ON ""core"".""AccountBlockedPhrases"" (""Phrase"");

                    -- 5b2. Decomposed 1NF Account Peers (core.AccountPeers)
                    CREATE TABLE IF NOT EXISTS ""core"".""AccountPeers"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE,
                        ""PeerAccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE RESTRICT,
                        ""Type"" integer NOT NULL DEFAULT 0,
                        ""Reason"" character varying(100),
                        ""TimestampCreated"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampExpires"" timestamp with time zone,
                        CONSTRAINT ""UQ_AccountPeers_AccountID_PeerAccountID_Type"" UNIQUE (""AccountID"", ""PeerAccountID"", ""Type"")
                    );
                    ALTER TABLE ""core"".""AccountPeers"" ADD COLUMN IF NOT EXISTS ""TimestampExpires"" timestamp with time zone;
                    CREATE INDEX IF NOT EXISTS ""IX_AccountPeers_AccountID"" ON ""core"".""AccountPeers"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountPeers_PeerAccountID"" ON ""core"".""AccountPeers"" (""PeerAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountPeers_TimestampExpires"" ON ""core"".""AccountPeers"" (""TimestampExpires"");

                    -- 5c. Decomposed 1NF Hero Mastery Experiences
                    CREATE TABLE IF NOT EXISTS ""stat"".""HeroMasteryExperiences"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""MasteryID"" integer NOT NULL,
                        ""HeroIdentifier"" character varying(100) NOT NULL,
                        ""Experience"" integer NOT NULL DEFAULT 0,
                        CONSTRAINT ""UQ_HeroMasteryExperiences_MasteryID_HeroIdentifier"" UNIQUE (""MasteryID"", ""HeroIdentifier""),
                        CONSTRAINT ""FK_HeroMasteryExperiences_Masteries_MasteryID"" FOREIGN KEY (""MasteryID"") REFERENCES ""stat"".""Masteries""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_HeroMasteryExperiences_MasteryID"" ON ""stat"".""HeroMasteryExperiences"" (""MasteryID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroMasteryExperiences_HeroIdentifier"" ON ""stat"".""HeroMasteryExperiences"" (""HeroIdentifier"");

                    -- 5d. Decomposed 1NF Hero Guide Child Tables
                    CREATE TABLE IF NOT EXISTS ""misc"".""HeroGuideItems"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""GuideID"" integer NOT NULL,
                        ""SlotType"" integer NOT NULL,
                        ""OrderIndex"" integer NOT NULL,
                        ""ItemCode"" character varying(100) NOT NULL,
                        CONSTRAINT ""UQ_HeroGuideItems_GuideID_SlotType_OrderIndex"" UNIQUE (""GuideID"", ""SlotType"", ""OrderIndex""),
                        CONSTRAINT ""FK_HeroGuideItems_HeroGuides_GuideID"" FOREIGN KEY (""GuideID"") REFERENCES ""misc"".""HeroGuides""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideItems_GuideID"" ON ""misc"".""HeroGuideItems"" (""GuideID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideItems_GuideID_SlotType"" ON ""misc"".""HeroGuideItems"" (""GuideID"", ""SlotType"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideItems_ItemCode"" ON ""misc"".""HeroGuideItems"" (""ItemCode"");

                    CREATE TABLE IF NOT EXISTS ""misc"".""HeroGuideAbilities"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""GuideID"" integer NOT NULL,
                        ""LevelSequence"" integer NOT NULL,
                        ""AbilityCode"" character varying(100) NOT NULL,
                        ""AbilitySlot"" integer NOT NULL DEFAULT 0,
                        CONSTRAINT ""UQ_HeroGuideAbilities_GuideID_LevelSequence"" UNIQUE (""GuideID"", ""LevelSequence""),
                        CONSTRAINT ""FK_HeroGuideAbilities_HeroGuides_GuideID"" FOREIGN KEY (""GuideID"") REFERENCES ""misc"".""HeroGuides""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideAbilities_GuideID"" ON ""misc"".""HeroGuideAbilities"" (""GuideID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideAbilities_LevelSequence"" ON ""misc"".""HeroGuideAbilities"" (""LevelSequence"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideAbilities_AbilityCode"" ON ""misc"".""HeroGuideAbilities"" (""AbilityCode"");

                    -- 5d-2. Authoritative Match Records & Lifecycle Ledger (stat.MatchRecords)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchRecords"" (
                        ""MatchID"" integer PRIMARY KEY,
                        ""ServerID"" integer NOT NULL DEFAULT 0,
                        ""ServerPort"" integer NOT NULL DEFAULT 0,
                        ""ServerName"" character varying(128) NOT NULL DEFAULT '',
                        ""HostAccountName"" character varying(64) NOT NULL DEFAULT '',
                        ""Map"" character varying(64) NOT NULL DEFAULT '',
                        ""MatchMode"" character varying(32) NOT NULL DEFAULT '',
                        ""IsCasual"" boolean NOT NULL DEFAULT false,
                        ""MatchType"" integer NOT NULL DEFAULT 0,
                        ""State"" integer NOT NULL DEFAULT 1,
                        ""TerminationReason"" character varying(255),
                        ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""StartedAt"" timestamp with time zone,
                        ""EndedAt"" timestamp with time zone,
                        ""PlayerCount"" integer NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchRecords_ServerID_State"" ON ""stat"".""MatchRecords"" (""ServerID"", ""State"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchRecords_HostAccountName"" ON ""stat"".""MatchRecords"" (""HostAccountName"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchRecords_CreatedAt"" ON ""stat"".""MatchRecords"" (""CreatedAt"");

                    -- 5e. Decomposed 1NF Match Bans (stat.MatchBans)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchBans"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""MatchID"" integer NOT NULL,
                        ""Team"" integer NOT NULL DEFAULT 0,
                        ""BanOrder"" integer NOT NULL DEFAULT 1,
                        ""HeroIdentifier"" character varying(50) NOT NULL,
                        CONSTRAINT ""UQ_MatchBans_MatchID_HeroIdentifier"" UNIQUE (""MatchID"", ""HeroIdentifier""),
                        CONSTRAINT ""FK_MatchBans_MatchStatistics_MatchID"" FOREIGN KEY (""MatchID"") REFERENCES ""stat"".""MatchStatistics""(""MatchID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchBans_HeroIdentifier"" ON ""stat"".""MatchBans"" (""HeroIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchBans_MatchID_BanOrder"" ON ""stat"".""MatchBans"" (""MatchID"", ""BanOrder"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchBans_MatchID_HeroIdentifier"" ON ""stat"".""MatchBans"" (""MatchID"", ""HeroIdentifier"");

                    -- 5f. Decomposed 1NF Account Guide Preferences (core.AccountGuidePreferences)
                    CREATE TABLE IF NOT EXISTS ""core"".""AccountGuidePreferences"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL,
                        ""GuideID"" integer NOT NULL,
                        ""IsFavorite"" boolean NOT NULL DEFAULT false,
                        ""IsDefault"" boolean NOT NULL DEFAULT false,
                        ""TimestampUpdated"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        CONSTRAINT ""UQ_AccountGuidePreferences_AccountID_GuideID"" UNIQUE (""AccountID"", ""GuideID""),
                        CONSTRAINT ""FK_AccountGuidePreferences_Accounts_AccountID"" FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE,
                        CONSTRAINT ""FK_AccountGuidePreferences_HeroGuides_GuideID"" FOREIGN KEY (""GuideID"") REFERENCES ""misc"".""HeroGuides""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_AccountGuidePreferences_AccountID"" ON ""core"".""AccountGuidePreferences"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountGuidePreferences_GuideID"" ON ""core"".""AccountGuidePreferences"" (""GuideID"");

                    -- 5g. Decomposed 1NF Match Player Inventories (stat.MatchPlayerInventories)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchPlayerInventories"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""PlayerStatisticID"" integer NOT NULL,
                        ""MatchID"" integer NOT NULL,
                        ""AccountID"" integer NOT NULL,
                        ""SlotIndex"" integer NOT NULL,
                        ""ItemIdentifier"" character varying(100) NOT NULL,
                        ""Charges"" integer NOT NULL DEFAULT 0,
                        CONSTRAINT ""UQ_MatchPlayerInventories_PlayerStatisticID_SlotIndex"" UNIQUE (""PlayerStatisticID"", ""SlotIndex""),
                        CONSTRAINT ""FK_MatchPlayerInventories_PlayerStatistics_PlayerStatisticID"" FOREIGN KEY (""PlayerStatisticID"") REFERENCES ""stat"".""PlayerStatistics""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchPlayerInventories_PlayerStatisticID"" ON ""stat"".""MatchPlayerInventories"" (""PlayerStatisticID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchPlayerInventories_ItemIdentifier"" ON ""stat"".""MatchPlayerInventories"" (""ItemIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchPlayerInventories_MatchID_AccountID"" ON ""stat"".""MatchPlayerInventories"" (""MatchID"", ""AccountID"");

                    -- 5h. Decomposed 1NF Match Item Events (stat.MatchItemEvents)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchItemEvents"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""PlayerStatisticID"" integer NOT NULL,
                        ""MatchID"" integer NOT NULL,
                        ""AccountID"" integer NOT NULL,
                        ""GameTimeSeconds"" integer NOT NULL,
                        ""EventType"" smallint NOT NULL,
                        ""ItemIdentifier"" character varying(100) NOT NULL,
                        CONSTRAINT ""FK_MatchItemEvents_PlayerStatistics_PlayerStatisticID"" FOREIGN KEY (""PlayerStatisticID"") REFERENCES ""stat"".""PlayerStatistics""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchItemEvents_PlayerStatisticID"" ON ""stat"".""MatchItemEvents"" (""PlayerStatisticID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchItemEvents_ItemIdentifier"" ON ""stat"".""MatchItemEvents"" (""ItemIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchItemEvents_MatchID_AccountID_GameTimeSeconds"" ON ""stat"".""MatchItemEvents"" (""MatchID"", ""AccountID"", ""GameTimeSeconds"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchItemEvents_EventType"" ON ""stat"".""MatchItemEvents"" (""EventType"");

                    -- 5i. Decomposed 1NF Match Ability Events (stat.MatchAbilityEvents)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchAbilityEvents"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""PlayerStatisticID"" integer NOT NULL,
                        ""MatchID"" integer NOT NULL,
                        ""AccountID"" integer NOT NULL,
                        ""HeroIdentifier"" character varying(100) NOT NULL,
                        ""AbilityIdentifier"" character varying(100) NOT NULL,
                        ""SlotIndex"" smallint NOT NULL,
                        ""Level"" smallint NOT NULL DEFAULT 1,
                        ""GameTimeSeconds"" integer NOT NULL,
                        CONSTRAINT ""FK_MatchAbilityEvents_PlayerStatistics_PlayerStatisticID"" FOREIGN KEY (""PlayerStatisticID"") REFERENCES ""stat"".""PlayerStatistics""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchAbilityEvents_PlayerStatisticID"" ON ""stat"".""MatchAbilityEvents"" (""PlayerStatisticID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchAbilityEvents_AbilityIdentifier"" ON ""stat"".""MatchAbilityEvents"" (""AbilityIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchAbilityEvents_HeroIdentifier"" ON ""stat"".""MatchAbilityEvents"" (""HeroIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchAbilityEvents_MatchID_AccountID_GameTimeSeconds"" ON ""stat"".""MatchAbilityEvents"" (""MatchID"", ""AccountID"", ""GameTimeSeconds"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchAbilityEvents_MatchID_SlotIndex_Level"" ON ""stat"".""MatchAbilityEvents"" (""MatchID"", ""SlotIndex"", ""Level"");

                    -- 5j. Decomposed 1NF Match Frags (stat.MatchFrags)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchFrags"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""MatchID"" integer NOT NULL,
                        ""KillerAccountID"" integer NOT NULL,
                        ""VictimAccountID"" integer NOT NULL,
                        ""GameTimeSeconds"" integer NOT NULL,
                        ""FirstBlood"" boolean NOT NULL DEFAULT false,
                        CONSTRAINT ""FK_MatchFrags_MatchStatistics_MatchID"" FOREIGN KEY (""MatchID"") REFERENCES ""stat"".""MatchStatistics""(""MatchID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFrags_MatchID"" ON ""stat"".""MatchFrags"" (""MatchID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFrags_KillerAccountID"" ON ""stat"".""MatchFrags"" (""KillerAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFrags_VictimAccountID"" ON ""stat"".""MatchFrags"" (""VictimAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFrags_MatchID_GameTimeSeconds"" ON ""stat"".""MatchFrags"" (""MatchID"", ""GameTimeSeconds"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFrags_KillerAccountID_VictimAccountID"" ON ""stat"".""MatchFrags"" (""KillerAccountID"", ""VictimAccountID"");

                    -- 5k. Decomposed 1NF Match Frag Assists (stat.MatchFragAssists)
                    CREATE TABLE IF NOT EXISTS ""stat"".""MatchFragAssists"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""FragID"" integer NOT NULL,
                        ""AssistingAccountID"" integer NOT NULL,
                        CONSTRAINT ""UQ_MatchFragAssists_FragID_AssistingAccountID"" UNIQUE (""FragID"", ""AssistingAccountID""),
                        CONSTRAINT ""FK_MatchFragAssists_MatchFrags_FragID"" FOREIGN KEY (""FragID"") REFERENCES ""stat"".""MatchFrags""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFragAssists_FragID"" ON ""stat"".""MatchFragAssists"" (""FragID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchFragAssists_AssistingAccountID"" ON ""stat"".""MatchFragAssists"" (""AssistingAccountID"");

                    -- 5l. Decomposed 1NF Hero Item Components (misc.HeroItemComponents)
                    CREATE TABLE IF NOT EXISTS ""misc"".""HeroItemComponents"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""ParentItemID"" integer NOT NULL,
                        ""ComponentItemCode"" character varying(100) NOT NULL,
                        ""Quantity"" integer NOT NULL DEFAULT 1,
                        CONSTRAINT ""UQ_HeroItemComponents_ParentItemID_ComponentItemCode"" UNIQUE (""ParentItemID"", ""ComponentItemCode""),
                        CONSTRAINT ""FK_HeroItemComponents_HeroItems_ParentItemID"" FOREIGN KEY (""ParentItemID"") REFERENCES ""misc"".""HeroItems""(""ID"") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_HeroItemComponents_ParentItemID"" ON ""misc"".""HeroItemComponents"" (""ParentItemID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroItemComponents_ComponentItemCode"" ON ""misc"".""HeroItemComponents"" (""ComponentItemCode"");

                    -- Ensure HeroItems.Cost is integer
                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns 
                            WHERE table_schema = 'misc' AND table_name = 'HeroItems' AND column_name = 'Cost' AND data_type LIKE '%char%'
                        ) THEN
                            ALTER TABLE ""misc"".""HeroItems"" ALTER COLUMN ""Cost"" TYPE integer USING (""Cost""::integer);
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- 5m. core.UserInvites & core.InviteSystemAuditLogs
                    CREATE TABLE IF NOT EXISTS ""core"".""UserInvites"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""Code"" character varying(32) NOT NULL,
                        ""CreatedByUserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE,
                        ""RedeemedByUserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL,
                        ""RedeemedByAccountID"" integer REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL,
                        ""TimestampCreated"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampExpires"" timestamp with time zone,
                        ""TimestampRedeemed"" timestamp with time zone,
                        ""IsRevoked"" boolean NOT NULL DEFAULT false,
                        ""RevocationReason"" character varying(255),
                        ""CreatorIpAddress"" character varying(64),
                        ""RedeemerIpAddress"" character varying(64)
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_UserInvites_Code"" ON ""core"".""UserInvites"" (""Code"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInvites_CreatedByUserID"" ON ""core"".""UserInvites"" (""CreatedByUserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInvites_RedeemedByUserID"" ON ""core"".""UserInvites"" (""RedeemedByUserID"");

                    -- 5n. Custom Map & 10v10 Statistics (stat.MatchStatistics / stat.PlayerStatistics / data.AccountMapStatistics)
                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics'
                              AND column_name = 'HeroIdentifier'
                              AND COALESCE(character_maximum_length, 0) < 64
                        ) THEN
                            ALTER TABLE ""stat"".""PlayerStatistics"" ALTER COLUMN ""HeroIdentifier"" TYPE character varying(64);
                        END IF;
                    END $$;

                    ALTER TABLE ""stat"".""PlayerStatistics"" ADD COLUMN IF NOT EXISTS ""CustomStats"" jsonb NULL;
                    ALTER TABLE ""stat"".""MatchStatistics"" ADD COLUMN IF NOT EXISTS ""CustomMatchStats"" jsonb NULL;
                    ALTER TABLE ""stat"".""MatchStatistics"" ADD COLUMN IF NOT EXISTS ""CustomSchemaKey"" character varying(32) NULL;
                    ALTER TABLE ""stat"".""MatchStatistics"" ADD COLUMN IF NOT EXISTS ""CustomSchemaVersion"" smallint NOT NULL DEFAULT 0;
                    CREATE INDEX IF NOT EXISTS ""IX_MatchStatistics_CustomSchemaKey"" ON ""stat"".""MatchStatistics"" (""CustomSchemaKey"");

                    CREATE TABLE IF NOT EXISTS ""data"".""AccountMapStatistics"" (
                        ""ID"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE,
                        ""MapSlug"" character varying(50) NOT NULL,
                        ""SchemaKey"" character varying(32) NOT NULL,
                        ""MatchesPlayed"" integer NOT NULL DEFAULT 0,
                        ""MatchesWon"" integer NOT NULL DEFAULT 0,
                        ""MatchesLost"" integer NOT NULL DEFAULT 0,
                        ""MatchesConceded"" integer NOT NULL DEFAULT 0,
                        ""Disconnects"" integer NOT NULL DEFAULT 0,
                        ""TimePlayedSeconds"" bigint NOT NULL DEFAULT 0,
                        ""HeroKills"" integer NOT NULL DEFAULT 0,
                        ""Deaths"" integer NOT NULL DEFAULT 0,
                        ""HeroAssists"" integer NOT NULL DEFAULT 0,
                        ""FavoriteHeroIdentifier"" character varying(64) NULL,
                        ""HeroPlayCountsJson"" jsonb NOT NULL DEFAULT jsonb_build_object(),
                        ""CustomTotals"" jsonb NOT NULL DEFAULT jsonb_build_object(),
                        ""BestRecords"" jsonb NOT NULL DEFAULT jsonb_build_object(),
                        ""LastMatchID"" integer NULL,
                        ""LastPlayedAtUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AccountMapStatistics_AccountID_MapSlug"" ON ""data"".""AccountMapStatistics"" (""AccountID"", ""MapSlug"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountMapStatistics_MapSlug_MatchesWon"" ON ""data"".""AccountMapStatistics"" (""MapSlug"", ""MatchesWon"");

                    -- Idempotent repair for historical custom-map PlayerStatistics rows mistakenly flagged RankedMatch = 1
                    UPDATE ""stat"".""PlayerStatistics"" ps
                    SET ""RankedMatch"" = 0,
                        ""PublicMatch"" = 1,
                        ""RankedSkillRatingChange"" = 0
                    FROM ""stat"".""MatchStatistics"" ms
                    WHERE ps.""MatchID"" = ms.""MatchID""
                      AND ps.""RankedMatch"" = 1
                      AND (
                          LOWER(ms.""Map"") LIKE '%devowars%' OR LOWER(ms.""Map"") LIKE '%truedevo%' OR
                          LOWER(ms.""Map"") LIKE '%soccer%' OR LOWER(ms.""Map"") LIKE '%striker%' OR
                          LOWER(ms.""Map"") LIKE '%enfos%' OR LOWER(ms.""Map"") LIKE '%arena%' OR
                          LOWER(ms.""Map"") LIKE '%valk%' OR LOWER(ms.""Map"") LIKE '%archer%' OR
                          LOWER(ms.""Map"") LIKE '%grimm%' OR LOWER(ms.""Map"") LIKE '%prophet%' OR
                          LOWER(ms.""Map"") LIKE '%ctf%' OR LOWER(ms.""Map"") LIKE '%capturetheflag%' OR
                          LOWER(ms.""Map"") LIKE '%deathmatch%' OR LOWER(ms.""Map"") LIKE '%watchtower%' OR
                          LOWER(ms.""Map"") LIKE '%darkwood%' OR LOWER(ms.""Map"") LIKE '%duel%' OR
                          LOWER(ms.""Map"") LIKE '%maze%' OR LOWER(ms.""Map"") LIKE '%nightmarish%' OR
                          LOWER(ms.""Map"") LIKE '%10v10%' OR LOWER(ms.""Map"") LIKE '%wtf%' OR
                          LOWER(ms.""Map"") LIKE '%omg%' OR LOWER(ms.""Map"") LIKE '%kingdoms%' OR
                          LOWER(ms.""Map"") LIKE '%persian%'
                      );

                    CREATE TABLE IF NOT EXISTS ""core"".""InviteSystemAuditLogs"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""StaffAccountID"" integer NOT NULL,
                        ""StaffAccountName"" character varying(64) NOT NULL,
                        ""InviteOnlyEnabled"" boolean NOT NULL,
                        ""Timestamp"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""Notes"" character varying(500),
                        ""IpAddress"" character varying(64)
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_InviteSystemAuditLogs_Timestamp"" ON ""core"".""InviteSystemAuditLogs"" (""Timestamp"");

                    -- War Effort Quest & Reward Cycle tables (v5.0.2)
                    CREATE TABLE IF NOT EXISTS ""data"".""WarEffortCycles"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""CycleNumber"" integer NOT NULL,
                        ""Name"" character varying(128) NOT NULL DEFAULT '',
                        ""StartsAtUtc"" timestamp with time zone NOT NULL,
                        ""EndsAtUtc"" timestamp with time zone NOT NULL,
                        ""IsActive"" boolean NOT NULL DEFAULT false,
                        ""CreatedAtUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_WarEffortCycles_CycleNumber"" ON ""data"".""WarEffortCycles"" (""CycleNumber"");
                    CREATE INDEX IF NOT EXISTS ""IX_WarEffortCycles_IsActive"" ON ""data"".""WarEffortCycles"" (""IsActive"");

                    CREATE TABLE IF NOT EXISTS ""data"".""QuestDefinitions"" (
                        ""Id"" integer PRIMARY KEY,
                        ""Code"" character varying(64) NOT NULL,
                        ""Title"" character varying(128) NOT NULL DEFAULT '',
                        ""Description"" character varying(512) NOT NULL DEFAULT '',
                        ""Category"" character varying(32) NOT NULL DEFAULT 'general',
                        ""NativeTriggerName"" character varying(64) NOT NULL DEFAULT 'achievement_value_games_won',
                        ""QuestTypeDef"" character varying(32) NOT NULL DEFAULT '',
                        ""QuestTypeKey"" integer NOT NULL DEFAULT 0,
                        ""HeroIdentifier"" character varying(64) NULL,
                        ""HeroRole"" character varying(32) NULL,
                        ""HeroAttribute"" character varying(32) NULL,
                        ""HeroAttackType"" character varying(16) NULL,
                        ""HeroTeamId"" integer NOT NULL DEFAULT 0,
                        ""GoalValue"" integer NOT NULL DEFAULT 1,
                        ""BagPointsReward"" integer NOT NULL DEFAULT 100,
                        ""CustomMetricKey"" character varying(64) NULL,
                        ""IconPath"" character varying(256) NULL,
                        ""IsNativeEngineTrigger"" boolean NOT NULL DEFAULT true,
                        ""Weight"" integer NOT NULL DEFAULT 10,
                        ""IsEnabled"" boolean NOT NULL DEFAULT true
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_QuestDefinitions_Code"" ON ""data"".""QuestDefinitions"" (""Code"");
                    CREATE INDEX IF NOT EXISTS ""IX_QuestDefinitions_Enabled_Native"" ON ""data"".""QuestDefinitions"" (""IsEnabled"", ""IsNativeEngineTrigger"");

                    CREATE TABLE IF NOT EXISTS ""data"".""AccountQuestSlots"" (
                        ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountId"" integer NOT NULL,
                        ""CycleId"" integer NOT NULL,
                        ""SlotIndex"" integer NOT NULL,
                        ""QuestDefinitionId"" integer NOT NULL,
                        ""Progress"" integer NOT NULL DEFAULT 0,
                        ""Goal"" integer NOT NULL DEFAULT 1,
                        ""AssignedAtUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""LastMatchId"" integer NULL,
                        ""CompletedAtUtc"" timestamp with time zone NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AccountQuestSlots_Account_Cycle_Slot"" ON ""data"".""AccountQuestSlots"" (""AccountId"", ""CycleId"", ""SlotIndex"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountQuestSlots_AccountId"" ON ""data"".""AccountQuestSlots"" (""AccountId"");

                    CREATE TABLE IF NOT EXISTS ""data"".""WarEffortBagMilestones"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""CycleId"" integer NOT NULL,
                        ""BagIndex"" integer NOT NULL,
                        ""BagId"" integer NOT NULL,
                        ""RequiredPoints"" integer NOT NULL,
                        ""RewardType"" character varying(32) NOT NULL DEFAULT 'MMPOINTS',
                        ""RewardAmount"" integer NOT NULL DEFAULT 0,
                        ""ProductId"" integer NULL,
                        ""AltSilverFallback"" integer NOT NULL DEFAULT 250,
                        ""Title"" character varying(128) NOT NULL DEFAULT ''
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_WarEffortBagMilestones_Cycle_BagIndex"" ON ""data"".""WarEffortBagMilestones"" (""CycleId"", ""BagIndex"");
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_WarEffortBagMilestones_BagId"" ON ""data"".""WarEffortBagMilestones"" (""BagId"");

                    CREATE TABLE IF NOT EXISTS ""data"".""AccountWarEffortProgress"" (
                        ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountId"" integer NOT NULL,
                        ""CycleId"" integer NOT NULL,
                        ""TotalBagPoints"" integer NOT NULL DEFAULT 0,
                        ""QuestsCompletedCount"" integer NOT NULL DEFAULT 0,
                        ""QuestsResetCount"" integer NOT NULL DEFAULT 0,
                        ""RerollsAvailable"" integer NOT NULL DEFAULT 1,
                        ""LastDailyRefreshUtcDate"" character varying(16) NOT NULL DEFAULT '',
                        ""LastUpdatedUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AccountWarEffortProgress_Account_Cycle"" ON ""data"".""AccountWarEffortProgress"" (""AccountId"", ""CycleId"");
                    CREATE INDEX IF NOT EXISTS ""IX_AccountWarEffortProgress_Cycle_Points"" ON ""data"".""AccountWarEffortProgress"" (""CycleId"", ""TotalBagPoints"");

                    CREATE TABLE IF NOT EXISTS ""data"".""AccountWarEffortBagClaims"" (
                        ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""AccountId"" integer NOT NULL,
                        ""CycleId"" integer NOT NULL,
                        ""BagIndex"" integer NOT NULL,
                        ""BagId"" integer NOT NULL,
                        ""ClaimedAtUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""RewardType"" character varying(32) NOT NULL DEFAULT 'MMPOINTS',
                        ""RewardAmount"" integer NOT NULL DEFAULT 0,
                        ""GrantedProductId"" integer NULL,
                        ""WasAltFallback"" boolean NOT NULL DEFAULT false,
                        ""AutoClaimedAtRollover"" boolean NOT NULL DEFAULT false,
                        ""IdempotencyKey"" character varying(128) NOT NULL DEFAULT ''
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AccountWarEffortBagClaims_Account_Cycle_Bag"" ON ""data"".""AccountWarEffortBagClaims"" (""AccountId"", ""CycleId"", ""BagIndex"");
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AccountWarEffortBagClaims_IdempotencyKey"" ON ""data"".""AccountWarEffortBagClaims"" (""IdempotencyKey"");

                    CREATE TABLE IF NOT EXISTS ""data"".""WarEffortCycleLeaderboardSnapshots"" (
                        ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""CycleId"" integer NOT NULL,
                        ""AccountId"" integer NOT NULL,
                        ""Rank"" integer NOT NULL,
                        ""TotalBagPoints"" integer NOT NULL DEFAULT 0,
                        ""QuestsCompletedCount"" integer NOT NULL DEFAULT 0,
                        ""CapturedAtUtc"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""UX_WarEffortCycleLeaderboardSnapshots_Cycle_Account"" ON ""data"".""WarEffortCycleLeaderboardSnapshots"" (""CycleId"", ""AccountId"");
                    CREATE INDEX IF NOT EXISTS ""IX_WarEffortCycleLeaderboardSnapshots_Cycle_Rank"" ON ""data"".""WarEffortCycleLeaderboardSnapshots"" (""CycleId"", ""Rank"");

                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.columns 
                            WHERE table_schema = 'core' AND table_name = 'Users' AND column_name = 'InvitedByUserID'
                        ) THEN
                            ALTER TABLE ""core"".""Users"" ADD COLUMN ""InvitedByUserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL;
                            CREATE INDEX IF NOT EXISTS ""IX_Users_InvitedByUserID"" ON ""core"".""Users"" (""InvitedByUserID"");
                        END IF;
                    END $$;

                    -- 6. EconomyTransactions indexes
                    CREATE INDEX IF NOT EXISTS ""IX_EconomyTransactions_UserID"" ON ""core"".""EconomyTransactions"" (""UserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_EconomyTransactions_AccountID"" ON ""core"".""EconomyTransactions"" (""AccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_EconomyTransactions_Timestamp"" ON ""core"".""EconomyTransactions"" (""Timestamp"");
                    ALTER TABLE ""core"".""EconomyTransactions"" ADD COLUMN IF NOT EXISTS ""IdempotencyKey"" character varying(128);
                    CREATE INDEX IF NOT EXISTS ""IX_EconomyTransactions_UserID_Timestamp"" ON ""core"".""EconomyTransactions"" (""UserID"", ""Timestamp"");
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_EconomyTransactions_UserID_IdempotencyKey"" ON ""core"".""EconomyTransactions"" (""UserID"", ""IdempotencyKey"") WHERE ""IdempotencyKey"" IS NOT NULL;

                    -- 6b. Combat telemetry GIN indexes (PostgreSQL - guarded)
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'MatchStatistics' AND column_name = 'FragHistory') THEN
                            CREATE INDEX IF NOT EXISTS ""IX_MatchStatistics_FragHistory_gin"" ON ""stat"".""MatchStatistics"" USING GIN (""FragHistory"");
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics' AND column_name = 'ItemHistory') THEN
                            CREATE INDEX IF NOT EXISTS ""IX_PlayerStatistics_ItemHistory_gin"" ON ""stat"".""PlayerStatistics"" USING GIN (""ItemHistory"");
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics' AND column_name = 'AbilityHistory') THEN
                            CREATE INDEX IF NOT EXISTS ""IX_PlayerStatistics_AbilityHistory_gin"" ON ""stat"".""PlayerStatistics"" USING GIN (""AbilityHistory"");
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- 7. Legacy schema adjustments
                    ALTER TABLE ""misc"".""HeroGuides"" ADD COLUMN IF NOT EXISTS ""TargetMap"" character varying(50) DEFAULT 'caldavar';
                    UPDATE ""misc"".""HeroGuides"" SET ""TargetMap"" = 'caldavar' WHERE ""TargetMap"" IS NULL OR ""TargetMap"" = 'all' OR ""TargetMap"" = '' OR ""TargetMap"" = 'caldavar_ranked' OR ""TargetMap"" = 'caldavar_public';
                    UPDATE ""misc"".""HeroGuides"" SET ""TargetMap"" = 'midwars' WHERE ""TargetMap"" = 'midwars_ranked' OR ""TargetMap"" = 'midwars_public';
                    ALTER TABLE ""misc"".""HeroGuideAbilities"" ADD COLUMN IF NOT EXISTS ""AbilitySlot"" integer NOT NULL DEFAULT 0;

                    -- Backfill normalized HeroGuideAbilities and HeroGuideItems if empty
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroGuideAbilities')
                           AND (SELECT COUNT(*) FROM ""misc"".""HeroGuideAbilities"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'misc' AND table_name = 'HeroGuides' AND column_name = 'AbilityQueue') THEN
                            INSERT INTO ""misc"".""HeroGuideAbilities"" (""GuideID"", ""LevelSequence"", ""AbilityCode"", ""AbilitySlot"")
                            SELECT g.""ID"", elem.ord::integer, TRIM(elem.val),
                                   CASE 
                                       WHEN elem.val ~ '^[0-9]+$' THEN elem.val::integer
                                       WHEN elem.val ~* 'Q$' OR elem.val ~* '1$' THEN 1
                                       WHEN elem.val ~* 'W$' OR elem.val ~* '2$' THEN 2
                                       WHEN elem.val ~* 'E$' OR elem.val ~* '3$' THEN 3
                                       WHEN elem.val ~* 'R$' OR elem.val ~* '4$' THEN 4
                                       ELSE 5
                                   END
                            FROM ""misc"".""HeroGuides"" g,
                            LATERAL unnest(string_to_array(replace(g.""AbilityQueue"", '|', ','), ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE g.""AbilityQueue"" IS NOT NULL AND TRIM(g.""AbilityQueue"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""GuideID"", ""LevelSequence"") DO NOTHING;
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroGuideItems')
                           AND (SELECT COUNT(*) FROM ""misc"".""HeroGuideItems"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'misc' AND table_name = 'HeroGuides' AND column_name = 'StartingItems') THEN
                            INSERT INTO ""misc"".""HeroGuideItems"" (""GuideID"", ""SlotType"", ""OrderIndex"", ""ItemCode"")
                            SELECT g.""ID"", 0, (elem.ord - 1)::integer, TRIM(elem.val)
                            FROM ""misc"".""HeroGuides"" g,
                            LATERAL unnest(string_to_array(replace(g.""StartingItems"", '|', ','), ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE g.""StartingItems"" IS NOT NULL AND TRIM(g.""StartingItems"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""GuideID"", ""SlotType"", ""OrderIndex"") DO NOTHING;

                            INSERT INTO ""misc"".""HeroGuideItems"" (""GuideID"", ""SlotType"", ""OrderIndex"", ""ItemCode"")
                            SELECT g.""ID"", 1, (elem.ord - 1)::integer, TRIM(elem.val)
                            FROM ""misc"".""HeroGuides"" g,
                            LATERAL unnest(string_to_array(replace(g.""EarlyGameItems"", '|', ','), ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE g.""EarlyGameItems"" IS NOT NULL AND TRIM(g.""EarlyGameItems"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""GuideID"", ""SlotType"", ""OrderIndex"") DO NOTHING;

                            INSERT INTO ""misc"".""HeroGuideItems"" (""GuideID"", ""SlotType"", ""OrderIndex"", ""ItemCode"")
                            SELECT g.""ID"", 2, (elem.ord - 1)::integer, TRIM(elem.val)
                            FROM ""misc"".""HeroGuides"" g,
                            LATERAL unnest(string_to_array(replace(g.""CoreItems"", '|', ','), ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE g.""CoreItems"" IS NOT NULL AND TRIM(g.""CoreItems"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""GuideID"", ""SlotType"", ""OrderIndex"") DO NOTHING;

                            INSERT INTO ""misc"".""HeroGuideItems"" (""GuideID"", ""SlotType"", ""OrderIndex"", ""ItemCode"")
                            SELECT g.""ID"", 3, (elem.ord - 1)::integer, TRIM(elem.val)
                            FROM ""misc"".""HeroGuides"" g,
                            LATERAL unnest(string_to_array(replace(g.""LuxuryItems"", '|', ','), ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE g.""LuxuryItems"" IS NOT NULL AND TRIM(g.""LuxuryItems"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""GuideID"", ""SlotType"", ""OrderIndex"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill MatchBans from legacy MatchStatistics.BannedHeroes if empty
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchBans')
                           AND (SELECT COUNT(*) FROM ""stat"".""MatchBans"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'MatchStatistics' AND column_name = 'BannedHeroes') THEN
                            INSERT INTO ""stat"".""MatchBans"" (""MatchID"", ""Team"", ""BanOrder"", ""HeroIdentifier"")
                            SELECT m.""MatchID"",
                                   CASE WHEN (elem.ord % 2 = 1) THEN 1 ELSE 2 END,
                                   elem.ord::integer,
                                   TRIM(elem.val)
                            FROM ""stat"".""MatchStatistics"" m,
                            LATERAL unnest(string_to_array(m.""BannedHeroes"", ',')) WITH ORDINALITY AS elem(val, ord)
                            WHERE m.""BannedHeroes"" IS NOT NULL AND TRIM(m.""BannedHeroes"") <> '' AND TRIM(elem.val) <> ''
                            ON CONFLICT (""MatchID"", ""HeroIdentifier"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill AccountGuidePreferences from legacy FavoriteGuideIDs if empty
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountGuidePreferences')
                           AND (SELECT COUNT(*) FROM ""core"".""AccountGuidePreferences"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'FavoriteGuideIDs') THEN
                            INSERT INTO ""core"".""AccountGuidePreferences"" (""AccountID"", ""GuideID"", ""IsFavorite"", ""IsDefault"")
                            SELECT a.""ID"", elem::integer, true, false
                            FROM ""core"".""Accounts"" a,
                            LATERAL unnest(a.""FavoriteGuideIDs"") AS elem
                            WHERE a.""FavoriteGuideIDs"" IS NOT NULL
                            ON CONFLICT (""AccountID"", ""GuideID"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill AccountGuidePreferences with IsDefault = true from DefaultGuideIDs
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountGuidePreferences')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'DefaultGuideIDs') THEN
                            INSERT INTO ""core"".""AccountGuidePreferences"" (""AccountID"", ""GuideID"", ""IsFavorite"", ""IsDefault"")
                            SELECT a.""ID"", elem::integer, false, true
                            FROM ""core"".""Accounts"" a,
                            LATERAL unnest(a.""DefaultGuideIDs"") AS elem
                            WHERE a.""DefaultGuideIDs"" IS NOT NULL
                            ON CONFLICT (""AccountID"", ""GuideID"")
                            DO UPDATE SET ""IsDefault"" = true, ""TimestampUpdated"" = CURRENT_TIMESTAMP;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill MatchPlayerInventories from legacy PlayerStatistics.Inventory if empty
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchPlayerInventories')
                           AND (SELECT COUNT(*) FROM ""stat"".""MatchPlayerInventories"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics' AND column_name = 'Inventory') THEN
                            INSERT INTO ""stat"".""MatchPlayerInventories"" (""PlayerStatisticID"", ""MatchID"", ""AccountID"", ""SlotIndex"", ""ItemIdentifier"", ""Charges"")
                            SELECT p.""ID"", p.""MatchID"", p.""AccountID"", (elem.ord - 1)::integer, TRIM(elem.val), 0
                            FROM ""stat"".""PlayerStatistics"" p,
                            LATERAL unnest(p.""Inventory"") WITH ORDINALITY AS elem(val, ord)
                            WHERE p.""Inventory"" IS NOT NULL AND TRIM(elem.val) <> ''
                            ON CONFLICT (""PlayerStatisticID"", ""SlotIndex"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill MatchItemEvents from legacy PlayerStatistics.ItemHistory if empty
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchItemEvents')
                           AND (SELECT COUNT(*) FROM ""stat"".""MatchItemEvents"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics' AND column_name = 'ItemHistory') THEN
                            INSERT INTO ""stat"".""MatchItemEvents"" (""PlayerStatisticID"", ""MatchID"", ""AccountID"", ""GameTimeSeconds"", ""EventType"", ""ItemIdentifier"")
                            SELECT p.""ID"", p.""MatchID"", p.""AccountID"",
                                   COALESCE((elem->>'GameTimeSeconds')::integer, (elem->>'GameTime')::integer, 0),
                                   COALESCE((elem->>'EventType')::smallint, 1),
                                   COALESCE(elem->>'ItemName', elem->>'ItemIdentifier', '')
                            FROM ""stat"".""PlayerStatistics"" p,
                            LATERAL jsonb_array_elements(p.""ItemHistory"") AS elem
                            WHERE p.""ItemHistory"" IS NOT NULL AND jsonb_typeof(p.""ItemHistory"") = 'array'
                              AND COALESCE(elem->>'ItemName', elem->>'ItemIdentifier', '') <> '';
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Backfill AccountSelectedStoreItems from legacy Accounts.SelectedStoreItems
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountSelectedStoreItems')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'SelectedStoreItems') THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'SelectedStoreItems' AND data_type = 'ARRAY') THEN
                                INSERT INTO ""core"".""AccountSelectedStoreItems"" (""AccountID"", ""ItemCode"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL unnest(a.""SelectedStoreItems"") AS elem
                                WHERE a.""SelectedStoreItems"" IS NOT NULL AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""ItemCode"") DO NOTHING;
                            ELSE
                                INSERT INTO ""core"".""AccountSelectedStoreItems"" (""AccountID"", ""ItemCode"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL jsonb_array_elements_text(a.""SelectedStoreItems""::jsonb) AS elem
                                WHERE a.""SelectedStoreItems"" IS NOT NULL AND a.""SelectedStoreItems""::text <> '[]' AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""ItemCode"") DO NOTHING;
                            END IF;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill AccountIpAddresses from legacy Accounts.IPAddressCollection
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountIpAddresses')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'IPAddressCollection') THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'IPAddressCollection' AND data_type = 'ARRAY') THEN
                                INSERT INTO ""core"".""AccountIpAddresses"" (""AccountID"", ""IPAddress"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL unnest(a.""IPAddressCollection"") AS elem
                                WHERE a.""IPAddressCollection"" IS NOT NULL AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""IPAddress"") DO NOTHING;
                            ELSE
                                INSERT INTO ""core"".""AccountIpAddresses"" (""AccountID"", ""IPAddress"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL jsonb_array_elements_text(a.""IPAddressCollection""::jsonb) AS elem
                                WHERE a.""IPAddressCollection"" IS NOT NULL AND a.""IPAddressCollection""::text <> '[]' AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""IPAddress"") DO NOTHING;
                            END IF;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill AccountMacAddresses from legacy Accounts.MACAddressCollection
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountMacAddresses')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'MACAddressCollection') THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'MACAddressCollection' AND data_type = 'ARRAY') THEN
                                INSERT INTO ""core"".""AccountMacAddresses"" (""AccountID"", ""MACAddress"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL unnest(a.""MACAddressCollection"") AS elem
                                WHERE a.""MACAddressCollection"" IS NOT NULL AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""MACAddress"") DO NOTHING;
                            ELSE
                                INSERT INTO ""core"".""AccountMacAddresses"" (""AccountID"", ""MACAddress"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL jsonb_array_elements_text(a.""MACAddressCollection""::jsonb) AS elem
                                WHERE a.""MACAddressCollection"" IS NOT NULL AND a.""MACAddressCollection""::text <> '[]' AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""MACAddress"") DO NOTHING;
                            END IF;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill AccountBlockedPhrases from legacy Accounts.BlockedPhrases
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountBlockedPhrases')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'BlockedPhrases') THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'BlockedPhrases' AND data_type = 'ARRAY') THEN
                                INSERT INTO ""core"".""AccountBlockedPhrases"" (""AccountID"", ""Phrase"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL unnest(a.""BlockedPhrases"") AS elem
                                WHERE a.""BlockedPhrases"" IS NOT NULL AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""Phrase"") DO NOTHING;
                            ELSE
                                INSERT INTO ""core"".""AccountBlockedPhrases"" (""AccountID"", ""Phrase"")
                                SELECT a.""ID"", TRIM(elem)
                                FROM ""core"".""Accounts"" a,
                                LATERAL jsonb_array_elements_text(a.""BlockedPhrases""::jsonb) AS elem
                                WHERE a.""BlockedPhrases"" IS NOT NULL AND a.""BlockedPhrases""::text <> '[]' AND TRIM(elem) <> ''
                                ON CONFLICT (""AccountID"", ""Phrase"") DO NOTHING;
                            END IF;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill Friendships from legacy Accounts.FriendedPeers
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'Friendships')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'FriendedPeers') THEN
                            INSERT INTO ""core"".""Friendships"" (""AccountID"", ""FriendAccountID"", ""FriendGroup"", ""Status"")
                            SELECT a.""ID"", (elem->>'ID')::integer, COALESCE(elem->>'FriendGroup', 'Default'), 1
                            FROM ""core"".""Accounts"" a,
                            LATERAL jsonb_array_elements(a.""FriendedPeers"") AS elem
                            WHERE a.""FriendedPeers"" IS NOT NULL AND jsonb_typeof(a.""FriendedPeers"") = 'array'
                              AND (elem->>'ID') IS NOT NULL
                              AND EXISTS (SELECT 1 FROM ""core"".""Accounts"" target WHERE target.""ID"" = (elem->>'ID')::integer)
                            ON CONFLICT (""AccountID"", ""FriendAccountID"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill AccountPeers from legacy Accounts.BannedPeers and IgnoredPeers
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountPeers')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'BannedPeers') THEN
                            INSERT INTO ""core"".""AccountPeers"" (""AccountID"", ""PeerAccountID"", ""Type"", ""Reason"", ""TimestampCreated"", ""TimestampExpires"")
                            SELECT a.""ID"", (elem->>'ID')::integer, 0, COALESCE(elem->>'BanReason', elem->>'Reason', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + INTERVAL '6 months'
                            FROM ""core"".""Accounts"" a,
                            LATERAL jsonb_array_elements(a.""BannedPeers"") AS elem
                            WHERE a.""BannedPeers"" IS NOT NULL AND jsonb_typeof(a.""BannedPeers"") = 'array'
                              AND (elem->>'ID') IS NOT NULL
                              AND EXISTS (SELECT 1 FROM ""core"".""Accounts"" target WHERE target.""ID"" = (elem->>'ID')::integer)
                            ON CONFLICT (""AccountID"", ""PeerAccountID"", ""Type"") DO NOTHING;
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountPeers')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Accounts' AND column_name = 'IgnoredPeers') THEN
                            INSERT INTO ""core"".""AccountPeers"" (""AccountID"", ""PeerAccountID"", ""Type"", ""Reason"", ""TimestampCreated"", ""TimestampExpires"")
                            SELECT a.""ID"", (elem->>'ID')::integer, 1, '', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + INTERVAL '6 months'
                            FROM ""core"".""Accounts"" a,
                            LATERAL jsonb_array_elements(a.""IgnoredPeers"") AS elem
                            WHERE a.""IgnoredPeers"" IS NOT NULL AND jsonb_typeof(a.""IgnoredPeers"") = 'array'
                              AND (elem->>'ID') IS NOT NULL
                              AND EXISTS (SELECT 1 FROM ""core"".""Accounts"" target WHERE target.""ID"" = (elem->>'ID')::integer)
                            ON CONFLICT (""AccountID"", ""PeerAccountID"", ""Type"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill UserInventory from legacy Users.OwnedStoreItems
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'UserInventory')
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Users' AND column_name = 'OwnedStoreItems') THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'Users' AND column_name = 'OwnedStoreItems' AND data_type = 'ARRAY') THEN
                                INSERT INTO ""core"".""UserInventory"" (""UserID"", ""ItemCode"", ""Quantity"", ""IsPermanent"", ""TimestampAcquired"", ""Source"")
                                SELECT u.""ID"", elem, 1, true, NOW(), 'LegacyDropBackfill'
                                FROM ""core"".""Users"" u,
                                LATERAL unnest(u.""OwnedStoreItems"") AS elem
                                WHERE u.""OwnedStoreItems"" IS NOT NULL AND cardinality(u.""OwnedStoreItems"") > 0
                                ON CONFLICT (""UserID"", ""ItemCode"") DO NOTHING;
                            ELSE
                                INSERT INTO ""core"".""UserInventory"" (""UserID"", ""ItemCode"", ""Quantity"", ""IsPermanent"", ""TimestampAcquired"", ""Source"")
                                SELECT u.""ID"", elem, 1, true, NOW(), 'LegacyDropBackfill'
                                FROM ""core"".""Users"" u,
                                LATERAL jsonb_array_elements_text(u.""OwnedStoreItems""::jsonb) AS elem
                                WHERE u.""OwnedStoreItems"" IS NOT NULL AND u.""OwnedStoreItems""::text <> '[]'
                                ON CONFLICT (""UserID"", ""ItemCode"") DO NOTHING;
                            END IF;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill MatchAbilityEvents from legacy PlayerStatistics.AbilityHistory
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchAbilityEvents')
                           AND (SELECT COUNT(*) FROM ""stat"".""MatchAbilityEvents"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics' AND column_name = 'AbilityHistory') THEN
                            INSERT INTO ""stat"".""MatchAbilityEvents"" (""PlayerStatisticID"", ""MatchID"", ""AccountID"", ""HeroIdentifier"", ""AbilityIdentifier"", ""SlotIndex"", ""Level"", ""GameTimeSeconds"")
                            SELECT p.""ID"", p.""MatchID"", p.""AccountID"",
                                   COALESCE(elem.val->>'HeroName', elem.val->>'HeroIdentifier', p.""HeroIdentifier""),
                                   COALESCE(elem.val->>'AbilityName', elem.val->>'AbilityIdentifier', ''),
                                   COALESCE((elem.val->>'SlotIndex')::smallint, 0),
                                   COALESCE((elem.val->>'Level')::smallint, (ROW_NUMBER() OVER (PARTITION BY p.""ID"", COALESCE(elem.val->>'AbilityName', elem.val->>'AbilityIdentifier', '') ORDER BY elem.elem_ord))::smallint),
                                   COALESCE((elem.val->>'GameTimeSeconds')::integer, 0)
                            FROM ""stat"".""PlayerStatistics"" p,
                            LATERAL jsonb_array_elements(p.""AbilityHistory"") WITH ORDINALITY AS elem(val, elem_ord)
                            WHERE p.""AbilityHistory"" IS NOT NULL AND jsonb_typeof(p.""AbilityHistory"") = 'array'
                              AND COALESCE(elem.val->>'AbilityName', elem.val->>'AbilityIdentifier', '') <> '';
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill MatchFrags from legacy MatchStatistics.FragHistory
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchFrags')
                           AND (SELECT COUNT(*) FROM ""stat"".""MatchFrags"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'stat' AND table_name = 'MatchStatistics' AND column_name = 'FragHistory') THEN
                            INSERT INTO ""stat"".""MatchFrags"" (""MatchID"", ""KillerAccountID"", ""VictimAccountID"", ""GameTimeSeconds"", ""FirstBlood"")
                            SELECT m.""MatchID"",
                                   COALESCE((elem.val->>'SourceID')::integer, (elem.val->>'KillerAccountID')::integer, 0),
                                   COALESCE((elem.val->>'TargetID')::integer, (elem.val->>'VictimAccountID')::integer, 0),
                                   COALESCE((elem.val->>'GameTimeSeconds')::integer, (elem.val->>'GameTime')::integer, 0),
                                   COALESCE((elem.val->>'FirstBlood')::boolean, (ROW_NUMBER() OVER (PARTITION BY m.""MatchID"" ORDER BY COALESCE((elem.val->>'GameTimeSeconds')::integer, 0), elem.elem_ord) = 1))
                            FROM ""stat"".""MatchStatistics"" m,
                            LATERAL jsonb_array_elements(m.""FragHistory"") WITH ORDINALITY AS elem(val, elem_ord)
                            WHERE m.""FragHistory"" IS NOT NULL AND jsonb_typeof(m.""FragHistory"") = 'array';
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Backfill HeroItemComponents from legacy HeroItems.ComponentCodesJson
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroItemComponents')
                           AND (SELECT COUNT(*) FROM ""misc"".""HeroItemComponents"") = 0
                           AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'misc' AND table_name = 'HeroItems' AND column_name = 'ComponentCodesJson') THEN
                            INSERT INTO ""misc"".""HeroItemComponents"" (""ParentItemID"", ""ComponentItemCode"", ""Quantity"")
                            SELECT i.""ID"", elem, COUNT(*)::integer
                            FROM ""misc"".""HeroItems"" i,
                            LATERAL jsonb_array_elements_text(i.""ComponentCodesJson""::jsonb) AS elem
                            WHERE i.""ComponentCodesJson"" IS NOT NULL 
                              AND i.""ComponentCodesJson"" <> '' 
                              AND i.""ComponentCodesJson"" <> '[]'
                            GROUP BY i.""ID"", elem
                            ON CONFLICT (""ParentItemID"", ""ComponentItemCode"") DO NOTHING;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END $$;

                    -- Safe cleanup of legacy / redundant columns
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""AlternativeAvatarName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""WardProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""TauntProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""AnnouncerProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""CourierProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""AccountIconProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""ChatColourProductName"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""Inventory"";
                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""ItemHistory"";

                    -- Horizon A, B, C, D: Safe drop of obsolete shadow columns
                    DROP INDEX IF EXISTS ""stat"".""IX_PlayerStatistics_AbilityHistory_gin"";
                    DROP INDEX IF EXISTS ""stat"".""IX_MatchStatistics_FragHistory_gin"";

                    ALTER TABLE ""stat"".""PlayerStatistics"" DROP COLUMN IF EXISTS ""AbilityHistory"";
                    ALTER TABLE ""stat"".""MatchStatistics"" DROP COLUMN IF EXISTS ""FragHistory"";
                    ALTER TABLE ""misc"".""HeroItems"" DROP COLUMN IF EXISTS ""ComponentCodesJson"";

                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""SelectedStoreItems"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""IPAddressCollection"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""MACAddressCollection"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""BlockedPhrases"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""BannedPeers"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""FriendedPeers"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""IgnoredPeers"";

                    ALTER TABLE ""core"".""Users"" DROP COLUMN IF EXISTS ""OwnedStoreItems"";

                    ALTER TABLE ""misc"".""HeroGuides"" DROP COLUMN IF EXISTS ""StartingItems"";
                    ALTER TABLE ""misc"".""HeroGuides"" DROP COLUMN IF EXISTS ""EarlyGameItems"";
                    ALTER TABLE ""misc"".""HeroGuides"" DROP COLUMN IF EXISTS ""CoreItems"";
                    ALTER TABLE ""misc"".""HeroGuides"" DROP COLUMN IF EXISTS ""LuxuryItems"";
                    ALTER TABLE ""misc"".""HeroGuides"" DROP COLUMN IF EXISTS ""SituationalItems"";
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'misc' AND table_name = 'HeroGuides' AND column_name = 'AbilityQueue') THEN
                            ALTER TABLE ""misc"".""HeroGuides"" ALTER COLUMN ""AbilityQueue"" DROP NOT NULL;
                            ALTER TABLE ""misc"".""HeroGuides"" ALTER COLUMN ""AbilityQueue"" SET DEFAULT '';
                        END IF;
                    END $$;

                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""DefaultGuideIDs"";
                    ALTER TABLE ""core"".""Accounts"" DROP COLUMN IF EXISTS ""FavoriteGuideIDs"";

                    ALTER TABLE ""stat"".""Masteries"" DROP COLUMN IF EXISTS ""HeroExperiences"";

                    ALTER TABLE ""core"".""Accounts"" ADD COLUMN IF NOT EXISTS ""OwnerAccountID"" integer REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL;
                    CREATE INDEX IF NOT EXISTS ""IX_Accounts_OwnerAccountID"" ON ""core"".""Accounts"" (""OwnerAccountID"");
                    ALTER TABLE ""core"".""HostApplications"" ADD COLUMN IF NOT EXISTS ""EncryptedAuthToken"" character varying(512);
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""LastPlinkoDailyClaimUtc"" timestamp with time zone;
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""Argon2idPasswordHash"" character varying(256);
                    CREATE TABLE IF NOT EXISTS ""misc"".""UserWebRewards"" (
                        ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""UserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE,
                        ""AccountID"" integer,
                        ""RewardType"" character varying(64) NOT NULL,
                        ""RewardCode"" character varying(128) NOT NULL,
                        ""Name"" character varying(128) NOT NULL,
                        ""Description"" character varying(256),
                        ""IsEquipped"" boolean NOT NULL DEFAULT false,
                        ""Source"" character varying(64) NOT NULL DEFAULT 'Plinko',
                        ""TimestampAcquired"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampExpires"" timestamp with time zone,
                        ""MetadataJson"" text
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_UserWebRewards_UserID"" ON ""misc"".""UserWebRewards"" (""UserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserWebRewards_UserID_RewardType"" ON ""misc"".""UserWebRewards"" (""UserID"", ""RewardType"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserWebRewards_UserID_IsEquipped"" ON ""misc"".""UserWebRewards"" (""UserID"", ""IsEquipped"");
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""TwitchUsername"" character varying(64);
                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""TwitchId"" character varying(32);
                    CREATE INDEX IF NOT EXISTS ""IX_Users_TwitchUsername_Lower"" ON ""core"".""Users"" (lower(""TwitchUsername""));
                    CREATE TABLE IF NOT EXISTS ""core"".""StreamerApplications"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""UserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE RESTRICT,
                        ""MainAccountID"" integer NOT NULL REFERENCES ""core"".""Accounts""(""ID"") ON DELETE RESTRICT,
                        ""TwitchUsername"" character varying(64) NOT NULL,
                        ""TwitchId"" character varying(32),
                        ""TwitchChannelUrl"" character varying(256),
                        ""Status"" integer NOT NULL DEFAULT 0,
                        ""ApplicationNotes"" character varying(500),
                        ""StaffNotes"" character varying(500),
                        ""ReviewedByStaffName"" character varying(64),
                        ""TimestampApplied"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampReviewed"" timestamp with time zone
                    );
                    ALTER TABLE ""core"".""StreamerApplications"" ADD COLUMN IF NOT EXISTS ""TwitchId"" character varying(32);
                    CREATE INDEX IF NOT EXISTS ""IX_StreamerApplications_Status"" ON ""core"".""StreamerApplications"" (""Status"");
                    CREATE INDEX IF NOT EXISTS ""IX_StreamerApplications_MainAccountID"" ON ""core"".""StreamerApplications"" (""MainAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_StreamerApplications_TwitchUsername"" ON ""core"".""StreamerApplications"" (""TwitchUsername"");
                    CREATE INDEX IF NOT EXISTS ""IX_StreamerApplications_UserID"" ON ""core"".""StreamerApplications"" (""UserID"");
                    ALTER TABLE ""data"".""AccountStatistics"" ADD COLUMN IF NOT EXISTS ""mu"" double precision NOT NULL DEFAULT 25.0;
                    ALTER TABLE ""data"".""AccountStatistics"" ADD COLUMN IF NOT EXISTS ""sigma"" double precision NOT NULL DEFAULT 8.333333333333334;

                    ALTER TABLE ""core"".""Users"" ADD COLUMN IF NOT EXISTS ""InvitedByUserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL;
                    CREATE INDEX IF NOT EXISTS ""IX_Users_InvitedByUserID"" ON ""core"".""Users"" (""InvitedByUserID"");

                    CREATE TABLE IF NOT EXISTS ""core"".""UserInvites"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""Code"" character varying(32) NOT NULL,
                        ""CreatedByUserID"" integer NOT NULL REFERENCES ""core"".""Users""(""ID"") ON DELETE CASCADE,
                        ""RedeemedByUserID"" integer REFERENCES ""core"".""Users""(""ID"") ON DELETE SET NULL,
                        ""RedeemedByAccountID"" integer REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL,
                        ""TimestampCreated"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""TimestampExpires"" timestamp with time zone,
                        ""TimestampRedeemed"" timestamp with time zone,
                        ""IsRevoked"" boolean NOT NULL DEFAULT FALSE,
                        ""RevocationReason"" character varying(256),
                        ""CreatorIpAddress"" character varying(45),
                        ""RedeemerIpAddress"" character varying(45)
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_UserInvites_Code"" ON ""core"".""UserInvites"" (""Code"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInvites_CreatedByUserID"" ON ""core"".""UserInvites"" (""CreatedByUserID"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInvites_RedeemedByUserID"" ON ""core"".""UserInvites"" (""RedeemedByUserID"");

                    CREATE TABLE IF NOT EXISTS ""core"".""InviteSystemAuditLogs"" (
                        ""ID"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        ""StaffAccountID"" integer NOT NULL,
                        ""StaffAccountName"" character varying(64) NOT NULL,
                        ""InviteOnlyEnabled"" boolean NOT NULL,
                        ""Timestamp"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""Notes"" character varying(500),
                        ""IpAddress"" character varying(45)
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_InviteSystemAuditLogs_Timestamp"" ON ""core"".""InviteSystemAuditLogs"" (""Timestamp"");",
                        cancellationToken);

                    // 3. Foreign Key constraints and covering indexes
                    logger.LogInformation("Enforcing Foreign Key cascading constraints and covering indexes...");
                    await context.Database.ExecuteSqlRawAsync(@"
                    DO $$
                    BEGIN
                        -- core.AccountSelectedStoreItems -> core.Accounts(ID)
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountSelectedStoreItems_AccountID_fkey') AND
                           EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountSelectedStoreItems_Accounts_AccountID') THEN
                            ALTER TABLE ""core"".""AccountSelectedStoreItems"" DROP CONSTRAINT ""AccountSelectedStoreItems_AccountID_fkey"";
                        ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname IN ('FK_AccountSelectedStoreItems_Accounts_AccountID', 'AccountSelectedStoreItems_AccountID_fkey')) THEN
                            ALTER TABLE ""core"".""AccountSelectedStoreItems""
                                ADD CONSTRAINT ""FK_AccountSelectedStoreItems_Accounts_AccountID""
                                FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- core.AccountIpAddresses -> core.Accounts(ID)
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountIpAddresses_AccountID_fkey') AND
                           EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountIpAddresses_Accounts_AccountID') THEN
                            ALTER TABLE ""core"".""AccountIpAddresses"" DROP CONSTRAINT ""AccountIpAddresses_AccountID_fkey"";
                        ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname IN ('FK_AccountIpAddresses_Accounts_AccountID', 'AccountIpAddresses_AccountID_fkey')) THEN
                            ALTER TABLE ""core"".""AccountIpAddresses""
                                ADD CONSTRAINT ""FK_AccountIpAddresses_Accounts_AccountID""
                                FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- core.AccountMacAddresses -> core.Accounts(ID)
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountMacAddresses_AccountID_fkey') AND
                           EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountMacAddresses_Accounts_AccountID') THEN
                            ALTER TABLE ""core"".""AccountMacAddresses"" DROP CONSTRAINT ""AccountMacAddresses_AccountID_fkey"";
                        ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname IN ('FK_AccountMacAddresses_Accounts_AccountID', 'AccountMacAddresses_AccountID_fkey')) THEN
                            ALTER TABLE ""core"".""AccountMacAddresses""
                                ADD CONSTRAINT ""FK_AccountMacAddresses_Accounts_AccountID""
                                FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- core.AccountBlockedPhrases -> core.Accounts(ID)
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountBlockedPhrases_AccountID_fkey') AND
                           EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountBlockedPhrases_Accounts_AccountID') THEN
                            ALTER TABLE ""core"".""AccountBlockedPhrases"" DROP CONSTRAINT ""AccountBlockedPhrases_AccountID_fkey"";
                        ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname IN ('FK_AccountBlockedPhrases_Accounts_AccountID', 'AccountBlockedPhrases_AccountID_fkey')) THEN
                            ALTER TABLE ""core"".""AccountBlockedPhrases""
                                ADD CONSTRAINT ""FK_AccountBlockedPhrases_Accounts_AccountID""
                                FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;
                        -- stat.PlayerStatistics -> core.Accounts(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PlayerStatistics_Accounts_AccountID') THEN
                            ALTER TABLE ""stat"".""PlayerStatistics""
                                ADD CONSTRAINT ""FK_PlayerStatistics_Accounts_AccountID""
                                FOREIGN KEY (""AccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- stat.PlayerStatistics -> stat.MatchStatistics(MatchID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PlayerStatistics_MatchStatistics_MatchID') THEN
                            ALTER TABLE ""stat"".""PlayerStatistics""
                                ADD CONSTRAINT ""FK_PlayerStatistics_MatchStatistics_MatchID""
                                FOREIGN KEY (""MatchID"") REFERENCES ""stat"".""MatchStatistics""(""MatchID"") ON DELETE CASCADE;
                        END IF;

                        -- data.AccountStatistics -> core.Accounts(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountStatistics_Accounts_account_id') THEN
                            ALTER TABLE ""data"".""AccountStatistics""
                                ADD CONSTRAINT ""FK_AccountStatistics_Accounts_account_id""
                                FOREIGN KEY (""account_id"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.HeroGuides -> core.Accounts(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_HeroGuides_Accounts_AuthorID') THEN
                            ALTER TABLE ""misc"".""HeroGuides""
                                ADD CONSTRAINT ""FK_HeroGuides_Accounts_AuthorID""
                                FOREIGN KEY (""AuthorID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.PlayerReports -> core.Accounts(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PlayerReports_Accounts_ReportedAccountID') THEN
                            ALTER TABLE ""misc"".""PlayerReports""
                                ADD CONSTRAINT ""FK_PlayerReports_Accounts_ReportedAccountID""
                                FOREIGN KEY (""ReportedAccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PlayerReports_Accounts_ReporterAccountID') THEN
                            ALTER TABLE ""misc"".""PlayerReports""
                                ADD CONSTRAINT ""FK_PlayerReports_Accounts_ReporterAccountID""
                                FOREIGN KEY (""ReporterAccountID"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE SET NULL;
                        END IF;

                        -- misc.PlayerReports -> stat.MatchStatistics(MatchID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PlayerReports_MatchStatistics_MatchID') THEN
                            ALTER TABLE ""misc"".""PlayerReports""
                                ADD CONSTRAINT ""FK_PlayerReports_MatchStatistics_MatchID""
                                FOREIGN KEY (""MatchID"") REFERENCES ""stat"".""MatchStatistics""(""MatchID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.Notifications -> core.Accounts(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Notifications_Accounts_AccountId') THEN
                            ALTER TABLE ""misc"".""Notifications""
                                ADD CONSTRAINT ""FK_Notifications_Accounts_AccountId""
                                FOREIGN KEY (""AccountId"") REFERENCES ""core"".""Accounts""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.MatchEvidence -> stat.MatchStatistics(MatchID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_MatchEvidence_MatchStatistics_MatchID') THEN
                            ALTER TABLE ""misc"".""MatchEvidence""
                                ADD CONSTRAINT ""FK_MatchEvidence_MatchStatistics_MatchID""
                                FOREIGN KEY (""MatchID"") REFERENCES ""stat"".""MatchStatistics""(""MatchID"") ON DELETE CASCADE;
                        END IF;

                        -- stat.HeroMasteryExperiences -> stat.Masteries(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_HeroMasteryExperiences_Masteries_MasteryID') THEN
                            ALTER TABLE ""stat"".""HeroMasteryExperiences""
                                ADD CONSTRAINT ""FK_HeroMasteryExperiences_Masteries_MasteryID""
                                FOREIGN KEY (""MasteryID"") REFERENCES ""stat"".""Masteries""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.HeroGuideItems -> misc.HeroGuides(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_HeroGuideItems_HeroGuides_GuideID') THEN
                            ALTER TABLE ""misc"".""HeroGuideItems""
                                ADD CONSTRAINT ""FK_HeroGuideItems_HeroGuides_GuideID""
                                FOREIGN KEY (""GuideID"") REFERENCES ""misc"".""HeroGuides""(""ID"") ON DELETE CASCADE;
                        END IF;

                        -- misc.HeroGuideAbilities -> misc.HeroGuides(ID)
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_HeroGuideAbilities_HeroGuides_GuideID') THEN
                            ALTER TABLE ""misc"".""HeroGuideAbilities""
                                ADD CONSTRAINT ""FK_HeroGuideAbilities_HeroGuides_GuideID""
                                FOREIGN KEY (""GuideID"") REFERENCES ""misc"".""HeroGuides""(""ID"") ON DELETE CASCADE;
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Covering and performance indexes
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuides_AuthorID"" ON ""misc"".""HeroGuides"" (""AuthorID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideItems_GuideID"" ON ""misc"".""HeroGuideItems"" (""GuideID"");
                    CREATE INDEX IF NOT EXISTS ""IX_HeroGuideAbilities_GuideID"" ON ""misc"".""HeroGuideAbilities"" (""GuideID"");
                    CREATE INDEX IF NOT EXISTS ""IX_PlayerReports_ReportedAccountID"" ON ""misc"".""PlayerReports"" (""ReportedAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_PlayerReports_ReporterAccountID"" ON ""misc"".""PlayerReports"" (""ReporterAccountID"");
                    CREATE INDEX IF NOT EXISTS ""IX_PlayerReports_MatchID"" ON ""misc"".""PlayerReports"" (""MatchID"");
                    CREATE INDEX IF NOT EXISTS ""IX_Notifications_AccountId"" ON ""misc"".""Notifications"" (""AccountId"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchEvidence_MatchID"" ON ""misc"".""MatchEvidence"" (""MatchID"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchStatistics_HostAccountName"" ON ""stat"".""MatchStatistics"" (""HostAccountName"");
                    CREATE INDEX IF NOT EXISTS ""IX_Accounts_Name_Lower"" ON ""core"".""Accounts"" (lower(""Name""));
                    CREATE INDEX IF NOT EXISTS ""IX_Users_DiscordUsername_Lower"" ON ""core"".""Users"" (lower(""DiscordUsername""));
                    CREATE INDEX IF NOT EXISTS ""IX_Accounts_Name_Trgm"" ON ""core"".""Accounts"" USING gin (""Name"" extensions.gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ""IX_Users_DiscordUsername_Trgm"" ON ""core"".""Users"" USING gin (""DiscordUsername"" extensions.gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ""IX_Clans_Name_Trgm"" ON ""core"".""Clans"" USING gin (""Name"" extensions.gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ""IX_MatchStatistics_TimestampRecorded_Brin"" ON ""stat"".""MatchStatistics"" USING brin (""TimestampRecorded"");
                    CREATE INDEX IF NOT EXISTS ""IX_PlayerStatistics_HeroIdentifier"" ON ""stat"".""PlayerStatistics"" (""HeroIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_PlayerStatistics_AccountID_HeroIdentifier"" ON ""stat"".""PlayerStatistics"" (""AccountID"", ""HeroIdentifier"");
                    CREATE INDEX IF NOT EXISTS ""IX_MatchStatistics_Map"" ON ""stat"".""MatchStatistics"" (""Map"");
                    CREATE INDEX IF NOT EXISTS ""IX_UserInventory_UserID_StoreItemId"" ON ""core"".""UserInventory"" (""UserID"", ""StoreItemId"");
                    CREATE INDEX IF NOT EXISTS ""IX_HostApplications_Approved"" ON ""core"".""HostApplications"" (""MainAccountID"", ""Status"") WHERE ""Status"" = 2;",
                        cancellationToken);

                    // 3b. Enforce case-insensitive unique clan tag & name indexes (v5.0.2)
                    logger.LogInformation("[DatabaseInitializer] Verifying case-insensitive unique indexes on core.Clans (IX_Clans_Tag, IX_Clans_Name)...");
                    await context.Database.ExecuteSqlRawAsync(@"
                    DO $$
                    DECLARE
                        has_dup_tag boolean := false;
                        has_dup_name boolean := false;
                        tag_idx_ok boolean := false;
                        name_idx_ok boolean := false;
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.tables
                            WHERE table_schema = 'core' AND table_name = 'Clans'
                        ) THEN
                            RETURN;
                        END IF;

                        SELECT EXISTS (
                            SELECT 1 FROM ""core"".""Clans""
                            GROUP BY lower(""Tag"")
                            HAVING COUNT(*) > 1
                        ) INTO has_dup_tag;

                        SELECT EXISTS (
                            SELECT 1 FROM ""core"".""Clans""
                            GROUP BY lower(""Name"")
                            HAVING COUNT(*) > 1
                        ) INTO has_dup_name;

                        SELECT EXISTS (
                            SELECT 1 FROM pg_indexes
                            WHERE schemaname = 'core'
                              AND tablename = 'Clans'
                              AND indexname = 'IX_Clans_Tag'
                              AND indexdef ILIKE 'CREATE UNIQUE INDEX%lower(%'
                        ) INTO tag_idx_ok;

                        SELECT EXISTS (
                            SELECT 1 FROM pg_indexes
                            WHERE schemaname = 'core'
                              AND tablename = 'Clans'
                              AND indexname = 'IX_Clans_Name'
                              AND indexdef ILIKE 'CREATE UNIQUE INDEX%lower(%'
                        ) INTO name_idx_ok;

                        IF has_dup_tag THEN
                            RAISE WARNING '[DatabaseInitializer] Skipping unique index IX_Clans_Tag: case-insensitive duplicate clan tags detected in core.""Clans"".';
                        ELSIF NOT tag_idx_ok THEN
                            DROP INDEX IF EXISTS ""core"".""IX_Clans_Tag"";
                            CREATE UNIQUE INDEX ""IX_Clans_Tag"" ON ""core"".""Clans"" (lower(""Tag""));
                        END IF;

                        IF has_dup_name THEN
                            RAISE WARNING '[DatabaseInitializer] Skipping unique index IX_Clans_Name: case-insensitive duplicate clan names detected in core.""Clans"".';
                        ELSIF NOT name_idx_ok THEN
                            DROP INDEX IF EXISTS ""core"".""IX_Clans_Name"";
                            CREATE UNIQUE INDEX ""IX_Clans_Name"" ON ""core"".""Clans"" (lower(""Name""));
                        END IF;

                        IF NOT has_dup_tag AND NOT has_dup_name THEN
                            DROP INDEX IF EXISTS ""core"".""IX_Clans_Name_Tag"";
                        END IF;
                    END $$;",
                        cancellationToken);

                    // 4. Supabase Row Level Security (RLS) across all custom tables
                    logger.LogInformation(
                        "Hardening Row Level Security (RLS) across all custom tables in Supabase...");
                    await context.Database.ExecuteSqlRawAsync(@"
                    DO $$
                    DECLARE
                        t text;
                        s text;
                        schemas text[] := ARRAY['core', 'data', 'stat', 'misc', 'nexus_auth'];
                        tables_to_rls text[][] := ARRAY[
                            ['core', 'Accounts'],
                            ['core', 'AccountSelectedStoreItems'],
                            ['core', 'AccountIpAddresses'],
                            ['core', 'AccountMacAddresses'],
                            ['core', 'AccountBlockedPhrases'],
                            ['core', 'AccountPeers'],
                            ['core', 'Clans'],
                            ['core', 'HostApplications'],
                            ['core', 'StreamerApplications'],
                            ['core', 'SystemTelemetryMetrics'],
                            ['core', 'DeveloperIssues'],
                            ['core', 'Users'],
                            ['core', 'EconomyTransactions'],
                            ['core', 'Friendships'],
                            ['core', 'UserInventory'],
                            ['core', 'AccountSessions'],
                            ['core', 'AccountGuidePreferences'],
                            ['data', 'AccountStatistics'],
                            ['data', 'MatchmakingQueueWaitEntries'],
                            ['data', 'StoreItems'],
                            ['stat', 'Masteries'],
                            ['stat', 'HeroMasteryExperiences'],
                            ['stat', 'MatchStatistics'],
                            ['stat', 'MatchBans'],
                            ['stat', 'PlayerStatistics'],
                            ['stat', 'MatchPlayerInventories'],
                            ['stat', 'MatchItemEvents'],
                            ['stat', 'MatchAbilityEvents'],
                            ['stat', 'MatchFrags'],
                            ['stat', 'MatchFragAssists'],
                            ['misc', 'HeroDetails'],
                            ['misc', 'HeroDifficultyVotes'],
                            ['misc', 'HeroGuides'],
                            ['misc', 'HeroGuideItems'],
                            ['misc', 'HeroGuideAbilities'],
                            ['misc', 'HeroItems'],
                            ['misc', 'HeroItemComponents'],
                            ['misc', 'MatchEvidence'],
                            ['misc', 'Notifications'],
                            ['misc', 'PlayerReports'],
                            ['misc', 'RedeemableCodes'],
                            ['misc', 'UserWebRewards'],
                            ['misc', 'LoreArcs'],
                            ['misc', 'LoreComics'],
                            ['misc', 'LorePages'],
                            ['misc', 'LoreCredits'],
                            ['misc', 'LoreRatings'],
                            ['nexus_auth', 'Roles'],
                            ['nexus_auth', 'Tokens'],
                            ['core', 'InviteSystemAuditLogs'],
                            ['core', 'UserInvites'],
                            ['stat', 'MatchRecords']
                        ];
                        i int;
                    BEGIN
                        FOR i IN 1..array_length(tables_to_rls, 1) LOOP
                            s := tables_to_rls[i][1];
                            t := tables_to_rls[i][2];
                            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = s AND table_name = t) THEN
                                EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY;', s, t);
                            END IF;
                        END LOOP;

                        -- Dynamic safeguard: Guarantee that all existing and future base tables in custom schemas have RLS enabled
                        FOREACH s IN ARRAY schemas LOOP
                            FOR t IN SELECT table_name FROM information_schema.tables WHERE table_schema = s AND table_type = 'BASE TABLE' LOOP
                                BEGIN
                                    EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY;', s, t);
                                EXCEPTION WHEN OTHERS THEN
                                    NULL;
                                END;
                            END LOOP;
                        END LOOP;
                    END $$;

                    -- Public Read Policies for static game metadata
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'data' AND table_name = 'StoreItems') THEN
                            DROP POLICY IF EXISTS ""Public Read StoreItems"" ON ""data"".""StoreItems"";
                            CREATE POLICY ""Public Read StoreItems"" ON ""data"".""StoreItems"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroDetails') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroDetails"" ON ""misc"".""HeroDetails"";
                            CREATE POLICY ""Public Read HeroDetails"" ON ""misc"".""HeroDetails"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroItems') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroItems"" ON ""misc"".""HeroItems"";
                            CREATE POLICY ""Public Read HeroItems"" ON ""misc"".""HeroItems"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroGuides') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroGuides"" ON ""misc"".""HeroGuides"";
                            CREATE POLICY ""Public Read HeroGuides"" ON ""misc"".""HeroGuides"" FOR SELECT USING (""Public"" = true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroGuideItems') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroGuideItems"" ON ""misc"".""HeroGuideItems"";
                            CREATE POLICY ""Public Read HeroGuideItems"" ON ""misc"".""HeroGuideItems"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroGuideAbilities') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroGuideAbilities"" ON ""misc"".""HeroGuideAbilities"";
                            CREATE POLICY ""Public Read HeroGuideAbilities"" ON ""misc"".""HeroGuideAbilities"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'LoreArcs') THEN
                            DROP POLICY IF EXISTS ""Public Read LoreArcs"" ON ""misc"".""LoreArcs"";
                            CREATE POLICY ""Public Read LoreArcs"" ON ""misc"".""LoreArcs"" FOR SELECT USING (""IsPublished"" = true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'LoreComics') THEN
                            DROP POLICY IF EXISTS ""Public Read LoreComics"" ON ""misc"".""LoreComics"";
                            CREATE POLICY ""Public Read LoreComics"" ON ""misc"".""LoreComics"" FOR SELECT USING (""IsPublished"" = true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'LorePages') THEN
                            DROP POLICY IF EXISTS ""Public Read LorePages"" ON ""misc"".""LorePages"";
                            CREATE POLICY ""Public Read LorePages"" ON ""misc"".""LorePages"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'LoreCredits') THEN
                            DROP POLICY IF EXISTS ""Public Read LoreCredits"" ON ""misc"".""LoreCredits"";
                            CREATE POLICY ""Public Read LoreCredits"" ON ""misc"".""LoreCredits"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'LoreRatings') THEN
                            DROP POLICY IF EXISTS ""Public Read LoreRatings"" ON ""misc"".""LoreRatings"";
                            CREATE POLICY ""Public Read LoreRatings"" ON ""misc"".""LoreRatings"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'nexus_auth' AND table_name = 'Roles') THEN
                            DROP POLICY IF EXISTS ""Public Read Roles"" ON ""nexus_auth"".""Roles"";
                            CREATE POLICY ""Public Read Roles"" ON ""nexus_auth"".""Roles"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'Clans') THEN
                            DROP POLICY IF EXISTS ""Public Read Clans"" ON ""core"".""Clans"";
                            CREATE POLICY ""Public Read Clans"" ON ""core"".""Clans"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchStatistics') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchStatistics"" ON ""stat"".""MatchStatistics"";
                            CREATE POLICY ""Public Read MatchStatistics"" ON ""stat"".""MatchStatistics"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchRecords') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchRecords"" ON ""stat"".""MatchRecords"";
                            CREATE POLICY ""Public Read MatchRecords"" ON ""stat"".""MatchRecords"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'PlayerStatistics') THEN
                            DROP POLICY IF EXISTS ""Public Read PlayerStatistics"" ON ""stat"".""PlayerStatistics"";
                            CREATE POLICY ""Public Read PlayerStatistics"" ON ""stat"".""PlayerStatistics"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'data' AND table_name = 'AccountStatistics') THEN
                            DROP POLICY IF EXISTS ""Public Read AccountStatistics"" ON ""data"".""AccountStatistics"";
                            CREATE POLICY ""Public Read AccountStatistics"" ON ""data"".""AccountStatistics"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'Masteries') THEN
                            DROP POLICY IF EXISTS ""Public Read Masteries"" ON ""stat"".""Masteries"";
                            CREATE POLICY ""Public Read Masteries"" ON ""stat"".""Masteries"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'HeroMasteryExperiences') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroMasteryExperiences"" ON ""stat"".""HeroMasteryExperiences"";
                            CREATE POLICY ""Public Read HeroMasteryExperiences"" ON ""stat"".""HeroMasteryExperiences"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchBans') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchBans"" ON ""stat"".""MatchBans"";
                            CREATE POLICY ""Public Read MatchBans"" ON ""stat"".""MatchBans"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'AccountGuidePreferences') THEN
                            DROP POLICY IF EXISTS ""Public Read AccountGuidePreferences"" ON ""core"".""AccountGuidePreferences"";
                            CREATE POLICY ""Public Read AccountGuidePreferences"" ON ""core"".""AccountGuidePreferences"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchAbilityEvents') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchAbilityEvents"" ON ""stat"".""MatchAbilityEvents"";
                            CREATE POLICY ""Public Read MatchAbilityEvents"" ON ""stat"".""MatchAbilityEvents"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchFrags') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchFrags"" ON ""stat"".""MatchFrags"";
                            CREATE POLICY ""Public Read MatchFrags"" ON ""stat"".""MatchFrags"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'stat' AND table_name = 'MatchFragAssists') THEN
                            DROP POLICY IF EXISTS ""Public Read MatchFragAssists"" ON ""stat"".""MatchFragAssists"";
                            CREATE POLICY ""Public Read MatchFragAssists"" ON ""stat"".""MatchFragAssists"" FOR SELECT USING (true);
                        END IF;

                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'misc' AND table_name = 'HeroItemComponents') THEN
                            DROP POLICY IF EXISTS ""Public Read HeroItemComponents"" ON ""misc"".""HeroItemComponents"";
                            CREATE POLICY ""Public Read HeroItemComponents"" ON ""misc"".""HeroItemComponents"" FOR SELECT USING (true);
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END $$;

                    -- Service Role Full Access Policies across custom schemas
                    DO $$
                    DECLARE
                        t text;
                        schemas text[] := ARRAY['core', 'data', 'stat', 'misc', 'nexus_auth'];
                        s text;
                    BEGIN
                        FOREACH s IN ARRAY schemas LOOP
                            FOR t IN SELECT table_name FROM information_schema.tables WHERE table_schema = s LOOP
                                BEGIN
                                    EXECUTE format('DROP POLICY IF EXISTS ""Service Role All"" ON %I.%I;', s, t);
                                    EXECUTE format('CREATE POLICY ""Service Role All"" ON %I.%I FOR ALL TO service_role USING (true) WITH CHECK (true);', s, t);
                                EXCEPTION WHEN OTHERS THEN
                                    NULL;
                                END;
                            END LOOP;
                        END LOOP;
                    END $$;", cancellationToken);

                    logger.LogInformation(
                        "Successfully verified and hardened all MERRICK tables, foreign keys, indexes, and RLS policies in PostgreSQL / Supabase");

                    // 3. Seed data inside the advisory lock
                    await SeedAsync(context, cancellationToken);
                }
                finally
                {
                    if (lockAcquired)
                        try
                        {
                            await using DbCommand unlockCmd = context.Database.GetDbConnection().CreateCommand();
                            unlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId});";
                            if (context.Database.GetDbConnection().State == ConnectionState.Open)
                                await unlockCmd.ExecuteScalarAsync(CancellationToken.None);
                        }
                        catch (Exception unlockEx)
                        {
                            logger.LogDebug(unlockEx, "Advisory unlock notice");
                        }
                }
            });

            if (skippedDueToConcurrentInit)
            {
                const long lockId = 742918235891234192L;
                Stopwatch peerWaitWatch = Stopwatch.StartNew();
                const int timeoutSeconds = 180;
                bool peerReady = false;

                while (peerWaitWatch.Elapsed.TotalSeconds < timeoutSeconds && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    int elapsedSec = (int)peerWaitWatch.Elapsed.TotalSeconds;
                    logger.LogInformation("[Bootstrap] waiting for peer initializer... ({ElapsedSeconds}s)", elapsedSec);

                    try
                    {
                        if (context.Database.GetDbConnection().State != ConnectionState.Open)
                        {
                            await context.Database.OpenConnectionAsync(cancellationToken);
                        }

                        await using DbCommand checkCmd = context.Database.GetDbConnection().CreateCommand();
                        checkCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'core' AND table_name = 'Users');";
                        object? existsResult = await checkCmd.ExecuteScalarAsync(cancellationToken);
                        bool usersTableExists = existsResult is true or 1 or "t" or "true" or "True";

                        if (usersTableExists)
                        {
                            await using DbCommand testLockCmd = context.Database.GetDbConnection().CreateCommand();
                            testLockCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId});";
                            object? testLockRes = await testLockCmd.ExecuteScalarAsync(cancellationToken);
                            bool testLockAcquired = testLockRes is true or 1 or "t" or "true" or "True";

                            if (testLockAcquired)
                            {
                                try
                                {
                                    await using DbCommand testUnlockCmd = context.Database.GetDbConnection().CreateCommand();
                                    testUnlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId});";
                                    await testUnlockCmd.ExecuteScalarAsync(CancellationToken.None);
                                }
                                catch (Exception unlockEx)
                                {
                                    logger.LogDebug(unlockEx, "Peer wait advisory unlock notice");
                                }

                                peerReady = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[Bootstrap] Exception while polling peer readiness: {Message}", ex.Message);
                    }
                }

                if (peerReady)
                {
                    logger.LogInformation("[Bootstrap] Peer DatabaseInitializer completed successfully after {ElapsedSeconds}s.", (int)peerWaitWatch.Elapsed.TotalSeconds);
                    IsCompleted = true;
                    return;
                }

                LastError = "Timed out after 180s waiting for peer DatabaseInitializer.";
                logger.LogError("[Bootstrap] Timed out after 180s waiting for peer DatabaseInitializer.");
                return;
            }

            /*
            {
                return;
            }
        }
        */
        }
        else
        {
            logger.LogInformation("Applying SQL Server migrations for MERRICK");
            await strategy.ExecuteAsync(context.Database.MigrateAsync, cancellationToken);
            await SeedAsync(context, cancellationToken);
        }

        logger.LogInformation("Database Initialization Completed After {ElapsedMilliseconds}ms ({DbSystem})",
            stopwatch.ElapsedMilliseconds, dbSystem);
        IsCompleted = true;
    }

    private async Task SeedAsync(MerrickContext context, CancellationToken cancellationToken)
    {
        IConfiguration? configuration = serviceProvider.GetService<IConfiguration>();
        IExecutionStrategy seedStrategy = context.Database.CreateExecutionStrategy();

        await seedStrategy.ExecuteAsync(async () =>
        {
            logger.LogInformation("Verifying seed status in database...");

            try
            {
                IHostEnvironment? hostEnv = serviceProvider.GetService<IHostEnvironment>();
                bool allowDevSeed = hostEnv is not null
                    ? (hostEnv.IsDevelopment() || string.Equals(configuration?["KONGOR_SEED_DEV_DATA"], "true", StringComparison.OrdinalIgnoreCase))
                    : SeedDataHandlers.IsDevelopmentSeedEnabled(configuration);
                bool usersExist = await context.Users.AnyAsync(cancellationToken);
                if (!usersExist && allowDevSeed)
                {
                    logger.LogInformation("Initial database seed required. Executing all stages...");
                    await SeedDataHandlers.SeedUsers(context, logger, cancellationToken);
                    await SeedDataHandlers.SeedClans(context, logger, cancellationToken);
                    await SeedDataHandlers.SeedAccounts(context, logger, cancellationToken);
                }

                // Hero Items, Details, and Guides are seeded independently with their own internal idempotency checks
                await SeedDataHandlers.SeedHeroGuides(context, logger, cancellationToken);
                await SeedDataHandlers.SeedHeroItems(context, logger, cancellationToken);
                await SeedDataHandlers.SeedHeroDetails(context, logger, cancellationToken);

                // Ensure store items catalog is seeded
                await SeedDataHandlers.SeedStoreItems(context, logger, cancellationToken);

                // Ensure staff accounts, permissions, and host profiles are synchronized
                configuration ??= serviceProvider.GetService<IConfiguration>();
                if (allowDevSeed)
                {
                    await SeedDataHandlers.EnsureStaffAccountsAsync(context, logger, cancellationToken, configuration);
                }
                await SeedDataHandlers.SeedLoreComics(context, logger, cancellationToken);
                if (allowDevSeed)
                {
                    await SeedDataHandlers.SeedMatchStatisticsAndRankings(context, logger, cancellationToken);
                }

                // Deprecate and drop legacy OwnedStoreItems column safely
                await EnsureLegacyOwnedStoreItemsDroppedAsync(context, logger, cancellationToken);

                if (configuration is not null)
                {
                    await SeedDataHandlers.EnsureStandaloneBootstrapAsync(context, configuration, logger, cancellationToken);
                }

                await SeedDataHandlers.EnsureQueueArcadeHostAccountsAsync(context, configuration, logger, cancellationToken);
                await SeedDataHandlers.EnsureSharedInviteCodesAsync(context, logger, cancellationToken);
                logger.LogInformation("Database seed verified successfully.");
            }
            catch (Exception ex)
            {
                if (string.Equals(configuration?["KONGOR_STANDALONE"], "true", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError(ex, "[SeedAsync] Fatal exception occurred during standalone bootstrap seeding execution");
                    throw;
                }

                logger.LogWarning(ex, "[SeedAsync] Non-fatal exception occurred during seeding execution");
            }
        });
    }

    public static async Task EnsureLegacyOwnedStoreItemsDroppedAsync(MerrickContext context, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        bool isPostgres = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        if (isPostgres)
        {
            try
            {
                logger?.LogInformation("[DatabaseInitializer] Verifying core.Users.OwnedStoreItems deprecation and 3NF inventory alignment...");
                await context.Database.ExecuteSqlRawAsync(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 
                        FROM information_schema.columns 
                        WHERE table_schema = 'core' 
                          AND table_name = 'Users' 
                          AND column_name = 'OwnedStoreItems'
                    ) THEN
                        -- Safely backfill any lingering items before dropping
                        INSERT INTO ""core"".""UserInventory"" (""UserID"", ""ItemCode"", ""Quantity"", ""IsPermanent"", ""TimestampAcquired"", ""Source"")
                        SELECT u.""ID"", jsonb_array_elements_text(u.""OwnedStoreItems""::jsonb), 1, true, NOW(), 'LegacyDropBackfill'
                        FROM ""core"".""Users"" u
                        WHERE u.""OwnedStoreItems"" IS NOT NULL AND u.""OwnedStoreItems""::text <> '[]'
                        ON CONFLICT (""UserID"", ""ItemCode"") DO NOTHING;

                        ALTER TABLE ""core"".""Users"" DROP COLUMN ""OwnedStoreItems"";
                    END IF;
                END $$;", cancellationToken);
                logger?.LogInformation("[DatabaseInitializer] core.Users.OwnedStoreItems successfully verified and dropped (3NF normalized).");
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "[DatabaseInitializer] Notice during legacy OwnedStoreItems drop: {Message}", ex.Message);
            }
        }
    }

    [GeneratedRegex(@"DO\s+\$EF\$[\s\S]*?\$EF\$;?", RegexOptions.IgnoreCase)]
    private static partial Regex DoBlockRegex();

    [GeneratedRegex(@"CREATE\s+TABLE\s+""?([a-zA-Z0-9_]+)""?\.""?([a-zA-Z0-9_]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"CREATE\s+(UNIQUE\s+)?INDEX\s+""?([a-zA-Z0-9_]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndexRegex();

    [GeneratedRegex(@"INSERT\s+INTO\s+([^\r\n;]+)\s+VALUES\s+([^\r\n;]+);", RegexOptions.IgnoreCase)]
    private static partial Regex InsertRegex();

    [GeneratedRegex(@"CREATE\s+COLLATION\s+(?!IF\s+NOT\s+EXISTS\s+)(""?)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateCollationRegex();
}
