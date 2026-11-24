// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Prebaking.Tests.Fixtures;

public static class SampleXmlData
{
    public const string ValidHeroesXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<heroes>
    <hero name=""Hero_Accursed"" displayName=""Accursed"" defaultIcon=""/heroes/accursed/icon.tga"" defaultModel=""hd_accursed/preview.mdf"" defaultEffect=""effects/body.effect"" category=""tank protective"" difficulty=""2.5"" primaryattribute=""strength"" strength=""23"" strengthperlevel=""2.7"" agility=""17"" agilityperlevel=""1.5"" intelligence=""19"" intelligenceperlevel=""1.8"" attacktype=""Melee"" attackdamagemin=""51"" attackdamagemax=""61"" attackrange=""128"" attackcooldown=""1700"" movespeed=""300"" armor=""2.38"" magicarmor=""5.5"" solorating=""4"" junglerating=""0"" carryrating=""2"" supportrating=""4"" initiatorrating=""3"" gankerrating=""1"" pusherrating=""1"" rangedrating=""0"" meleerating=""5"">
        <ability slot=""0"" name=""Ability_Accursed1"" displayName=""Cauterize"" icon=""/heroes/accursed/ability_01/icon.tga"" anim=""ability_1"" maxLevel=""4"" actionType=""target_entity"" manaCost=""50,60,70,80"" cooldown=""4500"" range=""800"" />
        <ability slot=""1"" name=""Ability_Accursed2"" displayName=""Shield"" icon=""/heroes/accursed/ability_02/icon.tga"" anim=""ability_2"" maxLevel=""4"" actionType=""target_entity"" manaCost=""100,105,110,115"" cooldown=""14000,12000,10000,8000"" range=""450"" />
        <altavatar key=""Hero_Accursed.Alt"" displayName=""Horseman Accursed"" path=""/alt"" iconPath=""/heroes/accursed/alt/icon.tga"" defaultModel=""preview.mdf"" defaultEffect=""effects/body.effect"" previewscale=""1.2"" />
        <altavatar key=""Hero_Accursed.Alt2"" displayName=""Shadow Accursed"" path=""/alt2"" iconPath=""/heroes/accursed/alt2/icon.tga"" defaultModel=""preview2.mdf"" defaultEffect=""effects/body2.effect"" previewscale=""1.0"" />
    </hero>
    <hero name=""Hero_Pyromancer"" displayName=""Pyromancer"" defaultIcon=""/heroes/pyromancer/icon.tga"" defaultModel=""preview.mdf"" defaultEffect=""effects/body.effect"" category=""ganker nuker"" difficulty=""3.0"" primaryattribute=""intelligence"" strength=""18"" strengthperlevel=""1.5"" agility=""16"" agilityperlevel=""1.5"" intelligence=""24"" intelligenceperlevel=""3.2"" attacktype=""Ranged"" attackdamagemin=""43"" attackdamagemax=""49"" attackrange=""600"" attackcooldown=""1700"" movespeed=""295"" armor=""1.24"" magicarmor=""5.5"">
        <ability slot=""0"" name=""Ability_Pyromancer1"" displayName=""Phoenix Wave"" icon=""/heroes/pyromancer/ability_01/icon.tga"" anim=""ability_1"" maxLevel=""4"" actionType=""target_position"" manaCost=""90,100,110,120"" cooldown=""8500"" range=""800"" />
        <altavatar key=""Hero_Pyromancer.Alt"" displayName=""Scorcher"" path=""/alt"" iconPath=""/heroes/pyromancer/alt/icon.tga"" defaultModel=""preview.mdf"" defaultEffect=""effects/body.effect"" previewscale=""1.1"" />
    </hero>
</heroes>";

    public const string ValidPreloadResXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<heroes_preloadres>
    <hero name=""Hero_Accursed"">
        <reslist>
            <file path=""/heroes/accursed/model.model"" />
            <file path=""/heroes/accursed/icon.tga"" />
            <file path=""/heroes/accursed/effects/body.effect"" />
            <file path=""/heroes/accursed/sounds/voice.wav"" />
        </reslist>
    </hero>
    <hero name=""Hero_Pyromancer"">
        <reslist>
            <file path=""/heroes/pyromancer/model.model"" />
            <file path=""/heroes/pyromancer/icon.tga"" />
            <file path=""/heroes/pyromancer/effects/body.effect"" />
        </reslist>
    </hero>
</heroes_preloadres>";

    public const string ValidPreviewXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<heroes_preview>
    <hero name=""Hero_Accursed"">
        <altavatar key=""Hero_Accursed.Alt"" type=""preview"" modelPath=""/heroes/accursed/alt/preview.mdf"" effectPath=""/heroes/accursed/alt/effects/body.effect"" modelScale=""1.2"" modelPosition=""0 0 -20"" modelAngle=""0 0 0"" ambient=""0.5 0.5 0.5"" sunHeight=""50"" sunAngle=""120"" sunColor=""1 1 1"" pedestalScale=""1.0"" usingEffect=""true"" newfeaturemodel=""false"" />
    </hero>
    <hero name=""Hero_Pyromancer"">
        <altavatar key=""Hero_Pyromancer.Alt"" type=""preview"" modelPath=""/heroes/pyromancer/alt/preview.mdf"" effectPath=""/heroes/pyromancer/alt/effects/body.effect"" modelScale=""1.1"" modelPosition=""0 0 0"" modelAngle=""0 0 0"" ambient=""0.6 0.6 0.6"" sunHeight=""60"" sunAngle=""90"" sunColor=""1 0.9 0.8"" pedestalScale=""1.0"" usingEffect=""true"" newfeaturemodel=""false"" />
    </hero>
</heroes_preview>";

