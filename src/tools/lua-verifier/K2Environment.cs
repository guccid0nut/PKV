// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LuaVerifier;

public class CvarManager
{
    private readonly Dictionary<string, string> _cvars = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) => _cvars[name] = value;
    public string GetString(string name) => _cvars.TryGetValue(name, out string? val) ? val : string.Empty;
    public bool GetBool(string name)
    {
        if (_cvars.TryGetValue(name, out string? val))
        {
            if (bool.TryParse(val, out bool b)) return b;
            if (val == "1") return true;
            if (val == "0") return false;
        }
        return false;
    }
    public int GetInt(string name) => _cvars.TryGetValue(name, out string? val) && int.TryParse(val, out int i) ? i : 0;
    public void Reset() => _cvars.Clear();
}

public static class K2Bridge
{
    private static readonly CvarManager s_cvarManager = new();
    private static readonly object s_vfsLock = new();
    private static readonly object s_warnLock = new();
    private static readonly List<string> s_globalWarnings = [];
    private static HashSet<string>? s_vfsEntries;
    private static string[]? s_orderedS2zPaths;

    public static CvarManager Cvars => s_cvarManager;
    public static bool StrictGlobals { get; set; }

    public static IReadOnlyList<string> GetGlobalWarnings()
    {
        lock (s_warnLock)
        {
            return [.. s_globalWarnings];
        }
    }

    /// <summary>
    /// Reads warnings isolated to a specific NLua state (safe under --parallel / multi-threaded xUnit runs).
    /// </summary>
    public static IReadOnlyList<string> GetGlobalWarnings(NLua.Lua lua)
    {
        ArgumentNullException.ThrowIfNull(lua);
        List<string> isolated = [];
        if (lua["__k2_global_warnings"] is NLua.LuaTable tbl)
        {
            foreach (object? val in tbl.Values)
            {
                if (val is string s && !string.IsNullOrEmpty(s))
                {
                    isolated.Add(s);
                }
            }
        }
        return isolated;
    }

    public static string FormatGlobalLeakWarning(string name, string source, int line) =>
        $"[K2-GLOBAL-WARN] Undeclared global write '{name}' at {source}:{line}. Remediation: prefix with 'local {name} = ...' (or pass --strict-globals to enforce).";

    public static string ReportGlobalLeak(string name, string source, int line)
    {
        string msg = FormatGlobalLeakWarning(name, source, line);
        lock (s_warnLock)
        {
            if (!s_globalWarnings.Contains(msg))
            {
                s_globalWarnings.Add(msg);
                Console.WriteLine(msg);
            }
        }
        return msg;
    }

    public static void InitializeEnvironment(NLua.Lua lua) => InitializeEnvironment(lua, StrictGlobals);

