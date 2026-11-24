// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ZORGATH.WebPortal.API.Services;

public interface IUserService
{
    Task<ServiceResult<User>> RegisterUserAsync(RegisterUserAndMainAccountDTO payload,
        CancellationToken cancellationToken = default);
}
