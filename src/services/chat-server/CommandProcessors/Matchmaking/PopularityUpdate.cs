// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_POPULARITY_UPDATE)]
public class PopularityUpdate : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        PopularityUpdateRequestData requestData = new(buffer);

        SendMatchmakingPopularity(session);
    }

    public static void SendMatchmakingPopularity(ChatSession session)
    {
        // Legacy Logic Hardcoded for Stability
        // Dynamic Logic via Configuration
        MatchmakingConfiguration config = SharedConfiguration.MatchmakingConfiguration;

        List<string> maps = [];
        List<string> types = ["1", "5", "6"]; // Default: Normal(1), Custom(5), Campaign(6)
        List<string> modes = [];
        List<string> regions = [];

        // Maps order MUST be caldavar (1), midwars (2), riftwars (3) for client UI card binding
        maps.Add(config.Ranked.Map); // caldavar
        if (!maps.Contains(config.Unranked.Map)) maps.Add(config.Unranked.Map);

        if (!maps.Contains(config.MidWars.Map)) maps.Add(config.MidWars.Map);

        if (!maps.Contains(config.RiftWars.Map)) maps.Add(config.RiftWars.Map);

        if (!types.Contains("3")) types.Add("3");

        if (!types.Contains("4")) types.Add("4");

        // Modes order starting with MidWars modes (ap, sd, ar) so All Pick is FIRST
        foreach (string m in config.MidWars.Modes)
            if (!modes.Contains(m))
                modes.Add(m);

        foreach (string m in config.Ranked.Modes)
            if (!modes.Contains(m))
                modes.Add(m);

        foreach (string m in config.Unranked.Modes)
            if (!modes.Contains(m))
                modes.Add(m);

        foreach (string m in config.RiftWars.Modes)
            if (!modes.Contains(m))
                modes.Add(m);

        // Regions
        foreach (string r in config.Ranked.Regions)
            if (!regions.Contains(r))
                regions.Add(r);

        foreach (string r in config.MidWars.Regions)
            if (!regions.Contains(r))
                regions.Add(r);

        foreach (string r in config.RiftWars.Regions)
            if (!regions.Contains(r))
                regions.Add(r);

        List<string> enabledGameTypes = [.. types.OrderBy(t => t)];

        // Standard known game modes to evaluate disabled combinations per map
        List<string> allKnownModes =
            ["ap", "sd", "ar", "sp", "hb", "bd", "bp", "br", "rp", "rd", "cd", "cm", "lp", "bb"];
        List<MatchmakingMapConfiguration> mapConfigs =
        [
            config.Ranked,
            config.Unranked,
            config.MidWars,
            config.RiftWars
        ];

        List<string> disabledGameModesByMapList =
        [
            .. from mc in mapConfigs
            let allowedModesForMap = new HashSet<string>(mc.Modes, StringComparer.OrdinalIgnoreCase)
            let pool = allKnownModes.Concat(modes).Distinct(StringComparer.OrdinalIgnoreCase)
            from m in pool
            where !allowedModesForMap.Contains(m)
            select $"{mc.Map}->{m}"
            // Construct Pipe Strings
        ];

        // Construct Pipe Strings
        string availableMaps = string.Join("|", maps);
        string gameTypes = string.Join("|", enabledGameTypes);
        string gameModes = string.Join("|", modes);
        string availableRegions = string.Join("|", regions);

        const string disabledGameModesByGameType = "";
        const string disabledGameModesByRankType = "";
        string disabledGameModesByMap = string.Join("|", disabledGameModesByMapList);
        const string restrictedRegions = "";
        const string clientCountryCode = "";

        // Legacy Legend String
        const string legend = "maps:modes:|regions:";

        ChatBuffer response = new();

        response.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_POPULARITY_UPDATE);

        response.WriteInt8(1); // TMM Availability
        response.WriteString(availableMaps); // Maps
        response.WriteString(gameTypes); // Types
        response.WriteString(gameModes); // Modes
        response.WriteString(availableRegions); // Regions
        response.WriteString(disabledGameModesByGameType);
        response.WriteString(disabledGameModesByRankType);
        response.WriteString(disabledGameModesByMap);
        response.WriteString(restrictedRegions);
        response.WriteString(clientCountryCode);
        response.WriteString(legend);

        // Popularity By Game Map (Count = Maps)
        foreach (string _ in maps) response.WriteInt8(10);

        // Popularity By Game Type (Count = Types * Maps) - CRITICAL: Legacy logic creates array of size Types * Maps
        for (int i = 0; i < enabledGameTypes.Count * maps.Count; i++) response.WriteInt8(10);

        // Popularity By Game Mode (Count = Modes)
        foreach (string _ in modes) response.WriteInt8(10);

        // Popularity By Region (Count = Regions)
        foreach (string _ in regions) response.WriteInt8(10);

        // Custom Map Rotation Time
        response.WriteInt32(0);

        session.Send(response);
    }
}
