// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * NEXUS Interactive 2D Minimap Canvas Visualizer Engine
 * Recreates the authentic Heroes of Newerth v4.10.1 spectator minimap experience.
 * Supports both Live Stream (SignalR) and 2D Match Replay Playback (Timeline Scrubbing).
 */

const ExactPlayerColors = [
    '#0042FF', // Slot 1: Blue (Legion)
    '#1CE6B9', // Slot 2: Teal (Legion)
    '#9000C0', // Slot 3: Purple (Legion)
    '#FFFC01', // Slot 4: Yellow (Legion)
    '#FE8A0E', // Slot 5: Orange (Legion)
    '#E55BB0', // Slot 6: Pink (Hellbourne)
    '#959697', // Slot 7: Grey (Hellbourne)
    '#7EBFF1', // Slot 8: Light Blue (Hellbourne)
    '#106246', // Slot 9: Dark Green (Hellbourne)
    '#8B4513'  // Slot 10: Brown (Hellbourne)
];

const CaldavarStructures = [
    // Legion Structures (Team 1)
    {id: 'legion_t1_top', name: 'Legion T1 Top', type: 'Tower', team: 1, x: 0.123, y: 0.424},
    {id: 'legion_t2_top', name: 'Legion T2 Top', type: 'Tower', team: 1, x: 0.127, y: 0.588},
    {id: 'legion_t3_top', name: 'Legion T3 Top', type: 'Tower', team: 1, x: 0.109, y: 0.719},
    {id: 'legion_mrax_top', name: 'Legion Melee Rax Top', type: 'MRax', team: 1, x: 0.094, y: 0.742},
    {id: 'legion_rrax_top', name: 'Legion Range Rax Top', type: 'RRax', team: 1, x: 0.125, y: 0.742},

    {id: 'legion_t1_mid', name: 'Legion T1 Mid', type: 'Tower', team: 1, x: 0.408, y: 0.611},
    {id: 'legion_t2_mid', name: 'Legion T2 Mid', type: 'Tower', team: 1, x: 0.293, y: 0.695},
    {id: 'legion_t3_mid', name: 'Legion T3 Mid', type: 'Tower', team: 1, x: 0.232, y: 0.785},
    {id: 'legion_mrax_mid', name: 'Legion Melee Rax Mid', type: 'MRax', team: 1, x: 0.207, y: 0.787},
    {id: 'legion_rrax_mid', name: 'Legion Range Rax Mid', type: 'RRax', team: 1, x: 0.231, y: 0.812},

    {id: 'legion_t1_bot', name: 'Legion T1 Bot', type: 'Tower', team: 1, x: 0.799, y: 0.916},
    {id: 'legion_t2_bot', name: 'Legion T2 Bot', type: 'Tower', team: 1, x: 0.479, y: 0.910},
    {id: 'legion_t3_bot', name: 'Legion T3 Bot', type: 'Tower', team: 1, x: 0.309, y: 0.922},
    {id: 'legion_mrax_bot', name: 'Legion Melee Rax Bot', type: 'MRax', team: 1, x: 0.289, y: 0.904},
    {id: 'legion_rrax_bot', name: 'Legion Range Rax Bot', type: 'RRax', team: 1, x: 0.289, y: 0.939},

    {id: 'legion_t4_top', name: 'Legion T4 Throne Top', type: 'Tower', team: 1, x: 0.182, y: 0.877},
    {id: 'legion_t4_bot', name: 'Legion T4 Throne Bot', type: 'Tower', team: 1, x: 0.158, y: 0.853},
    {id: 'legion_base', name: 'World Tree Base', type: 'Base', team: 1, x: 0.135, y: 0.865},

    // Hellbourne Structures (Team 2)
    {id: 'hellbourne_t1_top', name: 'Hellbourne T1 Top', type: 'Tower', team: 2, x: 0.193, y: 0.139},
    {id: 'hellbourne_t2_top', name: 'Hellbourne T2 Top', type: 'Tower', team: 2, x: 0.506, y: 0.142},
    {id: 'hellbourne_t3_top', name: 'Hellbourne T3 Top', type: 'Tower', team: 2, x: 0.672, y: 0.156},
    {id: 'hellbourne_mrax_top', name: 'Hellbourne Melee Rax Top', type: 'MRax', team: 2, x: 0.695, y: 0.174},
    {id: 'hellbourne_rrax_top', name: 'Hellbourne Range Rax Top', type: 'RRax', team: 2, x: 0.699, y: 0.139},

    {id: 'hellbourne_t1_mid', name: 'Hellbourne T1 Mid', type: 'Tower', team: 2, x: 0.545, y: 0.482},
    {id: 'hellbourne_t2_mid', name: 'Hellbourne T2 Mid', type: 'Tower', team: 2, x: 0.656, y: 0.394},
    {id: 'hellbourne_t3_mid', name: 'Hellbourne T3 Mid', type: 'Tower', team: 2, x: 0.734, y: 0.297},
    {id: 'hellbourne_mrax_mid', name: 'Hellbourne Melee Rax Mid', type: 'MRax', team: 2, x: 0.766, y: 0.289},
    {id: 'hellbourne_rrax_mid', name: 'Hellbourne Range Rax Mid', type: 'RRax', team: 2, x: 0.742, y: 0.266},

    {id: 'hellbourne_t1_bot', name: 'Hellbourne T1 Bot', type: 'Tower', team: 2, x: 0.869, y: 0.631},
    {id: 'hellbourne_t2_bot', name: 'Hellbourne T2 Bot', type: 'Tower', team: 2, x: 0.881, y: 0.473},
    {id: 'hellbourne_t3_bot', name: 'Hellbourne T3 Bot', type: 'Tower', team: 2, x: 0.875, y: 0.348},
    {id: 'hellbourne_mrax_bot', name: 'Hellbourne Melee Rax Bot', type: 'MRax', team: 2, x: 0.891, y: 0.326},
    {id: 'hellbourne_rrax_bot', name: 'Hellbourne Range Rax Bot', type: 'RRax', team: 2, x: 0.859, y: 0.320},

    {id: 'hellbourne_t4_top', name: 'Hellbourne T4 Throne Top', type: 'Tower', team: 2, x: 0.805, y: 0.244},
    {id: 'hellbourne_t4_bot', name: 'Hellbourne T4 Throne Bot', type: 'Tower', team: 2, x: 0.783, y: 0.219},
    {id: 'hellbourne_base', name: 'Sacrificial Shrine Base', type: 'Base', team: 2, x: 0.865, y: 0.135}
];

const MidWarsStructures = [
    // Legion Structures (Team 1)
    {id: 'legion_base', name: 'Legion Tree of Life Base', type: 'Base', team: 1, x: 0.215, y: 0.700},
    {id: 'legion_t2_mid', name: 'Legion T2 Gate Tower', type: 'Tower', team: 1, x: 0.255, y: 0.655},
    {id: 'legion_t1_mid', name: 'Legion T1 Mid Tower (River)', type: 'Tower', team: 1, x: 0.385, y: 0.505},

    // Hellbourne Structures (Team 2)
    {id: 'hellbourne_t1_mid', name: 'Hellbourne T1 Mid Tower (River)', type: 'Tower', team: 2, x: 0.495, y: 0.415},
    {id: 'hellbourne_t2_mid', name: 'Hellbourne T2 Gate Tower', type: 'Tower', team: 2, x: 0.635, y: 0.285},
    {id: 'hellbourne_base', name: 'Hellbourne Sacrificial Shrine Base', type: 'Base', team: 2, x: 0.670, y: 0.260}
];

