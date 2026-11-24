// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

public partial class ServerForCreateListResponse(List<MatchServer> servers, string cookie, string? requestorIP)
    : ServerListResponse(cookie)

{

    [PHPProperty("server_list")]
    public List<ServerForCreate> Servers { get; set; } =
    [
        .. servers
            .DistinctBy(server => server.ID)
            .Select(server => new ServerForCreate(
                server.ID.ToString(),
                ResolveIP(server, requestorIP) ?? server.IPAddress,
                ResolvePort(server, requestorIP),
                !string.IsNullOrWhiteSpace(server.Location) ? server.Location : "NEWERTH",
                server.Name))
    ];
}
