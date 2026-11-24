// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging;

namespace ZORGATH.WebPortal.API.Logging;

public static partial class Log
{
    // UserApiController
    [LoggerMessage(Level = LogLevel.Information, Message = "[UserAPI] User #{UserId} ({Username}) logged in successfully from IP: {IpAddress}")]
    public static partial void LogUserLoggedIn(this ILogger logger, int userId, string username, string ipAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[UserAPI] Login failed for username: {Username}. Reason: {Reason}")]
    public static partial void LogUserLoginFailed(this ILogger logger, string username, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[UserAPI] User #{UserId} registered successfully with email: {Email}")]
    public static partial void LogUserRegistered(this ILogger logger, int userId, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "[UserAPI] User #{UserId} updated profile")]
    public static partial void LogUserProfileUpdated(this ILogger logger, int userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[UserAPI] User not found for ID: {UserId}")]
    public static partial void LogUserNotFound(this ILogger logger, int userId);

    // AdminApiController
    [LoggerMessage(Level = LogLevel.Information, Message = "[AdminAPI] Admin #{AdminId} executed command '{Command}' on target #{TargetId}")]
    public static partial void LogAdminAction(this ILogger logger, int adminId, string command, int targetId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AdminAPI] Unauthorized admin access attempt by user #{UserId}")]
    public static partial void LogUnauthorizedAdminAccess(this ILogger logger, int userId);

    // PlayerStatsApiController
    [LoggerMessage(Level = LogLevel.Debug, Message = "[StatsAPI] Fetching stats for player #{AccountId}")]
    public static partial void LogFetchingPlayerStats(this ILogger logger, int accountId);

    // StoreApiController
    [LoggerMessage(Level = LogLevel.Information, Message = "[StoreAPI] User #{UserId} purchased item #{ItemId} for {Price} coins")]
    public static partial void LogStorePurchase(this ILogger logger, int userId, int itemId, int price);

    // EmailService
    [LoggerMessage(Level = LogLevel.Information, Message = "[EmailService] Sending '{Subject}' email to {Recipient}")]
    public static partial void LogSendingEmail(this ILogger logger, string subject, string recipient);

    [LoggerMessage(Level = LogLevel.Error, Message = "[EmailService] Failed to send email to {Recipient}")]
    public static partial void LogEmailSendFailed(this ILogger logger, Exception ex, string recipient);
}
