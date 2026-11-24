// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER K2-Calibrated 2D Match Replay & Tactical Suite Engine
 * Projects K2 CMinimap normalized coordinates (worldDim + minimappadding + inverted Y)
 * at 60 FPS with live Unit Inspector (Q/W/E/R levels, casts, clickable 6-slot items)
 * and synchronized Combat & Action Feed.
 */

const ExactPlayerColors = [
    '#0042FF', '#1CE6B9', '#9000C0', '#FFFC01', '#FE8A0E', // Legion 1..5
    '#E55BB0', '#959697', '#7EBFF1', '#106246', '#8B4513', // Hellbourne 6..10
    '#38BDF8', '#34D399', '#A855F7', '#FACC15', '#FB923C', // Extended 10v10 Legion 11..15
    '#F472B6', '#CBD5E1', '#60A5FA', '#4ADE80', '#D97706'  // Extended 10v10 Hellbourne 16..20
];

const CaldavarStructures = [
    { id: 'legion_t1_top', name: 'Legion T1 Top', type: 'Tower', team: 1, x: 0.123, y: 0.424 },
    { id: 'legion_t2_top', name: 'Legion T2 Top', type: 'Tower', team: 1, x: 0.127, y: 0.588 },
    { id: 'legion_t3_top', name: 'Legion T3 Top', type: 'Tower', team: 1, x: 0.109, y: 0.719 },
    { id: 'legion_mrax_top', name: 'Legion Melee Rax Top', type: 'MRax', team: 1, x: 0.094, y: 0.742 },
    { id: 'legion_rrax_top', name: 'Legion Range Rax Top', type: 'RRax', team: 1, x: 0.125, y: 0.742 },

    { id: 'legion_t1_mid', name: 'Legion T1 Mid', type: 'Tower', team: 1, x: 0.408, y: 0.611 },
    { id: 'legion_t2_mid', name: 'Legion T2 Mid', type: 'Tower', team: 1, x: 0.293, y: 0.695 },
    { id: 'legion_t3_mid', name: 'Legion T3 Mid', type: 'Tower', team: 1, x: 0.232, y: 0.785 },
    { id: 'legion_mrax_mid', name: 'Legion Melee Rax Mid', type: 'MRax', team: 1, x: 0.207, y: 0.787 },
    { id: 'legion_rrax_mid', name: 'Legion Range Rax Mid', type: 'RRax', team: 1, x: 0.231, y: 0.812 },

    { id: 'legion_t1_bot', name: 'Legion T1 Bot', type: 'Tower', team: 1, x: 0.799, y: 0.916 },
    { id: 'legion_t2_bot', name: 'Legion T2 Bot', type: 'Tower', team: 1, x: 0.479, y: 0.910 },
    { id: 'legion_t3_bot', name: 'Legion T3 Bot', type: 'Tower', team: 1, x: 0.309, y: 0.922 },
    { id: 'legion_mrax_bot', name: 'Legion Melee Rax Bot', type: 'MRax', team: 1, x: 0.289, y: 0.904 },
    { id: 'legion_rrax_bot', name: 'Legion Range Rax Bot', type: 'RRax', team: 1, x: 0.289, y: 0.939 },

    { id: 'legion_t4_top', name: 'Legion T4 Throne Top', type: 'Tower', team: 1, x: 0.182, y: 0.877 },
    { id: 'legion_t4_bot', name: 'Legion T4 Throne Bot', type: 'Tower', team: 1, x: 0.158, y: 0.853 },
    { id: 'legion_shrine', name: 'World Tree Base', type: 'Base', team: 1, x: 0.115, y: 0.885 },

    { id: 'hellbourne_t1_top', name: 'Hellbourne T1 Top', type: 'Tower', team: 2, x: 0.193, y: 0.139 },
    { id: 'hellbourne_t2_top', name: 'Hellbourne T2 Top', type: 'Tower', team: 2, x: 0.506, y: 0.142 },
    { id: 'hellbourne_t3_top', name: 'Hellbourne T3 Top', type: 'Tower', team: 2, x: 0.672, y: 0.156 },
    { id: 'hellbourne_mrax_top', name: 'Hellbourne Melee Rax Top', type: 'MRax', team: 2, x: 0.695, y: 0.174 },
    { id: 'hellbourne_rrax_top', name: 'Hellbourne Range Rax Top', type: 'RRax', team: 2, x: 0.699, y: 0.139 },

    { id: 'hellbourne_t1_mid', name: 'Hellbourne T1 Mid', type: 'Tower', team: 2, x: 0.545, y: 0.482 },
    { id: 'hellbourne_t2_mid', name: 'Hellbourne T2 Mid', type: 'Tower', team: 2, x: 0.656, y: 0.394 },
    { id: 'hellbourne_t3_mid', name: 'Hellbourne T3 Mid', type: 'Tower', team: 2, x: 0.734, y: 0.297 },
    { id: 'hellbourne_mrax_mid', name: 'Hellbourne Melee Rax Mid', type: 'MRax', team: 2, x: 0.766, y: 0.289 },
    { id: 'hellbourne_rrax_mid', name: 'Hellbourne Range Rax Mid', type: 'RRax', team: 2, x: 0.742, y: 0.266 },

    { id: 'hellbourne_t1_bot', name: 'Hellbourne T1 Bot', type: 'Tower', team: 2, x: 0.869, y: 0.631 },
    { id: 'hellbourne_t2_bot', name: 'Hellbourne T2 Bot', type: 'Tower', team: 2, x: 0.881, y: 0.473 },
    { id: 'hellbourne_t3_bot', name: 'Hellbourne T3 Bot', type: 'Tower', team: 2, x: 0.875, y: 0.348 },
    { id: 'hellbourne_mrax_bot', name: 'Hellbourne Melee Rax Bot', type: 'MRax', team: 2, x: 0.891, y: 0.326 },
    { id: 'hellbourne_rrax_bot', name: 'Hellbourne Range Rax Bot', type: 'RRax', team: 2, x: 0.859, y: 0.320 },

    { id: 'hellbourne_t4_top', name: 'Hellbourne T4 Throne Top', type: 'Tower', team: 2, x: 0.805, y: 0.244 },
    { id: 'hellbourne_t4_bot', name: 'Hellbourne T4 Throne Bot', type: 'Tower', team: 2, x: 0.783, y: 0.219 },
    { id: 'hellbourne_shrine', name: 'Sacrificial Shrine Base', type: 'Base', team: 2, x: 0.885, y: 0.115 }
];

// Calibrated against K2 midwars worldconfig (worldDim=8192, padding L=1100, R=1800, T=1500, B=900)
const MidWarsStructures = [
    { id: 'legion_shrine', name: 'Legion World Tree', type: 'Base', team: 1, x: 0.135, y: 0.755 },
    { id: 'legion_t2_mid', name: 'Legion Base Tower', type: 'Tower', team: 1, x: 0.225, y: 0.665 },
    { id: 'legion_t1_mid', name: 'Legion River Tower', type: 'Tower', team: 1, x: 0.365, y: 0.535 },
    { id: 'hellbourne_t1_mid', name: 'Hellbourne River Tower', type: 'Tower', team: 2, x: 0.525, y: 0.385 },
    { id: 'hellbourne_t2_mid', name: 'Hellbourne Base Tower', type: 'Tower', team: 2, x: 0.645, y: 0.245 },
    { id: 'hellbourne_shrine', name: 'Hellbourne Shrine', type: 'Base', team: 2, x: 0.735, y: 0.125 }
];

const GrimmsStructures = [
    { id: 'legion_shrine', name: 'Legion Base', type: 'Base', team: 1, x: 0.225, y: 0.755 },
    { id: 'legion_t1_top', name: 'Legion North Tower', type: 'Tower', team: 1, x: 0.310, y: 0.360 },
    { id: 'legion_t1_bot', name: 'Legion South Tower', type: 'Tower', team: 1, x: 0.640, y: 0.690 },
    { id: 'hellbourne_t1_top', name: 'Hellbourne North Tower', type: 'Tower', team: 2, x: 0.360, y: 0.310 },
    { id: 'hellbourne_t1_bot', name: 'Hellbourne South Tower', type: 'Tower', team: 2, x: 0.690, y: 0.640 },
    { id: 'hellbourne_shrine', name: 'Hellbourne Base', type: 'Base', team: 2, x: 0.770, y: 0.235 }
];

