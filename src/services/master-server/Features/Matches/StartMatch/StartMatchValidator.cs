// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Features.Matches.StartMatch;

public sealed class StartMatchValidator
{
    public (bool IsValid, string? ErrorMessage) Validate(StartMatchCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Session))
        {
            return (false, @"Missing Value For Form Parameter ""session""");
        }

        if (string.IsNullOrWhiteSpace(command.Map))
        {
            return (false, @"Missing Value For Form Parameter ""map""");
        }

        if (string.IsNullOrWhiteSpace(command.Version))
        {
            return (false, @"Missing Value For Form Parameter ""version""");
        }

        if (!string.IsNullOrWhiteSpace(command.Version) &&
            command.Version != "4.10.1.0" && command.Version != "5.0.0.0" &&
            !command.Version.StartsWith("4.") && !command.Version.StartsWith("5."))
        {
            return (false, $@"Unsupported Match Server Version ""{command.Version}""");
        }

        if (string.IsNullOrWhiteSpace(command.MatchName))
        {
            return (false, "Match Name Is NULL");
        }

        if (string.IsNullOrWhiteSpace(command.HostAccountName))
        {
            return (false, "Host Account Name Is NULL");
        }

        if (string.IsNullOrWhiteSpace(command.MatchMode))
        {
            return (false, @"Value Of Form Parameter ""match_mode"" Is NULL");
        }

        return (true, null);
    }
}