    public static void InitializeEnvironment(NLua.Lua lua, bool strictGlobals)
    {
        // Register backing C# functions for Cvar, VFS archive inspection, and global leak telemetry
        lua.RegisterFunction("MockGetCvarString", typeof(K2Bridge).GetMethod(nameof(GetCvarString)));
        lua.RegisterFunction("MockGetCvarBool", typeof(K2Bridge).GetMethod(nameof(GetCvarBool)));
        lua.RegisterFunction("MockGetCvarInt", typeof(K2Bridge).GetMethod(nameof(GetCvarInt)));
        lua.RegisterFunction("MockSetCvar", typeof(K2Bridge).GetMethod(nameof(SetCvar)));
        lua.RegisterFunction("MockVfsFileExists", typeof(K2Bridge).GetMethod(nameof(VfsFileExists)));
        lua.RegisterFunction("MockVfsReadFileText", typeof(K2Bridge).GetMethod(nameof(VfsReadFileText)));
        lua.RegisterFunction("MockVfsGetFileSize", typeof(K2Bridge).GetMethod(nameof(VfsGetFileSize)));
        lua.RegisterFunction("MockVfsReadFileHeaderHex", typeof(K2Bridge).GetMethod(nameof(VfsReadFileHeaderHex)));
        lua.RegisterFunction("MockReportGlobalLeak", typeof(K2Bridge).GetMethod(nameof(ReportGlobalLeak)));
        lua.RegisterFunction("MockResolveScriptPath", typeof(K2Bridge).GetMethod(nameof(ResolveScriptPath)));

        // Lua bridge for Cvar, UIManager, VFS helpers, and global tables from k2-field-test
        lua.DoString(@"
            runfile = function(p)
                local resolved = MockResolveScriptPath(tostring(p or ''))
                if resolved and resolved ~= '' then
                    dofile(resolved)
                end
            end

            Cvar = Cvar or {
                Get = function(n) return MockGetCvarString(tostring(n)) end,
                GetString = function(n) return MockGetCvarString(tostring(n)) end,
                GetBoolean = function(n) return MockGetCvarBool(tostring(n)) end,
                GetInteger = function(n) return MockGetCvarInt(tostring(n)) end,
                Set = function(n, v) MockSetCvar(tostring(n), tostring(v)) end,
                Find = function(n) return true end,
                CreateCvar = function(n, t, v) MockSetCvar(tostring(n), tostring(v)) end
            }

            UIManager = UIManager or {
                GetInterface = function(n) return GetWidget(n) end
            }

            -- Wire up engine cvar functions to backing store
            GetCvarString = function(n) return MockGetCvarString(tostring(n)) end
            GetCvarBool = function(n) return MockGetCvarBool(tostring(n)) end
            GetCvarInt = function(n) return MockGetCvarInt(tostring(n)) end
            Set = function(n, v) MockSetCvar(tostring(n), tostring(v)) end

            -- VFS file & .s2z archive helpers
            VfsFileExists = function(p) return MockVfsFileExists(tostring(p or '')) end
            VfsReadFileText = function(p) return MockVfsReadFileText(tostring(p or '')) end
            VfsGetFileSize = function(p) return MockVfsGetFileSize(tostring(p or '')) end
            VfsReadFileHeaderHex = function(p, n) return MockVfsReadFileHeaderHex(tostring(p or ''), tonumber(n) or 18) end
            ArchiveReadFile = ArchiveReadFile or VfsReadFileText
            Testing = Testing or {}
            Testing.FileExists = Testing.FileExists or VfsFileExists

            -- Global subsystems from k2-field-test
            HoN_Matchmaking = HoN_Matchmaking or {}
            Player_Stats = Player_Stats or {}
            Watch_System = Watch_System or {}
            Game_Lobby = Game_Lobby or {}
            TestSuite = TestSuite or { ClientAccessible = function() return false end }
            PlayerHosted = PlayerHosted or {}
            Match_Stats = Match_Stats or {}

            function IsPreseason() return false end
            function isNewUI() return false end
            function RegisterScript(n, f) end
            function RegisterScript2(n, f) end

            -- Lua 5.1 Standard Library Compatibility Aliases (K2 uses Lua 5.1)
            unpack = unpack or table.unpack
            loadstring = loadstring or load
            math.mod = math.mod or math.fmod
            string.gfind = string.gfind or string.gmatch
            getfenv = getfenv or function(_) return _G end
            setfenv = setfenv or function(f, _) return f end
        ");

        // Install lazy-failing Lua 5.2+/5.4 stdlib poison stubs & Tarantool-inspired _G.__newindex guard AFTER setup
        string strictFlagLua = strictGlobals ? "true" : "false";
        lua.DoString($$"""
            do
                local function k2_incompat(api, hint)
                    return function(...)
                        error("[K2-LUA51-INCOMPAT] '" .. api .. "' does not exist in K2 Lua 5.1 and will crash in-game. Remediation: " .. hint, 2)
                    end
                end

                table.pack = k2_incompat("table.pack", "Use '{ ... }' and 'select(\"#\", ...)' instead.")
                table.move = k2_incompat("table.move", "Use an explicit numeric 'for' loop instead.")
                math.tointeger = k2_incompat("math.tointeger", "Use 'math.floor(tonumber(x))' instead.")
                math.type = k2_incompat("math.type", "Use 'type(x) == \"number\"' instead.")
                math.ult = k2_incompat("math.ult", "Compare non-negative numbers directly in Lua 5.1.")
                string.pack = k2_incompat("string.pack", "Binary string.pack is Lua 5.3+; encode bytes manually.")
                string.unpack = k2_incompat("string.unpack", "Binary string.unpack is Lua 5.3+; use string.byte instead.")
                string.packsize = k2_incompat("string.packsize", "Binary string.packsize is Lua 5.3+.")

                local utf8_stub = {}
                setmetatable(utf8_stub, {
                    __index = function(_, k)
                        error("[K2-LUA51-INCOMPAT] 'utf8." .. tostring(k) .. "' does not exist in K2 Lua 5.1. Remediation: use string.byte/string.sub UTF-8 helpers.", 2)
                    end,
                    __newindex = function(_, k, _)
                        error("[K2-LUA51-INCOMPAT] 'utf8." .. tostring(k) .. "' does not exist in K2 Lua 5.1.", 2)
                    end
                })
                rawset(_G, "utf8", utf8_stub)

                if rawget(_G, "__k2_guard_installed") then
                    return
                end
                rawset(_G, "__k2_guard_installed", true)
                local state_warnings = {}
                rawset(_G, "__k2_global_warnings", state_warnings)

                local declared = {
                    _matchList = true,
                    _seasonStatsNormal = true,
                    _seasonStatsCasual = true,
                    base64Payload = true,
                    luanet = true,
                    CLRPackage = true,
                    import = true,
                    thread_idx = true,
                    unpack = true,
                    __k2_guard_installed = true,
                    __k2_global_warnings = true
                }
                for k, _ in pairs(_G) do
                    declared[k] = true
                end

                local strict_mode = {{strictFlagLua}}
                local guard_mt = {}
                local orig_newindex = nil
                local orig_index = nil
                local in_clr_lookup = false

                local function sync_metatable()
                    local cur_mt = getmetatable(_G)
                    if cur_mt == guard_mt then
                        return
                    end
                    if type(cur_mt) == "table" then
                        for mk, mv in pairs(cur_mt) do
                            if mk ~= "__newindex" and mk ~= "__index" then
                                guard_mt[mk] = mv
                            end
                        end
                        if cur_mt.__newindex ~= nil then
                            orig_newindex = cur_mt.__newindex
                        end
                        if cur_mt.__index ~= nil then
                            orig_index = cur_mt.__index
                            guard_mt.__index = function(t, k)
                                if type(orig_index) == "function" then
                                    in_clr_lookup = true
                                    local ok, res = pcall(orig_index, t, k)
                                    in_clr_lookup = false
                                    if not ok then error(res, 2) end
                                    return res
                                elseif type(orig_index) == "table" then
                                    return orig_index[k]
                                end
                                return nil
                            end
                        end
                    end
                    setmetatable(_G, guard_mt)
                end

                guard_mt.__newindex = function(t, k, v)
                    if type(k) == "string" and not declared[k] then
                        local is_bridge = in_clr_lookup or (string.sub(k, 1, 4) == "Mock") or (string.sub(k, 1, 4) == "_CS_") or (string.sub(k, 1, 6) == "luanet")
                        local info = debug.getinfo(2, "Sl")
                        local what = info and info.what or "C"
                        if not is_bridge and what ~= "C" then
                            local is_pascal_module = (what == "main") and (string.match(k, "^[A-Z]") ~= nil)
                            if not is_pascal_module then
                                local src = info and info.short_src or "<lua>"
                                local ln = info and info.currentline or 0
                                local warn_msg = MockReportGlobalLeak(k, src, ln)
                                state_warnings[#state_warnings + 1] = warn_msg
                                if strict_mode then
                                    error("[K2-GLOBAL-STRICT] Undeclared global write '" .. k .. "' at " .. src .. ":" .. tostring(ln) .. ". Remediation: prefix with 'local '.", 2)
                                end
                            end
                        end
                        declared[k] = true
                    end
                    if type(orig_newindex) == "function" then
                        orig_newindex(t, k, v)
                    else
                        rawset(t, k, v)
                    end
                end

                if type(rawget(_G, "import")) == "function" then
                    local orig_import = rawget(_G, "import")
                    rawset(_G, "import", function(...)
                        local r1, r2, r3 = orig_import(...)
                        sync_metatable()
                        return r1, r2, r3
                    end)
                end

                sync_metatable()
            end
        """);
    }

    public static string GetCvarString(string name) => s_cvarManager.GetString(name);
    public static bool GetCvarBool(string name) => s_cvarManager.GetBool(name);
    public static int GetCvarInt(string name) => s_cvarManager.GetInt(name);
    public static void SetCvar(string name, object? value = null) => s_cvarManager.Set(name, value?.ToString() ?? string.Empty);

    private static string? ResolveLooseFilePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        string clean = rawPath.Trim().TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrEmpty(clean)) return null;

