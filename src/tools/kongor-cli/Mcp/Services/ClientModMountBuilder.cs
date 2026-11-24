// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Core.Vfs;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service interface for building client VFS mod mounts, canonical launch arguments, and nickname resolution.
/// </summary>
public interface IClientModMountBuilder
{
    IReadOnlyList<string> BuildClientLaunchArguments(
        string gameDir,
        string mode,
        string? connectAddress,
        string masterServer,
        string resolution,
        bool fullscreen,
        int maxFps,
        string? customNickname = null,
        string windowMode = "foreground",
        int offscreenX = -32000,
        int offscreenY = -32000,
        int slotIndex = 0,
        string? pipeName = null,
        string? passwordMd5 = null,
        bool autoLogin = true);

    string ResolveDefaultNickname(string? clientDirectory = null);
    void EnsureClientDefaultNameConfigured(string gameDirectory, string nickname);
    bool TryParseResolution(string res, out int width, out int height);
    string FindRepoRoot(string startingDir);
    IReadOnlyList<string> GetCanonicalMountHierarchy(string? clientDir = null);
}

/// <summary>
/// Builds canonical launch arguments matching WILLOWMAKER and K2 specifications.
/// Injects full VFS mod mounts, DXVK Vulkan settings, low-latency audio, and Named Pipe IPC.
/// </summary>
public sealed class ClientModMountBuilder : IClientModMountBuilder
{
    public const int CanonicalSimulationTickRate = ClientModMountSpec.CanonicalSimulationTickRate;

    public static readonly IReadOnlyList<string> CanonicalMountTiers = ClientModMountSpec.CanonicalMountTiers;

    public static string GetCanonicalModString(bool includeOverride = true, bool isSafeMode = false) =>
        ClientModMountSpec.GetCanonicalModString(includeOverride, isSafeMode);

    public IReadOnlyList<string> GetCanonicalMountHierarchy(string? clientDir = null)
        => CanonicalMountTiers;

    public static void EnsureHeadlessStartupCfgSeeded(string gameDir, Action<string>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return;
        }

        try
        {
            string overrideDir = Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "override");
            Directory.CreateDirectory(overrideDir);
            string headlessCfg = Path.Combine(overrideDir, "startup_headless.cfg");
            if (!File.Exists(headlessCfg))
            {
                string foregroundCfg = Path.Combine(overrideDir, "startup.cfg");
                if (File.Exists(foregroundCfg))
                {
                    File.Copy(foregroundCfg, headlessCfg, overwrite: false);
                    logger?.Invoke($"[HeadlessConfig] Seeded isolated '{headlessCfg}' from '{foregroundCfg}'.");
                }
                else
                {
                    File.WriteAllText(
                        headlessCfg,
                        "SetSave \"vid_fullscreen\" \"false\" \"0\"\nSetSave \"d3d_exclusive\" \"false\" \"0\"\nSetSave \"options_fullscreen\" \"false\" \"0\"\n");
                    logger?.Invoke($"[HeadlessConfig] Created minimal isolated '{headlessCfg}'.");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[HeadlessConfig] Non-fatal warning seeding startup_headless.cfg: {ex.Message}");
        }
    }

