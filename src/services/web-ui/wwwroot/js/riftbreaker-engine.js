// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

(function (global) {
    'use strict';

    const log = (msg, ...args) => console.log(`[Riftbreaker] ${msg}`, ...args);
    const warn = (msg, ...args) => console.warn(`[Riftbreaker] ${msg}`, ...args);

    const ARENA = {
        halfW: 4.8,
        halfD: 2.85,
        p1BaseX: -4.2,
        p2BaseX: 4.2,
        p1RushMaxX: -1.35,
        p2RushMinX: 1.35,
        paddleHalfZ: 0.62,
        paddleHalfX: 0.16,
        ballRadius: 0.16,
        winningScore: 11
    };

    const HERO_META = {
        chronos: {
            id: 'chronos',
            name: 'Chronos',
            title: 'Master of Time',
            ultName: 'Chronosphere',
            ultDesc: 'Freezes balls inside a 0.15x time dome & releases them as an accelerated volley.',
            color: 0x22d3ee,
            cssColor: '#22d3ee',
            modelPath: '/assets/prebaked/models/chronos_default.glb',
            targetHeight: 0.78,
            anims: { idle: ['idle'], walk: ['walk_1', 'ability_walk_1'], attack: ['attack_1', 'attack_2'], ult: ['ability_4', 'attack_2'], taunt: ['taunt_1'] }
        },
        behemoth: {
            id: 'behemoth',
            name: 'Behemoth',
            title: 'Worldbreaker',
            ultName: 'Fissure',
            ultDesc: 'Slams an angled volcanic rock wall across mid-court for brutal bank-shot ricochets.',
            color: 0xf59e0b,
            cssColor: '#f59e0b',
            modelPath: '/assets/prebaked/models/behemoth_default.glb',
            targetHeight: 0.84,
            anims: { idle: ['idle'], walk: ['attack_1', 'idle'], attack: ['attack_1', 'attack_2'], ult: ['ability_1', 'attack_2'], taunt: ['taunt_1'] }
        },
        devourer: {
            id: 'devourer',
            name: 'Devourer',
            title: 'Butcher of Newerth',
            ultName: 'Meat Hook',
            ultDesc: 'Lashes out with his chain hook to snatch & yank a flying ball 90 degrees across lanes.',
            color: 0xf43f5e,
            cssColor: '#f43f5e',
            modelPath: '/assets/prebaked/models/devourer_default.glb',
            targetHeight: 0.86,
            anims: { idle: ['idle'], walk: ['walk_1'], attack: ['attack_1', 'attack_2'], ult: ['attack_3', 'attack_4', 'attack_1'], taunt: ['taunt_1'] }
        },
        bubbles: {
            id: 'bubbles',
            name: 'Bubbles',
            title: 'Emerald Guardian',
            ultName: 'Take Cover',
            ultDesc: 'Enters an invulnerable shell that auto-parries shots at 1.75x hyper speed.',
            color: 0x34d399,
            cssColor: '#34d399',
            modelPath: '/assets/prebaked/models/bubbles_default.glb',
            targetHeight: 0.76,
            anims: { idle: ['idle'], walk: ['walk_1', 'walk_2'], attack: ['attack_1', 'attack_2'], ult: ['taunt_1', 'attack_2'], taunt: ['taunt_1'] }
        },
        flux: {
            id: 'flux',
            name: 'Flux',
            title: 'Electro-Magnetic Warden',
            ultName: 'Magnetic Surge',
            ultDesc: 'Projects an electromagnetic gravity field that violently curves opponent trajectories.',
            color: 0x818cf8,
            cssColor: '#818cf8',
            modelPath: '/assets/prebaked/models/flux_default.glb',
            targetHeight: 0.80,
            anims: { idle: ['idle'], walk: ['ability_1', 'idle'], attack: ['ability_2', 'ability_1'], ult: ['ability_3', 'ability_2b'], taunt: ['taunt_1'] }
        },
        doctor_repulsor: {
            id: 'doctor_repulsor',
            name: 'Doctor Repulsor',
            title: 'Volt Tactician',
            ultName: 'Ludicrous Speed',
            ultDesc: 'Zips instantaneously along the court in a lightning surge that supercharges shots.',
            color: 0xe879f9,
            cssColor: '#e879f9',
            modelPath: '/assets/prebaked/models/doctor_repulsor_default.glb',
            targetHeight: 0.78,
            anims: { idle: ['idle'], walk: ['ability_4', 'ability_1'], attack: ['attack_1', 'attack_2'], ult: ['ability_4', 'ability_2'], taunt: ['taunt_1'] }
        }
    };

    const RIFT_CARDS = {
        portal_sidelines: {
            id: 'portal_sidelines',
            name: 'Portal Sidelines',
            desc: 'North & South rails open dimensional portals — balls wrap seamlessly top to bottom.'
        },
        twin_illusion: {
            id: 'twin_illusion',
            name: 'Twin Illusion Rune',
            desc: 'Manifests a shimmering second chaos orb on every serve for relentless double-ball rallies.'
        },
        prism_obelisks: {
            id: 'prism_obelisks',
            name: 'Prism Obelisks',
            desc: 'Raises two crystalline deflector pillars in mid-court that whip shots diagonally.'
        },
        gravity_conduit: {
            id: 'gravity_conduit',
            name: 'Gravity Conduit',
            desc: 'Ignites a central rift singularity that bends nearby ball arcs across center court.'
        }
    };

    // --- Modern Web Feature Helpers ---
    function supportsTopLayerExit() {
        return typeof CSS !== 'undefined' &&
            CSS.supports &&
            CSS.supports('transition-behavior', 'allow-discrete') &&
            CSS.supports('selector(:popover-open)');
    }

    function openTopLayerDialog(dialog) {
        if (!dialog || dialog.open) return;
        delete dialog.dataset.closing;
        dialog.showModal();
    }

    function closeTopLayer(el, reason = '') {
        if (!el) return;
        const isDialog = el instanceof HTMLDialogElement;
        if (isDialog && !el.open) return;
        if (!isDialog && !el.matches(':popover-open')) return;

        const finishClose = () => {
            delete el.dataset.closing;
            if (isDialog && el.open) el.close(reason);
            else if (!isDialog && el.matches(':popover-open') && el.hidePopover) el.hidePopover();
        };

        const prefersReduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (!supportsTopLayerExit() || prefersReduced) {
            finishClose();
            return;
        }

        el.dataset.closing = 'true';
        const anims = typeof el.getAnimations === 'function' ? el.getAnimations() : [];
        if (anims.length > 0) {
            Promise.allSettled(anims.map(a => a.finished)).then(finishClose);
        } else {
            finishClose();
        }
    }

    // --- Audio Manager (Web Audio API Synth + HoN Announcer .ogg Packs) ---
    class RiftAudioEngine {
        constructor() {
            this.ctx = null;
            this.muted = false;
            this.synthEnabled = true;
            this.pack = 'default';
            this.oggCache = new Map();
            this.lastBeatTime = 0;
            this.beatIndex = 0;
        }

        ensureContext() {
            if (this.muted) return null;
            if (!this.ctx) {
                const AudioCtx = window.AudioContext || window.webkitAudioContext;
                if (AudioCtx) {
                    this.ctx = new AudioCtx();
                }
            }
            if (this.ctx && this.ctx.state === 'suspended') {
                this.ctx.resume().catch(() => {});
            }
            return this.ctx;
        }

        setPack(pack) {
            const valid = ['default', 'breakycpk', 'flamboyant', 'bamf'];
            this.pack = valid.includes(pack) ? pack : 'default';
            log(`Announcer pack switched -> ${this.pack}`);
            this.preloadPack();
        }

        preloadPack() {
            const files = [
                `/sounds/announcer/${this.pack}/get_it_on.ogg`,
                `/sounds/announcer/${this.pack}/prepare_for_battle.ogg`,
                `/sounds/announcer/${this.pack}/first_blood.ogg`,
                `/sounds/announcer/${this.pack}/double_kill.ogg`,
                `/sounds/announcer/${this.pack}/triple_kill.ogg`,
                `/sounds/announcer/${this.pack}/annihilation.ogg`,
                `/sounds/announcer/${this.pack}/denied.ogg`,
                `/sounds/announcer/${this.pack}/powerup_illusion.ogg`,
                `/sounds/announcer/${this.pack}/kongor_slain.ogg`,
                `/sounds/announcer/${this.pack}/victory.ogg`,
                `/sounds/announcer/${this.pack}/defeat.ogg`,
                '/sounds/announcer/bamf/bullet_glass.ogg',
                '/sounds/devourer/hook_cast.ogg',
                '/sounds/devourer/hook_impact.ogg',
                '/sounds/ui/button_click.ogg',
                '/sounds/ui/select_unit.ogg',
                '/sounds/ui/ping.ogg'
            ];
            for (const url of files) {
                if (!this.oggCache.has(url)) {
                    const a = new Audio(url);
                    a.preload = 'none';
                    this.oggCache.set(url, a);
                }
            }
        }

        playOggUrl(url, volume = 0.72) {
            if (this.muted) return;
            try {
                let base = this.oggCache.get(url);
                if (!base) {
                    base = new Audio(url);
                    this.oggCache.set(url, base);
                }
                const clone = base.cloneNode();
                clone.volume = volume;
                clone.play().catch(() => {});
            } catch (_) {}
        }

        playAnnouncer(cue, volume = 0.75) {
            this.playOggUrl(`/sounds/announcer/${this.pack}/${cue}.ogg`, volume);
        }

        playPongBlip(freq = 520, duration = 0.055, wave = 'square', gainVal = 0.08) {
            const ctx = this.ensureContext();
            if (!ctx || this.muted) return;
            try {
                const now = ctx.currentTime;
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                osc.type = wave;
                osc.frequency.setValueAtTime(freq, now);
                gain.gain.setValueAtTime(gainVal, now);
                gain.gain.exponentialRampToValueAtTime(0.001, now + duration);
                osc.connect(gain);
                gain.connect(ctx.destination);
                osc.start(now);
                osc.stop(now + duration);
            } catch (_) {}
        }

        playParryChime() {
            this.playPongBlip(660, 0.09, 'triangle', 0.12);
            this.playPongBlip(990, 0.14, 'sine', 0.10);
        }

        playUltimateChime() {
            this.playPongBlip(330, 0.18, 'sawtooth', 0.09);
            this.playPongBlip(660, 0.22, 'triangle', 0.10);
        }

        tickOverdriveBeat(nowSec, act) {
            if (act < 4 || !this.synthEnabled || this.muted) return;
            if (nowSec - this.lastBeatTime < 0.48) return; // ~125 BPM chill pulse
            this.lastBeatTime = nowSec;
            const bassNotes = [110, 110, 146.83, 130.81];
            const note = bassNotes[this.beatIndex % bassNotes.length];
            this.beatIndex++;
            this.playPongBlip(note, 0.14, 'triangle', 0.05);
        }
    }

    // --- Main Riftbreaker Engine ---
    class RiftbreakerGame {
        constructor(config) {
            this.cfg = config || {};
            this.audio = new RiftAudioEngine();
            this.audio.setPack(config.initialPack || 'default');

            this.mode = config.initialMode || 'ai'; // 'ai' | 'local2p' | 'online'
            this.aiTier = 'tactician';
            this.roomCode = config.initialRoom || 'RIFT';
            this.playerName = config.playerName || 'Champion';
            this.localSlot = 1;
            this.playerId = this.initPlayerId();

            // Match state
            this.act = 1; // 1: CRT Pong, 2: Hero Duel, 3: Rift Shift, 4: Kongor Overdrive
            this.phase = 'playing'; // 'playing' | 'hero_draft' | 'card_draft' | 'victory' | 'lobby_wait'
            this.paused = false;
            this.comfortMode = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            this.skipIntroNext = Boolean(config.skipIntro);
            this.scores = [0, 0];
            this.streak = [0, 0];
            this.rallyCount = 0;
            this.longestRally = 0;
            this.parriesLanded = [0, 0];
            this.heroDraftTriggered = false;
            this.cardDraftTriggered = false;
            this.bossSpawned = false;
            this.activeRiftCards = new Set();
            this.winnerSlot = null;

            // Hero selections
            this.heroes = { 1: null, 2: null };
            this.heroRigs = { 1: null, 2: null };
            this.kongorRig = null;
            this.hookAssetTemplate = null;
            this.glbBufferCache = new Map();
            this.gltfLoader = null;

            // Player physics
            this.players = {
                1: { x: ARENA.p1BaseX, z: 0, vx: 0, vz: 0, energy: 55, parryTimer: 0, parryCooldown: 0, ultCooldown: 0, shellTimer: 0, dashTimer: 0 },
                2: { x: ARENA.p2BaseX, z: 0, vx: 0, vz: 0, energy: 55, parryTimer: 0, parryCooldown: 0, ultCooldown: 0, shellTimer: 0, dashTimer: 0 }
            };

            // Input tracking
            this.keys = new Set();
            this.pointerWorld = new THREE.Vector3(-ARENA.p1BaseX, 0, 0);
            this.pointerActive = false;
            this.hitstopTimer = 0;
            this.shakeIntensity = 0;
            this.shatterShards = [];
            this.balls = [];
            this.activeUltEffects = [];
            this.prismObelisks = [];
            this.hookVisuals = [];

            // Background suspend flags (Modern Web Guidance: visibility + contentvisibility)
            this.isTabHidden = document.hidden;
            this.isStageSkipped = false;
            this.lastFrameTime = performance.now();
            this.rafId = null;

            // Camera choreography
            this.camCurrentPos = new THREE.Vector3(0, 12.6, 0.01);
            this.camCurrentLook = new THREE.Vector3(0, 0, 0);
            this.camGoalPos = new THREE.Vector3(0, 12.6, 0.01);
            this.camGoalLook = new THREE.Vector3(0, 0, 0);

            // Scene handles
            this.scene = null;
            this.camera = null;
            this.renderer = null;
            this.gltfLoader = null;
            this.arenaMeshes = {};
            this.resizeObserver = null;
            this.hub = null;
            this.lastNetworkPaddleSent = 0;
            this.arStream = null;
            this.arActive = false;

            this.dom = this.resolveDom();
        }

        initPlayerId() {
            try {
                let pid = sessionStorage.getItem('riftbreaker_pid');
                if (!pid) {
                    pid = 'rb_' + Math.random().toString(36).slice(2, 10);
                    sessionStorage.setItem('riftbreaker_pid', pid);
                }
                return pid;
            } catch (_) {
                return 'rb_' + Math.random().toString(36).slice(2, 10);
            }
        }

        resolveDom() {
            return {
                stage: document.getElementById('riftbreakerStage'),
                container: document.getElementById('canvasContainer'),
                canvas: document.getElementById('gameCanvas'),
                video: document.getElementById('cameraFeed'),
                crtOverlay: document.getElementById('crtOverlay'),
                score1: document.getElementById('scoreP1'),
                score2: document.getElementById('scoreP2'),
                name1: document.getElementById('nameP1'),
                name2: document.getElementById('nameP2'),
                heroTag1: document.getElementById('heroTagP1'),
                heroTag2: document.getElementById('heroTagP2'),
                energyBar1: document.getElementById('energyBarP1'),
                energyBar2: document.getElementById('energyBarP2'),
                energyText1: document.getElementById('energyTextP1'),
                actBadge: document.getElementById('actBadge'),
                riftChips: document.getElementById('activeRiftChips'),
                toast: document.getElementById('announcerToast'),
                toastText: document.getElementById('announcerToastText'),
                btnPause: document.getElementById('btnPausePlay'),
                btnComfort: document.getElementById('btnComfortMode'),
                btnUlt: document.getElementById('btnCastUlt'),
                btnUltLabel: document.getElementById('btnCastUltLabel'),
                btnParry: document.getElementById('btnParryStrike'),
                settingsPopover: document.getElementById('settingsPopover'),
                selectAnnouncer: document.getElementById('selectAnnouncer'),
                selectAiTier: document.getElementById('selectAiTier'),
                chkSkipIntro: document.getElementById('chkSkipIntro'),
                btnToggleAudio: document.getElementById('btnToggleAudio'),
                btnToggleSynth: document.getElementById('btnToggleSynth'),
                btnToggleAr: document.getElementById('btnToggleAr'),
                heroDraftDialog: document.getElementById('heroDraftDialog'),
                heroGrid: document.getElementById('heroGrid'),
                heroDraftStatus: document.getElementById('heroDraftStatus'),
                cardDraftDialog: document.getElementById('cardDraftDialog'),
                cardGrid: document.getElementById('cardGrid'),
                cardDraftSubtitle: document.getElementById('cardDraftSubtitle'),
                modeDialog: document.getElementById('modeDialog'),
                inputRoomCode: document.getElementById('inputRoomCode'),
                inputPlayerName: document.getElementById('inputPlayerName'),
                onlineStatusBanner: document.getElementById('onlineStatusBanner'),
                victoryDialog: document.getElementById('victoryDialog'),
                victoryTitle: document.getElementById('victoryTitle'),
                victoryScore: document.getElementById('victoryScore'),
                victoryStats: document.getElementById('victoryStats')
            };
        }

        async init() {
            log('Bootstrapping Chrono-Pong: Riftbreaker engine...');
            this.initThreeScene();
            this.bindInputs();
            this.bindUi();
            this.audio.preloadPack();

            // Preload Kongor boss GLB + hook mesh silently in background
            this.preloadGlbAsset('/assets/prebaked/models/kongor_default.glb').then(() => this.prepareKongorBoss());
            this.preloadGlbAsset('/assets/prebaked/models/devourer_hook.glb');

            if (this.skipIntroNext) {
                await this.jumpStraightToHeroDraft();
            } else {
                this.spawnServeBall(1);
                this.announceBanner('RIFT-OS TERMINAL TENNIS v1.972 — First to 2 triggers Soul-Bind');
            }

            this.updateHud();
            this.startLoop();
            log('Engine ready. Mode:', this.mode, 'Comfort:', this.comfortMode);
        }

        initThreeScene() {
            const { canvas, container } = this.dom;
            this.scene = new THREE.Scene();
            this.scene.background = new THREE.Color(0x03050a);

            const w = Math.max(container.clientWidth || window.innerWidth, 320);
            const h = Math.max(container.clientHeight || window.innerHeight, 240);
            this.camera = new THREE.PerspectiveCamera(42, w / h, 0.1, 100);
            this.camera.position.copy(this.camCurrentPos);
            this.camera.lookAt(this.camCurrentLook);

            this.renderer = new THREE.WebGLRenderer({
                canvas,
                antialias: true,
                alpha: true,
                powerPreference: 'high-performance'
            });
            this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
            this.renderer.setSize(w, h, false);
            this.renderer.shadowMap.enabled = true;
            this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;

            if (global.KongorViewerCore) {
                global.KongorViewerCore.configureRendererColorSpace(this.renderer, {
                    toneMapping: 'AgX',
                    toneMappingExposure: 1.25
                });
                this.gltfLoader = global.KongorViewerCore.createKongorGLTFLoader({
                    dracoDecoderPath: '/lib/three/draco/',
                    enableDraco: true,
                    enableMeshopt: true
                });
                this.resizeObserver = global.KongorViewerCore.observeDevicePixelCanvasResize(
                    container, canvas, this.renderer, this.camera
                );
            } else if (THREE.GLTFLoader) {
                this.gltfLoader = new THREE.GLTFLoader();
            }

            this.buildLighting();
            this.buildArenaMeshes();
        }

        buildLighting() {
            const hemi = new THREE.HemisphereLight(0xa5f3fc, 0x0f172a, 0.9);
            this.scene.add(hemi);
            this.arenaMeshes.hemiLight = hemi;

            const dir = new THREE.DirectionalLight(0xffffff, 1.8);
            dir.position.set(4, 11, 6);
            dir.castShadow = true;
            dir.shadow.mapSize.width = 1024;
            dir.shadow.mapSize.height = 1024;
            dir.shadow.camera.near = 1;
            dir.shadow.camera.far = 30;
            dir.shadow.camera.left = -7;
            dir.shadow.camera.right = 7;
            dir.shadow.camera.top = 5;
            dir.shadow.camera.bottom = -5;
            this.scene.add(dir);
            this.arenaMeshes.dirLight = dir;

            const rim1 = new THREE.PointLight(0x22d3ee, 2.5, 14);
            rim1.position.set(-4.8, 2.2, 0);
            this.scene.add(rim1);
            this.arenaMeshes.rimP1 = rim1;

            const rim2 = new THREE.PointLight(0xf43f5e, 2.8, 14);
            rim2.position.set(4.8, 2.2, 0);
            this.scene.add(rim2);
            this.arenaMeshes.rimP2 = rim2;

            const bossLight = new THREE.PointLight(0xfbbf24, 0, 12);
            bossLight.position.set(0, 3.6, 1.4);
            this.scene.add(bossLight);
            this.arenaMeshes.bossLight = bossLight;
        }

        buildArenaMeshes() {
            // 1. Monochrome Retro 1972 Floor vs 3D Sanctum Platform
            const floorGeo = new THREE.BoxGeometry(ARENA.halfW * 2 + 0.4, 0.4, ARENA.halfD * 2 + 0.4);
            const floorMat = new THREE.MeshStandardMaterial({
                color: 0x04070d,
                roughness: 0.85,
                metalness: 0.15
            });
            const floor = new THREE.Mesh(floorGeo, floorMat);
            floor.position.set(0, -0.21, 0);
            floor.receiveShadow = true;
            this.scene.add(floor);
            this.arenaMeshes.floor = floor;

            // Sanctum Rune Grid (hidden in Act I, fades/switches in Act II+)
            const gridHelper = new THREE.GridHelper(12, 24, 0x0ea5e9, 0x1e293b);
            gridHelper.position.y = 0.005;
            gridHelper.visible = false;
            this.scene.add(gridHelper);
            this.arenaMeshes.sanctumGrid = gridHelper;

            // Center dashed divider line (Retro Act I aesthetic & 3D rift net)
            const centerLineGroup = new THREE.Group();
            for (let z = -ARENA.halfD + 0.2; z <= ARENA.halfD - 0.2; z += 0.45) {
                const dash = new THREE.Mesh(
                    new THREE.BoxGeometry(0.1, 0.02, 0.22),
                    new THREE.MeshBasicMaterial({ color: 0xe2e8f0 })
                );
                dash.position.set(0, 0.01, z);
                centerLineGroup.add(dash);
            }
            this.scene.add(centerLineGroup);
            this.arenaMeshes.centerDashes = centerLineGroup;

            // North & South Rails (glow bright portal blue/orange when Portal Sidelines active)
            const railGeo = new THREE.BoxGeometry(ARENA.halfW * 2 + 0.3, 0.22, 0.18);
            const northMat = new THREE.MeshStandardMaterial({ color: 0x94a3b8, emissive: 0x000000, roughness: 0.4 });
            const southMat = new THREE.MeshStandardMaterial({ color: 0x94a3b8, emissive: 0x000000, roughness: 0.4 });
            const northRail = new THREE.Mesh(railGeo, northMat);
            northRail.position.set(0, 0.11, -ARENA.halfD - 0.09);
            const southRail = new THREE.Mesh(railGeo, southMat);
            southRail.position.set(0, 0.11, ARENA.halfD + 0.09);
            this.scene.add(northRail, southRail);
            this.arenaMeshes.northRail = northRail;
            this.arenaMeshes.southRail = southRail;

            // Paddle Craft Meshes (P1 = Cyan/White, P2 = Crimson/White)
            this.arenaMeshes.paddles = {
                1: this.createPaddleUnit(0xf8fafc, 0x22d3ee),
                2: this.createPaddleUnit(0xf8fafc, 0xf43f5e)
            };
            this.scene.add(this.arenaMeshes.paddles[1].group);
            this.scene.add(this.arenaMeshes.paddles[2].group);

            // Gravity Conduit ring (Act III optional card)
            const gravRing = new THREE.Mesh(
                new THREE.TorusGeometry(0.85, 0.045, 16, 48),
                new THREE.MeshBasicMaterial({ color: 0xa855f7, transparent: true, opacity: 0.75 })
            );
            gravRing.rotation.x = Math.PI / 2;
            gravRing.position.y = 0.04;
            gravRing.visible = false;
            this.scene.add(gravRing);
            this.arenaMeshes.gravityRing = gravRing;

            // Kongor Pedestal Shockwave (Act IV)
            const stompRing = new THREE.Mesh(
                new THREE.RingGeometry(0.45, 0.65, 36),
                new THREE.MeshBasicMaterial({ color: 0xf97316, side: THREE.DoubleSide, transparent: true, opacity: 0 })
            );
            stompRing.rotation.x = -Math.PI / 2;
            stompRing.position.y = 0.03;
            stompRing.visible = false;
            this.scene.add(stompRing);
            this.arenaMeshes.kongorShockwave = stompRing;
        }

        createPaddleUnit(baseColor, accentColor) {
            const group = new THREE.Group();
            const body = new THREE.Mesh(
                new THREE.BoxGeometry(ARENA.paddleHalfX * 2, 0.18, ARENA.paddleHalfZ * 2),
                new THREE.MeshStandardMaterial({ color: baseColor, emissive: accentColor, emissiveIntensity: 0.15, roughness: 0.25, metalness: 0.5 })
            );
            body.position.y = 0.1;
            body.castShadow = true;
            group.add(body);

            // Parry shockwave shield arc (visible only during active parry window)
            const parryArc = new THREE.Mesh(
                new THREE.TorusGeometry(0.58, 0.05, 12, 28, Math.PI),
                new THREE.MeshBasicMaterial({ color: accentColor, transparent: true, opacity: 0.85 })
            );
            parryArc.rotation.x = Math.PI / 2;
            parryArc.rotation.z = -Math.PI / 2;
            parryArc.position.y = 0.22;
            parryArc.visible = false;
            group.add(parryArc);

            // Bubbles Take Cover shell sphere
            const shellSphere = new THREE.Mesh(
                new THREE.SphereGeometry(0.75, 24, 18),
                new THREE.MeshStandardMaterial({
                    color: 0x34d399,
                    emissive: 0x10b981,
                    emissiveIntensity: 0.6,
                    transparent: true,
                    opacity: 0.42,
                    wireframe: false
                })
            );
            shellSphere.position.y = 0.4;
            shellSphere.visible = false;
            group.add(shellSphere);

            const heroAnchor = new THREE.Group();
            heroAnchor.position.set(0, 0.19, 0);
            group.add(heroAnchor);

            return { group, body, parryArc, shellSphere, heroAnchor, accentColor };
        }

        // --- GLB Loading with Box3 Foot Grounding & ArrayBuffer Cache ---
        async preloadGlbAsset(url) {
            if (this.glbBufferCache.has(url)) return this.glbBufferCache.get(url);
            try {
                const resp = await fetch(url);
                if (!resp.ok) throw new Error(`HTTP ${resp.status} for ${url}`);
                const buf = await resp.arrayBuffer();
                this.glbBufferCache.set(url, buf);
                return buf;
            } catch (err) {
                warn(`Failed to preload GLB ${url}:`, err);
                return null;
            }
        }

        async instantiateGlb(url) {
            if (!this.gltfLoader) return null;
            const buf = await this.preloadGlbAsset(url);
            if (!buf) return null;
            return new Promise((resolve) => {
                this.gltfLoader.parse(
                    buf.slice(0),
                    '/assets/prebaked/models/',
                    (gltf) => resolve(gltf),
                    (err) => {
                        warn(`GLB parse failed (${url}):`, err);
                        resolve(null);
                    }
                );
            });
        }

        normalizeModelToHeight(model, targetHeight, animations = []) {
            if (animations && animations.length > 0) {
                const idleClip = animations.find(a => a.name.toLowerCase() === 'idle') || animations[0];
                if (idleClip) {
                    const tempMixer = new THREE.AnimationMixer(model);
                    tempMixer.clipAction(idleClip).play();
                    tempMixer.update(0.1);
                    tempMixer.stopAllAction();
                    tempMixer.uncacheRoot(model);
                }
            }

            model.updateMatrixWorld(true);
            const box = new THREE.Box3();
            const v = new THREE.Vector3();
            model.traverse((obj) => {
                if (obj.isMesh && obj.geometry && obj.geometry.attributes.position) {
                    const pos = obj.geometry.attributes.position;
                    const step = Math.max(1, Math.floor(pos.count / 200));
                    for (let i = 0; i < pos.count; i += step) {
                        if (obj.isSkinnedMesh && typeof obj.getVertexPosition === 'function') {
                            obj.getVertexPosition(i, v);
                        } else {
                            v.fromBufferAttribute(pos, i);
                        }
                        v.applyMatrix4(obj.matrixWorld);
                        box.expandByPoint(v);
                    }
                }
            });

            if (box.isEmpty()) {
                box.setFromObject(model);
            }

            const size = new THREE.Vector3();
            box.getSize(size);
            const rawH = Math.max(size.y, 0.001);
            const scale = targetHeight / rawH;
            model.scale.setScalar(scale);

            const center = new THREE.Vector3();
            box.getCenter(center);
            model.position.x = -center.x * scale;
            model.position.z = -center.z * scale;
            model.position.y = -box.min.y * scale;

            model.traverse((obj) => {
                if (obj.isMesh) {
                    obj.castShadow = true;
                    obj.receiveShadow = true;
                    if (global.KongorViewerCore && obj.material && obj.material.map) {
                        global.KongorViewerCore.configureTextureColorSpace(obj.material.map);
                    }
                }
            });
        }

        async attachHeroModel(slot, heroId) {
            const meta = HERO_META[heroId];
            if (!meta) return;
            const paddle = this.arenaMeshes.paddles[slot];
            if (!paddle) return;

            // Dispose previous rig cleanly
            if (this.heroRigs[slot]) {
                const old = this.heroRigs[slot];
                paddle.heroAnchor.remove(old.root);
                if (global.KongorViewerCore) {
                    global.KongorViewerCore.disposeObject3DResources(old.root, null, old.mixer);
                }
                this.heroRigs[slot] = null;
            }

            const gltf = await this.instantiateGlb(meta.modelPath);
            if (!gltf) {
                // Fallback stylized prism hero if network/WebGL failed
                const fallback = new THREE.Mesh(
                    new THREE.OctahedronGeometry(0.32, 1),
                    new THREE.MeshStandardMaterial({ color: meta.color, emissive: meta.color, emissiveIntensity: 0.45 })
                );
                fallback.position.y = 0.35;
                paddle.heroAnchor.add(fallback);
                this.heroRigs[slot] = { root: fallback, mixer: null, actions: {}, currentClip: 'idle' };
                return;
            }

            const root = gltf.scene;
            this.normalizeModelToHeight(root, meta.targetHeight, gltf.animations || []);
            // Slot 1 faces East (+X -> rot.y = +PI/2), Slot 2 faces West (-X -> rot.y = -PI/2)
            root.rotation.y = slot === 1 ? Math.PI / 2 : -Math.PI / 2;

            const mixer = new THREE.AnimationMixer(root);
            const actions = {};
            for (const clip of (gltf.animations || [])) {
                actions[clip.name.toLowerCase()] = mixer.clipAction(clip);
            }

            paddle.heroAnchor.add(root);
            paddle.body.material.emissive.setHex(meta.color);
            paddle.body.material.emissiveIntensity = 0.55;
            paddle.parryArc.material.color.setHex(meta.color);
            paddle.accentColor = meta.color;

            this.heroRigs[slot] = { root, mixer, actions, meta, currentClip: null };
            this.playHeroAnim(slot, 'idle', true);
            log(`Slot ${slot} bound ${meta.name} (${Object.keys(actions).length} clips)`);
        }

        playHeroAnim(slot, role, loop = true) {
            const rig = this.heroRigs[slot];
            if (!rig || !rig.actions) return;
            const candidates = rig.meta?.anims[role] || ['idle'];
            let chosen = null;
            let chosenName = 'idle';
            for (const c of candidates) {
                if (rig.actions[c.toLowerCase()]) {
                    chosen = rig.actions[c.toLowerCase()];
                    chosenName = c.toLowerCase();
                    break;
                }
            }
            if (!chosen) chosen = rig.actions['idle'] || Object.values(rig.actions)[0];
            if (!chosen || (loop && rig.currentClip === chosenName)) return;

            if (rig.currentAction && rig.currentAction !== chosen) {
                rig.currentAction.fadeOut(0.14);
            }
            chosen.reset();
            chosen.setLoop(loop ? THREE.LoopRepeat : THREE.LoopOnce);
            chosen.clampWhenFinished = !loop;
            chosen.fadeIn(0.12).play();
            rig.currentAction = chosen;
            rig.currentClip = chosenName;

            if (!loop) {
                const dur = (chosen.getClip()?.duration || 0.55) * 1000;
                setTimeout(() => {
                    if (this.heroRigs[slot] === rig) {
                        this.playHeroAnim(slot, 'idle', true);
                    }
                }, Math.min(dur, 1200));
            }
        }

        async prepareKongorBoss() {
            if (this.kongorRig) return;
            const gltf = await this.instantiateGlb('/assets/prebaked/models/kongor_default.glb');
            if (!gltf) return;
            const root = gltf.scene;
            this.normalizeModelToHeight(root, 1.85, gltf.animations || []);
            root.position.y = 2.5; // Starts above ready to slam down in Act IV
            root.visible = false;

            const mixer = new THREE.AnimationMixer(root);
            const actions = {};
            for (const clip of (gltf.animations || [])) {
                actions[clip.name.toLowerCase()] = mixer.clipAction(clip);
            }

            this.scene.add(root);
            this.kongorRig = {
                root,
                mixer,
                actions,
                slamTimer: 2.2,
                dropAnim: 0,
                shockwaveRadius: 0,
                shockwaveActive: false
            };
            log('Kongor Boss GLB cached & armed for Act IV');
        }

        // --- Balls & Physics ---
        spawnServeBall(servingSlot = 1, isIllusion = false) {
            const geo = new THREE.SphereGeometry(ARENA.ballRadius, 20, 16);
            const color = this.act === 1
                ? 0xf8fafc
                : (isIllusion ? 0xe879f9 : 0x38bdf8);
            const mat = new THREE.MeshStandardMaterial({
                color,
                emissive: color,
                emissiveIntensity: this.act === 1 ? 0.9 : 0.75,
                roughness: 0.2
            });
            const mesh = new THREE.Mesh(geo, mat);
            mesh.castShadow = true;

            const dirX = servingSlot === 1 ? 1 : -1;
            const angle = (Math.random() - 0.5) * 0.68;
            const speed = this.act === 1 ? 5.2 : 6.0 + Math.min(this.act * 0.45, 1.8);

            const ball = {
                mesh,
                x: dirX * -1.1,
                y: 0.24,
                z: isIllusion ? -0.75 : 0,
                vx: Math.cos(angle) * speed * dirX,
                vz: Math.sin(angle) * speed,
                spinZ: 0,
                lastHitter: servingSlot,
                isIllusion,
                parried: false,
                frozenInSphere: false,
                timeScale: 1
            };
            mesh.position.set(ball.x, ball.y, ball.z);
            this.scene.add(mesh);
            this.balls.push(ball);
            return ball;
        }

        clearAllBalls() {
            for (const b of this.balls) {
                this.scene.remove(b.mesh);
                b.mesh.geometry.dispose();
                b.mesh.material.dispose();
            }
            this.balls = [];
        }

        // --- Act Transitions & Screen Shatter ---
        transitionToAct2Visuals() {
            this.act = Math.max(this.act, 2);
            if (this.dom.crtOverlay) this.dom.crtOverlay.classList.add('hidden');
            this.scene.background = new THREE.Color(0x060b19);
            this.arenaMeshes.floor.material.color.setHex(0x0f172a);
            this.arenaMeshes.floor.material.roughness = 0.45;
            this.arenaMeshes.sanctumGrid.visible = true;
            this.arenaMeshes.northRail.material.color.setHex(0x38bdf8);
            this.arenaMeshes.southRail.material.color.setHex(0xf43f5e);

            // Switch camera smoothly from flat 1972 overhead to tilted 3D broadcast view
            this.camGoalPos.set(0, 6.9, 7.8);
            this.camGoalLook.set(0, 0.2, 0);
            this.updateHud();
        }

        triggerScreenShatter() {
            if (this.heroDraftTriggered) return;
            this.heroDraftTriggered = true;
            this.phase = 'hero_draft';
            this.audio.playOggUrl('/sounds/announcer/bamf/bullet_glass.ogg', 0.85);
            this.announceBanner('CHRONO SHATTER! CHOOSE YOUR CHAMPION');

            // Spawn 28 glowing 3D glass shards bursting from center screen
            for (let i = 0; i < 28; i++) {
                const shard = new THREE.Mesh(
                    new THREE.TetrahedronGeometry(0.18 + Math.random() * 0.2),
                    new THREE.MeshBasicMaterial({ color: 0xe0f2fe, transparent: true, opacity: 0.92, wireframe: i % 3 === 0 })
                );
                shard.position.set((Math.random() - 0.5) * 6, 1.5 + Math.random() * 2, (Math.random() - 0.5) * 3.5);
                this.scene.add(shard);
                this.shatterShards.push({
                    mesh: shard,
                    vx: (Math.random() - 0.5) * 5,
                    vy: 2 + Math.random() * 3.5,
                    vz: (Math.random() - 0.5) * 5,
                    rx: (Math.random() - 0.5) * 8,
                    ry: (Math.random() - 0.5) * 8,
                    life: 1.4
                });
            }

            this.transitionToAct2Visuals();
            this.openHeroDraftDialog();
        }

        openHeroDraftDialog() {
            this.phase = 'hero_draft';
            this.renderHeroGrid();
            openTopLayerDialog(this.dom.heroDraftDialog);
        }

        renderHeroGrid() {
            if (!this.dom.heroGrid) return;
            this.dom.heroGrid.innerHTML = '';
            Object.values(HERO_META).forEach((hero) => {
                const btn = document.createElement('button');
                btn.type = 'submit';
                btn.value = hero.id;
                btn.dataset.heroId = hero.id;
                btn.className = 'rb-hero-card group text-left p-4 rounded-2xl bg-slate-900/90 border border-slate-700 hover:border-cyan-400 focus-visible:border-cyan-300 transition-all cursor-pointer flex flex-col gap-1.5';
                btn.innerHTML = `
                    <div class="flex items-center justify-between gap-2">
                        <span class="text-base font-black text-white">${hero.name}</span>
                        <span class="text-[10px] font-black uppercase px-2 py-0.5 rounded-full border" style="color:${hero.cssColor}; border-color:${hero.cssColor}66; background:${hero.cssColor}1a;">${hero.ultName}</span>
                    </div>
                    <span class="text-[11px] font-semibold uppercase tracking-wider text-slate-400">${hero.title}</span>
                    <p class="text-xs text-slate-300 leading-snug mt-1">${hero.ultDesc}</p>
                `;
                this.dom.heroGrid.appendChild(btn);
            });
        }

        async selectHeroForSlot(slot, heroId) {
            if (!HERO_META[heroId]) return;
            this.heroes[slot] = heroId;
            await this.attachHeroModel(slot, heroId);
            this.updateHud();
        }

        async confirmHeroSelection(heroId) {
            if (!HERO_META[heroId]) return;
            this.audio.playOggUrl('/sounds/ui/select_unit.ogg', 0.8);

            await this.selectHeroForSlot(this.localSlot, heroId);

            if (this.mode === 'online' && this.hub) {
                if (this.dom.heroDraftStatus) {
                    this.dom.heroDraftStatus.textContent = `Locked ${HERO_META[heroId].name}! Waiting for opponent champion...`;
                }
                this.hub.invoke('SelectHero', this.roomCode, heroId).catch((e) => warn('SelectHero hub err', e));
                // Wait for OnHeroSelected(bothSelected=true) unless already set
                if (this.heroes[1] && this.heroes[2]) {
                    closeTopLayer(this.dom.heroDraftDialog, '');
                    this.resumeAfterHeroDraft();
                }
                return;
            }

            // Solo AI / Local 2P: pick complementary/counter hero for P2 automatically if unset
            const oppSlot = this.localSlot === 1 ? 2 : 1;
            if (!this.heroes[oppSlot]) {
                const pool = Object.keys(HERO_META).filter(id => id !== heroId);
                const oppHero = pool[Math.floor(Math.random() * pool.length)];
                await this.selectHeroForSlot(oppSlot, oppHero);
            }

            closeTopLayer(this.dom.heroDraftDialog, '');
            this.resumeAfterHeroDraft();
        }

        resumeAfterHeroDraft() {
            this.phase = 'playing';
            this.act = Math.max(this.act, 2);
            this.audio.playAnnouncer('get_it_on', 0.78);
            this.announceBanner(`ACT II: SANCTUM DUEL — ${HERO_META[this.heroes[1]]?.name || 'Hero'} vs ${HERO_META[this.heroes[2]]?.name || 'Hero'}! Press SPACE to Parry, E for Ult`);
            this.clearAllBalls();
            this.spawnServeBall(1);
            if (this.activeRiftCards.has('twin_illusion')) {
                this.spawnServeBall(2, true);
            }
            this.updateHud();
        }

        triggerCardDraft(pickerSlot = 1) {
            if (this.cardDraftTriggered) return;
            this.cardDraftTriggered = true;
            this.phase = 'card_draft';
            this.act = Math.max(this.act, 3);
            this.audio.playAnnouncer('prepare_for_battle', 0.75);

            if (this.dom.cardDraftSubtitle) {
                this.dom.cardDraftSubtitle.textContent =
                    `Trailing Champion (Player ${pickerSlot}) bends reality — select a permanent Act III Rift Modifier:`;
            }

            if (this.dom.cardGrid) {
                this.dom.cardGrid.innerHTML = '';
                Object.values(RIFT_CARDS).forEach((card) => {
                    const btn = document.createElement('button');
                    btn.type = 'submit';
                    btn.value = card.id;
                    btn.dataset.cardId = card.id;
                    btn.className = 'rb-rift-card text-left p-4 rounded-2xl bg-slate-900/95 border border-purple-500/40 hover:border-cyan-300 focus-visible:border-cyan-300 transition-all cursor-pointer flex flex-col gap-1.5';
                    btn.innerHTML = `
                        <span class="text-sm font-black text-cyan-300 uppercase tracking-wide">${card.name}</span>
                        <p class="text-xs text-slate-300 leading-snug">${card.desc}</p>
                    `;
                    this.dom.cardGrid.appendChild(btn);
                });
            }

            openTopLayerDialog(this.dom.cardDraftDialog);
            this.updateHud();
        }

        applyRiftCard(cardId) {
            const card = RIFT_CARDS[cardId];
            if (!card) return;
            this.activeRiftCards.add(cardId);
            this.act = Math.max(this.act, 3);
            this.audio.playOggUrl('/sounds/ui/ping.ogg', 0.75);
            this.audio.playAnnouncer(cardId === 'twin_illusion' ? 'powerup_illusion' : 'powerup_doubledamage', 0.72);

            if (cardId === 'portal_sidelines') {
                this.arenaMeshes.northRail.material.color.setHex(0x38bdf8);
                this.arenaMeshes.northRail.material.emissive.setHex(0x0284c7);
                this.arenaMeshes.northRail.material.emissiveIntensity = 0.85;
                this.arenaMeshes.southRail.material.color.setHex(0xfb923c);
                this.arenaMeshes.southRail.material.emissive.setHex(0xea580c);
                this.arenaMeshes.southRail.material.emissiveIntensity = 0.85;
            } else if (cardId === 'prism_obelisks' && this.prismObelisks.length === 0) {
                const locs = [[-1.55, -1.25], [1.55, 1.25]];
                for (const [ox, oz] of locs) {
                    const prism = new THREE.Mesh(
                        new THREE.CylinderGeometry(0.0, 0.34, 0.75, 3),
                        new THREE.MeshStandardMaterial({ color: 0x38bdf8, emissive: 0x06b6d4, emissiveIntensity: 0.7, roughness: 0.15, metalness: 0.8 })
                    );
                    prism.position.set(ox, 0.38, oz);
                    prism.castShadow = true;
                    this.scene.add(prism);
                    this.prismObelisks.push({ mesh: prism, x: ox, z: oz, r: 0.34 });
                }
            } else if (cardId === 'gravity_conduit') {
                this.arenaMeshes.gravityRing.visible = true;
            } else if (cardId === 'twin_illusion' && this.balls.length < 2) {
                this.spawnServeBall(2, true);
            }

            this.announceBanner(`RIFT RULE ACTIVE: ${card.name.toUpperCase()}!`);
            this.updateHud();
        }

        confirmRiftCard(cardId) {
            if (!RIFT_CARDS[cardId]) return;
            this.applyRiftCard(cardId);
            closeTopLayer(this.dom.cardDraftDialog, '');
            this.phase = 'playing';

            if (this.mode === 'online' && this.hub) {
                this.hub.invoke('SelectRiftCard', this.roomCode, cardId).catch((e) => warn('SelectRiftCard err', e));
            }
            if (this.balls.length === 0) {
                this.spawnServeBall(1);
                if (this.activeRiftCards.has('twin_illusion')) this.spawnServeBall(2, true);
            }
        }

        async triggerAct4BossOverdrive() {
            if (this.bossSpawned) return;
            this.bossSpawned = true;
            this.act = 4;
            this.audio.playAnnouncer('kongor_slain', 0.78);
            this.announceBanner('ACT IV: KONGOR CATACLYSM OVERDRIVE — FIRST TO 11 WINS!');

            if (!this.kongorRig) {
                await this.prepareKongorBoss();
            }
            if (this.kongorRig) {
                this.kongorRig.root.visible = true;
                this.kongorRig.root.position.set(0, 3.2, 0);
                this.kongorRig.dropAnim = 1.0;
                const taunt = this.kongorRig.actions['taunt_1'] || this.kongorRig.actions['ability_1'] || this.kongorRig.actions['idle'];
                taunt?.reset().play();
            }

            // Dramatic elevated corner angle for Kongor finale
            this.camGoalPos.set(0.4, 7.6, 8.6);
            this.camGoalLook.set(0, 0.35, 0);
            this.arenaMeshes.floor.material.color.setHex(0x160a14);
            this.arenaMeshes.hemiLight.intensity = 1.45;
            this.arenaMeshes.rimP1.intensity = 3.5;
            this.arenaMeshes.rimP2.intensity = 3.5;
            if (this.arenaMeshes.bossLight) this.arenaMeshes.bossLight.intensity = 7.5;
            this.updateHud();
        }

        // --- Gameplay Actions: Parry & Hero Ultimates ---
        attemptParry(slot) {
            if (this.phase !== 'playing' || this.paused) return;
            if (this.act < 2) return; // Act I is pure Pong!
            const p = this.players[slot];
            if (!p || p.parryCooldown > 0) return;

            p.parryTimer = 0.24;
            p.parryCooldown = 0.65;
            this.arenaMeshes.paddles[slot].parryArc.visible = true;
            this.playHeroAnim(slot, 'attack', false);
            this.audio.playPongBlip(520, 0.05, 'triangle', 0.06);
        }

        castHeroUltimate(slot) {
            if (this.phase !== 'playing' || this.paused || this.act < 2) return;
            const p = this.players[slot];
            const heroId = this.heroes[slot];
            if (!p || !heroId) return;
            if (p.energy < 100 && p.ultCooldown > 0) return;
            if (p.energy < 100) {
                if (slot === this.localSlot) {
                    this.announceBanner(`Need 100 Energy for ${HERO_META[heroId].ultName} (Parry shots to charge!)`);
                }
                return;
            }

            p.energy = 0;
            p.ultCooldown = 3.0;
            this.playHeroAnim(slot, 'ult', false);
            this.audio.playUltimateChime();

            const targetBall = this.balls[0] || { x: 0, z: 0 };
            this.spawnUltimateEffect(slot, heroId, p.x, p.z, targetBall.x, targetBall.z);

            if (this.mode === 'online' && slot === this.localSlot && this.hub) {
                this.hub.invoke('CastHeroUltimate', this.roomCode, slot, heroId, p.x, p.z, targetBall.x, targetBall.z)
                    .catch(() => {});
            }
            this.updateHud();
        }

        spawnUltimateEffect(slot, heroId, ox, oz, tx, tz) {
            const meta = HERO_META[heroId];
            this.announceBanner(`P${slot} ${meta.name}: ${meta.ultName.toUpperCase()}!`);

            if (heroId === 'chronos') {
                // Chronosphere dome freezes balls at 0.15x speed then launches volley
                const mesh = new THREE.Mesh(
                    new THREE.SphereGeometry(1.9, 24, 18),
                    new THREE.MeshBasicMaterial({ color: 0x22d3ee, wireframe: true, transparent: true, opacity: 0.65 })
                );
                mesh.position.set(tx * 0.45, 0.4, tz * 0.45);
                this.scene.add(mesh);
                this.activeUltEffects.push({
                    type: 'chronosphere', slot, mesh,
                    x: mesh.position.x, z: mesh.position.z, r: 1.9, life: 3.0
                });
            } else if (heroId === 'behemoth') {
                // Fissure angled rock wall across mid-court
                const angle = slot === 1 ? 0.45 : -0.45;
                const wall = new THREE.Mesh(
                    new THREE.BoxGeometry(0.28, 0.55, 2.2),
                    new THREE.MeshStandardMaterial({ color: 0xf59e0b, emissive: 0x9a3412, emissiveIntensity: 0.45, roughness: 0.8 })
                );
                const wx = slot === 1 ? 0.95 : -0.95;
                const wz = Math.max(-1.4, Math.min(1.4, tz));
                wall.position.set(wx, 0.28, wz);
                wall.rotation.y = angle;
                wall.castShadow = true;
                this.scene.add(wall);
                this.activeUltEffects.push({
                    type: 'fissure', slot, mesh: wall,
                    x: wx, z: wz, angle, halfLen: 1.1, life: 5.5
                });
            } else if (heroId === 'devourer') {
                this.audio.playOggUrl('/sounds/devourer/hook_cast.ogg', 0.78);
                // Meat Hook snatches active ball & yanks 90deg across lanes
                const ball = this.balls[0];
                if (ball) {
                    this.audio.playOggUrl('/sounds/devourer/hook_impact.ogg', 0.8);
                    const hookGroup = new THREE.Group();
                    const hookLine = new THREE.Mesh(
                        new THREE.CylinderGeometry(0.05, 0.05, Math.hypot(ball.x - ox, ball.z - oz), 8),
                        new THREE.MeshBasicMaterial({ color: 0xf43f5e })
                    );
                    hookLine.position.set((ball.x + ox) / 2, 0.35, (ball.z + oz) / 2);
                    hookLine.rotation.z = Math.PI / 2;
                    hookLine.rotation.y = -Math.atan2(ball.z - oz, ball.x - ox);
                    hookGroup.add(hookLine);
                    this.scene.add(hookGroup);

                    this.instantiateGlb('/assets/prebaked/models/devourer_hook.glb').then((hookGltf) => {
                        if (hookGltf && hookGroup.parent) {
                            const hMesh = hookGltf.scene;
                            this.normalizeModelToHeight(hMesh, 0.45);
                            hMesh.position.set(ball.x, 0.35, ball.z);
                            hookGroup.add(hMesh);
                        }
                    });
                    this.activeUltEffects.push({ type: 'hookline', mesh: hookGroup, life: 0.45 });

                    // 90-degree orthogonal trajectory kink!
                    const yankZ = ball.z >= 0 ? -Math.abs(ball.vx) * 0.7 : Math.abs(ball.vx) * 0.7;
                    ball.vz = yankZ;
                    ball.vx = (slot === 1 ? 1 : -1) * Math.max(Math.abs(ball.vx) * 1.25, 7.2);
                    ball.spinZ = (ball.z >= 0 ? -1 : 1) * 3.5;
                }
            } else if (heroId === 'bubbles') {
                this.players[slot].shellTimer = 1.6;
                this.arenaMeshes.paddles[slot].shellSphere.visible = true;
            } else if (heroId === 'flux') {
                const vortex = new THREE.Mesh(
                    new THREE.TorusGeometry(1.4, 0.06, 12, 36),
                    new THREE.MeshBasicMaterial({ color: 0x818cf8, transparent: true, opacity: 0.75 })
                );
                vortex.rotation.x = Math.PI / 2;
                vortex.position.set(slot === 1 ? 2.2 : -2.2, 0.08, 0);
                this.scene.add(vortex);
                this.activeUltEffects.push({
                    type: 'flux', slot, mesh: vortex,
                    x: vortex.position.x, z: 0, r: 2.3, life: 4.5
                });
            } else if (heroId === 'doctor_repulsor') {
                const p = this.players[slot];
                p.dashTimer = 1.2;
                p.x = slot === 1 ? ARENA.p1RushMaxX : ARENA.p2RushMinX;
                const bolt = new THREE.Mesh(
                    new THREE.BoxGeometry(2.6, 0.12, 0.25),
                    new THREE.MeshBasicMaterial({ color: 0xe879f9, transparent: true, opacity: 0.85 })
                );
                bolt.position.set((ox + p.x) / 2, 0.28, p.z);
                this.scene.add(bolt);
                this.activeUltEffects.push({ type: 'bolt', mesh: bolt, life: 0.55 });
            }
        }

        // --- Physics & Frame Step ---
        stepPhysics(dt) {
            if (this.phase !== 'playing' || this.paused) return;

            if (this.hitstopTimer > 0) {
                this.hitstopTimer -= dt;
                return;
            }

            // Passive energy trickle in Act II+
            if (this.act >= 2) {
                this.players[1].energy = Math.min(100, this.players[1].energy + dt * 11);
                this.players[2].energy = Math.min(100, this.players[2].energy + dt * 11);
            }

            this.stepPaddleControls(dt);
            this.stepUltEffects(dt);
            this.stepKongorBoss(dt);
            this.stepBalls(dt);
        }

        stepPaddleControls(dt) {
            // Player 1 (or localSlot in online)
            const mySlot = this.mode === 'online' ? this.localSlot : 1;
            const myP = this.players[mySlot];
            const minX = mySlot === 1 ? ARENA.p1BaseX : ARENA.p2RushMinX;
            const maxX = mySlot === 1 ? ARENA.p1RushMaxX : ARENA.p2BaseX;

            const prevX = myP.x;
            const prevZ = myP.z;

            let moveZ = 0;
            let moveX = 0;
            if (this.keys.has('KeyW') || (this.mode === 'ai' && this.keys.has('ArrowUp'))) moveZ -= 1;
            if (this.keys.has('KeyS') || (this.mode === 'ai' && this.keys.has('ArrowDown'))) moveZ += 1;
            if (this.act >= 2) {
                if (this.keys.has('KeyA') || (this.mode === 'ai' && this.keys.has('ArrowLeft'))) moveX -= 1;
                if (this.keys.has('KeyD') || (this.mode === 'ai' && this.keys.has('ArrowRight'))) moveX += 1;
            }

            const paddleSpeed = 7.4;
            if (moveZ !== 0 || moveX !== 0) {
                myP.z += moveZ * paddleSpeed * dt;
                if (this.act >= 2) myP.x += moveX * paddleSpeed * 0.78 * dt;
            } else if (this.pointerActive) {
                myP.z += (this.pointerWorld.z - myP.z) * Math.min(1, dt * 16);
                if (this.act >= 2) {
                    const desiredX = Math.max(minX, Math.min(maxX, this.pointerWorld.x));
                    myP.x += (desiredX - myP.x) * Math.min(1, dt * 10);
                }
            }

            if (this.act === 1) {
                myP.x = mySlot === 1 ? ARENA.p1BaseX : ARENA.p2BaseX;
            } else {
                myP.x = Math.max(minX, Math.min(maxX, myP.x));
            }
            myP.z = Math.max(-ARENA.halfD + ARENA.paddleHalfZ, Math.min(ARENA.halfD - ARENA.paddleHalfZ, myP.z));
            myP.vx = (myP.x - prevX) / Math.max(dt, 0.001);
            myP.vz = (myP.z - prevZ) / Math.max(dt, 0.001);

            if (this.heroRigs[mySlot]) {
                const moving = Math.hypot(myP.vx, myP.vz) > 0.6;
                if (myP.parryTimer <= 0) this.playHeroAnim(mySlot, moving ? 'walk' : 'idle', true);
            }

            // Player 2 (AI or Local 2P)
            if (this.mode === 'local2p') {
                const p2 = this.players[2];
                const p2PrevZ = p2.z;
                const p2PrevX = p2.x;
                let z2 = 0, x2 = 0;
                if (this.keys.has('ArrowUp')) z2 -= 1;
                if (this.keys.has('ArrowDown')) z2 += 1;
                if (this.act >= 2) {
                    if (this.keys.has('ArrowLeft')) x2 -= 1;
                    if (this.keys.has('ArrowRight')) x2 += 1;
                }
                p2.z = Math.max(-ARENA.halfD + ARENA.paddleHalfZ, Math.min(ARENA.halfD - ARENA.paddleHalfZ, p2.z + z2 * paddleSpeed * dt));
                p2.x = this.act === 1 ? ARENA.p2BaseX : Math.max(ARENA.p2RushMinX, Math.min(ARENA.p2BaseX, p2.x + x2 * paddleSpeed * 0.78 * dt));
                p2.vx = (p2.x - p2PrevX) / Math.max(dt, 0.001);
                p2.vz = (p2.z - p2PrevZ) / Math.max(dt, 0.001);
            } else if (this.mode === 'ai') {
                this.stepAiOpponent(dt);
            }

            // Tick cooldowns & update 3D transforms
            for (const slot of [1, 2]) {
                const p = this.players[slot];
                if (p.parryTimer > 0) {
                    p.parryTimer -= dt;
                    if (p.parryTimer <= 0) this.arenaMeshes.paddles[slot].parryArc.visible = false;
                }
                if (p.parryCooldown > 0) p.parryCooldown -= dt;
                if (p.ultCooldown > 0) p.ultCooldown -= dt;
                if (p.shellTimer > 0) {
                    p.shellTimer -= dt;
                    if (p.shellTimer <= 0) this.arenaMeshes.paddles[slot].shellSphere.visible = false;
                }
                if (p.dashTimer > 0) p.dashTimer -= dt;

                this.arenaMeshes.paddles[slot].group.position.set(p.x, 0, p.z);
            }

            // Network sync throttled ~30Hz
            if (this.mode === 'online' && this.hub && performance.now() - this.lastNetworkPaddleSent > 33) {
                this.lastNetworkPaddleSent = performance.now();
                this.hub.invoke('SendPaddleState', this.roomCode, mySlot, myP.x, myP.z, myP.vx, myP.vz, myP.parryTimer, myP.energy).catch(() => {});
            }
        }

        stepAiOpponent(dt) {
            const p2 = this.players[2];
            const prevX = p2.x;
            const prevZ = p2.z;
            const ball = this.balls[0];
            if (!ball) return;

            const tiers = {
                beginner: { speed: 4.1, error: 0.45, rush: false, parryRate: 0.25 },
                tactician: { speed: 6.2, error: 0.16, rush: true, parryRate: 0.55 },
                grandmaster: { speed: 8.1, error: 0.04, rush: true, parryRate: 0.85 }
            };
            const prof = tiers[this.aiTier] || tiers.tactician;

            let aimZ = ball.z + (ball.vx > 0 ? ball.vz * 0.18 : 0);
            aimZ = Math.max(-ARENA.halfD + ARENA.paddleHalfZ, Math.min(ARENA.halfD - ARENA.paddleHalfZ, aimZ));
            const dz = aimZ - p2.z;
            if (Math.abs(dz) > prof.error) {
                p2.z += Math.sign(dz) * Math.min(Math.abs(dz), prof.speed * dt);
            }

            if (this.act >= 2 && prof.rush) {
                const desiredX = ball.vx < 0 ? 2.55 : ARENA.p2BaseX - 0.3;
                p2.x += Math.sign(desiredX - p2.x) * Math.min(Math.abs(desiredX - p2.x), prof.speed * 0.45 * dt);
                p2.x = Math.max(ARENA.p2RushMinX, Math.min(ARENA.p2BaseX, p2.x));

                if (ball.vx > 0 && Math.abs(ball.x - p2.x) < 0.85 && Math.abs(ball.z - p2.z) < 0.72 && p2.parryCooldown <= 0) {
                    if (Math.random() < prof.parryRate) this.attemptParry(2);
                }
                if (p2.energy >= 100 && Math.random() < dt * 0.65) {
                    this.castHeroUltimate(2);
                }
            } else {
                p2.x = ARENA.p2BaseX;
            }

            p2.vx = (p2.x - prevX) / Math.max(dt, 0.001);
            p2.vz = (p2.z - prevZ) / Math.max(dt, 0.001);
            if (this.heroRigs[2] && p2.parryTimer <= 0) {
                this.playHeroAnim(2, Math.hypot(p2.vx, p2.vz) > 0.6 ? 'walk' : 'idle', true);
            }
        }

        stepUltEffects(dt) {
            for (let i = this.activeUltEffects.length - 1; i >= 0; i--) {
                const fx = this.activeUltEffects[i];
                fx.life -= dt;
                if (fx.type === 'chronosphere') {
                    fx.mesh.rotation.y += dt * 0.8;
                } else if (fx.type === 'flux') {
                    fx.mesh.rotation.z -= dt * 2.0;
                }

                if (fx.life <= 0) {
                    if (fx.type === 'chronosphere') {
                        // Release all suspended balls cleanly with burst speed
                        for (const b of this.balls) {
                            if (b.frozenInSphere) {
                                b.frozenInSphere = false;
                                b.timeScale = 1;
                                b.vx = (fx.slot === 1 ? 1 : -1) * Math.max(Math.abs(b.vx) * 1.4, 8.8);
                            }
                        }
                    }
                    this.scene.remove(fx.mesh);
                    fx.mesh.geometry?.dispose();
                    fx.mesh.material?.dispose();
                    this.activeUltEffects.splice(i, 1);
                }
            }
        }

        stepKongorBoss(dt) {
            if (!this.bossSpawned || !this.kongorRig || !this.kongorRig.root.visible) return;
            const k = this.kongorRig;

            if (k.dropAnim > 0) {
                k.dropAnim = Math.max(0, k.dropAnim - dt * 2.1);
                k.root.position.y = k.dropAnim * k.dropAnim * 3.2;
                if (k.dropAnim === 0) {
                    k.shockwaveActive = true;
                    k.shockwaveRadius = 0.5;
                    this.arenaMeshes.kongorShockwave.visible = true;
                }
            } else {
                k.slamTimer -= dt;
                if (k.slamTimer <= 0) {
                    k.slamTimer = 4.2; // Safe gentle interval > 4s
                    const stomp = k.actions['attack_2'] || k.actions['ability_1'] || k.actions['attack_1'];
                    stomp?.reset().setLoop(THREE.LoopOnce).play();
                    k.shockwaveActive = true;
                    k.shockwaveRadius = 0.5;
                    this.arenaMeshes.kongorShockwave.visible = true;
                    this.audio.playPongBlip(85, 0.22, 'triangle', 0.11);
                }
            }

            if (k.shockwaveActive) {
                k.shockwaveRadius += dt * 4.2;
                const scale = k.shockwaveRadius / 0.55;
                this.arenaMeshes.kongorShockwave.scale.set(scale, scale, 1);
                this.arenaMeshes.kongorShockwave.material.opacity = Math.max(0, 0.72 * (1 - k.shockwaveRadius / 4.2));
                if (k.shockwaveRadius >= 4.2) {
                    k.shockwaveActive = false;
                    this.arenaMeshes.kongorShockwave.visible = false;
                }
            }
        }

        stepBalls(dt) {
            for (let i = this.balls.length - 1; i >= 0; i--) {
                const b = this.balls[i];
                let localScale = 1;

                // Chronosphere time dilation check
                for (const fx of this.activeUltEffects) {
                    if (fx.type === 'chronosphere' && Math.hypot(b.x - fx.x, b.z - fx.z) < fx.r) {
                        localScale = 0.15;
                        b.frozenInSphere = true;
                    } else if (fx.type === 'flux' && Math.hypot(b.x - fx.x, b.z - fx.z) < fx.r) {
                        b.vz += (b.z >= 0 ? -1 : 1) * dt * 8.5;
                    }
                }
                b.timeScale = localScale;
                const effDt = dt * localScale;

                // Gravity Conduit Rift Card
                if (this.activeRiftCards.has('gravity_conduit')) {
                    const dist = Math.hypot(b.x, b.z);
                    if (dist > 0.35 && dist < 2.2) {
                        b.vz += (-b.z / dist) * effDt * 4.4;
                    }
                }

                // Magnus spin deflection
                b.vz += b.spinZ * effDt * 2.2;
                b.spinZ *= (1 - dt * 0.45);

                b.x += b.vx * effDt;
                b.z += b.vz * effDt;

                // North & South Walls (Normal Bounce or Portal Wrap!)
                const wallLimit = ARENA.halfD - ARENA.ballRadius;
                if (b.z > wallLimit || b.z < -wallLimit) {
                    if (this.activeRiftCards.has('portal_sidelines') && this.act >= 3) {
                        b.z = b.z > wallLimit ? -wallLimit + 0.08 : wallLimit - 0.08;
                        this.audio.playPongBlip(740, 0.06, 'sine', 0.07);
                    } else {
                        b.z = Math.sign(b.z) * wallLimit;
                        b.vz *= -1;
                        this.audio.playPongBlip(this.act === 1 ? 440 : 520, 0.04, this.act === 1 ? 'square' : 'triangle', 0.06);
                    }
                }

                // Prism Obelisks collisions
                for (const ob of this.prismObelisks) {
                    const dx = b.x - ob.x;
                    const dz = b.z - ob.z;
                    const d = Math.hypot(dx, dz);
                    if (d < ob.r + ARENA.ballRadius) {
                        const nx = dx / Math.max(d, 0.001);
                        const nz = dz / Math.max(d, 0.001);
                        const dot = b.vx * nx + b.vz * nz;
                        b.vx -= 2 * dot * nx;
                        b.vz -= 2 * dot * nz;
                        b.x = ob.x + nx * (ob.r + ARENA.ballRadius + 0.04);
                        b.z = ob.z + nz * (ob.r + ARENA.ballRadius + 0.04);
                        this.audio.playPongBlip(880, 0.06, 'sine', 0.08);
                    }
                }

                // Behemoth Fissure wall collision
                for (const fx of this.activeUltEffects) {
                    if (fx.type === 'fissure') {
                        const relX = b.x - fx.x;
                        const relZ = b.z - fx.z;
                        const cosA = Math.cos(-fx.angle);
                        const sinA = Math.sin(-fx.angle);
                        const lx = relX * cosA - relZ * sinA;
                        const lz = relX * sinA + relZ * cosA;
                        if (Math.abs(lx) < 0.25 + ARENA.ballRadius && Math.abs(lz) < fx.halfLen) {
                            b.vx *= -1.12;
                            b.vz += Math.sin(fx.angle) * 4.5;
                            b.x += Math.sign(b.vx) * 0.22;
                            this.audio.playPongBlip(180, 0.08, 'sawtooth', 0.09);
                        }
                    }
                }

                // Kongor boss center collision
                if (this.bossSpawned && this.kongorRig && this.kongorRig.dropAnim === 0) {
                    const kd = Math.hypot(b.x, b.z);
                    if (kd < 0.56) {
                        const nx = b.x / Math.max(kd, 0.001);
                        const nz = b.z / Math.max(kd, 0.001);
                        const spd = Math.max(Math.hypot(b.vx, b.vz), 6.8);
                        b.vx = nx * spd;
                        b.vz = nz * spd;
                        b.x = nx * 0.62;
                        b.z = nz * 0.62;
                    }
                }

                // Paddle Collisions
                for (const slot of [1, 2]) {
                    const p = this.players[slot];
                    const hitReachX = p.shellTimer > 0 ? ARENA.paddleHalfX + 0.52 : ARENA.paddleHalfX + ARENA.ballRadius + (p.parryTimer > 0 ? 0.22 : 0.04);
                    const hitReachZ = p.shellTimer > 0 ? ARENA.paddleHalfZ + 0.52 : ARENA.paddleHalfZ + ARENA.ballRadius + (p.parryTimer > 0 ? 0.24 : 0.04);
                    const movingToward = (slot === 1 && b.vx < 0) || (slot === 2 && b.vx > 0);

                    if (movingToward && Math.abs(b.x - p.x) < hitReachX && Math.abs(b.z - p.z) < hitReachZ) {
                        const relOffset = (b.z - p.z) / ARENA.paddleHalfZ;
                        const parried = p.parryTimer > 0 || p.shellTimer > 0;
                        const boost = parried ? (p.shellTimer > 0 ? 1.55 : 1.32) : 1.06;
                        const baseSpeed = Math.min(13.5, Math.hypot(b.vx, b.vz) * boost);
                        const outDir = slot === 1 ? 1 : -1;

                        b.vx = outDir * Math.max(Math.abs(Math.cos(relOffset * 0.72) * baseSpeed), 5.2);
                        b.vz = relOffset * baseSpeed * 0.68 + p.vz * 0.24;
                        b.spinZ = parried ? p.vz * 0.55 : p.vz * 0.18;
                        b.parried = parried;
                        b.lastHitter = slot;
                        b.x = p.x + outDir * (hitReachX + 0.05);

                        this.rallyCount++;
                        this.longestRally = Math.max(this.longestRally, this.rallyCount);

                        if (parried) {
                            this.parriesLanded[slot - 1]++;
                            p.energy = Math.min(100, p.energy + 35);
                            p.parryCooldown = 0.05; // Instant reward reset on sweet-spot parry
                            if (!this.comfortMode) {
                                this.hitstopTimer = 0.032;
                                this.shakeIntensity = 0.14;
                            }
                            this.audio.playParryChime();
                            b.mesh.material.color.setHex(0xfacc15);
                        } else {
                            p.energy = Math.min(100, p.energy + 12);
                            this.audio.playPongBlip(this.act === 1 ? 587 : 640, 0.055, this.act === 1 ? 'square' : 'triangle', 0.08);
                            b.mesh.material.color.setHex(this.act === 1 ? 0xf8fafc : (b.isIllusion ? 0xe879f9 : 0x38bdf8));
                        }

                        if (this.mode === 'online' && this.hub && slot === this.localSlot) {
                            this.hub.invoke('SyncBallRally', this.roomCode, slot, b.x, b.y, b.z, b.vx, 0, b.vz, b.spinZ, this.rallyCount, parried)
                                .catch(() => {});
                        }
                    }
                }

                b.mesh.position.set(b.x, b.y, b.z);

                // Goal Check!
                if (b.x > ARENA.halfW + 0.18) {
                    this.handleGoalScored(1, i);
                    break;
                } else if (b.x < -ARENA.halfW - 0.18) {
                    this.handleGoalScored(2, i);
                    break;
                }
            }
        }

        handleGoalScored(scorerSlot, ballIndex) {
            this.rallyCount = 0;
            this.balls.splice(ballIndex, 1).forEach(b => {
                this.scene.remove(b.mesh);
                b.mesh.geometry.dispose();
                b.mesh.material.dispose();
            });

            if (this.mode === 'online' && this.hub) {
                if (scorerSlot === this.localSlot) {
                    this.hub.invoke('ReportPointScored', this.roomCode, scorerSlot).catch(() => {});
                }
                return;
            }

            this.applyScoreUpdate(scorerSlot);
        }

        applyScoreUpdate(scorerSlot, forcedS1 = null, forcedS2 = null) {
            if (forcedS1 !== null && forcedS2 !== null) {
                this.scores[0] = forcedS1;
                this.scores[1] = forcedS2;
            } else {
                this.scores[scorerSlot - 1]++;
            }

            this.streak[scorerSlot - 1]++;
            this.streak[scorerSlot === 1 ? 1 : 0] = 0;
            this.playHeroAnim(scorerSlot, 'taunt', false);

            const totalScore = this.scores[0] + this.scores[1];
            const maxScore = Math.max(this.scores[0], this.scores[1]);

            if (this.act === 1) {
                this.audio.playPongBlip(220, 0.22, 'square', 0.10);
            } else {
                const st = this.streak[scorerSlot - 1];
                if (totalScore === 3) this.audio.playAnnouncer('first_blood', 0.78);
                else if (st === 2) this.audio.playAnnouncer('double_kill', 0.78);
                else if (st === 3) this.audio.playAnnouncer('triple_kill', 0.78);
                else if (st >= 5) this.audio.playAnnouncer('annihilation', 0.8);
                else this.audio.playAnnouncer('denied', 0.68);
            }

            this.updateHud();

            if (maxScore >= ARENA.winningScore) {
                this.finishMatch(this.scores[0] >= ARENA.winningScore ? 1 : 2);
                return;
            }

            if (!this.heroDraftTriggered && (totalScore >= 2 || maxScore >= 2)) {
                this.clearAllBalls();
                this.triggerScreenShatter();
                return;
            }

            if (!this.cardDraftTriggered && (maxScore >= 6 || totalScore >= 8)) {
                this.clearAllBalls();
                const trailing = this.scores[0] <= this.scores[1] ? 1 : 2;
                this.triggerCardDraft(trailing);
                return;
            }

            if (!this.bossSpawned && maxScore >= 9) {
                this.triggerAct4BossOverdrive();
            }

            if (this.balls.length === 0) {
                this.spawnServeBall(scorerSlot === 1 ? 2 : 1);
                if (this.activeRiftCards.has('twin_illusion')) {
                    this.spawnServeBall(scorerSlot, true);
                }
            }
        }

        finishMatch(winnerSlot) {
            this.phase = 'victory';
            this.winnerSlot = winnerSlot;
            this.clearAllBalls();
            const isLocalWin = winnerSlot === this.localSlot || (this.mode !== 'online' && winnerSlot === 1);
            this.audio.playAnnouncer(isLocalWin ? 'victory' : 'defeat', 0.82);

            const winnerName = winnerSlot === 1
                ? (this.dom.name1?.textContent || 'Player 1')
                : (this.dom.name2?.textContent || 'Player 2');

            if (this.dom.victoryTitle) {
                this.dom.victoryTitle.textContent = `${winnerName} Breaks the Rift!`;
            }
            if (this.dom.victoryScore) {
                this.dom.victoryScore.textContent = `Final Score: ${this.scores[0]} — ${this.scores[1]} (Act ${this.act})`;
            }
            if (this.dom.victoryStats) {
                this.dom.victoryStats.textContent =
                    `Longest Rally: ${this.longestRally} hits  •  Parries: ${this.parriesLanded[0]} vs ${this.parriesLanded[1]}`;
            }

            openTopLayerDialog(this.dom.victoryDialog);
            this.updateHud();
        }

        async resetMatch(skipIntro = false) {
            closeTopLayer(this.dom.victoryDialog);
            closeTopLayer(this.dom.heroDraftDialog);
            closeTopLayer(this.dom.cardDraftDialog);

            this.clearAllBalls();
            for (const fx of this.activeUltEffects) {
                this.scene.remove(fx.mesh);
                fx.mesh.geometry?.dispose();
                fx.mesh.material?.dispose();
            }
            this.activeUltEffects = [];
            for (const ob of this.prismObelisks) {
                this.scene.remove(ob.mesh);
                ob.mesh.geometry?.dispose();
                ob.mesh.material?.dispose();
            }
            this.prismObelisks = [];

            this.scores = [0, 0];
            this.streak = [0, 0];
            this.rallyCount = 0;
            this.longestRally = 0;
            this.parriesLanded = [0, 0];
            this.heroDraftTriggered = false;
            this.cardDraftTriggered = false;
            this.bossSpawned = false;
            this.winnerSlot = null;
            this.activeRiftCards.clear();
            this.arenaMeshes.gravityRing.visible = false;
            this.arenaMeshes.kongorShockwave.visible = false;
            if (this.kongorRig) this.kongorRig.root.visible = false;

            if (this.mode === 'online' && this.hub) {
                this.hub.invoke('ResetMatch', this.roomCode, skipIntro).catch(() => {});
            }

            if (skipIntro) {
                await this.jumpStraightToHeroDraft();
            } else {
                this.act = 1;
                this.phase = 'playing';
                this.heroes = { 1: null, 2: null };
                this.players[1].x = ARENA.p1BaseX;
                this.players[2].x = ARENA.p2BaseX;
                this.camGoalPos.set(0, 12.6, 0.01);
                this.camGoalLook.set(0, 0, 0);
                this.scene.background = new THREE.Color(0x03050a);
                this.arenaMeshes.floor.material.color.setHex(0x04070d);
                this.arenaMeshes.sanctumGrid.visible = false;
                if (this.dom.crtOverlay) this.dom.crtOverlay.classList.remove('hidden');
                this.spawnServeBall(1);
                this.announceBanner('SYSTEM REBOOT: TERMINAL TENNIS v1.972');
            }

            this.updateHud();
        }

        async jumpStraightToHeroDraft() {
            this.act = 2;
            this.heroDraftTriggered = true;
            this.transitionToAct2Visuals();
            this.camCurrentPos.copy(this.camGoalPos);
            this.openHeroDraftDialog();
        }

        // --- SignalR 1v1 Multiplayer ---
        async connectOnlineRoom(roomCodeInput, nameInput) {
            if (typeof global.signalR === 'undefined') {
                this.announceBanner('SignalR client unavailable offline — using Solo Tactician AI');
                return;
            }
            this.roomCode = (roomCodeInput || this.roomCode || 'RIFT').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8) || 'RIFT';
            this.playerName = (nameInput || this.playerName || 'Champion').trim().slice(0, 24);
            this.mode = 'online';

            if (this.hub) {
                try { await this.hub.stop(); } catch (_) {}
            }

            this.hub = new global.signalR.HubConnectionBuilder()
                .withUrl('/hubs/riftbreaker')
                .withAutomaticReconnect()
                .build();

            this.wireHubCallbacks();

            try {
                await this.hub.start();
                await this.hub.invoke('JoinRoom', this.roomCode, this.playerName, this.playerId);
                log('Connected to SignalR /hubs/riftbreaker room:', this.roomCode);
            } catch (err) {
                warn('SignalR connection failed:', err);
                if (this.dom.onlineStatusBanner) {
                    this.dom.onlineStatusBanner.textContent = 'Connection failed — check network or switch to Solo AI.';
                }
            }
        }

        wireHubCallbacks() {
            const h = this.hub;
            h.on('OnRoomJoined', async (code, slot, p1Name, p2Name, s1, s2, act, status, p1Hero, p2Hero, cards) => {
                this.roomCode = code;
                this.localSlot = slot;
                this.scores = [s1, s2];
                this.act = act;
                if (this.dom.name1) this.dom.name1.textContent = p1Name || 'Player 1';
                if (this.dom.name2) this.dom.name2.textContent = p2Name || 'Waiting...';
                if (p1Hero) await this.selectHeroForSlot(1, p1Hero);
                if (p2Hero) await this.selectHeroForSlot(2, p2Hero);
                for (const c of (cards || [])) this.applyRiftCard(c);
                if (this.dom.onlineStatusBanner) {
                    this.dom.onlineStatusBanner.textContent = `Joined Room ${code} as Player ${slot} (${status})`;
                }
                h.invoke('SetReady', code, this.arActive).catch(() => {});
                this.updateHud();
            });

            h.on('OnOpponentJoined', (slot, name) => {
                if (slot === 1 && this.dom.name1) this.dom.name1.textContent = name;
                if (slot === 2 && this.dom.name2) this.dom.name2.textContent = name;
                this.announceBanner(`${name} joined Room ${this.roomCode}!`);
            });

            h.on('OnMatchStarted', () => {
                closeTopLayer(this.dom.modeDialog);
                this.phase = 'playing';
                if (this.balls.length === 0) this.spawnServeBall(1);
                this.announceBanner('1v1 MATCH LIVE!');
            });

            h.on('OnOpponentPaddleState', (slot, x, z, vx, vz, parryTimer, energy) => {
                if (slot === this.localSlot) return;
                const p = this.players[slot];
                if (!p) return;
                p.x = x; p.z = z; p.vx = vx; p.vz = vz; p.parryTimer = parryTimer; p.energy = energy;
                this.arenaMeshes.paddles[slot].parryArc.visible = parryTimer > 0;
            });

            h.on('OnBallRallySynced', (authSlot, x, y, z, vx, vy, vz, spinZ, rally, parried) => {
                if (authSlot === this.localSlot) return;
                if (this.balls.length === 0) this.spawnServeBall(authSlot);
                const b = this.balls[0];
                b.x = x; b.y = y; b.z = z; b.vx = vx; b.vz = vz; b.spinZ = spinZ; b.parried = parried;
                this.rallyCount = rally;
            });

            h.on('OnHeroUltimateCast', (slot, heroId, ox, oz, tx, tz) => {
                if (slot === this.localSlot) return;
                this.playHeroAnim(slot, 'ult', false);
                this.spawnUltimateEffect(slot, heroId, ox, oz, tx, tz);
            });

            h.on('OnScoreUpdated', (s1, s2, scorerSlot, act) => {
                this.applyScoreUpdate(scorerSlot, s1, s2);
                if (act >= 2 && this.act < 2) this.transitionToAct2Visuals();
            });

            h.on('OnHeroDraftTriggered', () => {
                this.triggerScreenShatter();
            });

            h.on('OnHeroSelected', async (slot, heroId, bothSelected, p1Hero, p2Hero) => {
                if (p1Hero) await this.selectHeroForSlot(1, p1Hero);
                if (p2Hero) await this.selectHeroForSlot(2, p2Hero);
                if (bothSelected) {
                    closeTopLayer(this.dom.heroDraftDialog, heroId);
                    this.resumeAfterHeroDraft();
                }
            });

            h.on('OnCardDraftTriggered', (pickerSlot) => {
                if (pickerSlot === this.localSlot) {
                    this.triggerCardDraft(pickerSlot);
                } else {
                    this.announceBanner(`Player ${pickerSlot} is drafting a Tactical Rift Card...`);
                }
            });

            h.on('OnRiftCardActivated', (slot, cardId) => {
                this.applyRiftCard(cardId);
                closeTopLayer(this.dom.cardDraftDialog);
                this.phase = 'playing';
            });

            h.on('OnBossOverdriveTriggered', () => {
                this.triggerAct4BossOverdrive();
            });

            h.on('OnMatchWon', (winnerSlot, s1, s2) => {
                this.scores = [s1, s2];
                this.finishMatch(winnerSlot);
            });
        }

        // --- AR Webcam Passthrough ---
        async toggleArCamera() {
            if (this.arActive) {
                if (this.arStream) {
                    this.arStream.getTracks().forEach(t => t.stop());
                    this.arStream = null;
                }
                if (this.dom.video) {
                    this.dom.video.srcObject = null;
                    this.dom.video.classList.add('hidden');
                }
                this.arActive = false;
                this.scene.background = new THREE.Color(this.act === 1 ? 0x03050a : 0x060b19);
                this.announceBanner('Tabletop AR Webcam disabled');
                return;
            }

            try {
                this.arStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
                if (this.dom.video) {
                    this.dom.video.srcObject = this.arStream;
                    this.dom.video.classList.remove('hidden');
                }
                this.arActive = true;
                this.scene.background = null; // Transparent WebGL2 over camera feed
                this.announceBanner('Tabletop AR Mixed Reality Active!');
            } catch (err) {
                warn('Webcam unavailable:', err);
                this.announceBanner('Camera permission unavailable on this device');
            }
        }

        // --- UI & Input Wiring (Modern Web Guidance Compliant) ---
        bindInputs() {
            window.addEventListener('keydown', (e) => {
                if (e.target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName)) return;
                this.keys.add(e.code);
                if (e.code === 'Space') {
                    e.preventDefault();
                    this.attemptParry(this.mode === 'online' ? this.localSlot : 1);
                } else if (e.code === 'KeyE' || e.code === 'KeyG') {
                    this.castHeroUltimate(this.mode === 'online' ? this.localSlot : 1);
                } else if (e.code === 'KeyF') {
                    this.attemptParry(1);
                } else if (e.code === 'ShiftRight' || e.code === 'Slash') {
                    if (this.mode === 'local2p') this.attemptParry(2);
                } else if (e.code === 'Enter') {
                    if (this.mode === 'local2p') this.castHeroUltimate(2);
                } else if (e.code === 'KeyP') {
                    this.togglePause();
                } else if (e.code === 'KeyT') {
                    this.playHeroAnim(this.localSlot, 'taunt', false);
                    this.audio.playAnnouncer('smackdown', 0.72);
                }
            });

            window.addEventListener('keyup', (e) => {
                this.keys.delete(e.code);
            });

            const raycaster = new THREE.Raycaster();
            const groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
            const canvas = this.dom.canvas;

            const updatePointer = (clientX, clientY) => {
                const rect = canvas.getBoundingClientRect();
                const ndc = new THREE.Vector2(
                    ((clientX - rect.left) / rect.width) * 2 - 1,
                    -((clientY - rect.top) / rect.height) * 2 + 1
                );
                raycaster.setFromCamera(ndc, this.camera);
                const hit = new THREE.Vector3();
                if (raycaster.ray.intersectPlane(groundPlane, hit)) {
                    this.pointerWorld.copy(hit);
                    this.pointerActive = true;
                }
            };

            canvas.addEventListener('pointermove', (e) => updatePointer(e.clientX, e.clientY));
            canvas.addEventListener('pointerdown', (e) => {
                updatePointer(e.clientX, e.clientY);
                if (e.button === 0) this.attemptParry(this.mode === 'online' ? this.localSlot : 1);
                else if (e.button === 2) this.castHeroUltimate(this.mode === 'online' ? this.localSlot : 1);
            });
            canvas.addEventListener('contextmenu', (e) => e.preventDefault());

            // Modern Web Guidance: Suspend RAF/Audio when hidden OR offscreen via content-visibility
            document.addEventListener('visibilitychange', () => {
                this.isTabHidden = document.hidden;
                log(`visibilitychange -> hidden=${this.isTabHidden}`);
            });

            if (this.dom.stage) {
                this.dom.stage.addEventListener('contentvisibilityautostatechange', (e) => {
                    this.isStageSkipped = Boolean(e.skipped);
                    log(`contentvisibilityautostatechange -> skipped=${this.isStageSkipped}`);
                });
            }
        }

        bindUi() {
            const {
                heroDraftDialog, cardDraftDialog, modeDialog, victoryDialog, settingsPopover,
                selectAnnouncer, selectAiTier, chkSkipIntro
            } = this.dom;

            // Dialog form returns + required dialog.returnValue reset (Modern Web Guidance 'dialog')
            heroDraftDialog?.addEventListener('close', () => {
                const picked = heroDraftDialog.returnValue;
                heroDraftDialog.returnValue = '';
                if (this.phase !== 'hero_draft') return;
                if (picked && HERO_META[picked]) {
                    this.confirmHeroSelection(picked);
                } else if (!this.heroes[this.localSlot]) {
                    this.confirmHeroSelection('chronos');
                }
            });

            cardDraftDialog?.addEventListener('close', () => {
                const picked = cardDraftDialog.returnValue;
                cardDraftDialog.returnValue = '';
                if (this.phase !== 'card_draft') return;
                if (picked && RIFT_CARDS[picked]) {
                    this.confirmRiftCard(picked);
                } else {
                    this.confirmRiftCard('portal_sidelines');
                }
            });

            victoryDialog?.addEventListener('close', () => {
                const action = victoryDialog.returnValue;
                victoryDialog.returnValue = '';
                if (action === 'rematch_quick') this.resetMatch(true);
                else if (action === 'rematch_full') this.resetMatch(false);
            });

            modeDialog?.addEventListener('close', () => {
                modeDialog.returnValue = '';
            });

            // Light-dismiss backdrop fallback only for browsers lacking HTMLDialogElement closedby="any"
            if (!('closedBy' in HTMLDialogElement.prototype)) {
                let downOutside = false;
                modeDialog?.addEventListener('pointerdown', (e) => {
                    downOutside = e.target === modeDialog;
                });
                modeDialog?.addEventListener('pointerup', (e) => {
                    if (downOutside && e.target === modeDialog) closeTopLayer(modeDialog, 'dismiss');
                    downOutside = false;
                });
            }

            // Custom Command Actions ('command' event on target elements + progressive fallback < 20 lines)
            const wireCommandTarget = (target, handler) => {
                if (!target) return;
                target.addEventListener('command', (e) => handler(e.command, e.source));
            };

            wireCommandTarget(this.dom.stage, (cmd) => {
                if (cmd === '--pause-toggle') this.togglePause();
                else if (cmd === '--comfort-toggle') this.toggleComfortMode();
                else if (cmd === '--parry') this.attemptParry(this.mode === 'online' ? this.localSlot : 1);
                else if (cmd === '--cast-ult') this.castHeroUltimate(this.mode === 'online' ? this.localSlot : 1);
                else if (cmd === '--open-mode-dialog') openTopLayerDialog(modeDialog);
                else if (cmd === '--reset-full') this.resetMatch(false);
                else if (cmd === '--reset-draft') this.resetMatch(true);
            });

            wireCommandTarget(modeDialog, (cmd) => {
                if (cmd === '--show-mode') openTopLayerDialog(modeDialog);
                else if (cmd === '--close-mode') closeTopLayer(modeDialog);
            });

            // Fallback when Invoker Commands API ('commandfor') is absent in older browsers
            if (!('commandForElement' in HTMLButtonElement.prototype)) {
                document.addEventListener('click', (e) => {
                    const btn = e.target.closest('button[commandfor][command]');
                    if (!btn) return;
                    const target = document.getElementById(btn.getAttribute('commandfor'));
                    if (target) {
                        const ev = new Event('command');
                        ev.command = btn.getAttribute('command');
                        ev.source = btn;
                        target.dispatchEvent(ev);
                    }
                });
            }

            selectAnnouncer?.addEventListener('change', () => {
                this.audio.setPack(selectAnnouncer.value);
                this.audio.playAnnouncer('get_it_on', 0.72);
            });

            selectAiTier?.addEventListener('change', () => {
                this.aiTier = selectAiTier.value;
                this.announceBanner(`AI Difficulty set to ${this.aiTier.toUpperCase()}`);
            });

            chkSkipIntro?.addEventListener('change', () => {
                this.skipIntroNext = Boolean(chkSkipIntro.checked);
            });

            this.dom.btnToggleAudio?.addEventListener('click', () => {
                this.audio.muted = !this.audio.muted;
                this.dom.btnToggleAudio.setAttribute('aria-pressed', String(!this.audio.muted));
                this.dom.btnToggleAudio.textContent = this.audio.muted ? 'Audio: Muted' : 'Audio: On';
            });

            this.dom.btnToggleSynth?.addEventListener('click', () => {
                this.audio.synthEnabled = !this.audio.synthEnabled;
                this.dom.btnToggleSynth.setAttribute('aria-pressed', String(this.audio.synthEnabled));
                this.dom.btnToggleSynth.textContent = this.audio.synthEnabled ? 'Act IV Synth: On' : 'Act IV Synth: Off';
            });

            this.dom.btnToggleAr?.addEventListener('click', () => {
                this.toggleArCamera();
                closeTopLayer(settingsPopover);
            });

            // Mode switcher buttons inside #modeDialog
            document.getElementById('btnSelectSoloAi')?.addEventListener('click', () => {
                this.mode = 'ai';
                this.localSlot = 1;
                if (this.dom.name1) this.dom.name1.textContent = this.playerName;
                if (this.dom.name2) this.dom.name2.textContent = `AI (${this.aiTier})`;
                closeTopLayer(modeDialog);
                this.resetMatch(this.skipIntroNext);
            });

            document.getElementById('btnSelectLocal2P')?.addEventListener('click', () => {
                this.mode = 'local2p';
                this.localSlot = 1;
                if (this.dom.name1) this.dom.name1.textContent = 'Player 1 (WASD)';
                if (this.dom.name2) this.dom.name2.textContent = 'Player 2 (Arrows)';
                closeTopLayer(modeDialog);
                this.resetMatch(this.skipIntroNext);
            });

            document.getElementById('btnJoinOnlineRoom')?.addEventListener('click', () => {
                const roomVal = this.dom.inputRoomCode?.value || this.roomCode;
                const nameVal = this.dom.inputPlayerName?.value || this.playerName;
                this.connectOnlineRoom(roomVal, nameVal);
            });
        }

        togglePause() {
            this.paused = !this.paused;
            if (this.dom.btnPause) {
                this.dom.btnPause.textContent = this.paused ? 'Resume' : 'Pause';
                this.dom.btnPause.setAttribute('aria-label', this.paused ? 'Resume Match' : 'Pause Match');
            }
            this.announceBanner(this.paused ? 'Match Paused' : 'Match Resumed');
        }

        toggleComfortMode() {
            this.comfortMode = !this.comfortMode;
            if (this.dom.btnComfort) {
                this.dom.btnComfort.setAttribute('aria-pressed', String(this.comfortMode));
                this.dom.btnComfort.textContent = this.comfortMode ? 'CalmFX: On' : 'CalmFX: Off';
            }
            document.documentElement.dataset.comfort = this.comfortMode ? 'true' : 'false';
            this.announceBanner(this.comfortMode ? 'Comfort Mode enabled (camera shake & pulses reduced)' : 'Comfort Mode off');
        }

        announceBanner(msg) {
            if (!this.dom.toast || !this.dom.toastText) return;
            this.dom.toastText.textContent = msg;
            this.dom.toast.dataset.visible = 'true';
            clearTimeout(this._toastTimer);
            this._toastTimer = setTimeout(() => {
                this.dom.toast.dataset.visible = 'false';
            }, 3400);
        }

        updateHud() {
            if (this.dom.score1) this.dom.score1.textContent = String(this.scores[0]);
            if (this.dom.score2) this.dom.score2.textContent = String(this.scores[1]);

            const actLabels = {
                1: 'ACT I · TERMINAL TENNIS 1972',
                2: 'ACT II · 3D SANCTUM DUEL',
                3: 'ACT III · RIFT SHIFT',
                4: 'ACT IV · KONGOR OVERDRIVE'
            };
            if (this.dom.actBadge) this.dom.actBadge.textContent = actLabels[this.act] || 'ACT I';

            for (const slot of [1, 2]) {
                const h = this.heroes[slot] ? HERO_META[this.heroes[slot]] : null;
                const tagEl = slot === 1 ? this.dom.heroTag1 : this.dom.heroTag2;
                if (tagEl) tagEl.textContent = h ? `${h.name} · ${h.ultName}` : (this.act === 1 ? 'CRT Paddle' : 'Unbound');

                const eBar = slot === 1 ? this.dom.energyBar1 : this.dom.energyBar2;
                const pct = Math.round(this.players[slot].energy);
                if (eBar) eBar.style.width = `${pct}%`;
            }

            const mySlot = this.mode === 'online' ? this.localSlot : 1;
            const myHero = this.heroes[mySlot] ? HERO_META[this.heroes[mySlot]] : null;
            const myEnergy = Math.round(this.players[mySlot].energy);
            if (this.dom.energyText1) this.dom.energyText1.textContent = `${myEnergy}%`;
            if (this.dom.btnUltLabel) {
                this.dom.btnUltLabel.textContent = myHero
                    ? (myEnergy >= 100 ? `${myHero.ultName} READY!` : myHero.ultName)
                    : 'Hero Ult (Act II)';
            }
            if (this.dom.btnUlt) {
                this.dom.btnUlt.disabled = this.act < 2 || myEnergy < 100;
            }
            if (this.dom.btnParry) {
                this.dom.btnParry.disabled = this.act < 2;
            }

            if (this.dom.riftChips) {
                this.dom.riftChips.innerHTML = '';
                this.activeRiftCards.forEach((cid) => {
                    const chip = document.createElement('span');
                    chip.className = 'px-2 py-0.5 rounded-full bg-fuchsia-500/20 border border-fuchsia-400/50 text-[10px] font-bold text-fuchsia-200';
                    chip.textContent = RIFT_CARDS[cid]?.name || cid;
                    this.dom.riftChips.appendChild(chip);
                });
            }
        }

        // --- Animation & Render Loop ---
        startLoop() {
            const loop = (nowMs) => {
                this.rafId = requestAnimationFrame(loop);
                if (this.isTabHidden || this.isStageSkipped) {
                    this.lastFrameTime = nowMs;
                    return;
                }

                const dt = Math.min(0.05, Math.max(0.001, (nowMs - this.lastFrameTime) / 1000));
                this.lastFrameTime = nowMs;

                this.stepPhysics(dt);

                // Update skeletal animation mixers
                for (const slot of [1, 2]) {
                    this.heroRigs[slot]?.mixer?.update(dt);
                }
                this.kongorRig?.mixer?.update(dt);

                // Animate shatter shards
                for (let i = this.shatterShards.length - 1; i >= 0; i--) {
                    const s = this.shatterShards[i];
                    s.life -= dt;
                    s.mesh.position.x += s.vx * dt;
                    s.mesh.position.y += s.vy * dt;
                    s.mesh.position.z += s.vz * dt;
                    s.vy -= 8.5 * dt;
                    s.mesh.rotation.x += s.rx * dt;
                    s.mesh.rotation.y += s.ry * dt;
                    s.mesh.material.opacity = Math.max(0, s.life / 1.4);
                    if (s.life <= 0) {
                        this.scene.remove(s.mesh);
                        s.mesh.geometry.dispose();
                        s.mesh.material.dispose();
                        this.shatterShards.splice(i, 1);
                    }
                }

                // Smooth camera transition + subtle comfort-respecting impact shake
                const camLerp = Math.min(1, dt * 4.5);
                this.camCurrentPos.lerp(this.camGoalPos, camLerp);
                this.camCurrentLook.lerp(this.camGoalLook, camLerp);
                this.camera.position.copy(this.camCurrentPos);

                if (this.shakeIntensity > 0.005 && !this.comfortMode) {
                    this.camera.position.x += (Math.random() - 0.5) * this.shakeIntensity;
                    this.camera.position.y += (Math.random() - 0.5) * this.shakeIntensity;
                    this.shakeIntensity *= (1 - dt * 10);
                } else {
                    this.shakeIntensity = 0;
                }
                this.camera.lookAt(this.camCurrentLook);

                this.audio.tickOverdriveBeat(nowMs / 1000, this.act);
                this.updateHudEnergyOnly();
                this.renderer.render(this.scene, this.camera);
            };
            this.rafId = requestAnimationFrame(loop);
        }

        updateHudEnergyOnly() {
            if (this.dom.energyBar1) this.dom.energyBar1.style.width = `${Math.round(this.players[1].energy)}%`;
            if (this.dom.energyBar2) this.dom.energyBar2.style.width = `${Math.round(this.players[2].energy)}%`;
            const mySlot = this.mode === 'online' ? this.localSlot : 1;
            const myE = Math.round(this.players[mySlot].energy);
            if (this.dom.energyText1) this.dom.energyText1.textContent = `${myE}%`;
            if (this.dom.btnUlt) {
                this.dom.btnUlt.disabled = this.act < 2 || myE < 100;
                const h = this.heroes[mySlot] ? HERO_META[this.heroes[mySlot]] : null;
                if (this.dom.btnUltLabel && h) {
                    this.dom.btnUltLabel.textContent = myE >= 100 ? `${h.ultName} READY!` : h.ultName;
                }
            }
        }

        dispose() {
            if (this.rafId) cancelAnimationFrame(this.rafId);
            this.resizeObserver?.disconnect();
            if (this.hub) this.hub.stop().catch(() => {});
            if (this.arStream) this.arStream.getTracks().forEach(t => t.stop());
            if (global.KongorViewerCore && this.scene) {
                global.KongorViewerCore.disposeObject3DResources(this.scene, this.renderer, null);
            }
        }
    }

    global.RiftbreakerGame = RiftbreakerGame;
    global.RiftbreakerCatalog = { HERO_META, RIFT_CARDS, ARENA };
})(window);
