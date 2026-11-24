// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ZORGATH.WebPortal.API.Services;

public interface IAuthenticationService
{
    Task<ServiceResult<GetAuthenticationTokenDTO>> AuthenticateUserAsync(LogInUserDTO payload,
        CancellationToken cancellationToken = default);
}
