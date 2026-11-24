// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace MERRICK.DatabaseContext.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IGuideRepository"/> using <see cref="MerrickContext"/>.
/// </summary>
public class HeroGuideRepository(MerrickContext db, ILogger<HeroGuideRepository>? logger = null) : IGuideRepository
{
    private readonly MerrickContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<HeroGuideRepository> _logger = logger ?? NullLogger<HeroGuideRepository>.Instance;

    public async Task<HeroGuide?> GetByIdAsync(int guideId, CancellationToken ct = default)
    {
        _logger.LogDebug("[HeroGuideRepository] Fetching guide by ID #{GuideId}", guideId);
        return await _db.HeroGuides
            .Include(g => g.Author).ThenInclude(a => a.Clan)
            .Include(g => g.Author).ThenInclude(a => a.User).ThenInclude(u => u.Role)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .FirstOrDefaultAsync(g => g.ID == guideId, ct);
    }

    public async Task<IReadOnlyList<HeroGuide>> GetGuidesForHeroAsync(string heroIdentifier, int? accountId = null, string? accountName = null, CancellationToken ct = default)
    {
        string targetHero = string.IsNullOrWhiteSpace(heroIdentifier) ? "Hero_Valkyrie" : heroIdentifier.Trim();
        string targetHeroLower = targetHero.ToLower();
        _logger.LogDebug("[HeroGuideRepository] Fetching guides for Hero '{HeroIdentifier}', AccountId: {AccountId}, AccountName: '{AccountName}'", targetHero, accountId, accountName);

        IQueryable<HeroGuide> query = _db.HeroGuides
            .AsNoTracking()
            .Include(g => g.Author).ThenInclude(a => a.Clan)
            .Include(g => g.Author).ThenInclude(a => a.User)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .Where(g => g.HeroIdentifier.ToLower() == targetHeroLower);

        query = query.Where(g =>
            g.Featured ||
            g.Public ||
            (accountId.HasValue && g.Author != null && g.Author.ID == accountId.Value) ||
            (!string.IsNullOrEmpty(accountName) && g.Author != null && g.Author.Name == accountName));

        List<HeroGuide> results = await query
            .OrderByDescending(g => g.Featured)
            .ThenByDescending(g => g.Rating)
            .ThenByDescending(g => g.UpVotes)
            .ToListAsync(ct);

        _logger.LogDebug("[HeroGuideRepository] Found {Count} guides for Hero '{HeroIdentifier}'", results.Count, targetHero);
        return results;
    }

