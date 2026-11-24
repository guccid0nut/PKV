// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Tooling.Lua;
using FluentAssertions;
using LuaVerifier;
using NLua;
using NLua.Exceptions;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Value-Weighted Verification Suite for K2 Lua 5.1 Sandbox Parity &amp; Tarantool-style _G.__newindex Guard:
/// - protects: LuaVerifier runtime parity with K2 Engine's embedded Lua 5.1 VM and CLR LoadCLRPackage() interop.
/// - fails_when: NLua (Lua 5.4) silently executes Lua 5.2+/5.4-only APIs (utf8, table.pack, math.tointeger, string.pack)
///   that crash hon_x64.exe in-game, or misses undeclared lowercase/function-scoped global leaks.
/// - seam: Real NLua.Lua VM + LuaStatePooledObjectPolicy + K2Bridge.InitializeEnvironment (no mocks).
/// </summary>
public sealed class K2Lua51SandboxTests
{
    private static NLua.Lua CreateSandbox(bool strictGlobals = false)
    {
        LuaPoolOptions options = new()
        {
            CustomInitializer = l => K2Bridge.InitializeEnvironment(l, strictGlobals)
        };
        LuaStatePooledObjectPolicy policy = new(options);
        return policy.Create();
    }

    [Fact]
    public void Lua51Aliases_UnpackMathModStringGfind_ExecuteCleanlyWithoutGlobalWarnings()
    {
        K2Bridge.Reset();
        using NLua.Lua lua = CreateSandbox(strictGlobals: true);

        object[] result = lua.DoString(@"
            local a, b = unpack({ 10, 20 })
            local rem = math.mod(17, 5)
            local words = {}
            for w in string.gfind('kongor lua51 parity', '%w+') do
                words[#words + 1] = w
            end
            local env = getfenv(1)
            return a + b + rem, #words, (env == _G)
        ");

        result[0].Should().Be(32L);
        result[1].Should().Be(3L);
        result[2].Should().Be(true);
        K2Bridge.GetGlobalWarnings(lua).Should().BeEmpty();
    }

    [Theory]
    [InlineData("return utf8.len('hello')", "utf8.len")]
    [InlineData("return table.pack(1, 2, 3)", "table.pack")]
    [InlineData("return table.move({1,2}, 1, 2, 1, {})", "table.move")]
    [InlineData("return math.tointeger(42.0)", "math.tointeger")]
    [InlineData("return math.type(42)", "math.type")]
    [InlineData("return math.ult(1, 2)", "math.ult")]
    [InlineData("return string.pack('I4', 7)", "string.pack")]
    [InlineData("return string.unpack('I4', 'abcd')", "string.unpack")]
    [InlineData("return string.packsize('I4')", "string.packsize")]
    public void Lua54OnlyStdlibApis_ThrowActionableK2Lua51IncompatError(string luaSnippet, string expectedApiName)
    {
        using NLua.Lua lua = CreateSandbox(strictGlobals: false);

        Action act = () => lua.DoString(luaSnippet);

        LuaScriptException ex = act.Should().Throw<LuaScriptException>().Which;
        ex.Message.Should().Contain("[K2-LUA51-INCOMPAT]");
        ex.Message.Should().Contain(expectedApiName);
        ex.Message.Should().Contain("Remediation:");
    }

    [Fact]
    public void UndeclaredGlobalGuard_WarnMode_RecordsWarningForLeakWhileAllowingPascalModuleAndClrImport()
    {
        K2Bridge.Reset();
        using NLua.Lua lua = CreateSandbox(strictGlobals: false);

        // 1. Top-level PascalCase K2 UI module table + local variables + NLua CLR import/lookup must NOT trigger warnings
        lua.DoString(@"
            import('System')
            local clrGuidEmpty = tostring(Guid.Empty)
            MyCustomHudModule = MyCustomHudModule or {}
            local safeLocalCounter = 99
            function MyCustomHudModule:Tick()
                accidental_frame_leak = safeLocalCounter + 1
            end
            MyCustomHudModule:Tick()
        ");

        IReadOnlyList<string> warnings = K2Bridge.GetGlobalWarnings(lua);
        warnings.Should().ContainSingle(w => w.Contains("accidental_frame_leak"));
        warnings.Should().NotContain(w => w.Contains("MyCustomHudModule"));
        warnings.Should().NotContain(w => w.Contains("Guid"));
        lua["accidental_frame_leak"].Should().Be(100L);
    }

    [Fact]
    public void UndeclaredGlobalGuard_ParallelStates_IsolatesPerStateWarningsWithoutMixing()
    {
        Parallel.For(0, 8, idx =>
        {
            using NLua.Lua lua = CreateSandbox(strictGlobals: false);
            string uniqueVar = $"parallel_leak_{idx}";
            lua.DoString($@"
                function TriggerLeak()
                    {uniqueVar} = {idx}
                end
                TriggerLeak()
            ");

            IReadOnlyList<string> stateWarnings = K2Bridge.GetGlobalWarnings(lua);
            stateWarnings.Should().ContainSingle(w => w.Contains(uniqueVar));
        });
    }

    [Fact]
    public void UndeclaredGlobalGuard_StrictMode_ThrowsActionableErrorOnFunctionScopedGlobalLeak()
    {
        K2Bridge.Reset();
        using NLua.Lua lua = CreateSandbox(strictGlobals: true);

        Action act = () => lua.DoString(@"
            StoreVaultModule = {}
            function StoreVaultModule:Refresh()
                typo_item_idx = 7
            end
            StoreVaultModule:Refresh()
        ");

        LuaScriptException ex = act.Should().Throw<LuaScriptException>().Which;
        ex.Message.Should().Contain("[K2-GLOBAL-STRICT]");
        ex.Message.Should().Contain("typo_item_idx");
        ex.Message.Should().Contain("Remediation: prefix with 'local '");
    }
}
