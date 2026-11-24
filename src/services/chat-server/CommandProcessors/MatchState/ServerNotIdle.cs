// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

/// <summary>
///     Handles K2 NET_CHAT_GS_NOT_IDLE (0x0508), emitted by CServerChatConnection::ProcessPacket when ChatServer
///     sends NET_CHAT_GS_CREATE_MATCH (0x1502) to a dedicated slave whose local status is not SERVER_STATUS_IDLE.
/// </summary>
[ChatCommand(0x0508)]
internal sealed class ServerNotIdle(
    ILogger<ServerNotIdle> logger,
    IQueueArcadeService? queueArcadeService = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        try
        {
            _ = buffer.ReadCommand();
            byte currentStatus = buffer.ReadInt8();
            int matchUpId = buffer.ReadInt32();
            int arrangedType = buffer.ReadInt32();
            int groupCount = Math.Clamp(buffer.ReadInt32(), 0, 64);

            for (int i = 0; i < groupCount; i++)
            {
                _ = buffer.ReadInt32();
            }

            int serverId = session.ServerMetadata?.ServerID ?? -1;
            bool isQueueArcade = session.ServerMetadata?.IsQueueArcade == true;

            logger.LogWarning(
                "[ServerNotIdle] Game server ServerID={ServerID} reported NET_CHAT_GS_NOT_IDLE (0x0508): CurrentStatus={Status} MatchUpID={MatchUpID} ArrangedType={ArrangedType} GroupCount={GroupCount} IsQueueArcade={IsQueueArcade}",
                serverId,
                (ChatProtocol.ServerStatus)currentStatus,
                matchUpId,
                arrangedType,
                groupCount,
                isQueueArcade);

            if (session.ServerMetadata is null)
            {
                return;
            }

            if (isQueueArcade && queueArcadeService is not null)
            {
                queueArcadeService.OnArcadeServerNotIdle(session, currentStatus, matchUpId);
                return;
            }

            if (currentStatus != (byte)ChatProtocol.ServerStatus.SERVER_STATUS_IDLE)
            {
                session.ServerMetadata.Status = (ChatProtocol.ServerStatus)currentStatus;
            }
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(
                ex,
                "[ServerNotIdle] Malformed NET_CHAT_GS_NOT_IDLE (0x0508) packet from Session {SessionID}: {Message}",
                session.ID,
                ex.Message);
        }
    }
}
