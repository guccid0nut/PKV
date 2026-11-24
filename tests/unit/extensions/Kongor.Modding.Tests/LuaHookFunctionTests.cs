// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Modding.Patching;
using NLua;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Behavioural tests for <see cref="LuaScriptPatcher.HookFunction" />: the patched script is EXECUTED with NLua to
///     prove the wrapper really intercepts calls (including references captured later in the same file).
/// </summary>
public sealed class LuaHookFunctionTests
{
    private const string Wrapper = """
                                   local _orig_OnChat = OnChat
                                   function OnChat(msg)
                                     calls[#calls + 1] = "hook:" .. msg
                                     return _orig_OnChat(msg)
                                   end
                                   """;

    private static object[] Run(string script, string resultExpression)
    {
        using Lua lua = new();
        lua.DoString("calls = {}");
        lua.DoString(script);
        return lua.DoString($"return {resultExpression}");
    }

    [Fact]
    public void HookFunction_InsertsAfterMatchingEnd_SkippingNestedBlocksStringsAndComments()
    {
        string lua = """
                     function OnChat(msg)
                       if msg then
                         for i = 1, 1 do local x = i end
                         while false do end
                         repeat local y = 1 until true
                       end
                       local s = "end" -- end in a comment
                       local t = [[ function end ]]
                       --[==[ end end end ]==]
                       calls[#calls + 1] = "orig:" .. msg
                     end
                     registered = OnChat
                     """;

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", Wrapper);

        int hookIndex = patched.IndexOf("-- [Kongor Hook: OnChat]", StringComparison.Ordinal);
        hookIndex.Should().BeGreaterThan(patched.IndexOf("calls[#calls + 1] = \"orig:\"", StringComparison.Ordinal));
        hookIndex.Should().BeLessThan(patched.IndexOf("registered = OnChat", StringComparison.Ordinal),
            "the hook must be in place before later code captures the function by reference");
        patched.Should().NotContain("Kongor Hook Fallback");

        // Calling the reference captured AFTER the definition goes through the hook, and the original still runs.
        object[] result = Run(patched + "\nregistered('hi')", "table.concat(calls, ',')");
        result[0].Should().Be("hook:hi,orig:hi");
    }

    [Fact]
    public void HookFunction_SupportsLocalFunctionDeclarations()
    {
        string lua = """
                     local function OnChat(msg)
                       calls[#calls + 1] = "orig:" .. msg
                     end
                     OnChat('a')
                     """;

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", Wrapper);

        patched.Should().NotContain("Kongor Hook Fallback");
        object[] result = Run(patched, "table.concat(calls, ',')");
        result[0].Should().Be("hook:a,orig:a");
    }

    [Fact]
    public void HookFunction_SupportsAssignedFunctionExpressions()
    {
        string lua = """
                     OnChat = function(msg)
                       calls[#calls + 1] = "orig:" .. msg
                     end
                     OnChat('b')
                     """;

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", Wrapper);

        patched.Should().NotContain("Kongor Hook Fallback");
        object[] result = Run(patched, "table.concat(calls, ',')");
        result[0].Should().Be("hook:b,orig:b");
    }

    [Fact]
    public void HookFunction_WhenDeclarationMissing_AppendsMarkedFallbackAtEndOfFile()
    {
        string lua = "print('no target here')";

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", "-- wrapper");

        patched.Should().StartWith(lua);
        patched.Should().Contain("-- [Kongor Hook Fallback: OnChat (declaration not found); appended at end of file]");
        patched.TrimEnd().Should().EndWith("-- [End Hook: OnChat]");
    }

    [Fact]
    public void HookFunction_DoesNotMatchSimilarlyNamedFunctions()
    {
        string lua = """
                     function OnChatExtra(msg) end
                     function OnChat(msg)
                       calls[#calls + 1] = "orig:" .. msg
                     end
                     """;

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", Wrapper);

        patched.IndexOf("-- [Kongor Hook: OnChat]", StringComparison.Ordinal)
            .Should().BeGreaterThan(patched.IndexOf("function OnChat(msg)", StringComparison.Ordinal));
        object[] result = Run(patched + "\nOnChat('c')", "table.concat(calls, ',')");
        result[0].Should().Be("hook:c,orig:c");
    }

    [Theory]
    [InlineData("function OnChat(msg) if msg then return msg", "unclosed 'if' block")]
    [InlineData("function OnChat(msg) return msg end end", "unexpected 'end' statement")]
    [InlineData("function OnChat(msg) if msg != nil then return msg end end", "invalid operator '!='")]
    [InlineData("function OnChat(msg) local s = \"unterminated\nreturn msg end", "unterminated string literal")]
    [InlineData("function OnChat(msg) local t = { 1, 2 ) return msg end", "mismatched closing delimiter")]
    [InlineData("function OnChat(msg) local x = ; return msg end", "[luac51]")]
    public void HookFunction_WhenWrapperHasInvalidLuaSyntax_RejectsHookAndPreservesExecutableOriginalScript(
        string brokenWrapper,
        string expectedReasonFragment)
    {
        string lua = """
                     function OnChat(msg)
                       calls[#calls + 1] = "orig:" .. msg
                     end
                     """;

        string patched = LuaScriptPatcher.HookFunction(lua, "OnChat", brokenWrapper);

        patched.Should().Contain("-- [Kongor Hook Rejected: OnChat");
        patched.Should().Contain(expectedReasonFragment);
        patched.Should().NotContain("-- [Kongor Hook: OnChat]");

        // Prove the rejected script still compiles and executes cleanly in NLua!
        object[] result = Run(patched + "\nOnChat('safe')", "table.concat(calls, ',')");
        result[0].Should().Be("orig:safe");
    }
}
