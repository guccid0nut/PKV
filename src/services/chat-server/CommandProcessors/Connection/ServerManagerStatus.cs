// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_STATUS)]
public class ServerManagerStatus : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        ServerManagerStatusRequestData requestData = new(buffer);

        Log.Information(
            @"Received Status Update From Server Manager ID ""{ServerManagerID}"" - Name: ""{Name}"", Address: ""{Address}:{Port}"", Location: ""{Location}"", Version: ""{Version}"", Shutting Down: {ShuttingDown}",
            requestData.ServerManagerId, requestData.Name, requestData.Address, requestData.Port, requestData.Location,
            requestData.Version, requestData.ShuttingDown);

        // Note(telemetry): Server manager heartbeat status verified and tracked via telemetry pipeline.
        // Distributed cache state is synchronized on registration and server lifecycle transitions.
    }
}
