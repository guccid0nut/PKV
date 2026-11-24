// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging;

namespace MERRICK.DatabaseContext.Logging;

public static partial class Log
{
    // AccountRepository
    [LoggerMessage(Level = LogLevel.Debug, Message = "[AccountRepository] Fetching account by ID: {AccountId}")]
    public static partial void LogFetchingAccountById(this ILogger logger, int accountId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AccountRepository] Fetching account by name: {AccountName}")]
    public static partial void LogFetchingAccountByName(this ILogger logger, string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AccountRepository] Created account {AccountName} with ID {AccountId}")]
    public static partial void LogAccountCreated(this ILogger logger, string accountName, int accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AccountRepository] Updated account {AccountName} (ID: {AccountId})")]
    public static partial void LogAccountUpdated(this ILogger logger, string accountName, int accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AccountRepository] Account not found for query: {Query}")]
    public static partial void LogAccountNotFound(this ILogger logger, string query);

    // InventoryRepository
    [LoggerMessage(Level = LogLevel.Information, Message = "[InventoryRepository] Deducted {Amount} {CurrencyType} from Account #{AccountId}. New Balance: {NewBalance}")]
    public static partial void LogCurrencyDeducted(this ILogger logger, int amount, string currencyType, int accountId, int newBalance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InventoryRepository] Cannot deduct currency: Account #{AccountId} not found")]
    public static partial void LogCannotDeductAccountNotFound(this ILogger logger, int accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InventoryRepository] Insufficient funds for Account #{AccountId}. Required: {Required}, Available: {Available}")]
    public static partial void LogInsufficientFunds(this ILogger logger, int accountId, int required, int available);

    [LoggerMessage(Level = LogLevel.Information, Message = "[InventoryRepository] Added {Amount} {CurrencyType} to Account #{AccountId}. New Balance: {NewBalance}")]
    public static partial void LogCurrencyAdded(this ILogger logger, int amount, string currencyType, int accountId, int newBalance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[InventoryRepository] Cannot add currency: Account #{AccountId} not found")]
    public static partial void LogCannotAddAccountNotFound(this ILogger logger, int accountId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InventoryRepository] Setting selected store items for Account #{AccountId}")]
    public static partial void LogSettingSelectedStoreItems(this ILogger logger, int accountId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InventoryRepository] Fetching StoreItem by Code/Name '{ItemCode}'")]
    public static partial void LogFetchingStoreItem(this ILogger logger, string itemCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InventoryRepository] Fetching full Store catalog")]
    public static partial void LogFetchingStoreCatalog(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InventoryRepository] Fetching web rewards for User #{UserId}")]
    public static partial void LogFetchingWebRewards(this ILogger logger, int userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[InventoryRepository] Claimed Web Reward #{RewardId} for User #{UserId}")]
    public static partial void LogClaimedWebReward(this ILogger logger, int rewardId, int userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[InventoryRepository] Getting Plinko status for User #{UserId}")]
    public static partial void LogGettingPlinkoStatus(this ILogger logger, int userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[InventoryRepository] Redeeming promo code '{Code}' for User #{UserId}")]
    public static partial void LogRedeemingPromoCode(this ILogger logger, string code, int userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[InventoryRepository] Successfully redeemed promo code '{Code}' for User #{UserId}")]
    public static partial void LogPromoCodeRedeemed(this ILogger logger, string code, int userId);

    // MatchHistoryRepository
    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchHistoryRepository] Non-fatal exception occurred during match history query")]
    public static partial void LogMatchHistoryQueryWarning(this ILogger logger, Exception ex);

    // SessionRepository
    [LoggerMessage(Level = LogLevel.Debug, Message = "[SessionRepository] Session cache lookup for cookie prefix '{CookiePrefix}'")]
    public static partial void LogSessionLookup(this ILogger logger, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SessionRepository] Redis session lookup failed; falling back to PostgreSQL")]
    public static partial void LogSessionRedisFallback(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[SessionRepository] Session restored from PostgreSQL fallback for cookie prefix '{CookiePrefix}' (User: {AccountName})")]
    public static partial void LogSessionRestoredFromDb(this ILogger logger, string cookiePrefix, string accountName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[SessionRepository] Database session lookup encountered an unhandled error")]
    public static partial void LogSessionDbError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[SessionRepository] Removed session cookie from Redis cache")]
    public static partial void LogSessionRemovedFromRedis(this ILogger logger);

    // AdminMetricsService
    [LoggerMessage(Level = LogLevel.Warning, Message = "[AdminMetricsService] Failed reading SystemTelemetryMetrics from SQL DB.")]
    public static partial void LogFailedReadingSystemMetrics(this ILogger logger, Exception ex);

    // DatabaseInitializer
    [LoggerMessage(Level = LogLevel.Error, Message = "[DatabaseInitializer] Startup database initialization encountered an exception.")]
    public static partial void LogDatabaseInitError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initializing PostgreSQL / Supabase schema for MERRICK")]
    public static partial void LogInitPostgreSqlSchema(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ensuring PostgreSQL schemas and extensions exist...")]
    public static partial void LogEnsuringPostgreSqlSchemas(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully executed baseline DDL creation script in Supabase.")]
    public static partial void LogBaselineDdlSuccess(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing DDL migrations for StoreItems and normalized tables...")]
    public static partial void LogExecutingDdlMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating normalized tables and schema modifications...")]
    public static partial void LogCreatingNormalizedTables(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enforcing Foreign Key cascading constraints and covering indexes...")]
    public static partial void LogEnforcingForeignKeys(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying SQL Server migrations for MERRICK")]
    public static partial void LogApplyingSqlServerMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database Initialization Completed After {ElapsedMilliseconds}ms ({DbSystem})")]
    public static partial void LogDatabaseInitCompleted(this ILogger logger, long elapsedMilliseconds, string dbSystem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verifying seed status in database...")]
    public static partial void LogVerifyingSeedStatus(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initial database seed required. Executing all stages...")]
    public static partial void LogInitialSeedRequired(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database seed verified successfully.")]
    public static partial void LogDatabaseSeedVerified(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SeedAsync] Non-fatal exception occurred during seeding execution")]
    public static partial void LogSeedWarning(this ILogger logger, Exception ex);
}
