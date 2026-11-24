// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_ABANDON_MATCH)]
internal class AbandonMatch(IQueueArcadeService? queueArcadeService = null) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        // 0x0504 (NET_CHAT_GS_ABANDON_MATCH)
        // Verified in games/server/k2_x64.dll CHostServer::EndGame (RVA 0x8aa9e0 -> 0x8ab194):
        //   WriteShort(0x0504) + WriteByte((uint8)EDisconnectReason) => 3 bytes total (1 payload byte).
        try
        {
            if (!buffer.HasRemainingData(2))
            {
                Log.Warning(
                    "[AbandonMatch] Truncated 0x0504 packet from Session {SessionID}: missing 2-byte command header (Remaining={Remaining})",
                    session.ID,
                    Math.Max(0, buffer.Size - buffer.Offset));
                return;
            }

            _ = buffer.ReadCommand();
            long remaining = Math.Max(0, buffer.Size - buffer.Offset);
            int serverId = session.ServerMetadata?.ServerID ?? 0;

            if (remaining <= 0)
            {
                Log.Warning(
                    "[AbandonMatch] Truncated 0x0504 packet from ServerID={ServerID} (Session={SessionID}): empty payload",
                    serverId,
                    session.ID);
                return;
            }

            int matchId = session.ServerMetadata?.MatchID ?? 0;
            byte disconnectReason = 0;

            if (remaining == 1)
            {
                disconnectReason = buffer.ReadInt8();
            }
            else if (remaining == 4)
            {
                // Legacy 4-byte MatchID payload compatibility
                matchId = buffer.ReadInt32();
            }
            else
            {
                disconnectReason = buffer.ReadInt8();
                Log.Warning(
                    "[AbandonMatch] Unexpected 0x0504 payload length ({PayloadBytes} bytes, expected 1 byte EDisconnectReason) from ServerID={ServerID} (Session={SessionID}); processing safely",
                    remaining,
                    serverId,
                    session.ID);
            }

            Log.Information(
                "[AbandonMatch] Match Server {ServerID} Abandoning Match {MatchID} (DisconnectReason={DisconnectReason}, PayloadBytes={PayloadBytes})",
                serverId,
                matchId,
                disconnectReason,
                remaining);

            if (session.ServerMetadata?.IsQueueArcade == true)
            {
                queueArcadeService?.OnArcadeMatchEnded(session, matchId, "abandon_match");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AbandonMatch] Error parsing 0x0504 AbandonMatch packet from Session {SessionID}", session.ID);
        }
    }
}
