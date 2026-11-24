// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace Kongor.Core.Configuration;

/// <summary>
/// Immutable descriptor of a resolved bundled layout structure.
/// </summary>
public sealed record BundledLayout
{
    public string Root { get; init; }
    public string Rid { get; init; }
    public bool IsWindows { get; init; }

    public string ExeSuffix => IsWindows ? ".exe" : string.Empty;
    public string UserData => Path.Combine(Root, "userdata");
    public string SecretsDir => Path.Combine(UserData, "secrets");
    public string SecretsJson => Path.Combine(SecretsDir, "nexus_secrets.json");
    public string DockerEnv => Path.Combine(SecretsDir, ".env.docker");
    public string InitialCredentials => Path.Combine(UserData, "initial_admin_credentials.txt");
    public string InviteCodes => Path.Combine(UserData, "invite_codes.txt");
    public string DataProtectionKeys => Path.Combine(UserData, "dataprotection-keys");
    public string Logs => Path.Combine(UserData, "logs");
    public string DockerCompose => Path.Combine(Root, "infra", "docker", "docker-compose.yml");
    public string ClientDir => Path.Combine(Root, "games", "client");
    public string DedicatedServerDir => Path.Combine(Root, "games", "server");

    public string LauncherExe
    {
        get
        {
            string rootPath = Path.Combine(Root, "WILLOWMAKER" + ExeSuffix);
            if (File.Exists(rootPath))
            {
                return rootPath;
            }

            string ridPath = Path.Combine(Root, $"launcher-{Rid}", "WILLOWMAKER" + ExeSuffix);
            if (File.Exists(ridPath))
            {
                return ridPath;
            }

            return IsWindows ? rootPath : ridPath;
        }
    }

    public string ControlPanelExe => Path.Combine(Root, $"control-panel-{Rid}", "NEXUS.ControlPanel.GUI" + ExeSuffix);
    public string CompelExe => Path.Combine(Root, $"compel-{Rid}", "COMPEL" + ExeSuffix);
    public string CompelGuiExe => Path.Combine(Root, $"compel-{Rid}", "COMPEL.GUI" + ExeSuffix);
    public string KongorCliExe => Path.Combine(Root, $"tools-{Rid}", "kongor" + ExeSuffix);

    public string ServiceDir(string serviceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceFolder);
        return Path.Combine(Root, $"services-{Rid}", serviceFolder);
    }

    public static string MapDefaultServiceBinaryName(string serviceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceFolder);
        return serviceFolder switch
        {
            "master-server" => "KONGOR.MasterServer",
            "chat-server" => "TRANSMUTANSTEIN.ChatServer",
            "web-portal-api" => "ZORGATH.WebPortal.API",
            "web-portal-ui" => "DAWNBRINGER.WebPortal.UI",
            _ => serviceFolder
        };
    }

    public string ServiceExe(string serviceFolder, string? binaryBaseName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceFolder);
        string resolvedBaseName = string.IsNullOrWhiteSpace(binaryBaseName)
            ? MapDefaultServiceBinaryName(serviceFolder)
            : binaryBaseName;

        return Path.Combine(ServiceDir(serviceFolder), resolvedBaseName + ExeSuffix);
    }

    public BundledLayout(string root, string rid, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Rid = rid;
        IsWindows = isWindows;
    }

    public static string DetectCurrentRid(OSPlatform? osOverride = null, Architecture? archOverride = null)
    {
        Architecture arch = archOverride ?? RuntimeInformation.ProcessArchitecture;

        if (osOverride.HasValue)
        {
            OSPlatform os = osOverride.Value;
            if (os == OSPlatform.Windows)
            {
                return arch == Architecture.Arm64 ? "win-arm64" : "win-x64";
            }
            if (os == OSPlatform.Linux)
            {
                return arch == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            }
            if (os == OSPlatform.OSX)
            {
                return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            }

            return arch == Architecture.Arm64 ? $"{os.ToString().ToLowerInvariant()}-arm64" : $"{os.ToString().ToLowerInvariant()}-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return arch == Architecture.Arm64 ? "win-arm64" : "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return arch == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return arch == Architecture.Arm64 ? "unknown-arm64" : "unknown-x64";
    }
}

/// <summary>
/// Resolves a standalone/playtest bundled layout by walking up directory ancestors looking for
/// playtest_policy.json and games/client/.
/// </summary>
public static class BundledLayoutResolver
{
    private static readonly ConcurrentDictionary<string, byte> s_loggedRoots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public const string PolicyFileName = "playtest_policy.json";
    public const string GamesFolderName = "games";
    public const string ClientFolderName = "client";

    /// <summary>
    /// Attempts to resolve the bundle layout by walking up 0..3 parent directories from the start directory.
    /// </summary>
    /// <param name="layout">When resolved, contains the layout descriptor; otherwise null.</param>
    /// <param name="startDirectory">Starting probe directory. Defaults to AppContext.BaseDirectory.</param>
    /// <param name="ridOverride">Optional RID override; defaults to current runtime RID.</param>
    /// <param name="isWindowsOverride">Optional OS platform override; defaults to current OS.</param>
    /// <returns>True if a valid bundle root was found within 0..3 parent levels; false otherwise.</returns>
    public static bool TryResolve(
        [MaybeNullWhen(false)] out BundledLayout layout,
        string? startDirectory = null,
        string? ridOverride = null,
        bool? isWindowsOverride = null,
        Action<string>? diagnosticLogger = null)
    {
        Action<string> writeDiagnostic = diagnosticLogger ?? Console.Error.WriteLine;
        string start = !string.IsNullOrWhiteSpace(startDirectory)
            ? startDirectory
            : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONGOR_BUNDLE_ROOT"))
                ? Environment.GetEnvironmentVariable("KONGOR_BUNDLE_ROOT")!
                : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KongorWorkspaceResolver.EnvKongorRoot))
                    ? Environment.GetEnvironmentVariable(KongorWorkspaceResolver.EnvKongorRoot)!
                    : AppContext.BaseDirectory));
        if (string.IsNullOrWhiteSpace(start))
        {
            layout = null;
            return false;
        }

        string? current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        }
        catch (Exception ex)
        {
            writeDiagnostic($"[BundledLayout] Failed to normalize startDirectory '{start}': {ex.Message}");
            layout = null;
            return false;
        }

        for (int distance = 0; distance <= 3 && !string.IsNullOrEmpty(current); distance++)
        {
            string policyPath = Path.Combine(current, PolicyFileName);
            string clientDirPath = Path.Combine(current, GamesFolderName, ClientFolderName);

            if (File.Exists(policyPath) && Directory.Exists(clientDirPath))
            {
                string rid = !string.IsNullOrWhiteSpace(ridOverride)
                    ? ridOverride
                    : BundledLayout.DetectCurrentRid();
                bool isWindows = isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                layout = new BundledLayout(current, rid, isWindows);

                if (s_loggedRoots.TryAdd(layout.Root, 0))
                {
                    writeDiagnostic($"[BundledLayout] root={layout.Root} rid={layout.Rid}");
                }

                return true;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        layout = null;
        return false;
    }

    internal static void ResetLoggedRootsForTesting()
    {
        s_loggedRoots.Clear();
    }
}
