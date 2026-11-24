// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Requester;

public interface IClientRequestHandler
{
    Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default);
}
