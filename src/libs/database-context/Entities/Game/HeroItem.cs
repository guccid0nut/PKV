// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Index(nameof(Code), IsUnique = true)]
[Index(nameof(Name))]
public class HeroItem
{
    [Key] public int ID { get; set; }

    [MaxLength(100)] public required string Code { get; set; }

    [MaxLength(150)] public required string Name { get; set; }

    public int Cost { get; set; }

    [MaxLength(2000)] public required string Description { get; set; }

    [MaxLength(2000)] public required string Flavor { get; set; }

    [MaxLength(255)] public required string IconPath { get; set; }

    [InverseProperty(nameof(HeroItemComponent.ParentItem))]
    public List<HeroItemComponent> Components { get; set; } = [];

    private string? _componentCodesJson;

    [NotMapped]
    [MaxLength(2000)]
    public string ComponentCodesJson
    {
        get
        {
            if (Components is { Count: > 0 })
            {
                return System.Text.Json.JsonSerializer.Serialize(
                    Components.SelectMany(c => Enumerable.Repeat(c.ComponentItemCode, Math.Max(1, c.Quantity))).ToList());
            }
            return _componentCodesJson ?? "[]";
        }
        set
        {
            _componentCodesJson = value;
            SyncComponentsFromJson(value);
        }
    }

    private void SyncComponentsFromJson(string? json)
    {
        Components ??= [];
        Components.Clear();
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            List<string>? codes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (codes != null)
            {
                foreach (string code in codes)
                {
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    Components.Add(new HeroItemComponent
                    {
                        ParentItemID = ID,
                        ComponentItemCode = code.Trim(),
                        Quantity = 1
                    });
                }
            }
        }
        catch
        {
            // Fallback for non-JSON or malformed legacy strings
        }
    }

    public int RecipeCost { get; set; }

    [MaxLength(200)] public string Upgrade { get; set; } = "";

    [MaxLength(100)] public string Category { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
