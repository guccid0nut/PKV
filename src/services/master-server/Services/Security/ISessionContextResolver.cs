// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Security;

public interface ISessionContextResolver
{
    Task<Account?> ResolveAccountAsync(HttpContext context, CancellationToken cancellationToken = default);
}
