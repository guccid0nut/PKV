// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using MERRICK.DatabaseContext.Extensions;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles Monitor Game requests (Command 0x00B5 - CHAT_CMD_REQUEST_GAME_INFO).
///     Wire format verified against k2_x64.dll (0x18070ea40, 0x18069de90) and watch_user_v2.package.
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_GAME_INFO)]
internal sealed class RequestGameInfo(
    IChatContext chatContext,
    IDatabase? distributedCacheStore = null) : IAsynchronousCommandProcessor<ChatSession>
{
    private const string EmptyPlayerSlot = "0|||0|0|0|0";
    private const string DefaultLegionTeamInfo = "1|50|11|11|3|3|1.0|0|0|0";
    private const string DefaultHellbourneTeamInfo = "2|50|11|11|3|3|1.0|0|0|0";
    private const string LegionCrestTexture = "/ui/elements/crest_legion.tga";
    private const string HellbourneCrestTexture = "/ui/elements/crest_hellbourne.tga";

    private static readonly HashSet<string> VerifiedMapIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "caldavar",
        "caldavar_old",
        "midwars",
        "grimmscrossing",
        "riftwars",
        "soccer",
        "capturetheflag",
        "devowars",
        "prophets",
        "thegrimmhunt",
        "tutorial"
    };

    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        RequestGameInfoData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        string strippedWatchedUser = MERRICK.DatabaseContext.Extensions.AccountExtensions
            .SeparateClanTagFromAccountName(requestData.WatchedUser.Trim())
            .AccountName
            .Trim();

        if (string.IsNullOrWhiteSpace(strippedWatchedUser))
        {
            Log.Warning("[RequestGameInfo] Empty watched user requested by '{Requester}'", session.Account.Name);
            return;
        }

        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(s => (object?)s.Account is not null &&
                                 string.Equals(s.Account.Name, strippedWatchedUser, StringComparison.OrdinalIgnoreCase));

        if (targetSession is null || targetSession.ClientMetadata is null)
        {
            Log.Information(
                "[RequestGameInfo] Target '{WatchedUser}' is offline for requester '{Requester}'",
                strippedWatchedUser, session.Account.Name);
            return;
        }

        if (!session.Account.IsStaff)
        {
            if (targetSession.ClientMetadata.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE &&
                targetSession.Account.ID != session.Account.ID)
            {
                Log.Information(
                    "[RequestGameInfo] Blocked Monitor Game on invisible target '{Target}' from '{Requester}'",
                    targetSession.Account.Name, session.Account.Name);
                return;
            }

            if (targetSession.Account.HasIgnoredPeer(session.Account.ID) ||
                targetSession.Account.HasBannedPeer(session.Account.ID))
            {
                Log.Information(
                    "[RequestGameInfo] Blocked Monitor Game on '{Target}' from ignored/banned peer '{Requester}'",
                    targetSession.Account.Name, session.Account.Name);
                return;
            }
        }

        if (targetSession.ClientMetadata.LastKnownClientState is not (
            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME or
            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME))
        {
            Log.Information(
                "[RequestGameInfo] Target '{Target}' is not in a game (State={State}) for requester '{Requester}'",
                targetSession.Account.Name, targetSession.ClientMetadata.LastKnownClientState, session.Account.Name);
            return;
        }

        MatchStartData? matchData = null;
        if (distributedCacheStore is not null && targetSession.ClientMetadata.MatchID is > 0)
        {
            matchData = await distributedCacheStore.GetMatchStartData(targetSession.ClientMetadata.MatchID.Value);
        }

        MatchServerChatSessionMetadata? serverMeta = ResolveServerMetadata(
            chatContext,
            matchData?.ServerID,
            targetSession.ClientMetadata.MatchID,
            targetSession.ClientMetadata.MatchServerAddress);

        string gameName = FirstNonEmpty(
            serverMeta?.GameName,
            targetSession.ClientMetadata.MatchName,
            matchData?.MatchName,
            $"{targetSession.Account.Name}'s Match");

        string rawMapName = FirstNonEmpty(serverMeta?.MapName, matchData?.Map, "caldavar");
        string mapIconKey = VerifiedMapIcons.Contains(rawMapName) ? rawMapName.ToLowerInvariant() : string.Empty;

        bool isCasual = serverMeta?.CasualMode ?? matchData?.IsCasual ?? false;
        byte officialByte = serverMeta?.Official ?? 1;
        bool isGated = serverMeta?.Gated ?? false;

        // Pass empty string unless a verified /ui/icons/<mode>.tga asset is known; watch_user_v2.package:232 falls back to /ui/icons/invis.tga
        string gameModeIcon = string.Empty;

        string formattedGameTime = FormatElapsedTime(serverMeta?.CurrentGameTime ?? 0, matchData?.TimestampStarted);

        string legionTeamInfo = FirstNonEmpty(serverMeta?.LegionTeamInformation, DefaultLegionTeamInfo);
        string hellbourneTeamInfo = FirstNonEmpty(serverMeta?.HellbourneTeamInformation, DefaultHellbourneTeamInfo);

        string[] playerSlots = BuildPlayerSlots(chatContext, targetSession, serverMeta);

        ChatBuffer response = new();
        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_GAME_INFO);
        response.WriteString(strippedWatchedUser); // param0 (must match {user} in watch_user_box_{user})
        response.WriteString(gameName); // param13
        response.WriteString(mapIconKey); // param14 (/maps/{param14}/icon.tga or $invis)
        response.WriteInt8(isCasual ? (byte)1 : (byte)0); // param17
        response.WriteInt8(officialByte); // param18
        response.WriteInt8(isGated ? (byte)1 : (byte)0); // param19
        response.WriteString(gameModeIcon); // param15 (/ui/icons/{param15}.tga or /ui/icons/invis.tga)
        response.WriteString(formattedGameTime); // param16
        response.WriteString(legionTeamInfo); // param1
        response.WriteString(hellbourneTeamInfo); // param2

        for (int i = 0; i < 10; i++)
        {
            response.WriteString(playerSlots[i]); // param3..param12
        }

        session.Send(response);

        Log.Information(
            "[RequestGameInfo] Sent 0x00B5 game info for watched='{WatchedUser}' (Game='{GameName}', Map='{Map}', Time='{GameTime}') -> requester='{Requester}'",
            strippedWatchedUser, gameName, rawMapName, formattedGameTime, session.Account.Name);
    }

    internal static string FormatElapsedTime(int rawGameTimeMs, DateTimeOffset? timestampStarted)
    {
        int totalSeconds = 0;
        if (rawGameTimeMs > 0)
        {
            totalSeconds = rawGameTimeMs >= 1000 ? rawGameTimeMs / 1000 : rawGameTimeMs;
        }
        else if (timestampStarted.HasValue)
        {
            totalSeconds = Math.Max(0, (int)(TimeProvider.System.GetUtcNow() - timestampStarted.Value).TotalSeconds);
        }

        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1.0
            ? string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", ts.Minutes, ts.Seconds);
    }

    private static string[] BuildPlayerSlots(
        IChatContext chatContext,
        ChatSession targetSession,
        MatchServerChatSessionMetadata? serverMeta)
    {
        string[] slots = new string[10];
        for (int i = 0; i < 10; i++)
        {
            slots[i] = EmptyPlayerSlot;
        }

        if (serverMeta?.PlayerInformation is { Length: >= 10 } &&
            serverMeta.PlayerInformation.Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            bool anyOccupied = false;
            for (int i = 0; i < 10; i++)
            {
                slots[i] = NormalizePlayerSlot(serverMeta.PlayerInformation[i], i);
                if (!string.Equals(slots[i], EmptyPlayerSlot, StringComparison.Ordinal))
                {
                    anyOccupied = true;
                }
            }

            if (anyOccupied)
            {
                return slots;
            }
        }

        // Synthesize roster from active client sessions in the same match so watch_user_box always renders populated rows
        List<ChatSession> matchPeers = [targetSession];
        int? matchId = targetSession.ClientMetadata?.MatchID;
        string? serverAddr = targetSession.ClientMetadata?.MatchServerAddress;

        if (chatContext.ClientChatSessions is not null)
        {
            foreach (ChatSession candidate in chatContext.ClientChatSessions.Values)
            {
                if ((object?)candidate.Account is null ||
                    candidate.Account.ID == targetSession.Account.ID ||
                    candidate.ClientMetadata is null)
                {
                    continue;
                }

                bool sameMatch = (matchId is > 0 && candidate.ClientMetadata.MatchID == matchId) ||
                                 (!string.IsNullOrWhiteSpace(serverAddr) &&
                                  serverAddr != "0.0.0.0:0" &&
                                  string.Equals(candidate.ClientMetadata.MatchServerAddress, serverAddr, StringComparison.OrdinalIgnoreCase));

                if (sameMatch)
                {
                    matchPeers.Add(candidate);
                    if (matchPeers.Count >= 10) break;
                }
            }
        }

        for (int i = 0; i < matchPeers.Count && i < 10; i++)
        {
            Account acc = matchPeers[i].Account;
            string crest = i < 5 ? LegionCrestTexture : HellbourneCrestTexture;
            slots[i] = string.Create(
                CultureInfo.InvariantCulture,
                $"{acc.ID}|{acc.GetNameWithClanTag()}|{crest}|1|0|0|0");
        }

        return slots;
    }

    private static string NormalizePlayerSlot(string? rawSlot, int slotIndex)
    {
        if (string.IsNullOrWhiteSpace(rawSlot))
        {
            return EmptyPlayerSlot;
        }

        string[] parts = rawSlot.Split('|');
        if (parts.Length < 7 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return EmptyPlayerSlot;
        }

        string textureToken = parts[2].Trim();
        bool isVfsTexturePath = (textureToken.StartsWith('/') || textureToken.StartsWith('$')) &&
                                (textureToken.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ||
                                 textureToken.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                                 textureToken.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 textureToken.StartsWith('$'));

        if (!isVfsTexturePath)
        {
            parts[2] = slotIndex < 5 ? LegionCrestTexture : HellbourneCrestTexture;
        }

        return string.Join('|', parts[..7]);
    }

    private static MatchServerChatSessionMetadata? ResolveServerMetadata(
        IChatContext chatContext,
        int? serverId,
        int? matchId,
        string? serverAddress)
    {
        if (chatContext.MatchServerChatSessions is null)
        {
            return null;
        }

        if (serverId is > 0 &&
            chatContext.MatchServerChatSessions.TryGetValue(serverId.Value, out ChatSession? byIdSession) &&
            byIdSession.ServerMetadata is not null)
        {
            return byIdSession.ServerMetadata;
        }

        foreach (ChatSession serverSession in chatContext.MatchServerChatSessions.Values)
        {
            MatchServerChatSessionMetadata? meta = serverSession.ServerMetadata;
            if (meta is null) continue;

            if (matchId is > 0 && meta.MatchID == matchId.Value)
            {
                return meta;
            }

            if (!string.IsNullOrWhiteSpace(serverAddress) &&
                string.Equals($"{meta.Address}:{meta.Port}", serverAddress, StringComparison.OrdinalIgnoreCase))
            {
                return meta;
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}

file class RequestGameInfoData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string WatchedUser { get; } = buffer.ReadString();
}
