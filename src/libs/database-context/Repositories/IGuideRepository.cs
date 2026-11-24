// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Game;

namespace NEXUS.Contracts.Data;

/// <summary>
/// Encapsulates data access for hero build guides, active guide resolution,
/// voting, default and favorite preferences, and guide persistence.
/// </summary>
public interface IGuideRepository
{
    /// <summary>
    /// Retrieves a guide by its primary key ID including author and clan metadata.
    /// </summary>
    Task<HeroGuide?> GetByIdAsync(int guideId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all accessible guides for a specific hero identifier (e.g. "Hero_Valkyrie").
    /// Includes featured guides, public guides, and private guides authored by the requesting account.
    /// Ordered by Featured descending, Rating descending, and UpVotes descending.
    /// </summary>
    Task<IReadOnlyList<HeroGuide>> GetGuidesForHeroAsync(string heroIdentifier, int? accountId = null, string? accountName = null, CancellationToken ct = default);

    /// <summary>
    /// Resolves the single active guide to show in-game for a hero, prioritizing:
    /// 1. User's designated default guide for this hero (matching defaultGuideIds).
    /// 2. User's own authored guide for this hero (newest first).
    /// 3. Official featured / highest rated public guide.
    /// </summary>
    Task<HeroGuide?> GetActiveGuideForHeroAsync(string heroIdentifier, int? accountId, string? accountName, IEnumerable<int>? defaultGuideIds, CancellationToken ct = default);

    /// <summary>
    /// Sets or removes a guide as the default guide for its hero on the account.
    /// Replaces any existing default guide for the same hero.
    /// Returns the active default guide ID (or -1 if removed).
    /// </summary>
    Task<int> SetDefaultGuideAsync(int accountId, int guideId, bool isSet, CancellationToken ct = default);

    /// <summary>
    /// Adds or removes a guide from the account's favorite guide IDs.
    /// Returns the guide ID if added, or -1 if removed.
    /// </summary>
    Task<int> SetFavoriteGuideAsync(int accountId, int guideId, bool isSet, CancellationToken ct = default);

    /// <summary>
    /// Submits an upvote (voteVal &gt; 0) or downvote (voteVal &lt;= 0) for a guide.
    /// Recalculates and persists the updated Rating.
    /// </summary>
    Task<(int UpVotes, int DownVotes, float Rating)> VoteGuideAsync(int guideId, int voteVal, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a hero guide in the database.
    /// </summary>
    Task<HeroGuide> SaveGuideAsync(HeroGuide guide, CancellationToken ct = default);

    /// <summary>
    /// Deletes a guide by ID if owned by the caller or authorized.
    /// </summary>
    Task<bool> DeleteGuideAsync(int guideId, CancellationToken ct = default);

    /// <summary>
    /// Toggles the Featured status of a guide (official status).
    /// </summary>
    Task<bool> ToggleFeaturedAsync(int guideId, CancellationToken ct = default);

    /// <summary>
    /// Toggles the Public status of a guide.
    /// </summary>
    Task<bool> TogglePublicAsync(int guideId, CancellationToken ct = default);

    /// <summary>
    /// Recursively resolves the recipe components tree for a given item code from misc.HeroItems and misc.HeroItemComponents.
    /// </summary>
    Task<ItemRecipeNodeDTO?> ResolveRecipeTreeAsync(string itemCode, CancellationToken ct = default);
}
