// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace Kongor.ModCli;

public sealed class ModStateConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public HashSet<string> EnabledMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string GetConfigPath(string modsDirectory)
    {
        return Path.Combine(modsDirectory, "mods_state.json");
    }

    public static async Task<ModStateConfig> LoadAsync(string modsDirectory)
    {
        string path = GetConfigPath(modsDirectory);
        if (!File.Exists(path)) return new ModStateConfig();

        try
        {
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ModStateConfig>(json) ?? new ModStateConfig();
        }
        catch
        {
            return new ModStateConfig();
        }
    }

    public static async Task SaveAsync(string modsDirectory, ModStateConfig config)
    {
        string path = GetConfigPath(modsDirectory);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }
}
