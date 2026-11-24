// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

public partial class ServerForJoin(string id, string ip, string port, string location, string name)
    : ServerForResponse(id, ip, port, location, name)
{
    [PHPProperty("class")] public string Category { get; set; } = "1";
}
