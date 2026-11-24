// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Models;

/// <summary>
///     Represents an option in an address selection dropdown (such as Master Server or Content Delivery Network).
/// </summary>
public sealed class AddressSelectItem
{
    /// <summary>
    ///     The text displayed in the selection interface.
    /// </summary>
    public required string DisplayText { get; init; }

    /// <summary>
    ///     The network target address or URL. When <see langword="null"/>, indicates a custom address entry.
    /// </summary>
    public string? TargetURL { get; init; }

    /// <summary>
    ///     Optional contextual explanation shown on hover.
    /// </summary>
    public string? TooltipText { get; init; }

    /// <summary>
    ///     Indicates whether this entry is currently selectable or greyed out/disabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    ///     Indicates whether this entry represents a custom user-supplied address.
    /// </summary>
    public bool IsCustom => TargetURL is null;

    public override string ToString() => DisplayText;
}
