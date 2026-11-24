// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ILearnatoriumService.cs
// System: ZORGATH Web Portal API - Learnatorium Playback & Guide Service Interface
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Learnatorium;

namespace ZORGATH.WebPortal.API.Services.Learnatorium;

public interface ILearnatoriumService
{
    /// <summary>
    ///     Generates or retrieves full 2D minimap coordinate playback data for a match.
    /// </summary>
    Task<LearnatoriumReplayPlaybackDto?> GetReplayPlaybackAsync(int matchId, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves dynamic skill progression and winning item builds for a hero.
    /// </summary>
    Task<LearnatoriumHeroGuideDto?> GetHeroGuideAsync(string heroIdentifier, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves chronological combat events and highlights for a match.
    /// </summary>
    Task<IReadOnlyList<PlaybackCombatHighlightDto>> GetCombatLogAsync(int matchId, CancellationToken ct = default);
}
