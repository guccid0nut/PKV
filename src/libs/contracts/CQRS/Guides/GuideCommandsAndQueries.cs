// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Guides;

#region Request DTOs

/// <summary>
/// Request DTO for saving a hero guide.
/// </summary>
/// <param name="Request">Detailed guide parameters and item/ability lists.</param>
/// <param name="AuthorAccountName">Account nickname of the author.</param>
/// <param name="IsStaff">Staff privilege flag.</param>
public record SaveGuideApiRequestDto(SaveHeroGuideRequestDto Request, string AuthorAccountName, bool IsStaff);

/// <summary>
/// Request DTO for deleting a hero guide.
/// </summary>
/// <param name="AccountName">Account name requesting the deletion.</param>
public record DeleteGuideApiRequestDto(string AccountName);

/// <summary>
/// Request DTO for toggling public/private visibility of a guide.
/// </summary>
/// <param name="AccountName">Account name requesting the change.</param>
/// <param name="IsStaff">Staff privilege flag.</param>
public record TogglePrivacyApiRequestDto(string AccountName, bool IsStaff);

/// <summary>
/// Request DTO for setting a guide as default for an account.
/// </summary>
/// <param name="AccountName">Account name.</param>
public record ToggleDefaultApiRequestDto(string AccountName);

/// <summary>
/// Request DTO for toggling a guide in an account's favorites.
/// </summary>
/// <param name="AccountName">Account name.</param>
public record ToggleFavoriteApiRequestDto(string AccountName);

/// <summary>
/// Request DTO for voting on a hero's difficulty rating.
/// </summary>
/// <param name="Ign">Player in-game nickname.</param>
/// <param name="Difficulty">Difficulty score (1 to 5).</param>
public record VoteDifficultyApiRequestDto(string Ign, int Difficulty);

#endregion

#region Operation Statuses & Responses

public enum GuideSaveStatus
{
    Success,
    Unauthorized,
    Forbidden
}

public record SaveGuideCommandResponse(GuideSaveStatus Status, SaveGuideResultDto Dto);

public enum GuideDeleteStatus
{
    Success,
    NotFound,
    BadRequest,
    Forbidden
}

public record DeleteGuideCommandResponse(GuideDeleteStatus Status, string? ErrorMessage = null);

public enum GuideToggleStatus
{
    Success,
    NotFound,
    Forbidden
}

public record TogglePrivacyCommandResponse(GuideToggleStatus Status, TogglePrivacyResultDto? Dto = null, string? ErrorMessage = null);

public enum GuidePreferenceStatus
{
    Success,
    AccountNotFound,
    GuideNotFound
}

public record ToggleDefaultGuideCommandResponse(GuidePreferenceStatus Status, ToggleDefaultGuideResultDto? Dto = null, string? ErrorMessage = null);

public record ToggleFavoriteGuideCommandResponse(GuidePreferenceStatus Status, ToggleFavoriteGuideResultDto? Dto = null, string? ErrorMessage = null);

#endregion

#region Queries & Commands

/// <summary>
/// Query to search and retrieve paginated hero guides with optional filters.
/// </summary>
public sealed record SearchGuidesQuery(
    string? Hero = null,
    string? Map = null,
    string? Q = null,
    string? Sort = "featured",
    int Page = 1,
    int PageSize = 18,
    string? CurrentAccountName = null,
    bool IsStaff = false) : IRequest<GuideSearchResultDto>;

/// <summary>
/// Query to retrieve a hero guide by its unique integer identifier.
/// </summary>
public sealed record GetGuideByIdQuery(int Id) : IRequest<HeroGuideDto?>;

/// <summary>
/// Query to retrieve official and featured guides for a hero identifier.
/// </summary>
public sealed record GetGuidesForHeroQuery(string HeroIdentifier) : IRequest<List<HeroGuideDto>>;

/// <summary>
/// Command to create or update a hero guide.
/// </summary>
public sealed record SaveGuideCommand(SaveGuideApiRequestDto Request) : IRequest<SaveGuideCommandResponse>;

/// <summary>
/// Command to delete a hero guide if owned by the requesting player.
/// </summary>
public sealed record DeleteGuideCommand(int Id, string AccountName) : IRequest<DeleteGuideCommandResponse>;

/// <summary>
/// Command to record an upvote or downvote for a hero guide.
/// </summary>
public sealed record VoteGuideCommand(int Id, bool IsUpvote) : IRequest<VoteGuideResultDto?>;

/// <summary>
/// Command to toggle the public/private visibility status of a hero guide.
/// </summary>
public sealed record ToggleGuidePrivacyCommand(int Id, string AccountName, bool IsStaff) : IRequest<TogglePrivacyCommandResponse>;

/// <summary>
/// Command to toggle official/featured badge on a hero guide.
/// </summary>
public sealed record ToggleGuideOfficialCommand(int Id) : IRequest<ToggleOfficialResultDto?>;

/// <summary>
/// Command to toggle a hero guide as default for an account.
/// </summary>
public sealed record ToggleDefaultGuideCommand(int Id, string AccountName) : IRequest<ToggleDefaultGuideCommandResponse>;

/// <summary>
/// Command to toggle a hero guide as a favorite for an account.
/// </summary>
public sealed record ToggleFavoriteGuideCommand(int Id, string AccountName) : IRequest<ToggleFavoriteGuideCommandResponse>;

/// <summary>
/// Query to retrieve difficulty rating votes for a hero.
/// </summary>
public sealed record GetHeroDifficultyVotesQuery(string HeroIdentifier) : IRequest<List<HeroDifficultyVoteDto>>;

/// <summary>
/// Command to submit a hero difficulty rating vote.
/// </summary>
public sealed record VoteHeroDifficultyCommand(string HeroIdentifier, string Ign, int Difficulty) : IRequest<HeroDifficultyVoteResultDto>;

#endregion
