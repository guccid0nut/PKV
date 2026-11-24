// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Configuration;

/// <summary>
///     Validates <see cref="CDNOptions" /> at startup before synchronisation or match server launch.
/// </summary>
public sealed class CDNOptionsValidator : IValidateOptions<CDNOptions>
{
    public ValidateOptionsResult Validate(string? name, CDNOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add(@"""CDN.Host"" Must Be Provided");
        }
        else
        {
            string host = options.Host.Trim();
            if (!Uri.TryCreate(host, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($@"""CDN.Host"" ""{options.Host}"" Must Be A Valid Absolute HTTP or HTTPS URL");
            }
            else if (string.IsNullOrWhiteSpace(uri.Host))
            {
                failures.Add($@"""CDN.Host"" ""{options.Host}"" Must Contain A Valid Hostname");
            }
        }

        if (options.ParallelTransfers is < 1 or > 64)
        {
            failures.Add($@"""CDN.ParallelTransfers"" ({options.ParallelTransfers}) Must Be Between 1 And 64");
        }

        if (string.IsNullOrWhiteSpace(options.WindowsVariant))
        {
            failures.Add(@"""CDN.WindowsVariant"" Must Be Provided");
        }

        if (string.IsNullOrWhiteSpace(options.LinuxVariant))
        {
            failures.Add(@"""CDN.LinuxVariant"" Must Be Provided");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