const DevoWarsStructures = [
    { id: 'legion_shrine', name: 'Legion Shop & Spawn', type: 'Base', team: 1, x: 0.230, y: 0.715 },
    { id: 'hellbourne_shrine', name: 'Hellbourne Shop & Spawn', type: 'Base', team: 2, x: 0.710, y: 0.240 }
];

const TrueDevoStructures = [
    { id: 'legion_shrine', name: 'Legion Spawn Bank', type: 'Base', team: 1, x: 0.085, y: 0.510 },
    { id: 'hellbourne_shrine', name: 'Hellbourne Spawn Bank', type: 'Base', team: 2, x: 0.915, y: 0.510 }
];

class MatchReplayPlayer {
    constructor(canvasId, options = {}) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) return;

        this.ctx = this.canvas.getContext('2d');
        this.matchId = options.matchId;
        this.map = options.map || 'caldavar';
        this.timelineData = options.preloadedData || null;

        // Playback State
        this.currentSecond = 0;
        this.totalSeconds = 300;
        this.playbackSpeed = 1.0;
        this.isPlaying = false;
        this.lastFrameTimestamp = 0;
        this._rafId = null;
        this._canvasRect = null;
        this._lastInspectorTick = -1;
        this._lastFeedTick = -1;

        // Camera & Selection State
        this.cameraMode = 'all'; // 'all' | 'director' | 'player'
        this.selectedAccountId = null;
        this.hoveredPlayer = null;
        this.feedFilter = 'all';
        this.highlightedPin = null;
        this.highlightedPinUntil = 0;

        // Visual Assets
        this.mapImage = new Image();
        this.heroImages = new Map();

        this.init();
    }

    async init() {
        this.mapImage.src = this.getMinimapPath(this.map);

        this.initControls();
        this.initEvents();
        this.setupKeyboardShortcuts();

        if (this.timelineData) {
            this.applyTimelineData(this.timelineData);
        } else if (this.matchId) {
            await this.loadTelemetry();
        }

        if ('ResizeObserver' in window && this.canvas) {
            this._resizeObserver = new ResizeObserver(() => { this._canvasRect = null; });
            this._resizeObserver.observe(this.canvas);
        }
        window.addEventListener('scroll', () => { this._canvasRect = null; }, { passive: true });
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden && !this._rafId) {
                this.lastFrameTimestamp = 0;
                this._rafId = requestAnimationFrame((ts) => this.renderLoop(ts));
            }
        });

        this._rafId = requestAnimationFrame((ts) => this.renderLoop(ts));
    }

    getMinimapPath(mapName) {
        if (!mapName) return '/images/maps/minimap_caldavar.png';
        const normalizedSlug = String(mapName).toLowerCase()
            .replace(/^\/?images\/maps\//, '')
            .replace(/^maps\//, '')
            .replace(/\.(s2z|png|tga|dds|webp|svg)$/i, '')
            .replace(/^minimap[\s_-]+/, '')
            .replace(/[^a-z0-9]+/g, '_')
            .replace(/^_+|_+$/g, '');

        const exactCustomSlugs = new Set([
            'archers', 'arena', 'caldavar', 'caldavar_halloween', 'caldavar_old', 'caldavar_reborn',
            'caldavar_wtf', 'capturetheflag', 'colosseum_duel', 'darkwoodvale', 'devowars',
            'devowars_10v10', 'empath_maze', 'enfos', 'grimmscrossing', 'mids_omg', 'midwars',
            'midwars_10v10', 'midwars_halloween', 'midwars_reborn', 'nightmarish_coop',
            'pebbles_duel', 'persian_pass', 'prophets', 'riftwars', 'soccer', 'spotlight',
            'strikers', 'team_deathmatch', 'thegrimmhunt', 'three_kingdoms', 'truedevo',
            'truedevo_10v10', 'tutorial', 'valkwars', 'watchtower'
        ]);
        const mapSlugAliases = {
            'forests_of_caldavar': 'caldavar', 'forestsofcaldavar': 'caldavar', 'caldavar_1v1': 'caldavar',
            'caldavar_ranked': 'caldavar', 'caldavar_public': 'caldavar', 'caldavar_casual': 'caldavar',
            'quickmatch': 'caldavar', 'forests_of_caldavar_old': 'caldavar_old',
            'forests_of_caldavar_reborn': 'caldavar_reborn', 'mid_wars': 'midwars', 'mid_wars_old': 'midwars',
            'midwars_ranked': 'midwars', 'midwars_public': 'midwars', 'mid_wars_reborn': 'midwars_reborn',
            'mid_wars_10v10': 'midwars_10v10', 'mid_wars_halloween': 'midwars_halloween',
            'devo_wars': 'devowars', 'devo_wars_10v10': 'devowars_10v10', 'true_devo': 'truedevo',
            'true_devo_10v10': 'truedevo_10v10', 'rift_wars': 'riftwars', 'riftwars_ranked': 'riftwars',
            'grimms_crossing': 'grimmscrossing', 'grimmscross': 'grimmscrossing',
            'darkwood_vale': 'darkwoodvale', 'darkwood': 'darkwoodvale',
            'capture_the_flag': 'capturetheflag', 'ctf': 'capturetheflag',
            'team_death_match': 'team_deathmatch', 'teamdeathmatch': 'team_deathmatch', 'tdm': 'team_deathmatch',
            'the_grimm_hunt': 'thegrimmhunt', 'grimmhunt': 'thegrimmhunt', 'hon_strikers': 'strikers',
            'striker': 'strikers', 'prophet': 'prophets', 'archer_wars': 'archers', 'archerwars': 'archers',
            'valk_wars': 'valkwars', 'threekingdoms': 'three_kingdoms', 'persianpass': 'persian_pass',
            'pebblesduel': 'pebbles_duel', 'colosseumduel': 'colosseum_duel', 'empathmaze': 'empath_maze',
            'nightmarishcoop': 'nightmarish_coop'
        };
        const rev = '?v=k2crop2';
        const mappedSlug = mapSlugAliases[normalizedSlug] || normalizedSlug;
        if (exactCustomSlugs.has(mappedSlug)) return `/images/maps/minimap_${mappedSlug}.png${rev}`;
        if (mappedSlug.includes('midwars')) return `/images/maps/minimap_midwars.png${rev}`;
        if (mappedSlug.includes('caldavar')) return `/images/maps/minimap_caldavar.png${rev}`;
        if (mappedSlug.includes('devo')) return `/images/maps/minimap_devowars.png${rev}`;
        if (mappedSlug.includes('grimm')) return `/images/maps/minimap_grimmscrossing.png${rev}`;
        return `/images/maps/minimap_caldavar.png${rev}`;
    }

    getHeroIconPath(heroId) {
        if (!heroId) return '/images/heroes/default.png';
        let clean = String(heroId).toLowerCase()
            .replace(/^\/?images\/heroes\//, '')
            .replace(/\.(png|tga|dds|webp|svg)$/i, '')
            .replace(/^icon_/, '')
            .replace(/^hero_/, '')
            .split('.')[0]
            .replace(/[^a-z0-9]/g, '');

        const aliasMap = {
            'flameguard': 'pyromancer',
            'hiro': 'swiftblade',
            'frosty': 'glacius',
            'hunter': 'bloodhunter',
            'krixi': 'valkyrie',
            'shaman': 'dementedshaman',
            'flint': 'flintbeastwood',
            'rocky': 'pebbles',
            'yogi': 'wildsoul',
            'keeper': 'keeperoftheforest',
            'babayaga': 'wretchedhag',
            'baba_yaga': 'wretchedhag',
            'wretched_hag': 'wretchedhag',
            'scar': 'madman',
            'ebulus': 'slither',
            'javaras': 'magebane',
            'arachna': 'arachna',
            'treant': 'keeperoftheforest'
        };

        if (aliasMap[clean]) clean = aliasMap[clean];
        return `/images/heroes/${clean}.png`;
    }

    getItemIconPath(item) {
        if (!item) return '/images/items/default.png';
        let clean = String(item).toLowerCase().replace(/^item_/, '').replace(/_/g, '').replace(/\s+/g, '').trim();

        const aliasMap = {
            'staffmaster': 'staffofthemaster',
            'mystic': 'mysticvestments',
            'pk': 'portalkey',
            'silence': 'hellflower',
            'chalice': 'bloodchalice',
            'bracer': 'fortifiedbracer',
            'firebrand': 'lightbrand',
            'barbedarmor': 'item_shield2',
            'runes': 'runesoftheblight',
            'blight': 'runesoftheblight',
            'potion': 'healthpotion',
            'codex': 'nuke',
            'astrolabe': 'astrolabe',
            'homecomingstone': 'homecomingstone'
        };

        if (aliasMap[clean]) clean = aliasMap[clean];
        return `/images/items/${clean}.png`;
    }

    async loadTelemetry() {
        try {
            let res = await fetch(`/api/matches/${this.matchId}/replay`);
            if (!res.ok) {
                res = await fetch(`/api/replays/${this.matchId}/telemetry`);
            }
            if (!res.ok) throw new Error(`Replay telemetry returned HTTP ${res.status}`);
            const data = await res.json();
            this.applyTimelineData(data);
        } catch (e) {
            console.warn('[ReplayPlayer] Failed to load replay telemetry:', e);
            const feed = document.getElementById('replay-action-feed');
            if (feed) {
                feed.innerHTML = '<div class="text-center py-4 text-rose-400 text-xs">Telemetry unavailable for this match.</div>';
            }
        }
    }

    applyTimelineData(data) {
        this.timelineData = data;
        this.totalSeconds = Math.max(1, data.totalDurationSeconds || 300);

        if (data.map) {
            this.map = data.map;
            const resolvedMapSrc = this.getMinimapPath(data.map);
            if (!this.mapImage.src.endsWith(resolvedMapSrc)) {
                this.mapImage.src = resolvedMapSrc;
            }
        }

        // Update Telemetry Fidelity Badge
        const badge = document.getElementById('replay-telemetry-badge');
        const badgeText = document.getElementById('replay-telemetry-badge-text');
        if (badge && badgeText) {
            const isVerified = data.telemetrySource === 'server_log' || data.telemetrySource === 'honreplay';
            badgeText.textContent = data.telemetryLabel || (isVerified ? 'Verified Server Telemetry' : 'Tactical Reconstruction');
            badge.className = `text-[10px] font-mono font-bold px-2.5 py-0.5 rounded-md border inline-flex items-center gap-1.5 ${
                isVerified
                    ? 'bg-emerald-500/15 text-emerald-300 border-emerald-500/35'
                    : 'bg-amber-500/15 text-amber-300 border-amber-500/35'
            }`;
        }

        // Preload hero portraits
        if (Array.isArray(data.players)) {
            for (const p of data.players) {
                if (!this.heroImages.has(p.heroIdentifier)) {
                    const img = new Image();
                    img.src = this.getHeroIconPath(p.heroIdentifier);
                    img.onerror = () => { img.src = '/images/heroes/default.png'; };
                    this.heroImages.set(p.heroIdentifier, img);
                }
            }
        }

        this.renderEventPins();
        this.updateRosterUI();

        if (this.selectedAccountId === null && Array.isArray(data.players) && data.players.length > 0) {
            this.selectedAccountId = data.players[0].accountID;
        }

        this.updateScrubberUI();
        this.updateHUD();
        this.updateInspector(true);
        this.updateActionFeed(true);
    }

    initControls() {
        const playBtn = document.getElementById('replay-play-btn');
        if (playBtn) {
            playBtn.addEventListener('click', () => {
                if (this.currentSecond >= this.totalSeconds) {
                    this.currentSecond = 0;
                }
                this.togglePlay();
            });
        }

        const scrubber = document.getElementById('replay-scrubber');
        if (scrubber) {
            scrubber.addEventListener('input', (e) => {
                this.seekTo(parseFloat(e.target.value));
            });
        }

        document.querySelectorAll('.replay-speed-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                const speed = parseFloat(e.currentTarget.dataset.speed);
                this.setSpeed(speed);
            });
        });

        const povSelect = document.getElementById('replay-pov-select');
        if (povSelect) {
            povSelect.addEventListener('change', (e) => {
                const val = e.target.value;
                if (val === 'all') {
                    this.cameraMode = 'all';
                } else if (val === 'director') {
                    this.cameraMode = 'director';
                } else {
                    this.cameraMode = 'player';
                    this.selectedAccountId = parseInt(val, 10);
                }
                this.updateInspector(true);
            });
        }

        document.querySelectorAll('.replay-feed-filter').forEach(btn => {
            btn.addEventListener('click', (e) => {
                this.feedFilter = e.currentTarget.dataset.feedFilter || 'all';
                document.querySelectorAll('.replay-feed-filter').forEach(b => {
                    const active = b.dataset.feedFilter === this.feedFilter;
                    b.className = `replay-feed-filter px-2 py-0.5 rounded-md ${
                        active ? 'bg-purple-600 text-white' : 'bg-slate-800 text-slate-400 hover:text-white'
                    }`;
                });
                this.updateActionFeed(true);
            });
        });
    }

    setupKeyboardShortcuts() {
        window.addEventListener('keydown', (e) => {
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA') return;
            if (e.code === 'Space') {
                e.preventDefault();
                if (this.currentSecond >= this.totalSeconds) this.currentSecond = 0;
                this.togglePlay();
            } else if (e.code === 'ArrowRight') {
                e.preventDefault();
                this.seekTo(Math.min(this.totalSeconds, this.currentSecond + 10));
            } else if (e.code === 'ArrowLeft') {
                e.preventDefault();
                this.seekTo(Math.max(0, this.currentSecond - 10));
            }
        });
    }

    initEvents() {
        this.canvas.addEventListener('mousemove', (e) => {
            const rect = this._canvasRect || (this._canvasRect = this.canvas.getBoundingClientRect());
            if (!rect.width || !rect.height) return;
            const mouseX = (e.clientX - rect.left) / rect.width;
            const mouseY = (e.clientY - rect.top) / rect.height;

            const currentPlayers = this.getCurrentPlayerSnapshots();
            let found = null;
            let bestDist = 0.05;

            for (const p of currentPlayers) {
                const dx = mouseX - p.positionX;
                const dy = mouseY - p.positionY;
                const dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < bestDist) {
                    bestDist = dist;
                    found = p;
                }
            }

            this.hoveredPlayer = found;
            this.canvas.style.cursor = found ? 'pointer' : 'crosshair';
            this.updateTooltip(e, found);
        });

        this.canvas.addEventListener('mouseleave', () => {
            this.hoveredPlayer = null;
            const tooltip = document.getElementById('replay-tooltip');
            if (tooltip) tooltip.classList.add('hidden');
        });

        this.canvas.addEventListener('click', () => {
            if (this.hoveredPlayer) {
                this.selectedAccountId = this.hoveredPlayer.accountID;
                this.cameraMode = 'player';
                const povSelect = document.getElementById('replay-pov-select');
                if (povSelect) povSelect.value = String(this.selectedAccountId);
                this.updateInspector(true);
            }
        });
    }

    togglePlay() {
        this.isPlaying = !this.isPlaying;
        const playBtn = document.getElementById('replay-play-btn');
        if (playBtn) {
            playBtn.innerHTML = this.isPlaying ? '⏸️' : '▶️';
        }
    }

    seekTo(seconds, highlightEvent = null) {
        this.currentSecond = Math.max(0, Math.min(seconds, this.totalSeconds));
        if (highlightEvent) {
            this.highlightedPin = highlightEvent;
            this.highlightedPinUntil = performance.now() + 2600;
        }
        this.updateScrubberUI();
        this.updateHUD();
        this.updateInspector(true);
        this.updateActionFeed(true);
    }

    setSpeed(speed) {
        this.playbackSpeed = speed;
        document.querySelectorAll('.replay-speed-btn').forEach(btn => {
            if (parseFloat(btn.dataset.speed) === speed) {
                btn.classList.add('bg-purple-600', 'text-white', 'font-black');
                btn.classList.remove('bg-slate-800', 'text-slate-400');
            } else {
                btn.classList.remove('bg-purple-600', 'text-white', 'font-black');
                btn.classList.add('bg-slate-800', 'text-slate-400');
            }
        });
    }

    renderLoop(timestamp) {
        if (document.hidden) {
            this._rafId = null;
            this.lastFrameTimestamp = 0;
            return;
        }
        if (!this.lastFrameTimestamp) this.lastFrameTimestamp = timestamp;
        const deltaSeconds = Math.min(0.1, (timestamp - this.lastFrameTimestamp) / 1000);
        this.lastFrameTimestamp = timestamp;

        if (this.isPlaying) {
            this.currentSecond += deltaSeconds * this.playbackSpeed;
            if (this.currentSecond >= this.totalSeconds) {
                this.currentSecond = this.totalSeconds;
                this.isPlaying = false;
                const playBtn = document.getElementById('replay-play-btn');
                if (playBtn) playBtn.innerHTML = '🔄';
            }

            if (this.cameraMode === 'director' && this.timelineData?.eventPins) {
                const nearbyCombat = this.timelineData.eventPins.find(pin =>
                    Math.abs(pin.matchTimeSeconds - this.currentSecond) < 2.2 &&
                    pin.attackerHero &&
                    (pin.eventType === 'KILL' || pin.eventType === 'FIRST_BLOOD' || pin.eventType === 'MULTIKILL')
                );
                if (nearbyCombat && this.timelineData.players) {
                    const actor = this.timelineData.players.find(pl =>
                        String(pl.heroIdentifier).toLowerCase() === String(nearbyCombat.attackerHero).toLowerCase()
                    );
                    if (actor && actor.accountID !== this.selectedAccountId) {
                        this.selectedAccountId = actor.accountID;
                    }
                }
            }

            this.updateScrubberUI();
            this.updateHUD();
            this.updateInspector(false);
            this.updateActionFeed(false);
        }

        this.draw(timestamp);
        this._rafId = requestAnimationFrame((ts) => this.renderLoop(ts));
    }

    draw(nowTs = performance.now()) {
        const width = this.canvas.width;
        const height = this.canvas.height;
        const ctx = this.ctx;

        ctx.clearRect(0, 0, width, height);

        // 1. Draw Calibrated Minimap Background
        if (this.mapImage.complete && this.mapImage.naturalWidth > 0) {
            ctx.drawImage(this.mapImage, 0, 0, width, height);
            ctx.fillStyle = 'rgba(9, 13, 22, 0.18)';
            ctx.fillRect(0, 0, width, height);
        } else {
            ctx.fillStyle = '#090d16';
            ctx.fillRect(0, 0, width, height);
        }

        const { players: currentPlayers, structures: currentStructures } = this.getCurrentFrameState();

        // 2. Draw Map-Specific Structures (with live intact/destroyed state)
        this.drawStructures(ctx, width, height, currentStructures);

        const selectedPlayer = currentPlayers.find(p => p.accountID === this.selectedAccountId);

        // 3. Draw POV Vision Cone when locked onto a player or Action Director
        if (selectedPlayer && this.cameraMode !== 'all') {
            const spx = selectedPlayer.positionX * width;
            const spy = selectedPlayer.positionY * height;

            ctx.save();
            const coneAngle = selectedPlayer.headingAngle !== undefined
                ? selectedPlayer.headingAngle
                : (selectedPlayer.team === 1 ? -Math.PI * 0.25 : Math.PI * 0.75);
            const coneLength = width * 0.30;
            const fov = Math.PI * 0.44;

            const gradient = ctx.createRadialGradient(spx, spy, 8, spx, spy, coneLength);
            gradient.addColorStop(0, selectedPlayer.team === 1 ? 'rgba(56, 189, 248, 0.32)' : 'rgba(251, 113, 133, 0.32)');
            gradient.addColorStop(0.7, selectedPlayer.team === 1 ? 'rgba(56, 189, 248, 0.09)' : 'rgba(251, 113, 133, 0.09)');
            gradient.addColorStop(1, 'rgba(0, 0, 0, 0)');

            ctx.beginPath();
            ctx.moveTo(spx, spy);
            ctx.arc(spx, spy, coneLength, coneAngle - fov / 2, coneAngle + fov / 2);
            ctx.closePath();
            ctx.fillStyle = gradient;
            ctx.fill();
            ctx.restore();
        }

        // 4. Draw Combat Skirmish Tethers between adjacent enemy heroes
        for (let i = 0; i < currentPlayers.length; i++) {
            for (let j = i + 1; j < currentPlayers.length; j++) {
                const pA = currentPlayers[i];
                const pB = currentPlayers[j];
                if (pA.team !== pB.team && pA.healthPercent > 0 && pB.healthPercent > 0) {
                    const dx = (pA.positionX - pB.positionX) * width;
                    const dy = (pA.positionY - pB.positionY) * height;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist < width * 0.12) {
                        ctx.save();
                        ctx.beginPath();
                        ctx.setLineDash([4, 4]);
                        ctx.moveTo(pA.positionX * width, pA.positionY * height);
                        ctx.lineTo(pB.positionX * width, pB.positionY * height);
                        ctx.strokeStyle = 'rgba(244, 63, 94, 0.45)';
                        ctx.lineWidth = 1.4;
                        ctx.stroke();
                        ctx.restore();
                    }
                }
            }
        }

        // 5. Draw Subtle On-Map Event Ripples & Callouts
        this.drawMapEventCallouts(ctx, width, height, nowTs);

        // 6. Draw Heroes (scale icon radius gracefully for 10v10)
        const baseRadius = currentPlayers.length > 12 ? width * 0.020 : width * 0.024;
        let team1Slot = 0;
        let team2Slot = 5;

        for (let pIdx = 0; pIdx < currentPlayers.length; pIdx++) {
            const p = currentPlayers[pIdx];
            const px = p.positionX * width;
            const py = p.positionY * height;
            const radius = baseRadius;
            const isDead = p.healthPercent <= 0 || p.isAlive === false;

            const colorIdx = p.team === 1
                ? (team1Slot < 5 ? team1Slot++ : 10 + ((team1Slot++ - 5) % 5))
                : (team2Slot < 10 ? team2Slot++ : 15 + ((team2Slot++ - 10) % 5));
            const slotColor = ExactPlayerColors[colorIdx % ExactPlayerColors.length];

            const isSelected = this.selectedAccountId === p.accountID;
            const isHovered = this.hoveredPlayer?.accountID === p.accountID;

            if (isSelected || isHovered) {
                ctx.beginPath();
                ctx.arc(px, py, radius + 5.5, 0, Math.PI * 2);
                ctx.fillStyle = isSelected ? 'rgba(168, 85, 247, 0.42)' : 'rgba(250, 204, 21, 0.35)';
                ctx.fill();
                ctx.lineWidth = 2.2;
                ctx.strokeStyle = isSelected ? '#c084fc' : '#facc15';
                ctx.stroke();
            }

            // Hero Portrait Circle
            ctx.save();
            if (isDead) ctx.globalAlpha = 0.40;
            ctx.beginPath();
            ctx.arc(px, py, radius, 0, Math.PI * 2);
            ctx.clip();

            let heroImg = this.heroImages.get(p.heroIdentifier);
            if (!heroImg && p.heroIdentifier) {
                heroImg = new Image();
                heroImg.src = this.getHeroIconPath(p.heroIdentifier);
                heroImg.onerror = () => { heroImg.src = '/images/heroes/default.png'; };
                this.heroImages.set(p.heroIdentifier, heroImg);
            }

            if (heroImg && heroImg.complete && heroImg.naturalWidth > 0) {
                ctx.drawImage(heroImg, px - radius, py - radius, radius * 2, radius * 2);
            } else {
                ctx.fillStyle = p.team === 1 ? '#0284c7' : '#e11d48';
                ctx.fillRect(px - radius, py - radius, radius * 2, radius * 2);
            }
            ctx.restore();

            // Outer K2 Slot Color Ring + Inner Team Ring
            ctx.beginPath();
            ctx.arc(px, py, radius, 0, Math.PI * 2);
            ctx.lineWidth = 2.6;
            ctx.strokeStyle = isDead ? '#64748b' : slotColor;
            ctx.stroke();

            ctx.beginPath();
            ctx.arc(px, py, radius - 2, 0, Math.PI * 2);
            ctx.lineWidth = 1.4;
            ctx.strokeStyle = isDead ? '#475569' : (p.team === 1 ? '#22c55e' : '#ef4444');
            ctx.stroke();

            // Heading Pointer
            if (!isDead && p.headingAngle !== undefined) {
                const hx = px + Math.cos(p.headingAngle) * (radius + 2);
                const hy = py + Math.sin(p.headingAngle) * (radius + 2);
                ctx.beginPath();
                ctx.arc(hx, hy, 2.3, 0, Math.PI * 2);
                ctx.fillStyle = p.team === 1 ? '#38bdf8' : '#fb7185';
                ctx.fill();
            }

            // Subtle Spell Cast Tag
            if (!isDead && p.lastCastAbility) {
                ctx.save();
                ctx.font = 'bold 9px sans-serif';
                ctx.fillStyle = '#fef08a';
                ctx.textAlign = 'center';
                ctx.shadowColor = '#000000';
                ctx.shadowBlur = 4;
                ctx.fillText(`⚡ ${p.lastCastAbility}`, px, py - radius - 11);
                ctx.restore();
            } else if (p.isTeleporting) {
                const pulse = (nowTs % 900) / 900;
                ctx.save();
                ctx.beginPath();
                ctx.arc(px, py, radius + 2 + pulse * 12, 0, Math.PI * 2);
                ctx.strokeStyle = `rgba(56, 189, 248, ${1 - pulse})`;
                ctx.lineWidth = 2;
                ctx.stroke();
                ctx.restore();
            }

            // Health Bar Overhead
            if (isDead) {
                ctx.font = '11px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('💀', px, py - radius - 3);
            } else {
                const barW = radius * 2.1;
                const barH = 3.2;
                const barX = px - barW / 2;
                const barY = py - radius - 6.5;

                ctx.fillStyle = 'rgba(15, 23, 42, 0.88)';
                ctx.fillRect(barX - 1, barY - 1, barW + 2, barH + 2);
                ctx.fillStyle = p.team === 1 ? '#22c55e' : '#f43f5e';
                ctx.fillRect(barX, barY, Math.max(0, Math.min(1, p.healthPercent / 100)) * barW, barH);
            }

            // Level Pip
            ctx.beginPath();
            ctx.arc(px + radius - 1.5, py + radius - 1.5, 6, 0, Math.PI * 2);
            ctx.fillStyle = '#0f172a';
            ctx.fill();
            ctx.strokeStyle = '#f59e0b';
            ctx.lineWidth = 1;
            ctx.stroke();
            ctx.fillStyle = '#fbbf24';
            ctx.font = 'bold 7.5px monospace';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(String(p.level || 1), px + radius - 1.5, py + radius - 1.5);

            // Player Label
            ctx.fillStyle = '#f8fafc';
            ctx.font = 'bold 8.5px sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'alphabetic';
            ctx.shadowColor = '#000000';
            ctx.shadowBlur = 4;
            ctx.fillText(p.accountName || 'Hero', px, py + radius + 10);
            ctx.shadowBlur = 0;
        }

        // 7. Top-Left Active Camera Pill
        if (selectedPlayer && this.cameraMode !== 'all') {
            ctx.save();
            ctx.fillStyle = 'rgba(15, 23, 42, 0.86)';
            ctx.strokeStyle = 'rgba(168, 85, 247, 0.55)';
            ctx.lineWidth = 1;
            const bannerText = `${this.cameraMode === 'director' ? 'DIRECTOR' : 'POV'}: ${selectedPlayer.accountName} (${selectedPlayer.heroDisplayName})`;
            ctx.font = 'bold 10.5px sans-serif';
            const bw = Math.min(width - 24, Math.max(190, ctx.measureText(bannerText).width + 38));
            ctx.beginPath();
            ctx.roundRect(10, 10, bw, 24, [8]);
            ctx.fill();
            ctx.stroke();

            const pulse = (Math.sin(nowTs * 0.006) + 1) * 0.5;
            ctx.fillStyle = `rgba(239, 68, 68, ${0.4 + pulse * 0.6})`;
            ctx.beginPath();
            ctx.arc(21, 22, 3.8, 0, Math.PI * 2);
            ctx.fill();

            ctx.fillStyle = '#f1f5f9';
            ctx.textAlign = 'left';
            ctx.fillText(bannerText, 30, 26);
            ctx.restore();
        }
    }

    drawMapEventCallouts(ctx, width, height, nowTs) {
        if (!this.timelineData?.eventPins) return;

        // Draw clicked feed highlight beacon if active
        if (this.highlightedPin && nowTs < this.highlightedPinUntil) {
            const hx = (this.highlightedPin.positionX ?? 0.5) * width;
            const hy = (this.highlightedPin.positionY ?? 0.5) * height;
            const prog = 1 - (this.highlightedPinUntil - nowTs) / 2600;
            ctx.save();
            ctx.beginPath();
            ctx.arc(hx, hy, 12 + prog * 26, 0, Math.PI * 2);
            ctx.strokeStyle = `rgba(34, 211, 238, ${Math.max(0.15, 1 - prog)})`;
            ctx.lineWidth = 2.5;
            ctx.stroke();
            ctx.restore();
        }

        const recentPins = this.timelineData.eventPins.filter(pin => {
            const dt = this.currentSecond - pin.matchTimeSeconds;
            return dt >= -0.4 && dt <= 3.0 &&
                pin.eventType !== 'ABILITY'; // keep frequent ability casts subtle on hero overhead
        }).slice(-3);

        for (const pin of recentPins) {
            const age = Math.max(0, (this.currentSecond - pin.matchTimeSeconds) / 3.0);
            const alpha = Math.max(0, 1 - age);
            const ex = (pin.positionX ?? 0.5) * width;
            const ey = (pin.positionY ?? 0.5) * height;

            let ringColor = '250, 204, 21';
            let badgeIcon = '⚔️';
            if (pin.eventType === 'KILL' || pin.eventType === 'FIRST_BLOOD' || pin.eventType === 'MULTIKILL') {
                ringColor = pin.team === 1 ? '56, 189, 248' : '244, 63, 94';
                badgeIcon = pin.eventType === 'FIRST_BLOOD' ? '🩸' : '⚔️';
            } else if (pin.eventType === 'TOWER' || pin.eventType === 'VICTORY') {
                ringColor = '245, 158, 11';
                badgeIcon = pin.eventType === 'VICTORY' ? '👑' : '🏰';
            } else if (pin.eventType === 'ITEM' || pin.eventType === 'ITEM_ACTIVATE') {
                ringColor = '168, 85, 247';
                badgeIcon = '🛒';
            } else if (pin.eventType === 'RUNE') {
                ringColor = '52, 211, 153';
                badgeIcon = '🔮';
            }

            ctx.save();
            ctx.beginPath();
            ctx.arc(ex, ey, 8 + age * 22, 0, Math.PI * 2);
            ctx.strokeStyle = `rgba(${ringColor}, ${alpha * 0.75})`;
            ctx.lineWidth = 2;
            ctx.stroke();

            if (pin.eventType === 'KILL' || pin.eventType === 'FIRST_BLOOD' || pin.eventType === 'MULTIKILL' || pin.eventType === 'TOWER' || pin.eventType === 'VICTORY') {
                ctx.font = 'bold 9.5px sans-serif';
                const shortLabel = `${badgeIcon} ${String(pin.description || '').slice(0, 44)}`;
                const tw = Math.min(width - 20, ctx.measureText(shortLabel).width + 14);
                const bx = Math.max(8, Math.min(width - tw - 8, ex - tw / 2));
                const by = Math.max(38, ey - 22 - age * 8);

                ctx.fillStyle = `rgba(15, 23, 42, ${alpha * 0.88})`;
                ctx.strokeStyle = `rgba(${ringColor}, ${alpha * 0.75})`;
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.roundRect(bx, by - 12, tw, 17, [6]);
                ctx.fill();
                ctx.stroke();

                ctx.fillStyle = `rgba(254, 249, 195, ${alpha})`;
                ctx.textAlign = 'center';
                ctx.fillText(shortLabel, bx + tw / 2, by);
            }
            ctx.restore();
        }
    }

    drawStructures(ctx, width, height, structureStates = {}) {
        const cleanMap = String(this.map || '').toLowerCase();
        let structureList = [];
        if (cleanMap.includes('caldavar')) {
            structureList = CaldavarStructures;
        } else if (cleanMap.includes('midwars')) {
            structureList = MidWarsStructures;
        } else if (cleanMap.includes('grimm') || cleanMap.includes('riftwars')) {
            structureList = GrimmsStructures;
        } else if (cleanMap.includes('truedevo')) {
            structureList = TrueDevoStructures;
        } else if (cleanMap.includes('devo')) {
            structureList = DevoWarsStructures;
        } else {
            structureList = [
                { id: 'legion_shrine', name: 'Legion Base', type: 'Base', team: 1, x: 0.16, y: 0.82 },
                { id: 'hellbourne_shrine', name: 'Hellbourne Base', type: 'Base', team: 2, x: 0.84, y: 0.18 }
            ];
        }

        for (const st of structureList) {
            const isAlive = structureStates[st.id] !== false;
            const sx = st.x * width;
            const sy = st.y * height;
            const teamColor = !isAlive ? '#334155' : (st.team === 1 ? '#22c55e' : '#ef4444');

            ctx.save();
            if (!isAlive) ctx.globalAlpha = 0.45;

            if (st.type === 'Tower') {
                ctx.beginPath();
                ctx.rect(sx - 4, sy - 4, 8, 8);
                ctx.fillStyle = teamColor;
                ctx.fill();
                ctx.strokeStyle = isAlive ? '#ffffff' : '#64748b';
                ctx.lineWidth = 1.1;
                ctx.stroke();
            } else if (st.type === 'MRax' || st.type === 'RRax') {
                ctx.beginPath();
                ctx.moveTo(sx, sy - 3.5);
                ctx.lineTo(sx + 3.5, sy);
                ctx.lineTo(sx, sy + 3.5);
                ctx.lineTo(sx - 3.5, sy);
                ctx.closePath();
                ctx.fillStyle = teamColor;
                ctx.fill();
                ctx.strokeStyle = st.type === 'MRax' ? '#facc15' : '#38bdf8';
                ctx.lineWidth = 0.9;
                ctx.stroke();
            } else if (st.type === 'Base') {
                ctx.beginPath();
                ctx.arc(sx, sy, 8.5, 0, Math.PI * 2);
                ctx.fillStyle = !isAlive ? '#334155' : (st.team === 1 ? '#0284c7' : '#e11d48');
                ctx.fill();
                ctx.strokeStyle = '#facc15';
                ctx.lineWidth = 1.8;
                ctx.stroke();
            }
            ctx.restore();
        }
    }

    getCurrentFrameState() {
        if (!this.timelineData?.keyframes || this.timelineData.keyframes.length === 0) {
            return { players: [], structures: {} };
        }

        const frames = this.timelineData.keyframes;
        const targetSec = this.currentSecond;

        let prevFrame = frames[0];
        let nextFrame = frames[frames.length - 1];

        for (let i = 0; i < frames.length; i++) {
            if (frames[i].second <= targetSec) prevFrame = frames[i];
            if (frames[i].second >= targetSec) {
                nextFrame = frames[i];
                break;
            }
        }

        const frameSpan = nextFrame.second - prevFrame.second;
        const alpha = frameSpan > 0 ? (targetSec - prevFrame.second) / frameSpan : 0;

        const results = [];
        const prevPlayers = prevFrame.players || [];
        const nextPlayers = nextFrame.players || [];

        for (let i = 0; i < prevPlayers.length; i++) {
            const p1 = prevPlayers[i];
            const p2 = nextPlayers.find(p => p.accountID === p1.accountID && p.accountName === p1.accountName)
                || nextPlayers.find(p => p.accountID === p1.accountID)
                || p1;

            const dsq = Math.pow(p2.positionX - p1.positionX, 2) + Math.pow(p2.positionY - p1.positionY, 2);
            const jumped = dsq > 0.05;

            let h1 = p1.headingAngle !== undefined ? p1.headingAngle : (p1.team === 1 ? -Math.PI * 0.25 : Math.PI * 0.75);
            let h2 = p2.headingAngle !== undefined ? p2.headingAngle : h1;
            let diff = ((h2 - h1 + Math.PI * 3) % (Math.PI * 2)) - Math.PI;

            const snapSource = alpha >= 0.5 ? p2 : p1;
            results.push({
                accountID: p1.accountID,
                accountName: p1.accountName,
                heroIdentifier: p1.heroIdentifier,
                heroDisplayName: p1.heroDisplayName,
                team: p1.team,
                level: snapSource.level,
                kills: snapSource.kills,
                deaths: snapSource.deaths,
                assists: snapSource.assists,
                gpm: snapSource.gpm,
                xpm: snapSource.xpm,
                gold: Math.round(p1.gold + (p2.gold - p1.gold) * alpha),
                healthPercent: p1.healthPercent + (p2.healthPercent - p1.healthPercent) * alpha,
                manaPercent: p1.manaPercent + (p2.manaPercent - p1.manaPercent) * alpha,
                positionX: jumped ? snapSource.positionX : (p1.positionX + (p2.positionX - p1.positionX) * alpha),
                positionY: jumped ? snapSource.positionY : (p1.positionY + (p2.positionY - p1.positionY) * alpha),
                headingAngle: h1 + diff * alpha,
                isAlive: snapSource.isAlive !== false && (p1.healthPercent + (p2.healthPercent - p1.healthPercent) * alpha) > 0,
                isTeleporting: jumped && p1.healthPercent > 0 && p2.healthPercent > 0,
                items: snapSource.items || [],
                abilities: snapSource.abilities || p1.abilities || [],
                lastCastAbility: alpha < 0.45 ? (p1.lastCastAbility || p2.lastCastAbility) : p2.lastCastAbility
            });
        }

        return {
            players: results,
            structures: prevFrame.structures || {}
        };
    }

    getCurrentPlayerSnapshots() {
        return this.getCurrentFrameState().players;
    }

    formatClock(sec) {
        const m = Math.floor(Math.max(0, sec) / 60);
        const s = Math.floor(Math.max(0, sec) % 60);
        return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    }

    updateScrubberUI() {
        const scrubber = document.getElementById('replay-scrubber');
        if (scrubber) {
            scrubber.max = this.totalSeconds;
            scrubber.value = this.currentSecond;
        }

        const timeLabel = document.getElementById('replay-time-display');
        if (timeLabel) {
            timeLabel.innerText = `${this.formatClock(this.currentSecond)} / ${this.formatClock(this.totalSeconds)}`;
        }
    }

    renderEventPins() {
        const container = document.getElementById('replay-event-pins');
        if (!container || !this.timelineData?.eventPins) return;

        // Show major pins on scrubber track so 200+ ability/item pins don't overlap the slider
        const majorPins = this.timelineData.eventPins.filter(pin =>
            pin.eventType === 'KILL' ||
            pin.eventType === 'FIRST_BLOOD' ||
            pin.eventType === 'MULTIKILL' ||
            pin.eventType === 'TOWER' ||
            pin.eventType === 'VICTORY'
        ).slice(0, 45);

        container.innerHTML = majorPins.map((pin, idx) => {
            const pct = Math.max(1, Math.min(99, (pin.matchTimeSeconds / this.totalSeconds) * 100));
            const icon = pin.eventType === 'FIRST_BLOOD' ? '🩸'
                : pin.eventType === 'KILL' ? '⚔️'
                    : pin.eventType === 'TOWER' ? '🏰'
                        : pin.eventType === 'MULTIKILL' ? '🔥' : '👑';
            const safeDesc = String(pin.description || '').replace(/"/g, '&quot;');
            return `
                <button type="button" data-pin-idx="${idx}"
                        style="left: ${pct}%;"
                        title="[${this.formatClock(pin.matchTimeSeconds)}] ${safeDesc}"
                        class="replay-scrubber-pin absolute -top-2.5 -translate-x-1/2 w-4 h-4 rounded-full bg-slate-900 border border-purple-500/60 flex items-center justify-center text-[9px] shadow hover:scale-125 transition-transform z-10 cursor-pointer">
                    ${icon}
                </button>
            `;
        }).join('');

        container.querySelectorAll('.replay-scrubber-pin').forEach(btn => {
            btn.addEventListener('click', (e) => {
                const idx = parseInt(e.currentTarget.dataset.pinIdx, 10);
                const pin = majorPins[idx];
                if (pin) this.seekTo(pin.matchTimeSeconds, pin);
            });
        });
    }

    updateTooltip(e, player) {
        const tooltip = document.getElementById('replay-tooltip');
        if (!tooltip) return;

        if (!player) {
            tooltip.classList.add('hidden');
            return;
        }

        tooltip.classList.remove('hidden');

        const tooltipWidth = 260;
        const tooltipHeight = 80;
        let left = e.clientX + 16;
        let top = e.clientY - 30;

        if (left + tooltipWidth > window.innerWidth - 10) left = e.clientX - tooltipWidth - 16;
        if (top + tooltipHeight > window.innerHeight - 10) top = window.innerHeight - tooltipHeight - 10;
        if (top < 10) top = 10;
        if (left < 10) left = 10;

        tooltip.style.position = 'fixed';
        tooltip.style.left = `${left}px`;
        tooltip.style.top = `${top}px`;
        tooltip.style.zIndex = '9999';
        tooltip.style.pointerEvents = 'none';

        const heroIcon = this.getHeroIconPath(player.heroIdentifier);
        tooltip.innerHTML = `
            <div class="flex items-center gap-2.5">
                <img src="${heroIcon}" class="w-7 h-7 rounded-lg object-cover border border-slate-700 shadow-sm" onerror="this.src='/images/heroes/default.png';"/>
                <div>
                    <div class="font-bold text-white text-xs flex items-center gap-1.5">
                        <span class="w-2 h-2 rounded-full ${player.team === 1 ? 'bg-sky-400' : 'bg-rose-400'}"></span>
                        <span>${player.accountName}</span>
                        <span class="text-[10px] text-purple-300 font-semibold">(${player.heroDisplayName} · Lv ${player.level})</span>
                    </div>
                </div>
            </div>
            <div class="text-[11px] text-slate-300 mt-1.5 flex gap-3 font-mono border-t border-slate-800/80 pt-1">
                <span>KDA: <b class="text-amber-400">${player.kills}/${player.deaths}/${player.assists}</b></span>
                <span>GPM: <b class="text-emerald-400">${player.gpm}</b></span>
                <span>Gold: <b class="text-yellow-400">${player.gold}</b></span>
            </div>
        `;
    }

    updateHUD() {
        const currentPlayers = this.getCurrentPlayerSnapshots();
        const legionKills = currentPlayers.filter(p => p.team === 1).reduce((acc, p) => acc + (p.kills || 0), 0);
        const hellKills = currentPlayers.filter(p => p.team === 2).reduce((acc, p) => acc + (p.kills || 0), 0);

        const scoreLegion = document.getElementById('replay-score-legion');
        const scoreHell = document.getElementById('replay-score-hellbourne');
        if (scoreLegion) scoreLegion.innerText = legionKills;
        if (scoreHell) scoreHell.innerText = hellKills;
    }

    updateRosterUI() {
        const povSelect = document.getElementById('replay-pov-select');
        if (!povSelect || !this.timelineData?.players) return;

        povSelect.innerHTML = `
            <option value="all">👁️ Full Map Spectator</option>
            <option value="director">🎬 Action Director Cam (Auto-Follow Combat)</option>
            ${this.timelineData.players.map(p => `
                <option value="${p.accountID}">
                    ${p.team === 1 ? '🛡️ [Legion]' : '💀 [Hellbourne]'} ${p.accountName} (${p.heroDisplayName})
                </option>
            `).join('')}
        `;
    }

    updateActionFeed(force = false) {
        const feedEl = document.getElementById('replay-action-feed');
        if (!feedEl || !this.timelineData?.eventPins) return;

        const tick = Math.floor(this.currentSecond);
        if (!force && tick === this._lastFeedTick) return;
        this._lastFeedTick = tick;

        const filtered = this.timelineData.eventPins.filter(pin => {
            if (this.feedFilter === 'combat') {
                return ['KILL', 'FIRST_BLOOD', 'MULTIKILL', 'DEATH', 'TOWER', 'VICTORY'].includes(pin.eventType);
            }
            if (this.feedFilter === 'items') {
                return ['ITEM', 'ITEM_ACTIVATE'].includes(pin.eventType);
            }
            if (this.feedFilter === 'spells') {
                return ['ABILITY', 'RUNE'].includes(pin.eventType);
            }
            return true;
        });

        if (filtered.length === 0) {
            feedEl.innerHTML = '<div class="text-center py-4 text-slate-500 text-xs">No events in this category.</div>';
            return;
        }

        // Show events around current timestamp (or full chronological list up to 80 entries)
        const visible = filtered.slice(0, 120);
        feedEl.innerHTML = visible.map((pin, idx) => {
            const isPastOrNow = pin.matchTimeSeconds <= this.currentSecond + 0.5;
            const isCurrent = Math.abs(pin.matchTimeSeconds - this.currentSecond) <= 2.5;
            const icon = pin.eventType === 'FIRST_BLOOD' ? '🩸'
                : pin.eventType === 'KILL' ? '⚔️'
                    : pin.eventType === 'MULTIKILL' ? '🔥'
                        : pin.eventType === 'TOWER' ? '🏰'
                            : pin.eventType === 'VICTORY' ? '👑'
                                : (pin.eventType === 'ITEM' || pin.eventType === 'ITEM_ACTIVATE') ? '🛒'
                                    : pin.eventType === 'RUNE' ? '🔮' : '⚡';

            const rowCls = isCurrent
                ? 'bg-purple-950/60 border-purple-400/60 text-white shadow-sm'
                : isPastOrNow
                    ? 'bg-slate-950/75 border-slate-800/90 text-slate-200 hover:border-purple-500/40'
                    : 'bg-slate-950/35 border-slate-800/40 text-slate-500 hover:text-slate-300';

            const safeDesc = String(pin.description || '').replace(/</g, '&lt;').replace(/>/g, '&gt;');
            return `
                <button type="button" data-feed-idx="${idx}"
                        class="replay-feed-row w-full text-left px-2.5 py-1.5 rounded-xl border transition-all flex items-start gap-2 cursor-pointer ${rowCls}">
                    <span class="font-mono text-[10px] text-purple-300 shrink-0 mt-0.5">${this.formatClock(pin.matchTimeSeconds)}</span>
                    <span class="shrink-0">${icon}</span>
                    <span class="leading-snug break-words">${safeDesc}</span>
                </button>
            `;
        }).join('');

        feedEl.querySelectorAll('.replay-feed-row').forEach(btn => {
            btn.addEventListener('click', (e) => {
                const idx = parseInt(e.currentTarget.dataset.feedIdx, 10);
                const pin = visible[idx];
                if (pin) {
                    this.seekTo(pin.matchTimeSeconds, pin);
                }
            });
        });
    }

    updateInspector(force = false) {
        const inspector = document.getElementById('replay-player-inspector');
        if (!inspector) return;

        const tick = Math.floor(this.currentSecond * 2);
        if (!force && tick === this._lastInspectorTick) return;
        this._lastInspectorTick = tick;

        if (this.selectedAccountId === null) {
            inspector.innerHTML = `
                <div class="text-center py-6 text-slate-500 text-xs">
                    <span>Click any hero on the map or select a POV to inspect live items &amp; Q/W/E/R skills</span>
                </div>
            `;
            return;
        }

        const currentPlayers = this.getCurrentPlayerSnapshots();
        const player = currentPlayers.find(p => p.accountID === this.selectedAccountId) || currentPlayers[0];
        if (!player) return;

        const heroIcon = this.getHeroIconPath(player.heroIdentifier);
        const hpPct = Math.round(Math.max(0, Math.min(100, player.healthPercent || 0)));
        const mpPct = Math.round(Math.max(0, Math.min(100, player.manaPercent || 0)));

        const itemSlots = [];
        const equipped = Array.isArray(player.items) ? player.items.slice(0, 6) : [];
        for (let s = 0; s < 6; s++) {
            const it = equipped[s] || null;
            if (it) {
                const iconPath = this.getItemIconPath(it);
                const safeCode = String(it).replace(/'/g, "\\'");
                itemSlots.push(`
                    <button type="button" onclick="if(window.showItemModal) window.showItemModal('${safeCode}')"
                            class="w-9 h-9 rounded-lg overflow-hidden bg-slate-950 border border-amber-500/35 hover:border-amber-400 p-0.5 shadow-sm transition-transform hover:scale-105 cursor-pointer"
                            title="${it} (Click for recipe &amp; stats)">
                        <img src="${iconPath}" onerror="this.onerror=null;this.src='/images/items/default.png';" class="w-full h-full object-contain rounded"/>
                    </button>
                `);
            } else {
                itemSlots.push(`
                    <div class="w-9 h-9 rounded-lg bg-slate-950/50 border border-slate-800/80 flex items-center justify-center text-[9px] text-slate-700 font-mono">
                        ${s + 1}
                    </div>
                `);
            }
        }

        inspector.innerHTML = `
            <div class="flex items-center gap-3">
                <div class="w-12 h-12 rounded-xl overflow-hidden bg-slate-900 border ${player.team === 1 ? 'border-emerald-500/40' : 'border-rose-500/40'} shrink-0">
                    <img src="${heroIcon}" onerror="this.src='/images/heroes/default.png';" class="w-full h-full object-cover"/>
                </div>
                <div class="min-w-0 flex-1">
                    <div class="flex items-center justify-between gap-2">
                        <h4 class="text-sm font-extrabold text-white truncate">${player.accountName}</h4>
                        <span class="text-[10px] font-mono px-1.5 py-0.5 rounded ${player.team === 1 ? 'bg-emerald-500/15 text-emerald-300' : 'bg-rose-500/15 text-rose-300'}">
                            ${player.team === 1 ? 'Legion' : 'Hellbourne'}
                        </span>
                    </div>
                    <div class="text-xs text-purple-300 font-semibold">${player.heroDisplayName} · Lv ${player.level}</div>
                    <div class="mt-1.5 space-y-1">
                        <div class="w-full h-1.5 bg-slate-950 rounded-full overflow-hidden border border-slate-800">
                            <div class="h-full bg-emerald-500 transition-all" style="width:${hpPct}%"></div>
                        </div>
                        <div class="w-full h-1.5 bg-slate-950 rounded-full overflow-hidden border border-slate-800">
                            <div class="h-full bg-sky-500 transition-all" style="width:${mpPct}%"></div>
                        </div>
                    </div>
                </div>
            </div>
            <div class="grid grid-cols-4 gap-1.5 pt-2 border-t border-slate-800 text-center">
                <div class="p-1.5 rounded-lg bg-slate-950/60">
                    <div class="text-[9px] text-slate-500 uppercase font-bold">KDA</div>
                    <div class="text-xs font-mono font-black text-white">${player.kills}/${player.deaths}/${player.assists}</div>
                </div>
                <div class="p-1.5 rounded-lg bg-slate-950/60">
                    <div class="text-[9px] text-slate-500 uppercase font-bold">GPM</div>
                    <div class="text-xs font-mono font-black text-emerald-400">${player.gpm}</div>
                </div>
                <div class="p-1.5 rounded-lg bg-slate-950/60">
                    <div class="text-[9px] text-slate-500 uppercase font-bold">XPM</div>
                    <div class="text-xs font-mono font-black text-sky-400">${player.xpm}</div>
                </div>
                <div class="p-1.5 rounded-lg bg-slate-950/60">
                    <div class="text-[9px] text-slate-500 uppercase font-bold">Gold</div>
                    <div class="text-xs font-mono font-black text-amber-400">${player.gold}</div>
                </div>
            </div>
            <div class="pt-2 border-t border-slate-800 space-y-1.5">
                <div class="text-[10px] text-slate-400 uppercase font-bold flex items-center justify-between">
                    <span>Skills &amp; Casts</span>
                    <span class="text-[9px] text-purple-400 font-mono">Q · W · E · R</span>
                </div>
                <div class="grid grid-cols-4 gap-1.5">
                    ${player.abilities && player.abilities.length > 0 ? player.abilities.map((ab, idx) => {
                        const onCd = ab.cooldownRemaining > 0;
                        const isCasting = player.lastCastAbility && player.lastCastAbility === ab.name;
                        const maxPips = idx === 3 ? 3 : 4;
                        const learned = Math.max(0, Math.min(maxPips, ab.level || 0));
                        let pips = '';
                        for (let p = 0; p < maxPips; p++) {
                            pips += `<span class="w-1.5 h-1.5 rounded-full ${p < learned ? 'bg-amber-400' : 'bg-slate-700'}"></span>`;
                        }
                        const borderCls = isCasting
                            ? 'border-amber-400 ring-2 ring-amber-400/50'
                            : learned === 0
                                ? 'border-slate-800 opacity-50'
                                : onCd
                                    ? 'border-slate-700 opacity-80'
                                    : 'border-purple-500/60';
                        return `
                            <div class="relative rounded-lg overflow-hidden bg-slate-950 border ${borderCls} p-0.5 aspect-square flex flex-col justify-between"
                                 title="${ab.name} (${ab.slot}) · Level ${learned}/${maxPips}">
                                <img src="${ab.iconPath}" onerror="this.src='/images/heroes/default.png';" class="absolute inset-0 w-full h-full object-cover"/>
                                ${onCd ? `
                                    <div class="absolute inset-0 bg-slate-950/80 flex items-center justify-center">
                                        <span class="text-xs font-mono font-black text-amber-400">${ab.cooldownRemaining}s</span>
                                    </div>
                                ` : ''}
                                <div class="relative z-10 flex justify-between items-start w-full px-1 pt-0.5">
                                    <span class="bg-slate-900/90 text-[9px] font-black text-purple-300 px-1 rounded">${ab.slot}</span>
                                    <span class="bg-slate-900/90 text-[8px] text-emerald-400 font-bold px-0.5 rounded">L${learned}</span>
                                </div>
                                <div class="relative z-10 flex flex-col items-center gap-0.5 bg-slate-950/85 rounded px-0.5 py-0.5 mt-auto">
                                    <div class="flex items-center gap-0.5">${pips}</div>
                                </div>
                            </div>
                        `;
                    }).join('') : '<span class="text-[11px] text-slate-500 italic col-span-4 text-center">No abilities</span>'}
                </div>
            </div>
            <div class="pt-2 border-t border-slate-800 space-y-1.5">
                <div class="text-[10px] text-slate-400 uppercase font-bold flex items-center justify-between">
                    <span>6-Slot Inventory</span>
                    <span class="text-[9px] text-amber-400/80">Click item for tree</span>
                </div>
                <div class="grid grid-cols-6 gap-1.5">
                    ${itemSlots.join('')}
                </div>
            </div>
        `;
    }
}

window.MatchReplayPlayer = MatchReplayPlayer;
