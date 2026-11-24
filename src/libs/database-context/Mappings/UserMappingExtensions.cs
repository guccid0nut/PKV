// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;
using MERRICK.DatabaseContext.Extensions;

namespace MERRICK.DatabaseContext.Mappings;

public static class UserMappingExtensions
{
    public static GetBasicAccountDTO ToBasicDTO(this Account account, bool includeClanTag = true)
    {
        ArgumentNullException.ThrowIfNull(account);
        string name = includeClanTag ? account.GetNameWithClanTag() : account.Name;
        return new GetBasicAccountDTO(account.ID, name);
    }

    public static GetBasicUserDTO ToBasicDTO(this User user, bool includeClanTag = true)
    {
        ArgumentNullException.ThrowIfNull(user);
        List<GetBasicAccountDTO> accounts = user.Accounts.Count > 0
            ? [.. user.Accounts.Select(a => a.ToBasicDTO(includeClanTag))]
            : [];

        return new GetBasicUserDTO(user.ID, user.EmailAddress, accounts);
    }
}
