// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;

namespace ASPIRE.Common.Prebaking.Generators;

public static class LuaCacheGenerator
{
    public static string GenerateLuaCache(EntityCacheContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        StringBuilder sb = new(256 * 1024);

        // Header Comment & Idempotency Guard
        sb.AppendLine("-- ============================================================================");
        sb.AppendLine("-- PROJECT KONGOR: PRE-BAKED HERO & ENTITY METADATA CACHE");
        sb.AppendLine(
            $"-- Generated At (UTC): {TimeProvider.System.GetUtcNow():yyyy-MM-dd HH:mm:ss} | Version: {container.Version}");
        sb.AppendLine(
            $"-- Total Heroes: {container.Heroes.Count} | Total Avatars: {container.AltAvatars.Count} | Total Abilities: {container.Abilities.Count} | Total Items: {container.Items.Count}");
        sb.AppendLine("-- ============================================================================");
        sb.AppendLine();
        sb.AppendLine("if HeroDefinitionsCache and HeroDefinitionsCache._initialized then return end");
        sb.AppendLine();
        sb.AppendLine("HeroDefinitionsCache = HeroDefinitionsCache or {}");
        sb.AppendLine();

        // 1. HEROES TABLE
        sb.AppendLine("HeroDefinitionsCache.Heroes = {");
        foreach (HeroDefinitionDto hero in container.Heroes.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            string dir = ExtractDirFromPath(hero.DefaultIcon, hero.Name);
            string faction = !string.IsNullOrWhiteSpace(hero.Faction)
                ? hero.Faction
                : !string.IsNullOrWhiteSpace(hero.Team)
                    ? hero.Team
                    : hero.Category.Contains("Hellbourne", StringComparison.OrdinalIgnoreCase)
                        ? "Hellbourne"
                        : "Legion";
            string attr = string.IsNullOrWhiteSpace(hero.PrimaryAttribute)
                ? "strength"
                : hero.PrimaryAttribute.ToLowerInvariant();

            sb.Append($"\t['{Escape(hero.Name)}'] = {{ ");
            sb.Append($"id = {hero.Id}, ");
            sb.Append($"name = '{Escape(hero.Name)}', ");
            sb.Append($"displayName = '{Escape(hero.DisplayName)}', ");
            sb.Append($"dir = '{Escape(dir)}', ");
            sb.Append($"icon = '{Escape(hero.DefaultIcon)}', ");
            sb.Append($"faction = '{faction}', ");
            sb.Append($"attr = '{attr}', ");
            sb.Append($"attackType = '{Escape(hero.AttackType)}', ");
            sb.Append($"strength = {hero.Strength.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"strengthPerLevel = {hero.StrengthPerLevel.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"agility = {hero.Agility.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"agilityPerLevel = {hero.AgilityPerLevel.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"intelligence = {hero.Intelligence.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append(
                $"intelligencePerLevel = {hero.IntelligencePerLevel.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"armor = {hero.Armor.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"magicArmor = {hero.MagicArmor.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"attackDamageMin = {hero.AttackDamageMin.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"attackDamageMax = {hero.AttackDamageMax.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"attackRange = {hero.AttackRange.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"moveSpeed = {hero.MoveSpeed.ToString("F1", CultureInfo.InvariantCulture)}, ");
            sb.Append($"model = '{Escape(hero.DefaultModel)}', ");
            sb.Append($"category = '{Escape(hero.Category)}', ");
            sb.Append($"difficulty = {hero.Difficulty.ToString("F1", CultureInfo.InvariantCulture)}, ");

            // Abilities array
            sb.Append("abilities = { ");
            for (int i = 0; i < hero.Abilities.Count; i++)
            {
                AbilityDefinitionDto ab = hero.Abilities[i];
                sb.Append(
                    $"{{ slot = {ab.Slot}, name = '{Escape(ab.Name)}', displayName = '{Escape(ab.DisplayName)}', icon = '{Escape(ab.Icon)}' }}");
                if (i < hero.Abilities.Count - 1) sb.Append(", ");
            }

            sb.Append(" }, ");

            // AltAvatars array / map
            sb.Append("avatars = { ");
            for (int i = 0; i < hero.AltAvatars.Count; i++)
            {
                AltAvatarDto av = hero.AltAvatars[i];
                sb.Append(
                    $"['{Escape(av.Key)}'] = {{ key = '{Escape(av.Key)}', displayName = '{Escape(av.DisplayName)}', icon = '{Escape(av.IconPath)}', model = '{Escape(av.DefaultModel)}' }}");
                if (i < hero.AltAvatars.Count - 1) sb.Append(", ");
            }

            sb.Append(" }");

            sb.AppendLine(" },");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // 2. ALT AVATARS TABLE
        sb.AppendLine("HeroDefinitionsCache.AltAvatars = {");
        foreach (AltAvatarDto avatar in container.AltAvatars.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"\t['{Escape(avatar.Key)}'] = {{ ");
            sb.Append($"key = '{Escape(avatar.Key)}', ");
            sb.Append($"hero = '{Escape(avatar.HeroName)}', ");
            sb.Append($"displayName = '{Escape(avatar.DisplayName)}', ");
            sb.Append($"icon = '{Escape(avatar.IconPath)}', ");
            sb.Append($"model = '{Escape(avatar.DefaultModel)}', ");
            sb.Append($"previewPos = '{Escape(avatar.PreviewPos)}', ");
            sb.Append($"previewAngles = '{Escape(avatar.PreviewAngles)}', ");
            sb.Append($"previewScale = {avatar.PreviewScale.ToString("F2", CultureInfo.InvariantCulture)}");
            sb.AppendLine(" },");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // 3. ABILITIES TABLE
        sb.AppendLine("HeroDefinitionsCache.Abilities = {");
        foreach (AbilityDefinitionDto ability in container.Abilities.OrderBy(a => a.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"\t['{Escape(ability.Name)}'] = {{ ");
            sb.Append($"name = '{Escape(ability.Name)}', ");
            sb.Append($"displayName = '{Escape(ability.DisplayName)}', ");
            sb.Append($"icon = '{Escape(ability.Icon)}', ");
            sb.Append($"slot = {ability.Slot}, ");
            sb.Append($"actionType = '{Escape(ability.ActionType)}', ");
            sb.Append($"manaCost = '{Escape(ability.ManaCost)}', ");
            sb.Append($"cooldown = '{Escape(ability.CooldownTime)}'");
            sb.AppendLine(" },");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // 4. ITEMS TABLE
        sb.AppendLine("HeroDefinitionsCache.Items = {");
        foreach (ItemDefinitionDto item in container.Items.OrderBy(it => it.Name, StringComparer.OrdinalIgnoreCase))
        {
            string icon = !string.IsNullOrWhiteSpace(item.Icon) ? item.Icon : item.IconPath;
            sb.Append($"\t['{Escape(item.Name)}'] = {{ ");
            sb.Append($"name = '{Escape(item.Name)}', ");
            sb.Append($"displayName = '{Escape(item.DisplayName)}', ");
            sb.Append($"icon = '{Escape(icon)}', ");
            sb.Append($"cost = {item.Cost}");
            sb.AppendLine(" },");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // 5. COSMETICS TABLE
        sb.AppendLine("HeroDefinitionsCache.Cosmetics = {");
        if (container.CosmeticUpgrades != null)
            foreach (CosmeticUpgradeDto cos in container.CosmeticUpgrades.OrderBy(c => c.Name,
                         StringComparer.OrdinalIgnoreCase))
            {
                sb.Append($"\t['{Escape(cos.Name)}'] = {{ ");
                sb.Append($"name = '{Escape(cos.Name)}', ");
                sb.Append($"category = '{Escape(cos.Category)}', ");
                sb.Append($"texture = '{Escape(cos.Texture)}', ");
                sb.Append($"modifier = '{Escape(cos.Modifier)}', ");
                sb.Append($"color = '{Escape(cos.Color)}'");
                sb.AppendLine(" },");
            }

        sb.AppendLine("}");
        sb.AppendLine();

        // 6. FAST ALIAS MAP & LOOKUP FUNCTIONS
        sb.AppendLine(@"-- Fast alias lookup maps
local aliasMap = {}
local folderAliases = {
	['plague rider'] = 'diseasedrider',
	['plaguerider'] = 'diseasedrider',
	['soul reaper'] = 'helldemon',
	['soulreaper'] = 'helldemon',
	['magmus'] = 'magmar',
	['myrmidon'] = 'hydromancer',
	['blacksmith'] = 'dwarf_magi',
	['slither'] = 'ebulus',
	['wretched hag'] = 'babayaga',
	['wretchedhag'] = 'babayaga',
	['witch slayer'] = 'witchslayer',
	['witch_slayer'] = 'witchslayer',
	['the chipper'] = 'chipper',
	['thechipper'] = 'chipper',
	['the dark lady'] = 'darklady',
	['thedarklady'] = 'darklady',
	['the gladiator'] = 'gladiator',
	['thegladiator'] = 'gladiator',
	['the madman'] = 'scar',
	['themadman'] = 'scar',
	['madman'] = 'scar',
	['night hound'] = 'hunter',
	['nighthound'] = 'hunter',
	['war beast'] = 'wolfman',
	['warbeast'] = 'wolfman',
	['lord salforis'] = 'dreadknight',
	['lordsalforis'] = 'dreadknight',
	['salforis'] = 'dreadknight',
	['keeper of the forest'] = 'treant',
	['keeperoftheforest'] = 'treant',
	['drunken master'] = 'drunkenmaster',
	['drunkenmaster'] = 'drunkenmaster',
	['flint beastwood'] = 'flint_beastwood',
	['flintbeastwood'] = 'flint_beastwood',
	['corrupted disciple'] = 'corrupted_disciple',
	['corrupteddisciple'] = 'corrupted_disciple',
	['doctor repulsor'] = 'doctor_repulsor',
	['doctorrepulsor'] = 'doctor_repulsor',
	['monkey king'] = 'monkeyking',
	['monkeyking'] = 'monkeyking',
	['moon queen'] = 'krixi',
	['moonqueen'] = 'krixi',
	['voodoo jester'] = 'voodoo',
	['voodoojester'] = 'voodoo',
	['king klout'] = 'klout',
	['kingklout'] = 'klout',
	['sir benzington'] = 'benzington',
	['sirbenzington'] = 'benzington',
	['master of arms'] = 'master_of_arms',
	['masterofarms'] = 'master_of_arms',
	['amun-ra'] = 'ra',
	['amun_ra'] = 'ra',
	['amun ra'] = 'ra',
	['amunra'] = 'ra',
	['prisoner 945'] = 'prisoner',
	['prisoner945'] = 'prisoner',
	['dwarf magi'] = 'dwarf_magi',
	['dwarfmagi'] = 'dwarf_magi'
}

for k, v in pairs(HeroDefinitionsCache.Heroes) do
	aliasMap[k:lower()] = v
	local short = k:gsub('^Hero_', ''):lower()
	aliasMap[short] = v
	if v.dir then
		aliasMap[v.dir:lower()] = v
		aliasMap[v.dir:lower():gsub('_', '')] = v
	end
	if v.displayName then
		local disp = v.displayName:lower()
		aliasMap[disp] = v
		aliasMap[disp:gsub('%s+', '')] = v
		aliasMap[disp:gsub('%s+', '_')] = v
	end
end

for alias, targetDir in pairs(folderAliases) do
	if not aliasMap[alias] and aliasMap[targetDir] then
		aliasMap[alias] = aliasMap[targetDir]
	end
end

local itemAliasMap = {}
for k, v in pairs(HeroDefinitionsCache.Items) do
	itemAliasMap[k:lower()] = v
	local short = k:gsub('^Item_', ''):lower()
	itemAliasMap[short] = v
	if v.displayName then
		local disp = v.displayName:lower()
		itemAliasMap[disp] = v
		itemAliasMap[disp:gsub('%s+', '')] = v
		itemAliasMap[disp:gsub('%s+', '_')] = v
	end
end

local cosmeticAliasMap = {}
for k, v in pairs(HeroDefinitionsCache.Cosmetics) do
	cosmeticAliasMap[k:lower()] = v
	local short = k:gsub('^aa%.', ''):gsub('^ai%.', ''):gsub('^cs%.', ''):gsub('^cc%.', ''):gsub('^accounticon_', ''):lower()
	cosmeticAliasMap[short] = v
	if v.texture then
		local filename = v.texture:match('/([^/]+)%.%w+$')
		if filename then cosmeticAliasMap[filename:lower()] = v end
	end
end

HeroDefinitionsCache.Aliases = aliasMap
HeroDefinitionsCache.ItemAliases = itemAliasMap
HeroDefinitionsCache.CosmeticAliases = cosmeticAliasMap

HeroDefinitionsCache.HeroIdMap = {}
for _, hero in pairs(HeroDefinitionsCache.Heroes) do
	if hero.id then
		HeroDefinitionsCache.HeroIdMap[hero.id] = hero
	end
end

HeroDefinitionsCache.ShopCategories = {
	['Shop_Supplies'] = { 'Item_MinorTotem', 'Item_PretendersCrown', 'Item_MarkOfTheNovice', 'Item_CrushingClaws', 'Item_DuckBoots', 'Item_HealthPotion', 'Item_RunesOfTheBlight', 'Item_ManaPotion', 'Item_ManaStone', 'Item_HomecomingStone', 'Item_FlamingEye', 'Item_DustOfRevelation', 'Item_Bottle', 'Item_EyeOfRevelation', 'Item_IronBuckler' },
	['Shop_Accessories'] = { 'Item_Marchers', 'Item_GuardianRing', 'Item_Ringmail', 'Item_EnhancedMarchers', 'Item_Steamboots', 'Item_Striders', 'Item_PostHaste', 'Item_PlatedGreaves', 'Item_GhostMarchers', 'Item_MajorTotem', 'Item_RingOfSorcery', 'Item_BloodChalice', 'Item_GraveLocket', 'Item_Astrolabe', 'Item_NomesWisdom', 'Item_SolsGulston', 'Item_ShieldOfTheFive', 'Item_RingOfTheTeacher' },
	['Shop_Weapons'] = { 'Item_Punchdagger', 'Item_Broadsword', 'Item_Warhammer', 'Item_HelmOfTheVictim', 'Item_Platemail', 'Item_Steamstaff', 'Item_LoggersHatchet', 'Item_ToxinClaws', 'Item_Halberd', 'Item_AlacrityBand', 'Item_Boltx', 'Item_HeavyBlade', 'Item_AcolytesStaff' },
	['Shop_Relics'] = { 'Item_BlessedOrb', 'Item_HungrySpirit', 'Item_MysticVestments', 'Item_StaffOfMastery', 'Item_BrainOfMaliken', 'Item_MightyBlade', 'Item_Quickblade', 'Item_NeophytesBook', 'Item_Warpcft', 'Item_Relic' },
	['Shop_Secret'] = { 'Item_ManaRegen3', 'Item_HealthRegen2', 'Item_BrainOfMaliken', 'Item_NeophytesBook', 'Item_BlessedOrb', 'Item_StaffOfMastery', 'Item_Bottle', 'Item_Relic' },
	['Shop_Recipes1'] = { 'Item_Replenish', 'Item_LifeSteal', 'Item_NomesWisdom', 'Item_Astrolabe', 'Item_PushStaff', 'Item_BarrierIdol', 'Item_Excruciator', 'Item_PortalKey' },
	['Shop_Recipes2'] = { 'Item_Strength6', 'Item_Intelligence7', 'Item_SpellShards', 'Item_RestorationStone', 'Item_Immunity', 'Item_Morph', 'Item_Hellflower', 'Item_BehemothsHeart' },
	['Shop_Recipes3'] = { 'Item_ElderParasite', 'Item_WhisperingHelm', 'Item_ManaBurn1', 'Item_ManaBurn2', 'Item_Sicarius', 'Item_Lightning1', 'Item_Lightning2', 'Item_Critical1', 'Item_Critical2', 'Item_SavageMace', 'Item_Shieldbreaker', 'Item_Dawnbringer', 'Item_FrostwolfSkull', 'Item_Weapon1', 'Item_Weapon3' },
	['Shop_Recipes4'] = { 'Item_Armor3', 'Item_Armor4', 'Item_Damage1', 'Item_Brutalizer', 'Item_Evasion', 'Item_Deflector', 'Item_NullStone', 'Item_Protect', 'Item_MagicArmor2', 'Item_BarbedArmor' },
	['Shop_Recipes5'] = { 'Item_Dawnbringer', 'Item_GrimoireOfPower', 'Item_HarkonsBlade', 'Item_PowerSupply', 'Item_BloodborneMaul', 'Item_SpellwarpToken', 'Item_Intelligence4' },
	['Shop_Observatory'] = { 'Item_HomecomingStone', 'Item_FlamingEye', 'Item_DustOfRevelation', 'Item_Bottle', 'Item_EyeOfRevelation' }
}

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	if item and item.icon and item.icon ~= '' then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	if item and item.icon and item.icon ~= '' then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	if item and item.icon and item.icon ~= '' then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	if item and item.icon and item.icon ~= '' then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	if item and item.icon and item.icon ~= '' then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHero(heroName)
	if not heroName or heroName == '' then return nil end
	if HeroDefinitionsCache.Heroes then
		return HeroDefinitionsCache.Heroes[heroName] or HeroDefinitionsCache.Heroes['Hero_' .. tostring(heroName):gsub('^Hero_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	local heroName, altPart = tostring(avatarKey):match('^([^%.]+)%.?(.*)$')
	heroName = heroName or avatarKey
	local hero = HeroDefinitionsCache.GetHero(heroName)
	if hero and hero.avatars then
		return hero.avatars[avatarKey] or hero.avatars[altPart] or hero.avatars[heroName .. '.' .. tostring(altPart or '')]
	end
	return nil
end

function HeroDefinitionsCache.GetHeroIcon(heroKey)
	local hero = HeroDefinitionsCache.GetHero(heroKey)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetItem(itemName)
	if not itemName or itemName == '' then return nil end
	if HeroDefinitionsCache.Items then
		return HeroDefinitionsCache.Items[itemName] 
			or HeroDefinitionsCache.Items['Item_' .. tostring(itemName):gsub('^Item_', '')] 
			or HeroDefinitionsCache.Items[tostring(itemName):gsub('^Item_', '')]
	end
	return nil
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = (HeroDefinitionsCache.GetItem and HeroDefinitionsCache.GetItem(itemName)) or (HeroDefinitionsCache.Items and HeroDefinitionsCache.Items[itemName]) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = HeroDefinitionsCache.GetItem(itemName) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = HeroDefinitionsCache.GetItem(itemName) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = HeroDefinitionsCache.GetItem(itemName) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = HeroDefinitionsCache.GetItem(itemName) or { name = itemName, cost = 0, displayName = itemName }
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(itemName)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(itemName)) or itemData.displayName or itemName
			local itIcon = (HeroDefinitionsCache.GetItemIcon and HeroDefinitionsCache.GetItemIcon(itemName)) or itemData.icon or (GetEntityIconPath and GetEntityIconPath(itemName)) or ''
			table.insert(items, {
				itemName = itemData.name or itemName,
				name = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itIcon,
				components = comps
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			local rec = HeroDefinitionsCache.GetItemRecipe and HeroDefinitionsCache.GetItemRecipe(name)
			local comps = (rec and rec.components) or itemData.components or {}
			local costOnly = (rec and rec.costOnly) or itemData.costOnly or 0
			local dispName = (HeroDefinitionsCache.GetItemDisplayName and HeroDefinitionsCache.GetItemDisplayName(name)) or itemData.displayName or name
			table.insert(items, {
				itemName = itemData.name or name,
				name = itemData.name or name,
				itemCost = itemData.cost or 0,
				cost = itemData.cost or 0,
				itemCostOnly = costOnly,
				costOnly = costOnly,
				displayName = dispName,
				icon = itemData.icon or (GetEntityIconPath and GetEntityIconPath(name)) or '',
				components = comps
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

for k, v in pairs(HeroDefinitionsCache.ShopCategories) do
	HeroDefinitionsCache.ShopCategories[k:lower()] = v
end

function HeroDefinitionsCache.GetHero(heroIdentifier)
	if not heroIdentifier or heroIdentifier == '' then return nil end
	local num = tonumber(heroIdentifier)
	if num and HeroDefinitionsCache.HeroIdMap and HeroDefinitionsCache.HeroIdMap[num] then
		return HeroDefinitionsCache.HeroIdMap[num]
	end
	local clean = tostring(heroIdentifier):lower():gsub('^hero_', '')
	return aliasMap[tostring(heroIdentifier):lower()] or aliasMap[clean] or HeroDefinitionsCache.Heroes[heroIdentifier]
end

function HeroDefinitionsCache.GetHeroIcon(heroIdentifier)
	if not heroIdentifier or heroIdentifier == '' or heroIdentifier == '0' or heroIdentifier == 'none' or heroIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local hero = HeroDefinitionsCache.GetHero(heroIdentifier)
	if hero and hero.icon and hero.icon ~= '' then
		return hero.icon
	end
	if hero and hero.dir and hero.dir ~= '' then
		return '/heroes/' .. hero.dir .. '/icon.tga'
	end
	local clean = tostring(heroIdentifier or ''):gsub('^Hero_', ''):lower():gsub('%s+', '_')
	if folderAliases[clean] then clean = folderAliases[clean] end
	if clean == '' or clean == '0' or clean == 'none' or clean == 'null' then return '/ui/common/ability_coverup.tga' end
	return '/heroes/' .. clean .. '/icon.tga'
end

function HeroDefinitionsCache.GetHeroDisplayName(heroIdentifier)
	if not heroIdentifier or heroIdentifier == '' then return '' end
	local hero = HeroDefinitionsCache.GetHero(heroIdentifier)
	if hero and hero.displayName and hero.displayName ~= '' then
		return hero.displayName
	end
	return tostring(heroIdentifier):gsub('^Hero_', '')
end

function HeroDefinitionsCache.GetAvatar(avatarKey)
	if not avatarKey or avatarKey == '' then return nil end
	return HeroDefinitionsCache.AltAvatars[avatarKey]
end

function HeroDefinitionsCache.GetAvatarIcon(avatarKey)
	local av = HeroDefinitionsCache.GetAvatar(avatarKey)
	if av and av.icon and av.icon ~= '' then
		return av.icon
	end
	local baseHeroKey = tostring(avatarKey or ''):gsub('%..*$', '')
	return HeroDefinitionsCache.GetHeroIcon(baseHeroKey)
end

function HeroDefinitionsCache.GetAbility(abilityKey)
	if not abilityKey or abilityKey == '' then return nil end
	return HeroDefinitionsCache.Abilities[abilityKey]
end

function HeroDefinitionsCache.GetAbilityIcon(abilityKey)
	local ab = HeroDefinitionsCache.GetAbility(abilityKey)
	if ab and ab.icon and ab.icon ~= '' then
		return ab.icon
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetItem(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' then return nil end
	local clean = tostring(itemIdentifier):lower():gsub('^item_', '')
	return itemAliasMap[tostring(itemIdentifier):lower()] or itemAliasMap[clean] or HeroDefinitionsCache.Items[itemIdentifier]
end

function HeroDefinitionsCache.GetItemDisplayName(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' then return '' end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.displayName and item.displayName ~= '' and item.displayName ~= itemIdentifier then
		return item.displayName
	end
	local clean = tostring(itemIdentifier):gsub('^Item_', '')
	clean = clean:gsub('([a-z])([A-Z])', '%1 %2')
	return clean
end

function HeroDefinitionsCache.GetItemIcon(itemIdentifier)
	if not itemIdentifier or itemIdentifier == '' or itemIdentifier == '0' or itemIdentifier == 'none' or itemIdentifier == 'null' then
		return '/ui/common/ability_coverup.tga'
	end
	local raw = tostring(itemIdentifier)
	local clean = raw:gsub('^Item_', '')
	if HeroDefinitionsCache.KnownItemIcons then
		local mapped = HeroDefinitionsCache.KnownItemIcons[raw] or HeroDefinitionsCache.KnownItemIcons[clean] or HeroDefinitionsCache.KnownItemIcons[raw:lower()] or HeroDefinitionsCache.KnownItemIcons[clean:lower()]
		if mapped and mapped ~= '' then return mapped end
	end
	local item = HeroDefinitionsCache.GetItem(itemIdentifier)
	if item and item.icon and item.icon ~= '' and not item.icon:find('^/items/[^/]+/icon%.tga') then
		return item.icon
	end
	return '/ui/common/ability_coverup.tga'
end

local function GetCustomAccountIconTexture(accountid, slot)
	local aid = tonumber(accountid) or 0
	if aid <= 0 then
		aid = (GetAccountID and tonumber(GetAccountID())) or (Client and Client.GetAccountID and tonumber(Client.GetAccountID())) or (UIGetAccountID and tonumber(UIGetAccountID())) or 0
	end
	local s = tonumber(slot) or 1
	local base = (GetICBURL and GetICBURL()) or (GetMasterServerURL and GetMasterServerURL()) or 'http://127.0.0.1:55555'
	if aid > 0 then
		local p0 = math.floor(aid / 1000000)
		local p1 = math.floor((aid - p0 * 1000000) / 1000)
		return '!' .. base .. '/icons/' .. p0 .. '/' .. p1 .. '/' .. aid .. '/' .. s .. '.cai'
	end
	local name = (GetAccountName and GetAccountName()) or ''
	if name ~= '' then
		local chatInfo = GetChatClientInfo and GetChatClientInfo(StripClanTag(name), 'getaccounticontexturepath')
		if chatInfo and chatInfo ~= '' then
			local parts = Explode('|', chatInfo)
			if parts and parts[1] and parts[1] ~= '' and not parts[1]:lower():find('default%.tga') and not parts[1]:lower():find('mole%.tga') and not parts[1]:lower():find('chiprel') then
				return parts[1]
			end
		end
		return '!' .. base .. '/icons/0/0/' .. tostring(StripClanTag(name)) .. '/' .. s .. '.cai'
	end
	return ''
end
_G.GetCustomAccountIconTexture = GetCustomAccountIconTexture

function HeroDefinitionsCache.GetAccountIcon(iconIdentifier, account_id)
	if not iconIdentifier or iconIdentifier == '' then return nil end
	local raw = tostring(iconIdentifier):lower()
	if raw:find('^ai%.custom_icon') or raw:find('^custom_icon') then
		local slot = raw:match(':(%d+)') or 1
		return GetCustomAccountIconTexture(account_id, slot)
	end
	if raw == '0' then
		return ''
	end
	if raw:find('^cs%.') or raw:find('^cc%.') or raw:find('^t%.') or raw:find('^c%.') or raw:find('^av%.') or raw:find('^w%.') then
		return ''
	end
	local clean = raw:gsub('^aa%.', ''):gsub('^ai%.', ''):gsub('^accounticon_', '')
	if clean:find('^custom_icon') then
		local slot = clean:match(':(%d+)') or 1
		return GetCustomAccountIconTexture(account_id, slot)
	end
	if clean == '0' or clean == '' then
		return ''
	end
	local cos = cosmeticAliasMap[raw] or cosmeticAliasMap[clean] or HeroDefinitionsCache.Cosmetics[iconIdentifier] or HeroDefinitionsCache.Cosmetics[clean]
	if cos and cos.texture and cos.texture ~= '' and not cos.texture:lower():find('default%.tga') and not cos.texture:lower():find('mole%.tga') and not cos.texture:lower():find('chiprel') then
		return cos.texture
	end
	if clean ~= '' and clean ~= '0' then
		return '/ui/fe2/store/icons/account_icons/' .. clean .. '.tga'
	end
	return '/ui/common/ability_coverup.tga'
end

function HeroDefinitionsCache.GetChatSymbol(symbolIdentifier)
	if not symbolIdentifier or symbolIdentifier == '' or symbolIdentifier == '0' then return '$invis' end
	local raw = tostring(symbolIdentifier):lower()
	if raw == '$invis' or raw == 'none' then return '$invis' end
	if raw:find('^ai%.') or raw:find('^cc%.') or raw:find('^t%.') or raw:find('^c%.') or raw:find('^av%.') or raw:find('^w%.') or raw:find('^custom_icon') then
		return '$invis'
	end
	local clean = raw:gsub('^cs%.', ''):gsub('^chatsymbol_', '')
	if clean == '' or clean == '0' or clean == 'none' or clean == '$invis' then return '$invis' end
	local cos = cosmeticAliasMap[raw] or cosmeticAliasMap[clean] or HeroDefinitionsCache.Cosmetics[symbolIdentifier] or HeroDefinitionsCache.Cosmetics[clean]
	if cos and cos.texture and cos.texture ~= '' then
		return cos.texture
	end
	if clean ~= '' and clean ~= '0' then
		return '/ui/fe2/store/icons/chat_symbols/' .. clean .. '.tga'
	end
	return '$invis'
end

function HeroDefinitionsCache.GetShopItems(catType)
	local items = {}
	local categoryList = HeroDefinitionsCache.ShopCategories and (HeroDefinitionsCache.ShopCategories[catType] or HeroDefinitionsCache.ShopCategories[tostring(catType):lower()])
	if categoryList then
		for _, itemName in ipairs(categoryList) do
			local itemData = HeroDefinitionsCache.GetItem(itemName) or { name = itemName, cost = 0, displayName = itemName }
			table.insert(items, {
				itemName = itemData.name or itemName,
				itemCost = itemData.cost or 0,
				itemCostOnly = itemData.cost or 0,
				displayName = itemData.displayName or itemName,
				components = itemData.components or {}
			})
		end
	elseif HeroDefinitionsCache.Items then
		for name, itemData in pairs(HeroDefinitionsCache.Items) do
			table.insert(items, {
				itemName = itemData.name or name,
				itemCost = itemData.cost or 0,
				itemCostOnly = itemData.cost or 0,
				displayName = itemData.displayName or name,
				components = itemData.components or {}
			})
		end
		table.sort(items, function(a, b) return (a.itemName or '') < (b.itemName or '') end)
	end
	return items
end

function HeroDefinitionsCache.ParseMasteryInfo(masteryInfo)
	local result = {}
	local seen = {}
	if type(masteryInfo) == 'table' then
		for k, v in pairs(masteryInfo) do
			if type(v) == 'table' then
				local rawName = v.heroname or v.heroName or v.name or k
				local heroDisplayName = HeroDefinitionsCache.GetHeroDisplayName(rawName)
				local heroIcon = HeroDefinitionsCache.GetHeroIcon(rawName)
				local exp = tonumber(v.exp or v.experience or v.level_exp or 0) or 0
				local level = tonumber(v.level or 0) or 0
				if level == 0 and exp > 0 then
					level = math.max(1, math.floor(exp / 1000))
				end
				seen[rawName:lower()] = true
				if heroDisplayName then seen[heroDisplayName:lower()] = true end
				table.insert(result, {
					heroName = heroDisplayName,
					rawHeroName = rawName,
					heroIcon = heroIcon,
					level = level,
					exp = exp,
					own = true
				})
			end
		end
	end
	
	-- Populate all remaining heroes from cache so every slot has an icon and name
	for name, data in pairs(HeroDefinitionsCache.Heroes) do
		if not seen[name:lower()] and not seen[(data.displayName or ''):lower()] then
			table.insert(result, {
				heroName = data.displayName or name,
				rawHeroName = name,
				heroIcon = data.icon or ('/heroes/' .. data.dir .. '/icon.tga'),
				level = 0,
				exp = 0,
				own = true
			})
		end
	end
	
	return result
end

function HeroDefinitionsCache.PopulateCompendium()
	local listLegionAgi = GetWidget and GetWidget('compendium_simple_list_legion_agi')
	local listHellAgi = GetWidget and GetWidget('compendium_simple_list_hellbourne_agi')
	local listLegionInt = GetWidget and GetWidget('compendium_simple_list_legion_int')
	local listHellInt = GetWidget and GetWidget('compendium_simple_list_hellbourne_int')
	local listLegionStr = GetWidget and GetWidget('compendium_simple_list_legion_str')
	local listHellStr = GetWidget and GetWidget('compendium_simple_list_hellbourne_str')

	local listDetailed1 = GetWidget and (GetWidget('compendium_listbox_detailed1') or GetWidget('compendium_listbox_detailed'))
	local listDetailed0 = GetWidget and GetWidget('compendium_listbox_detailed0')

	local sortedKeys = {}
	for k in pairs(HeroDefinitionsCache.Heroes) do table.insert(sortedKeys, k) end
	table.sort(sortedKeys)

	for _, entityName in ipairs(sortedKeys) do
		local data = HeroDefinitionsCache.Heroes[entityName]
		local faction = data.faction or 'Legion'
		local attr = (data.attr or 'agility'):lower()
		local targetList = nil

		if attr == 'agility' then
			targetList = (faction == 'Legion') and listLegionAgi or listHellAgi
		elseif attr == 'intelligence' then
			targetList = (faction == 'Legion') and listLegionInt or listHellInt
		else
			targetList = (faction == 'Legion') and listLegionStr or listHellStr
		end

		local attrIcon = '/ui/common/primary_agility.tga'
		if attr == 'strength' then
			attrIcon = '/ui/common/primary_strength.tga'
		elseif attr == 'intelligence' then
			attrIcon = '/ui/common/primary_intelligence.tga'
		end

		if targetList then
			targetList:AddTemplateListItem('simple_compendium_hero_item', entityName,
				'icon', data.icon or ('/heroes/' .. data.dir .. '/icon.tga'),
				'heroentity', entityName,
				'attribute_icon', attrIcon,
				'attribute', attr,
				'faction', faction,
				'description', data.displayName or entityName,
				'attack_type', data.attackType or 'Melee',
				'name', data.displayName or entityName,
				'ownership', '1',
				'cost', '0',
				'purchasable', '0',
				'premium', '0',
				'premiumsilvercost', '0',
				'eap', 'false',
				'free', 'true',
				'haseap', 'false',
				'magicarmor', tostring(data.magicArmor or 5.5),
				'trial', ''
			)
		end

		-- Detailed hero list
		local dList = listDetailed1 or listDetailed0
		if dList then
			local atkIcon = (data.attackType == 'Ranged') and '/ui/fe2/elements/store2/ranged.png' or '/ui/fe2/elements/store2/melee.png'
			local ab0 = (data.abilities and data.abilities[1] and data.abilities[1].icon) or ''
			local ab1 = (data.abilities and data.abilities[2] and data.abilities[2].icon) or ''
			local ab2 = (data.abilities and data.abilities[3] and data.abilities[3].icon) or ''
			local ab3 = (data.abilities and data.abilities[4] and data.abilities[4].icon) or ''
			local sortKey = (data.displayName or entityName) .. ',' .. attr .. ',' .. atkIcon .. ',' .. string.format('%04d', math.floor(data.strength or 20)) .. ',' .. string.format('%04d', math.floor(data.agility or 20)) .. ',' .. string.format('%04d', math.floor(data.intelligence or 20)) .. ',' .. string.format('%04d', math.floor((data.armor or 3) * 10)) .. ',' .. string.format('%04d', math.floor(((data.attackDamageMin or 45) + (data.attackDamageMax or 55)) / 2)) .. ',' .. string.format('%04d', math.floor(data.moveSpeed or 300))

			dList:AddTemplateListItemWithSort('hero_list_item', entityName, sortKey,
				'hero_name', data.displayName or entityName,
				'attribute', attr,
				'strength', tostring(math.floor(data.strength or 20)),
				'strength_per_level', string.format('%.1f', data.strengthPerLevel or 2.0),
				'agility', tostring(math.floor(data.agility or 20)),
				'agility_per_level', string.format('%.1f', data.agilityPerLevel or 2.0),
				'intelligence', tostring(math.floor(data.intelligence or 20)),
				'intelligence_per_level', string.format('%.1f', data.intelligencePerLevel or 2.0),
				'armor', string.format('%.1f', data.armor or 3.0),
				'atk_dmg_min', tostring(math.floor(data.attackDamageMin or 45)),
				'atk_dmg_max', tostring(math.floor(data.attackDamageMax or 55)),
				'movespeed', tostring(math.floor(data.moveSpeed or 300)),
				'mindamage', tostring(math.floor(data.attackDamageMin or 45)),
				'maxdamage', tostring(math.floor(data.attackDamageMax or 55)),
				'hero_icon', data.icon or ('/heroes/' .. data.dir .. '/icon.tga'),
				'ability_0_icon', ab0,
				'ability_1_icon', ab1,
				'ability_2_icon', ab2,
				'ability_3_icon', ab3,
				'attribute_icon', attrIcon,
				'armor_percent', '15%',
				'magic_armor_percent', '25%',
				'DPS', tostring(math.floor((data.attackDamageMin or 45) * 0.8)),
				'attack_range', tostring(math.floor(data.attackRange or 150)),
				'attack_type_icon', atkIcon,
				'faction', faction,
				'ownership', '1',
				'cost', '0',
				'purchasable', '0',
				'premium', '0',
				'premiumsilvercost', '0',
				'eap', 'false',
				'free', 'true',
				'haseap', 'false',
				'value', entityName,
				'magicarmor', tostring(data.magicArmor or 5.5)
			)
		end
	end
end

-- Global Engine Hooks for instant 0ms Lookups
local originalGetEntityIconPath = _G.GetEntityIconPath
function _G.GetEntityIconPath(entityIdentifier)
	if not entityIdentifier or entityIdentifier == '' then return '' end
	local cleanStr = tostring(entityIdentifier)
	if cleanStr:find('^Item_') or cleanStr:find('^item_') then
		if HeroDefinitionsCache and HeroDefinitionsCache.GetItemIcon then
			local icon = HeroDefinitionsCache.GetItemIcon(cleanStr)
			if icon and icon ~= '' and icon ~= '/ui/common/empty_pack_garena.tga' then
				return icon
			end
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetHeroIcon then
		local icon = HeroDefinitionsCache.GetHeroIcon(cleanStr)
		if icon and icon ~= '' and icon ~= '/ui/icons/empty.tga' and icon ~= '/ui/common/ability_coverup.tga' then
			return icon
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetAvatarIcon then
		local icon = HeroDefinitionsCache.GetAvatarIcon(cleanStr)
		if icon and icon ~= '' and icon ~= '/ui/common/ability_coverup.tga' then
			return icon
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetItemIcon then
		local icon = HeroDefinitionsCache.GetItemIcon(cleanStr)
		if icon and icon ~= '' and icon ~= '/ui/common/empty_pack_garena.tga' then
			return icon
		end
	end
	if originalGetEntityIconPath then
		local ok, orig = pcall(originalGetEntityIconPath, entityIdentifier)
		if ok and orig and orig ~= '' then return orig end
	end
	if cleanStr:lower():find('^item_') then
		return '/items/' .. cleanStr:lower():gsub('^item_', '') .. '/icon.tga'
	end
	return HeroDefinitionsCache.GetHeroIcon(cleanStr)
end

local originalGetEntityDisplayName = _G.GetEntityDisplayName
function _G.GetEntityDisplayName(heroIdentifier)
	if not heroIdentifier or heroIdentifier == '' then return '' end
	local cleanStr = tostring(heroIdentifier)
	if HeroDefinitionsCache and HeroDefinitionsCache.GetHeroDisplayName then
		local name = HeroDefinitionsCache.GetHeroDisplayName(cleanStr)
		if name and name ~= '' and name ~= cleanStr and name ~= cleanStr:gsub('^Hero_', '') then
			return name
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetItemDisplayName then
		local itemDisp = HeroDefinitionsCache.GetItemDisplayName(cleanStr)
		if itemDisp and itemDisp ~= '' and itemDisp ~= cleanStr then
			return itemDisp
		end
	end
	if originalGetEntityDisplayName then
		local ok, orig = pcall(originalGetEntityDisplayName, heroIdentifier)
		if ok and orig and orig ~= '' then return orig end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetHeroDisplayName then
		return HeroDefinitionsCache.GetHeroDisplayName(cleanStr)
	end
	return cleanStr:gsub('^Hero_', ''):gsub('^Item_', '')
end

local originalGetAvatarIconPath = _G.GetAvatarIconPath
function _G.GetAvatarIconPath(avatarKey)
	if not avatarKey or avatarKey == '' then return '' end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetAvatarIcon then
		local icon = HeroDefinitionsCache.GetAvatarIcon(avatarKey)
		if icon and icon ~= '' then
			return icon
		end
	end
	if originalGetAvatarIconPath then
		local ok, orig = pcall(originalGetAvatarIconPath, avatarKey)
		if ok and orig and orig ~= '' then return orig end
	end
	return HeroDefinitionsCache.GetHeroIcon(avatarKey)
end

local originalGetAltAvatars = _G.GetAltAvatars
function _G.GetAltAvatars(heroName)
	local alts = {}
	local seen = {}
	if originalGetAltAvatars then
		local ok, res = pcall(originalGetAltAvatars, heroName)
		if ok and res and type(res) == 'table' and #res > 0 then
			for _, av in ipairs(res) do
				table.insert(alts, av)
				if av.Name then
					seen[av.Name:lower()] = true
					seen[(heroName .. '.' .. av.Name):lower()] = true
				end
			end
		end
	end

	if HeroDefinitionsCache and HeroDefinitionsCache.GetHero then
		local hero = HeroDefinitionsCache.GetHero(heroName)
		if hero and hero.avatars then
			if #alts == 0 then
				table.insert(alts, {
					Name = 'Base',
					TypeName = hero.name,
					DisplayName = hero.displayName or 'Default',
					AvatarDisplayName = hero.displayName or 'Default',
					Icon = hero.icon,
					DefaultModel = hero.model or 'preview.mdf',
					PreviewModel = hero.model or 'preview.mdf',
					Available = true,
					Cost = 0,
					PremiumCost = 0
				})
				seen['base'] = true
				seen['hero'] = true
			end

			local sortedAltKeys = {}
			for key, _ in pairs(hero.avatars) do
				table.insert(sortedAltKeys, key)
			end
			table.sort(sortedAltKeys)

			for _, key in ipairs(sortedAltKeys) do
				local av = hero.avatars[key]
				local shortName = key:gsub('^' .. hero.name .. '%.', '')
				if shortName ~= 'Hero' and shortName ~= 'Base' and not seen[shortName:lower()] and not seen[key:lower()] then
					local isOwned = false
					if not IsMasterServerConnected or not IsMasterServerConnected() then
						isOwned = true
					else
						if CanAccessAltAvatar then
							isOwned = CanAccessAltAvatar(hero.name .. '.' .. shortName) or CanAccessAltAvatar(key)
						end
						if not isOwned and CanAccessProduct then
							isOwned = CanAccessProduct(hero.name .. '.' .. shortName) or CanAccessProduct(key)
						end
						if (hero.name == 'Hero_Bubbles' and shortName == 'Alt16') or key == 'Hero_Bubbles.Alt16' then
							isOwned = true
						end
					end

					local dispName = (hero.name == 'Hero_Bubbles' and shortName == 'Alt16') and 'Abyssal Bubbles' or (av.avatarDisplayName or av.displayName or shortName)
					table.insert(alts, {
						Name = shortName,
						TypeName = hero.name,
						DisplayName = dispName,
						AvatarDisplayName = dispName,
						Icon = (av.icon and av.icon ~= '') and av.icon or hero.icon,
						DefaultModel = av.model or 'preview.mdf',
						PreviewModel = av.model or 'preview.mdf',
						Available = isOwned,
						Cost = 0,
						PremiumCost = 0
					})
					seen[shortName:lower()] = true
					seen[key:lower()] = true
				end
			end
		end
	end

	for _, av in ipairs(alts) do
		if av.TypeName == 'Hero_Bubbles' and av.Name == 'Alt16' then
			av.Available = true
			if not av.AvatarDisplayName or av.AvatarDisplayName == '' or av.AvatarDisplayName == 'Hero_Bubbles.Alt16' or av.AvatarDisplayName == 'Alt16' then
				av.AvatarDisplayName = 'Abyssal Bubbles'
			end
			if not av.DisplayName or av.DisplayName == '' or av.DisplayName == 'Hero_Bubbles.Alt16' or av.DisplayName == 'Alt16' then
				av.DisplayName = 'Abyssal Bubbles'
			end
			if not av.Icon or av.Icon == '' then
				av.Icon = '/heroes/bubbles/alt16/icon.tga'
			end
		end
	end

	if not IsMasterServerConnected or not IsMasterServerConnected() then
		for _, av in ipairs(alts) do
			av.Available = true
		end
	end

	return alts
end

local originalGetShopItems = _G.GetShopItems
function _G.GetShopItems(catType)
	if originalGetShopItems then
		local ok, res = pcall(originalGetShopItems, catType)
		if ok and res and type(res) == 'table' and #res > 0 then
			return res
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetShopItems then
		return HeroDefinitionsCache.GetShopItems(catType)
	end
	return {}
end

local _previewModelPathCache = {}
local originalGetHeroPreviewModelPath = _G.GetHeroPreviewModelPathFromProduct
function _G.GetHeroPreviewModelPathFromProduct(heroString)
	if not heroString or heroString == '' then return '' end
	if _previewModelPathCache[heroString] then return _previewModelPathCache[heroString] end

	local isCustom = HeroDefinitionsCache and HeroDefinitionsCache.CustomAvatars and (HeroDefinitionsCache.CustomAvatars[heroString] or false)

	-- Try native C++ engine / heroes.xml preview model path first for standard avatars
	if not isCustom and originalGetHeroPreviewModelPath then
		local ok, path = pcall(originalGetHeroPreviewModelPath, heroString)
		if ok and path and path ~= '' and path ~= 'nil' then
			_previewModelPathCache[heroString] = path
			return path
		end
	end

	local heroName, altPart = heroString:match('^([^%.]+)%.?(.*)$')
	heroName = heroName or heroString
	local hero = HeroDefinitionsCache and HeroDefinitionsCache.GetHero and HeroDefinitionsCache.GetHero(heroName)
	local heroDir = (hero and hero.dir) or tostring(heroName):gsub('^Hero_', ''):lower()

	-- Base hero case
	if not altPart or altPart == '' or altPart:lower() == 'hero' or altPart:lower() == 'base' or altPart:lower() == 'classic' then
		local result = nil
		if hero and hero.model and hero.model ~= '' then
			if hero.model:find('^/') then
				result = hero.model
			else
				result = '/heroes/' .. heroDir .. '/' .. hero.model
			end
		else
			result = '/heroes/' .. heroDir .. '/preview.mdf'
		end
		_previewModelPathCache[heroString] = result
		return result
	end

	-- Alt Avatar case
	local av = nil
	if HeroDefinitionsCache and HeroDefinitionsCache.GetAvatar then
		av = HeroDefinitionsCache.GetAvatar(heroString)
	end
	if not av and hero and hero.avatars then
		av = hero.avatars[heroString] or hero.avatars[altPart]
	end

	-- Determine altFolder from icon path (e.g. /heroes/<hero>/<alt>/icon.tga) or altPart
	local altFolder = nil
	if av and av.icon and av.icon:find('^/heroes/[^/]+/([^/]+)/') then
		local candFolder = av.icon:match('^/heroes/[^/]+/([^/]+)/')
		if candFolder and candFolder:lower() ~= 'icons' and candFolder:lower() ~= 'effects' and candFolder:lower() ~= 'sounds' and candFolder:lower() ~= 'clips' and candFolder:lower() ~= 'shared' and candFolder:lower() ~= 'reskin' then
			altFolder = candFolder
		end
	end
	if not altFolder or altFolder == '' then
		altFolder = altPart:lower()
	end

	if av and av.model and av.model ~= '' then
		if av.model:find('^/') then
			_previewModelPathCache[heroString] = av.model
			return av.model
		elseif av.model:find('/') then
			local result = '/heroes/' .. heroDir .. '/' .. av.model
			_previewModelPathCache[heroString] = result
			return result
		else
			local cand = '/heroes/' .. heroDir .. '/' .. altFolder .. '/' .. av.model
			_previewModelPathCache[heroString] = cand
			return cand
		end
	end

	if originalGetHeroPreviewModelPath then
		local ok, path = pcall(originalGetHeroPreviewModelPath, heroString)
		if ok and path and path ~= '' and path ~= 'nil' then
			_previewModelPathCache[heroString] = path
			return path
		end
	end

	local fallback = '/heroes/' .. heroDir .. '/' .. altFolder .. '/preview.mdf'
	_previewModelPathCache[heroString] = fallback
	return fallback
end

local function extractUpgradeTokens(selected_upgrade)
	local tokens = {}
	if not selected_upgrade then return tokens end
	if type(selected_upgrade) == 'table' then
		for _, v in pairs(selected_upgrade) do
			if type(v) == 'string' then
				for part in v:gmatch('[^|,]+') do
					local trimmed = part:gsub('^%s*(.-)%s*$', '%1')
					if trimmed ~= '' then table.insert(tokens, trimmed) end
				end
			end
		end
	elseif type(selected_upgrade) == 'string' then
		for part in selected_upgrade:gmatch('[^|,]+') do
			local trimmed = part:gsub('^%s*(.-)%s*$', '%1')
			if trimmed ~= '' then table.insert(tokens, trimmed) end
		end
	end
	return tokens
end

local originalGetAccountIconTexturePathFromUpgrades = _G.GetAccountIconTexturePathFromUpgrades
function _G.GetAccountIconTexturePathFromUpgrades(selected_upgrade, accountid)
	if not selected_upgrade or selected_upgrade == '' or selected_upgrade == '0' then return '' end
	local tokens = extractUpgradeTokens(selected_upgrade)
	for _, token in ipairs(tokens) do
		local lower = token:lower()
		if lower:find('^ai%.custom_icon') or lower:find('^custom_icon') then
			local slot = lower:match(':(%d+)') or 1
			return GetCustomAccountIconTexture(accountid, slot)
		end
		if lower:find('^ai%.') or lower:find('^aa%.') or lower:find('^accounticon_') then
			local clean = token:gsub('^[Aa][Ii]%.', ''):gsub('^[Aa][Aa]%.', ''):gsub('^[Aa]ccount[Ii]con_', '')
			if clean:lower():find('^custom_icon') then
				local slot = clean:lower():match(':(%d+)') or 1
				return GetCustomAccountIconTexture(accountid, slot)
			end
			if clean == '0' or clean == '' then
				return ''
			end
			if HeroDefinitionsCache and HeroDefinitionsCache.GetAccountIcon then
				local icon = HeroDefinitionsCache.GetAccountIcon(clean, accountid) or HeroDefinitionsCache.GetAccountIcon(token, accountid)
				if icon and icon ~= '' and icon ~= '/ui/common/ability_coverup.tga' and icon ~= '$invis' and not icon:lower():find('default%.tga') and not icon:lower():find('mole%.tga') and not icon:lower():find('chiprel') then
					return icon
				end
			end
		end
	end
	if originalGetAccountIconTexturePathFromUpgrades then
		local ok, res = pcall(originalGetAccountIconTexturePathFromUpgrades, selected_upgrade, accountid)
		if ok and res and res ~= '' and res ~= '$invis' and res ~= '/ui/common/ability_coverup.tga' and not res:lower():find('default%.tga') and not res:lower():find('mole%.tga') and not res:lower():find('chiprel') then
			return res
		end
	end
	return ''
end

local originalGetAccountIconPath = _G.GetAccountIconPath
function _G.GetAccountIconPath(iconName)
	if not iconName or iconName == '' or iconName == '0' then return '' end
	local lower = tostring(iconName):lower()
	if lower:find('^ai%.custom_icon') or lower:find('^custom_icon') or lower:find('custom_icon') then
		local slot = lower:match(':(%d+)') or 1
		return GetCustomAccountIconTexture(nil, slot)
	end
	if originalGetAccountIconPath then
		local ok, res = pcall(originalGetAccountIconPath, iconName)
		if ok and res and res ~= '' and res ~= '$invis' and res ~= '/ui/common/ability_coverup.tga' and not res:lower():find('default%.tga') and not res:lower():find('mole%.tga') and not res:lower():find('chiprel') then
			return res
		end
	end
	if HeroDefinitionsCache and HeroDefinitionsCache.GetAccountIcon then
		local icon = HeroDefinitionsCache.GetAccountIcon(iconName)
		if icon and icon ~= '' and icon ~= '$invis' and icon ~= '/ui/common/ability_coverup.tga' and not icon:lower():find('default%.tga') and not icon:lower():find('mole%.tga') and not icon:lower():find('chiprel') then
			return icon
		end
	end
	return ''
end

local originalGetChatSymbolTexturePathFromUpgrades = _G.GetChatSymbolTexturePathFromUpgrades
function _G.GetChatSymbolTexturePathFromUpgrades(selected_upgrade)
	if not selected_upgrade or selected_upgrade == '' or selected_upgrade == '0' then return '$invis' end
	local tokens = extractUpgradeTokens(selected_upgrade)
	for _, token in ipairs(tokens) do
		local lower = token:lower()
		if lower:find('^cs%.') or lower:find('^chatsymbol_') then
			local clean = token:gsub('^[Cc][Ss]%.', ''):gsub('^[Cc]hat[Ss]ymbol_', '')
			if clean == '' or clean == '0' or clean:lower() == 'none' or clean == '$invis' then
				return '$invis'
			end
			if HeroDefinitionsCache and HeroDefinitionsCache.GetChatSymbol then
				local symbol = HeroDefinitionsCache.GetChatSymbol(clean) or HeroDefinitionsCache.GetChatSymbol(token)
				if symbol and symbol ~= '' and symbol ~= '$invis' then
					return symbol
				end
			end
		end
	end
	if originalGetChatSymbolTexturePathFromUpgrades then
		local ok, res = pcall(originalGetChatSymbolTexturePathFromUpgrades, selected_upgrade)
		if ok and res and res ~= '' and res ~= '$invis' then
			return res
		end
	end
	return '$invis'
end

HeroDefinitionsCache._initialized = true
_G.HeroDefinitionsCache = HeroDefinitionsCache
");

        return sb.ToString();
    }

    public static async Task GenerateLuaCacheAsync(EntityCacheContainer container, string outputFilePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        string lua = GenerateLuaCache(container);

        string? dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputFilePath, lua, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static string ExtractDirFromPath(string iconPath, string heroName)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return heroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

        string[] parts = iconPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("heroes", StringComparison.OrdinalIgnoreCase)) return parts[1];

        return heroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static string Escape(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n");
    }
}
