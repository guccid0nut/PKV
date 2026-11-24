// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

public class GroupCreateRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string ClientVersion { get; } = buffer.ReadString();

    public ChatProtocol.TMMType GroupType { get; } = (ChatProtocol.TMMType)buffer.ReadInt8();

    public ChatProtocol.TMMGameType GameType { get; } = (ChatProtocol.TMMGameType)buffer.ReadInt8();

    public string MapName { get; } = buffer.ReadString();

    public string[] GameModes { get; } = buffer.ReadString().Split('|', StringSplitOptions.RemoveEmptyEntries);

    public string[] GameRegions { get; } = buffer.ReadString().Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Ranked { get; } = buffer.ReadBool();

    /// <summary>
    ///     0: Skill Disparity Will Be Higher But The Matchmaking Queue Time Will Be Shorter
    ///     <br />
    ///     1: Skill Disparity Will Be Lower But The Matchmaking Queue Time Will Be Longer
    /// </summary>
    public byte MatchFidelity { get; } = buffer.ReadInt8();

    /// <summary>
    ///     1: Easy, 2: Medium, 3: Hard
    /// </summary>
    /// <remarks>
    ///     Only Used For Bot Matches, But Sent With Every Request To Create A Group
    /// </remarks>
    public byte BotDifficulty { get; } = buffer.ReadInt8();

    /// <remarks>
    ///     Only Used For Bot Matches, But Sent With Every Request To Create A Group
    /// </remarks>
    public bool RandomizeBots { get; } = buffer.ReadBool();

    public MatchmakingGroupInformation ToGroupInformation()
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = ClientVersion,
            GroupType = GroupType,
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