    public const string ValidUpgradesXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<upgrades>
    <chatsymbol name=""afghanistan"" texture=""/ui/icons/flags/afghanistan.tga"" sortindex=""1"" />
    <accounticon name=""Default Icon"" texture=""/ui/icons/account/default.tga"" sortindex=""2"" />
    <ward name=""aluna_ward"" icon=""/ui/icons/wards/aluna.tga"" modifier=""W_Aluna"" sortindex=""3"" />
    <courier name=""penguin_courier"" icon=""/ui/icons/courier/penguin.tga"" modifier=""C_Penguin"" sortindex=""4"" />
    <taunt name=""baby_taunt"" icon=""/ui/icons/taunts/baby.tga"" voiceset=""baby"" sortindex=""5"" />
    <announcervoice name=""flamboyant"" icon=""/ui/icons/announcers/flamboyant.tga"" voiceset=""flamboyant"" arcadetext=""flamboyant"" sortindex=""6"" />
    <tpeffect name=""flame_tp"" icon=""/ui/icons/tp/flame.tga"" modifier=""TP_Flame"" sortindex=""7"" />
    <chatnamecolor name=""gold_color"" color=""#FFD700"" ingamecolor=""#FFD700"" sortindex=""8"" />
</upgrades>";

    public const string ValidMechanicsXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<gamemechanics>
    <attacktype name=""Melee"" deniedexpmultiplier=""0.5"" uphillmisschance=""0.0"" />
    <attacktype name=""Ranged"" deniedexpmultiplier=""0.5"" uphillmisschance=""0.25"" />
    <combattypes>
        <combattable name=""Hero"">
            <attackmultiplier target=""Hero"" value=""1.0"" />
            <attackmultiplier target=""Building"" value=""0.5"" />
            <spellmultiplier target=""Hero"" value=""1.0"" />
            <aggropriority target=""Hero"" value=""10"" />
            <attackpriority target=""Hero"" value=""5"" />
        </combattable>
    </combattypes>
    <popup name=""Critical"" color=""#FF0000"" message=""CRITICAL!"" value=""true"" duration=""1500"" fadetime=""500"" visibility=""all"" />
    <immunitytype name=""MagicImmunity"" immune=""Magic"" />
    <targetscheme name=""enemy_units"" allow=""enemy,unit"" />
    <armortype name=""Hero"" effects=""none"" factor=""0.06"" />
    <stealthtype name=""Invisibility"" />
    <revealtype name=""TrueSight"" reveal=""Invisibility"" />
    <effecttype name=""Magic"" />
    <herocategory name=""Carry"" />
</gamemechanics>";

    public const string ValidAbilityEntity = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<ability name=""Ability_TestStrike"" displayName=""Test Strike"" icon=""/heroes/test/ability_01/icon.tga"" anim=""ability_1"" casttime=""200"" castactiontime=""100"" maxlevel=""4"" actiontype=""target_entity"" targetscheme=""enemy_units"" casteffecttype=""Magic"" casteffect=""cast.effect"" manacost=""90,100,110,120"" cooldowntime=""6000"" range=""600"" areacastmaterial=""/materials/area.material"" />";

    public const string ValidStateEntity = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<state name=""State_TestBuff"" icon=""/heroes/test/ability_01/icon.tga"" passiveeffect=""effects/buff.effect"" effecttype=""StatusBuff"" allowtransfer=""false"" ishidden=""false"" statuseffecttooltip=""State_TestBuff"" modifierkey=""test_buff"" duration=""5000"" />";

    public const string ValidProjectileEntity = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<projectile name=""Projectile_TestArrow"" model=""arrow.mdf"" modelscale=""1.5"" effectscale=""1.0"" impacteffect=""effects/impact.effect"" traileffect=""effects/trail.effect"" deatheffect=""effects/death.effect"" speed=""1200"" maxspeed=""1500"" gravity=""0"" flying=""true"" flyheight=""100"" touchradius=""50"" touchtargetscheme=""enemy_units"" lifetime=""3000"" canturn=""false"" disjointable=""true"" homing=""false"" homingturnspeed=""0"" />";

    public const string ValidItemEntity = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<item name=""Item_TestBlade"" displayName=""Test Blade"" icon=""/items/test_blade/icon.tga"" cost=""1500"" actiontype=""target_self"" activatescheme=""can_activate"" casttime=""0"" castactiontime=""0"" cooldowntime=""10000"" cooldowntype=""test_blade"" casteffect=""cast.effect"" statuseffecttooltip=""Item_TestBlade"" allowsharing=""false"" noninterrupting=""true"" nobreakchannel=""true"" nonaggressive=""true"" />";
}
