// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using MERRICK.DatabaseContext.Entities.Statistics;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST)]
public class UploadRequest(
    MerrickContext merrick,
    ILogger<UploadRequest> logger) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        UploadRequestData requestData = new(buffer);
        int matchId = requestData.MatchID;
        string targetType = requestData.TargetType;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[UploadRequest] Processing upload request from Account {AccountId} ({AccountName}) for MatchID: {MatchID}, TargetType: {TargetType}",
                session.Account.ID, session.Account.Name, matchId, targetType);

        MatchStatistics? match = await merrick.MatchStatistics
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchId);

        byte updateType = (byte)ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED;
        if (match is null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[UploadRequest] Match ID {MatchID} not found in database.", matchId);

            updateType = (byte)ChatProtocol.UploadUpdateType.EUUT_FILE_DOES_NOT_EXIST;
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "[UploadRequest] Match ID {MatchID} found in database. Sending status ALREADY_UPLOADED.", matchId);
        }

        // CHAT_CMD_UPLOAD_STATUS (0x00BF) packet layout expected by K2 engine:
        // ushort Command (0x00BF) - 2 bytes
        // int MatchID - 4 bytes
        // byte UpdateType - 1 byte
        ChatBuffer response = new();
        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_STATUS);
        response.WriteInt32(matchId);
        response.WriteInt8(updateType);

        session.Send(response);
    }
}
