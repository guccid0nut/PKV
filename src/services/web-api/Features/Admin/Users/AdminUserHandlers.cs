// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Users;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

public sealed class GetAdminUserSummaryHandler(
    MerrickContext databaseContext,
    ILogger<GetAdminUserSummaryHandler> logger)
    : IRequestHandler<GetAdminUserSummaryQuery, UserSummaryStatsDto>
{
    public async Task<UserSummaryStatsDto> Handle(GetAdminUserSummaryQuery request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Calculating admin user summary statistics");
        int totalUsers = await databaseContext.Users
            .CountAsync(u => !u.EmailAddress.StartsWith("host_"), cancellationToken);
        int totalAccounts = await databaseContext.Accounts.CountAsync(cancellationToken);
        int totalStaff = await databaseContext.Users.CountAsync(
            u => !u.EmailAddress.StartsWith("host_") &&
                 (u.Role.Name == UserRoles.Administrator ||
                  u.Accounts.Any(a =>
                      a.Type == AccountType.Staff || a.Type == AccountType.MatchModerator ||
                      a.Type == AccountType.GameMaster)),
            cancellationToken);
        int totalHosts = await databaseContext.Accounts
            .CountAsync(a => a.Type == AccountType.ServerHost, cancellationToken);

        return new UserSummaryStatsDto
        {
            TotalUsers = totalUsers,
            TotalAccounts = totalAccounts,
            TotalStaff = totalStaff,
            TotalHosts = totalHosts,
            IsDatabaseConnected = true,
            StatusMessage = "Connected to Database via ZORGATH WebPortal API."
        };
    }
}