    public IReadOnlyList<string> BuildClientLaunchArguments(
        string gameDir,
        string mode,
        string? connectAddress,
        string masterServer,
        string resolution,
        bool fullscreen,
        int maxFps,
        string? customNickname = null,
        string windowMode = "foreground",
        int offscreenX = -32000,
        int offscreenY = -32000,
        int slotIndex = 0,
        string? pipeName = null,
        string? passwordMd5 = null,
        bool autoLogin = true)
    {
        bool isHeadless = string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(mode, "headless", StringComparison.OrdinalIgnoreCase);

        bool isP2p = string.Equals(mode, "p2p_host", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "p2p_connect", StringComparison.OrdinalIgnoreCase);

        string effectiveMasterServer = isP2p
            ? ((string.Equals(masterServer, "127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) || string.Equals(masterServer, "masterserver.newbs.io", StringComparison.OrdinalIgnoreCase)) ? "127.0.0.1:19999" : GameLauncherService.NormalizeMasterServerAddress(masterServer))
            : GameLauncherService.NormalizeMasterServerAddress(masterServer);

        List<string> args = [];

        // 1. Service Endpoints Routing
        bool isLocalhostCluster = ClientModMountSpec.IsLocalhostEndpoint(effectiveMasterServer) && !isP2p;
        args.Add($"-masterserver {effectiveMasterServer}");
        if (isLocalhostCluster)
        {
            args.Add("-chatserver 127.0.0.1:11031");
        }
        args.Add($"-webserver {effectiveMasterServer}");
        args.Add($"-messageserver {effectiveMasterServer}");
        if (isLocalhostCluster)
        {
            args.Add("+Set chat_serverAddrOverride 127.0.0.1");
            args.Add("+Set chat_serverPortOverride 11031");
        }

        // 2. Canonical VFS Mod Mounts Chain:
        // Tier: override (hot-reload loose files & mod extractions)
        // Tier: base (engine base package: resources0.s2z)
        // Tier: game (base game packages: textures.s2z, hero_content.s2z, game.resources)
        // Tier: ClientPackageFiles (unpacked active modern client features)
        // Tier: override (evaluated FIRST by K2 reverse GetFile iteration)
        args.Add(GetCanonicalModString());

        // 3. Real-Time Non-Blocking Console Logging (Slot-isolated to prevent lock collision)
        args.Add("-writeconsole");
        if (slotIndex > 0)
        {
            args.Add($"-logfile console_client_{slotIndex}.log");
        }
        else
        {
            args.Add("-logfile console.log");
        }
        args.Add("+Set log_flush false");
        args.Add("+Set con_flushLog false");
        args.Add("+Set sys_flushLogs false");
        args.Add("+Set con_log 1");
        args.Add("+Set host_writeConsole true");

        // 4. Named Pipe IPC Hook (supports per-slot or custom pipe)
        string effectivePipe = !string.IsNullOrWhiteSpace(pipeName) ? pipeName.Trim() : "willowmaker-ipc";
        args.Add($"-pipe {effectivePipe}");

        // 5. Performance & FPS Uncapping (DXVK Vulkan compatibility)
        args.Add($"+Set host_maxFPS {maxFps}");
        args.Add($"+Set cl_maxFPS {maxFps}");
        args.Add($"+Set vid_maxFPS {maxFps}");
        args.Add("+Set sys_sleepTime 0");
        args.Add("+Set vid_bkgndSleep 0");
        args.Add("+Set cl_sleep 0");
        args.Add("+Set host_sleep 0");
        args.Add("+Set gl_swapInterval 0");
        args.Add("+Set d3d_tripleBuffering false");
        args.Add("+Set d3d_pure true");
        args.Add("+Set vid_driver vid_vulkan");
        args.Add("+Set vid_textureCompression true");
        if (slotIndex > 0)
        {
            args.Add("+Set vid_textureDownsize 1");
        }

        // 5b. Networking & NAT Compatibility (Align with dedicated servers)
        args.Add("+Set net_enableWatermark false");

        // 6. Low-Latency Audio Tuning (5.3ms Mixer & 64-Voice Spatial Polyphony) or Headless Silence (RAM Diet on secondary slots)
        if (isHeadless || slotIndex > 0)
        {
            args.Add("+Set sound_disable true");
        }
        else
        {
            args.Add("+Set sound_numChannels 64");
            args.Add("+Set sound_bufferLength 256");
            args.Add("+Set sound_mixRate 48000");
            args.Add("+Set sound_recording_mixRate 48000");
        }

        // 7. UI & Developer Mode
        args.Add("+Set releaseStage_dev true");
        args.Add("+Set releaseStage_test true");
        args.Add("+Set releaseStage_stable false");
        args.Add("+Set ui_dev true");
        args.Add("+Set ui_dev_button true");
        args.Add("+Set ui_showQuickStart true");
        args.Add("+Set ui_canCreateGame true");
        args.Add("+Set cg_24hourClock true");
        args.Add("+Set host_dynamicResReload true");

        // 8. Display & Resolution Settings (Slot-isolated offscreen coordinates)
        if (isHeadless)
        {
            if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
            {
                EnsureHeadlessStartupCfgSeeded(gameDir);
            }

            // Do not pass vid_headless true because it disables GPU viewport presentation.
            // vid_pos offscreenX offscreenY positions the window offscreen for full GPU rendering with zero desktop disruption.
            // Isolate config writes into ~/startup_headless.cfg and never mutate cg_constrainCursor so foreground startup.cfg stays clean.
            // Honor explicit non-default resolution (e.g. 2560x1440 for High-Hz perf-canary), defaulting to 1280x720.
            int headlessW = 1280;
            int headlessH = 720;
            if (!string.IsNullOrWhiteSpace(resolution) &&
                !string.Equals(resolution.Trim(), "1920x1080", StringComparison.OrdinalIgnoreCase) &&
                TryParseResolution(resolution.Trim(), out int parsedW, out int parsedH))
            {
                headlessW = parsedW;
                headlessH = parsedH;
            }
            args.Add("+Set host_startupCfg \"~/startup_headless.cfg\"");
            args.Add($"+Set vid_resolution \"{headlessW},{headlessH}\"");
            args.Add($"+Set vid_width {headlessW}");
            args.Add($"+Set vid_height {headlessH}");
            args.Add("+Set vid_fullscreen false");
            args.Add("+Set vid_borderless true");
            args.Add("+Set d3d_exclusive false");
            args.Add("+Set options_fullscreen false");
            args.Add($"+Set vid_pos {offscreenX} {offscreenY}");
            args.Add($"-x {offscreenX}");
            args.Add($"-y {offscreenY}");
            args.Add("+Set sys_autoSaveConfig false");
        }
        else
        {
            string res = string.IsNullOrWhiteSpace(resolution) ? "1920x1080" : resolution.Trim();
            int posX = (slotIndex <= 1) ? 20 : (20 + (slotIndex - 1) * 660);
            int posY = 40;
            if (TryParseResolution(res, out int width, out int height))
            {
                args.Add($"+Set vid_resolution \"{width},{height}\"");
                args.Add($"+Set vid_width {width}");
                args.Add($"+Set vid_height {height}");
            }
            args.Add("+Set vid_bpp 32");
            args.Add("+Set vid_refreshRate 60");
            if (fullscreen)
            {
                args.Add("+Set vid_fullscreen true");
                args.Add("+Set vid_borderless false");
            }
            else
            {
                args.Add($"+Set vid_pos {posX} {posY}");
                args.Add("+Set vid_fullscreen false");
                args.Add("+Set vid_borderless true");
            }
        }

        // 9. Account Authentication & In-Memory Credential Provisioning (No disk .cfg modification)
        string resolvedNick = !string.IsNullOrWhiteSpace(customNickname) &&
                             !string.Equals(customNickname, Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(customNickname, "UnnamedNewbie", StringComparison.OrdinalIgnoreCase)
            ? customNickname.Trim()
            : (slotIndex == 0 ? ResolveDefaultNickname(gameDir) : $"guest-{slotIndex:D2}");

        string resolvedPassMd5 = !string.IsNullOrWhiteSpace(passwordMd5)
            ? passwordMd5.Trim()
            : ClientCredentialManager.DefaultPasswordMd5;

        args.Add($"+Set login_name \"{resolvedNick}\"");
        args.Add($"+Set login_password \"{resolvedPassMd5}\"");
        args.Add("+Set login_rememberName true");
        args.Add($"+Set login_rememberPassword {(slotIndex > 0 ? "false" : "true")}");
        args.Add($"+Set login_autoLogin {(autoLogin ? "true" : "false")}");
        args.Add($"+Set p2p_nickname \"{resolvedNick}\"");

        // 9. Startup Mode
        if (string.Equals(mode, "practice", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set svr_login false");
            args.Add("+Set svr_broadcastLocal false");
            args.Add($"+Set svr_gameFPS {CanonicalSimulationTickRate}");
            args.Add("+Set svr_maxFramesPerHostFrame 5");
            args.Add("+Set sv_precacheEntities false");
            args.Add("+Set ui_quickStart true");
            args.Add("+Set dev true");
            args.Add("+Set cheats true");
            args.Add("+Set map caldavar");
            args.Add("+Set default_hero Hero_Pyromancer");
            args.Add("+StartGame practice PracticeGame map:caldavar allheroes:true");
        }
        else if (string.Equals(mode, "bot_match", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set game_botMatch true");
            args.Add("+Set svr_botMatch true");
            args.Add("+StartGame bots");
        }
        else if (string.Equals(mode, "p2p_host", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set p2p_mode true");
            args.Add("+Set p2p_auto_host true");
            args.Add("+Set p2p_standalone true");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
        }
        else if (string.Equals(mode, "p2p_connect", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set p2p_mode true");
            args.Add("+Set p2p_auto_connect true");
            args.Add("+Set p2p_standalone true");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                args.Add($"+Set p2p_connect_addr \"{connectAddress.Trim()}\"");
                args.Add($"+Connect {connectAddress.Trim()}");
            }
        }
        else if (string.Equals(mode, "connect", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(connectAddress))
        {
            args.Add($"+Connect {connectAddress.Trim()}");
        }

        // Direct connect address override regardless of mode
        if (!string.IsNullOrWhiteSpace(connectAddress) && !string.Equals(mode, "connect", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "p2p_connect", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"+Connect {connectAddress.Trim()}");
        }

        return args.AsReadOnly();
    }

    public string ResolveDefaultNickname(string? clientDirectory = null)
    {
        string baseDir = clientDirectory ?? Environment.CurrentDirectory;
        string repoRoot = FindRepoRoot(baseDir);
        string clientDir = Path.Combine(repoRoot, "games", "client");

        // 1. Try launcher_settings.json
        try
        {
            string settingsPath = Path.Combine(clientDir, "launcher_settings.json");
            if (!File.Exists(settingsPath))
            {
                settingsPath = Path.Combine(baseDir, "launcher_settings.json");
            }
            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("LastPlayerNickname", out JsonElement elem))
                {
                    string? candidate = elem.GetString();
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        !string.Equals(candidate, Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(candidate, "UnnamedNewbie", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.Trim();
                    }
                }
            }
        }
        catch { }

        // 2. Try login.cfg
        string[] loginCfgCandidates =
        [
            Path.Combine(clientDir, "userdataKONGOR", "configuration", "login.cfg"),
            Path.Combine(clientDir, "userdataKONGOR", "extensions", "login.cfg"),
            Path.Combine(clientDir, "KONGOR", "configuration", "login.cfg"),
            Path.Combine(clientDir, "userdata", "login.cfg"),
            Path.Combine(baseDir, "userdata", "login.cfg"),
            Path.Combine(baseDir, "KONGOR", "configuration", "login.cfg")
        ];

        foreach (string path in loginCfgCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                string content = File.ReadAllText(path);
                if (content.Contains('\0'))
                {
                    content = File.ReadAllText(path, Encoding.Unicode);
                }

                Match match = Regex.Match(content, @"(?:SetSave\s+""?login_name""?\s+""?([^""\r\n]+)""?|login_name\s+([^\r\n\t ]+))", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string found = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value.Trim() : match.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(found) &&
                        !string.Equals(found, Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(found, "UnnamedNewbie", StringComparison.OrdinalIgnoreCase))
                    {
                        return found;
                    }
                }
            }
            catch { }
        }

        return "Player";
    }

    public void EnsureClientDefaultNameConfigured(string gameDirectory, string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.Equals(nickname, Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string overrideStringtablesDir = Path.Combine(gameDirectory, "override", "stringtables");
            Directory.CreateDirectory(overrideStringtablesDir);

            string[] filenames = ["client_messages_en.str", "client_messages.str"];
            foreach (string file in filenames)
            {
                string targetPath = Path.Combine(overrideStringtablesDir, file);
                string content = $"client_default_name\t\t\t\t{nickname}\r\n";
                if (File.Exists(targetPath))
                {
                    string existing = File.ReadAllText(targetPath, Encoding.UTF8);
                    if (Regex.IsMatch(existing, @"^client_default_name\s+", RegexOptions.Multiline))
                    {
                        content = Regex.Replace(existing, @"^client_default_name\s+[^\r\n]*", $"client_default_name\t\t\t\t{nickname}", RegexOptions.Multiline);
                    }
                    else
                    {
                        content = content + "\r\n" + existing;
                    }
                }
                File.WriteAllText(targetPath, content, Encoding.UTF8);
            }

            string sharedOverrideDir = Path.GetFullPath(Path.Combine(gameDirectory, "..", "shared", "override", "stringtables"));
            if (Directory.Exists(Path.GetDirectoryName(sharedOverrideDir)))
            {
                Directory.CreateDirectory(sharedOverrideDir);
                foreach (string file in filenames)
                {
                    string targetPath = Path.Combine(sharedOverrideDir, file);
                    string content = $"client_default_name\t\t\t\t{nickname}\r\n";
                    if (File.Exists(targetPath))
                    {
                        string existing = File.ReadAllText(targetPath, Encoding.UTF8);
                        if (Regex.IsMatch(existing, @"^client_default_name\s+", RegexOptions.Multiline))
                        {
                            content = Regex.Replace(existing, @"^client_default_name\s+[^\r\n]*", $"client_default_name\t\t\t\t{nickname}", RegexOptions.Multiline);
                        }
                        else
                        {
                            content = content + "\r\n" + existing;
                        }
                    }
                    File.WriteAllText(targetPath, content, Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Non-critical stringtable override
        }
    }

    public bool TryParseResolution(string res, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(res)) return false;

        string[] parts = res.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height))
        {
            return width > 0 && height > 0;
        }

        return false;
    }

    public string FindRepoRoot(string startingDir)
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out var ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions { InvocationCwd = startingDir }) && ws is not null)
        {
            return ws.Root;
        }

        return startingDir;
    }

    /// <summary>
    /// Exhaustively validates all combinations of canonical VFS mount tier states and .bak1-.bak5 backup rotation tiers
    /// using <see cref="Exhaustigen"/> without heap allocations or nested loops (#223).
    /// Returns the total number of valid state combinations verified.
    /// </summary>
    public static int VerifyExhaustiveMountAndBackupStates()
    {
        Exhaustigen gen = new();
        int combinations = 0;
        int tierCount = CanonicalMountTiers.Count;

        while (!gen.Done())
        {
            int activeMountTierIndex = gen.Index(tierCount);
            VfsBackupRotationTier backupTier = gen.EnumValue<VfsBackupRotationTier>();
            bool overridePresent = gen.Bool();

            string winningTier = overridePresent
                ? CanonicalMountTiers[tierCount - 1]
                : CanonicalMountTiers[activeMountTierIndex];

            if (overridePresent && !string.Equals(winningTier, "override", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"[ClientModMountBuilder] VFS precedence invariant violated: expected 'override', got '{winningTier}' (backup={backupTier}).");
            }

            combinations++;
        }

        return combinations;
    }
}

/// <summary>
/// 5-tier .bak rotation states for non-destructive .s2z archive injection (#223).
/// </summary>
public enum VfsBackupRotationTier
{
    None = 0,
    Bak1 = 1,
    Bak2 = 2,
    Bak3 = 3,
    Bak4 = 4,
    Bak5 = 5
}

/// <summary>
/// Wave 4 (#223): TigerBeetle-inspired zero-allocation 32-slot stateful decision-tree cursor
/// (`src/testing/exhaustigen.zig`) that exhaustively enumerates combinations of discrete choices
/// (`Index`, `Bool`, `EnumValue`) without nested loops or heap allocations.
/// </summary>
public struct Exhaustigen
{
    public const int MaxSlots = 32;

    [System.Runtime.CompilerServices.InlineArray(MaxSlots)]
    private struct DecisionSlotBuffer
    {
        private DecisionSlot _element0;
    }

    private struct DecisionSlot
    {
        public int Value;
        public int Count;
    }

    private static class EnumSlotCache<TEnum> where TEnum : struct, Enum
    {
        public static readonly TEnum[] Values = Enum.GetValues<TEnum>();
    }

    private DecisionSlotBuffer _slots;
    private int _stackCount;
    private int _cursor;
    private bool _started;
    private bool _exhausted;

    public readonly int Depth => _stackCount;
    public readonly int Cursor => _cursor;

    /// <summary>
    /// Selects the current branch index in `[0, count)` for the current decision node.
    /// </summary>
    public int Index(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "[Exhaustigen] Choice count must be strictly positive.");
        }

        if (_exhausted)
        {
            throw new InvalidOperationException("[Exhaustigen] Decision tree is already exhausted.");
        }

        if (_cursor >= MaxSlots)
        {
            throw new InvalidOperationException($"[Exhaustigen] Decision depth exceeded maximum of {MaxSlots} slots.");
        }

        _started = true;

        if (_cursor < _stackCount)
        {
            DecisionSlot existing = _slots[_cursor];
            if (existing.Count != count)
            {
                throw new InvalidOperationException($"[Exhaustigen] Nondeterministic choice count at cursor {_cursor}: expected {existing.Count}, got {count}.");
            }

            _cursor++;
            return existing.Value;
        }

        _slots[_cursor] = new DecisionSlot
        {
            Value = 0,
            Count = count
        };
        _stackCount = _cursor + 1;
        _cursor++;
        return 0;
    }

    /// <summary>
    /// Exhaustively enumerates `false` then `true` at the current decision node.
    /// </summary>
    public bool Bool()
    {
        return Index(2) == 1;
    }

    /// <summary>
    /// Exhaustively enumerates all defined values of <typeparamref name="TEnum"/> at the current decision node.
    /// </summary>
    public TEnum EnumValue<TEnum>() where TEnum : struct, Enum
    {
        TEnum[] values = EnumSlotCache<TEnum>.Values;
        int idx = Index(values.Length);
        return values[idx];
    }

    /// <summary>
    /// Advances the decision-tree cursor to the next combination.
    /// Supports both `while (!gen.Done())` and `do { ... } while (!gen.Done())` loop idioms.
    /// Returns `true` when all combinations have been visited.
    /// </summary>
    public bool Done()
    {
        if (_exhausted)
        {
            return true;
        }

        if (!_started && _cursor == 0)
        {
            _started = true;
            return false;
        }

        _cursor = 0;

        for (int i = _stackCount - 1; i >= 0; i--)
        {
            DecisionSlot slot = _slots[i];
            if (slot.Value + 1 < slot.Count)
            {
                slot.Value++;
                _slots[i] = slot;
                _stackCount = i + 1;
                return false;
            }
        }

        _stackCount = 0;
        _exhausted = true;
        return true;
    }
}