class MinimapVisualizer {
    constructor(canvasId, goldChartId, initialMatchData, options = {}) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) return;
        this.ctx = this.canvas.getContext('2d');
        this.goldChartId = goldChartId;
        this.options = options || {};
        this.isReplayMode = Boolean(options.isReplayMode);

        this.match = initialMatchData || {};
        this.lastTickId = initialMatchData.tickId || 0;
        this.wards = initialMatchData.activeWards || [];
        this.goldTimeline = initialMatchData.goldTimeline || [];
        this.players = initialMatchData.players || [];
        this.structures = initialMatchData.structures || {};
        this.recentEvents = initialMatchData.recentEvents || [];

        this.interpolatedPlayers = new Map();
        this.mapImage = new Image();
        this.heroImages = new Map();
        this.selectedPlayer = null;
        this.hoveredPlayer = null;
        this.lastFrameTime = performance.now();
        this.animFrameId = null;

        window.ActiveMinimapVisualizer = this;

        // Replay Playback State
        this.replayKeyframes = [];
        this.replayDuration = initialMatchData.elapsedSeconds || 0;
        this.playbackTime = 0;
        this.playbackSpeed = 1.0;
        this.isPlaying = false;
        this.playbackInterval = null;

        // Offscreen Canvas for static map caching
        this.offscreenCanvas = document.createElement('canvas');
        this.offscreenCtx = this.offscreenCanvas.getContext('2d');
        this.isOffscreenDirty = true;

        this.initHiDpiCanvas();
        this.initMapTexture();
        this.initPlayers();
        this.updateRosterUI();
        this.updateHeaderStats();
        this.updateCombatFeedUI();
        this.initGoldChart();
        this.initEvents();

        if (this.isReplayMode) {
            this.initReplayControls();
            if (options.replayEndpoint) {
                this.loadReplayTelemetry(options.replayEndpoint);
            }
        } else {
            this.initSignalR();
        }

        this.startRenderLoop();
    }

    escapeHtml(val) {
        return String(val ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    initHiDpiCanvas() {
        const dpr = window.devicePixelRatio || 1;
        const rect = this.canvas.getBoundingClientRect();
        this.cachedCanvasRect = rect;
        this.logicalWidth = rect.width > 0 ? rect.width : 620;
        this.logicalHeight = rect.height > 0 ? rect.height : 620;

        this.canvas.width = this.logicalWidth * dpr;
        this.canvas.height = this.logicalHeight * dpr;
        this.ctx.scale(dpr, dpr);

        this.offscreenCanvas.width = this.logicalWidth;
        this.offscreenCanvas.height = this.logicalHeight;
    }

    initMapTexture() {
        this.mapType = (this.match.map || 'caldavar').toLowerCase();
        this.mapImage.src = this.getMinimapPath(this.mapType);
        this.mapImage.onload = () => {
            this.isOffscreenDirty = true;
        };
        this.mapImage.onerror = () => {
            this.mapImage.src = '/images/maps/minimap_caldavar.png';
            this.isOffscreenDirty = true;
        };
    }

    getMinimapPath(rawMapName) {
        if (!rawMapName) return '/images/maps/minimap_caldavar.png';
        const normalizedSlug = String(rawMapName).toLowerCase().replace(/^\/?images\/maps\//, '').replace(/^maps\//, '').replace(/\.(s2z|png|tga|dds|webp|svg)$/i, '').replace(/^minimap[\s_-]+/, '').replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
        const exactCustomSlugs = new Set(['archers','arena','caldavar','caldavar_halloween','caldavar_old','caldavar_reborn','caldavar_wtf','capturetheflag','colosseum_duel','darkwoodvale','devowars','devowars_10v10','empath_maze','enfos','grimmscrossing','mids_omg','midwars','midwars_10v10','midwars_halloween','midwars_reborn','nightmarish_coop','pebbles_duel','persian_pass','prophets','riftwars','soccer','spotlight','strikers','team_deathmatch','thegrimmhunt','three_kingdoms','truedevo','truedevo_10v10','tutorial','valkwars','watchtower']);
        const mapSlugAliases = { 'forests_of_caldavar': 'caldavar', 'forestsofcaldavar': 'caldavar', 'caldavar_1v1': 'caldavar', 'caldavar_ranked': 'caldavar', 'caldavar_public': 'caldavar', 'caldavar_casual': 'caldavar', 'quickmatch': 'caldavar', 'forests_of_caldavar_old': 'caldavar_old', 'forests_of_caldavar_reborn': 'caldavar_reborn', 'mid_wars': 'midwars', 'mid_wars_old': 'midwars', 'midwars_ranked': 'midwars', 'midwars_public': 'midwars', 'mid_wars_reborn': 'midwars_reborn', 'mid_wars_10v10': 'midwars_10v10', 'mid_wars_halloween': 'midwars_halloween', 'devo_wars': 'devowars', 'devo_wars_10v10': 'devowars_10v10', 'true_devo': 'truedevo', 'true_devo_10v10': 'truedevo_10v10', 'rift_wars': 'riftwars', 'riftwars_ranked': 'riftwars', 'grimms_crossing': 'grimmscrossing', 'grimmscross': 'grimmscrossing', 'darkwood_vale': 'darkwoodvale', 'darkwood': 'darkwoodvale', 'capture_the_flag': 'capturetheflag', 'ctf': 'capturetheflag', 'team_death_match': 'team_deathmatch', 'teamdeathmatch': 'team_deathmatch', 'tdm': 'team_deathmatch', 'the_grimm_hunt': 'thegrimmhunt', 'grimmhunt': 'thegrimmhunt', 'hon_strikers': 'strikers', 'striker': 'strikers', 'prophet': 'prophets', 'archer_wars': 'archers', 'archerwars': 'archers', 'valk_wars': 'valkwars', 'threekingdoms': 'three_kingdoms', 'persianpass': 'persian_pass', 'pebblesduel': 'pebbles_duel', 'colosseumduel': 'colosseum_duel', 'empathmaze': 'empath_maze', 'nightmarishcoop': 'nightmarish_coop' };
        const rev = '?v=k2crop2';
        const mappedSlug = mapSlugAliases[normalizedSlug] || normalizedSlug;
        if (exactCustomSlugs.has(mappedSlug)) return `/images/maps/minimap_${mappedSlug}.png${rev}`;
        const clean = mappedSlug;

        if (clean.includes('midwars_reborn') || clean.includes('midwarsreborn')) return `/images/maps/minimap_midwars_reborn.png${rev}`;
        if (clean.includes('midwars')) return `/images/maps/minimap_midwars.png${rev}`;
        if (clean.includes('caldavar_old')) return `/images/maps/minimap_caldavar_old.png${rev}`;
        if (clean.includes('caldavar_reborn')) return `/images/maps/minimap_caldavar_reborn.png${rev}`;
        if (clean.includes('caldavar')) return `/images/maps/minimap_caldavar.png${rev}`;
        if (clean.includes('grimmscross') || clean.includes('grimm')) return `/images/maps/minimap_grimmscrossing.png${rev}`;
        if (clean.includes('darkwood')) return `/images/maps/minimap_darkwoodvale.png${rev}`;
        if (clean.includes('devo')) return `/images/maps/minimap_devowars.png${rev}`;
        if (clean.includes('rift')) return `/images/maps/minimap_riftwars.png${rev}`;
        if (clean.includes('watchtower')) return `/images/maps/minimap_watchtower.png${rev}`;
        if (clean.includes('team_deathmatch') || clean.includes('teamdeathmatch') || clean.includes('tdm')) return `/images/maps/minimap_team_deathmatch.png${rev}`;
        if (clean.includes('capturetheflag') || clean.includes('ctf')) return `/images/maps/minimap_capturetheflag.png${rev}`;
        if (clean.includes('thegrimmhunt') || clean.includes('grimmhunt')) return `/images/maps/minimap_thegrimmhunt.png${rev}`;
        if (clean.includes('prophet')) return `/images/maps/minimap_prophets.png${rev}`;
        if (clean.includes('soccer') || clean.includes('striker')) return `/images/maps/minimap_strikers.png${rev}`;
        if (clean.includes('tutorial')) return `/images/maps/minimap_tutorial.png${rev}`;

        return `/images/maps/minimap_${clean}.png${rev}`;
    }

    getHeroIconPath(heroId) {
        if (!heroId) return '/images/heroes/default.png';
        let clean = String(heroId).toLowerCase().replace(/^\/?images\/heroes\//, '').replace(/\.(png|tga|dds|webp|svg)$/i, '').replace(/^icon_/, '').replace(/^hero_/, '').replace(/[^a-z0-9]/g, '');

        const aliasMap = {
            'flameguard': 'pyromancer',
            'hiro': 'swiftblade',
            'frosty': 'glacius',
            'hunter': 'bloodhunter',
            'krixi': 'valkyrie',
            'shaman': 'dementedshaman',
            'witchslayer': 'witchslayer',
            'flint': 'flintbeastwood',
            'pebbles': 'pebbles',
            'rocky': 'pebbles',
            'wildsoul': 'wildsoul',
            'keeper': 'keeperoftheforest',
            'deadwood': 'deadwood',
            'voodoo': 'voodoojester',
            'warbeast': 'warbeast',
            'succubus': 'succubus',
            'valkyrie': 'valkyrie',
            'glacius': 'glacius',
            'bloodhunter': 'bloodhunter',
            'swiftblade': 'swiftblade',
            'pyromancer': 'pyromancer',
            'devourer': 'devourer',
            'armadon': 'armadon',
            'hammerstorm': 'hammerstorm',
            'bubbles': 'bubbles',
            'myrmidon': 'myrmidon'
        };

        if (aliasMap[clean]) clean = aliasMap[clean];
        return `/images/heroes/${clean}.png`;
    }

    getHeroDisplayName(heroId) {
        if (!heroId) return 'Picking Hero...';
        const clean = heroId.toLowerCase().replace('hero_', '').replace(/_/g, '').trim();
        const displayMap = {
            'bubbles': 'Bubbles',
            'corrupteddisciple': 'Corrupted Disciple',
            'riftmage': 'Riftwalker',
            'riftwalker': 'Riftwalker',
            'glacius': 'Glacius',
            'pyromancer': 'Pyromancer',
            'swiftblade': 'Swiftblade',
            'hiro': 'Swiftblade',
            'devourer': 'Devourer',
            'flintbeastwood': 'Flint Beastwood',
            'flint': 'Flint Beastwood',
            'pebbles': 'Pebbles',
            'rocky': 'Pebbles',
            'witchslayer': 'Witch Slayer',
            'valkyrie': 'Valkyrie',
            'krixi': 'Valkyrie',
            'bloodhunter': 'Blood Hunter',
            'hunter': 'Blood Hunter',
            'dementedshaman': 'Demented Shaman',
            'shaman': 'Demented Shaman',
            'deadwood': 'Deadwood',
            'voodoojester': 'Voodoo Jester',
            'warbeast': 'War Beast',
            'succubus': 'Succubus',
            'babayaga': 'Wretched Hag',
            'wretchedhag': 'Wretched Hag',
            'scout': 'Scout',
            'magebane': 'Magebane',
            'nighthound': 'Night Hound',
            'chronos': 'Chronos',
            'madman': 'The Madman',
            'darklady': 'The Dark Lady',
            'moonqueen': 'Moon Queen',
            'sandwraith': 'Sand Wraith',
            'shadowblade': 'Shadowblade',
            'solstice': 'Solstice',
            'amunra': 'Amun-Ra',
            'ra': 'Amun-Ra',
            'zephyr': 'Zephyr',
            'electrician': 'Electrician',
            'rampage': 'Rampage',
            'pandamonium': 'Pandamonium',
            'moraxus': 'Moraxus',
            'kraken': 'Kraken',
            'gauntlet': 'Gauntlet',
            'behemoth': 'Behemoth',
            'armadon': 'Armadon',
            'accused': 'Accursed',
            'accursed': 'Accursed',
            'maliken': 'Maliken',
            'draconis': 'Draconis',
            'artillery': 'Artillery',
            'gunblade': 'Gunblade',
            'blitz': 'Blitz',
            'berzerker': 'Berzerker',
            'ellonia': 'Ellonia',
            'gravekeeper': 'Gravekeeper',
            'bushwack': 'Bushwack',
            'grinex': 'Grinex',
            'salforis': 'Salforis',
            'dr_repulsor': 'Doctor Repulsor',
            'repulsor': 'Doctor Repulsor',
            'chipper': 'The Chipper',
            'bombardier': 'Bombardier',
            'artesia': 'Artesia',
            'aluna': 'Aluna',
            'nomad': 'Nomad',
            'monkeyking': 'Monkey King',
            'emeraldwarden': 'Emerald Warden',
            'silhouette': 'Silhouette',
            'parasite': 'Parasite',
            'revenant': 'Revenant',
            'midas': 'Midas',
            'geopancer': 'Geomancer',
            'geomancer': 'Geomancer',
            'cthulhuphant': 'Cthulhuphant',
            'masterofarms': 'Master of Arms',
            'moa': 'Master of Arms',
            'rhapsody': 'Rhapsody',
            'sir_benzington': 'Sir Benzington',
            'benzington': 'Sir Benzington',
            'paragon': 'Paragon',
            'tarot': 'Tarot',
            'solomon': 'Solomon',
            'skrap': 'Skrap',
            'oogie': 'Oogie',
            'nitro': 'Nitro',
            'apex': 'Apex',
            'adrenaline': 'Adrenaline',
            'goldenveil': 'Goldenveil',
            'calamity': 'Calamity',
            'klint': 'Klint'
        };
        if (displayMap[clean]) return displayMap[clean];
        const raw = heroId.replace(/hero_/i, '').replace(/_/g, ' ').trim();
        return raw.charAt(0).toUpperCase() + raw.slice(1);
    }

    getItemIconPath(rawItemName) {
        if (!rawItemName) return '/images/items/default.png';
        let clean = rawItemName.toLowerCase().replace('item_', '').replace(/_/g, '').trim();

        const aliasMap = {
            'shrunken': 'shrunkenhead',
            'shrunkenhead': 'shrunkenhead',
            'pk': 'portalkey',
            'portalkey': 'portalkey',
            'boots': 'marchers',
            'ghost': 'ghostmarchers',
            'ghostmarchers': 'ghostmarchers',
            'steam': 'steamboots',
            'steamboots': 'steamboots',
            'striders': 'striders',
            'nullstone': 'nullstone',
            'firebrand': 'firebrand',
            'frostbrand': 'frostbrand',
            'frostburn': 'frostburn',
            'dawnbringer': 'dawnbringer',
            'runes': 'runesoftheblight',
            'runesoftheblight': 'runesoftheblight',
            'potion': 'healthpotion',
            'healthpotion': 'healthpotion',
            'manapotion': 'manapotion',
            'powersupply': 'powersupply',
            'manabattery': 'manabattery',
            'bottle': 'bottle',
            'bloodchalice': 'bloodchalice',
            'scarab': 'scarab',
            'crushingclaws': 'crushingclaws',
            'markofthenovice': 'markofthenovice',
            'flamingeye': 'flamingeye',
            'manaeye': 'manaeye',
            'wardofsight': 'wardofsight',
            'wardofrevelation': 'wardofrevelation',
            'homecomingstone': 'homecomingstone'
        };

        if (aliasMap[clean]) clean = aliasMap[clean];
        return `/images/items/${clean}.png`;
    }

    getItemDisplayName(rawItemName) {
        if (!rawItemName) return 'Item';
        const clean = rawItemName.toLowerCase().replace('item_', '').replace(/_/g, '').trim();
        const displayMap = {
            'bloodchalice': 'Blood Chalice',
            'scarab': 'Scarab',
            'crushingclaws': 'Crushing Claws',
            'markofthenovice': 'Mark of the Novice',
            'flamingeye': 'Ward of Sight',
            'manaeye': 'Ward of Revelation',
            'wardofsight': 'Ward of Sight',
            'wardofrevelation': 'Ward of Revelation',
            'shrunkenhead': 'Shrunken Head',
            'portalkey': 'Portal Key',
            'marchers': 'Marchers',
            'steamboots': 'Steamboots',
            'ghostmarchers': 'Ghost Marchers',
            'powersupply': 'Power Supply',
            'manabattery': 'Mana Battery',
            'nullstone': 'Nullstone',
            'frostburn': 'Frostburn',
            'dawnbringer': 'Dawnbringer',
            'firebrand': 'Firebrand',
            'frostbrand': 'Frostbrand',
            'grimoireofpower': 'Grimoire of Power',
            'homecomingstone': 'Homecoming Stone',
            'healthpotion': 'Health Potion',
            'manapotion': 'Mana Potion',
            'runesoftheblight': 'Runes of the Blight'
        };
        if (displayMap[clean]) return displayMap[clean];
        const raw = rawItemName.replace(/item_/i, '').replace(/_/g, ' ').trim();
        return raw.charAt(0).toUpperCase() + raw.slice(1);
    }

    initPlayers() {
        const rawPlayers = this.match.players || this.match.Players || this.players || [];
        this.players = rawPlayers.map(p => {
            const hId = p.heroIdentifier || p.HeroIdentifier || '';
            const hDisplay = p.heroDisplayName || p.HeroDisplayName || this.getHeroDisplayName(hId);
            return {
                accountID: p.accountID || p.accountId || p.AccountID || p.AccountId || (p.slot !== undefined ? p.slot + 1 : 1),
                accountName: p.accountName || p.AccountName || 'guccidonut',
                team: p.team || p.Team || 1,
                heroIdentifier: hId,
                heroDisplayName: hDisplay,
                positionX: p.positionX !== undefined ? p.positionX : (p.PositionX !== undefined ? p.PositionX : 0.5),
                positionY: p.positionY !== undefined ? p.positionY : (p.PositionY !== undefined ? p.PositionY : 0.5),
                headingAngle: p.headingAngle !== undefined ? p.headingAngle : (p.HeadingAngle !== undefined ? p.HeadingAngle : 0),
                healthPercent: p.healthPercent !== undefined ? p.healthPercent : (p.HealthPercent !== undefined ? p.HealthPercent : 100),
                manaPercent: p.manaPercent !== undefined ? p.manaPercent : (p.ManaPercent !== undefined ? p.ManaPercent : 100),
                level: p.level || p.Level || 1,
                isAlive: p.isAlive !== false && p.IsAlive !== false,
                respawnTimer: p.respawnTimer || p.RespawnTimer || 0,
                currentGold: p.currentGold !== undefined ? p.currentGold : (p.CurrentGold !== undefined ? p.CurrentGold : 0),
                kills: p.kills || p.Kills || 0,
                deaths: p.deaths || p.Deaths || 0,
                assists: p.assists || p.Assists || 0,
                gpm: p.gpm || p.GPM || 0,
                xpm: p.xpm || p.XPM || 0,
                inventory: p.inventory || p.Inventory || [],
                abilities: p.abilities || p.Abilities || []
            };
        });

        for (let i = 0; i < this.players.length; i++) {
            const p = this.players[i];
            const accId = p.accountID || p.accountId || p.AccountID;
            const slotIndex = p.team === 1 ? i : (i + 5);
            const playerSlotColor = ExactPlayerColors[slotIndex % ExactPlayerColors.length];

            let existingState = this.interpolatedPlayers.get(accId);
            if (!existingState) {
                this.interpolatedPlayers.set(accId, {
                    currentX: p.positionX,
                    currentY: p.positionY,
                    targetX: p.positionX,
                    targetY: p.positionY,
                    headingAngle: p.headingAngle !== undefined ? p.headingAngle : (p.team === 1 ? 0.785 : -2.356),
                    health: p.healthPercent,
                    mana: p.manaPercent,
                    level: p.level,
                    isAlive: p.isAlive,
                    respawnTimer: p.respawnTimer,
                    heroId: p.heroIdentifier,
                    name: p.accountName,
                    team: p.team,
                    slotColor: playerSlotColor,
                    items: p.inventory || [],
                    abilities: p.abilities || [],
                    lastCastAbility: p.lastCastAbility
                });
            } else {
                existingState.targetX = p.positionX;
                existingState.targetY = p.positionY;
                existingState.health = p.healthPercent;
                existingState.mana = p.manaPercent;
                existingState.level = p.level;
                existingState.isAlive = p.isAlive;
                existingState.respawnTimer = p.respawnTimer;
                if (p.heroIdentifier) existingState.heroId = p.heroIdentifier;
                if (p.accountName) existingState.name = p.accountName;
                if (p.team) existingState.team = p.team;
                if (p.inventory) existingState.items = p.inventory;
                if (p.abilities && p.abilities.length > 0) existingState.abilities = p.abilities;
                if (p.lastCastAbility) existingState.lastCastAbility = p.lastCastAbility;
            }

            if (p.heroIdentifier && !this.heroImages.has(p.heroIdentifier)) {
                const img = new Image();
                img.src = this.getHeroIconPath(p.heroIdentifier);
                img.onerror = () => {
                    img.src = '/images/heroes/default.png';
                };
                this.heroImages.set(p.heroIdentifier, img);
            }
        }

        if (this.players.length > 0) {
            if (!this.selectedPlayer) {
                this.selectedPlayer = this.players[0];
            } else {
                const matched = this.players.find(p => p.accountID === this.selectedPlayer.accountID);
                if (matched) this.selectedPlayer = matched;
            }
            this.updateInspector(this.selectedPlayer);
        }
    }

    initEvents() {
        const refreshCanvasRect = () => { this.cachedCanvasRect = this.canvas.getBoundingClientRect(); };
        this.canvas.addEventListener('mouseenter', refreshCanvasRect);
        window.addEventListener('scroll', refreshCanvasRect, { passive: true });
        this.canvas.addEventListener('mousemove', (e) => {
            const rect = this.cachedCanvasRect || (this.cachedCanvasRect = this.canvas.getBoundingClientRect());
            const mouseX = (e.clientX - rect.left) / rect.width;
            const mouseY = (e.clientY - rect.top) / rect.height;

            let foundPlayer = null;
            for (const [accId, p] of this.interpolatedPlayers) {
                if (!p.isAlive) continue;
                const dx = mouseX - p.currentX;
                const dy = mouseY - p.currentY;
                const dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < 0.045) {
                    foundPlayer = this.players.find(pl => pl.accountID === accId);
                    break;
                }
            }

            let foundWard = null;
            if (!foundPlayer && this.wards) {
                for (const w of this.wards) {
                    const dx = mouseX - w.positionX;
                    const dy = mouseY - w.positionY;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist < 0.035) {
                        foundWard = w;
                        break;
                    }
                }
            }

            this.hoveredPlayer = foundPlayer;
            this.hoveredWard = foundWard;
            this.canvas.style.cursor = (foundPlayer || foundWard) ? 'pointer' : 'default';
            this.updateTooltip(e, foundPlayer, foundWard);
        });

        this.canvas.addEventListener('mouseleave', () => {
            this.hoveredPlayer = null;
            this.hoveredWard = null;
            const tooltip = document.getElementById('minimap-tooltip');
            if (tooltip) tooltip.classList.add('hidden');
        });

        this.canvas.addEventListener('click', () => {
            if (this.hoveredPlayer) {
                this.selectPlayer(this.hoveredPlayer);
            }
        });

        window.addEventListener('resize', () => {
            this.initHiDpiCanvas();
            this.isOffscreenDirty = true;
        });
    }

    selectPlayer(player) {
        if (!player) return;
        this.selectedPlayer = player;
        this.updateInspector(player);
        this.updateRosterUI();
    }

    selectPlayerByAccountId(accId) {
        const p = this.players.find(pl => pl.accountID === accId || pl.AccountID === accId);
        if (p) {
            this.selectPlayer(p);
        }
    }

    // --- REPLAY PLAYBACK CONTROLLER ---
    async loadReplayTelemetry(endpoint) {
        try {
            const res = await fetch(endpoint);
            if (res.ok) {
                const replayDto = await res.json();
                this.replayDuration = replayDto.durationSeconds || this.replayDuration;
                this.replayKeyframes = replayDto.keyframes || [];
                if (replayDto.goldTimeline) {
                    this.goldTimeline = replayDto.goldTimeline;
                    this.rebuildGoldChart();
                }
                if (replayDto.players && replayDto.players.length > 0) {
                    this.players = replayDto.players;
                    this.initPlayers();
                }
                this.updateReplayTimelineUI();
            }
        } catch (err) {
            console.error('[Minimap] Error loading replay telemetry', err);
        }
    }

    initReplayControls() {
        const playBtn = document.getElementById('replay-btn-play');
        const scrubSlider = document.getElementById('replay-scrubber');
        const speedSelect = document.getElementById('replay-speed-selector');
        const jumpBackBtn = document.getElementById('replay-btn-back');
        const jumpFwdBtn = document.getElementById('replay-btn-fwd');

        if (playBtn) {
            playBtn.addEventListener('click', () => {
                this.togglePlayback();
            });
        }

        if (scrubSlider) {
            scrubSlider.max = this.replayDuration;
            scrubSlider.value = 0;
            scrubSlider.addEventListener('input', (e) => {
                this.seek(parseFloat(e.target.value));
            });
        }

        if (speedSelect) {
            speedSelect.addEventListener('change', (e) => {
                this.playbackSpeed = parseFloat(e.target.value) || 1.0;
            });
        }

        if (jumpBackBtn) {
            jumpBackBtn.addEventListener('click', () => {
                this.seek(Math.max(0, this.playbackTime - 10));
            });
        }

        if (jumpFwdBtn) {
            jumpFwdBtn.addEventListener('click', () => {
                this.seek(Math.min(this.replayDuration, this.playbackTime + 10));
            });
        }
    }

    togglePlayback() {
        this.isPlaying = !this.isPlaying;
        const playIcon = document.getElementById('replay-play-icon');
        const playText = document.getElementById('replay-play-text');

        if (this.isPlaying) {
            if (playIcon) playIcon.textContent = '⏸';
            if (playText) playText.textContent = 'Pause';
            this.lastPlaybackTick = performance.now();
        } else {
            if (playIcon) playIcon.textContent = '▶';
            if (playText) playText.textContent = 'Play';
        }
    }

    seek(seconds) {
        this.playbackTime = Math.max(0, Math.min(this.replayDuration, seconds));
        this.applyReplayTime(this.playbackTime);
        this.updateReplayTimelineUI();
    }

    applyReplayTime(time) {
        if (this.replayKeyframes.length === 0) return;

        // Find surrounding keyframes
        let prevKf = this.replayKeyframes[0];
        let nextKf = this.replayKeyframes[this.replayKeyframes.length - 1];

        for (let i = 0; i < this.replayKeyframes.length; i++) {
            if (this.replayKeyframes[i].second <= time) {
                prevKf = this.replayKeyframes[i];
            }
            if (this.replayKeyframes[i].second >= time) {
                nextKf = this.replayKeyframes[i];
                break;
            }
        }

        const span = nextKf.second - prevKf.second;
        const alpha = span > 0 ? (time - prevKf.second) / span : 0;

        if (prevKf.players) {
            for (const pState of prevKf.players) {
                const targetState = nextKf.players?.find(p => p.accountID === pState.accountID) || pState;
                const interp = this.interpolatedPlayers.get(pState.accountID);
                if (interp) {
                    interp.currentX = pState.positionX + (targetState.positionX - pState.positionX) * alpha;
                    interp.currentY = pState.positionY + (targetState.positionY - pState.positionY) * alpha;
                    interp.targetX = interp.currentX;
                    interp.targetY = interp.currentY;
                    interp.health = pState.healthPercent;
                    interp.level = pState.level;
                    interp.isAlive = pState.isAlive;
                }
            }
        }

        this.match.elapsedSeconds = Math.floor(time);
        this.updateHeaderStats();
    }

    updateReplayTimelineUI() {
        const scrubber = document.getElementById('replay-scrubber');
        if (scrubber) scrubber.value = this.playbackTime;

        const timeLabel = document.getElementById('replay-time-display');
        if (timeLabel) {
            const curM = Math.floor(this.playbackTime / 60);
            const curS = Math.floor(this.playbackTime % 60);
            const totM = Math.floor(this.replayDuration / 60);
            const totS = Math.floor(this.replayDuration % 60);
            timeLabel.textContent = `${curM.toString().padStart(2, '0')}:${curS.toString().padStart(2, '0')} / ${totM.toString().padStart(2, '0')}:${totS.toString().padStart(2, '0')}`;
        }
    }

    // --- LIVE SIGNALR SUBSCRIPTION ---
    initSignalR() {
        if (typeof signalR === 'undefined') return;

        const currentMatchId = this.match.matchID || this.match.matchId || 0;
        this.currentMatchId = currentMatchId;

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/live-matches')
            .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        this.connection.on('OnMatchSnapshot', (snapshot) => {
            const snapId = snapshot.matchID || snapshot.matchId;
            if (snapId === this.currentMatchId) {
                this.match = snapshot;
                this.lastTickId = snapshot.tickId || 0;
                this.wards = snapshot.activeWards || [];
                this.goldTimeline = snapshot.goldTimeline || [];
                this.players = snapshot.players || [];
                this.recentEvents = snapshot.recentEvents || [];
                this.initPlayers();
                this.rebuildGoldChart();
                this.updateHeaderStats();
                this.updateRosterUI();
                this.updateCombatFeedUI();
                this.isOffscreenDirty = true;
            }
        });

        this.connection.on('OnMatchTick', (delta) => {
            const deltaId = delta.matchID || delta.matchId;
            if (deltaId === this.currentMatchId) {
                this.onDeltaTick(delta);
            }
        });

        this.connection.on('OnCombatEvent', (combatEvent) => {
            this.appendCombatEvent(combatEvent);
        });

        this.connection.on('OnGoldAdvantageUpdated', (goldPoint) => {
            this.pushGoldDataPoint(goldPoint);
        });

        this.connection.start().then(() => {
            if (this.currentMatchId > 0) {
                this.connection.invoke('JoinMatch', this.currentMatchId);
            }
        }).catch(err => {
            console.error('[Minimap] SignalR connection failed', err);
        });
    }

    updateRosterUI() {
        const legionContainer = document.getElementById('roster-legion-container');
        const hellbourneContainer = document.getElementById('roster-hellbourne-container');
        if (!legionContainer || !hellbourneContainer) return;

        const legionPlayers = this.players.filter(p => (p.team || p.Team) === 1);
        const hellbournePlayers = this.players.filter(p => (p.team || p.Team) === 2);

        legionContainer.innerHTML = legionPlayers.map(p => {
            const heroId = p.heroIdentifier || p.HeroIdentifier || '';
            const name = this.escapeHtml(p.accountName || p.AccountName || 'Player');
            const kills = p.kills !== undefined ? p.kills : (p.Kills || 0);
            const deaths = p.deaths !== undefined ? p.deaths : (p.Deaths || 0);
            const assists = p.assists !== undefined ? p.assists : (p.Assists || 0);
            const icon = this.getHeroIconPath(heroId);
            const isSelected = this.selectedPlayer?.accountID === p.accountID;
            return `
                <div onclick="window.ActiveMinimapVisualizer?.selectPlayerByAccountId(${p.accountID})"
                     class="p-2 rounded-lg ${isSelected ? 'bg-sky-950/80 border-sky-400 ring-1 ring-sky-400 shadow-md shadow-sky-500/20' : 'bg-slate-950/60 border-sky-500/20 hover:bg-slate-900/90 hover:border-sky-500/50'} border flex items-center gap-2 cursor-pointer transition-all duration-150 group"
                     title="Click to inspect ${name}">
                    <div class="w-6 h-6 rounded-md overflow-hidden bg-slate-900 flex-shrink-0 border border-slate-700/60 group-hover:border-sky-400">
                        <img src="${icon}" onError="this.src='/images/heroes/default.png';" class="w-full h-full object-cover" alt=""/>
                    </div>
                    <div class="min-w-0 flex-1">
                        <div class="font-bold text-white text-[11px] truncate group-hover:text-sky-300 transition-colors">${name}</div>
                        <div class="text-[9px] text-slate-400 font-mono">${kills}/${deaths}/${assists} · Lv ${p.level || 1}</div>
                    </div>
                </div>
            `;
        }).join('') || '<div class="text-[10px] text-slate-500 italic p-1">No players</div>';

        hellbourneContainer.innerHTML = hellbournePlayers.map(p => {
            const heroId = p.heroIdentifier || p.HeroIdentifier || '';
            const name = this.escapeHtml(p.accountName || p.AccountName || 'Player');
            const kills = p.kills !== undefined ? p.kills : (p.Kills || 0);
            const deaths = p.deaths !== undefined ? p.deaths : (p.Deaths || 0);
            const assists = p.assists !== undefined ? p.assists : (p.Assists || 0);
            const icon = this.getHeroIconPath(heroId);
            const isSelected = this.selectedPlayer?.accountID === p.accountID;
            return `
                <div onclick="window.ActiveMinimapVisualizer?.selectPlayerByAccountId(${p.accountID})"
                     class="p-2 rounded-lg ${isSelected ? 'bg-rose-950/80 border-rose-400 ring-1 ring-rose-400 shadow-md shadow-rose-500/20' : 'bg-slate-950/60 border-rose-500/20 hover:bg-slate-900/90 hover:border-rose-500/50'} border flex items-center gap-2 cursor-pointer transition-all duration-150 group"
                     title="Click to inspect ${name}">
                    <div class="w-6 h-6 rounded-md overflow-hidden bg-slate-900 flex-shrink-0 border border-slate-700/60 group-hover:border-rose-400">
                        <img src="${icon}" onError="this.src='/images/heroes/default.png';" class="w-full h-full object-cover" alt=""/>
                    </div>
                    <div class="min-w-0 flex-1">
                        <div class="font-bold text-white text-[11px] truncate group-hover:text-rose-300 transition-colors">${name}</div>
                        <div class="text-[9px] text-slate-400 font-mono">${kills}/${deaths}/${assists} · Lv ${p.level || 1}</div>
                    </div>
                </div>
            `;
        }).join('') || '<div class="text-[10px] text-slate-500 italic p-1">No players</div>';
    }

    onDeltaTick(delta) {
        this.lastTickId = delta.tickId || delta.TickId || this.lastTickId;
        this.match.elapsedSeconds = delta.elapsedSeconds !== undefined ? delta.elapsedSeconds : delta.ElapsedSeconds;
        this.match.scoreLegion = delta.scoreLegion !== undefined ? delta.scoreLegion : delta.ScoreLegion;
        this.match.scoreHellbourne = delta.scoreHellbourne !== undefined ? delta.scoreHellbourne : delta.ScoreHellbourne;

        const pDeltas = delta.playerDeltas || delta.PlayerDeltas || [];
        if (pDeltas.length > 0) {
            let rosterChanged = false;
            for (const pDelta of pDeltas) {
                const accId = pDelta.accountID || pDelta.accountId || pDelta.AccountID || pDelta.AccountId || 1;
                let playerState = this.interpolatedPlayers.get(accId);
                const team = pDelta.team || pDelta.Team || 1;
                const heroId = pDelta.heroIdentifier || pDelta.HeroIdentifier || '';
                const posX = pDelta.positionX !== undefined ? pDelta.positionX : (pDelta.PositionX !== undefined ? pDelta.PositionX : 0.5);
                const posY = pDelta.positionY !== undefined ? pDelta.positionY : (pDelta.PositionY !== undefined ? pDelta.PositionY : 0.5);
                const gold = pDelta.currentGold !== undefined ? pDelta.currentGold : (pDelta.CurrentGold !== undefined ? pDelta.CurrentGold : 0);

                if (!playerState) {
                    const newPlayer = {
                        accountID: accId,
                        accountName: pDelta.accountName || pDelta.AccountName || 'guccidonut',
                        team: team,
                        heroIdentifier: heroId,
                        heroDisplayName: pDelta.heroDisplayName || pDelta.HeroDisplayName || 'Picking Hero...',
                        positionX: posX,
                        positionY: posY,
                        level: pDelta.level || pDelta.Level || 1,
                        healthPercent: pDelta.healthPercent !== undefined ? pDelta.healthPercent : 100,
                        manaPercent: pDelta.manaPercent !== undefined ? pDelta.manaPercent : 100,
                        isAlive: pDelta.isAlive !== false,
                        currentGold: gold,
                        kills: pDelta.kills || 0,
                        deaths: pDelta.deaths || 0,
                        assists: pDelta.assists || 0,
                        inventory: pDelta.inventory || [],
                        abilities: []
                    };
                    this.players.push(newPlayer);
                    const slotIndex = newPlayer.team === 1 ? (this.players.length - 1) : (this.players.length + 4);
                    const playerSlotColor = ExactPlayerColors[slotIndex % ExactPlayerColors.length];

                    playerState = {
                        currentX: newPlayer.positionX,
                        currentY: newPlayer.positionY,
                        targetX: newPlayer.positionX,
                        targetY: newPlayer.positionY,
                        headingAngle: newPlayer.team === 1 ? 0.785 : -2.356,
                        health: newPlayer.healthPercent,
                        mana: newPlayer.manaPercent,
                        level: newPlayer.level,
                        isAlive: newPlayer.isAlive,
                        respawnTimer: 0,
                        heroId: newPlayer.heroIdentifier,
                        name: newPlayer.accountName,
                        team: newPlayer.team,
                        slotColor: playerSlotColor,
                        items: newPlayer.inventory,
                        abilities: []
                    };
                    this.interpolatedPlayers.set(accId, playerState);
                    rosterChanged = true;
                } else {
                    if (team > 0 && team !== playerState.team) {
                        playerState.team = team;
                        const pl = this.players.find(p => p.accountID === accId);
                        if (pl) pl.team = team;
                        rosterChanged = true;
                    }
                    playerState.targetX = posX;
                    playerState.targetY = posY;
                    if (pDelta.headingAngle !== undefined) playerState.headingAngle = pDelta.headingAngle;
                    if (pDelta.healthPercent !== undefined) playerState.health = pDelta.healthPercent;
                    if (pDelta.manaPercent !== undefined) playerState.mana = pDelta.manaPercent;
                    if (pDelta.level !== undefined) playerState.level = pDelta.level;
                    if (pDelta.isAlive !== undefined) playerState.isAlive = pDelta.isAlive;
                    if (heroId) playerState.heroId = heroId;
                    if (pDelta.inventory) playerState.items = pDelta.inventory;
                    if (pDelta.abilities) playerState.abilities = pDelta.abilities;
                    if (pDelta.lastCastAbility) playerState.lastCastAbility = pDelta.lastCastAbility;

                    const pl = this.players.find(p => p.accountID === accId);
                    if (pl) {
                        pl.positionX = posX;
                        pl.positionY = posY;
                        pl.currentGold = gold;
                        if (pDelta.kills !== undefined) pl.kills = pDelta.kills;
                        if (pDelta.deaths !== undefined) pl.deaths = pDelta.deaths;
                        if (pDelta.assists !== undefined) pl.assists = pDelta.assists;
                        if (pDelta.level !== undefined) pl.level = pDelta.level;
                        if (pDelta.isAlive !== undefined) pl.isAlive = pDelta.isAlive;
                        if (pDelta.gpm !== undefined) pl.gpm = pDelta.gpm;
                        if (pDelta.xpm !== undefined) pl.xpm = pDelta.xpm;
                        if (pDelta.inventory) pl.inventory = pDelta.inventory;
                        if (pDelta.abilities) pl.abilities = pDelta.abilities;
                        if (pDelta.lastCastAbility) pl.lastCastAbility = pDelta.lastCastAbility;
                        if (heroId) {
                            pl.heroIdentifier = heroId;
                            pl.heroDisplayName = this.getHeroDisplayName(heroId);
                        }
                    }
                }
            }
            if (rosterChanged) {
                this.updateRosterUI();
            }
            if (this.selectedPlayer) {
                const updatedSel = this.players.find(p => p.accountID === this.selectedPlayer.accountID);
                if (updatedSel) {
                    this.selectedPlayer = updatedSel;
                    this.updateInspector(this.selectedPlayer);
                }
            }
        }

        const events = delta.recentEvents || delta.RecentEvents;
        if (events && events.length > 0) {
            this.recentEvents = events;
            this.updateCombatFeedUI();
        }

        this.updateHeaderStats();
    }

    initGoldChart() {
        const canvas = document.getElementById(this.goldChartId);
        if (!canvas || typeof Chart === 'undefined') return;

        if (this.goldChart) this.goldChart.destroy();

        const labels = this.goldTimeline.map(g => `${Math.floor(g.second / 60)}m`);
        const dataAdvantage = this.goldTimeline.map(g => g.goldAdvantage);

        this.goldChart = new Chart(canvas.getContext('2d'), {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Net Worth Advantage (Legion vs Hellbourne)',
                    data: dataAdvantage,
                    borderColor: '#38bdf8',
                    backgroundColor: 'rgba(56, 189, 248, 0.15)',
                    fill: true,
                    tension: 0.35,
                    pointRadius: 2,
                    borderWidth: 2
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {grid: {color: 'rgba(255, 255, 255, 0.05)'}, ticks: {color: '#94a3b8', font: {size: 10}}},
                    y: {
                        grid: {color: 'rgba(255, 255, 255, 0.08)'},
                        ticks: {
                            color: '#94a3b8',
                            callback: (v) => (v >= 0 ? `+${v}` : `${v}`)
                        }
                    }
                },
                plugins: {
                    legend: {display: false}
                }
            }
        });
    }

    rebuildGoldChart() {
        if (!this.goldChart) return;
        this.goldChart.data.labels = this.goldTimeline.map(g => `${Math.floor(g.second / 60)}m`);
        this.goldChart.data.datasets[0].data = this.goldTimeline.map(g => g.goldAdvantage);
        this.goldChart.update();
    }

    pushGoldDataPoint(point) {
        if (!this.goldChart) return;
        this.goldChart.data.labels.push(`${Math.floor(point.second / 60)}m`);
        this.goldChart.data.datasets[0].data.push(point.goldAdvantage);
        this.goldChart.update('none');
    }

    startRenderLoop() {
        const render = (now) => {
            if (document.hidden) {
                this.animFrameId = null;
                return;
            }
            const dt = Math.min((now - this.lastFrameTime) / 1000, 0.1);
            this.lastFrameTime = now;

            if (this.isReplayMode && this.isPlaying) {
                this.playbackTime = Math.min(this.replayDuration, this.playbackTime + dt * this.playbackSpeed);
                this.applyReplayTime(this.playbackTime);
                this.updateReplayTimelineUI();
                if (this.playbackTime >= this.replayDuration) {
                    this.isPlaying = false;
                    this.togglePlayback();
                }
            }

            this.render(dt);
            this.animFrameId = requestAnimationFrame(render);
        };
        if (!this._visBound) {
            this._visBound = true;
            document.addEventListener('visibilitychange', () => {
                if (!document.hidden && !this.animFrameId) {
                    this.lastFrameTime = performance.now();
                    this.animFrameId = requestAnimationFrame(render);
                }
            });
        }
        if (!document.hidden) {
            this.animFrameId = requestAnimationFrame(render);
        }
    }

    render(dt) {
        const width = this.logicalWidth;
        const height = this.logicalHeight;
        const ctx = this.ctx;

        ctx.clearRect(0, 0, width, height);

        // Layer 0: Static Map Background & Structures
        if (this.isOffscreenDirty) {
            this.renderOffscreenBase();
            this.isOffscreenDirty = false;
        }
        ctx.drawImage(this.offscreenCanvas, 0, 0, width, height);

        // Layer 1: Ward Vision Radii
        this.renderWards(ctx, width, height);

        // Layer 2: Player Hero Units
        this.renderPlayers(ctx, width, height, dt);
    }

    renderOffscreenBase() {
        const ctx = this.offscreenCtx;
        const width = this.logicalWidth;
        const height = this.logicalHeight;

        ctx.clearRect(0, 0, width, height);

        if (this.mapImage.complete && this.mapImage.naturalWidth > 0) {
            ctx.drawImage(this.mapImage, 0, 0, width, height);
        } else {
            ctx.fillStyle = '#0f172a';
            ctx.fillRect(0, 0, width, height);
        }

        this.drawStructures(ctx, width, height);
    }

    drawStructures(ctx, width, height) {
        const isMidWars = Boolean(this.match.map && (this.match.map.toLowerCase().includes('midwars') || this.match.map.toLowerCase().includes('mw')));
        const structureList = isMidWars ? MidWarsStructures : CaldavarStructures;

        for (const s of structureList) {
            const sx = s.x * width;
            const sy = s.y * height;
            const isAlive = this.structures[s.id] !== false;
            const isLegion = s.team === 1;
            const teamColor = isLegion ? '#38bdf8' : '#f43f5e';
            const teamGlow = isLegion ? 'rgba(56, 189, 248, 0.45)' : 'rgba(244, 63, 94, 0.45)';

            ctx.save();
            if (s.type === 'Base') {
                const baseSize = width * 0.024;
                ctx.beginPath();
                ctx.arc(sx, sy, baseSize, 0, Math.PI * 2);
                ctx.fillStyle = isAlive ? teamGlow : 'rgba(15, 23, 42, 0.6)';
                ctx.fill();
                ctx.lineWidth = 2;
                ctx.strokeStyle = isAlive ? teamColor : '#475569';
                ctx.stroke();

                ctx.beginPath();
                ctx.arc(sx, sy, baseSize * 0.5, 0, Math.PI * 2);
                ctx.fillStyle = isAlive ? teamColor : '#64748b';
                ctx.fill();
            } else {
                // Towers & Rax
                const towerRadius = width * 0.015;
                ctx.beginPath();
                ctx.arc(sx, sy, towerRadius, 0, Math.PI * 2);
                ctx.fillStyle = isAlive ? teamColor : '#334155';
                ctx.fill();
                ctx.lineWidth = 1.5;
                ctx.strokeStyle = isAlive ? '#ffffff' : '#64748b';
                ctx.stroke();

                if (!isAlive) {
                    ctx.beginPath();
                    ctx.moveTo(sx - towerRadius * 0.5, sy - towerRadius * 0.5);
                    ctx.lineTo(sx + towerRadius * 0.5, sy + towerRadius * 0.5);
                    ctx.moveTo(sx + towerRadius * 0.5, sy - towerRadius * 0.5);
                    ctx.lineTo(sx - towerRadius * 0.5, sy + towerRadius * 0.5);
                    ctx.strokeStyle = '#ef4444';
                    ctx.lineWidth = 1.5;
                    ctx.stroke();
                }
            }
            ctx.restore();
        }
    }

    renderWards(ctx, width, height) {
        for (const ward of this.wards) {
            const wx = ward.positionX * width;
            const wy = ward.positionY * height;
            const isSight = (ward.wardType || 'Sight') === 'Sight';
            const radius = (ward.visionRadius || (isSight ? 0.085 : 0.065)) * width;
            const wardRgb = isSight ? '234, 179, 8' : '217, 70, 239'; // Yellow for Sight, Purple for Rev
            const eyeColor = isSight ? '#eab308' : '#d946ef';

            ctx.save();
            ctx.beginPath();
            ctx.arc(wx, wy, radius, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(${wardRgb}, 0.14)`;
            ctx.fill();
            ctx.lineWidth = 1.5;
            ctx.setLineDash([4, 4]);
            ctx.strokeStyle = `rgba(${wardRgb}, 0.70)`;
            ctx.stroke();

            // Ward Center Eye
            ctx.beginPath();
            ctx.arc(wx, wy, 5.5, 0, Math.PI * 2);
            ctx.fillStyle = eyeColor;
            ctx.fill();
            ctx.strokeStyle = '#ffffff';
            ctx.lineWidth = 1.5;
            ctx.setLineDash([]);
            ctx.stroke();
            ctx.restore();
        }
    }

    renderPlayers(ctx, width, height, dt) {
        const decayRate = 8.0;
        const lerpFactor = 1 - Math.exp(-decayRate * dt);

        for (const [accId, p] of this.interpolatedPlayers) {
            if (!p.isAlive) continue;

            p.currentX += (p.targetX - p.currentX) * lerpFactor;
            p.currentY += (p.targetY - p.currentY) * lerpFactor;

            const px = p.currentX * width;
            const py = p.currentY * height;
            const radius = width * 0.024;

            const isSelected = this.selectedPlayer?.accountID === accId;
            const isHovered = this.hoveredPlayer?.accountID === accId;

            // Outer Selection / Hover Aura
            if (isSelected || isHovered) {
                ctx.beginPath();
                ctx.arc(px, py, radius + 5, 0, Math.PI * 2);
                ctx.fillStyle = isSelected ? 'rgba(168, 85, 247, 0.40)' : 'rgba(56, 189, 248, 0.35)';
                ctx.fill();
                ctx.lineWidth = 2;
                ctx.strokeStyle = isSelected ? '#c084fc' : '#38bdf8';
                ctx.stroke();
            }

            // Hero Circle Avatar
            ctx.save();
            ctx.beginPath();
            ctx.arc(px, py, radius, 0, Math.PI * 2);
            ctx.clip();

            let heroImg = this.heroImages.get(p.heroId);
            if (!heroImg && p.heroId) {
                heroImg = new Image();
                heroImg.src = this.getHeroIconPath(p.heroId);
                heroImg.onerror = () => {
                    heroImg.src = '/images/heroes/default.png';
                };
                this.heroImages.set(p.heroId, heroImg);
            }

            if (heroImg && heroImg.complete && heroImg.naturalWidth > 0) {
                ctx.drawImage(heroImg, px - radius, py - radius, radius * 2, radius * 2);
            } else {
                ctx.fillStyle = p.team === 1 ? '#2563eb' : '#e11d48';
                ctx.fillRect(px - radius, py - radius, radius * 2, radius * 2);
            }
            ctx.restore();

            // Authentic HoN Player-Slot Colored Outer Ring
            ctx.beginPath();
            ctx.arc(px, py, radius, 0, Math.PI * 2);
            ctx.lineWidth = 3;
            ctx.strokeStyle = p.slotColor || (p.team === 1 ? '#38bdf8' : '#E55BB0');
            ctx.stroke();

            // Inner Team Outline (Sky Blue for Legion / Red for Hellbourne)
            ctx.beginPath();
            ctx.arc(px, py, radius - 2, 0, Math.PI * 2);
            ctx.lineWidth = 1.5;
            ctx.strokeStyle = p.team === 1 ? '#38bdf8' : '#ef4444';
            ctx.stroke();

            // Directional Heading Indicator Pointer
            if (p.headingAngle !== undefined) {
                const hx = px + Math.cos(p.headingAngle) * (radius + 4);
                const hy = py + Math.sin(p.headingAngle) * (radius + 4);
                ctx.beginPath();
                ctx.arc(hx, hy, 3, 0, Math.PI * 2);
                ctx.fillStyle = p.slotColor || '#ffffff';
                ctx.fill();
                ctx.strokeStyle = '#000000';
                ctx.lineWidth = 1;
                ctx.stroke();
            }

            // Overhead Health Bar
            const barW = radius * 2.2;
            const barH = 3;
            const barX = px - barW / 2;
            const barY = py - radius - 6;

            ctx.fillStyle = 'rgba(15, 23, 42, 0.8)';
            ctx.fillRect(barX - 1, barY - 1, barW + 2, barH + 2);
            ctx.fillStyle = p.team === 1 ? '#38bdf8' : '#f43f5e';
            ctx.fillRect(barX, barY, (p.health / 100) * barW, barH);

            // Level Pill Badge
            ctx.beginPath();
            ctx.arc(px + radius - 2, py + radius - 2, 6, 0, Math.PI * 2);
            ctx.fillStyle = '#0f172a';
            ctx.fill();
            ctx.strokeStyle = p.team === 1 ? '#38bdf8' : '#f43f5e';
            ctx.lineWidth = 1;
            ctx.stroke();
            ctx.fillStyle = p.team === 1 ? '#38bdf8' : '#fb7185';
            ctx.font = 'bold 8px monospace';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(p.level.toString(), px + radius - 2, py + radius - 2);

            // Player Name
            ctx.fillStyle = '#ffffff';
            ctx.font = 'bold 9px sans-serif';
            ctx.textAlign = 'center';
            ctx.shadowColor = '#000000';
            ctx.shadowBlur = 4;
            ctx.fillText(p.name, px, py + radius + 11);
            ctx.shadowBlur = 0;
        }
    }

    drawStructures(ctx, width, height) {
        const structures = this.match.structures || {};
        const isMidwars = this.mapType.includes('midwars');
        const structureList = isMidwars ? MidWarsStructures : CaldavarStructures;

        for (const st of structureList) {
            const isAlive = structures[st.id] !== false;
            const sx = st.x * width;
            const sy = st.y * height;
            const teamColor = st.team === 1 ? '#22c55e' : '#ef4444';

            ctx.save();
            if (st.type === 'Tower') {
                // Square Tower Icon
                ctx.beginPath();
                ctx.rect(sx - 4.5, sy - 4.5, 9, 9);
                ctx.fillStyle = isAlive ? teamColor : '#334155';
                ctx.fill();
                ctx.strokeStyle = isAlive ? '#ffffff' : '#1e293b';
                ctx.lineWidth = 1.2;
                ctx.stroke();
            } else if (st.type === 'MRax') {
                // Diamond Melee Rax Icon
                ctx.beginPath();
                ctx.moveTo(sx, sy - 4);
                ctx.lineTo(sx + 4, sy);
                ctx.lineTo(sx, sy + 4);
                ctx.lineTo(sx - 4, sy);
                ctx.closePath();
                ctx.fillStyle = isAlive ? teamColor : '#334155';
                ctx.fill();
                ctx.strokeStyle = isAlive ? '#facc15' : '#1e293b';
                ctx.lineWidth = 1;
                ctx.stroke();
            } else if (st.type === 'RRax') {
                // Diamond Range Rax Icon
                ctx.beginPath();
                ctx.moveTo(sx, sy - 3.5);
                ctx.lineTo(sx + 3.5, sy);
                ctx.lineTo(sx, sy + 3.5);
                ctx.lineTo(sx - 3.5, sy);
                ctx.closePath();
                ctx.fillStyle = isAlive ? teamColor : '#334155';
                ctx.fill();
                ctx.strokeStyle = isAlive ? '#38bdf8' : '#1e293b';
                ctx.lineWidth = 1;
                ctx.stroke();
            } else if (st.type === 'Base') {
                // Main Throne Fortress Icon
                ctx.beginPath();
                ctx.arc(sx, sy, 10, 0, Math.PI * 2);
                ctx.fillStyle = isAlive ? (st.team === 1 ? '#0284c7' : '#e11d48') : '#1e293b';
                ctx.fill();
                ctx.strokeStyle = '#facc15';
                ctx.lineWidth = 2;
                ctx.stroke();
            }
            ctx.restore();
        }
    }

    updateHeaderStats() {
        const timeEl = document.getElementById('live-match-timer');
        if (timeEl) {
            const s = this.match.elapsedSeconds || 0;
            const m = Math.floor(s / 60);
            const sec = s % 60;
            timeEl.textContent = `${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
        }

        const scoreLegion = document.getElementById('score-legion');
        const scoreHell = document.getElementById('score-hellbourne');
        if (scoreLegion) scoreLegion.textContent = this.match.scoreLegion || 0;
        if (scoreHell) scoreHell.textContent = this.match.scoreHellbourne || 0;

        // Push Bar (Gold / Experience balance bar matching specui.lua)
        const pushBarLegion = document.getElementById('push-bar-legion');
        const pushBarHell = document.getElementById('push-bar-hellbourne');
        if (pushBarLegion && pushBarHell) {
            const scoreL = this.match.scoreLegion || 0;
            const scoreH = this.match.scoreHellbourne || 0;
            let legionPct = 50;
            if (scoreL + scoreH > 0) {
                legionPct = Math.max(15, Math.min(85, (scoreL / (scoreL + scoreH)) * 100));
            }
            pushBarLegion.style.width = `${legionPct}%`;
            pushBarHell.style.width = `${100 - legionPct}%`;
        }
    }

    updateCombatFeedUI() {
        const feedContainer = document.getElementById('live-combat-feed');
        if (!feedContainer) return;

        const events = this.recentEvents || [];
        if (events.length === 0) {
            feedContainer.innerHTML = '<div class="text-[10px] text-slate-500 italic p-2 text-center">No combat events yet</div>';
            return;
        }

        feedContainer.innerHTML = events.slice(0, 15).map(ev => {
            const type = (ev.eventType || ev.EventType || 'KILL').toUpperCase();
            let icon = '⚔️';
            let color = 'text-rose-400';
            if (type === 'TOWER') {
                icon = '🏰';
                color = 'text-amber-400';
            } else if (type === 'BOSS') {
                icon = '👑';
                color = 'text-purple-400';
            } else if (type === 'WARD') {
                icon = '👁️';
                color = 'text-emerald-400';
            } else if (type === 'ITEM') {
                icon = '🏆';
                color = 'text-yellow-400';
            }

            const desc = this.escapeHtml(ev.description || ev.Description || `${ev.attackerName || 'Player'} defeated ${ev.victimName || 'Target'}`);
            const timeSec = ev.matchTimeSeconds !== undefined ? ev.matchTimeSeconds : (ev.MatchTimeSeconds || 0);
            const m = Math.floor(timeSec / 60);
            const s = timeSec % 60;
            const timeStr = `${m}:${s.toString().padStart(2, '0')}`;

            return `
                <div class="p-2 rounded-lg bg-slate-950/70 border border-slate-800 text-xs flex items-center justify-between gap-2 animate-fadeIn hover:border-slate-700 transition-colors">
                    <div class="flex items-center gap-2 min-w-0">
                        <span class="${color} font-bold flex-shrink-0 text-sm">${icon}</span>
                        <span class="text-slate-200 font-medium truncate">${desc}</span>
                    </div>
                    <span class="text-[10px] text-slate-500 font-mono flex-shrink-0">${timeStr}</span>
                </div>
            `;
        }).join('');
    }

    appendCombatEvent(ev) {
        if (!this.recentEvents) this.recentEvents = [];
        this.recentEvents.unshift(ev);
        if (this.recentEvents.length > 20) this.recentEvents.pop();
        this.updateCombatFeedUI();
    }

    pushGoldDataPoint(goldPoint) {
        if (!this.goldTimeline) this.goldTimeline = [];
        this.goldTimeline.push(goldPoint);
        this.rebuildGoldChart();
    }

    rebuildGoldChart() {
        const canvas = document.getElementById('live-gold-chart-canvas');
        if (!canvas) return;

        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        const width = rect.width || 600;
        const height = rect.height || 112;

        if (canvas.width !== Math.floor(width * dpr) || canvas.height !== Math.floor(height * dpr)) {
            canvas.width = Math.floor(width * dpr);
            canvas.height = Math.floor(height * dpr);
        }

        ctx.save();
        ctx.scale(dpr, dpr);
        ctx.clearRect(0, 0, width, height);

        const timeline = this.goldTimeline || [];
        const badge = document.getElementById('gold-lead-badge');

        if (timeline.length === 0) {
            // Draw empty baseline
            ctx.strokeStyle = '#334155';
            ctx.lineWidth = 1;
            ctx.setLineDash([4, 4]);
            ctx.beginPath();
            ctx.moveTo(10, height / 2);
            ctx.lineTo(width - 10, height / 2);
            ctx.stroke();

            ctx.fillStyle = '#64748b';
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText('Awaiting match gold telemetry...', width / 2, height / 2 - 8);
            ctx.restore();
            if (badge) {
                badge.textContent = 'Even';
                badge.className = 'px-2 py-0.5 rounded text-[10px] font-mono font-bold bg-slate-800 text-slate-300';
            }
            return;
        }

        let maxAdv = 1000;
        const points = timeline.map(p => {
            const time = p.timeSeconds !== undefined ? p.timeSeconds : (p.TimeSeconds || 0);
            const adv = p.legionAdvantage !== undefined ? p.legionAdvantage : (p.LegionAdvantage || 0);
            if (Math.abs(adv) > maxAdv) maxAdv = Math.abs(adv);
            return {time, adv};
        });

        points.sort((a, b) => a.time - b.time);
        const maxTime = Math.max(60, points[points.length - 1].time);
        const lastAdv = points[points.length - 1].adv;

        if (badge) {
            if (Math.abs(lastAdv) < 150) {
                badge.textContent = 'Even';
                badge.className = 'px-2 py-0.5 rounded text-[10px] font-mono font-bold bg-slate-800 text-slate-300';
            } else if (lastAdv > 0) {
                badge.textContent = `+${Math.round(lastAdv).toLocaleString()} Legion`;
                badge.className = 'px-2 py-0.5 rounded text-[10px] font-mono font-bold bg-sky-950 text-sky-300 border border-sky-500/40';
            } else {
                badge.textContent = `+${Math.round(Math.abs(lastAdv)).toLocaleString()} Hellbourne`;
                badge.className = 'px-2 py-0.5 rounded text-[10px] font-mono font-bold bg-rose-950 text-rose-300 border border-rose-500/40';
            }
        }

        const padX = 20;
        const padY = 12;
        const chartW = width - padX * 2;
        const chartH = height - padY * 2;
        const zeroY = padY + chartH / 2;

        // Draw center zero line
        ctx.strokeStyle = '#475569';
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 3]);
        ctx.beginPath();
        ctx.moveTo(padX, zeroY);
        ctx.lineTo(padX + chartW, zeroY);
        ctx.stroke();
        ctx.setLineDash([]);

        const coords = points.map(p => {
            const x = padX + (p.time / maxTime) * chartW;
            const y = zeroY - (p.adv / maxAdv) * (chartH / 2 * 0.88);
            return {x, y, adv: p.adv};
        });

        if (coords.length > 1) {
            // Gradient area fill
            ctx.beginPath();
            ctx.moveTo(coords[0].x, zeroY);
            for (const pt of coords) {
                ctx.lineTo(pt.x, pt.y);
            }
            ctx.lineTo(coords[coords.length - 1].x, zeroY);
            ctx.closePath();

            const grad = ctx.createLinearGradient(0, padY, 0, height - padY);
            grad.addColorStop(0, 'rgba(56, 189, 248, 0.35)');
            grad.addColorStop(0.5, 'rgba(148, 163, 184, 0.05)');
            grad.addColorStop(1, 'rgba(244, 63, 94, 0.35)');
            ctx.fillStyle = grad;
            ctx.fill();

            // Line stroke
            ctx.beginPath();
            ctx.moveTo(coords[0].x, coords[0].y);
            for (let i = 1; i < coords.length; i++) {
                ctx.lineTo(coords[i].x, coords[i].y);
            }
            ctx.strokeStyle = lastAdv >= 0 ? '#38bdf8' : '#fb7185';
            ctx.lineWidth = 2;
            ctx.stroke();

            // Highlight latest point
            const lastPt = coords[coords.length - 1];
            ctx.beginPath();
            ctx.arc(lastPt.x, lastPt.y, 4, 0, Math.PI * 2);
            ctx.fillStyle = lastAdv >= 0 ? '#38bdf8' : '#fb7185';
            ctx.fill();
            ctx.strokeStyle = '#ffffff';
            ctx.lineWidth = 1.5;
            ctx.stroke();
        }

        ctx.restore();
    }

    updateTooltip(e, player, ward) {
        const tooltip = document.getElementById('minimap-tooltip');
        if (!tooltip) return;

        if (!player && !ward) {
            tooltip.classList.add('hidden');
            return;
        }

        tooltip.classList.remove('hidden');

        const tooltipWidth = 270;
        const tooltipHeight = 85;
        let left = e.clientX + 14;
        let top = e.clientY + 14;

        if (left + tooltipWidth > window.innerWidth - 12) left = e.clientX - tooltipWidth - 12;
        if (top + tooltipHeight > window.innerHeight - 12) top = e.clientY - tooltipHeight - 12;
        if (top < 10) top = 10;
        if (left < 10) left = 10;

        tooltip.style.position = 'fixed';
        tooltip.style.left = `${left}px`;
        tooltip.style.top = `${top}px`;
        tooltip.style.zIndex = '99999';
        tooltip.style.pointerEvents = 'none';

        if (ward) {
            const isSight = (ward.wardType || 'Sight') === 'Sight';
            const wardName = isSight ? 'Ward of Sight' : 'Ward of Revelation';
            const iconPath = isSight ? '/images/items/wardofsight.png' : '/images/items/wardofrevelation.png';
            const teamBadge = ward.team === 1 ? 'Legion' : 'Hellbourne';
            const teamColor = ward.team === 1 ? 'text-sky-400 border-sky-500/40 bg-sky-950/40' : 'text-rose-400 border-rose-500/40 bg-rose-950/40';
            const placer = this.escapeHtml(ward.placerHeroName || (ward.team === 1 ? 'Legion Hero' : 'Hellbourne Hero'));

            tooltip.innerHTML = `
                <div class="flex items-center gap-2.5">
                    <div class="w-8 h-8 rounded-lg overflow-hidden bg-slate-950 border border-slate-700/80 p-0.5 shadow-sm flex-shrink-0">
                        <img src="${iconPath}" class="w-full h-full object-contain" onerror="this.src='/images/items/default.png';"/>
                    </div>
                    <div class="flex-1 min-w-0">
                        <div class="font-bold text-white text-xs flex items-center justify-between">
                            <span>${wardName}</span>
                            <span class="text-[9px] px-1.5 py-0.5 rounded border font-mono ${teamColor}">${teamBadge}</span>
                        </div>
                        <div class="text-[11px] text-amber-300 font-semibold mt-0.5 truncate">
                            Placed by: <span class="text-white font-bold">${placer}</span>
                        </div>
                    </div>
                </div>
                <div class="text-[10px] text-slate-400 mt-1.5 flex items-center justify-between font-mono border-t border-slate-800/80 pt-1">
                    <span>Vision: <b class="text-purple-300">${isSight ? '1600 Range' : '850 (True Sight)'}</b></span>
                    <span>${ward.expiresInSeconds || 360}s Duration</span>
                </div>
            `;
            return;
        }

        const heroIcon = this.getHeroIconPath(player.heroIdentifier);
        const displayName = player.heroDisplayName || this.getHeroDisplayName(player.heroIdentifier);

        tooltip.innerHTML = `
            <div class="flex items-center gap-2.5">
                <img src="${heroIcon}" class="w-8 h-8 rounded-lg object-cover border border-slate-700 shadow-sm flex-shrink-0" onerror="this.src='/images/heroes/default.png';"/>
                <div class="flex-1 min-w-0">
                    <div class="font-bold text-white text-xs flex items-center justify-between">
                        <span class="truncate">${this.escapeHtml(player.accountName)}</span>
                        <span class="text-[10px] font-mono font-bold ${player.team === 1 ? 'text-sky-400' : 'text-rose-400'}">Team ${player.team}</span>
                    </div>
                    <div class="text-[11px] text-purple-300 font-semibold truncate">
                        ${displayName} · Level ${player.level}
                    </div>
                </div>
            </div>
            <div class="text-[10px] text-slate-300 mt-1.5 flex justify-between font-mono border-t border-slate-800/80 pt-1">
                <span>KDA: <b class="text-amber-400">${player.kills || 0}/${player.deaths || 0}/${player.assists || 0}</b></span>
                <span>GPM: <b class="text-emerald-400">${player.gpm || 0}</b></span>
                <span>Gold: <b class="text-yellow-400">${(player.currentGold || 0).toLocaleString()} GC</b></span>
            </div>
        `;
    }

    getHeroDefaultAbilities(heroIdentifier) {
        if (!heroIdentifier) return [];
        const clean = heroIdentifier.toLowerCase().replace('hero_', '').trim();
        const slots = ['Q', 'W', 'E', 'R'];
        const abilityNames = {
            'bubbles': ['Shell Surf', 'Song of the Sea', 'Take Cover', 'Kelp Field'],
            'hammerstorm': ['Hammer Throw', 'Galvanize', 'Brute Strength', 'Gods Vigor'],
            'swiftblade': ['Blade Frenzy', 'Way of the Sword', 'Counter-Attack', 'Swift Slashes'],
            'pyromancer': ['Phoenix Wave', 'Dragon Slave', 'Fervor', 'Blazing Strike'],
            'witch_slayer': ['Graveyard', 'Miniaturization', 'Power Drain', 'Silver Bullet'],
            'devourer': ['Gudgeon Hook', 'Decay', 'Cadaver Armor', 'Devour'],
            'chronos': ['Time Leap', 'Rewind', 'Curse of Ages', 'Chronosphere']
        };

        const names = abilityNames[clean] || ['Ability 1', 'Ability 2', 'Ability 3', 'Ultimate'];
        return slots.map((slot, idx) => ({
            slot: slot,
            name: names[idx],
            iconPath: `/images/abilities/${clean}_${idx + 1}.png`,
            cooldownRemaining: 0
        }));
    }

    updateInspector(player) {
        const inspector = document.getElementById('player-inspector');
        if (!inspector || !player) return;

        const heroIcon = this.getHeroIconPath(player.heroIdentifier);
        const playerState = this.interpolatedPlayers.get(player.accountID);
        let abilities = playerState?.abilities || player.abilities || [];
        if (!abilities || abilities.length === 0) {
            abilities = this.getHeroDefaultAbilities(player.heroIdentifier);
        }
        const items = playerState?.items || player.inventory || [];
        const displayName = player.heroDisplayName || this.getHeroDisplayName(player.heroIdentifier);

        inspector.innerHTML = `
            <div class="flex items-center gap-3">
                <div class="w-12 h-12 rounded-xl overflow-hidden bg-slate-900 border border-purple-500/30">
                    <img src="${heroIcon}" onError="this.src='/images/heroes/default.png';" class="w-full h-full object-cover"/>
                </div>
                <div>
                    <h4 class="text-base font-bold text-white">${this.escapeHtml(player.accountName)}</h4>
                    <div class="text-xs text-purple-300 font-semibold">${displayName} · Level ${player.level}</div>
                </div>
            </div>
            <div class="grid grid-cols-4 gap-2 pt-2 border-t border-slate-800 text-center">
                <div class="p-2 rounded-lg bg-slate-950/60">
                    <div class="text-[10px] text-slate-500 uppercase font-bold">KDA</div>
                    <div class="text-xs font-mono font-black text-white">${player.kills || 0}/${player.deaths || 0}/${player.assists || 0}</div>
                </div>
                <div class="p-2 rounded-lg bg-slate-950/60">
                    <div class="text-[10px] text-slate-500 uppercase font-bold">GPM</div>
                    <div class="text-xs font-mono font-black text-emerald-400">${player.gpm || 0}</div>
                </div>
                <div class="p-2 rounded-lg bg-slate-950/60">
                    <div class="text-[10px] text-slate-500 uppercase font-bold">XPM</div>
                    <div class="text-xs font-mono font-black text-sky-400">${player.xpm || 0}</div>
                </div>
                <div class="p-2 rounded-lg bg-slate-950/60">
                    <div class="text-[10px] text-slate-500 uppercase font-bold">Gold</div>
                    <div class="text-xs font-mono font-black text-amber-400">${(player.currentGold || 0).toLocaleString()} GC</div>
                </div>
            </div>
            <div class="pt-2 border-t border-slate-800 space-y-1.5">
                <div class="text-[10px] text-slate-500 uppercase font-bold">Inventory</div>
                <div class="grid grid-cols-6 gap-1.5">
                    ${[0, 1, 2, 3, 4, 5].map(i => {
            const it = items && items[i];
            if (it) {
                const iconPath = this.getItemIconPath(it);
                return `
                                <div class="w-8 h-8 rounded-lg overflow-hidden bg-slate-950 border border-slate-700/80 p-0.5 shadow-sm hover:border-purple-400 transition-colors cursor-pointer" title="${this.getItemDisplayName(it)}">
                                    <img src="${iconPath}" onError="this.src='/images/items/default.png';" class="w-full h-full object-contain"/>
                                </div>
                            `;
            } else {
                return `
                                <div class="w-8 h-8 rounded-lg bg-slate-950/60 border border-slate-800/80 flex items-center justify-center">
                                    <span class="w-1.5 h-1.5 rounded-full bg-slate-800/60"></span>
                                </div>
                            `;
            }
        }).join('')}
                </div>
            </div>
            <div class="pt-2 border-t border-slate-800 space-y-1.5">
                <div class="text-[10px] text-slate-500 uppercase font-bold flex items-center justify-between">
                    <span>Abilities & Cooldowns</span>
                    ${player.lastCastAbility ? `<span class="text-[9px] text-purple-400 font-mono">Cast: ${player.lastCastAbility}</span>` : ''}
                </div>
                <div class="grid grid-cols-4 gap-2">
                    ${abilities && abilities.length > 0 ? abilities.map(ab => {
            const isCd = (ab.cooldownRemaining || ab.CooldownRemaining || 0) > 0;
            const cdRem = ab.cooldownRemaining || ab.CooldownRemaining || 0;
            const iconPath = ab.iconPath || ab.IconPath || `/images/abilities/default.png`;
            const slot = ab.slot || ab.Slot || 'Q';
            const name = ab.name || ab.Name || 'Ability';
            return `
                            <div class="relative group p-1 rounded-xl bg-slate-950/80 border ${isCd ? 'border-slate-800 opacity-70' : 'border-purple-500/40 shadow-sm shadow-purple-500/10'} flex flex-col items-center gap-1">
                                <div class="relative w-10 h-10 rounded-lg overflow-hidden bg-slate-900 border border-slate-700/60 flex-shrink-0">
                                    <img src="${iconPath}" onError="this.src='/images/abilities/default.png';" class="w-full h-full object-cover ${isCd ? 'grayscale' : ''}"/>
                                    ${isCd ? `
                                        <div class="absolute inset-0 bg-slate-950/80 backdrop-blur-[1px] flex items-center justify-center font-mono font-black text-xs text-amber-400">
                                            ${cdRem}s
                                        </div>
                                    ` : ''}
                                    <div class="absolute bottom-0 right-0 px-1 rounded-tl bg-slate-950/90 text-[8px] font-black text-purple-300 font-mono">
                                        ${slot}
                                    </div>
                                </div>
                                <div class="text-[9px] font-bold text-slate-300 truncate w-full text-center" title="${name}">
                                    ${name}
                                </div>
                            </div>
                        `;
        }).join('') : `
                        <div class="col-span-4 text-[10px] text-slate-500 italic text-center py-1">
                            Abilities loading...
                        </div>
                    `}
                </div>
            </div>
        `;
    }
}

window.MinimapVisualizer = MinimapVisualizer;
window.ExactPlayerColors = ExactPlayerColors;
