// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;

namespace ASPIRE.Common.Utilities;

/// <summary>
///     Provides bidirectional mapping between Web Portal item slugs / display names
///     and the internal HoN game engine entity names (e.g. Item_Morph, Item_RunesOfTheBlight, Item_PushStaff).
/// </summary>
public static class HonItemMapping
{
    private static readonly FrozenDictionary<string, string> WebToEngineMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Consumables & Basics
            { "blightstones", "Item_RunesOfTheBlight" },
            { "runesoftheblight", "Item_RunesOfTheBlight" },
            { "healthpotion", "Item_HealthPotion" },
            { "manapotion", "Item_ManaPotion" },
            { "goldenapple", "Item_GoldenApple" },
            { "goldapple", "Item_GoldenApple" },
            { "wardofsight", "Item_FlamingEye" },
            { "flamingeye", "Item_FlamingEye" },
            { "wardofrevelation", "Item_ManaEye" },
            { "manaeye", "Item_ManaEye" },
            { "boundeye", "Item_BoundEye" },
            { "homecomingstone", "Item_HomecomingStone" },
            { "bottle", "Item_Bottle" },
            { "powersupply", "Item_PowerSupply" },
            { "manabattery", "Item_ManaBattery" },
            { "somazsynchronizer", "Item_SomazsSynchronizer" },
            { "somazssynchronizer", "Item_SomazsSynchronizer" },

            // Attributes & Components
            { "minortotem", "Item_MinorTotem" },
            { "crushingclaws", "Item_CrushingClaws" },
            { "markofthenovice", "Item_MarkOfTheNovice" },
            { "duckboots", "Item_DuckBoots" },
            { "pretenderscrown", "Item_PretendersCrown" },
            { "scarab", "Item_Scarab" },
            { "soulscream", "Item_Soulscream" },
            { "apprenticesrobe", "Item_ApprenticesRobe" },
            { "broadsword", "Item_Broadsword" },
            { "punchdagger", "Item_Punchdagger" },
            { "quickblade", "Item_Quickblade" },
            { "lifetube", "Item_Lifetube" },
            { "glowstone", "Item_Glowstone" },
            { "mysticvestments", "Item_MysticVestments" },
            { "axeofthemalphai", "Item_AxeOfTheMalphai" },
            { "brutalizer", "Item_Brutalizer" },
            { "spikedbola", "Item_SpikedBola" },
            { "steamstaff", "Item_Steamstaff" },
            { "trinketofrestoration", "Item_TrinketOfRestoration" },
            { "windwhistle", "Item_WindWhistle" },
            { "loggershatchet", "Item_LoggersHatchet" },
            { "ironbuckler", "Item_IronBuckler" },
            { "ironshield", "Item_IronShield" },
            { "guardianring", "Item_GuardianRing" },

            // Boots
            { "marchers", "Item_Marchers" },
            { "steamboots", "Item_Steamboots" },
            { "striders", "Item_Striders" },
            { "platedgreaves", "Item_PlatedGreaves" },
            { "ghostmarchers", "Item_Fleetfeet" },
            { "fleetfeet", "Item_Fleetfeet" },
            { "posthaste", "Item_PostHaste" },
            { "arcaneboots", "Item_ArcaneBoots" },
            { "pegasusboots", "Item_PegasusBoots" },

            // Mid-Game & Support Items
            { "bloodchalice", "Item_BloodChalice" },
            { "gravelocket", "Item_GraveLocket" },
            { "opheliaspact", "Item_OpheliasPact" },
            { "lextalionis", "Item_LexTalionis" },
            { "astrolabe", "Item_Astrolabe" },
            { "astrolobe", "Item_Astrolabe" },
            { "portalkey", "Item_PortalKey" },
            { "ringofsorcery", "Item_Replenish" },
            { "replenish", "Item_Replenish" },
            { "ringoftheteacher", "Item_ManaRegen3" },
            { "manaregen3", "Item_ManaRegen3" },
            { "shamansheaddress", "Item_MagicArmor2" },
            { "magicarmor2", "Item_MagicArmor2" },
            { "nomeswisdom", "Item_NomesWisdom" },
            { "barrieridol", "Item_BarrierIdol" },
            { "energizer", "Item_Energizer" },
            { "tabletofcommand", "Item_PushStaff" },
            { "pushstaff", "Item_PushStaff" },
            { "sacrificialstone", "Item_SacrificialStone" },
            { "dreamcatcher", "Item_Dreamcatcher" },
            { "voidtalisman", "Item_VoidTalisman" },
            { "solsbulwark", "Item_SolsBulwark" },
            { "solbulwark", "Item_SolsBulwark" },

