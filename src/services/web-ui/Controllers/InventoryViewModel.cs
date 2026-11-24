// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Controllers;

// ReSharper disable PropertyCanBeMadeInitOnly.Global
public class InventoryViewModel
{
    public string AccountName { get; set; } = string.Empty;
    public List<string> OwnedItems { get; set; } = [];
    public Dictionary<string, string> CommonItems { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}
