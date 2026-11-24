// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Logging;

public static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "API Registration Failed: {StatusCode} {Error}")]
    public static partial void LogApiRegistrationFailed(this ILogger logger, int statusCode, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Exception during registration")]
    public static partial void LogRegistrationException(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Auth] Blocked portal login for banned account: {AccountName}")]
    public static partial void LogBlockedBannedLogin(this ILogger logger, string accountName);
}