            // Weapons, Orbs & High Tier
            { "kuldrassheepstick", "Item_Morph" },
            { "sheepstick", "Item_Morph" },
            { "morph", "Item_Morph" },
            { "codex", "Item_Nuke" },
            { "nuke", "Item_Nuke" },
            { "shrunkenhead", "Item_Immunity" },
            { "immunity", "Item_Immunity" },
            { "nullstone", "Item_Protect" },
            { "protect", "Item_Protect" },
            { "hellflower", "Item_Silence" },
            { "silence", "Item_Silence" },
            { "geometersbane", "Item_ManaBurn2" },
            { "manaburn2", "Item_ManaBurn2" },
            { "nullfireblade", "Item_ManaBurn1" },
            { "manaburn1", "Item_ManaBurn1" },
            { "frostwolfsskull", "Item_Freeze" },
            { "frostwolfskull", "Item_Freeze" },
            { "freeze", "Item_Freeze" },
            { "shieldbreaker", "Item_Pierce" },
            { "pierce", "Item_Pierce" },
            { "barbedarmor", "Item_Excruciator" },
            { "excruciator", "Item_Excruciator" },
            { "harkonsblade", "Item_HarkonsBlade" },
            { "harkons_blade", "Item_HarkonsBlade" },
            { "sandscepter", "Item_SandScepter" },
            { "sand_scepter", "Item_SandScepter" },
            { "grimoireofpower", "Item_GrimoireOfPower" },
            { "grimoire_of_power", "Item_GrimoireOfPower" },
            { "tomeofpower", "Item_GrimoireOfPower" },
            { "staffofthemaster", "Item_Intelligence7" },
            { "intelligence7", "Item_Intelligence7" },
            { "riftshards", "Item_Critical1" },
            { "critical1", "Item_Critical1" },
            { "mockofbrilliance", "Item_Damage10" },
            { "damage10", "Item_Damage10" },
            { "doombringer", "Item_Damage9" },
            { "damage9", "Item_Damage9" },
            { "symbolofrage", "Item_LifeSteal4" },
            { "lifesteal4", "Item_LifeSteal4" },
            { "abyssalskull", "Item_LifeSteal5" },
            { "lifesteal5", "Item_LifeSteal5" },
            { "wingbow", "Item_Evasion" },
            { "evasion", "Item_Evasion" },
            { "dancingblade", "Item_Wingbow" },
            { "savagemace", "Item_Weapon3" },
            { "weapon3", "Item_Weapon3" },
            { "alchemistsbones", "Item_Gloves3" },
            { "gloves3", "Item_Gloves3" },
            { "iconofthegoddess", "Item_HealthMana2" },
            { "healthmana2", "Item_HealthMana2" },
            { "helmoftheblacklegion", "Item_Shield2" },
            { "shield2", "Item_Shield2" },
            { "runedcleaver", "Item_Splash" },
            { "splash", "Item_Splash" },
            { "assassinsshroud", "Item_Stealth" },
            { "stealth", "Item_Stealth" },
            { "puzzlebox", "Item_Summon" },
            { "summon", "Item_Summon" },
            { "spellshards", "Item_SpellShards" },
            { "arcanebomb", "Item_ArcaneBomb" },
            { "daemonicbreastplate", "Item_DaemonicBreastplate" },
            { "behemothsheart", "Item_BehemothsHeart" },
            { "insanitarius", "Item_Insanitarius" },
            { "elderparasite", "Item_ElderParasite" },
            { "dawnbringer", "Item_Dawnbringer" },
            { "lightbrand", "Item_Lightbrand" },
            { "masterslegacy", "Item_MastersLegacy" },
            { "twinblades", "Item_TwinBlades" },
            { "thunderclaw", "Item_Lightning1" },
            { "lightning1", "Item_Lightning1" },
            { "chargedhammer", "Item_Lightning2" },
            { "lightning2", "Item_Lightning2" },
            { "stormspirit", "Item_Intelligence6" },
            { "cyclone", "Item_Intelligence6" },
            { "intelligence6", "Item_Intelligence6" },
            { "restorationstone", "Item_RestorationStone" },
            { "genjuro", "Item_Sasuke" },
            { "sasuke", "Item_Sasuke" },
            { "slayer", "Item_Voulge" },
            { "voulge", "Item_Voulge" },
            { "refreshingornament", "Item_MysticPotpourri" },
            { "mysticpotpourri", "Item_MysticPotpourri" },
            { "bolsteringarmband", "Item_BlessedArmband" },
            { "blessedarmband", "Item_BlessedArmband" },
            { "blessedorb", "Item_Confluence" },
            { "confluence", "Item_Confluence" },
            { "glovesoftheswift", "Item_GlovesOfHaste" },
            { "glovesofhaste", "Item_GlovesOfHaste" },
            { "pickledbrain", "Item_BrainOfMaliken" },
            { "brainofmaliken", "Item_BrainOfMaliken" },
            { "sustainer", "Item_Regen" },
            { "regen", "Item_Regen" },
            { "arcana", "Item_Weapon1" },
            { "weapon1", "Item_Weapon1" },
            { "fortifiedbracer", "Item_Strength5" },
            { "strength5", "Item_Strength5" },
            { "amuletofexile", "Item_Intelligence5" },
            { "intelligence5", "Item_Intelligence5" },
            { "icebrand", "Item_Strength6" },
            { "strength6", "Item_Strength6" },
            { "firebrand", "Item_Sicarius" },
            { "sicarius", "Item_Sicarius" },
            { "frostburn", "Item_StrengthAgility" },
            { "strengthagility", "Item_StrengthAgility" },
            { "orbofzamos", "Item_SomazsSynchronizer" },
            { "tomeofelements", "Item_ElementUsersTome" },
            { "elementuserstome", "Item_ElementUsersTome" },
            { "ultorsheavyhelm", "Item_UltorsHeavyPlate" },
            { "ultorsheavyplate", "Item_UltorsHeavyPlate" },
            { "groundcourier", "Item_GroundFamiliar" },
            { "groundfamiliar", "Item_GroundFamiliar" },
            { "flyingcourier", "Item_FlyingCourier" },
            { "tokenoflife", "Item_Revive" },
            { "revive", "Item_Revive" },
            { "madfredsbrassknuckles", "Item_Madfreds_BrassKnuckles" },
            { "madfreds_brassknuckles", "Item_Madfreds_BrassKnuckles" },
            { "spellsunder", "Item_Spell_Sunder" },
            { "spell_sunder", "Item_Spell_Sunder" },
            { "sorceryboots", "Item_ArcaneBoots" }
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> EngineToWebCodeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Item_Morph", "kuldrassheepstick" },
            { "Item_PushStaff", "tabletofcommand" },
            { "Item_RunesOfTheBlight", "blightstones" },
            { "Item_Nuke", "codex" },
            { "Item_Immunity", "shrunkenhead" },
            { "Item_Protect", "nullstone" },
            { "Item_Silence", "hellflower" },
            { "Item_ManaBurn2", "geometersbane" },
            { "Item_ManaBurn1", "nullfireblade" },
            { "Item_Freeze", "frostwolfsskull" },
            { "Item_Pierce", "shieldbreaker" },
            { "Item_Excruciator", "barbedarmor" },
            { "Item_Intelligence6", "stormspirit" },
            { "Item_Stormspirit", "stormspirit" },
            { "Item_HarkonsBlade", "harkonsblade" },
            { "Item_SandScepter", "sandscepter" },
            { "Item_GrimoireOfPower", "grimoireofpower" },
            { "Item_Intelligence7", "staffofthemaster" },
            { "Item_Critical1", "riftshards" },
            { "Item_Damage9", "doombringer" },
            { "Item_Damage10", "mockofbrilliance" },
            { "Item_LifeSteal4", "symbolofrage" },
            { "Item_LifeSteal5", "abyssalskull" },
            { "Item_Evasion", "wingbow" },
            { "Item_Wingbow", "dancingblade" },
            { "Item_Weapon3", "savagemace" },
            { "Item_Gloves3", "alchemistsbones" },
            { "Item_HealthMana2", "iconofthegoddess" },
            { "Item_MagicArmor2", "shamansheaddress" },
            { "Item_ManaRegen3", "ringoftheteacher" },
            { "Item_Replenish", "ringofsorcery" },
            { "Item_Shield2", "helmoftheblacklegion" },
            { "Item_Splash", "runedcleaver" },
            { "Item_Stealth", "assassinsshroud" },
            { "Item_Summon", "puzzlebox" },
            { "Item_SpellShards", "spellshards" },
            { "Item_ArcaneBomb", "arcanebomb" },
            { "Item_Fleetfeet", "fleetfeet" },
            { "Item_EnhancedMarchers", "ghostmarchers" },
            { "Item_Lightning1", "thunderclaw" },
            { "Item_Lightning2", "chargedhammer" },
            { "Item_FlamingEye", "wardofsight" },
            { "Item_ManaEye", "wardofrevelation" },
            { "Item_Astrolabe", "astrolabe" },
            { "Item_Sasuke", "genjuro" },
            { "Item_Voulge", "slayer" },
            { "Item_MysticPotpourri", "refreshingornament" },
            { "Item_BlessedArmband", "bolsteringarmband" },
            { "Item_Confluence", "blessedorb" },
            { "Item_GlovesOfHaste", "glovesoftheswift" },
            { "Item_BrainOfMaliken", "pickledbrain" },
            { "Item_Regen", "sustainer" },
            { "Item_Weapon1", "arcana" },
            { "Item_Strength5", "fortifiedbracer" },
            { "Item_Intelligence5", "amuletofexile" },
            { "Item_Strength6", "icebrand" },
            { "Item_Sicarius", "firebrand" },
            { "Item_StrengthAgility", "frostburn" },
            { "Item_SomazsSynchronizer", "orbofzamos" },
            { "Item_ElementUsersTome", "tomeofelements" },
            { "Item_UltorsHeavyPlate", "ultorsheavyhelm" },
            { "Item_GroundFamiliar", "groundcourier" },
            { "Item_Revive", "tokenoflife" },
            { "Item_Madfreds_BrassKnuckles", "madfredsbrassknuckles" },
            { "Item_Spell_Sunder", "spellsunder" }
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Comprehensive cross-game equivalencies (Dota 1, Dota 2, League of Legends),
    ///     popular community abbreviations, and legacy HoN search terms.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ItemSearchAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "Item_Immunity",
                "bkb, sh, qss, shrunken head, black king bar, spell immunity, magic immunity, quicksilver sash, avatar, immune, panic button"
            },
            {
                "shrunkenhead",
                "bkb, sh, qss, shrunken head, black king bar, spell immunity, magic immunity, quicksilver sash, avatar, immune, panic button"
            },
            {
                "Item_PortalKey",
                "pk, bd, flash, blink, blink dagger, portal key, dagger, jump, teleport, rocketbelt, protobelt, kelen"
            },
            {
                "portalkey",
                "pk, bd, flash, blink, blink dagger, portal key, dagger, jump, teleport, rocketbelt, protobelt, kelen"
            },
            {
                "Item_PushStaff",
                "force, forcestaff, push, toc, tablet of command, force staff, push staff, rocketbelt, hurricane pike"
            },
            {
                "tabletofcommand",
                "force, forcestaff, push, toc, tablet of command, force staff, push staff, rocketbelt, hurricane pike"
            },
            {
                "Item_Morph",
                "hex, sheep, sheepstick, ks, kss, vyse, kuldra, kuldras sheepstick, scythe of vyse, guinsoo, polymorph, pig, disable"
            },
            {
                "kuldrassheepstick",
                "hex, sheep, sheepstick, ks, kss, vyse, kuldra, kuldras sheepstick, scythe of vyse, guinsoo, polymorph, pig, disable"
            },
            {
                "Item_Pierce",
                "deso, sb, shieldbreaker, desolator, stygian desolator, corruption, minus armor, armor reduction, the black cleaver, last whisper, serylda, ldr"
            },
            {
                "shieldbreaker",
                "deso, sb, shieldbreaker, desolator, stygian desolator, corruption, minus armor, armor reduction, the black cleaver, last whisper, serylda, ldr"
            },
            {
                "Item_Critical1",
                "crit, daed, daedalus, buriza, ie, riftshards, buriza-do kyanon, crystalys, infinity edge, critical strike, big crit"
            },
            {
                "riftshards",
                "crit, daed, daedalus, buriza, ie, riftshards, buriza-do kyanon, crystalys, infinity edge, critical strike, big crit"
            },
            {
                "Item_Damage10",
                "mock, mob, rad, radiance, mock of brilliance, burn aura, sunfire aegis, sunfire cape, bamis cinder, hollow radiance, relic"
            },
            {
                "mockofbrilliance",
                "mock, mob, rad, radiance, mock of brilliance, burn aura, sunfire aegis, sunfire cape, bamis cinder, hollow radiance, relic"
            },
            {
                "Item_BehemothsHeart",
                "heart, hot, warmog, warmogs, behemoths heart, heart of tarrasque, tarrasque, reaver, giants belt, heartsteel, tank"
            },
            {
                "behemothsheart",
                "heart, hot, warmog, warmogs, behemoths heart, heart of tarrasque, tarrasque, reaver, giants belt, heartsteel, tank"
            },
            {
                "Item_DaemonicBreastplate",
                "ac, db, cuirass, assault cuirass, daemonic breastplate, hyperstone, frozen heart, randuins omen, plate, aura armor"
            },
            {
                "daemonicbreastplate",
                "ac, db, cuirass, assault cuirass, daemonic breastplate, hyperstone, frozen heart, randuins omen, plate, aura armor"
            },
            {
                "Item_Splash",
                "bf, cleave, battlefury, battle fury, runed cleaver, tiamat, ravenous hydra, titanic hydra, perseverance, splash"
            },
            {
                "runedcleaver",
                "bf, cleave, battlefury, battle fury, runed cleaver, tiamat, ravenous hydra, titanic hydra, perseverance, splash"
            },
            {
                "Item_ManaBurn2",
                "manta, gb, geometers bane, manta style, illusions, mirror image, phantom dancer, yasha, clone"
            },
            {
                "geometersbane",
                "manta, gb, geometers bane, manta style, illusions, mirror image, phantom dancer, yasha, clone"
            },
            {
                "Item_Freeze",
                "skadi, eos, fws, frostwolf, frostwolfs skull, eye of skadi, cold attack, rylais crystal scepter, frozen mallet, slow, frost"
            },
            {
                "frostwolfsskull",
                "skadi, eos, fws, frostwolf, frostwolfs skull, eye of skadi, cold attack, rylais crystal scepter, frozen mallet, slow, frost"
            },
            {
                "Item_Protect",
                "linken, linkens, null stone, linkens sphere, spell block, banshees veil, edge of night, spell shield"
            },
            {
                "nullstone",
                "linken, linkens, null stone, linkens sphere, spell block, banshees veil, edge of night, spell shield"
            },
            {
                "Item_Intelligence6",
                "eul, euls, cyclone, ss, stormspirit, euls scepter of divinity, zhonya, stopwatch, wind waker, tornado, lift"
            },
            {
                "stormspirit",
                "eul, euls, cyclone, ss, stormspirit, euls scepter of divinity, zhonya, stopwatch, wind waker, tornado, lift"
            },
            {
                "Item_Silence",
                "orchid, hf, silence, hellflower, orchid malevolence, bloodthorn, deathfire grasp, dfg, soul burn, burst"
            },
            {
                "hellflower",
                "orchid, hf, silence, hellflower, orchid malevolence, bloodthorn, deathfire grasp, dfg, soul burn, burst"
            },
            {
                "Item_Evasion",
                "butterfly, bfly, evasion, wb, wingbow, eaglehorn, eaglesong, flutter, dodge, phantom dancer, miss chance"
            },
            {
                "wingbow",
                "butterfly, bfly, evasion, wb, wingbow, eaglehorn, eaglesong, flutter, dodge, phantom dancer, miss chance"
            },
            {
                "Item_Excruciator",
                "bm, blademail, blade mail, thornmail, bramble vest, barbed armor, return damage, reflect, spikes"
            },
            {
                "barbedarmor",
                "bm, blademail, blade mail, thornmail, bramble vest, barbed armor, return damage, reflect, spikes"
            },
            {
                "Item_VoidTalisman",
                "ghost, ghostscepter, ghost scepter, ethereal, vt, zhonya, void talisman, stasis, stopwatch, untargetable, immune phys"
            },
            {
                "voidtalisman",
                "ghost, ghostscepter, ghost scepter, ethereal, vt, zhonya, void talisman, stasis, stopwatch, untargetable, immune phys"
            },
            {
                "Item_BarrierIdol",
                "pipe, pipeofinsight, pipe of insight, barrier idol, hood of defiance, locket of the iron solari, magic shield, maw of malmortius"
            },
            {
                "barrieridol",
                "pipe, pipeofinsight, pipe of insight, barrier idol, hood of defiance, locket of the iron solari, magic shield, maw of malmortius"
            },
            {
                "Item_Astrolabe",
                "mek, mekansm, heal, astro, astrolabe, redemption, mikaels blessing, guardian greaves, team heal, aoe heal"
            },
            {
                "astrolabe",
                "mek, mekansm, heal, astro, astrolabe, redemption, mikaels blessing, guardian greaves, team heal, aoe heal"
            },
            {
                "Item_ManaBurn1",
                "diffusal, diffusalblade, diffusal blade, nullfire blade, manaburn, purge, botrk, blade of the ruined king, slow, drain"
            },
            {
                "nullfireblade",
                "diffusal, diffusalblade, diffusal blade, nullfire blade, manaburn, purge, botrk, blade of the ruined king, slow, drain"
            },
            {
                "Item_StrengthAgility",
                "sny, s&y, sangeandyasha, sange and yasha, frostburn, trinity force, stridebreaker, maim, speed"
            },
            {
                "frostburn",
                "sny, s&y, sangeandyasha, sange and yasha, frostburn, trinity force, stridebreaker, maim, speed"
            },
            { "Item_Strength6", "sange, maim, icebrand, ogre axe, phage, steraks gage, str sword" },
            { "icebrand", "sange, maim, icebrand, ogre axe, phage, steraks gage, str sword" },
            { "Item_Sicarius", "yasha, firebrand, sicarius, zeal, phantom dancer, agi sword, movespeed" },
            { "firebrand", "yasha, firebrand, sicarius, zeal, phantom dancer, agi sword, movespeed" },
            { "Item_Lightbrand", "kaya, lightbrand, spell amp, staff of wizardry, int sword, mana loss reduction" },
            { "lightbrand", "kaya, lightbrand, spell amp, staff of wizardry, int sword, mana loss reduction" },
            { "Item_Dawnbringer", "trident, dawnbringer, triple brand, sange kaya yasha, sky trident" },
            { "dawnbringer", "trident, dawnbringer, triple brand, sange kaya yasha, sky trident" },
            {
                "Item_Insanitarius",
                "armlet, armletofmordiggian, armlet of mordiggian, insanitarius, toggle, unholy strength, steraks gage, hp toggle"
            },
            {
                "insanitarius",
                "armlet, armletofmordiggian, armlet of mordiggian, insanitarius, toggle, unholy strength, steraks gage, hp toggle"
            },
            {
                "Item_Stealth",
                "lothar, lothars, shadowblade, shadow blade, assassin's shroud, assassinsshroud, invis, duskblade, stealth, backstab"
            },
            {
                "assassinsshroud",
                "lothar, lothars, shadowblade, shadow blade, assassin's shroud, assassinsshroud, invis, duskblade, stealth, backstab"
            },
            { "Item_Nuke", "dagon, codex, nuke, red cane, finger, burst, ludens tempest, deathfire grasp, dfg, zap" },
            { "codex", "dagon, codex, nuke, red cane, finger, burst, ludens tempest, deathfire grasp, dfg, zap" },
            { "Item_Brutalizer", "basher, skullbasher, skull basher, brutalizer, bash, stun, abyssal blade" },
            { "brutalizer", "basher, skullbasher, skull basher, brutalizer, bash, stun, abyssal blade" },
            {
                "Item_Lightning2",
                "mjollnir, mjolnir, charged hammer, hyperstone, chain lightning, statikk shiv, static charge"
            },
            {
                "chargedhammer",
                "mjollnir, mjolnir, charged hammer, hyperstone, chain lightning, statikk shiv, static charge"
            },
            { "Item_Lightning1", "maelstrom, mael, thunderclaw, chain lightning, kircheis shard, statikk shiv" },
            { "thunderclaw", "maelstrom, mael, thunderclaw, chain lightning, kircheis shard, statikk shiv" },
            {
                "Item_LifeSteal4",
                "satanic, sor, symbol of rage, symbolofrage, lifesteal, unholy rage, bloodthirster, bt, vamp, unkillable"
            },
            {
                "symbolofrage",
                "satanic, sor, symbol of rage, symbolofrage, lifesteal, unholy rage, bloodthirster, bt, vamp, unkillable"
            },
            {
                "Item_LifeSteal5",
                "vlad, vlads, vladimir, vladimiroffering, vladmir's offering, abyssal skull, abyssalskull, vampiric scepter, lifesteal aura"
            },
            {
                "abyssalskull",
                "vlad, vlads, vladimir, vladimiroffering, vladmir's offering, abyssal skull, abyssalskull, vampiric scepter, lifesteal aura"
            },
            { "Item_Summon", "necro, necrobook, necronomicon, puzzlebox, summons, mana burn minions, minions" },
            { "puzzlebox", "necro, necrobook, necronomicon, puzzlebox, summons, mana burn minions, minions" },
            {
                "Item_Intelligence7",
                "aghs, aghanims, aghanims scepter, aghanims sceptre, staff of the master, staffofthemaster, sotm, ultimate upgrade"
            },
            {
                "staffofthemaster",
                "aghs, aghanims, aghanims scepter, aghanims sceptre, staff of the master, staffofthemaster, sotm, ultimate upgrade"
            },
            { "Item_MastersLegacy", "aghs blessing, aghanims blessing, masters legacy, consumable aghs, shard" },
            { "masterslegacy", "aghs blessing, aghanims blessing, masters legacy, consumable aghs, shard" },
            {
                "Item_PostHaste",
                "bot, travels, bootsoftravel, boots of travel, post haste, posthaste, tp boots, mobis, mobility boots"
            },
            {
                "posthaste",
                "bot, travels, bootsoftravel, boots of travel, post haste, posthaste, tp boots, mobis, mobility boots"
            },
            {
                "Item_EnhancedMarchers",
                "phase, phaseboots, phase boots, ghost marchers, ghostmarchers, swifties, boots of swiftness, speed active"
            },
            {
                "ghostmarchers",
                "phase, phaseboots, phase boots, ghost marchers, ghostmarchers, swifties, boots of swiftness, speed active"
            },
            {
                "Item_Steamboots",
                "treads, powertreads, power treads, steamboots, tread switch, stat boots, berserkers greaves, attack speed boots"
            },
            {
                "steamboots",
                "treads, powertreads, power treads, steamboots, tread switch, stat boots, berserkers greaves, attack speed boots"
            },
            {
                "Item_ArcaneBoots",
                "mana boots, arcane boots, sorcery boots, sorceryboots, energy booster boots, blue boots, mana shoes"
            },
            {
                "sorceryboots",
                "mana boots, arcane boots, sorcery boots, sorceryboots, energy booster boots, blue boots, mana shoes"
            },
            {
                "Item_Striders",
                "tranquil, tranquils, tranquil boots, striders, green boots, roaming boots, regen boots"
            },
            { "striders", "tranquil, tranquils, tranquil boots, striders, green boots, roaming boots, regen boots" },
            { "Item_Marchers", "boots, brown boots, marchers, boots of speed, basic boots" },
            { "marchers", "boots, brown boots, marchers, boots of speed, basic boots" },
            {
                "Item_PlatedGreaves",
                "armor boots, greaves, plated greaves, platedgreaves, ninja tabi, plated steelcaps, guardian greaves"
            },
            {
                "platedgreaves",
                "armor boots, greaves, plated greaves, platedgreaves, ninja tabi, plated steelcaps, guardian greaves"
            },
            {
                "Item_PowerSupply",
                "wand, magicwand, magic wand, power supply, powersupply, charges, battery, refillable potion"
            },
            {
                "powersupply",
                "wand, magicwand, magic wand, power supply, powersupply, charges, battery, refillable potion"
            },
            { "Item_ManaBattery", "stick, magicstick, magic stick, mana battery, manabattery, charges" },
            { "manabattery", "stick, magicstick, magic stick, mana battery, manabattery, charges" },
            {
                "Item_RunesOfTheBlight",
                "tango, tangos, blight stones, blightstones, runes of the blight, tree eat, wood, biscuits, hp regen"
            },
            {
                "blightstones",
                "tango, tangos, blight stones, blightstones, runes of the blight, tree eat, wood, biscuits, hp regen"
            },
            {
                "Item_HealthPotion",
                "salve, healingsalve, healing salve, health potion, healthpotion, flask, red pot, hp pot"
            },
            {
                "healthpotion",
                "salve, healingsalve, healing salve, health potion, healthpotion, flask, red pot, hp pot"
            },
            { "Item_ManaPotion", "clarity, claritypotion, clarity potion, mana potion, manapotion, blue pot, mp pot" },
            { "manapotion", "clarity, claritypotion, clarity potion, mana potion, manapotion, blue pot, mp pot" },
            { "Item_GoldenApple", "faerie fire, faeriefire, golden apple, goldenapple, apple, instant heal" },
            { "goldenapple", "faerie fire, faeriefire, golden apple, goldenapple, apple, instant heal" },
            { "Item_LunarTear", "mango, enchantedmango, enchanted mango, lunar tear, lunartear, instant mana" },
            { "lunartear", "mango, enchantedmango, enchanted mango, lunar tear, lunartear, instant mana" },
            {
                "Item_NihilCrystal",
                "raindrops, infusedraindrops, infused raindrops, nihil crystal, nihilcrystal, magic block"
            },
            {
                "nihilcrystal",
                "raindrops, infusedraindrops, infused raindrops, nihil crystal, nihilcrystal, magic block"
            },
            {
                "Item_BoundEye",
                "gem, gemoftruesight, gem of true sight, bound eye, boundeye, truesight, reveal, oracle elixir"
            },
            {
                "boundeye",
                "gem, gemoftruesight, gem of true sight, bound eye, boundeye, truesight, reveal, oracle elixir"
            },
            {
                "Item_DustOfRevelation",
                "dust, dustofappearance, dust of appearance, dust of revelation, dustofrevelation, reveal, sweeper, oracle lens"
            },
            {
                "dustofrevelation",
                "dust, dustofappearance, dust of appearance, dust of revelation, dustofrevelation, reveal, sweeper, oracle lens"
            },
            {
                "Item_ManaEye",
                "sentry, sentryward, sentry ward, ward of revelation, wardofrevelation, pink ward, control ward, blue ward, sentries"
            },
            {
                "wardofrevelation",
                "sentry, sentryward, sentry ward, ward of revelation, wardofrevelation, pink ward, control ward, blue ward, sentries"
            },
            {
                "Item_FlamingEye",
                "obs, observer, observerward, observer ward, ward of sight, wardofsight, yellow ward, green ward, stealth ward, vision"
            },
            {
                "wardofsight",
                "obs, observer, observerward, observer ward, ward of sight, wardofsight, yellow ward, green ward, stealth ward, vision"
            },
            {
                "Item_HomecomingStone",
                "tp, tpscroll, tp scroll, town portal, town portal scroll, homecoming stone, homecomingstone, teleport"
            },
            {
                "homecomingstone",
                "tp, tpscroll, tp scroll, town portal, town portal scroll, homecoming stone, homecomingstone, teleport"
            },
            { "Item_Bottle", "bottle, rune bottle, bottle of tears, corrupting potion, refill" },
            { "bottle", "bottle, rune bottle, bottle of tears, corrupting potion, refill" },
            {
                "Item_FrostfieldPlate",
                "shiva, shivas, shivasguard, shiva's guard, frostfield plate, frostfieldplate, freezearmor, frozen heart"
            },
            {
                "frostfieldplate",
                "shiva, shivas, shivasguard, shiva's guard, frostfield plate, frostfieldplate, freezearmor, frozen heart"
            },
            {
                "Item_RestorationStone",
                "refresher, refresherorb, refresher orb, restoration stone, restorationstone, reset cds, double ult"
            },
            {
                "restorationstone",
                "refresher, refresherorb, refresher orb, restoration stone, restorationstone, reset cds, double ult"
            },
            {
                "Item_Damage9",
                "rapier, divinerapier, divine rapier, doom bringer, doombringer, drop on death, 300 damage, hubris"
            },
            {
                "doombringer",
                "rapier, divinerapier, divine rapier, doom bringer, doombringer, drop on death, 300 damage, hubris"
            },
            {
                "Item_Weapon3",
                "mkb, monkeykingbar, monkey king bar, savage mace, savagemace, true strike, mini stun, terminus, giant slayer"
            },
            {
                "savagemace",
                "mkb, monkeykingbar, monkey king bar, savage mace, savagemace, true strike, mini stun, terminus, giant slayer"
            },
            {
                "Item_ElderParasite",
                "mom, maskofmadness, mask of madness, elder parasite, elderparasite, berserk, lifesteal, attack speed"
            },
            {
                "elderparasite",
                "mom, maskofmadness, mask of madness, elder parasite, elderparasite, berserk, lifesteal, attack speed"
            },
            { "Item_Halberd", "halberd, heavenshalberd, heaven's halberd, disarm, javelin" },
            {
                "Item_SacrificialStone",
                "bloodstone, sacrificial stone, sacrificialstone, mana regen, suicide, rod of ages"
            },
            { "sacrificialstone", "bloodstone, sacrificial stone, sacrificialstone, mana regen, suicide, rod of ages" },
            {
                "Item_SolsBulwark",
                "medallion, medallionofcourage, solarcrest, solar crest, sols bulwark, solsbulwark, minus armor, armor buff"
            },
            {
                "solsbulwark",
                "medallion, medallionofcourage, solarcrest, solar crest, sols bulwark, solsbulwark, minus armor, armor buff"
            },
            {
                "Item_GraveLocket",
                "urn, urnofshadows, urn of shadows, spiritvessel, spirit vessel, grave locket, gravelocket, charges, heal dot"
            },
            {
                "gravelocket",
                "urn, urnofshadows, urn of shadows, spiritvessel, spirit vessel, grave locket, gravelocket, charges, heal dot"
            },
            { "Item_BloodChalice", "soulring, soul ring, blood chalice, bloodchalice, hp for mana, sacrifice" },
            { "bloodchalice", "soulring, soul ring, blood chalice, bloodchalice, hp for mana, sacrifice" },
            {
                "Item_MagicArmor2",
                "hood, hoodofdefiance, hood of defiance, shamans headdress, shamansheaddress, magic resist, spectres cowl"
            },
            {
                "shamansheaddress",
                "hood, hoodofdefiance, hood of defiance, shamans headdress, shamansheaddress, magic resist, spectres cowl"
            },
            {
                "Item_Shield2",
                "vanguard, helm of the black legion, helmoftheblacklegion, hotbl, damage block, stout shield, dorans shield"
            },
            {
                "helmoftheblacklegion",
                "vanguard, helm of the black legion, helmoftheblacklegion, hotbl, damage block, stout shield, dorans shield"
            },
            {
                "Item_Energizer",
                "drums, drumsofendurance, drums of endurance, energizer, speed aura, shurelyas battlesong"
            },
            { "energizer", "drums, drumsofendurance, drums of endurance, energizer, speed aura, shurelyas battlesong" },
            {
                "Item_ManaRegen3",
                "basilius, aquila, ring of basilius, ring of aquila, ring of the teacher, ringoftheteacher, rott, rob, mana aura"
            },
            {
                "ringoftheteacher",
                "basilius, aquila, ring of basilius, ring of aquila, ring of the teacher, ringoftheteacher, rott, rob, mana aura"
            },
            { "Item_Replenish", "arcanering, arcane ring, ring of sorcery, ringofsorcery, ros, ar, mana replenish" },
            { "ringofsorcery", "arcanering, arcane ring, ring of sorcery, ringofsorcery, ros, ar, mana replenish" },
            {
                "Item_LoggersHatchet",
                "qb, quellingblade, quelling blade, loggers hatchet, loggershatchet, hatchet, dorans blade, tree cut"
            },
            {
                "loggershatchet",
                "qb, quellingblade, quelling blade, loggers hatchet, loggershatchet, hatchet, dorans blade, tree cut"
            },
            {
                "Item_Strength5",
                "bracer, fortified bracer, fortifiedbracer, fb, dorans shield, ruby crystal, str component"
            },
            {
                "fortifiedbracer",
                "bracer, fortified bracer, fortifiedbracer, fb, dorans shield, ruby crystal, str component"
            },
            {
                "Item_Soulscream",
                "wraithband, wraith band, soulscream ring, soulscream, ssr, wb, dorans blade, agi component"
            },
            {
                "soulscream",
                "wraithband, wraith band, soulscream ring, soulscream, ssr, wb, dorans blade, agi component"
            },
            {
                "Item_Intelligence5",
                "null, nulltalisman, null talisman, amulet of exile, amuletofexile, nt, dorans ring, amplifying tome, int component"
            },
            {
                "amuletofexile",
                "null, nulltalisman, null talisman, amulet of exile, amuletofexile, nt, dorans ring, amplifying tome, int component"
            },
            {
                "Item_GrimoireOfPower",
                "veil, veilofdiscord, veil of discord, grimoire of power, grimoireofpower, magic amp, spell damage"
            },
            {
                "grimoireofpower",
                "veil, veilofdiscord, veil of discord, grimoire of power, grimoireofpower, magic amp, spell damage"
            },
            { "Item_JadeSpire", "aetherlens, aether lens, jade spire, jadespire, cast range, lens, spell range" },
            { "jadespire", "aetherlens, aether lens, jade spire, jadespire, cast range, lens, spell range" },
            { "Item_VeiledRot", "smoke, smokeofdeceit, smoke of deceit, veiled rot, veiledrot, gank, invis team" },
            { "veiledrot", "smoke, smokeofdeceit, smoke of deceit, veiled rot, veiledrot, gank, invis team" },
            {
                "Item_Revive",
                "aegis, aegisoftheimmortal, aegis of the immortal, token of life, tokenoflife, guardian angel, ga, roshan, revive"
            },
            {
                "tokenoflife",
                "aegis, aegisoftheimmortal, aegis of the immortal, token of life, tokenoflife, guardian angel, ga, roshan, revive"
            },
            { "Item_TwinBlades", "echosabre, echo sabre, twin blades, twinblades, double strike, echo, double attack" },
            { "twinblades", "echosabre, echo sabre, twin blades, twinblades, double strike, echo, double attack" },
            { "Item_AdevesCloak", "aeondisk, aeon disk, adeves cloak, adevescloak, antiburst, combo breaker" },
            { "adevescloak", "aeondisk, aeon disk, adeves cloak, adevescloak, antiburst, combo breaker" },
            {
                "Item_SpellShards",
                "spellshards, spell shards, magic penetration, cdr, cooldown reduction, kaya, magic crit"
            },
            {
                "spellshards", "spellshards, spell shards, magic penetration, cdr, cooldown reduction, kaya, magic crit"
            },
            { "Item_ArcaneBomb", "arcane bomb, arcanebomb, meteor hammer, bomb, creep clear, magic burst" },
            { "arcanebomb", "arcane bomb, arcanebomb, meteor hammer, bomb, creep clear, magic burst" },
            { "Item_LexTalionis", "atos, rodoftatos, rod of atos, lex talionis, lextalionis, root, cripple" },
            { "lextalionis", "atos, rodoftatos, rod of atos, lex talionis, lextalionis, root, cripple" },
            { "Item_SandScepter", "sand scepter, sandscepter, sand, cyclone, trap, immobilize" },
            { "sandscepter", "sand scepter, sandscepter, sand, cyclone, trap, immobilize" },
            { "Item_WindWhistle", "windlace, wind lace, wind whistle, windwhistle, movespeed, 20ms" },
            { "windwhistle", "windlace, wind lace, wind whistle, windwhistle, movespeed, 20ms" },
            { "Item_BloodRuby", "fluffyhat, fluffy hat, blood ruby, bloodruby, ruby crystal, 150hp" },
            { "bloodruby", "fluffyhat, fluffy hat, blood ruby, bloodruby, ruby crystal, 150hp" },
            { "Item_Genjuro", "genjuro, sasuke, stealth, invis, movement speed, slow attack, shadow blade" },
            { "genjuro", "genjuro, sasuke, stealth, invis, movement speed, slow attack, shadow blade" },
            { "Item_Slayer", "slayer, voulge, damage, attack speed, claymore" },
            { "slayer", "slayer, voulge, damage, attack speed, claymore" },
            { "Item_Spell_Sunder", "spell sunder, spellsunder, magic armor pierce, spell pierce, magic penetration, sunder" },
            { "spellsunder", "spell sunder, spellsunder, magic armor pierce, spell pierce, magic penetration, sunder" }
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Converts any raw token or web item code into the exact canonical HoN game engine entity name.
    ///     Example: "blightstones" -> "Item_RunesOfTheBlight", "Item_Blightstones" -> "Item_RunesOfTheBlight", "sheepstick" ->
    ///     "Item_Morph".
    /// </summary>
    public static string ToEngineEntityName(string? rawItem)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return string.Empty;

        string trimmed = rawItem.Trim();
        string clean = trimmed.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)
            ? trimmed[5..]
            : trimmed;

        clean = clean.Replace("_", "").Replace(" ", "").Replace("'", "").ToLowerInvariant();

        if (WebToEngineMap.TryGetValue(clean, out string? engineEntity)) return engineEntity;

        if (trimmed.StartsWith("Item_", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = trimmed[5..];
            if (suffix.Length > 0) return "Item_" + char.ToUpperInvariant(suffix[0]) + suffix[1..];

            return trimmed;
        }

        if (clean.Length > 0) return "Item_" + char.ToUpperInvariant(clean[0]) + clean[1..];

        return $"Item_{trimmed}";
    }

    /// <summary>
    ///     Converts a HoN game engine entity name to the canonical web slug used for images/catalog.
    ///     Example: "Item_Morph" -> "kuldrassheepstick", "Item_RunesOfTheBlight" -> "blightstones".
    /// </summary>
    public static string ToWebSlug(string? rawItem)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return string.Empty;

        string trimmed = rawItem.Trim();
        if (EngineToWebCodeMap.TryGetValue(trimmed, out string? webCode)) return webCode;

        return (trimmed.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)
            ? trimmed[5..]
            : trimmed).ToLowerInvariant();
    }

    /// <summary>
    ///     Gets all search keywords, abbreviations, and cross-game equivalents for an item.
    /// </summary>
    public static string GetSearchTerms(string? rawItem)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return string.Empty;

        string trimmed = rawItem.Trim();
        if (ItemSearchAliases.TryGetValue(trimmed, out string? terms)) return terms;

        string engineCode = ToEngineEntityName(trimmed);
        if (ItemSearchAliases.TryGetValue(engineCode, out string? engineTerms)) return engineTerms;

        string webSlug = ToWebSlug(trimmed);
        return ItemSearchAliases.TryGetValue(webSlug, out string? webTerms) ? webTerms : string.Empty;
    }

    /// <summary>
    ///     Checks if an item matches a search query against its name, code, abbreviations, and cross-game aliases.
    /// </summary>
    public static bool MatchesSearch(string? itemCode, string? itemName, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return true;

        if (!string.IsNullOrWhiteSpace(itemName) && itemName.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;

        if (!string.IsNullOrWhiteSpace(itemCode) && itemCode.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;

        string terms = GetSearchTerms(itemCode);
        if (string.IsNullOrWhiteSpace(terms) && !string.IsNullOrWhiteSpace(itemName)) terms = GetSearchTerms(itemName);

        if (!string.IsNullOrWhiteSpace(terms))
        {
            string tLower = terms.ToLowerInvariant();
            if (tLower.Contains(q)) return true;

            string[] tokens = tLower.Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(t =>
                t.Equals(q, StringComparison.OrdinalIgnoreCase) || t.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>
    ///     Computes a relevancy score (0-100) for search ranking.
    /// </summary>
    public static int GetSearchScore(string? itemCode, string? itemName, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return 0;

        string name = (itemName ?? string.Empty).ToLowerInvariant().Trim();
        string code = (itemCode ?? string.Empty).ToLowerInvariant().Trim();

        // 1. Exact name or code match
        if (name == q || code == q) return 100;

        // 2. Direct Name Starts With Query (e.g. "spell" -> Spellshards, Spell Sunder)
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 97;

        // 3. Name Word Starts With Query (e.g. "stone" -> Null Stone, Restoration Stone)
        string[] nameWords = name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (nameWords.Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 93;

        // 4. Exact abbreviation or single-alias match in search terms
        string terms = GetSearchTerms(itemCode);
        if (string.IsNullOrWhiteSpace(terms) && !string.IsNullOrWhiteSpace(itemName)) terms = GetSearchTerms(itemName);

        if (!string.IsNullOrWhiteSpace(terms))
        {
            // Split into distinct comma-separated alias phrases
            string[] aliases = terms.ToLowerInvariant()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // Exact whole-alias match (e.g. "pk", "bkb", "sheepstick", "hex", "deso", "blink", "warmogs")
            if (aliases.Any(a => a == q)) return 90;

            // Alias starts with query (e.g. "blink dagger" for "blink", "force staff" for "force")
            if (aliases.Any(a => a.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 85;

            // Individual word in an alias phrase starts with query (e.g. "spell block" or "spell immunity" for "spell")
            string[] aliasWords = terms.ToLowerInvariant()
                .Split([' ', ',', ';', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (aliasWords.Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 70;
        }

        // 5. Code starts with query
        if (code.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 78;

        // 6. Contains in name (substring match)
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 55;

        // 7. Contains in search terms (substring match)
        if (!string.IsNullOrWhiteSpace(terms) && terms.Contains(q, StringComparison.OrdinalIgnoreCase)) return 45;

        // 8. Contains in code
        return code.Contains(q, StringComparison.OrdinalIgnoreCase) ? 35 : 0;
    }
}
