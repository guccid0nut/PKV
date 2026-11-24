// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Inventory;

/// <summary>
///     Command to grant an item to a user's inventory by product code.
/// </summary>
/// <param name="AccountName">The target player account name.</param>
/// <param name="ProductCode">The product/item code to grant.</param>
public sealed record AddInventoryItemCommand(string AccountName, string ProductCode) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to remove an item from a user's inventory by product code.
/// </summary>
/// <param name="AccountName">The target player account name.</param>
/// <param name="ProductCode">The product/item code to remove.</param>
public sealed record RemoveInventoryItemCommand(string AccountName, string ProductCode) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to reset a user's inventory to default starter items.
/// </summary>
/// <param name="AccountName">The target player account name.</param>
public sealed record ResetInventoryCommand(string AccountName) : IRequest<AdminActionResultDto>;
