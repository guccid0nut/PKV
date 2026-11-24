// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;

namespace MERRICK.DatabaseContext.Repositories;

public class AccountRepository(MerrickContext db, ILogger<AccountRepository>? logger = null) : IAccountRepository
{
    private readonly ILogger<AccountRepository> _logger = logger ?? NullLogger<AccountRepository>.Instance;

    internal MerrickContext DbContext => db;

    public async Task<Account?> GetByIdAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching Account #{AccountId} with related profile graphs", accountId);
        return await db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.Clan)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .Include(a => a.SelectedStoreItems)
            .Include(a => a.IPAddressCollection)
            .Include(a => a.MACAddressCollection)
            .Include(a => a.BlockedPhrases)
            .Include(a => a.Peers)
            .Include(a => a.GuidePreferences)
            .Include(a => a.Friendships).ThenInclude(f => f.FriendAccount).ThenInclude(fa => fa!.Clan)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);
    }

    public async Task<Account?> GetByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        string trimmed = nickname.Trim();
        _logger.LogDebug("[AccountRepository] Fetching Account by Nickname '{Nickname}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts
                .AsNoTracking()
                .AsSplitQuery()
                .Include(a => a.User).ThenInclude(u => u.Role)
                .Include(a => a.User).ThenInclude(u => u.InventoryItems)
                .Include(a => a.Clan)
                .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                .Include(a => a.SelectedStoreItems)
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .Include(a => a.Peers)
                .Include(a => a.GuidePreferences)
                .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
        }

        Account? account = await db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.User).ThenInclude(u => u.InventoryItems)
            .Include(a => a.Clan)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .Include(a => a.SelectedStoreItems)
            .Include(a => a.IPAddressCollection)
            .Include(a => a.MACAddressCollection)
            .Include(a => a.BlockedPhrases)
            .Include(a => a.Peers)
            .Include(a => a.GuidePreferences)
            .FirstOrDefaultAsync(a => a.Name == trimmed, ct);

        return account;
    }

    public async Task<int?> GetIdByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        string trimmed = nickname.Trim();
        _logger.LogDebug("[AccountRepository] Resolving lightweight Account ID for Nickname '{Nickname}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts
                .AsNoTracking()
                .Where(a => a.Name.ToLower() == lower)
                .Select(a => (int?)a.ID)
                .FirstOrDefaultAsync(ct);
        }

        return await db.Accounts
            .AsNoTracking()
            .Where(a => a.Name == trimmed)
            .Select(a => (int?)a.ID)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Account?> GetByUserIdAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching Account by UserId #{UserId}", userId);
        return await db.Accounts
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(a => a.User).ThenInclude(u => u.Role)
                   .Include(a => a.Clan)
                   .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                   .FirstOrDefaultAsync(a => a.User.ID == userId && a.IsMain, ct)
               ?? await db.Accounts
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(a => a.User).ThenInclude(u => u.Role)
                   .Include(a => a.Clan)
                   .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                   .FirstOrDefaultAsync(a => a.User.ID == userId, ct);
    }

    public async Task<bool> ExistsByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return false;
        string trimmed = nickname.Trim();
        _logger.LogDebug("[AccountRepository] Checking existence for Nickname '{Nickname}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts.AnyAsync(a => a.Name.ToLower() == lower, ct);
        }

        return await db.Accounts.AnyAsync(a => a.Name == trimmed, ct);
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        string trimmed = email.Trim();
        _logger.LogDebug("[AccountRepository] Checking existence for Email '{Email}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Users.AnyAsync(u => u.EmailAddress.ToLower() == lower, ct);
        }

        return await db.Users.AnyAsync(u => u.EmailAddress == trimmed, ct);
    }

    public async Task<Account> CreateAsync(Account account, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Creating Account '{AccountName}' for User #{UserId}", account.Name,
            account.User?.ID ?? 0);
        await db.Accounts.AddAsync(account, ct);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Updating Account #{AccountId} ('{AccountName}')", account.ID,
            account.Name);
        EntityEntry<Account>? existingEntry =
            db.ChangeTracker.Entries<Account>().FirstOrDefault(e => e.Entity.ID == account.ID);
        if (existingEntry == null)
        {
            Account? dbAccount = await db.Accounts
                .Include(a => a.SelectedStoreItems)
                .Include(a => a.Peers)
                .Include(a => a.GuidePreferences)
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.ID == account.ID, ct);

            if (dbAccount != null)
            {
                existingEntry = db.Entry(dbAccount);
            }
        }

        if (existingEntry != null)
        {
            if (!ReferenceEquals(existingEntry.Entity, account))
            {
                existingEntry.CurrentValues.SetValues(account);

                if (account.Peers != null)
                {
                    existingEntry.Entity.Peers ??= [];
                    existingEntry.Entity.Peers.RemoveAll(p => !account.Peers.Any(ap => ap.PeerAccountID == p.PeerAccountID && ap.Type == p.Type));
                    foreach (AccountPeer ap in account.Peers)
                    {
                        AccountPeer? match = existingEntry.Entity.Peers.FirstOrDefault(p => p.PeerAccountID == ap.PeerAccountID && p.Type == ap.Type);
                        if (match == null)
                        {
                            existingEntry.Entity.Peers.Add(new AccountPeer
                            {
                                AccountID = existingEntry.Entity.ID,
                                PeerAccountID = ap.PeerAccountID,
                                Type = ap.Type,
                                Reason = ap.Reason,
                                TimestampCreated = ap.TimestampCreated,
                                TimestampExpires = ap.TimestampExpires
                            });
                        }
                        else
                        {
                            match.Reason = ap.Reason;
                            match.TimestampExpires = ap.TimestampExpires;
                        }
                    }
                }

                HashSet<string> desiredCodes = new(account.SelectedStoreItems.Select(i => i.ItemCode), StringComparer.OrdinalIgnoreCase);
                existingEntry.Entity.SelectedStoreItems.RemoveAll(i => !desiredCodes.Contains(i.ItemCode));
                HashSet<string> currentCodes = new(existingEntry.Entity.SelectedStoreItems.Select(i => i.ItemCode), StringComparer.OrdinalIgnoreCase);
                foreach (AccountSelectedStoreItem item in account.SelectedStoreItems)
                {
                    if (!currentCodes.Contains(item.ItemCode))
                    {
                        existingEntry.Entity.SelectedStoreItems.Add(new AccountSelectedStoreItem
                        {
                            AccountID = existingEntry.Entity.ID,
                            ItemCode = item.ItemCode
                        });
                        currentCodes.Add(item.ItemCode);
                    }
                }

                if (account.GuidePreferences != null)
                {
                    existingEntry.Entity.GuidePreferences ??= [];
                    existingEntry.Entity.GuidePreferences.RemoveAll(p => !account.GuidePreferences.Any(gp => gp.GuideID == p.GuideID));
                    foreach (AccountGuidePreference gp in account.GuidePreferences)
                    {
                        AccountGuidePreference? match = existingEntry.Entity.GuidePreferences.FirstOrDefault(p => p.GuideID == gp.GuideID);
                        if (match == null)
                        {
                            existingEntry.Entity.GuidePreferences.Add(new AccountGuidePreference
                            {
                                AccountID = existingEntry.Entity.ID,
                                GuideID = gp.GuideID,
                                IsDefault = gp.IsDefault,
                                IsFavorite = gp.IsFavorite,
                                TimestampUpdated = gp.TimestampUpdated
                            });
                        }
                        else
                        {
                            match.IsDefault = gp.IsDefault;
                            match.IsFavorite = gp.IsFavorite;
                            match.TimestampUpdated = gp.TimestampUpdated;
                        }
                    }
                }

                if (account.User != null)
                {
                    EntityEntry<User>? userEntry = db.ChangeTracker.Entries<User>()
                        .FirstOrDefault(e => e.Entity.ID == account.User.ID);
                    if (userEntry != null && !ReferenceEquals(userEntry.Entity, account.User))
                    {
                        if (account.User.InventoryItems?.Any(i => i.ID == 0) == true &&
                            !userEntry.Collection(u => u.InventoryItems).IsLoaded)
                        {
                            int newItemCount = account.User.InventoryItems.Count(i => i.ID == 0);
                            _logger.LogError(
                                "[AccountRepository] Caller passed {NewItemCount} untracked UserInventory item(s) with ID==0 on detached Account #{AccountId} ('{AccountName}') while User.InventoryItems is not loaded; AccountRepository.UpdateAsync is scalar/profile-only and will NOT persist InventoryItems! Use a tracked MerrickContext transaction instead.",
                                newItemCount, account.ID, account.Name);
                        }

                        userEntry.CurrentValues.SetValues(account.User);
                    }
                }
            }
        }
        else
        {
            db.Accounts.Update(account);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task BanAccountAsync(int accountId, DateTimeOffset? expiresAt, string reason, int? bannedBy = null,
        CancellationToken ct = default)
    {
        _logger.LogWarning("[AccountRepository] Banning Account #{AccountId} until {ExpiresAt}. Reason: {Reason}",
            accountId, expiresAt, reason);
        try
        {
            await db.Accounts
                .Where(a => a.ID == accountId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.BanExpiration, expiresAt)
                    .SetProperty(a => a.BanReason, reason)
                    .SetProperty(a => a.Type, AccountType.Disabled), ct);
        }
        catch (InvalidOperationException)
        {
            Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
            if (account is not null)
            {
                account.BanExpiration = expiresAt;
                account.BanReason = reason;
                account.Type = AccountType.Disabled;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task UnbanAccountAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Unbanning Account #{AccountId}", accountId);
        try
        {
            await db.Accounts
                .Where(a => a.ID == accountId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.BanExpiration, (DateTimeOffset?)null)
                    .SetProperty(a => a.BanReason, (string?)null)
                    .SetProperty(a => a.Type, a => a.Type == AccountType.Disabled ? AccountType.Normal : a.Type), ct);
        }
        catch (InvalidOperationException)
        {
            Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
            if (account is not null)
            {
                account.BanExpiration = null;
                account.BanReason = null;
                if (account.Type == AccountType.Disabled) account.Type = AccountType.Normal;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching User #{UserId}", userId);
        return await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan)
            .Include(u => u.InventoryItems)
            .FirstOrDefaultAsync(u => u.ID == userId, ct);
    }

    public async Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        string trimmed = email.Trim();
        _logger.LogDebug("[AccountRepository] Fetching User by Email '{Email}'", trimmed);

        IQueryable<User> query = db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan)
            .Include(u => u.InventoryItems);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await query.FirstOrDefaultAsync(u => u.EmailAddress.ToLower() == lower || u.EmailAddress.ToLower() == $"d_{lower}", ct);
        }

        string dEmail = $"d_{trimmed}";
        return await query.FirstOrDefaultAsync(u => u.EmailAddress == trimmed || u.EmailAddress == dEmail, ct);
    }

    public async Task<User?> GetUserByDiscordIdAsync(string discordId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching User by DiscordId '{DiscordId}'", discordId);
        return await db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan)
            .Include(u => u.InventoryItems)
            .FirstOrDefaultAsync(u => u.DiscordId == discordId, ct);
    }

    public async Task<User?> GetUserByAccountNameAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        string trimmed = accountName.Trim();
        _logger.LogDebug("[AccountRepository] Fetching User by Account Name '{AccountName}'", trimmed);

        IQueryable<User> query = db.Users
            .AsNoTracking()
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await query.FirstOrDefaultAsync(u => u.Accounts.Any(a => a.Name.ToLower() == lower), ct);
        }

        return await query.FirstOrDefaultAsync(u => u.Accounts.Any(a => a.Name == trimmed), ct);
    }

    public async Task<User> RegisterOrLoginDiscordUserAsync(DiscordAuthRequestDto payload,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] RegisterOrLoginDiscordUserAsync for DiscordId '{DiscordId}'",
            payload.DiscordId);

        User? user = await db.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan)
            .Include(u => u.Accounts).ThenInclude(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(u => u.DiscordId == payload.DiscordId, ct);

        if (user == null)
        {
            string fallbackEmailPrefix = $"d_{payload.DiscordId}";
            user = await db.Users
                .Include(u => u.Role)
                .Include(u => u.Accounts).ThenInclude(a => a.Clan)
                .Include(u => u.Accounts).ThenInclude(a => a.SelectedStoreItems)
                .FirstOrDefaultAsync(u =>
                    (!string.IsNullOrWhiteSpace(payload.Email) && u.EmailAddress == payload.Email && string.IsNullOrEmpty(u.DiscordId)) ||
                    u.EmailAddress == fallbackEmailPrefix ||
                    u.EmailAddress == $"{fallbackEmailPrefix}@discord.projectkongor.local", ct);

            if (user != null)
            {
                bool isPrivilegedCandidate =
                    (user.Role != null && string.Equals(user.Role.Name, UserRoles.Administrator, StringComparison.OrdinalIgnoreCase)) ||
                    user.Accounts.Any(a => a.Type is AccountType.Staff or AccountType.ServerHost);

                if ((!string.IsNullOrEmpty(user.DiscordId) && user.DiscordId != payload.DiscordId) || isPrivilegedCandidate)
                {
                    _logger.LogWarning(
                        "[SECURITY-ALERT] Blocked automatic Discord OAuth link for DiscordId '{DiscordId}' onto User #{UserId} (privileged={IsPrivileged}).",
                        payload.DiscordId, user.ID, isPrivilegedCandidate);
                    user = null;
                }
            }

            if (user != null)
            {
                user.DiscordId = payload.DiscordId;
                if (!string.IsNullOrWhiteSpace(payload.DiscordUsername)) user.DiscordUsername = payload.DiscordUsername;
                if (!string.IsNullOrWhiteSpace(payload.DiscordAvatar)) user.DiscordAvatar = payload.DiscordAvatar;
                await db.SaveChangesAsync(ct);
            }
        }

        if (user != null)
        {
            user.DiscordId = payload.DiscordId;
            if (!string.IsNullOrWhiteSpace(payload.DiscordUsername)) user.DiscordUsername = payload.DiscordUsername;
            if (!string.IsNullOrWhiteSpace(payload.DiscordAvatar)) user.DiscordAvatar = payload.DiscordAvatar;
            user.TimestampLastActive = TimeProvider.System.GetUtcNow();

            bool hasCustomIconInInv = await db.UserInventory.AnyAsync(
                i => i.UserID == user.ID && i.ItemCode == "ai.custom_icon:1", ct);
            if (!hasCustomIconInInv)
            {
                db.UserInventory.Add(new UserInventory
                {
                    UserID = user.ID,
                    AccountID = user.Accounts.FirstOrDefault()?.ID,
                    ItemCode = "ai.custom_icon:1",
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = TimeProvider.System.GetUtcNow(),
                    Source = "DiscordSync"
                });
            }

            foreach (Account acc in user.Accounts)
            {
                bool hasCustomIcon = acc.SelectedStoreItems.Any(i =>
                    string.Equals(i.ItemCode, "ai.custom_icon:1", StringComparison.OrdinalIgnoreCase));
                bool hasOtherAiItems = acc.SelectedStoreItems.Any(i =>
                    i.ItemCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(i.ItemCode, "ai.custom_icon:1", StringComparison.OrdinalIgnoreCase));

                if (!hasCustomIcon || hasOtherAiItems)
                {
                    acc.SelectedStoreItems.RemoveAll(i => i.ItemCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase));
                    acc.SelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = acc.ID, ItemCode = "ai.custom_icon:1" });
                }

                _logger.LogInformation(
                    "[AccountRepository] Synced Discord Avatar '{AvatarHash}' and 'ai.custom_icon:1' for Account #{AccountId} ('{AccountName}') under User #{UserId}",
                    payload.DiscordAvatar ?? "NULL", acc.ID, acc.Name, user.ID);
            }

            await db.SaveChangesAsync(ct);
            return user;
        }

        // Create new user for Discord OAuth
        Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct);
        if (userRole == null)
        {
            userRole = new Role { Name = UserRoles.User };
            db.Roles.Add(userRole);
            await db.SaveChangesAsync(ct);
        }

        string fallbackDiscordEmail = $"d_{payload.DiscordId}@discord.projectkongor.local";
        string email = !string.IsNullOrWhiteSpace(payload.Email) && !await db.Users.AnyAsync(u => u.EmailAddress == payload.Email, ct)
            ? payload.Email
            : fallbackDiscordEmail;
        user = new User
        {
            EmailAddress = email,
            SRPPasswordSalt = string.Empty,
            SRPPasswordHash = string.Empty,
            PBKDF2PasswordHash = string.Empty,
            DiscordId = payload.DiscordId,
            DiscordUsername = payload.DiscordUsername,
            DiscordAvatar = payload.DiscordAvatar,
            Role = userRole,
            GoldCoins = 0,
            SilverCoins = 0,
            PlinkoTickets = 5,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampLastActive = TimeProvider.System.GetUtcNow()
        };

        user.InventoryItems.Add(new UserInventory
        {
            User = user,
            ItemCode = "ai.custom_icon:1",
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "DiscordRegistration"
        });

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(payload.InGameName))
        {
            string cleanIgn = payload.InGameName.Trim();
            if (!await db.Accounts.AnyAsync(a => a.Name == cleanIgn, ct))
            {
                Account account = new()
                {
                    Name = cleanIgn,
                    User = user,
                    IsMain = true,
                    Type = AccountType.Normal,
                    SelectedStoreItems = ["ai.custom_icon:1"],
                    TimestampCreated = TimeProvider.System.GetUtcNow(),
                    TimestampLastActive = TimeProvider.System.GetUtcNow()
                };
                db.Accounts.Add(account);
                await db.SaveChangesAsync(ct);
            }
        }

        user = await db.Users
            .Include(u => u.Role)
            .Include(u => u.Accounts).ThenInclude(a => a.Clan)
            .Include(u => u.Accounts).ThenInclude(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(u => u.ID == user.ID, ct);

        return user!;
    }

    public async Task<bool> SetInGamePasswordAsync(int userId, string salt, string srpHash, string? pbkdf2Hash = null,
        string? argon2idHash = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Setting in-game password for User #{UserId} (Argon2id provided: {HasArgon2})",
            userId, !string.IsNullOrEmpty(argon2idHash));
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;

        user.SRPPasswordSalt = salt;
        user.SRPPasswordHash = srpHash;
        if (!string.IsNullOrEmpty(pbkdf2Hash)) user.PBKDF2PasswordHash = pbkdf2Hash;
        // If an explicit Argon2id hash was provided, store it; otherwise invalidate stale hash so web login falls back to new PBKDF2
        user.Argon2idPasswordHash = argon2idHash;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpgradeUserArgon2idHashAsync(int userId, string argon2idHash, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Shadow-upgrading User #{UserId} to Argon2id", userId);
        try
        {
            await db.Users
                .Where(u => u.ID == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Argon2idPasswordHash, argon2idHash), ct);
        }
        catch (InvalidOperationException)
        {
            // InMemory EF Core test provider fallback
            User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
            if (user != null)
            {
                user.Argon2idPasswordHash = argon2idHash;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task<bool> LinkTwitchAsync(int userId, string twitchId, string twitchUsername,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Linking Twitch for User #{UserId} ('{TwitchUsername}')", userId,
            twitchUsername);
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;

        user.TwitchId = twitchId;
        user.TwitchUsername = twitchUsername;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnlinkTwitchAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Unlinking Twitch for User #{UserId}", userId);
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;

        user.TwitchId = null;
        user.TwitchUsername = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<User> CreateUserAsync(User user, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Creating User '{Email}'", user.EmailAddress);
        await db.Users.AddAsync(user, ct);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private async Task<User?> UpdateDetachedUserProfileAsync(User user, CancellationToken ct)
    {
        User? existing = await db.Users.FirstOrDefaultAsync(u => u.ID == user.ID, ct);
        if (existing == null)
        {
            db.Users.Attach(user);
            return user;
        }

        existing.EmailAddress = user.EmailAddress;
        existing.DiscordId = user.DiscordId;
        existing.DiscordUsername = user.DiscordUsername;
        existing.DiscordAvatar = user.DiscordAvatar;
        existing.TwitchUsername = user.TwitchUsername;
        existing.TwitchId = user.TwitchId;
        existing.RoleID = user.RoleID;
        existing.SRPPasswordSalt = user.SRPPasswordSalt;
        existing.SRPPasswordHash = user.SRPPasswordHash;
        existing.PBKDF2PasswordHash = user.PBKDF2PasswordHash;
        existing.Argon2idPasswordHash = user.Argon2idPasswordHash;
        existing.InvitedByUserID = user.InvitedByUserID;
        existing.TimestampLastActive = user.TimestampLastActive;
        existing.LastPlinkoDailyClaimUtc = user.LastPlinkoDailyClaimUtc;
        existing.TotalLevel = user.TotalLevel;
        existing.TotalExperience = user.TotalExperience;
        return existing;
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Updating User #{UserId} ('{Email}')", user.ID, user.EmailAddress);
        EntityEntry<User>? userEntry = db.ChangeTracker.Entries<User>()
            .FirstOrDefault(e => e.Entity.ID == user.ID);

        if (userEntry != null)
        {
            if (!ReferenceEquals(userEntry.Entity, user))
            {
                userEntry.CurrentValues.SetValues(user);
            }
        }
        else
        {
            User? trackedUser = await UpdateDetachedUserProfileAsync(user, ct);
            if (trackedUser != null && user.InventoryItems != null)
            {
                foreach (UserInventory invItem in user.InventoryItems.Where(i => i.ID == 0))
                {
                    invItem.UserID = trackedUser.ID;
                    invItem.User = trackedUser;
                }
            }
        }

        if (user.InventoryItems != null)
        {
            foreach (UserInventory item in user.InventoryItems)
            {
                if (item.ID == 0 && !db.UserInventory.Local.Contains(item))
                {
                    db.UserInventory.Add(item);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching Role by Name '{RoleName}'", roleName);
        return await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == roleName, ct);
    }

    public async Task<IReadOnlyList<Account>> SearchAccountsAsync(string? query, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Searching accounts with query='{Query}', page={Page}, size={PageSize}",
            query, page, pageSize);
        IQueryable<Account> q = db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.Clan);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string trimmed = query.Trim();
            q = q.Where(a => a.Name.Contains(trimmed));
        }

        return await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Account>> GetAccountsByIdsAsync(IEnumerable<int> accountIds,
        CancellationToken ct = default)
    {
        List<int> ids = [.. accountIds];
        _logger.LogDebug("[AccountRepository] Fetching {Count} accounts by IDs", ids.Count);
        return await db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.User).ThenInclude(u => u.InventoryItems)
            .Include(a => a.Clan)
            .Include(a => a.SelectedStoreItems)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .Include(a => a.GuidePreferences)
            .Where(a => ids.Contains(a.ID))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Account>> GetAccountsForUserAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching accounts for user {UserId}", userId);
        return await db.Accounts
            .Where(a => a.UserID == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Account>> GetAccountsByNamesAsync(IEnumerable<string> names,
        CancellationToken ct = default)
    {
        List<string> nameList = [.. names];
        _logger.LogDebug("[AccountRepository] Fetching {Count} accounts by names", nameList.Count);
        return await db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.Clan)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .Where(a => nameList.Contains(a.Name))
            .ToListAsync(ct);
    }

    public async Task<int> GetTotalAccountsCountAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Counting total accounts");
        return await db.Accounts.CountAsync(ct);
    }

    public async Task<int> GetTotalUsersCountAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Counting total users");
        return await db.Users.CountAsync(ct);
    }

    public async Task<int> GetTotalStaffCountAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Counting total staff users");
        return await db.Users.CountAsync(u => u.Role.Name == "Administrator" || u.Role.Name == "Staff", ct);
    }

    public async Task<int> GetTotalHostsCountAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Counting total approved hosts");
        return await db.HostApplications.CountAsync(h => h.Status == HostApplicationStatus.Approved, ct);
    }

    public async Task<IReadOnlyList<User>> SearchUsersAsync(string? query, string? roleFilter, int limit = 100,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[AccountRepository] Searching users with query='{Query}', roleFilter='{RoleFilter}', limit={Limit}", query,
            roleFilter, limit);
        IQueryable<User> q = db.Users
            .AsNoTracking()
            .AsSplitQuery()
            .Include(u => u.Role)
            .Include(u => u.Accounts);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string trimmed = query.Trim();
            q = q.Where(u => u.EmailAddress.Contains(trimmed) || u.Accounts.Any(a => a.Name.Contains(trimmed)));
        }

        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            string trimmedRole = roleFilter.Trim();
            q = q.Where(u => u.Role.Name == trimmedRole);
        }

        return await q.Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HostApplication>> GetApprovedHostApplicationsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching approved host applications");
        return await db.HostApplications
            .AsNoTracking()
            .AsSplitQuery()
            .Include(h => h.MainAccount)
            .Include(h => h.HostAccount)
            .Include(h => h.User)
            .Where(h => h.Status == HostApplicationStatus.Approved)
            .OrderByDescending(h => h.TimestampApplied)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HostApplication>> GetAllHostApplicationsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching all host applications");
        return await db.HostApplications
            .AsNoTracking()
            .AsSplitQuery()
            .Include(h => h.MainAccount)
            .Include(h => h.HostAccount)
            .Include(h => h.User)
            .OrderByDescending(h => h.TimestampApplied)
            .ToListAsync(ct);
    }

    public async Task<HostApplication?> GetHostApplicationByIdOrAccountIdAsync(int id, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching host application by ID/Account ID #{Id}", id);
        return await db.HostApplications
            .AsNoTracking()
            .AsSplitQuery()
            .Include(h => h.MainAccount)
            .Include(h => h.HostAccount)
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.ID == id || h.MainAccountID == id || h.HostAccountID == id, ct);
    }

    public async Task<HostApplication?> CreateHostApplicationAsync(string serverNamePrefix, string location,
        int instances, string? applicationNotes, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AccountRepository] Creating Host Application with serverNamePrefix '{ServerNamePrefix}'",
            serverNamePrefix);
        Account? mainAccount =
            await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == serverNamePrefix, ct);
        if (mainAccount == null)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] Rejected HostApplication for unknown applicant '{ApplicantAccount}'.",
                serverNamePrefix);
            return null;
        }
        if (mainAccount == null) return null;

        HostApplication? pendingApp = await db.HostApplications
            .FirstOrDefaultAsync(h => h.MainAccountID == mainAccount.ID && h.Status == HostApplicationStatus.Pending,
                ct);
        if (pendingApp != null) return null;

        int existingHostCount = await db.Accounts
            .CountAsync(
                a => (a.OwnerAccountID == mainAccount.ID || a.User.ID == mainAccount.User.ID) &&
                     a.Type == AccountType.ServerHost, ct);

        string suffix = existingHostCount > 0 ? $"_{existingHostCount + 1}" : "";
        string hostUsername = $"{mainAccount.Name}_host{suffix}";

        HostApplication application = new()
        {
            UserID = mainAccount.User.ID,
            User = mainAccount.User,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostUsername = hostUsername,
            ServerNamePrefix = string.IsNullOrWhiteSpace(serverNamePrefix)
                ? $"{mainAccount.Name}'s Server"
                : serverNamePrefix.Trim(),
            Location = string.IsNullOrWhiteSpace(location) ? "USE" : location.Trim(),
            Instances = instances > 0 ? instances : 2,
            ApplicationNotes = applicationNotes,
            Status = HostApplicationStatus.Pending,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };

        db.HostApplications.Add(application);
        await db.SaveChangesAsync(ct);
        return application;
    }

    public async Task<HostApplication?> CreateHostApplicationForAccountAsync(string applicantAccountName,
        string? serverNamePrefix, string location, int instances, string? applicationNotes,
        CancellationToken ct = default)
    {
        HostApplication? app = await CreateHostApplicationAsync(applicantAccountName, location, instances, applicationNotes, ct);
        if (app != null && !string.IsNullOrWhiteSpace(serverNamePrefix) &&
            !string.Equals(app.ServerNamePrefix, serverNamePrefix.Trim(), StringComparison.Ordinal))
        {
            app.ServerNamePrefix = serverNamePrefix.Trim();
            await db.SaveChangesAsync(ct);
        }

        return app;
    }

    public async Task<StreamerApplication?> CreateOrUpdateStreamerApplicationAsync(int userId, string twitchUsername,
        string? twitchId, string? twitchChannelUrl, string? applicationNotes, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[AccountRepository] CreateOrUpdateStreamerApplication for User #{UserId}, Twitch '{TwitchUsername}'",
            userId, twitchUsername);
        User? user = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return null;

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        if (mainAccount == null) return null;

        string twitchUser = !string.IsNullOrEmpty(twitchUsername)
            ? twitchUsername
            : !string.IsNullOrEmpty(twitchChannelUrl)
                ? twitchChannelUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "Unknown"
                : "Unknown";

        StreamerApplication? existing = await db.StreamerApplications.FirstOrDefaultAsync(s => s.UserID == user.ID, ct);
        if (existing != null && existing.Status == StreamerApplicationStatus.Pending) return null;

        if (existing != null)
        {
            existing.TwitchUsername = twitchUser;
            existing.TwitchId = twitchId;
            existing.TwitchChannelUrl = twitchChannelUrl;
            existing.ApplicationNotes = applicationNotes;
            existing.Status = StreamerApplicationStatus.Pending;
            existing.TimestampApplied = TimeProvider.System.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return existing;
        }

        StreamerApplication application = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            TwitchUsername = twitchUser,
            TwitchId = twitchId,
            TwitchChannelUrl = twitchChannelUrl,
            ApplicationNotes = applicationNotes,
            Status = StreamerApplicationStatus.Pending,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };

        db.StreamerApplications.Add(application);
        await db.SaveChangesAsync(ct);
        return application;
    }

    public async Task<StreamerApplication?> GetStreamerApplicationByUserIdAsync(int userId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching streamer application for User #{UserId}", userId);
        return await db.StreamerApplications
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.UserID == userId, ct);
    }

    public async Task<HostApplication?> GetHostApplicationByUserIdAsync(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Fetching host application for User #{UserId}", userId);
        return await db.HostApplications
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.UserID == userId, ct);
    }

    public async Task<HostTelemetryResultDto?> GetHostTelemetryAsync(string accountName, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Calculating host telemetry for Account '{AccountName}'", accountName);
        Account? account = await db.Accounts
            .Include(a => a.User)
            .Include(a => a.Statistics)
            .FirstOrDefaultAsync(a => a.Name == accountName, ct);

        if (account == null) return null;

        int? directUserId = account.User?.ID;
        List<HostApplication> relatedHostApps = await db.HostApplications
            .Include(h => h.HostAccount)
            .Include(h => h.MainAccount)
            .Include(h => h.User)
            .AsNoTracking()
            .Where(h => (directUserId.HasValue && h.UserID == directUserId.Value) ||
                        h.MainAccountID == account.ID ||
                        (h.HostAccountID.HasValue && h.HostAccountID.Value == account.ID) ||
                        h.HostUsername == account.Name)
            .ToListAsync(ct);

        int? rootUserId = directUserId ?? relatedHostApps.FirstOrDefault(h => h.User != null)?.UserID;

        List<Account> allUserAccounts = [];
        if (rootUserId.HasValue)
            allUserAccounts = await db.Accounts
                .Include(a => a.Statistics)
                .AsNoTracking()
                .Where(a => a.User.ID == rootUserId.Value)
                .ToListAsync(ct);

        if (allUserAccounts.All(a => a.ID != account.ID)) allUserAccounts.Add(account);

        List<int> allUserAccountIds = [.. allUserAccounts.Select(a => a.ID).Distinct()];

        List<HostApplication> allHostApps = await db.HostApplications
            .Include(h => h.HostAccount)
            .Include(h => h.MainAccount)
            .AsNoTracking()
            .Where(h => (rootUserId.HasValue && h.UserID == rootUserId.Value) ||
                        allUserAccountIds.Contains(h.MainAccountID) ||
                        (h.HostAccountID.HasValue && allUserAccountIds.Contains(h.HostAccountID.Value)))
            .ToListAsync(ct);

        List<HostApplication> mergedHostApps =
            [.. relatedHostApps.Concat(allHostApps).GroupBy(h => h.ID).Select(g => g.First())];

        List<string> linkedHostNames =
            [.. allUserAccounts.Where(a => a.Type == AccountType.ServerHost).Select(a => a.Name)];
        List<string> ownedHostNames = await db.Accounts
            .Where(a => a.OwnerAccountID == account.ID)
            .Select(a => a.Name)
            .ToListAsync(ct);
        linkedHostNames.AddRange(ownedHostNames);

        foreach (HostApplication app in mergedHostApps.Where(h => h.Status == HostApplicationStatus.Approved))
        {
            if (!string.IsNullOrWhiteSpace(app.HostUsername)) linkedHostNames.Add(app.HostUsername);
            if (app.HostAccount != null && !string.IsNullOrWhiteSpace(app.HostAccount.Name))
                linkedHostNames.Add(app.HostAccount.Name);
        }

        if (account.Type == AccountType.ServerHost) linkedHostNames.Add(account.Name);
        linkedHostNames = [.. linkedHostNames.Distinct(StringComparer.OrdinalIgnoreCase)];

        List<Account> playerAccounts = [.. allUserAccounts.Where(a => a.Type != AccountType.ServerHost)];
        List<int> linkedPlayerAccountIds = [.. playerAccounts.Select(a => a.ID).Distinct()];
        List<string> linkedPlayerAccountNames = [.. playerAccounts.Select(a => a.Name).Distinct()];

        if (account.OwnerAccountID.HasValue && !linkedPlayerAccountIds.Contains(account.OwnerAccountID.Value))
            linkedPlayerAccountIds.Add(account.OwnerAccountID.Value);

        if (linkedPlayerAccountIds.Count == 0 && account.Type != AccountType.ServerHost)
        {
            linkedPlayerAccountIds.Add(account.ID);
            linkedPlayerAccountNames.Add(account.Name);
        }

        bool hasLinkedHostAccounts = linkedHostNames.Count > 0 ||
                                     mergedHostApps.Any(h => h.Status == HostApplicationStatus.Approved);
        bool hasPendingHostApp = mergedHostApps.Any(h => h.Status == HostApplicationStatus.Pending);
        HostApplication? primaryHostApp =
            mergedHostApps.FirstOrDefault(h => h.Status == HostApplicationStatus.Approved) ??
            mergedHostApps.FirstOrDefault();
        string primaryHostUsername = primaryHostApp?.HostUsername ?? (account.Type == AccountType.ServerHost
            ? account.Name
            : linkedHostNames.FirstOrDefault() ?? $"{account.Name}_host");

        int gamesHosted = 0;
        if (linkedHostNames.Count > 0)
            gamesHosted = await db.MatchStatistics.AsNoTracking()
                .Where(m => linkedHostNames.Contains(m.HostAccountName)).CountAsync(ct);

        int totalPlayed = 0;
        if (linkedPlayerAccountIds.Count > 0)
        {
            totalPlayed = await db.PlayerStatistics.AsNoTracking()
                .Where(ps => linkedPlayerAccountIds.Contains(ps.AccountID)).CountAsync(ct);
            if (totalPlayed == 0)
                totalPlayed = playerAccounts.Sum(a =>
                    a.Statistics
                        .FirstOrDefault(s =>
                            s.Type is AccountStatisticsType.Matchmaking or AccountStatisticsType.Player)
                        ?.MatchesPlayed ?? 0);
        }

        double hostingRatio = gamesHosted > 0 ? Math.Round((double)gamesHosted / Math.Max(1, totalPlayed), 2) : 0.0;
        double communityContribution = 0.0;
        if (gamesHosted > 0)
        {
            int totalCommunityMatches = await db.MatchStatistics.AsNoTracking().CountAsync(ct);
            if (totalCommunityMatches > 0)
                communityContribution = Math.Round((double)gamesHosted / totalCommunityMatches * 100.0, 1);
        }

        string? hostBadgeTier = null;
        if (hasLinkedHostAccounts || account.Type == AccountType.ServerHost || gamesHosted > 0)
            hostBadgeTier = gamesHosted switch
            {
                >= 250 => "Patron of Newerth",
                _ when hostingRatio >= 2.0 && gamesHosted >= 50 => "Patron of Newerth",
                >= 100 => "Master Host",
                _ when hostingRatio >= 1.0 && gamesHosted >= 25 => "Master Host",
                >= 25 => "Dedicated Host",
                _ when hostingRatio >= 0.5 && gamesHosted >= 10 => "Dedicated Host",
                _ => "Community Host"
            };

        return new HostTelemetryResultDto(
            hasLinkedHostAccounts,
            hasPendingHostApp,
            linkedHostNames,
            linkedPlayerAccountIds,
            linkedPlayerAccountNames,
            gamesHosted,
            totalPlayed,
            hostingRatio,
            communityContribution,
            hostBadgeTier,
            primaryHostApp != null ? DtoMappingExtensions.ToDto(primaryHostApp) : null,
            primaryHostUsername
        );
    }

    public async Task<Account?> GetAuthIdentityByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        string trimmed = nickname.Trim();
        _logger.LogDebug("[AccountRepository] Loading lightweight AuthIdentity for '{Nickname}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts
                .Include(a => a.User)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
        }

        return await db.Accounts
            .Include(a => a.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == trimmed, ct);
    }

    public async Task<Account?> GetForPreAuthAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        string trimmed = accountName.Trim();
        _logger.LogDebug("[AccountRepository] Loading Account for PreAuth: '{AccountName}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts
                .Include(a => a.User)
                .Include(a => a.Clan)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
        }

        return await db.Accounts
            .Include(a => a.User)
            .Include(a => a.Clan)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == trimmed, ct);
    }

    public async Task<Account?> GetForAuthenticationAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        string trimmed = accountName.Trim();
        _logger.LogDebug("[AccountRepository] Loading Account graph for Authentication: '{AccountName}'", trimmed);

        IQueryable<Account> query = db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User).ThenInclude(u => u.InventoryItems)
            .Include(a => a.Clan)
            .Include(a => a.Peers).ThenInclude(p => p.PeerAccount)
            .Include(a => a.Friendships).ThenInclude(f => f.FriendAccount).ThenInclude(fa => fa!.Clan)
            .Include(a => a.Statistics);

        Account? account;
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            account = await query.FirstOrDefaultAsync(a => a.Name.ToLower() == lower, ct);
        }
        else
        {
            account = await query.FirstOrDefaultAsync(a => a.Name == trimmed, ct);
        }

        if (account is null)
        {
            return null;
        }

        if (account.User is { } user)
        {
            user.Accounts = await db.Accounts
                .AsNoTracking()
                .Where(a => a.User.ID == user.ID)
                .OrderBy(a => a.TimestampCreated)
                .ToListAsync(ct);
        }

        if (account.Clan is { } clan)
        {
            clan.Members = await db.Accounts
                .AsNoTracking()
                .Where(a => a.Clan != null && a.Clan.ID == clan.ID)
                .ToListAsync(ct);
        }

        return account;
    }

    public async Task<Account?> GetForSwitchAuthAsync(int targetAccountId, string? targetAccountName, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Loading target Account for SwitchAuth: Id={TargetId}, Name='{TargetName}'",
            targetAccountId, targetAccountName ?? "NULL");

        string? trimmed = targetAccountName?.Trim();

        IQueryable<Account> query = db.Accounts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.User)
            .Include(a => a.Clan)
            .Include(a => a.Peers).ThenInclude(p => p.PeerAccount)
            .Include(a => a.Friendships).ThenInclude(f => f.FriendAccount).ThenInclude(fa => fa!.Clan)
            .Include(a => a.Statistics);

        Account? targetAccount;
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string? lower = trimmed?.ToLowerInvariant();
            targetAccount = await query.FirstOrDefaultAsync(a => (targetAccountId > 0 && a.ID == targetAccountId) ||
                                                                (!string.IsNullOrEmpty(lower) && a.Name.ToLower() == lower), ct);
        }
        else
        {
            targetAccount = await query.FirstOrDefaultAsync(a => (targetAccountId > 0 && a.ID == targetAccountId) ||
                                                                (!string.IsNullOrEmpty(trimmed) && a.Name == trimmed), ct);
        }

        if (targetAccount is null)
        {
            return null;
        }

        if (targetAccount.User is { } targetUser)
        {
            targetUser.Accounts = await db.Accounts
                .AsNoTracking()
                .Where(a => a.User.ID == targetUser.ID)
                .OrderBy(a => a.TimestampCreated)
                .ToListAsync(ct);
        }

        if (targetAccount.Clan is { } targetClan)
        {
            targetClan.Members = await db.Accounts
                .AsNoTracking()
                .Where(a => a.Clan != null && a.Clan.ID == targetClan.ID)
                .ToListAsync(ct);
        }

        return targetAccount;
    }

    public async Task<int?> GetAccountIdByNicknameAsync(string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        string trimmed = nickname.Trim();
        _logger.LogDebug("[AccountRepository] Resolving Account ID for Nickname '{Nickname}'", trimmed);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            string lower = trimmed.ToLowerInvariant();
            return await db.Accounts
                .AsNoTracking()
                .Where(a => a.Name.ToLower() == lower)
                .Select(a => (int?)a.ID)
                .FirstOrDefaultAsync(ct);
        }

        return await db.Accounts
            .AsNoTracking()
            .Where(a => a.Name == trimmed)
            .Select(a => (int?)a.ID)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RecordAccountTelemetryAndSessionAsync(
        int accountId,
        AccountSession session,
        string? ipAddress,
        string? macAddress,
        string? sysInfo,
        string? sysInfoHash,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Recording session metadata for Account #{AccountId}", accountId);
        try
        {
            await db.AccountSessions.AddAsync(session, ct);

            Account? trackedAccount = await db.Accounts
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .FirstOrDefaultAsync(a => a.ID == accountId, ct);
            if (trackedAccount is not null)
            {
                trackedAccount.TimestampLastActive = TimeProvider.System.GetUtcNow();
                if (!string.IsNullOrEmpty(ipAddress))
                {
                    trackedAccount.AddIPAddress(ipAddress);
                }

                if (!string.IsNullOrEmpty(macAddress))
                {
                    trackedAccount.AddMACAddress(macAddress);
                }

                if (!string.IsNullOrEmpty(sysInfo) && !trackedAccount.SystemInformationCollection.Contains(sysInfo))
                {
                    trackedAccount.SystemInformationCollection.Add(sysInfo);
                }

                if (!string.IsNullOrEmpty(sysInfoHash) && !trackedAccount.SystemInformationHashCollection.Contains(sysInfoHash))
                {
                    trackedAccount.SystemInformationHashCollection.Add(sysInfoHash);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AccountRepository] Non-fatal notice: Failed to record session metadata in database for Account #{AccountId}", accountId);
        }
    }

    public async Task UpdateLastActiveAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogDebug("[AccountRepository] Updating LastActive timestamp for Account #{AccountId}", accountId);
        try
        {
            await db.Accounts
                .Where(a => a.ID == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TimestampLastActive, TimeProvider.System.GetUtcNow()), ct);
        }
        catch (InvalidOperationException)
        {
            Account? tracked = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
            if (tracked is not null)
            {
                tracked.TimestampLastActive = TimeProvider.System.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task<Account?> CreateSubAccountAsync(int mainAccountId, string subAccountNickname, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Creating subaccount '{SubNick}' for Main Account #{MainAccountId}",
            subAccountNickname, mainAccountId);

        Account? mainAccount = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == mainAccountId, ct);

        if (mainAccount is null || mainAccount.User is null)
        {
            return null;
        }

        bool isTaken = await ExistsByNicknameAsync(subAccountNickname, ct);
        if (isTaken)
        {
            return null;
        }

        Account subAccount = new()
        {
            Name = subAccountNickname,
            User = mainAccount.User,
            IsMain = false,
            Type = mainAccount.Type,
            BanExpiration = mainAccount.BanExpiration,
            BanReason = mainAccount.BanReason,
            SuspensionExpiration = mainAccount.SuspensionExpiration,
            SuspensionReason = mainAccount.SuspensionReason,
            MuteExpiration = mainAccount.MuteExpiration,
            MuteReason = mainAccount.MuteReason
        };

        await db.Accounts.AddAsync(subAccount, ct);

        // Consume subaccount item if present in user inventory
        string[] subAccountCodes = ["m.Sub-Account", "Sub-Account", "ac.subaccount", "other.162", "162"];
        UserInventory? subAccountInv = await db.UserInventory
            .FirstOrDefaultAsync(i => (i.UserID == mainAccount.User.ID || i.AccountID == mainAccount.ID) &&
                                      subAccountCodes.Contains(i.ItemCode), ct);
        if (subAccountInv != null)
        {
            if (subAccountInv.Quantity > 1)
                subAccountInv.Quantity -= 1;
            else
                db.UserInventory.Remove(subAccountInv);
        }

        await db.SaveChangesAsync(ct);
        return subAccount;
    }

    public async Task<bool> ChangeNicknameAsync(int accountId, string newNickname, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Changing nickname for Account #{AccountId} to '{NewNick}'",
            accountId, newNickname);

        Account? account = await db.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == accountId, ct);

        if (account is null)
        {
            return false;
        }

        bool isTaken = await ExistsByNicknameAsync(newNickname, ct);
        if (isTaken)
        {
            return false;
        }

        account.Name = newNickname;

        if (account.User is { } user)
        {
            // Consume name change item if present in user inventory so it is repeatable/re-purchasable
            string[] nameChangeCodes = ["m.Nickname Change", "Nickname Change", "ac.namechange", "other.161", "161"];
            UserInventory? nameChangeInv = await db.UserInventory
                .FirstOrDefaultAsync(i => (i.UserID == user.ID || i.AccountID == account.ID) &&
                                          nameChangeCodes.Contains(i.ItemCode), ct);
            if (nameChangeInv != null)
            {
                if (nameChangeInv.Quantity > 1)
                    nameChangeInv.Quantity -= 1;
                else
                    db.UserInventory.Remove(nameChangeInv);
            }
        }

        db.Accounts.Update(account);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateCloudStoragePreferencesAsync(int accountId, bool? useCloud, bool? autoUpload, DateTimeOffset? backupTime = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Updating Cloud Storage preferences for Account #{AccountId}: UseCloud={UseCloud}, AutoUpload={AutoUpload}, BackupTime={BackupTime}",
            accountId, useCloud, autoUpload, backupTime);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
        if (account is null) return false;

        if (useCloud.HasValue) account.UseCloud = useCloud.Value;
        if (autoUpload.HasValue) account.AutomaticCloudUpload = autoUpload.Value;
        if (backupTime.HasValue) account.BackupLastUpdatedTime = backupTime.Value;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateCloudStoragePreferencesByNameAsync(string accountName, bool? useCloud, bool? autoUpload, DateTimeOffset? backupTime = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return false;

        _logger.LogInformation("[AccountRepository] Updating Cloud Storage preferences for Account '{AccountName}': UseCloud={UseCloud}, AutoUpload={AutoUpload}, BackupTime={BackupTime}",
            accountName, useCloud, autoUpload, backupTime);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Name == accountName, ct);
        if (account is null) return false;

        if (useCloud.HasValue) account.UseCloud = useCloud.Value;
        if (autoUpload.HasValue) account.AutomaticCloudUpload = autoUpload.Value;
        if (backupTime.HasValue) account.BackupLastUpdatedTime = backupTime.Value;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SavePlayerReportsAsync(IEnumerable<PlayerReport> reports, CancellationToken ct = default)
    {
        List<PlayerReport> reportList = [.. reports];
        if (reportList.Count == 0) return;

        _logger.LogInformation("[AccountRepository] Batch saving {Count} PlayerReports for Match #{MatchId}",
            reportList.Count, reportList[0].MatchID);

        await db.PlayerReports.AddRangeAsync(reportList, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SavePlayerReportAsync(PlayerReport report, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Saving PlayerReport from #{ReporterId} targeting #{OffenderId} for Match #{MatchId}",
            report.ReporterAccountID, report.ReportedAccountID, report.MatchID);

        await db.PlayerReports.AddAsync(report, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetApprovedStreamerTwitchUsernamesAsync(int limit = 4, CancellationToken ct = default)
    {
        return await db.StreamerApplications
            .AsNoTracking()
            .Where(s => s.Status == StreamerApplicationStatus.Approved)
            .OrderByDescending(s => s.TimestampReviewed)
            .Take(limit)
            .Select(s => s.TwitchUsername)
            .ToListAsync(ct);
    }

    public async Task DissociateFriendshipsAsync(int accountId, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] Dissociating friendships for Account #{AccountId}", accountId);
        await db.Friendships
            .Where(f => f.AccountID == accountId || f.FriendAccountID == accountId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<AccountPeerResult> AddPeerAsync(int accountId, int peerAccountId, PeerType type, string reason = "", DateTimeOffset? expires = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] AddPeer: Account #{AccountId}, Peer #{PeerAccountId}, Type={Type}", accountId, peerAccountId, type);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset effectiveExpiration = expires ?? now.AddMonths(6);

        AccountPeer? existing = await db.AccountPeers
            .FirstOrDefaultAsync(p => p.AccountID == accountId && p.PeerAccountID == peerAccountId && p.Type == type, ct);

        if (existing != null)
        {
            existing.Reason = reason;
            existing.TimestampExpires = effectiveExpiration;
            await db.SaveChangesAsync(ct);
            return AccountPeerResult.Succeeded(existing);
        }

        if (type == PeerType.Banned)
        {
            int activeBannedCount = await db.AccountPeers
                .CountAsync(p => p.AccountID == accountId && p.Type == PeerType.Banned && (p.TimestampExpires == null || p.TimestampExpires > now), ct);

            if (activeBannedCount >= Account.MaxActiveBannedPeers)
            {
                return AccountPeerResult.Failed($"You have reached the maximum limit of {Account.MaxActiveBannedPeers} banned accounts. Remove an existing entry to add more.");
            }
        }

        AccountPeer peer = new()
        {
            AccountID = accountId,
            PeerAccountID = peerAccountId,
            Type = type,
            Reason = reason,
            TimestampCreated = now,
            TimestampExpires = effectiveExpiration
        };

        db.AccountPeers.Add(peer);
        await db.SaveChangesAsync(ct);
        return AccountPeerResult.Succeeded(peer);
    }

    public async Task<bool> RemovePeerAsync(int accountId, int peerAccountId, PeerType type, CancellationToken ct = default)
    {
        _logger.LogInformation("[AccountRepository] RemovePeer: Account #{AccountId}, Peer #{PeerAccountId}, Type={Type}", accountId, peerAccountId, type);
        AccountPeer? existing = await db.AccountPeers
            .FirstOrDefaultAsync(p => p.AccountID == accountId && p.PeerAccountID == peerAccountId && p.Type == type, ct);

        if (existing == null) return false;

        db.AccountPeers.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AccountPeer>> GetPeersAsync(int accountId, PeerType? type = null, CancellationToken ct = default)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        IQueryable<AccountPeer> query = db.AccountPeers
            .AsNoTracking()
            .Where(p => p.AccountID == accountId && (p.TimestampExpires == null || p.TimestampExpires > now));

        if (type.HasValue)
        {
            query = query.Where(p => p.Type == type.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<int> RemoveAllNotificationsAsync(int accountId, CancellationToken ct = default)
    {
        if (accountId <= 0) return 0;

        _logger.LogInformation("[AccountRepository] Removing all notifications for Account #{AccountId}", accountId);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            List<Entities.Notification> rows = await db.Notifications
                .Where(n => n.AccountId == accountId)
                .ToListAsync(ct);
            if (rows.Count == 0) return 0;

            db.Notifications.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }

        try
        {
            return await db.Notifications
                .Where(n => n.AccountId == accountId)
                .ExecuteDeleteAsync(ct);
        }
        catch (InvalidOperationException)
        {
            List<Entities.Notification> rows = await db.Notifications
                .Where(n => n.AccountId == accountId)
                .ToListAsync(ct);
            if (rows.Count == 0) return 0;

            db.Notifications.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    public async Task<bool> DeleteNotificationAsync(int accountId, int notificationId, CancellationToken ct = default)
    {
        if (accountId <= 0 || notificationId <= 0) return false;

        _logger.LogInformation(
            "[AccountRepository] Deleting notification #{NotificationId} for Account #{AccountId}",
            notificationId, accountId);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            Entities.Notification? existing = await db.Notifications
                .FirstOrDefaultAsync(n => n.AccountId == accountId && n.NotificationId == notificationId, ct);
            if (existing == null) return false;

            db.Notifications.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        }

        try
        {
            int deleted = await db.Notifications
                .Where(n => n.AccountId == accountId && n.NotificationId == notificationId)
                .ExecuteDeleteAsync(ct);
            return deleted > 0;
        }
        catch (InvalidOperationException)
        {
            Entities.Notification? existing = await db.Notifications
                .FirstOrDefaultAsync(n => n.AccountId == accountId && n.NotificationId == notificationId, ct);
            if (existing == null) return false;

            db.Notifications.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }

    public async Task<int> CreateNotificationAsync(int accountId, string content, CancellationToken ct = default)
    {
        if (accountId <= 0) return 0;

        string safeContent = string.IsNullOrEmpty(content)
            ? string.Empty
            : (content.Length > 2048 ? content[..2048] : content);

        Entities.Notification notification = new()
        {
            AccountId = accountId,
            Content = safeContent,
            TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[AccountRepository] Created notification #{NotificationId} for Account #{AccountId} (len={ContentLen})",
            notification.NotificationId, accountId, safeContent.Length);

        return notification.NotificationId;
    }

    public async Task<Account?> FindBaseServerHostByPrefixAsync(
        string baseNamePrefix,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseNamePrefix))
        {
            return null;
        }

        string prefix = baseNamePrefix.Trim();
        List<Account> matches = await db.Accounts
            .Include(a => a.User)
            .Where(a => a.Type == AccountType.ServerHost &&
                        a.Name.StartsWith(prefix) &&
                        !a.Name.EndsWith("_arc") &&
                        !a.Name.EndsWith("_local") &&
                        !a.Name.EndsWith("_dock") &&
                        !a.Name.EndsWith("_k3s"))
            .Take(2)
            .ToListAsync(ct);

        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<Account?> ProvisionDerivedArcadeHostAccountAsync(
        string arcadeAccountName,
        Account verifiedBaseHost,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(arcadeAccountName) || verifiedBaseHost.User == null)
        {
            return null;
        }

        string trimmedArcadeName = arcadeAccountName.Trim();
        if (trimmedArcadeName.Length > 15)
        {
            _logger.LogWarning(
                "[AccountRepository] Refused to provision Queue Arcade account '{ArcadeAccount}' because length {Length} exceeds HoN/PostgreSQL VARCHAR(15) limit.",
                trimmedArcadeName,
                trimmedArcadeName.Length);
            return null;
        }

        Account? existing = await GetForAuthenticationAsync(trimmedArcadeName, ct);
        if (existing != null)
        {
            return existing;
        }

        Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct);
        if (userRole == null)
        {
            return null;
        }

        string arcadeEmail = $"host_{trimmedArcadeName.ToLowerInvariant()}@nexus.local";
        User? arcadeUser = await db.Users.FirstOrDefaultAsync(u => u.EmailAddress == arcadeEmail, ct);
        if (arcadeUser == null)
        {
            arcadeUser = new User
            {
                EmailAddress = arcadeEmail,
                Role = userRole,
                SRPPasswordSalt = verifiedBaseHost.User.SRPPasswordSalt,
                SRPPasswordHash = verifiedBaseHost.User.SRPPasswordHash,
                Argon2idPasswordHash = verifiedBaseHost.User.Argon2idPasswordHash
            };
            arcadeUser.PBKDF2PasswordHash = !string.IsNullOrEmpty(verifiedBaseHost.User.PBKDF2PasswordHash)
                ? verifiedBaseHost.User.PBKDF2PasswordHash
                : new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(
                    arcadeUser,
                    Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await db.Users.AddAsync(arcadeUser, ct);
        }

        Account arcadeAccount = new()
        {
            Name = trimmedArcadeName,
            User = arcadeUser,
            OwnerAccountID = verifiedBaseHost.OwnerAccountID,
            Type = AccountType.ServerHost,
            IsMain = false,
            Clan = null,
            ClanTier = ClanTier.None,
            TimestampJoinedClan = null,
            AscensionLevel = 666,
            AutoConnectChatChannels = [ChatChannels.ServerHostsChannel]
        };

        await db.Accounts.AddAsync(arcadeAccount, ct);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[AccountRepository] Auto-provisioned Queue Arcade ServerHost account '{ArcadeAccount}' from verified base host '{BaseHost}'",
            trimmedArcadeName,
            verifiedBaseHost.Name);

        return await GetForAuthenticationAsync(trimmedArcadeName, ct) ?? arcadeAccount;
    }
}
