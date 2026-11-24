// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ILearnatoriumApiClient.cs
// System: DAWNBRINGER Web Portal UI - Learnatorium API Client Interface
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Learnatorium;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public interface ILearnatoriumApiClient
{
    Task<LearnatoriumReplayPlaybackDto?> GetReplayPlaybackAsync(int matchId, CancellationToken ct = default);
    Task<LearnatoriumHeroGuideDto?> GetHeroGuideAsync(string heroIdentifier, CancellationToken ct = default);
    Task<IReadOnlyList<PlaybackCombatHighlightDto>> GetCombatLogAsync(int matchId, CancellationToken ct = default);
}
