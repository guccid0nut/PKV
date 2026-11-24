// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

public class GroupGameOptionUpdateRequestData
{
    public GroupGameOptionUpdateRequestData(ChatBuffer buffer)
    {
        // Legacy Protocol Quirk: Some clients verify/repeat the command ID (0x0D08) at the start of the payload.
        // Others (or modern tests) might not. We consume any leading instances matching the command ID.
        while (buffer.HasRemainingData(2) &&
               BitConverter.ToUInt16(buffer.Peek(2), 0) == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GAME_OPTION_UPDATE)
        {
            buffer.ReadInt16();
        }

        GameType = (ChatProtocol.TMMGameType)buffer.ReadInt8();

        MapName = buffer.ReadString();
        GameModes = buffer.ReadString().Split('|');
        GameRegions = buffer.ReadString().Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Ranked = buffer.ReadBool();
        MatchFidelity = buffer.ReadInt8();
        BotDifficulty = buffer.ReadInt8();
        RandomizeBots = buffer.ReadBool();
    }

    public ChatProtocol.TMMGameType GameType { get; set; }
    public string MapName { get; set; }
    public string[] GameModes { get; set; }
    public string[] GameRegions { get; set; }
    public bool Ranked { get; set; }
    public byte MatchFidelity { get; set; }
    public byte BotDifficulty { get; set; }
    public bool RandomizeBots { get; set; }

    public MatchmakingGroupInformation ToGroupInformation()
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = string.Empty, // Not Sent In Update
            GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP, // Placeholder, will be preserved by UpdateInformation
            GameType = GameType,
            MapName = MapName,
            GameModes = GameModes,
            GameRegions = GameRegions,
            Ranked = Ranked,
            MatchFidelity = MatchFidelity,
            BotDifficulty = BotDifficulty,
            RandomizeBots = RandomizeBots
        };
    }
}
