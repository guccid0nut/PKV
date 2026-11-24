// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ZORGATH.WebPortal.API.Services.Email;

/// <summary>
///     Self-hosted RFC 5321 SMTP email delivery service backed by <see cref="SmtpClient" /> (MailKit, MIT)
///     so community operators can run transactional registration mail without proprietary SaaS vendor lock-in.
/// </summary>
public sealed class SmtpEmailService(
    IOptions<OperationalConfiguration> configuration,
    ILogger<SmtpEmailService> logger,
    IWebHostEnvironment hostEnvironment,
    Func<ISmtpClient>? smtpClientFactory = null) : IEmailService
{
    private string BaseUrl =>
        string.IsNullOrWhiteSpace(configuration.Value.UIBaseUrl)
            ? hostEnvironment.IsDevelopment() ? "https://localhost:5557" : "https://portal.ui.kongor.net"
            : configuration.Value.UIBaseUrl;

    public Task<bool> SendEmailAddressRegistrationLink(
        string emailAddress,
        string token,
        CancellationToken cancellationToken = default)
    {
        string link = BaseUrl + "/register/" + token;
        const string subject = "Verify Email Address";
        string body = "You need to verify your email address before you can create your Heroes Of Newerth account."
                      + Environment.NewLine + "Please follow the link below to continue:"
                      + Environment.NewLine + Environment.NewLine + link
                      + Environment.NewLine + Environment.NewLine + "Regards,"
                      + Environment.NewLine + "The Project KONGOR Team";

        return SendSmtpAsync(emailAddress, subject, body, cancellationToken);
    }

    public Task<bool> SendEmailAddressRegistrationConfirmation(
        string emailAddress,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        const string subject = "Email Address Verified";
        string body = $"Hi {accountName},"
                      + Environment.NewLine + Environment.NewLine
                      + "Congratulations on verifying the email address linked to your Heroes Of Newerth account. "
                      + "Please remember to be respectful to your fellow Newerthians, and to maintain your account in good standing. "
                      + "Suspensions carry over across accounts so, if you receive a suspension, you will not be able to log back into the game by creating a new account."
                      + Environment.NewLine + Environment.NewLine + "Regards,"
                      + Environment.NewLine + "The Project KONGOR Team";

        return SendSmtpAsync(emailAddress, subject, body, cancellationToken);
    }

    internal async Task<bool> SendSmtpAsync(
        string toEmail,
        string subject,
        string plainTextContent,
        CancellationToken cancellationToken = default)
    {
        OperationalConfigurationEmail emailCfg = configuration.Value.Email;
        if (!emailCfg.SmtpEnabled || string.IsNullOrWhiteSpace(emailCfg.SmtpHost))
        {
            logger.LogInformation(
                "[SmtpEmailService] Optional SMTP delivery is disabled (SmtpEnabled={SmtpEnabled}, Discord-only auth active); skipping SMTP send for '{Subject}'.",
                emailCfg.SmtpEnabled,
                subject);
            return true;
        }

        try
        {
            MimeMessage message = new();
            message.From.Add(new MailboxAddress(emailCfg.FromName ?? "Project KONGOR", emailCfg.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = plainTextContent };

            using ISmtpClient client = smtpClientFactory?.Invoke() ?? new SmtpClient();
            SecureSocketOptions tlsMode = emailCfg.SmtpUseSsl
                ? (emailCfg.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
                : SecureSocketOptions.None;

            await client.ConnectAsync(emailCfg.SmtpHost, emailCfg.SmtpPort, tlsMode, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(emailCfg.SmtpUsername))
            {
                await client.AuthenticateAsync(emailCfg.SmtpUsername, emailCfg.SmtpPassword ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "[SmtpEmailService] Delivered '{Subject}' via SMTP host {SmtpHost}:{SmtpPort} (TlsMode={TlsMode}).",
                subject,
                emailCfg.SmtpHost,
                emailCfg.SmtpPort,
                tlsMode);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[SmtpEmailService] Failed delivering '{Subject}' via SMTP host {SmtpHost}:{SmtpPort}.",
                subject,
                emailCfg.SmtpHost,
                emailCfg.SmtpPort);
            return false;
        }
    }
}
