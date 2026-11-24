// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

public abstract class ServerForResponse(string id, string ip, string port, string location, string name)
{
    [PHPProperty("server_id")] public string ID { get; set; } = id;

    [PHPProperty("ip")] public string IPAddress { get; set; } = ip;

    [PHPProperty("port")] public string Port { get; set; } = port;

    [PHPProperty("location")] public string Location { get; set; } = location;

    [PHPProperty("name")] public string Name { get; set; } = name;
}
