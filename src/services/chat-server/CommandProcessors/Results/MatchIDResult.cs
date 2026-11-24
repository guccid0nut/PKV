// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Results;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ID_RESULT)]
public class MatchIdResult(IDatabase distributedCacheStore) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        MatchIdResultRequestData requestData = new(buffer);

        if (requestData.Result is not ChatProtocol.MatchIDResult.MIDR_SUCCESS)
        {
            int serverId = session.ServerMetadata?.ServerID ?? 0;
            MatchStartData? data = serverId > 0
                ? await distributedCacheStore.GetMatchStartDataByMatchServerID(serverId)
                : null;

            if (data is null)
                Log.Warning(
                    @"[MatchIDResult] Match ID allocation failed before MatchStartData was stored for Server Session ""{SessionID}"" (ServerID={ServerID}, Result={Result}, RequestTime={RequestTime}ms)",
                    session.ID, serverId, requestData.Result, requestData.RequestTimeMilliseconds);

            else
                Log.Error(@"Publishing Match ID ""{MatchID}"" Has Failed In {RequestTime}ms With Result ""{Result}""",
                    data.MatchID, requestData.RequestTimeMilliseconds, requestData.Result);
        }
    }
}
