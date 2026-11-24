// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;
using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Patching;
using Xunit;

namespace Kongor.Modding.Tests;

public class PatchingTests
{
    [Fact]
    public async Task FuzzyStringPatcher_FindAndInsert_InsertsContentAtCursor()
    {
        string original = "<interface>\n  <panel name=\"chat_box\" />\n</interface>";
        PatchOperation[] ops =
        [
            new FindOperation("<panel name=\"chat_box\" />"),
            new InsertOperation("\n  <panel name=\"custom_hud\" />", Position: InsertPosition.After)
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Contain("<panel name=\"chat_box\" />\n  <panel name=\"custom_hud\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_FuzzyWhitespaceTolerance_MatchesRegardlessOfWhitespaceRuns()
    {
        string original = "<interface>\r\n\t\t<panel    name=\"chat_box\"   width=\"100%\"   />\r\n</interface>";
        PatchOperation[] ops =
        [
            new FindOperation("<panel name=\"chat_box\" width=\"100%\" />"),
            new ReplaceOperation("<panel name=\"chat_box_modified\" />")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Contain("<panel name=\"chat_box_modified\" />");
    }

    [Fact]
    public async Task FuzzyStringPatcher_FindAllOperations_ReplacesAndDeletesGlobally()
    {
        string original = "item_health_potion, item_mana_potion, item_health_potion";

        // Global replace
        PatchOperation[] ops1 =
        [
            new FindAllOperation("item_health_potion"),
            new ReplaceOperation("item_mega_potion")
        ];
        string patched1 = await FuzzyStringPatcher.ApplyPatchAsync(original, ops1);
        patched1.Should().Be("item_mega_potion, item_mana_potion, item_mega_potion");

        // Global insert before
        PatchOperation[] ops2 =
        [
            new FindAllOperation("item_mana_potion"),
            new InsertOperation("custom_", Position: InsertPosition.Before)
        ];
        string patched2 = await FuzzyStringPatcher.ApplyPatchAsync(original, ops2);
        patched2.Should().Be("item_health_potion, custom_item_mana_potion, item_health_potion");

        // Global delete
        PatchOperation[] ops3 =
        [
            new FindAllOperation("item_health_potion"),
            new DeleteOperation()
        ];
        string patched3 = await FuzzyStringPatcher.ApplyPatchAsync(original, ops3);
        patched3.Should().Be(", item_mana_potion, ");
    }

    [Fact]
    public async Task FuzzyStringPatcher_FindUp_SearchesBackwardCorrectly()
    {
        string original = "first_target middle second_target end";
        PatchOperation[] ops =
        [
            new FindOperation("second_target"),
            new FindUpOperation("first_target"),
            new ReplaceOperation("MODIFIED_FIRST")
        ];

        string patched = await FuzzyStringPatcher.ApplyPatchAsync(original, ops);
        patched.Should().Be("MODIFIED_FIRST middle second_target end");
    }

    [Fact]
    public void XmlAstPatcher_WhenInvoked_MatchesAndMutatesDomElements()
    {
        string xml = @"<interface>
  <panel name=""chat_box"" width=""100"">
    <label name=""chat_text"" text=""hello"" />
  </panel>
</interface>";

        XDocument doc = XmlAstPatcher.Parse(xml);

        // Find by name attribute
        XElement? panel = XmlAstPatcher.FindElement(doc, "panel[name=chat_box]");
        panel.Should().NotBeNull();

        // Mutate attribute
        XmlAstPatcher.SetAttribute(panel!, "width", "200");
        panel!.Attribute("width")?.Value.Should().Be("200");

        // Insert child
        XElement newLabel = new("label", new XAttribute("name", "status_label"), new XAttribute("text", "active"));
        XmlAstPatcher.InsertChild(panel, newLabel);
        panel.Elements("label").Should().HaveCount(2);

        // Remove element
        XElement? oldLabel = XmlAstPatcher.FindElement(panel, "label[name=chat_text]");
        oldLabel.Should().NotBeNull();
        XmlAstPatcher.RemoveElement(oldLabel!);
        panel.Elements("label").Should().HaveCount(1);
    }

    [Fact]
    public void StringtablePatcher_WhenInvoked_MergesUpdatesAndDeletions()
    {
        string originalStr = @"// Game localization
chat_all	All Chat
chat_team	Team Chat
chat_whisper	Whisper Message
";

        StringtableEntryOperation[] operations =
        [
            new("chat_team", "Allied Team Chat"),
            new("chat_clan", "Clan Chat"),
            new("chat_whisper", "", StringtableAction.Delete)
        ];

        string patched = StringtablePatcher.ApplyPatch(originalStr, operations);

        patched.Should().Contain("chat_team\tAllied Team Chat");
        patched.Should().Contain("chat_clan\tClan Chat");
        patched.Should().NotContain("chat_whisper");
        patched.Should().Contain("// Game localization");
    }

    [Fact]
    public void LuaScriptPatcher_WhenInvoked_InjectsHeaderAndHooksFunctions()
    {
        string lua = @"function InitLobby()
    print('Lobby loaded')
end";

        string withHeader = LuaScriptPatcher.InjectHeader(lua, "local custom_var = 123");
        withHeader.Should().Contain("local custom_var = 123");
        withHeader.Should().Contain("function InitLobby()");

        string withHook =
            LuaScriptPatcher.HookFunction(withHeader, "InitLobby", "function Hooked_InitLobby() print('Hook') end");
        withHook.Should().Contain("function Hooked_InitLobby()");
        withHook.IndexOf("function Hooked_InitLobby()").Should().BeGreaterThan(withHook.IndexOf("function InitLobby()"));

        string replaced =
            LuaScriptPatcher.ReplaceSymbol("GetLocalClient():GetPlayer()", "GetLocalClient", "client.GetLocalClient");
        replaced.Should().Be("client.GetLocalClient():GetPlayer()");
    }
}
