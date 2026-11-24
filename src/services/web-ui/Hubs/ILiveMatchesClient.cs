// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public interface ILiveMatchesClient
{
    // Match Specific Events
    Task OnMatchSnapshot(LiveMatchData matchSnapshot);
    Task OnMatchTick(LiveMatchDeltaDto delta);
    Task OnCombatEvent(LiveCombatEvent combatEvent);
    Task OnGoldGraphUpdate(LiveGoldTimelinePoint goldPoint);
    Task OnWardPlaced(LiveWardState ward);
    Task OnWardExpired(string wardId);

    // Overview Page Events
    Task OnOverviewUpdated(List<LiveMatchData> activeMatches);
    Task OnMatchStarted(LiveMatchData match);
    Task OnMatchConcluded(int matchId, int winningTeam);
}
