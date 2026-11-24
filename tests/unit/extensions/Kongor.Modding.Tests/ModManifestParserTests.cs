// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Parsing;
using Xunit;

namespace Kongor.Modding.Tests;

public class ModManifestParserTests
{
    private const string SampleModXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<modification 
    application=""Heroes of Newerth"" 
    appversion=""*"" 
    mmversion=""1.3""
    name=""AOE RI Complete Rework"" 
    version=""1.6.4"" 
    date=""2026-08-28""
    description=""Complete rework of AOE range indicators""
    author=""He, NogiOsminogi""
    weblink=""https://forums.heroesofnewerth.com""
    icon=""icon.png""
    category=""Combat UI"">
    
    <requirement name=""CoreUI"" version="">=1.0.0"" />
    <incompatibility name=""OldAOEMod"" version=""1.0-2.0"" />
    <applybefore name=""SwiftPicker"" />
    <applyafter name=""BaseUI"" />

    <copyfile name=""ui/aoe/indicator.png"" source=""res/indicator.png"" overwrite=""yes"" condition=""not 'DisableAOE'"" />

    <editfile name=""ui/fe2/main.interface"" condition=""'BaseUI'"">
        <find position=""before""><![CDATA[<panel name=""chat_box""]]></find>
        <findup position=""after""><![CDATA[<interface>]]></findup>
        <findall><![CDATA[old_function()]]></findall>
        <insert position=""after"" source=""res/snippet.xml""><![CDATA[<include file=""ui/aoe/indicator.package"" />]]></insert>
        <replace><![CDATA[new_function()]]></replace>
        <delete />
    </editfile>

    <editstringtable name=""stringtables/game_messages_en.str"">
        <set key=""chat_aoe_enabled"" value=""AOE Indicators Enabled"" />
        <delete key=""chat_old_aoe"" />
    </editstringtable>
</modification>";

