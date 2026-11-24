// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Controllers.MessageController;

[ApiController]
[Route("message")]
[Consumes("application/x-www-form-urlencoded")]
public class MessageController : ControllerBase
{
    [HttpPost("list/{id:int}")]
    public IActionResult List(int id)
    {
        // Stub implementation to prevent 404 errors.
        // Likely used for MOTD or system messages.

        Dictionary<string, object> response = [];

        return Ok(PhpSerialization.Serialize(response));
    }
}
