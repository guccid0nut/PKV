// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using NEXUS.Contracts.Domain;

namespace NEXUS.Contracts.Data;

public record ClanRankUpdateResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; } = 200;
    public int TargetAccountId { get; init; }
    public string? TargetAccountName { get; init; }
    public ClanTier NewTargetTier { get; init; }
    public ClanTier PreviousTargetTier { get; init; }
    public int RequesterAccountId { get; init; }
    public string? RequesterAccountName { get; init; }
    public ClanTier NewRequesterTier { get; init; }
    public int ClanId { get; init; }
    public string? ClanName { get; init; }
    public string? ClanTag { get; init; }
    public bool IsKick { get; init; }

    public string RedisPublishPayload => $"{TargetAccountId}:{NewTargetTier}:{RequesterAccountId}:{ClanId}:{ClanName}";

    public static ClanRankUpdateResult Fail(string errorMessage, int statusCode = 400) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        StatusCode = statusCode
    };
}

public interface IClanRepository
{
    Task<Clan?> GetByIdAsync(int clanId, CancellationToken ct = default);
    Task<Clan?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Clan?> GetByTagAsync(string tag, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetClanMembersAsync(int clanId, CancellationToken ct = default);
    Task<Account?> GetAccountWithClanAsync(int accountId, CancellationToken ct = default);
    Task<Account?> GetAccountWithClanByNameAsync(string accountName, CancellationToken ct = default);
    Task<ClanRankUpdateResult> SetMemberRankAsync(int requesterAccountId, int targetAccountId, int clanId, string rankAction, CancellationToken ct = default);
    Task<Clan> CreateClanAsync(string name, string tag, int creatorAccountId, IEnumerable<int>? foundingMemberAccountIds = null, CancellationToken ct = default);
    Task<bool> AddMemberAsync(int accountId, int clanId, ClanTier tier = ClanTier.Member, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(int accountId, int clanId, CancellationToken ct = default);
    Task<bool> DisbandClanAsync(int clanId, CancellationToken ct = default);
    Task<bool> ChangeClanTagAsync(int clanId, string newTag, CancellationToken ct = default);
}
