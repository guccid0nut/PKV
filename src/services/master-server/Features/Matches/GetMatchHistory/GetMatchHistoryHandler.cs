// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain.Identifiers;

namespace KONGOR.MasterServer.Features.Matches.GetMatchHistory;

public interface IGetMatchHistoryHandler
{
    Task<GetMatchHistoryResponse> HandleAsync(GetMatchHistoryQuery query, CancellationToken ct = default);
}

public sealed class GetMatchHistoryHandler(
    IMatchHistoryRepository matchHistoryRepository,
    IAccountRepository accountRepository,
    ILogger<GetMatchHistoryHandler> logger) : IGetMatchHistoryHandler
{
    public async Task<GetMatchHistoryResponse> HandleAsync(GetMatchHistoryQuery query, CancellationToken ct = default)
    {
        Account? account = null;
        if (!string.IsNullOrEmpty(query.Nickname))
        {
            account = await accountRepository.GetByNicknameAsync(query.Nickname, ct);
        }
        else if (query.AccountId.HasValue && query.AccountId.Value.Value > 0)
        {
            account = await accountRepository.GetByIdAsync(query.AccountId.Value.Value, ct);
        }

        if (account == null)
        {
            logger.LogWarning("[GetMatchHistory] Account not found for Nickname='{Nickname}', AccountId={AccountId}",
                query.Nickname ?? "<null>", query.AccountId?.Value ?? 0);
            return GetMatchHistoryResponse.NotFound("Account Not Found");
        }

        IReadOnlyList<int> lastMatchIds = await matchHistoryRepository.GetRecentMatchIdsByAccountAsync(
            account.ID, account.Name, query.Limit, ct);

        Dictionary<string, string> lastStats = [];
        foreach (int info in lastMatchIds)
        {
            lastStats[info.ToString(CultureInfo.InvariantCulture)] = info.ToString(CultureInfo.InvariantCulture);
        }

        string commaSeparated = string.Join(",", lastMatchIds);
        Dictionary<object, object> response = new()
        {
            { 0, commaSeparated },
            { "0", commaSeparated },
            { "matches", commaSeparated },
            { "last_stats", lastStats },
            { "success", "True" },
            { "hosttime", TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
        };

        return GetMatchHistoryResponse.Succeeded(response);
    }
}