public sealed class SearchAdminUsersHandler(
    MerrickContext databaseContext,
    ILogger<SearchAdminUsersHandler> logger)
    : IRequestHandler<SearchAdminUsersQuery, List<AdminUserDetailDto>>
{
    public async Task<List<AdminUserDetailDto>> Handle(SearchAdminUsersQuery request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Searching admin users with Query: '{Query}', RoleFilter: '{RoleFilter}'",
            request.Query, request.RoleFilter);

        IQueryable<User> queryable = databaseContext.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts)
                .ThenInclude(a => a.Statistics)
            .Include(u => u.Accounts)
                .ThenInclude(a => a.SelectedStoreItems)
            .Include(u => u.InventoryItems)
            .Where(u => !u.EmailAddress.StartsWith("host_"));

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            string q = request.Query.Trim();
            queryable = queryable.Where(u =>
                u.EmailAddress.Contains(q) ||
                (u.DiscordUsername != null && u.DiscordUsername.Contains(q)) ||
                (u.DiscordId != null && u.DiscordId.Contains(q)) ||
                u.Accounts.Any(a => a.Name.Contains(q)));
        }

        if (!string.IsNullOrWhiteSpace(request.RoleFilter))
        {
            string rf = request.RoleFilter.Trim().ToUpperInvariant();
            if (rf == "STAFF")
            {
                queryable = queryable.Where(u =>
                    u.Role.Name == UserRoles.Administrator ||
                    u.Accounts.Any(a => a.Type == AccountType.Staff ||
                                        a.Type == AccountType.MatchModerator ||
                                        a.Type == AccountType.GameMaster));
            }
            else if (rf == "HOST")
            {
                queryable = queryable.Where(u =>
                    u.Accounts.Any(a => a.Type == AccountType.ServerHost ||
                                        databaseContext.Accounts.Any(ha =>
                                            ha.Type == AccountType.ServerHost &&
                                            ha.OwnerAccountID == a.ID)));
            }
            else if (rf == "STANDARD")
            {
                queryable = queryable.Where(u =>
                    u.Role.Name != UserRoles.Administrator &&
                    !u.Accounts.Any(a =>
                        a.Type == AccountType.Staff ||
                        a.Type == AccountType.MatchModerator ||
                        a.Type == AccountType.GameMaster ||
                        a.Type == AccountType.ServerHost ||
                        databaseContext.Accounts.Any(ha =>
                            ha.Type == AccountType.ServerHost &&
                            ha.OwnerAccountID == a.ID)));
            }
        }

        List<User> users = await queryable
            .OrderBy(u => u.ID)
            .Take(100)
            .ToListAsync(cancellationToken);

        List<int> accountIds = users.SelectMany(u => u.Accounts).Select(a => a.ID).Distinct().ToList();

        List<Account> ownedHosts = await databaseContext.Accounts
            .Include(a => a.Statistics)
            .Include(a => a.SelectedStoreItems)
            .Where(a => a.Type == AccountType.ServerHost && a.OwnerAccountID.HasValue && accountIds.Contains(a.OwnerAccountID.Value))
            .ToListAsync(cancellationToken);

        List<HostApplication> hostApps = await databaseContext.HostApplications
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
            await databaseContext.SaveChangesAsync(cancellationToken);
        }

        return users.Select(u =>
        {
            List<int> accIds = [.. u.Accounts.Select(a => a.ID)];
            List<Account> userOwnedHosts =
            [
                .. ownedHosts.Where(h => h.OwnerAccountID.HasValue && accIds.Contains(h.OwnerAccountID.Value))
            ];
            List<Account> allAccounts = [.. u.Accounts.Concat(userOwnedHosts).DistinctBy(a => a.ID)];

            return new AdminUserDetailDto
            {
                Id = u.ID,
                EmailAddress = u.EmailAddress,
                DiscordId = u.DiscordId,
                DiscordUsername = u.DiscordUsername,
                DiscordAvatar = u.DiscordAvatar,
                RoleName = u.Role?.Name ?? "User",
                IsStaff = u.Role?.Name == UserRoles.Administrator ||
                          u.Accounts.Any(a => a.Type == AccountType.Staff),
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
                        return new AdminAccountItemDto
                        {
                            Id = a.ID,
                            Name = a.Name,
                            IsMain = a.IsMain,
                            IsStaff = a.Type is AccountType.Staff or AccountType.MatchModerator
                                or AccountType.GameMaster,
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
}

public sealed class GetAdminUserByIdHandler(
    MerrickContext databaseContext,
    ILogger<GetAdminUserByIdHandler> logger)
    : IRequestHandler<GetAdminUserByIdQuery, AdminUserDetailDto?>
{
    public async Task<AdminUserDetailDto?> Handle(GetAdminUserByIdQuery request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Retrieving admin user profile for User #{UserId}", request.Id);

        User? user = await databaseContext.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts)
                .ThenInclude(a => a.Statistics)
            .Include(u => u.Accounts)
                .ThenInclude(a => a.SelectedStoreItems)
            .Include(u => u.InventoryItems)
            .FirstOrDefaultAsync(u => u.ID == request.Id, cancellationToken);

        if (user == null) return null;

        List<int> currentAccountIds = [.. user.Accounts.Select(a => a.ID)];
        List<Account> userOwnedHosts = await databaseContext.Accounts
            .Include(a => a.Statistics)
            .Include(a => a.SelectedStoreItems)
            .Where(a => a.Type == AccountType.ServerHost && a.OwnerAccountID.HasValue &&
                        currentAccountIds.Contains(a.OwnerAccountID.Value))
            .ToListAsync(cancellationToken);

        List<int> allHostIds = [.. currentAccountIds, .. userOwnedHosts.Select(h => h.ID)];
        List<HostApplication> hostApps = await databaseContext.HostApplications
            .Where(h => h.UserID == request.Id ||
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
            await databaseContext.SaveChangesAsync(cancellationToken);
        }

        List<Account> allAccounts = [.. user.Accounts.Concat(userOwnedHosts).DistinctBy(a => a.ID)];

        return new AdminUserDetailDto
        {
            Id = user.ID,
            EmailAddress = user.EmailAddress,
            DiscordId = user.DiscordId,
            DiscordUsername = user.DiscordUsername,
            DiscordAvatar = user.DiscordAvatar,
            RoleName = user.Role?.Name ?? "User",
            IsStaff = user.Role?.Name == UserRoles.Administrator ||
                      user.Accounts.Any(a => a.Type == AccountType.Staff),
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
                    HostApplication? app = hostApps.FirstOrDefault(h =>
                        h.HostAccountID == a.ID || h.MainAccountID == a.ID);
                    var (hostPassword, authToken) = ResolveHostCredentials(app);
                    return new AdminAccountItemDto
                    {
                        Id = a.ID,
                        Name = a.Name,
                        IsMain = a.IsMain,
                        IsStaff = a.Type is AccountType.Staff or AccountType.MatchModerator
                            or AccountType.GameMaster,
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

    private static List<Account> userOwnedHosts(List<Account> ownedHosts, List<int> accIds) =>
        [.. ownedHosts.Where(h => h.OwnerAccountID.HasValue && accIds.Contains(h.OwnerAccountID.Value))];

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
}

public sealed class SetUserRoleHandler(
    MerrickContext databaseContext,
    ILogger<SetUserRoleHandler> logger)
    : IRequestHandler<SetUserRoleCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(SetUserRoleCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Setting User #{UserId} role to {RoleName}", request.UserId, request.RoleName);

        User? user = await databaseContext.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);
        if (user == null)
        {
            logger.LogWarning("User #{UserId} not found", request.UserId);
            return new AdminActionResultDto(false, $"User #{request.UserId} not found", request.UserId);
        }

        Role? targetRole = await databaseContext.Roles
            .FirstOrDefaultAsync(r => r.Name == request.RoleName, cancellationToken);
        if (targetRole == null)
        {
            targetRole = new Role { Name = request.RoleName };
            await databaseContext.Roles.AddAsync(targetRole, cancellationToken);
        }

        user.Role = targetRole;
        await databaseContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User #{UserId} role updated to {RoleName}", request.UserId, request.RoleName);
        return new AdminActionResultDto(true, $"Role updated to {request.RoleName}", request.UserId);
    }
}

public sealed class ResetHostPasswordHandler(
    MerrickContext databaseContext,
    ILogger<ResetHostPasswordHandler> logger)
    : IRequestHandler<ResetHostPasswordCommand, ResetCredentialResponse?>
{
    public async Task<ResetCredentialResponse?> Handle(ResetHostPasswordCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Resetting host password for Account #{AccountId}", request.AccountId);

        Account? account = await databaseContext.Accounts.Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);
        if (account == null) return null;

        string rawPassword = HostSecurityService.GenerateRandomHostPassword(16);
        string salt = HostSecurityService.GenerateSrpSalt();
        string srpHash = HostSecurityService.ComputeSrpHash(rawPassword, salt, passwordIsHashed: false);

        account.User.SRPPasswordSalt = salt;
        account.User.SRPPasswordHash = srpHash;

        HostApplication? hostApp = await databaseContext.HostApplications
            .FirstOrDefaultAsync(h => h.HostAccountID == request.AccountId || h.MainAccountID == request.AccountId, cancellationToken);
        if (hostApp != null)
        {
            hostApp.EncryptedHostPassword = HostSecurityService.EncryptPassword(rawPassword);
        }

        await databaseContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Host password successfully reset for Account #{AccountId}", request.AccountId);
        return new ResetCredentialResponse { Value = rawPassword };
    }
}

public sealed class ResetHostAuthTokenHandler(
    MerrickContext databaseContext,
    ILogger<ResetHostAuthTokenHandler> logger)
    : IRequestHandler<ResetHostAuthTokenCommand, ResetCredentialResponse?>
{
    public async Task<ResetCredentialResponse?> Handle(ResetHostAuthTokenCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Resetting host auth token for Account #{AccountId}", request.AccountId);

        Account? account = await databaseContext.Accounts
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);
        if (account == null) return null;

        string rawToken = "tok_" + Guid.NewGuid().ToString("N")[..20];
        HostApplication? hostApp = await databaseContext.HostApplications
            .FirstOrDefaultAsync(h => h.HostAccountID == request.AccountId || h.MainAccountID == request.AccountId, cancellationToken);
        if (hostApp != null)
        {
            hostApp.EncryptedAuthToken = HostSecurityService.EncryptPassword(rawToken);
            await databaseContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Host auth token successfully reset for Account #{AccountId}", request.AccountId);
        return new ResetCredentialResponse { Value = rawToken };
    }
}

public sealed class UpdateUserEconomyHandler(
    MerrickContext databaseContext,
    ILogger<UpdateUserEconomyHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<UpdateUserEconomyCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(UpdateUserEconomyCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating economy for User #{UserId} to Gold: {Gold}, Silver: {Silver}, Plinko: {Plinko}",
            request.UserId, request.GoldCoins, request.SilverCoins, request.PlinkoTickets);

        User? user = await databaseContext.Users.Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);
        if (user == null) return new AdminActionResultDto(false, $"User #{request.UserId} not found", request.UserId);

        int goldDelta = request.GoldCoins - user.GoldCoins;
        int silverDelta = request.SilverCoins - user.SilverCoins;
        int plinkoDelta = request.PlinkoTickets - user.PlinkoTickets;

        user.GoldCoins = Math.Max(0, request.GoldCoins);
        user.SilverCoins = Math.Max(0, request.SilverCoins);
        user.PlinkoTickets = Math.Max(0, request.PlinkoTickets);

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        databaseContext.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = mainAccount?.ID,
            GoldCoinsDelta = goldDelta,
            SilverCoinsDelta = silverDelta,
            PlinkoTicketsDelta = plinkoDelta,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "AdminUpdateEconomy",
            Description = "Admin manual override of user currency balances",
            Timestamp = _timeProvider.GetUtcNow()
        });

        await databaseContext.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "Economy balances updated successfully", user.ID);
    }
}