    public async Task<HeroGuide?> GetActiveGuideForHeroAsync(string heroIdentifier, int? accountId, string? accountName, IEnumerable<int>? defaultGuideIds, CancellationToken ct = default)
    {
        string targetHero = string.IsNullOrWhiteSpace(heroIdentifier) ? "Hero_Valkyrie" : heroIdentifier.Trim();
        string targetHeroLower = targetHero.ToLower();
        _logger.LogDebug("[HeroGuideRepository] Resolving active guide for Hero '{HeroIdentifier}', AccountId: {AccountId}, AccountName: '{AccountName}'", targetHero, accountId, accountName);

        List<int> defaultIdsList = defaultGuideIds?.ToList() ?? [];

        // 1. Prioritize designated user default guide for this hero
        if (defaultIdsList.Count > 0)
        {
            HeroGuide? defaultGuide = await _db.HeroGuides
                .AsNoTracking()
                .Include(g => g.Author).ThenInclude(a => a.Clan)
                .Include(g => g.Author).ThenInclude(a => a.User)
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .Where(g => g.HeroIdentifier.ToLower() == targetHeroLower && defaultIdsList.Contains(g.ID))
                .FirstOrDefaultAsync(ct);

            if (defaultGuide != null)
            {
                _logger.LogDebug("[HeroGuideRepository] Resolved active guide #{GuideId} from user default guides", defaultGuide.ID);
                return defaultGuide;
            }
        }

        // 2. Prioritize user's own authored guide for this hero
        if (accountId.HasValue || !string.IsNullOrEmpty(accountName))
        {
            HeroGuide? authoredGuide = await _db.HeroGuides
                .AsNoTracking()
                .Include(g => g.Author).ThenInclude(a => a.Clan)
                .Include(g => g.Author).ThenInclude(a => a.User)
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .Where(g => g.HeroIdentifier.ToLower() == targetHeroLower &&
                            ((accountId.HasValue && g.Author != null && g.Author.ID == accountId.Value) ||
                             (!string.IsNullOrEmpty(accountName) && g.Author != null && g.Author.Name == accountName)))
                .OrderByDescending(g => g.TimestampLastUpdated ?? g.TimestampCreated)
                .FirstOrDefaultAsync(ct);

            if (authoredGuide != null)
            {
                _logger.LogDebug("[HeroGuideRepository] Resolved active guide #{GuideId} from user authored guides", authoredGuide.ID);
                return authoredGuide;
            }
        }

        // 3. Fallback to Featured / Highest Rating public guide
        HeroGuide? featuredOrPublicGuide = await _db.HeroGuides
                .AsNoTracking()
                .Include(g => g.Author).ThenInclude(a => a.Clan)
                .Include(g => g.Author).ThenInclude(a => a.User)
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .Where(g => g.HeroIdentifier.ToLower() == targetHeroLower && (g.Featured || g.Public))
                .OrderByDescending(g => g.Featured)
                .ThenByDescending(g => g.Rating)
                .ThenByDescending(g => g.UpVotes)
                .FirstOrDefaultAsync(ct);

        _logger.LogDebug("[HeroGuideRepository] Resolved active guide #{GuideId} from featured/public candidates", featuredOrPublicGuide?.ID ?? 0);
        return featuredOrPublicGuide;
    }

    public async Task<int> SetDefaultGuideAsync(int accountId, int guideId, bool isSet, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] SetDefaultGuide: Account #{AccountId}, Guide #{GuideId}, IsSet={IsSet}", accountId, guideId, isSet);

        Account? account = await _db.Accounts.Include(a => a.GuidePreferences).FirstOrDefaultAsync(a => a.ID == accountId, ct);
        if (account is null)
        {
            _logger.LogWarning("[HeroGuideRepository] SetDefaultGuideAsync failed: Account #{AccountId} not found", accountId);
            return -1;
        }

        if (isSet && guideId > 0)
        {
            HeroGuide? targetGuide = await _db.HeroGuides.AsNoTracking().FirstOrDefaultAsync(g => g.ID == guideId, ct);
            if (targetGuide is not null)
            {
                string targetHeroLower = targetGuide.HeroIdentifier.ToLower();
                List<int> sameHeroGuideIds = await _db.HeroGuides
                    .AsNoTracking()
                    .Where(g => g.HeroIdentifier.ToLower() == targetHeroLower)
                    .Select(g => g.ID)
                    .ToListAsync(ct);

                HashSet<int> sameHeroGuideIdSet = [.. sameHeroGuideIds];
                account.DefaultGuideIDs.RemoveAll(sameHeroGuideIdSet.Contains);
                account.DefaultGuideIDs.Add(guideId);
            }
            else
            {
                account.DefaultGuideIDs.Remove(guideId);
                account.DefaultGuideIDs.Add(guideId);
            }
        }
        else
        {
            account.DefaultGuideIDs.Remove(guideId);
        }

