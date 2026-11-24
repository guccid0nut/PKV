// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ZORGATH.WebPortal.API.Models.Configuration;

public class OperationalConfigurationEmail
{
    public required string ApiKey { get; set; }
    public required string FromEmail { get; set; }
    public string? FromName { get; set; }

    /// <summary>
    ///     Strictly opt-in (default: false). Project KONGOR V uses Discord-only authentication by default
    ///     and does not require or collect player email addresses. Community self-hosters who opt into
    ///     SMTP delivery must explicitly set SmtpEnabled=true, Provider="Smtp", and SmtpHost.
    /// </summary>
    public bool SmtpEnabled { get; set; } = false;
    public string? Provider { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool SmtpUseSsl { get; set; } = true;
}
