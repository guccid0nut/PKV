// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Security;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using NEXUS.ControlPanel.GUI.Services.Models;
using ConfigurationExtensions = ASPIRE.Common.Extensions.ConfigurationExtensions;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class UserManagementService(
    ISecretsManagerService? secretsManager = null,
    HttpClient? httpClient = null,
    Action<string, string, bool>? logSink = null,
    Func<MerrickContext>? dbContextFactory = null,
    TimeProvider? timeProvider = null) : IUserManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(3));
    private readonly Action<string, string, bool>? _logSink = logSink;
    private readonly ISecretsManagerService? _secretsManager = secretsManager;
    private readonly Func<MerrickContext>? _dbContextFactory = dbContextFactory;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly object _optionsLock = new();
    private DbContextOptions<MerrickContext>? _cachedOptions;
    private Npgsql.NpgsqlDataSource? _cachedDataSource;
    private string? _cachedConnectionString;

    public async Task<UserSummaryStats> GetSummaryStatsAsync(CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log("[DIRECT_DB] Querying user summary stats from Supabase PostgreSQL...");
                    int totalUsers = await db.Users
                        .CountAsync(u => !u.EmailAddress.StartsWith("host_"), cancellationToken);
                    int totalAccounts = await db.Accounts
                        .CountAsync(cancellationToken);
                    int totalStaff = await db.Users
                        .CountAsync(
                            u => !u.EmailAddress.StartsWith("host_") &&
                                 (u.Role.Name == UserRoles.Administrator ||
                                  u.Accounts.Any(a =>
                                      a.Type == AccountType.Staff ||
                                      a.Type == AccountType.MatchModerator ||
                                      a.Type == AccountType.GameMaster)),
                            cancellationToken);
                    int totalHosts = await db.Accounts
                        .CountAsync(a => a.Type == AccountType.ServerHost, cancellationToken);

                    Log($"[DIRECT_DB] Summary stats: {totalUsers} users, {totalAccounts} accounts, {totalStaff} staff, {totalHosts} hosts.");
                    return new UserSummaryStats
                    {
                        TotalUsers = totalUsers,
                        TotalAccounts = totalAccounts,
                        TotalStaff = totalStaff,
                        TotalHosts = totalHosts,
                        IsDatabaseConnected = true,
                        StatusMessage = "Connected to Supabase PostgreSQL (Direct DB Mode)"
                    };
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] Supabase query failed ({ex.Message}). Falling back to WebPortal API...", true);
                }
            }
        }

        return await GetSummaryStatsViaApiAsync(cancellationToken);
    }

    public async Task<List<UserDetailDto>> SearchUsersAsync(string query, string roleFilter,
        CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Searching users (query='{query}', roleFilter='{roleFilter}')...");

                    IQueryable<User> queryable = db.Users
                        .Include(u => u.Role)
                        .Include(u => u.Accounts)
                            .ThenInclude(a => a.SelectedStoreItems)
                        .Where(u => !u.EmailAddress.StartsWith("host_"))
                        .AsNoTracking();

                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        string trimmed = query.Trim();
                        queryable = queryable.Where(u =>
                            u.EmailAddress.Contains(trimmed) ||
                            (u.DiscordUsername != null && u.DiscordUsername.Contains(trimmed)) ||
                            (u.DiscordId != null && u.DiscordId.Contains(trimmed)) ||
                            u.Accounts.Any(a => a.Name.Contains(trimmed)));
                    }

                    if (!string.IsNullOrWhiteSpace(roleFilter))
                    {
                        string normalizedFilter = roleFilter.ToUpperInvariant();
                        if (normalizedFilter == "STAFF")
                        {
                            queryable = queryable.Where(u =>
                                u.Role.Name == UserRoles.Administrator ||
                                u.Accounts.Any(a =>
                                    a.Type == AccountType.Staff ||
                                    a.Type == AccountType.MatchModerator ||
                                    a.Type == AccountType.GameMaster));
                        }
                        else if (normalizedFilter == "HOST")
                        {
                            queryable = queryable.Where(u =>
                                u.Accounts.Any(a => a.Type == AccountType.ServerHost ||
                                                    db.Accounts.Any(ha => ha.Type == AccountType.ServerHost && ha.OwnerAccountID == a.ID)));
                        }
                        else if (normalizedFilter == "STANDARD")
                        {
                            queryable = queryable.Where(u =>
                                u.Role.Name != UserRoles.Administrator &&
                                !u.Accounts.Any(a =>
                                    a.Type == AccountType.Staff ||
                                    a.Type == AccountType.MatchModerator ||
                                    a.Type == AccountType.GameMaster ||
                                    a.Type == AccountType.ServerHost ||
                                    db.Accounts.Any(ha => ha.Type == AccountType.ServerHost && ha.OwnerAccountID == a.ID)));
                        }
                    }

                    List<User> users = await queryable
                        .OrderBy(u => u.ID)
                        .Take(100)
                        .ToListAsync(cancellationToken);

                    List<int> userAccountIds = [.. users.SelectMany(u => u.Accounts).Select(a => a.ID).Distinct()];

                    // Optimized query: Only fetch owned hosts for the retrieved accounts using index IX_Accounts_OwnerAccountID
                    List<Account> ownedHosts = userAccountIds.Count == 0
                        ? []
                        : await db.Accounts
                            .Include(a => a.SelectedStoreItems)
                            .AsNoTracking()
                            .Where(a => a.Type == AccountType.ServerHost && a.OwnerAccountID.HasValue &&
                                        userAccountIds.Contains(a.OwnerAccountID.Value))
                            .ToListAsync(cancellationToken);

                    List<int> allAccountIds = [.. userAccountIds, .. ownedHosts.Select(h => h.ID)];
                    List<int> userIds = [.. users.Select(u => u.ID)];

                    // Optimized query: Only fetch host applications matching retrieved IDs using indexes
                    List<HostApplication> hostApps = allAccountIds.Count == 0
                        ? []
                        : await db.HostApplications
                            .Where(h => userIds.Contains(h.UserID) ||
                                        (h.HostAccountID.HasValue && allAccountIds.Contains(h.HostAccountID.Value)) ||
                                        allAccountIds.Contains(h.MainAccountID))
                            .ToListAsync(cancellationToken);

                    bool healedAny = false;
                    foreach (HostApplication h in hostApps.Where(h => h.Status == HostApplicationStatus.Approved && string.IsNullOrEmpty(h.EncryptedHostPassword)))
                    {
                        string pass = HostSecurityService.GenerateRandomHostPassword();
                        h.EncryptedHostPassword = HostSecurityService.EncryptPassword(pass);
                        healedAny = true;
                    }
                    if (healedAny)
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    List<UserDetailDto> result = MapUsersToDtos(users, ownedHosts, hostApps);
                    Log($"[DIRECT_DB] Search completed: {result.Count} users mapped.");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] SearchUsersAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await SearchUsersViaApiAsync(query, roleFilter, cancellationToken);
    }

    public async Task<UserDetailDto?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Fetching user #{userId} from Supabase...");
                    User? user = await db.Users
                        .Include(u => u.Role)
                        .Include(u => u.Accounts)
                            .ThenInclude(a => a.SelectedStoreItems)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);

                    if (user == null) return null;

                    List<int> currentAccountIds = [.. user.Accounts.Select(a => a.ID)];
                    List<Account> userOwnedHosts = currentAccountIds.Count == 0
                        ? []
                        : await db.Accounts
                            .Include(a => a.SelectedStoreItems)
                            .AsNoTracking()
                            .Where(a => a.Type == AccountType.ServerHost && a.OwnerAccountID.HasValue &&
                                        currentAccountIds.Contains(a.OwnerAccountID.Value))
                            .ToListAsync(cancellationToken);

                    List<int> allHostIds = [.. currentAccountIds, .. userOwnedHosts.Select(h => h.ID)];
                    List<HostApplication> hostApps = await db.HostApplications
                        .Where(h => h.UserID == userId ||
                                    (h.HostAccountID.HasValue && allHostIds.Contains(h.HostAccountID.Value)) ||
                                    allHostIds.Contains(h.MainAccountID))
                        .ToListAsync(cancellationToken);

                    bool healedAny = false;
                    foreach (HostApplication h in hostApps.Where(h => h.Status == HostApplicationStatus.Approved && string.IsNullOrEmpty(h.EncryptedHostPassword)))
                    {
                        string pass = HostSecurityService.GenerateRandomHostPassword();
                        h.EncryptedHostPassword = HostSecurityService.EncryptPassword(pass);
                        healedAny = true;
                    }
                    if (healedAny)
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    List<Account> allAccounts = [.. user.Accounts.Concat(userOwnedHosts).DistinctBy(a => a.ID)];
                    return MapSingleUserToDto(user, allAccounts, hostApps);
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] GetUserByIdAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await GetUserByIdViaApiAsync(userId, cancellationToken);
    }

    public async Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Setting user #{userId} role to '{roleName}'...");
                    User? user = await db.Users.Include(u => u.Role)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);
                    if (user == null) return false;

                    Role? targetRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);
                    if (targetRole == null)
                    {
                        targetRole = new Role { Name = roleName };
                        await db.Roles.AddAsync(targetRole, cancellationToken);
                    }

                    user.Role = targetRole;
                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] User #{userId} role successfully changed to '{roleName}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] SetUserRoleAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await SetUserRoleViaApiAsync(userId, roleName, cancellationToken);
    }

    public async Task<bool> SetAccountRoleAsync(int accountId, AccountType type,
        CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Setting account #{accountId} type to '{type}'...");
                    Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, cancellationToken);
                    if (account == null) return false;

                    account.Type = type;
                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] Account #{accountId} type successfully updated to '{type}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] SetAccountRoleAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await SetAccountRoleViaApiAsync(accountId, type, cancellationToken);
    }

    public async Task<string?> ResetHostPasswordAsync(int accountId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Resetting host password for account #{accountId}...");
                    Account? account = await db.Accounts.Include(a => a.User)
                        .FirstOrDefaultAsync(a => a.ID == accountId, cancellationToken);
                    if (account == null) return null;

                    string rawPassword = HostSecurityService.GenerateRandomHostPassword(16);
                    string salt = HostSecurityService.GenerateSrpSalt();
                    string srpHash = HostSecurityService.ComputeSrpHash(rawPassword, salt, passwordIsHashed: false);

                    account.User.SRPPasswordSalt = salt;
                    account.User.SRPPasswordHash = srpHash;

                    HostApplication? hostApp = await db.HostApplications.FirstOrDefaultAsync(
                        h => h.HostAccountID == accountId || h.MainAccountID == accountId, cancellationToken);
                    if (hostApp != null)
                    {
                        hostApp.EncryptedHostPassword = HostSecurityService.EncryptPassword(rawPassword);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] Host password reset successfully for account #{accountId}.");
                    return rawPassword;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] ResetHostPasswordAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await ResetHostPasswordViaApiAsync(accountId, cancellationToken);
    }

    public async Task<string?> ResetHostAuthTokenAsync(int accountId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Resetting auth token for account #{accountId}...");
                    Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, cancellationToken);
                    if (account == null) return null;

                    string rawToken = "tok_" + Guid.NewGuid().ToString("N")[..20];
                    HostApplication? hostApp = await db.HostApplications.FirstOrDefaultAsync(
                        h => h.HostAccountID == accountId || h.MainAccountID == accountId, cancellationToken);
                    if (hostApp != null)
                    {
                        hostApp.EncryptedAuthToken = HostSecurityService.EncryptPassword(rawToken);
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    Log($"[DIRECT_DB] Auth token reset successfully for account #{accountId}.");
                    return rawToken;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] ResetHostAuthTokenAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await ResetHostAuthTokenViaApiAsync(accountId, cancellationToken);
    }

    public async Task<bool> UpdateUserEconomyAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Updating economy for user #{userId} (Gold: {goldCoins}, Silver: {silverCoins}, Tickets: {plinkoTickets})...");
                    User? user = await db.Users.Include(u => u.Accounts)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);
                    if (user == null) return false;

                    int goldDelta = goldCoins - user.GoldCoins;
                    int silverDelta = silverCoins - user.SilverCoins;
                    int plinkoDelta = plinkoTickets - user.PlinkoTickets;

                    user.GoldCoins = Math.Max(0, goldCoins);
                    user.SilverCoins = Math.Max(0, silverCoins);
                    user.PlinkoTickets = Math.Max(0, plinkoTickets);

                    Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                    db.EconomyTransactions.Add(new EconomyTransaction
                    {
                        UserID = user.ID,
                        AccountID = mainAccount?.ID,
                        GoldCoinsDelta = goldDelta,
                        SilverCoinsDelta = silverDelta,
                        PlinkoTicketsDelta = plinkoDelta,
                        GoldCoinsBalanceAfter = user.GoldCoins,
                        SilverCoinsBalanceAfter = user.SilverCoins,
                        PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                        TransactionType = "ControlPanelUpdateEconomy",
                        Description = "Control Panel manual override of user currency balances (Direct DB)",
                        Timestamp = _timeProvider.GetUtcNow()
                    });

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] Economy updated successfully for user #{userId}.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] UpdateUserEconomyAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await UpdateUserEconomyViaApiAsync(userId, goldCoins, silverCoins, plinkoTickets, cancellationToken);
    }

    public async Task<bool> AdjustUserBalanceAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Adjusting balance for user #{userId} (ΔGold: {goldCoins}, ΔSilver: {silverCoins}, ΔTickets: {plinkoTickets})...");
                    User? user = await db.Users.Include(u => u.Accounts)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);
                    if (user == null) return false;

                    int oldGold = user.GoldCoins;
                    int oldSilver = user.SilverCoins;
                    int oldPlinko = user.PlinkoTickets;

                    user.GoldCoins = Math.Max(0, user.GoldCoins + goldCoins);
                    user.SilverCoins = Math.Max(0, user.SilverCoins + silverCoins);
                    user.PlinkoTickets = Math.Max(0, user.PlinkoTickets + plinkoTickets);

                    Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                    db.EconomyTransactions.Add(new EconomyTransaction
                    {
                        UserID = user.ID,
                        AccountID = mainAccount?.ID,
                        GoldCoinsDelta = user.GoldCoins - oldGold,
                        SilverCoinsDelta = user.SilverCoins - oldSilver,
                        PlinkoTicketsDelta = user.PlinkoTickets - oldPlinko,
                        GoldCoinsBalanceAfter = user.GoldCoins,
                        SilverCoinsBalanceAfter = user.SilverCoins,
                        PlinkoTicketsBalanceAfter = user.PlinkoTickets,
                        TransactionType = "ControlPanelAdjustBalance",
                        Description = "Control Panel balance adjustment (Direct DB)",
                        Timestamp = _timeProvider.GetUtcNow()
                    });

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] Balance adjusted successfully for user #{userId}.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] AdjustUserBalanceAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await AdjustUserBalanceViaApiAsync(userId, goldCoins, silverCoins, plinkoTickets, cancellationToken);
    }

    public async Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Promoting user #{userId} to STAFF...");
                    User? user = await db.Users
                        .Include(u => u.Role)
                        .Include(u => u.Accounts)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);

                    if (user == null) return false;

                    Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.Administrator, cancellationToken);
                    if (adminRole == null)
                    {
                        adminRole = new Role { Name = UserRoles.Administrator };
                        await db.Roles.AddAsync(adminRole, cancellationToken);
                    }

                    user.Role = adminRole;
                    Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                    if (mainAccount != null)
                    {
                        mainAccount.Type = AccountType.Staff;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] User #{userId} successfully promoted to STAFF.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] PromoteUserToStaffAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await PromoteUserToStaffViaApiAsync(userId, cancellationToken);
    }

    public async Task<bool> PromoteUserToHostAsync(int userId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Promoting user #{userId} to HOST...");
                    User? user = await db.Users
                        .Include(u => u.Accounts)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);

                    if (user == null) return false;

                    Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
                    if (mainAccount != null)
                    {
                        mainAccount.Type = AccountType.ServerHost;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] User #{userId} successfully promoted to HOST.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] PromoteUserToHostAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await PromoteUserToHostViaApiAsync(userId, cancellationToken);
    }

    public async Task<bool> RevertUserToStandardAsync(int userId, CancellationToken cancellationToken = default)
    {
        MerrickContext? db = await CreateDbContextAsync(cancellationToken);
        if (db != null)
        {
            await using (db)
            {
                try
                {
                    Log($"[DIRECT_DB] Reverting user #{userId} to standard player...");
                    User? user = await db.Users
                        .Include(u => u.Role)
                        .Include(u => u.Accounts)
                        .FirstOrDefaultAsync(u => u.ID == userId, cancellationToken);

                    if (user == null) return false;

                    Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
                    if (userRole == null)
                    {
                        userRole = new Role { Name = UserRoles.User };
                        await db.Roles.AddAsync(userRole, cancellationToken);
                    }

                    user.Role = userRole;
                    foreach (Account a in user.Accounts)
                    {
                        a.Type = AccountType.Normal;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    Log($"[DIRECT_DB] User #{userId} successfully reverted to standard player.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[DIRECT_DB] RevertUserToStandardAsync failed ({ex.Message}). Falling back to API...", true);
                }
            }
        }

        return await RevertUserToStandardViaApiAsync(userId, cancellationToken);
    }

    #region Direct DB Mapping Helpers

    private static List<UserDetailDto> MapUsersToDtos(
        List<User> users,
        List<Account> ownedHosts,
        List<HostApplication> hostApps)
    {
        return users.Select(u =>
        {
            List<int> accIds = [.. u.Accounts.Select(a => a.ID)];
            List<Account> userOwnedHosts =
            [
                .. ownedHosts.Where(h => h.OwnerAccountID.HasValue && accIds.Contains(h.OwnerAccountID.Value))
            ];
            List<Account> allAccounts = [.. u.Accounts.Concat(userOwnedHosts).DistinctBy(a => a.ID)];

            return new UserDetailDto
            {
                Id = u.ID,
                EmailAddress = u.EmailAddress,
                DiscordId = u.DiscordId,
                DiscordUsername = u.DiscordUsername,
                DiscordAvatar = u.DiscordAvatar,
                RoleName = u.Role?.Name ?? "User",
                IsStaff = u.Role?.Name == UserRoles.Administrator ||
                          u.Accounts.Any(a => a.Type == AccountType.Staff ||
                                              a.Type == AccountType.MatchModerator ||
                                              a.Type == AccountType.GameMaster),
                GoldCoins = u.GoldCoins,
                SilverCoins = u.SilverCoins,
                PlinkoTickets = u.PlinkoTickets,
                TotalLevel = u.TotalLevel,
                TotalExperience = u.TotalExperience,
                TimestampCreated = u.TimestampCreated,
                TimestampLastActive = u.TimestampLastActive,
                Accounts =
                [
                    .. allAccounts.Select(a =>
                    {
                        HostApplication? app = hostApps.FirstOrDefault(h =>
                            h.HostAccountID == a.ID || h.MainAccountID == a.ID);
                        var (hostPassword, authToken) = ResolveHostCredentials(app);
                        return new AccountItemDto
                        {
                            Id = a.ID,
                            Name = a.Name,
                            IsMain = a.IsMain,
                            IsStaff = a.Type is AccountType.Staff or AccountType.MatchModerator or AccountType.GameMaster,
                            Type = a.Type,
                            AscensionLevel = a.AscensionLevel,
                            TimestampCreated = a.TimestampCreated,
                            TimestampLastActive = a.TimestampLastActive,
                            HostApplicationId = app?.ID,
                            HostPassword = hostPassword,
                            AuthToken = authToken,
                            Icon = a.GetIconNoPrefixCode()
                        };
                    })
                ]
            };
        }).ToList();
    }

    private static (string? HostPassword, string? AuthToken) ResolveHostCredentials(HostApplication? app)
    {
        if (app == null) return (null, null);

        string? hostPassword = null;
        if (!string.IsNullOrWhiteSpace(app.EncryptedHostPassword))
        {
            hostPassword = HostSecurityService.DecryptPassword(app.EncryptedHostPassword);
            if (string.IsNullOrEmpty(hostPassword))
            {
                hostPassword = app.EncryptedHostPassword;
            }
        }

        string? authToken = null;
        if (!string.IsNullOrWhiteSpace(app.EncryptedAuthToken))
        {
            authToken = HostSecurityService.DecryptPassword(app.EncryptedAuthToken);
            if (string.IsNullOrEmpty(authToken))
            {
                authToken = app.EncryptedAuthToken;
            }
        }

        return (hostPassword, authToken);
    }

    private static UserDetailDto MapSingleUserToDto(
        User user,
        List<Account> allAccounts,
        List<HostApplication> hostApps)
    {
        return new UserDetailDto
        {
            Id = user.ID,
            EmailAddress = user.EmailAddress,
            DiscordId = user.DiscordId,
            DiscordUsername = user.DiscordUsername,
            DiscordAvatar = user.DiscordAvatar,
            RoleName = user.Role?.Name ?? "User",
            IsStaff = user.Role?.Name == UserRoles.Administrator ||
                      user.Accounts.Any(a => a.Type == AccountType.Staff ||
                                          a.Type == AccountType.MatchModerator ||
                                          a.Type == AccountType.GameMaster),
            GoldCoins = user.GoldCoins,
            SilverCoins = user.SilverCoins,
            PlinkoTickets = user.PlinkoTickets,
            TotalLevel = user.TotalLevel,
            TotalExperience = user.TotalExperience,
            TimestampCreated = user.TimestampCreated,
            TimestampLastActive = user.TimestampLastActive,
            Accounts =
            [
                .. allAccounts.Select(a =>
                {
                    HostApplication? app =
                        hostApps.FirstOrDefault(h => h.HostAccountID == a.ID || h.MainAccountID == a.ID);
                    var (hostPassword, authToken) = ResolveHostCredentials(app);
                    return new AccountItemDto
                    {
                        Id = a.ID,
                        Name = a.Name,
                        IsMain = a.IsMain,
                        IsStaff = a.Type is AccountType.Staff or AccountType.MatchModerator or AccountType.GameMaster,
                        Type = a.Type,
                        AscensionLevel = a.AscensionLevel,
                        TimestampCreated = a.TimestampCreated,
                        TimestampLastActive = a.TimestampLastActive,
                        HostApplicationId = app?.ID,
                        HostPassword = hostPassword,
                        AuthToken = authToken,
                        Icon = a.GetIconNoPrefixCode()
                    };
                })
            ]
        };
    }

    private static string GenerateSecureAlphaNumericString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$*=";
        byte[] randomBytes = RandomNumberGenerator.GetBytes(length);
        StringBuilder sb = new(length);
        for (int i = 0; i < length; i++) sb.Append(chars[randomBytes[i] % chars.Length]);
        return sb.ToString();
    }

    #endregion

    #region DbContext Factory

    private async Task<MerrickContext?> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        if (_dbContextFactory != null)
        {
            return _dbContextFactory();
        }

        string? connStr = null;
        if (_secretsManager != null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(secrets.SupabaseDatabaseConnectionString))
                {
                    connStr = secrets.SupabaseDatabaseConnectionString;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load secrets for database connection: {ex.Message}", true);
            }
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            connStr = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING")
                      ?? Environment.GetEnvironmentVariable("ConnectionStrings:MERRICK")
                      ?? Environment.GetEnvironmentVariable("DATABASE_POSTGRESQL_CONNECTION_STRING");
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            return null;
        }

        string normalized = ConfigurationExtensions.NormalizePostgreSqlConnectionString(connStr);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        lock (_optionsLock)
        {
            if (_cachedOptions == null || _cachedDataSource == null || _cachedConnectionString != normalized)
            {
                _cachedDataSource?.Dispose();
                Npgsql.NpgsqlDataSourceBuilder dsBuilder = new(normalized);
                // NEXUS Control Panel diagnostic DB fallback shares the low-limit Supavisor session pool:
                // cap strictly so Control Panel polling cannot contribute to EMAXCONNSESSION exhaustion.
                if (dsBuilder.ConnectionStringBuilder.Host?.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true &&
                    dsBuilder.ConnectionStringBuilder.Port != 6543 &&
                    !dsBuilder.ConnectionStringBuilder.NoResetOnClose)
                {
                    dsBuilder.ConnectionStringBuilder.MaxPoolSize = 2;
                    dsBuilder.ConnectionStringBuilder.MinPoolSize = 0;
                    dsBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 10;
                    dsBuilder.ConnectionStringBuilder.ConnectionPruningInterval = 5;
                }
                else
                {
                    dsBuilder.ConnectionStringBuilder.MaxPoolSize = Math.Min(
                        dsBuilder.ConnectionStringBuilder.MaxPoolSize > 0 ? dsBuilder.ConnectionStringBuilder.MaxPoolSize : 5,
                        5);
                }

                _cachedDataSource = dsBuilder.Build();
                DbContextOptionsBuilder<MerrickContext> builder = new();
                builder.UseNpgsql(_cachedDataSource, npgsql =>
                {
                    npgsql.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
                    npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    npgsql.EnableRetryOnFailure(3);
                    npgsql.CommandTimeout(15);
                });
                _cachedOptions = builder.Options;
                _cachedConnectionString = normalized;
            }
        }

        return new MerrickContext(_cachedOptions);
    }

    #endregion

    #region HTTP API Fallback Methods

    private async Task<UserSummaryStats> GetSummaryStatsViaApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log("[API_FALLBACK] Connecting to ZORGATH WebPortal API...");
            using HttpRequestMessage req =
                await CreateAuthenticatedRequestAsync(HttpMethod.Get, "api/admin/users/stats", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);

            if (resp.IsSuccessStatusCode)
            {
                UserSummaryStats? stats =
                    await resp.Content.ReadFromJsonAsync<UserSummaryStats>(JsonOptions, cancellationToken);
                if (stats != null)
                {
                    Log($"[API_FALLBACK] API stats: {stats.TotalUsers} users, {stats.TotalAccounts} accounts, {stats.TotalStaff} staff, {stats.TotalHosts} hosts.");
                    return stats;
                }
            }

            return new UserSummaryStats
            {
                TotalUsers = 0,
                TotalAccounts = 0,
                TotalStaff = 0,
                TotalHosts = 0,
                IsDatabaseConnected = false,
                StatusMessage = $"API response: {resp.StatusCode}"
            };
        }
        catch (OperationCanceledException)
        {
            Log("[API_FALLBACK] ZORGATH WebPortal API is offline or timed out at port 5200.");
            return new UserSummaryStats
            {
                IsDatabaseConnected = false,
                StatusMessage = "Web API service offline (Port 5200)"
            };
        }
        catch (HttpRequestException ex)
        {
            Log($"[API_FALLBACK] ZORGATH WebPortal API unreachable: {ex.Message}");
            return new UserSummaryStats
            {
                IsDatabaseConnected = false,
                StatusMessage = "Web API service unreachable"
            };
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] GetSummaryStatsAsync error: {ex.Message}", true);
            return new UserSummaryStats
            {
                IsDatabaseConnected = false,
                StatusMessage = $"API error: {ex.Message}"
            };
        }
    }

    private async Task<List<UserDetailDto>> SearchUsersViaApiAsync(string query, string roleFilter,
        CancellationToken cancellationToken)
    {
        try
        {
            string queryParam = Uri.EscapeDataString(query ?? string.Empty);
            string roleParam = Uri.EscapeDataString(roleFilter ?? string.Empty);
            string path = $"api/admin/users?query={queryParam}&roleFilter={roleParam}";

            using HttpRequestMessage req =
                await CreateAuthenticatedRequestAsync(HttpMethod.Get, path, cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);

            if (resp.IsSuccessStatusCode)
            {
                List<UserDetailDto>? dtos =
                    await resp.Content.ReadFromJsonAsync<List<UserDetailDto>>(JsonOptions, cancellationToken);
                return dtos ?? [];
            }

            Log($"[API_FALLBACK] SearchUsersAsync returned status {resp.StatusCode}");
            return [];
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] SearchUsersAsync error: {ex.Message}", true);
            return [];
        }
    }

    private async Task<UserDetailDto?> GetUserByIdViaApiAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req =
                await CreateAuthenticatedRequestAsync(HttpMethod.Get, $"api/admin/users/{userId}", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);

            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<UserDetailDto>(JsonOptions, cancellationToken);

            return null;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] GetUserByIdAsync error for #{userId}: {ex.Message}", true);
            return null;
        }
    }

    private async Task<bool> SetUserRoleViaApiAsync(int userId, string roleName, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/role", cancellationToken);
            req.Content = JsonContent.Create(new SetUserRoleRequest { RoleName = roleName });
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] SetUserRoleAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<bool> SetAccountRoleViaApiAsync(int accountId, AccountType type,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/accounts/{accountId}/role", cancellationToken);
            req.Content = JsonContent.Create(new SetAccountRoleRequest { Type = (int)type });
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] SetAccountRoleAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<string?> ResetHostPasswordViaApiAsync(int accountId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/accounts/{accountId}/reset-password", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                ResetCredentialResponse? data =
                    await resp.Content.ReadFromJsonAsync<ResetCredentialResponse>(JsonOptions, cancellationToken);
                return data?.Value;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] ResetHostPasswordAsync error: {ex.Message}", true);
            return null;
        }
    }

    private async Task<string?> ResetHostAuthTokenViaApiAsync(int accountId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/accounts/{accountId}/reset-token", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                ResetCredentialResponse? data =
                    await resp.Content.ReadFromJsonAsync<ResetCredentialResponse>(JsonOptions, cancellationToken);
                return data?.Value;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] ResetHostAuthTokenAsync error: {ex.Message}", true);
            return null;
        }
    }

    private async Task<bool> UpdateUserEconomyViaApiAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/economy", cancellationToken);
            req.Content = JsonContent.Create(new UpdateUserEconomyRequest
            {
                GoldCoins = goldCoins,
                SilverCoins = silverCoins,
                PlinkoTickets = plinkoTickets
            });
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] UpdateUserEconomyAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<bool> AdjustUserBalanceViaApiAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/adjust-balance", cancellationToken);
            req.Content = JsonContent.Create(new AdjustUserBalanceRequest
            {
                GoldDelta = goldCoins,
                SilverDelta = silverCoins,
                TicketsDelta = plinkoTickets
            });
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] AdjustUserBalanceAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<bool> PromoteUserToStaffViaApiAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/promote-staff", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] PromoteUserToStaffAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<bool> PromoteUserToHostViaApiAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/promote-host", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] PromoteUserToHostAsync error: {ex.Message}", true);
            return false;
        }
    }

    private async Task<bool> RevertUserToStandardViaApiAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post,
                $"api/admin/users/{userId}/revert-standard", cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log($"[API_FALLBACK] RevertUserToStandardAsync error: {ex.Message}", true);
            return false;
        }
    }

    #endregion

    private void Log(string message, bool isError = false)
    {
        _logSink?.Invoke("USER_MGMT", message, isError);
    }

    private async Task<string> ResolveBaseApiUrlAsync(CancellationToken cancellationToken)
    {
        string? envUrl = Environment.GetEnvironmentVariable("WEB_PORTAL_API_URL")
                         ?? Environment.GetEnvironmentVariable("ZORGATH_API_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
            return envUrl.TrimEnd('/');

        int port = 5200;
        if (_secretsManager != null)
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (secrets.WebPortalApiHttpPort > 0 && secrets.WebPortalApiHttpPort != 5100)
                    port = secrets.WebPortalApiHttpPort;
            }
            catch
            {
                // Fallback default
            }

        return $"http://127.0.0.1:{port}";
    }

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(HttpMethod method, string path,
        CancellationToken cancellationToken)
    {
        string baseUrl = await ResolveBaseApiUrlAsync(cancellationToken);
        string url = $"{baseUrl}/{path.TrimStart('/')}";
        HttpRequestMessage request = new(method, url);

        string serviceKey = SecurityConstants.DefaultInternalServiceKey;
        if (_secretsManager != null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(secrets.InternalServiceKey))
                {
                    serviceKey = secrets.InternalServiceKey.Trim();
                }
            }
            catch
            {
                // Fallback default
            }
        }
        else
        {
            string? envKey = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                serviceKey = envKey.Trim();
            }
        }

        request.Headers.TryAddWithoutValidation(SecurityConstants.InternalServiceKeyHeader, serviceKey);
        return request;
    }
}