        account.SyncDefaultGuidesFromList(account.DefaultGuideIDs);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[HeroGuideRepository] Default guides updated for Account #{AccountId}. Current DefaultGuideIDs: [{Guides}]", accountId, string.Join(",", account.DefaultGuideIDs));
        return isSet ? guideId : -1;
    }

    public async Task<int> SetFavoriteGuideAsync(int accountId, int guideId, bool isSet, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] SetFavoriteGuideAsync: Account #{AccountId}, Guide #{GuideId}, isSet: {IsSet}", accountId, guideId, isSet);

        Account? account = await _db.Accounts.Include(a => a.GuidePreferences).FirstOrDefaultAsync(a => a.ID == accountId, ct);
        if (account is null)
        {
            _logger.LogWarning("[HeroGuideRepository] SetFavoriteGuideAsync failed: Account #{AccountId} not found", accountId);
            return -1;
        }

        if (isSet && guideId > 0)
        {
            if (!account.FavoriteGuideIDs.Contains(guideId))
            {
                account.FavoriteGuideIDs.Add(guideId);
            }

            AccountGuidePreference? pref = account.GuidePreferences.FirstOrDefault(p => p.GuideID == guideId);
            if (pref is null)
            {
                account.GuidePreferences.Add(new AccountGuidePreference
                {
                    AccountID = accountId,
                    GuideID = guideId,
                    IsFavorite = true,
                    IsDefault = false
                });
            }
            else
            {
                pref.IsFavorite = true;
                pref.TimestampUpdated = TimeProvider.System.GetUtcNow();
            }
        }
        else
        {
            account.FavoriteGuideIDs.Remove(guideId);

            AccountGuidePreference? pref = account.GuidePreferences.FirstOrDefault(p => p.GuideID == guideId);
            if (pref is not null)
            {
                if (pref.IsDefault)
                {
                    pref.IsFavorite = false;
                    pref.TimestampUpdated = TimeProvider.System.GetUtcNow();
                }
                else
                {
                    account.GuidePreferences.Remove(pref);
                    _db.AccountGuidePreferences.Remove(pref);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[HeroGuideRepository] Favorite guides updated for Account #{AccountId}. Current FavoriteGuideIDs: [{Guides}]", accountId, string.Join(",", account.FavoriteGuideIDs));
        return isSet ? guideId : -1;
    }

    public async Task<(int UpVotes, int DownVotes, float Rating)> VoteGuideAsync(int guideId, int voteVal, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] VoteGuideAsync: Guide #{GuideId}, VoteVal: {VoteVal}", guideId, voteVal);

        HeroGuide? guide = await _db.HeroGuides.FirstOrDefaultAsync(g => g.ID == guideId, ct);
        if (guide is null)
        {
            _logger.LogWarning("[HeroGuideRepository] VoteGuideAsync failed: Guide #{GuideId} not found", guideId);
            return (0, 0, 0f);
        }

        if (voteVal > 0)
        {
            guide.UpVotes++;
        }
        else
        {
            guide.DownVotes++;
        }

        int totalVotes = guide.UpVotes + guide.DownVotes;
        guide.Rating = totalVotes > 0
            ? (float)Math.Round((double)guide.UpVotes / totalVotes, 2)
            : 1.0f;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[HeroGuideRepository] Guide #{GuideId} voted: Up={Up}, Down={Down}, Rating={Rating}", guideId, guide.UpVotes, guide.DownVotes, guide.Rating);
        return (guide.UpVotes, guide.DownVotes, guide.Rating);
    }

    public async Task<HeroGuide> SaveGuideAsync(HeroGuide guide, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(guide);

        if (guide.ID == 0)
        {
            _logger.LogInformation("[HeroGuideRepository] Creating new guide '{GuideName}' for Hero '{HeroIdentifier}'", guide.Name, guide.HeroIdentifier);
            guide.TimestampCreated = TimeProvider.System.GetUtcNow();
            await _db.HeroGuides.AddAsync(guide, ct);
        }
        else
        {
            _logger.LogInformation("[HeroGuideRepository] Updating existing guide #{GuideId} ('{GuideName}')", guide.ID, guide.Name);
            guide.TimestampLastUpdated = TimeProvider.System.GetUtcNow();

            HeroGuide? existing = await _db.HeroGuides
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .FirstOrDefaultAsync(g => g.ID == guide.ID, ct);

            if (existing != null)
            {
                if (!ReferenceEquals(existing, guide))
                {
                    _db.Entry(existing).CurrentValues.SetValues(guide);

                    // Reconcile Items
                    existing.Items.Clear();
                    if (guide.Items is { Count: > 0 })
                    {
                        foreach (HeroGuideItem item in guide.Items)
                        {
                            existing.Items.Add(new HeroGuideItem
                            {
                                GuideID = existing.ID,
                                Guide = existing,
                                SlotType = item.SlotType,
                                OrderIndex = item.OrderIndex,
                                ItemCode = item.ItemCode
                            });
                        }
                    }

                    // Reconcile Abilities
                    existing.Abilities.Clear();
                    if (guide.Abilities is { Count: > 0 })
                    {
                        foreach (HeroGuideAbility ability in guide.Abilities)
                        {
                            existing.Abilities.Add(new HeroGuideAbility
                            {
                                GuideID = existing.ID,
                                Guide = existing,
                                LevelSequence = ability.LevelSequence,
                                AbilitySlot = ability.AbilitySlot,
                                AbilityCode = ability.AbilityCode
                            });
                        }
                    }
                }
                guide = existing;
            }
            else
            {
                _db.HeroGuides.Update(guide);
            }
        }

        await _db.SaveChangesAsync(ct);
        return guide;
    }

    public async Task<bool> DeleteGuideAsync(int guideId, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] Deleting guide #{GuideId}", guideId);
        HeroGuide? guide = await _db.HeroGuides
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .FirstOrDefaultAsync(g => g.ID == guideId, ct);
        if (guide is null) return false;

        _db.HeroGuides.Remove(guide);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ToggleFeaturedAsync(int guideId, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] Toggling Featured status for Guide #{GuideId}", guideId);
        HeroGuide? guide = await _db.HeroGuides.FirstOrDefaultAsync(g => g.ID == guideId, ct);
        if (guide is null) return false;

        guide.Featured = !guide.Featured;
        guide.TimestampLastUpdated = TimeProvider.System.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return guide.Featured;
    }

    public async Task<bool> TogglePublicAsync(int guideId, CancellationToken ct = default)
    {
        _logger.LogInformation("[HeroGuideRepository] Toggling Public status for Guide #{GuideId}", guideId);
        HeroGuide? guide = await _db.HeroGuides.FirstOrDefaultAsync(g => g.ID == guideId, ct);
        if (guide is null) return false;

        guide.Public = !guide.Public;
        guide.TimestampLastUpdated = TimeProvider.System.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return guide.Public;
    }

    public async Task<ItemRecipeNodeDTO?> ResolveRecipeTreeAsync(string itemCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return null;

        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        return await ResolveInternalAsync(itemCode.Trim(), visited, ct);
    }

    private async Task<ItemRecipeNodeDTO?> ResolveInternalAsync(string itemCode, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(itemCode))
        {
            return null;
        }

        HeroItem? item = await _db.HeroItems.AsNoTracking()
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.Code.ToLower() == itemCode.ToLower(), ct);

        if (item is null) return null;

        ItemRecipeNodeDTO node = new()
        {
            ItemCode = item.Code,
            Name = item.Name,
            Cost = item.Cost,
            RecipeCost = item.RecipeCost,
            Components = []
        };

        if (item.Components is { Count: > 0 })
        {
            foreach (HeroItemComponent comp in item.Components)
            {
                ItemRecipeNodeDTO? subRecipe = await ResolveInternalAsync(comp.ComponentItemCode, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase), ct);
                node.Components.Add(new ItemRecipeComponentDTO
                {
                    ComponentItemCode = comp.ComponentItemCode,
                    Quantity = comp.Quantity,
                    SubRecipe = subRecipe
                });
            }
        }

        return node;
    }
}
