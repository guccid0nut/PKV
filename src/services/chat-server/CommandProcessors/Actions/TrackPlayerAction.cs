// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Actions;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_TRACK_PLAYER_ACTION)]
public class TrackPlayerAction : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        TrackPlayerActionRequestData requestData = new(buffer);

        Log.Information("Tracking player action {Action} for Session {SessionId} (Account: {AccountId})",
            requestData.Action, session.ID, session.Account?.ID);

        switch (requestData.Action)
        {
            case ChatProtocol.ActionCampaign.AC_DAILY_LOGINS:
                Log.Information("Recorded daily login telemetry event for Session {SessionId} (Account: {AccountId})",
                    session.ID, session.Account?.ID);
                break;
            case ChatProtocol.ActionCampaign.AC_CLICKED_HON_STORE:
                Log.Debug("Recorded store click telemetry action {Action} for Session {SessionId} (Account: {AccountId})",
                    requestData.Action, session.ID, session.Account?.ID);
                break;
            default:
                Log.Warning(@"Unhandled User Action: ""{Action}"" (Session: {SessionId}, Account: {AccountId})",
                    requestData.Action, session.ID, session.Account?.ID);
                break;
        }
    }
}