        string[] candidates =
        [
            clean,
            $"games/client/override/{clean}",
            $"games/client/game/{clean}",
            $"games/client/ClientPackageFiles/{clean}",
            $"games/client/{clean}",
            $"override/{clean}",
            $"game/{clean}"
        ];

        foreach (string cand in candidates)
        {
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    public static string ResolveScriptPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string? loose = ResolveLooseFilePath(path);
        if (loose != null) return Path.GetFullPath(loose);

        string fileName = Path.GetFileName(path);
        string[] candidates =
        [
            $"games/client/override/bots/{fileName}",
            $"games/client/game/bots/{fileName}",
            $"games/client/override/{path}",
            $"games/client/game/{path}"
        ];
        foreach (string cand in candidates)
        {
            if (File.Exists(cand)) return Path.GetFullPath(cand);
        }
        return string.Empty;
    }

    public static void InitializeBotEnvironment(NLua.Lua lua)
    {
        lua.DoString(@"
            object = object or {}
            object.core = object.core or {}
            object.eventsLib = object.eventsLib or {}
            object.behaviorLib = object.behaviorLib or { tHTNDomain = { Tasks = {}, Operators = {}, Methods = {} } }
            object.metadata = object.metadata or {}
            object.illusionLib = object.illusionLib or {}
            object.guardrails = object.guardrails or {}
            object.rlInference = object.rlInference or {}
            object.soccerBotBrain = object.soccerBotBrain or {}

            core = object.core
            core.BotEcho = core.BotEcho or function(...) end
            core.VerboseLog = core.VerboseLog or function(...) end
            core.BotLog = core.BotLog or function(...) end
            core.SToMS = function(s) return (s or 0) * 1000 end
            core.MSToS = function(ms) return (ms or 0) / 1000 end
            core.MinToMS = function(m) return (m or 0) * 60 * 1000 end
            core.MSToMin = function(ms) return (ms or 0) / (60 * 1000) end
            core.RadToDeg = function(r) return (r or 0) * (180 / math.pi) end
            core.DegToRad = function(d) return (d or 0) * (math.pi / 180) end
            object.logger = { bWriteLog = false, bVerboseLog = false }
            object.myName = ""TestBot""
            Log = Log or function(...) end

            Echo = Echo or print
            Vector3 = Vector3 or {
                Create = function(x, y, z)
                    local v = { x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or 0 }
                    v.Length = function(self) return math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z) end
                    v.Normalize = function(self)
                        local l = self:Length()
                        if l < 1e-6 then return Vector3.Create(0,0,0) end
                        return Vector3.Create(self.x / l, self.y / l, self.z / l)
                    end
                    return v
                end
            }
            HoN = HoN or {}
            HoN.GetGameTime = HoN.GetGameTime or function() return 0 end
            HoN.GetMatchTime = HoN.GetMatchTime or function() return 0 end
        ");

        string coreBotPath = ResolveScriptPath("bots/botbraincore.lua");
        if (!string.IsNullOrEmpty(coreBotPath) && File.Exists(coreBotPath))
        {
            lua.DoFile(coreBotPath);
        }
    }

