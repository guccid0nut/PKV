// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Configuration;

/// <summary>
///     Loads, and on first run generates, the single self-describing "COMPEL.json" configuration file that sits alongside
///     the executable.
/// </summary>
public static class CompelConfigurationLoader
{
    private const string FileName = "COMPEL.json";

    // A Dedicated Context Whose Options Indent The Output And Avoid Escaping Apostrophes And Slashes In The Descriptions, So The Generated File Reads Cleanly.
    private static readonly CompelConfigurationJSONContext WriteContext = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    public static string ResolvePath()
    {
        // 1. Direct check in base directory
        string baseFile = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(baseFile))
            return baseFile;

        // 2. Direct check in current directory
        string currentFile = Path.Combine(Environment.CurrentDirectory, FileName);
        if (File.Exists(currentFile))
            return currentFile;

        // 3. Search up directory tree for games/server/COMPEL.json or games/server/COMPEL.JSON
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidateUpper = Path.Combine(current, "games", "server", "COMPEL.JSON");
            if (File.Exists(candidateUpper))
                return candidateUpper;

            string candidateLower = Path.Combine(current, "games", "server", "COMPEL.json");
            if (File.Exists(candidateLower))
                return candidateLower;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        // 4. Search up directory tree from current working directory
        current = Environment.CurrentDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidateUpper = Path.Combine(current, "games", "server", "COMPEL.JSON");
            if (File.Exists(candidateUpper))
                return candidateUpper;

            string candidateLower = Path.Combine(current, "games", "server", "COMPEL.json");
            if (File.Exists(candidateLower))
                return candidateLower;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        return baseFile;
    }

    public static bool Exists()
    {
        return File.Exists(ResolvePath());
    }

    /// <summary>
    ///     Writes a "COMPEL.json" populated with default values and descriptions.
    /// </summary>
    public static void CreateDefault()
    {
        CreateDefault(ResolvePath());
    }

    internal static void CreateDefault(string path)
    {
        string serialised =
            JsonSerializer.Serialize(new CompelConfigurationFile(), WriteContext.CompelConfigurationFile);

        File.WriteAllText(path, serialised);
    }

    /// <summary>
    ///     Reads and deserialises "COMPEL.json". Settings absent from the file fall back to their defaults.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown with a message naming the problem when "COMPEL.json" is not valid JSON, or does not match the expected shape
    ///     (for example a string where a number is expected), rather than letting a raw <see cref="JsonException" />
    ///     propagate.
    /// </exception>
    public static CompelConfigurationFile Load()
    {
        return Load(ResolvePath());
    }

    internal static CompelConfigurationFile Load(string path)
    {
        string json = File.ReadAllText(path);

        CompelConfigurationFile file;

        try
        {
            file = JsonSerializer.Deserialize(json, CompelConfigurationJSONContext.Default.CompelConfigurationFile) ??
                   throw new InvalidOperationException(@"""COMPEL.json"" Deserialised To NULL");
        }

        catch (JsonException exception)
        {
            throw new InvalidOperationException($@"""COMPEL.json"" Is Not Valid: {exception.Message}", exception);
        }

        // A Setting Explicitly Set To "null" In The File Deserialises As A Null Object, So Each Is Coalesced Back To Its Default. This Keeps A Null Setting Behaving Like A Missing One (Falling Back To Its Default) Rather Than Faulting Later When Its Value Is Read.
        file.UserName ??= new UserNameSetting();
        file.Password ??= new PasswordSetting();
        file.Instances ??= new InstancesSetting();
        file.Gateway ??= new GatewaySetting();
        file.Location ??= new LocationSetting();
        file.ServerNamePrefix ??= new ServerNamePrefixSetting();
        file.UseProxy ??= new UseProxySetting();
        file.PortRangeOffset ??= new PortRangeOffsetSetting();
        file.RuntimeArtefactsPath ??= new RuntimeArtefactsPathSetting();
        file.CDNSynchronisation ??= new CDNSynchronisationSetting();
        file.AuthenticationToken ??= new AuthenticationTokenSetting();
        file.ControlPlanePort ??= new ControlPlanePortSetting();
        file.GrafanaOtlpEndpoint ??= new GrafanaOtlpEndpointSetting();
        file.GrafanaOtlpHeaders ??= new GrafanaOtlpHeadersSetting();
        file.PyroscopeServerAddress ??= new PyroscopeServerAddressSetting();
        file.PyroscopeBasicAuthUser ??= new PyroscopeBasicAuthUserSetting();
        file.PyroscopeBasicAuthPassword ??= new PyroscopeBasicAuthPasswordSetting();
        file.PriorityMode ??= new PriorityModeSetting();

        return file;
    }
}
