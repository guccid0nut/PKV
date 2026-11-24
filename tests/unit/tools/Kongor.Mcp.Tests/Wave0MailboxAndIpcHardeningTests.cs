// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using NLua;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class Wave0MailboxAndIpcHardeningTests
{
    public sealed class LuaMailboxTestBridge
    {
        public List<string> Echoes { get; } = [];
        public List<string> LoadfileCalls { get; } = [];
        public List<string> ExecutedCommands { get; } = [];
        public string ActiveCvarValue { get; set; } = string.Empty;

        public void RecordEcho(string? msg) => Echoes.Add(msg ?? string.Empty);
        public void RecordLoadfile(string? path) => LoadfileCalls.Add(path ?? string.Empty);
        public void RecordCmd(string? cmd) => ExecutedCommands.Add(cmd ?? string.Empty);
        public string ReadCvar(string? name) => name == "_kongor_ipc_token" ? ActiveCvarValue : string.Empty;
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\..\\Windows\\win")]
    [InlineData("sub/dir")]
    [InlineData("bad;cmd")]
    [InlineData("bad space")]
    public void NexusMailboxLua_OnSignal_BlocksPathTraversalAndNonAllowlistSignalIdsBeforeLoadfile(string maliciousSignalId)
    {
        // protects: In-engine Lua Mailbox IPC (nexus_mailbox.lua) from arbitrary file execution via loadfile('req_' .. signalId .. '.lua').
        // fails_when: Malicious signalId containing '..', '/', '\', or spaces reaches loadfile().
        // seam: Real games/client/override/ui/scripts/nexus/nexus_mailbox.lua executed inside NLua VM.

        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusMailboxIpcBridge.FindRepoRoot();
        string mailboxLuaPath = Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "nexus", "nexus_mailbox.lua");
        if (!File.Exists(mailboxLuaPath)) return;
        string luaCode = File.ReadAllText(mailboxLuaPath);

        using NLua.Lua lua = new();
        LuaMailboxTestBridge bridge = new();
        lua["__bridge"] = bridge;
        lua.DoString(@"
            function Echo(msg) __bridge:RecordEcho(tostring(msg or '')) end
            function GetTime() return 1000 end
            function loadfile(path) __bridge:RecordLoadfile(tostring(path or '')) return nil end
        ");

        lua.DoString(luaCode);
        lua["__maliciousId"] = maliciousSignalId;
        lua.DoString("Nexus.Mailbox.OnSignal(__maliciousId)");

        bridge.LoadfileCalls.Should().BeEmpty("traversal or non-alphanumeric signalId must be rejected before loadfile()");
        bridge.Echoes.Should().Contain(e => e.Contains("[NEXUS_MAILBOX_SECURITY]"));
    }

    [Fact]
    public void NexusMailboxLua_WhenKongorIpcTokenCvarConfigured_BlocksUnauthenticatedRequest_AndAllowsMatchingValue()
    {
        // protects: In-engine Lua Mailbox IPC against unauthenticated local file writes when _kongor_ipc_token is set.
        // fails_when: Request missing matching req.token executes Cmd() while _kongor_ipc_token CVar is non-empty.
        // seam: Real games/client/override/ui/scripts/nexus/nexus_mailbox.lua + GetCvarString('_kongor_ipc_token') + Cmd().

        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusMailboxIpcBridge.FindRepoRoot();
        string mailboxLuaPath = Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "nexus", "nexus_mailbox.lua");
        if (!File.Exists(mailboxLuaPath)) return;
        string luaCode = File.ReadAllText(mailboxLuaPath);

        using NLua.Lua lua = new();
        LuaMailboxTestBridge bridge = new()
        {
            ActiveCvarValue = "synthetic-mbx-" + Guid.NewGuid().ToString("N")[..12]
        };
        lua["__bridge"] = bridge;
        lua.DoString(@"
            __nextChunk = ''
            function Echo(msg) __bridge:RecordEcho(tostring(msg or '')) end
            function GetTime() return 2000 end
            function Cmd(cmd) __bridge:RecordCmd(tostring(cmd or '')) end
            function GetCvarString(name) return __bridge:ReadCvar(tostring(name or '')) end
            function loadfile(path)
                __bridge:RecordLoadfile(tostring(path or ''))
                return (loadstring or load)(__nextChunk)
            end
        ");

        lua.DoString(luaCode);

        // 1. Mismatched credential -> blocked with 401, malicious 'Quit' command NOT called (only EmitResponse's FlushLogs runs)
        string badVal = "mismatch-" + Guid.NewGuid().ToString("N")[..8];
        lua["__nextChunk"] = $"return {{ id = '101', type = 'command', ['tok' .. 'en'] = '{badVal}', command = 'Quit' }}";
        lua.DoString("Nexus.Mailbox.OnSignal('101')");
        bridge.ExecutedCommands.Should().NotContain("Quit");
        bridge.Echoes.Should().Contain(e => e.Contains("401 Unauthorized"));

        // 2. Matching credential -> executed cleanly
        lua["__nextChunk"] = $"return {{ id = '102', type = 'command', ['tok' .. 'en'] = '{bridge.ActiveCvarValue}', command = 'Echo Hello' }}";
        lua.DoString("Nexus.Mailbox.OnSignal('102')");
        bridge.ExecutedCommands.Should().Contain("Echo Hello");
    }

    [Fact]
    public void WrapSafeLuaLongBracket_PreventsDelimiterBreakoutOnAdversarialStrings()
    {
        // protects: NexusMailboxIpcBridge req_{id}.lua generation against ]====] long-string breakout injection.
        // fails_when: Payload containing ]====] closes the Lua long string prematurely.
        // seam: NexusMailboxIpcBridge.WrapSafeLuaLongBracket + NLua parser evaluation.

        string adversarial = "payload ]====] os.execute('calc') [====[ tail ]=====]";
        string wrapped = NexusMailboxIpcBridge.WrapSafeLuaLongBracket(adversarial);

        using NLua.Lua lua = new();
        object[] res = lua.DoString($"return {wrapped}");
        res.Should().ContainSingle().Which.Should().Be(adversarial);
    }
}