    private static ZipArchive OpenS2zReadShared(string s2zPath)
    {
        FileStream fs = new(s2zPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static HashSet<string> EnsureVfsIndexLoaded()
    {
        if (s_vfsEntries is not null) return s_vfsEntries;
        lock (s_vfsLock)
        {
            if (s_vfsEntries is not null) return s_vfsEntries;
            HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);
            string gameDir = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "game");
            if (Directory.Exists(gameDir))
            {
                string[] s2zFiles = Directory.GetFiles(gameDir, "resources*.s2z")
                    .OrderByDescending(f =>
                    {
                        Match m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"resources(\d+)", RegexOptions.IgnoreCase);
                        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : -1;
                    })
                    .ToArray();
                s_orderedS2zPaths = s2zFiles;
                foreach (string s2z in s2zFiles)
                {
                    try
                    {
                        using ZipArchive zip = OpenS2zReadShared(s2z);
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            if (entry.Length > 0 && !entry.FullName.EndsWith('/'))
                            {
                                string norm = "/" + entry.FullName.Replace('\\', '/').TrimStart('/');
                                entries.Add(norm);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[LuaVerifier][VFS] Archive index warning ({s2z}): {ex.Message}");
                    }
                }
            }
            else
            {
                s_orderedS2zPaths = [];
            }
            s_vfsEntries = entries;
            return s_vfsEntries;
        }
    }

