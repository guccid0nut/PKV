// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Constants;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Inventory;

namespace ZORGATH.WebPortal.API.Features.Admin.Inventory;

/// <summary>
///     Handler for granting an item to a user's inventory.
/// </summary>
public sealed class AddInventoryItemHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<AddInventoryItemCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(AddInventoryItemCommand request, CancellationToken cancellationToken)
    {
        Account? account = await db.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.InventoryItems)
            .FirstOrDefaultAsync(a => a.Name == request.AccountName, cancellationToken);

        if (account == null || account.User == null)
            return new AdminActionResultDto(false, "Account not found", 0);

        string trimmed = request.ProductCode.Trim();
        UserInventory? existing = account.User.InventoryItems.FirstOrDefault(i =>
            string.Equals(i.ItemCode, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            StoreItem? storeItem = await db.StoreItems
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Code == trimmed || s.Name == trimmed, cancellationToken);

            account.User.InventoryItems.Add(new UserInventory
            {
                User = account.User,
                AccountID = account.ID,
                ItemCode = trimmed,
                StoreItemId = storeItem?.Id,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = _timeProvider.GetUtcNow(),
                Source = "Admin"
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return new AdminActionResultDto(true, "Item added to inventory successfully", account.ID);
    }
}

/// <summary>
///     Handler for removing an item from a user's inventory.
/// </summary>
public sealed class RemoveInventoryItemHandler(MerrickContext db)
    : IRequestHandler<RemoveInventoryItemCommand, AdminActionResultDto>
{
    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(RemoveInventoryItemCommand request, CancellationToken cancellationToken)
    {
        Account? account = await db.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.InventoryItems)
            .FirstOrDefaultAsync(a => a.Name == request.AccountName, cancellationToken);

        if (account == null || account.User == null)
            return new AdminActionResultDto(false, "Account not found", 0);

        string trimmed = request.ProductCode.Trim();
        UserInventory? existing = account.User.InventoryItems.FirstOrDefault(i =>
            string.Equals(i.ItemCode, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            db.UserInventory.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new AdminActionResultDto(true, "Item removed from inventory successfully", account.ID);
    }
}

/// <summary>
///     Handler for resetting a user's inventory to default starter items.
/// </summary>
public sealed class ResetInventoryHandler(MerrickContext db)
    : IRequestHandler<ResetInventoryCommand, AdminActionResultDto>
{
    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(ResetInventoryCommand request, CancellationToken cancellationToken)
    {
        Account? account = await db.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.InventoryItems)
            .FirstOrDefaultAsync(a => a.Name == request.AccountName, cancellationToken);

        if (account == null || account.User == null)
            return new AdminActionResultDto(false, "Account not found", 0);

        if (account.User.InventoryItems.Count > 0)
        {
            db.UserInventory.RemoveRange(account.User.InventoryItems);
        }

        account.SelectedStoreItems = [.. StoreDefaults.DefaultSelectedItems];
        await db.SaveChangesAsync(cancellationToken);

        return new AdminActionResultDto(true, "Inventory reset successfully", account.ID);
    }
}