public sealed class PromoteUserToStaffHandler(
    MerrickContext databaseContext,
    ILogger<PromoteUserToStaffHandler> logger)
    : IRequestHandler<PromoteUserToStaffCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(PromoteUserToStaffCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Promoting User #{UserId} to Staff", request.UserId);

        User? user = await databaseContext.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);

        if (user == null) return new AdminActionResultDto(false, $"User #{request.UserId} not found", request.UserId);

        Role? adminRole = await databaseContext.Roles
            .FirstOrDefaultAsync(r => r.Name == UserRoles.Administrator, cancellationToken);
        if (adminRole == null)
        {
            adminRole = new Role { Name = UserRoles.Administrator };
            await databaseContext.Roles.AddAsync(adminRole, cancellationToken);
        }

        user.Role = adminRole;
        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        if (mainAccount != null) mainAccount.Type = AccountType.Staff;

        await databaseContext.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "User promoted to staff successfully", user.ID);
    }
}

public sealed class PromoteUserToHostHandler(
    MerrickContext databaseContext,
    ILogger<PromoteUserToHostHandler> logger)
    : IRequestHandler<PromoteUserToHostCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(PromoteUserToHostCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Promoting User #{UserId} main account to ServerHost", request.UserId);

        User? user = await databaseContext.Users
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);

        if (user == null) return new AdminActionResultDto(false, $"User #{request.UserId} not found", request.UserId);

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        if (mainAccount != null) mainAccount.Type = AccountType.ServerHost;

        await databaseContext.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "User promoted to host successfully", user.ID);
    }
}

public sealed class RevertUserToStandardHandler(
    MerrickContext databaseContext,
    ILogger<RevertUserToStandardHandler> logger)
    : IRequestHandler<RevertUserToStandardCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(RevertUserToStandardCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Reverting User #{UserId} to standard status", request.UserId);

        User? user = await databaseContext.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);

        if (user == null) return new AdminActionResultDto(false, $"User #{request.UserId} not found", request.UserId);

        Role? userRole = await databaseContext.Roles
            .FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
        if (userRole == null)
        {
            userRole = new Role { Name = UserRoles.User };
            await databaseContext.Roles.AddAsync(userRole, cancellationToken);
        }

        user.Role = userRole;
        foreach (Account a in user.Accounts) a.Type = AccountType.Normal;

        await databaseContext.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "User reverted to standard successfully", user.ID);
    }
}