    public static bool VfsFileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (ResolveLooseFilePath(path) is not null) return true;
        string clean = NormalizeVfsEntryPath(path);
        return EnsureVfsIndexLoaded().Contains(clean);
    }

    private static string NormalizeVfsEntryPath(string rawPath)
    {
        string clean = rawPath.Trim().TrimStart('/', '\\').Replace('\\', '/');
        string[] prefixes = ["games/client/override/", "games/client/game/", "games/client/ClientPackageFiles/", "games/client/", "override/", "game/"];
        foreach (string prefix in prefixes)
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[prefix.Length..];
                break;
            }
        }
        return "/" + clean.TrimStart('/');
    }

    public static string? VfsReadFileText(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string? loose = ResolveLooseFilePath(path);
        if (loose is not null)
        {
            return File.ReadAllText(loose);
        }

        string clean = NormalizeVfsEntryPath(path);
        if (!EnsureVfsIndexLoaded().Contains(clean) || s_orderedS2zPaths is null) return null;

        string entryTarget = clean.TrimStart('/');
        foreach (string s2z in s_orderedS2zPaths)
        {
            try
            {
                using ZipArchive zip = OpenS2zReadShared(s2z);
                ZipArchiveEntry? match = zip.Entries.FirstOrDefault(e =>
                    e.Length > 0 && string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), entryTarget, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    using StreamReader sr = new(match.Open());
                    return sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LuaVerifier][VFS] Archive read warning ({s2z}): {ex.Message}");
            }
        }
        return null;
    }

    public static long VfsGetFileSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        string? loose = ResolveLooseFilePath(path);
        if (loose is not null)
        {
            return new FileInfo(loose).Length;
        }

        string clean = NormalizeVfsEntryPath(path);
        if (!EnsureVfsIndexLoaded().Contains(clean) || s_orderedS2zPaths is null) return -1;

        string entryTarget = clean.TrimStart('/');
        foreach (string s2z in s_orderedS2zPaths)
        {
            try
            {
                using ZipArchive zip = OpenS2zReadShared(s2z);
                ZipArchiveEntry? match = zip.Entries.FirstOrDefault(e =>
                    e.Length > 0 && string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), entryTarget, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match.Length;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LuaVerifier][VFS] Archive size warning ({s2z}): {ex.Message}");
            }
        }
        return -1;
    }

    public static string? VfsReadFileHeaderHex(string path, int byteCount = 18)
    {
        if (string.IsNullOrWhiteSpace(path) || byteCount <= 0) return null;
        string? loose = ResolveLooseFilePath(path);
        if (loose is not null)
        {
            using FileStream fs = new(loose, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buf = new byte[byteCount];
            int read = fs.Read(buf, 0, byteCount);
            return Convert.ToHexString(buf, 0, read);
        }

        string clean = NormalizeVfsEntryPath(path);
        if (!EnsureVfsIndexLoaded().Contains(clean) || s_orderedS2zPaths is null) return null;

        string entryTarget = clean.TrimStart('/');
        foreach (string s2z in s_orderedS2zPaths)
        {
            try
            {
                using ZipArchive zip = OpenS2zReadShared(s2z);
                ZipArchiveEntry? match = zip.Entries.FirstOrDefault(e =>
                    e.Length > 0 && string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), entryTarget, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    using Stream stream = match.Open();
                    byte[] buf = new byte[byteCount];
                    int total = 0;
                    while (total < byteCount)
                    {
                        int r = stream.Read(buf, total, byteCount - total);
                        if (r <= 0) break;
                        total += r;
                    }
                    return Convert.ToHexString(buf, 0, total);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LuaVerifier][VFS] Archive header warning ({s2z}): {ex.Message}");
            }
        }
        return null;
    }

    public static void Reset()
    {
        s_cvarManager.Reset();
        lock (s_warnLock)
        {
            s_globalWarnings.Clear();
        }
    }
}