    [Fact]
    public void Parse_ValidManifest_PopulatesAllPropertiesCorrectly()
    {
        ModManifest manifest = ModManifestParser.Parse(SampleModXml);

        manifest.Name.Should().Be("AOE RI Complete Rework");
        manifest.Version.Should().Be("1.6.4");
        manifest.Author.Should().Be("He, NogiOsminogi");
        manifest.Category.Should().Be("Combat UI");
        manifest.AppVersion.Should().Be("*");
        manifest.MmVersion.Should().Be("1.3");
        manifest.Date.Should().Be("2026-08-28");
        manifest.Description.Should().Be("Complete rework of AOE range indicators");
        manifest.WebLink.Should().Be("https://forums.heroesofnewerth.com");
        manifest.Icon.Should().Be("icon.png");

        manifest.Requirements.Should().HaveCount(1);
        manifest.Requirements[0].Name.Should().Be("CoreUI");
        manifest.Requirements[0].VersionExpression.Should().Be(">=1.0.0");

        manifest.Incompatibilities.Should().HaveCount(1);
        manifest.Incompatibilities[0].Name.Should().Be("OldAOEMod");
        manifest.Incompatibilities[0].VersionExpression.Should().Be("1.0-2.0");

        manifest.ApplyBefore.Should().HaveCount(1);
        manifest.ApplyBefore[0].Name.Should().Be("SwiftPicker");

        manifest.ApplyAfter.Should().HaveCount(1);
        manifest.ApplyAfter[0].Name.Should().Be("BaseUI");

        manifest.FileCopies.Should().HaveCount(1);
        manifest.FileCopies[0].TargetName.Should().Be("ui/aoe/indicator.png");
        manifest.FileCopies[0].SourcePath.Should().Be("res/indicator.png");
        manifest.FileCopies[0].Overwrite.Should().Be(FileOverwriteMode.Yes);
        manifest.FileCopies[0].Condition.Should().Be("not 'DisableAOE'");

        manifest.FileEdits.Should().HaveCount(1);
        manifest.FileEdits[0].TargetName.Should().Be("ui/fe2/main.interface");
        manifest.FileEdits[0].Condition.Should().Be("'BaseUI'");
        manifest.FileEdits[0].Operations.Should().HaveCount(6);

        manifest.FileEdits[0].Operations[0].Should().BeOfType<FindOperation>()
            .Which.Position.Should().Be(FindPosition.Before);

        manifest.FileEdits[0].Operations[1].Should().BeOfType<FindUpOperation>()
            .Which.Position.Should().Be(FindPosition.After);

        manifest.FileEdits[0].Operations[2].Should().BeOfType<FindAllOperation>()
            .Which.Target.Should().Be("old_function()");

        manifest.FileEdits[0].Operations[3].Should().BeOfType<InsertOperation>()
            .Which.Position.Should().Be(InsertPosition.After);

        manifest.FileEdits[0].Operations[4].Should().BeOfType<ReplaceOperation>()
            .Which.Content.Should().Be("new_function()");

        manifest.FileEdits[0].Operations[5].Should().BeOfType<DeleteOperation>();

        manifest.StringtableEdits.Should().HaveCount(1);
        manifest.StringtableEdits[0].TargetName.Should().Be("stringtables/game_messages_en.str");
        manifest.StringtableEdits[0].Entries.Should().HaveCount(2);
        manifest.StringtableEdits[0].Entries[0].Key.Should().Be("chat_aoe_enabled");
        manifest.StringtableEdits[0].Entries[0].Value.Should().Be("AOE Indicators Enabled");
        manifest.StringtableEdits[0].Entries[0].Action.Should().Be(StringtableAction.Set);
        manifest.StringtableEdits[0].Entries[1].Key.Should().Be("chat_old_aoe");
        manifest.StringtableEdits[0].Entries[1].Action.Should().Be(StringtableAction.Delete);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesAllInformation()
    {
        ModManifest original = ModManifestParser.Parse(SampleModXml);
        string serializedXml = ModManifestParser.Serialize(original);
        ModManifest deserialized = ModManifestParser.Parse(serializedXml);

        deserialized.Name.Should().Be(original.Name);
        deserialized.Version.Should().Be(original.Version);
        deserialized.Author.Should().Be(original.Author);
        deserialized.Requirements.Should().BeEquivalentTo(original.Requirements);
        deserialized.Incompatibilities.Should().BeEquivalentTo(original.Incompatibilities);
        deserialized.ApplyBefore.Should().BeEquivalentTo(original.ApplyBefore);
        deserialized.ApplyAfter.Should().BeEquivalentTo(original.ApplyAfter);
        deserialized.FileCopies.Should().BeEquivalentTo(original.FileCopies);
        deserialized.FileEdits.Should().BeEquivalentTo(original.FileEdits);
        deserialized.StringtableEdits.Should().BeEquivalentTo(original.StringtableEdits);
    }

    [Fact]
    public void Parse_LegacyChildElements_ExtractsMetadataSuccessfully()
    {
        string legacyXml = @"<modification name=""LegacyMod"" version=""1.0"">
            <author>OldAuthor</author>
            <description>Legacy description in child element</description>
            <requirement>OtherMod [v2.0]</requirement>
        </modification>";

        ModManifest manifest = ModManifestParser.Parse(legacyXml);
        manifest.Name.Should().Be("LegacyMod");
        manifest.Author.Should().Be("OldAuthor");
        manifest.Description.Should().Be("Legacy description in child element");
        manifest.Requirements.Should().HaveCount(1);
        manifest.Requirements[0].Name.Should().Be("OtherMod");
        manifest.Requirements[0].VersionExpression.Should().Be("2.0");
    }

    [Fact]
    public void Parse_ManifestWithXmlEntities_DecodesEntitiesCorrectly()
    {
        string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<modification 
    name=""Tactical HUD &amp; Neutral Camp Overlays"" 
    version=""1.0.0"" 
    author=""ProjectKongor"" 
    description=""Tactical HUD providing neutral camp stack countdowns, pull windows (:45-:55) for 12 neutral camps, ally ultimate readiness trackers, and dynamic buyback status indicators."" 
    appversion=""*"" 
    mmversion=""1.3"" 
    icon=""icon.png"" 
    category=""UI &amp; HUD"">
  <copyfile name=""ui/tactical_hud.package"" source=""ui/tactical_hud.package"" />
  <copyfile name=""ui/scripts/tactical_hud.lua"" source=""ui/scripts/tactical_hud.lua"" />
  <editfile name=""ui/game_apex.interface"">
    <find><![CDATA[<lua file=""/ui/scripts/game_new.lua"" />]]></find>
    <insert position=""after""><![CDATA[
	<include file=""/ui/tactical_hud.package"" />
	<lua file=""/ui/scripts/tactical_hud.lua"" />]]></insert>
  </editfile>
</modification>";

        ModManifest manifest = ModManifestParser.Parse(xml);
        manifest.Name.Should().Be("Tactical HUD & Neutral Camp Overlays");
        manifest.Category.Should().Be("UI & HUD");
        manifest.Author.Should().Be("ProjectKongor");
        manifest.FileCopies.Should().HaveCount(2);
        manifest.FileEdits.Should().HaveCount(1);
    }
}
