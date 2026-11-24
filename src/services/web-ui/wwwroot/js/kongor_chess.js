// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Project KONGOR: Tabletop AR Chess (Legion vs. Hellbourne)
 *
 * An authentic 3D Tabletop AR and Studio Desk Chess experience starring iconic
 * Heroes of Newerth, featuring:
 * - Option B Mirrored Roster: Legion (Emerald Jade / Gold) vs. Hellbourne (Crimson / Obsidian)
 * - Alternating AR Board: One transparent, one solid/frosted, with dynamic team under-glow
 * - Genuine Unit Animations:
 *     * Queen (Moon Queen) walks to space
 *     * Knight (Sir Benzington) L-shape parabolic leap over pieces
 *     * Rook (Magmus) straight-line Lava Surge dash
 *     * King (Monkey King) 1-step agile dash & castling vault over Magmus
 *     * Pawns (Creeps) marching stride
 *     * Combat captures: attack animation + genuine defender death animation + graveyard bench
 * - Legendary Announcer FX:
 *     * Samuel L. Jackson ("Surprise MOTHAF*CKA!") on Check (with 10s cooldown + King hazard ring)
 *     * BreakyCPK ("BUT AT WHAT COST?!?") on piece exchanges
 *     * First Blood on first capture of the match
 *     * Victory fanfares
 * - FIDE-Compliant rules via chess.js
 * - Instant offline Bot play (Novice, Challenger, Grandmaster Minimax AI)
 * - SignalR 1v1 Online Multiplayer with server-synchronized chess clocks (3m, 5m, 10m, 30m, unlimited)
 */

(function () {
    "use strict";

    // --- Configuration & Constants ---
    const BOARD_SIZE = 8;
    const TILE_SIZE = 0.22; // In meters for tabletop scale
    const BOARD_HEIGHT = 0.012; // Sleek low-profile floating tiles on table
    const PIECE_BASE_RADIUS = 0.08;

    const COLOR_WHITE = 'w'; // Legion
    const COLOR_BLACK = 'b'; // Hellbourne

    // --- Module State ---
    let scene = null;
    let camera = null;
    let renderer = null;
    let controls = null;
    let clock = null;
    let animFrameId = null;

    // --- 3D Board Objects ---
    let boardGroup = null;
    let tileMeshes = {}; // key: "r,c" => mesh
    let shadowPlane = null;
    let graveyardLegion = [];
    let graveyardHellbourne = [];
    let selectionRing = null;
    let targetMarkerGroup = null;
    let checkHazardRing = null;

    // --- Chess Rule Engine ---
    let chess = null; // chess.js instance
    let gameMode = 'vs_bot_novice'; // 'vs_bot_novice' | 'vs_bot_challenger' | 'vs_bot_master' | 'pass_and_play' | 'bot_vs_bot' | 'online_1v1'
    let timeControl = '5m';
    let p1ClockMs = 300000;
    let p2ClockMs = 300000;
    let clockInterval = null;
    let lastClockTick = Date.now();

    // --- Asset & Figurine State ---
    const modelCache = {}; // heroKey => gltf
    let pieces = []; // Array of Piece objects
    let piecesGrid = Array(BOARD_SIZE).fill(null).map(() => Array(BOARD_SIZE).fill(null));

    // --- Interaction State ---
    let selectedSquare = null; // 'e2'
    let legalMovesForSelected = [];
    let isAnimationBusy = false; // STRICT LOCK: No moves allowed until current animation completely finishes
    let hasFirstBloodOccurred = false;
    let lastCheckAnnouncerTime = 0;
    let isGameOver = false;
    let pendingPromotionMove = null;

    // --- AR & Audio Settings ---
    let isCameraActive = false;
    let mediaStream = null;
    let isAudioMuted = false;
    const activeAudioSet = new Set();

    // --- SignalR Online Multiplayer State ---
    let signalRConnection = null;
    let currentRoomCode = null;
    let playerSlot = 1; // 1 = Legion (White, Host), 2 = Hellbourne (Black, Challenger)
    let playerName = 'Commander';
    let isOnlineGame = false;

    // --- Raycasting & Hover Identification Badge ---
    let raycaster = null;
    let pointer = null;
    let hoveredPiece = null;
    const PIECE_BADGE_INFO = {
        p: { icon: 'fa-chess-pawn', name: 'Pawn', hero: 'Legionnaire' },
        n: { icon: 'fa-chess-knight', name: 'Knight', hero: 'Sir Benzington' },
        b: { icon: 'fa-chess-bishop', name: 'Bishop', hero: 'Jeraziah' },
        r: { icon: 'fa-chess-rook', name: 'Rook', hero: 'Magmus' },
        q: { icon: 'fa-chess-queen', name: 'Queen', hero: 'Moon Queen' },
        k: { icon: 'fa-chess-king', name: 'King', hero: 'Monkey King' }
    };

    // =========================================================================
    // 1. Coordinates & Board Math
    // =========================================================================

    function fileRankToRowCol(square) {
        // e.g. "e2" -> col 4 ('e'), row 6 (rank 2 from top row 0..7)
        const col = square.charCodeAt(0) - 'a'.charCodeAt(0);
        const rank = parseInt(square[1], 10);
        const row = 8 - rank;
        return { row, col };
    }

    function rowColToFileRank(row, col) {
        const file = String.fromCharCode('a'.charCodeAt(0) + col);
        const rank = 8 - row;
        return `${file}${rank}`;
    }

    function boardToWorld(row, col) {
        const x = (col - 3.5) * TILE_SIZE;
        const z = (row - 3.5) * TILE_SIZE;
        return new THREE.Vector3(x, BOARD_HEIGHT, z);
    }

    // =========================================================================
    // 2. Three.js Scene, Camera & Lighting Setup
    // =========================================================================

    function initThreeScene() {
        const container = document.getElementById('chessCanvasContainer');
        const canvas = document.getElementById('chessCanvas');
        if (!container || !canvas) return;

        clock = new THREE.Clock();
        scene = new THREE.Scene();

        camera = new THREE.PerspectiveCamera(
            45,
            container.clientWidth / container.clientHeight,
            0.05,
            50
        );
        camera.position.set(0, 1.45, 1.55);

        renderer = new THREE.WebGLRenderer({
            canvas: canvas,
            alpha: true,
            antialias: true,
            powerPreference: "high-performance"
        });
        renderer.setSize(container.clientWidth, container.clientHeight);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.shadowMap.enabled = true;
        renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        if (window.KongorViewerCore && window.KongorViewerCore.configureRendererColorSpace) {
            window.KongorViewerCore.configureRendererColorSpace(renderer);
        }
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.35;

        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.maxPolarAngle = Math.PI / 2 - 0.05;
        controls.minDistance = 0.4;
        controls.maxDistance = 4.0;
        controls.target.set(0, BOARD_HEIGHT, 0);

        const cal = (window.KongorViewerCore && window.KongorViewerCore.calibrateLightIntensity)
            ? window.KongorViewerCore.calibrateLightIntensity
            : (v) => v * Math.PI;

        // Balanced Hemisphere & Ambient Studio Light for both Legion & Hellbourne
        const hemiLight = new THREE.HemisphereLight(0xffffff, 0x334155, cal(1.15));
        hemiLight.position.set(0, 5, 0);
        scene.add(hemiLight);

        const ambientLight = new THREE.AmbientLight(0xfff7ed, cal(0.85));
        scene.add(ambientLight);

        // Key Light from Legion / South Side (Casts soft directional shadows on table)
        const dirLight = new THREE.DirectionalLight(0xffedd5, cal(1.55));
        dirLight.position.set(1.5, 3.5, 1.8);
        dirLight.castShadow = true;
        dirLight.shadow.mapSize.width = 2048;
        dirLight.shadow.mapSize.height = 2048;
        dirLight.shadow.camera.near = 0.5;
        dirLight.shadow.camera.far = 8.0;
        dirLight.shadow.camera.left = -1.5;
        dirLight.shadow.camera.right = 1.5;
        dirLight.shadow.camera.top = 1.5;
        dirLight.shadow.camera.bottom = -1.5;
        dirLight.shadow.bias = -0.0005;
        scene.add(dirLight);

        // Balanced Fill & Rim Light from Hellbourne / North Side (Warm neutral-rose so Hellbourne faces are crisp, never dark or solid red)
        const rimLight = new THREE.DirectionalLight(0xfff1f2, cal(1.45));
        rimLight.position.set(-1.8, 2.8, -2.2);
        scene.add(rimLight);

        raycaster = new THREE.Raycaster();
        pointer = new THREE.Vector2();

        buildChessBoard();
        setupVisualMarkers();
        initEventListeners();

        // Responsive resize
        window.addEventListener('resize', onWindowResize);

        // Start render loop
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden && renderer && animFrameId === null) {
                if (clock) clock.getDelta();
                renderLoop();
            }
        });
        renderLoop();
    }

    function onWindowResize() {
        const container = document.getElementById('chessCanvasContainer');
        if (!container || !camera || !renderer) return;

        camera.aspect = container.clientWidth / container.clientHeight;
        camera.updateProjectionMatrix();
        renderer.setSize(container.clientWidth, container.clientHeight);
    }

    function renderLoop() {
        if (document.hidden) {
            animFrameId = null;
            return;
        }
        animFrameId = requestAnimationFrame(renderLoop);

        const delta = clock.getDelta();

        // Update Animation Mixers
        for (const p of pieces) {
            if (p.mixer) p.mixer.update(delta);
        }

        // Animate Selection Halo & Check Hazard Ring
        if (selectionRing && selectionRing.visible) {
            selectionRing.rotation.z += delta * 1.5;
        }
        if (checkHazardRing && checkHazardRing.visible) {
            const pulse = 1.0 + Math.sin(Date.now() * 0.008) * 0.15;
            checkHazardRing.scale.set(pulse, pulse, pulse);
        }

        // Update Floating 3D Hover Badge Position
        if (hoveredPiece) {
            updateHoverBadgePosition();
        }

        controls.update();
        renderer.render(scene, camera);
    }

    // =========================================================================
    // 3. 8x8 Board Generation with Alternating AR Tiles & Dynamic Under-Glow
    // =========================================================================

    function buildChessBoard() {
        boardGroup = new THREE.Group();
        scene.add(boardGroup);

        const tileGeo = new THREE.BoxGeometry(TILE_SIZE * 0.96, BOARD_HEIGHT, TILE_SIZE * 0.96);

        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const isDark = (r + c) % 2 === 1;

                // Base Tile Material: Alternating one solid/frosted, one translucent
                const tileMat = new THREE.MeshStandardMaterial({
                    color: isDark ? 0x0f172a : 0xe2e8f0,
                    emissive: isDark ? 0x1e293b : 0x94a3b8,
                    emissiveIntensity: 0.2,
                    roughness: isDark ? 0.6 : 0.3,
                    metalness: 0.25,
                    transparent: true,
                    opacity: isDark ? (isCameraActive ? 0.12 : 0.28) : 0.85
                });

                const tileMesh = new THREE.Mesh(tileGeo, tileMat);
                const pos = boardToWorld(r, c);
                tileMesh.position.set(pos.x, BOARD_HEIGHT / 2, pos.z);
                tileMesh.receiveShadow = true;
                tileMesh.userData = { row: r, col: c, square: rowColToFileRank(r, c), isDark: isDark };

                // Glowing tile border trim
                const edgesGeo = new THREE.EdgesGeometry(tileGeo);
                const edgeMat = new THREE.LineBasicMaterial({
                    color: isDark ? 0x334155 : 0xcbd5e1,
                    transparent: true,
                    opacity: isDark ? 0.35 : 0.6
                });
                const edgeLine = new THREE.LineSegments(edgesGeo, edgeMat);
                tileMesh.add(edgeLine);
                tileMesh.userData.edgeLine = edgeLine;

                boardGroup.add(tileMesh);
                tileMeshes[`${r},${c}`] = tileMesh;
            }
        }

        // Tabletop Soft Contact Shadow Plane (Real tabletop shadow receiver)
        const shadowGeo = new THREE.PlaneGeometry(8.0, 8.0);
        const shadowMat = new THREE.ShadowMaterial({ opacity: 0.42 });
        shadowPlane = new THREE.Mesh(shadowGeo, shadowMat);
        shadowPlane.rotation.x = -Math.PI / 2;
        shadowPlane.position.y = 0;
        shadowPlane.receiveShadow = true;
        scene.add(shadowPlane);
    }

    /**
     * Calibrate tile appearance and transparency:
     * - Occupied by LEGION (White): Solid Radiant Emerald Jade (0x064e3b, emissive 0x047857) with glowing green rim
     * - Occupied by HELLBOURNE (Black): Solid Volcanic Crimson Obsidian (0x18181b, emissive 0x991b1b) with glowing ruby rim
     * - Empty square: Reverts cleanly back to default alternating state (transparent dark vs solid frosted light)
     */
    function updateBoardTransparency() {
        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const tileMesh = tileMeshes[`${r},${c}`];
                if (!tileMesh) continue;

                const isDark = tileMesh.userData.isDark;
                const occupyingPiece = piecesGrid[r][c];
                const isOccupied = occupyingPiece !== null && occupyingPiece.isAlive;

                tileMesh.material.transparent = true;

                if (isOccupied) {
                    tileMesh.material.opacity = 1.0;
                    const isLegion = occupyingPiece.color === COLOR_WHITE;

                    if (isLegion) {
                        // Legion Team: Radiant Emerald Jade (Green)
                        tileMesh.material.color.setHex(0x064e3b);
                        tileMesh.material.emissive.setHex(0x047857);
                        tileMesh.material.emissiveIntensity = 0.35;
                        tileMesh.material.roughness = 0.35;
                        tileMesh.material.metalness = 0.4;
                        if (tileMesh.userData.edgeLine) {
                            tileMesh.userData.edgeLine.material.color.setHex(0x10b981);
                            tileMesh.userData.edgeLine.material.opacity = 0.8;
                        }
                    } else {
                        // Hellbourne Team: Volcanic Crimson Obsidian (Red)
                        tileMesh.material.color.setHex(0x18181b);
                        tileMesh.material.emissive.setHex(0x991b1b);
                        tileMesh.material.emissiveIntensity = 0.35;
                        tileMesh.material.roughness = 0.65;
                        tileMesh.material.metalness = 0.5;
                        if (tileMesh.userData.edgeLine) {
                            tileMesh.userData.edgeLine.material.color.setHex(0xef4444);
                            tileMesh.userData.edgeLine.material.opacity = 0.8;
                        }
                    }
                } else {
                    // Empty square: reverts to default alternating state
                    if (isDark) {
                        tileMesh.material.opacity = isCameraActive ? 0.12 : 0.28;
                        tileMesh.material.color.setHex(0x0f172a);
                        tileMesh.material.emissive.setHex(0x1e293b);
                        tileMesh.material.emissiveIntensity = 0.15;
                        tileMesh.material.roughness = 0.6;
                        tileMesh.material.metalness = 0.25;
                        if (tileMesh.userData.edgeLine) {
                            tileMesh.userData.edgeLine.material.color.setHex(0x334155);
                            tileMesh.userData.edgeLine.material.opacity = isCameraActive ? 0.2 : 0.35;
                        }
                    } else {
                        tileMesh.material.opacity = isCameraActive ? 0.75 : 0.90;
                        tileMesh.material.color.setHex(0xe2e8f0);
                        tileMesh.material.emissive.setHex(0x94a3b8);
                        tileMesh.material.emissiveIntensity = 0.2;
                        tileMesh.material.roughness = 0.35;
                        tileMesh.material.metalness = 0.2;
                        if (tileMesh.userData.edgeLine) {
                            tileMesh.userData.edgeLine.material.color.setHex(0xcbd5e1);
                            tileMesh.userData.edgeLine.material.opacity = 0.6;
                        }
                    }
                }
                tileMesh.material.needsUpdate = true;
            }
        }
    }

    function setupVisualMarkers() {
        // Selection Halo Ring under current active piece
        const ringGeo = new THREE.RingGeometry(PIECE_BASE_RADIUS * 0.85, PIECE_BASE_RADIUS * 1.15, 32);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0xfbbf24,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.9
        });
        selectionRing = new THREE.Mesh(ringGeo, ringMat);
        selectionRing.rotation.x = -Math.PI / 2;
        selectionRing.position.y = BOARD_HEIGHT + 0.012;
        selectionRing.visible = false;
        scene.add(selectionRing);

        // Check Hazard Ring (Pulsing fiery red under the King when in check)
        const hazardGeo = new THREE.RingGeometry(PIECE_BASE_RADIUS * 0.95, PIECE_BASE_RADIUS * 1.35, 32);
        const hazardMat = new THREE.MeshBasicMaterial({
            color: 0xef4444,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.85
        });
        checkHazardRing = new THREE.Mesh(hazardGeo, hazardMat);
        checkHazardRing.rotation.x = -Math.PI / 2;
        checkHazardRing.position.y = BOARD_HEIGHT + 0.013;
        checkHazardRing.visible = false;
        scene.add(checkHazardRing);

        targetMarkerGroup = new THREE.Group();
        scene.add(targetMarkerGroup);
    }

    function highlightMoveTargets(legalMoves) {
        clearMoveTargets();
        const dotGeo = new THREE.CircleGeometry(0.065, 24);

        for (const m of legalMoves) {
            const { row, col } = fileRankToRowCol(m.to);
            const pos = boardToWorld(row, col);
            const isCapture = !!m.captured;

            const destTile = tileMeshes[`${row},${col}`];
            if (destTile && destTile.material) {
                destTile.material.opacity = 0.95;
                destTile.material.needsUpdate = true;
            }

            // Emerald for quiet move, Fiery Red for capture
            const dotMat = new THREE.MeshBasicMaterial({
                color: isCapture ? 0xef4444 : 0x10b981,
                side: THREE.DoubleSide,
                transparent: true,
                opacity: 0.92
            });

            const marker = new THREE.Mesh(dotGeo, dotMat);
            marker.rotation.x = -Math.PI / 2;
            marker.position.set(pos.x, BOARD_HEIGHT + 0.014, pos.z);
            marker.userData = { targetMove: m };
            targetMarkerGroup.add(marker);
        }
    }

    function clearMoveTargets() {
        while (targetMarkerGroup.children.length > 0) {
            targetMarkerGroup.remove(targetMarkerGroup.children[0]);
        }
        if (selectionRing) selectionRing.visible = false;
        updateBoardTransparency();
    }

    // =========================================================================
    // 4. Piece Spawning, Model Preloading & Procedural Figurines
    // =========================================================================

    const HERO_MODELS = {
        k: '/assets/prebaked/models/monkey_king_default.glb', // King: Monkey King
        q: '/assets/prebaked/models/krixi_default.glb',       // Queen: Moon Queen
        r: '/assets/prebaked/models/magmar_default.glb',      // Rook: Magmus
        n: '/assets/prebaked/models/sir_benzington_default.glb', // Knight: Sir Benzington
        b: '/assets/prebaked/models/vindicator_default.glb',  // Bishop: Vindicator (uniform baseline height ~0.177m)
        p: '/assets/prebaked/models/legionnaire_default.glb'  // Pawn: Classic Melee Creep / Legionnaire
    };

    const FALLBACK_MODELS = {
        b: '/assets/prebaked/models/vindicator_default.glb',
        p: '/assets/prebaked/models/legionnaire_default.glb'
    };

    // Equal Height Calibration: All back-rank pieces balanced to Vindicator baseline height (~0.177m)
    const PIECE_CONFIG = {
        k: { scale: 0.00192, yOffset: 0.028 },  // King: Monkey King (scaled & feet lifted to tile level)
        q: { scale: 0.00185, yOffset: 0.001 },  // Queen: Moon Queen (scaled to Vindicator baseline height ~0.177m)
        r: { scale: 0.00215, yOffset: 0.002 },  // Rook: Magmus (scaled to Vindicator baseline height ~0.177m)
        n: { scale: 0.00208, yOffset: 0.004 },  // Knight: Sir Benzington (-15% down from 0.00245)
        b: { scale: 0.00150, yOffset: 0.000 },  // Bishop: Vindicator (Reference baseline height ~0.177m)
        p: { scale: 0.00216, yOffset: 0.000 }   // Pawn: Creep (+80% up from 0.00120)
    };

    // Audio Voice Maps: Iconic hero voicelines for units
    const HERO_SOUND_MAP = {
        k: 'monkey_king',
        q: 'krixi',
        r: 'magmar',
        n: 'sir_benzington',
        b: 'vindicator',
        p: 'legionnaire'
    };

    async function preloadModel(type, url, fallbackUrl) {
        if (modelCache[type]) return modelCache[type];

        const loader = new THREE.GLTFLoader();

        async function tryLoad(targetUrl) {
            return new Promise((resolve) => {
                loader.load(
                    targetUrl,
                    (gltf) => resolve(gltf),
                    undefined,
                    () => resolve(null)
                );
            });
        }

        let gltf = await tryLoad(url);
        if (!gltf && fallbackUrl) {
            gltf = await tryLoad(fallbackUrl);
        }

        if (gltf) {
            modelCache[type] = gltf;
        }
        return gltf;
    }

    async function preloadAllModels() {
        const promises = Object.keys(HERO_MODELS).map(type => {
            return preloadModel(type, HERO_MODELS[type], FALLBACK_MODELS[type]);
        });
        await Promise.allSettled(promises);
    }

    function createProceduralFigurine(type, color) {
        const group = new THREE.Group();
        const isLegion = color === COLOR_WHITE;
        const baseColor = isLegion ? 0xf59e0b : 0xdc2626;

        // Base pedestal cylinder
        const baseGeo = new THREE.CylinderGeometry(0.045, 0.055, 0.02, 16);
        const baseMat = new THREE.MeshStandardMaterial({
            color: isLegion ? 0x064e3b : 0x18181b,
            metalness: 0.6,
            roughness: 0.3
        });
        const baseMesh = new THREE.Mesh(baseGeo, baseMat);
        baseMesh.position.y = 0.01;
        group.add(baseMesh);

        // Body cone / cylinder based on piece type
        let bodyGeo = null;
        let height = 0.12;

        switch (type) {
            case 'k': // King
                bodyGeo = new THREE.CylinderGeometry(0.025, 0.04, 0.18, 16);
                height = 0.18;
                break;
            case 'q': // Queen
                bodyGeo = new THREE.CylinderGeometry(0.022, 0.038, 0.16, 16);
                height = 0.16;
                break;
            case 'b': // Bishop
                bodyGeo = new THREE.ConeGeometry(0.035, 0.15, 16);
                height = 0.15;
                break;
            case 'n': // Knight
                bodyGeo = new THREE.BoxGeometry(0.04, 0.14, 0.05);
                height = 0.14;
                break;
            case 'r': // Rook
                bodyGeo = new THREE.CylinderGeometry(0.038, 0.042, 0.13, 16);
                height = 0.13;
                break;
            case 'p': // Pawn
            default:
                bodyGeo = new THREE.ConeGeometry(0.03, 0.10, 16);
                height = 0.10;
                break;
        }

        const bodyMat = new THREE.MeshStandardMaterial({
            color: baseColor,
            metalness: 0.4,
            roughness: 0.4
        });
        const bodyMesh = new THREE.Mesh(bodyGeo, bodyMat);
        bodyMesh.position.y = 0.02 + height / 2;
        group.add(bodyMesh);

        // Head sphere
        const headGeo = new THREE.SphereGeometry(0.025, 16, 16);
        const headMat = new THREE.MeshStandardMaterial({
            color: isLegion ? 0xfde047 : 0xf87171,
            metalness: 0.5,
            roughness: 0.3
        });
        const headMesh = new THREE.Mesh(headGeo, headMat);
        headMesh.position.y = 0.02 + height + 0.015;
        group.add(headMesh);

        return group;
    }

    function instantiatePiece(type, color, row, col, id) {
        const pieceGroup = new THREE.Group();
        const isLegion = color === COLOR_WHITE;

        // Heading Group: facing forward toward opponent across the board
        const headingGroup = new THREE.Group();
        headingGroup.rotation.y = isLegion ? Math.PI : 0;
        pieceGroup.add(headingGroup);

        const gltf = modelCache[type];
        let mixer = null;
        const actions = {};

        if (gltf && window.THREE.SkeletonUtils) {
            const modelScene = window.THREE.SkeletonUtils.clone(gltf.scene);
            const cfg = PIECE_CONFIG[type] || { scale: 0.0015, yOffset: 0.0 };
            modelScene.scale.set(cfg.scale, cfg.scale, cfg.scale);
            modelScene.position.y = cfg.yOffset;

            // Apply Faction Materials while preserving crisp hero textures (never paint meshes without emissiveMap solid red)
            modelScene.traverse((node) => {
                if (node.isMesh) {
                    node.castShadow = true;
                    node.receiveShadow = true;
                    if (node.material) {
                        const cloneMat = (mat) => {
                            const cloned = mat.clone();
                            cloned.side = THREE.DoubleSide;
                            if (cloned.map && window.KongorViewerCore && window.KongorViewerCore.configureTextureColorSpace) {
                                window.KongorViewerCore.configureTextureColorSpace(cloned.map);
                            }
                            cloned.roughness = 0.42;
                            cloned.metalness = 0.15;
                            if (isLegion) {
                                cloned.color.setHex(0xffffff);
                                if (cloned.emissive) {
                                    if (cloned.emissiveMap) {
                                        cloned.emissive.setHex(0xfffbeb);
                                        cloned.emissiveIntensity = 0.25;
                                    } else {
                                        cloned.emissive.setHex(0x000000);
                                        cloned.emissiveIntensity = 0.0;
                                    }
                                }
                            } else {
                                // Keep crisp natural hero textures for Hellbourne; only tint emissive where an emissiveMap actually exists
                                cloned.color.setHex(0xf8fafc);
                                if (cloned.emissive) {
                                    if (cloned.emissiveMap) {
                                        cloned.emissive.setHex(0xfb7185);
                                        cloned.emissiveIntensity = 0.35;
                                    } else {
                                        cloned.emissive.setHex(0x000000);
                                        cloned.emissiveIntensity = 0.0;
                                    }
                                }
                            }
                            cloned.needsUpdate = true;
                            return cloned;
                        };
                        node.material = Array.isArray(node.material)
                            ? node.material.map(cloneMat)
                            : cloneMat(node.material);
                    }
                }
            });

            headingGroup.add(modelScene);

            mixer = new THREE.AnimationMixer(modelScene);
            if (gltf.animations) {
                gltf.animations.forEach((clip) => {
                    actions[clip.name] = mixer.clipAction(clip);
                });
            }

            // Start idle animation
            const idle = actions['idle'] || actions['dancing_idle'] || Object.values(actions)[0];
            if (idle) {
                idle.play();
                idle.time = Math.random() * 2.0;
            }
        } else {
            // Procedural Figurine fallback
            const figurine = createProceduralFigurine(type, color);
            headingGroup.add(figurine);
        }

        const worldPos = boardToWorld(row, col);
        pieceGroup.position.copy(worldPos);
        scene.add(pieceGroup);

        const pieceObj = {
            id: id,
            type: type,
            color: color,
            row: row,
            col: col,
            square: rowColToFileRank(row, col),
            isAlive: true,
            mesh: pieceGroup,
            headingGroup: headingGroup,
            mixer: mixer,
            actions: actions
        };

        // Tag all meshes so raycaster immediately maps hit geometry to piece
        pieceGroup.traverse((node) => {
            node.userData.piece = pieceObj;
        });

        return pieceObj;
    }

    function populateBoardFromChess() {
        // Clear existing pieces
        for (const p of pieces) {
            scene.remove(p.mesh);
        }
        pieces = [];
        piecesGrid = Array(BOARD_SIZE).fill(null).map(() => Array(BOARD_SIZE).fill(null));

        const board = chess.board();
        let pieceCounter = 1;

        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const squareObj = board[r][c];
                if (squareObj) {
                    const p = instantiatePiece(squareObj.type, squareObj.color, r, c, `p_${pieceCounter++}`);
                    pieces.push(p);
                    piecesGrid[r][c] = p;
                }
            }
        }

        updateBoardTransparency();
        updateCheckStateVisuals();
    }

    // =========================================================================
    // 5. Piece Animations & Movement Choreography
    // =========================================================================

    function getPieceAction(piece, actionName) {
        if (!piece || !piece.actions) return null;
        if (piece.actions[actionName]) return piece.actions[actionName];
        if (piece.actions[`${actionName}_1`]) return piece.actions[`${actionName}_1`];
        if (piece.actions[`${actionName}_2`]) return piece.actions[`${actionName}_2`];
        const lower = actionName.toLowerCase();
        if (piece.actions[lower]) return piece.actions[lower];
        if (piece.actions[`${lower}_1`]) return piece.actions[`${lower}_1`];
        for (const [key, act] of Object.entries(piece.actions)) {
            if (key.toLowerCase().includes(lower)) return act;
        }
        return null;
    }

    function playPieceAction(piece, actionName, loopOnce = false) {
        if (!piece || !piece.actions) return;
        const action = getPieceAction(piece, actionName);
        if (action) {
            action.reset();
            if (loopOnce) {
                action.setLoop(THREE.LoopOnce);
                action.clampWhenFinished = true;
            } else {
                action.setLoop(THREE.LoopRepeat);
                action.clampWhenFinished = false;
            }
            action.play();
        } else {
            console.warn(`[KongorChess] Animation clip "${actionName}" not found on piece ${piece.type}`, Object.keys(piece.actions));
        }
    }

    function stopPieceAction(piece, actionName) {
        if (!piece || !piece.actions) return;
        const action = getPieceAction(piece, actionName);
        if (action) {
            action.stop();
        }
    }

    /**
     * Executes the visual animation for a chess move.
     * Enforces the STRICT LOCK: isAnimationBusy = true prevents any input
     * until movement, attacks, and deaths have fully concluded.
     */
    function animateMove(move, onComplete) {
        isAnimationBusy = true;
        hideHoverBadge();

        const fromPos = fileRankToRowCol(move.from);
        const toPos = fileRankToRowCol(move.to);

        const movingPiece = piecesGrid[fromPos.row][fromPos.col];
        if (!movingPiece) {
            if (onComplete) onComplete();
            isAnimationBusy = false;
            return;
        }

        // Determine if capture
        let capturedPiece = null;
        if (move.captured) {
            if (move.flags && move.flags.includes('e')) {
                // En Passant: captured pawn is on (fromPos.row, toPos.col)
                capturedPiece = piecesGrid[fromPos.row][toPos.col];
            } else {
                capturedPiece = piecesGrid[toPos.row][toPos.col];
            }
        }

        const startWorld = boardToWorld(fromPos.row, fromPos.col);
        const targetWorld = boardToWorld(toPos.row, toPos.col);

        // Turn heading toward target destination
        const dir = new THREE.Vector3().subVectors(targetWorld, startWorld);
        if (dir.lengthSq() > 0.0001) {
            const targetYaw = Math.atan2(dir.x, dir.z);
            movingPiece.headingGroup.rotation.y = targetYaw;
        }

        const pieceType = movingPiece.type;
        let animDuration = 650; // ms

        // Set movement animation
        if (pieceType === 'n') {
            // Knight: Sir Benzington L-shape high leap arc
            animDuration = 800;
            playPieceAction(movingPiece, 'ability_4', true);
        } else if (pieceType === 'r') {
            // Rook: Magmus high-speed straight-line Lava Surge slide
            animDuration = 500;
            playPieceAction(movingPiece, 'ability_1', true);
        } else if (pieceType === 'k') {
            // King: Monkey King 1-step agile dash
            animDuration = 450;
            playPieceAction(movingPiece, 'ability_1', true);
        } else {
            // Queen (Moon Queen) & Pawns (Creeps / Legionnaire) & Bishops: Walk to space
            animDuration = 650;
            console.log(`[KongorChess] Playing walk animation for piece ${pieceType} (${movingPiece.id})`);
            playPieceAction(movingPiece, 'walk');
        }

        const startTime = Date.now();

        function step() {
            const elapsed = Date.now() - startTime;
            const t = Math.min(elapsed / animDuration, 1.0);

            // Interpolate position
            const currentX = THREE.MathUtils.lerp(startWorld.x, targetWorld.x, t);
            const currentZ = THREE.MathUtils.lerp(startWorld.z, targetWorld.z, t);
            let currentY = BOARD_HEIGHT;

            // Knight Parabolic Arc
            if (pieceType === 'n') {
                const arcHeight = 0.32; // Clears over intervening pieces
                currentY += Math.sin(t * Math.PI) * arcHeight;
            }

            movingPiece.mesh.position.set(currentX, currentY, currentZ);

            if (t < 1.0) {
                requestAnimationFrame(step);
            } else {
                // Movement finished: stop walk cycle
                if (pieceType !== 'n' && pieceType !== 'r' && pieceType !== 'k') {
                    stopPieceAction(movingPiece, 'walk');
                    const idle = getPieceAction(movingPiece, 'idle') || movingPiece.actions['dancing_idle'] || Object.values(movingPiece.actions)[0];
                    if (idle) {
                        idle.reset();
                        idle.play();
                    }
                }

                // Restore forward facing heading across the board
                movingPiece.headingGroup.rotation.y = movingPiece.color === COLOR_WHITE ? Math.PI : 0;

                // Handle combat resolution if capture occurred
                if (capturedPiece && capturedPiece.isAlive) {
                    executeCombatCapture(movingPiece, capturedPiece, () => {
                        finalizeMovePositions();
                    });
                } else {
                    finalizeMovePositions();
                }
            }
        }

        function finalizeMovePositions() {
            // Update 3D grid references
            piecesGrid[fromPos.row][fromPos.col] = null;
            piecesGrid[toPos.row][toPos.col] = movingPiece;
            movingPiece.row = toPos.row;
            movingPiece.col = toPos.col;
            movingPiece.square = move.to;

            // Handle Castling: rook jumps over
            if (move.flags && (move.flags.includes('k') || move.flags.includes('q'))) {
                animateCastlingRook(move);
            }

            // Handle Promotion morph
            if (move.promotion) {
                executePromotionMorph(movingPiece, move.promotion);
            }

            updateBoardTransparency();
            updateCheckStateVisuals();

            // Play turn switch sound signifying current turn ended and opponent turn begins
            playTurnEndSound();

            isAnimationBusy = false; // RELEASE LOCK
            if (onComplete) onComplete();
        }

        requestAnimationFrame(step);
    }

    function animateCastlingRook(kingMove) {
        // Find corresponding rook
        const isKingside = kingMove.flags.includes('k');
        const row = kingMove.color === COLOR_WHITE ? 7 : 0;
        const fromCol = isKingside ? 7 : 0;
        const toCol = isKingside ? 5 : 3;

        const rook = piecesGrid[row][fromCol];
        if (!rook) return;

        const rookStart = boardToWorld(row, fromCol);
        const rookTarget = boardToWorld(row, toCol);

        piecesGrid[row][fromCol] = null;
        piecesGrid[row][toCol] = rook;
        rook.row = row;
        rook.col = toCol;
        rook.square = rowColToFileRank(row, toCol);

        // Magmus slides straight while Monkey King vaulted over
        playPieceAction(rook, 'ability_1', true);
        const startTime = Date.now();
        const duration = 400;

        function rookStep() {
            const elapsed = Date.now() - startTime;
            const t = Math.min(elapsed / duration, 1.0);
            rook.mesh.position.lerpVectors(rookStart, rookTarget, t);
            if (t < 1.0) {
                requestAnimationFrame(rookStep);
            } else {
                rook.mesh.position.copy(rookTarget);
                rook.headingGroup.rotation.y = rook.color === COLOR_WHITE ? Math.PI : 0;
            }
        }
        requestAnimationFrame(rookStep);
    }

    function executeCombatCapture(attacker, defender, onDone) {
        // Attacker plays attack animation
        playPieceAction(attacker, 'attack_1', true);
        playHeroCombatSound(attacker.type, 'attack');

        // Announcer trigger
        if (!hasFirstBloodOccurred) {
            hasFirstBloodOccurred = true;
            showBannerNotification("🩸 FIRST BLOOD!", "fa-skull");
            playAudioFx('announcer', 'first_blood');
        } else {
            // BreakyCPK on piece capture
            showBannerNotification("⚔️ BUT AT WHAT COST?!?", "fa-bolt");
            playAudioFx('breakycpk', 'smackdown');
        }

        // Defender plays genuine death animation
        defender.isAlive = false;
        if (hoveredPiece === defender) {
            hideHoverBadge();
        }
        playPieceAction(defender, 'death', true);
        playHeroCombatSound(defender.type, 'death');

        // Defending piece sinks / dissolves and is placed in graveyard bench
        const startY = defender.mesh.position.y;
        const startTime = Date.now();
        const duration = 700;

        function sinkStep() {
            const elapsed = Date.now() - startTime;
            const t = Math.min(elapsed / duration, 1.0);
            defender.mesh.position.y = startY - t * 0.15;
            defender.mesh.scale.setScalar(1.0 - t * 0.4);

            if (t < 1.0) {
                requestAnimationFrame(sinkStep);
            } else {
                scene.remove(defender.mesh);
                if (onDone) onDone();
            }
        }
        requestAnimationFrame(sinkStep);
    }

    function executePromotionMorph(piece, newType) {
        showBannerNotification("⭐ HERO PROMOTION!", "fa-star");
        playAudioFx('announcer', 'prepare_for_battle');

        // Swap to promoted hero model
        const promotedGltf = modelCache[newType];
        if (promotedGltf && window.THREE.SkeletonUtils) {
            while (piece.headingGroup.children.length > 0) {
                piece.headingGroup.remove(piece.headingGroup.children[0]);
            }
            const modelScene = window.THREE.SkeletonUtils.clone(promotedGltf.scene);
            const cfg = PIECE_CONFIG[newType] || { scale: 0.0015, yOffset: 0.0 };
            modelScene.scale.set(cfg.scale, cfg.scale, cfg.scale);
            modelScene.position.y = cfg.yOffset;

            // Apply Faction Tints
            const isLegion = piece.color === COLOR_WHITE;
            modelScene.traverse((node) => {
                if (node.isMesh) {
                    node.castShadow = true;
                    node.receiveShadow = true;
                    if (node.material) {
                        node.material = node.material.clone();
                        if (isLegion) {
                            node.material.color.setHex(0xfef08a); // Golden tint
                        } else {
                            node.material.color.setHex(0x64748b); // Dark charcoal tint
                            node.material.emissive.setHex(0x7f1d1d);
                            node.material.emissiveIntensity = 0.25;
                        }
                    }
                }
            });

            piece.headingGroup.rotation.y = isLegion ? Math.PI : 0;
            piece.headingGroup.add(modelScene);
            piece.type = newType;

            piece.mixer = new THREE.AnimationMixer(modelScene);
            piece.actions = {};
            if (promotedGltf.animations) {
                promotedGltf.animations.forEach((clip) => {
                    piece.actions[clip.name] = piece.mixer.clipAction(clip);
                });
            }
            const idle = piece.actions['idle'] || Object.values(piece.actions)[0];
            if (idle) idle.play();
        }
    }

    // =========================================================================
    // 6. Check & Checkmate Visuals & Announcers
    // =========================================================================

    function updateCheckStateVisuals() {
        const inCheck = chess.in_check();
        const checkBadge = document.getElementById('checkIndicatorBadge');

        if (inCheck && !chess.in_checkmate()) {
            if (checkBadge) checkBadge.classList.remove('hidden');

            // Find current turn's king to place the red hazard ring under
            const activeColor = chess.turn();
            const kingPiece = pieces.find(p => p.isAlive && p.type === 'k' && p.color === activeColor);

            if (kingPiece && checkHazardRing) {
                const pos = boardToWorld(kingPiece.row, kingPiece.col);
                checkHazardRing.position.set(pos.x, BOARD_HEIGHT + 0.013, pos.z);
                checkHazardRing.visible = true;
            }

            // Samuel L. Jackson Announcer with 10s cooldown
            const now = Date.now();
            if (now - lastCheckAnnouncerTime > 10000) {
                lastCheckAnnouncerTime = now;
                showBannerNotification("💥 SURPRISE MOTHAF*CKA!", "fa-radiation");
                playAudioFx('bamf', 'surprise_motherfucker');
            }
        } else {
            if (checkBadge) checkBadge.classList.add('hidden');
            if (checkHazardRing) checkHazardRing.visible = false;
        }

        if (chess.in_checkmate()) {
            isGameOver = true;
            const winner = chess.turn() === 'b' ? 'Legion' : 'Hellbourne';
            showBannerNotification(`🏆 CHECKMATE! ${winner.toUpperCase()} WINS!`, "fa-trophy");
            playAudioFx('announcer', 'legion_wins');
            showVictoryModal(winner, "Checkmate");
        } else if (chess.in_draw()) {
            isGameOver = true;
            showBannerNotification("🤝 DRAW / STALEMATE", "fa-handshake");
            showVictoryModal("Draw", "Stalemate");
        }
    }

    // =========================================================================
    // 7. Audio System, Hero Voicelines & Turn Sounds
    // =========================================================================

    function playTurnEndSound() {
        if (isAudioMuted) return;
        // Authentic chess clock switch sound
        const soundUrl = `/sounds/ui/button_clock.ogg`;
        const audio = new Audio(soundUrl);
        audio.volume = 0.85;
        activeAudioSet.add(audio);

        const cleanup = () => activeAudioSet.delete(audio);
        audio.addEventListener('ended', cleanup);
        audio.addEventListener('error', () => {
            cleanup();
            const fallbackAudio = new Audio(`/sounds/ui/notification.ogg`);
            fallbackAudio.volume = 0.85;
            fallbackAudio.play().catch(() => {});
        });
        audio.play().catch(() => cleanup());
    }

    function isLocalPlayerColor(pieceColor) {
        if (isOnlineGame) {
            return (playerSlot === 1 && pieceColor === COLOR_WHITE) || (playerSlot === 2 && pieceColor === COLOR_BLACK);
        }
        if (gameMode.startsWith('vs_bot_')) {
            return pieceColor === COLOR_WHITE;
        }
        if (gameMode === 'pass_and_play') {
            return true;
        }
        return false; // Spectator / bot_vs_bot
    }

    function playPieceVoice(pieceType, category, pieceColor = null) {
        if (isAudioMuted) return;
        if (pieceColor && !isLocalPlayerColor(pieceColor)) return;
        const hero = HERO_SOUND_MAP[pieceType];
        if (!hero) return;

        // Pick 1..4 randomly
        const index = Math.floor(Math.random() * 4) + 1;
        const soundUrl = `/sounds/${hero}/${category}_${index}.ogg`;

        const audio = new Audio(soundUrl);
        audio.volume = 0.88;
        activeAudioSet.add(audio);

        const cleanup = () => activeAudioSet.delete(audio);
        audio.addEventListener('ended', cleanup);
        audio.addEventListener('error', () => {
            cleanup();
            const fallbackUrl = `/api/v1/assets/sound?hero=${hero}&action=${category}_${index}&_t=${Date.now()}`;
            const fbAudio = new Audio(fallbackUrl);
            fbAudio.volume = 0.88;
            fbAudio.play().catch(() => {});
        });
        audio.play().catch(() => cleanup());
    }

    function playHeroCombatSound(pieceType, action) {
        if (isAudioMuted) return;
        const hero = HERO_SOUND_MAP[pieceType] || 'monkey_king';
        const file = action === 'death' ? 'death.ogg' : 'attack_1.ogg';
        const soundUrl = `/sounds/${hero}/${file}`;

        const audio = new Audio(soundUrl);
        audio.volume = 0.85;
        activeAudioSet.add(audio);

        const cleanup = () => activeAudioSet.delete(audio);
        audio.addEventListener('ended', cleanup);
        audio.addEventListener('error', () => {
            cleanup();
            const fallbackUrl = `/api/v1/assets/sound?hero=${hero}&action=${action}&_t=${Date.now()}`;
            const fbAudio = new Audio(fallbackUrl);
            fbAudio.volume = 0.85;
            fbAudio.play().catch(() => {});
        });
        audio.play().catch(() => cleanup());
    }

    function playAudioFx(category, actionType) {
        if (isAudioMuted) return;

        let soundUrl = '';
        if (category === 'hero') {
            soundUrl = `/sounds/monkey_king/attack_1.ogg`;
        } else if (category === 'bamf') {
            soundUrl = `/sounds/announcer/bamf/smackdown.ogg`;
        } else if (category === 'breakycpk') {
            soundUrl = `/sounds/announcer/breakycpk/smackdown.ogg`;
        } else if (category === 'announcer') {
            soundUrl = `/sounds/announcer/default/${encodeURIComponent(actionType)}.ogg`;
        } else {
            soundUrl = `/sounds/ui/${encodeURIComponent(actionType)}.ogg`;
        }

        const audio = new Audio(soundUrl);
        audio.volume = 0.9;
        activeAudioSet.add(audio);

        const cleanup = () => {
            activeAudioSet.delete(audio);
        };

        audio.addEventListener('ended', cleanup);
        audio.addEventListener('error', () => {
            cleanup();
            let fbUrl = '';
            if (category === 'bamf') {
                fbUrl = `/api/v1/assets/sound?announcer=bamf&action=smackdown&_t=${Date.now()}`;
            } else if (category === 'breakycpk') {
                fbUrl = `/api/v1/assets/sound?announcer=breakycpk&action=smackdown&_t=${Date.now()}`;
            } else {
                fbUrl = `/api/v1/assets/sound?path=shared/sounds/announcer/${encodeURIComponent(actionType)}.ogg`;
            }
            const fbAudio = new Audio(fbUrl);
            fbAudio.volume = 0.9;
            fbAudio.play().catch(() => {});
        });
        audio.play().catch(() => cleanup());
    }

    function showBannerNotification(text, iconClass = "fa-bolt") {
        const banner = document.getElementById('chessAnnouncerBanner');
        const label = document.getElementById('chessAnnouncerText');
        const icon = document.getElementById('announcerIcon');
        if (!banner || !label) return;

        label.innerText = text;
        if (icon) icon.className = `fas ${iconClass} text-slate-950`;

        banner.classList.remove('hidden');
        banner.classList.remove('opacity-0', 'scale-90');
        banner.classList.add('opacity-100', 'scale-100');

        setTimeout(() => {
            banner.classList.remove('opacity-100', 'scale-100');
            banner.classList.add('opacity-0', 'scale-90');
            setTimeout(() => banner.classList.add('hidden'), 350);
        }, 2200);
    }

    // =========================================================================
    // 8. Hover Identification Badge & 3D Projection
    // =========================================================================

    const PIECE_BADGE_HEAD_OFFSETS = {
        k: 0.23, // King (Monkey King)
        q: 0.21, // Queen (Moon Queen)
        r: 0.20, // Rook (Magmus)
        n: 0.21, // Knight (Sir Benzington)
        b: 0.20, // Bishop (Jeraziah / Vindicator)
        p: 0.22  // Pawn (Legionnaire Creep)
    };

    let hideBadgeTimeout = null;

    function getPieceHeadPosition(piece) {
        if (!piece || !piece.mesh) return null;

        const typeKey = (piece.type || 'p').toLowerCase();
        const worldPos = new THREE.Vector3();
        piece.mesh.getWorldPosition(worldPos);

        const defaultOffset = PIECE_BADGE_HEAD_OFFSETS[typeKey] || 0.21;
        let headY = worldPos.y + defaultOffset;

        // Bounding box validation: only accept bounding box height if it lies within realistic model bounds [0.12m, 0.25m].
        // Models like Sir Benzington (lance / orbital jump morphs up to 2500 units) and Moon Queen (morphs up to 356 units)
        // have huge bounding boxes in Three.js that would otherwise project miles above the screen.
        try {
            const bbox = new THREE.Box3().setFromObject(piece.mesh);
            const relativeHeight = bbox.max.y - worldPos.y;
            if (isFinite(relativeHeight) && relativeHeight >= 0.12 && relativeHeight <= 0.25) {
                headY = bbox.max.y + 0.035;
            }
        } catch (e) {
            // Keep calibrated default headY
        }

        return new THREE.Vector3(worldPos.x, headY, worldPos.z);
    }

    function updateHoverBadgePosition() {
        if (!hoveredPiece || !hoveredPiece.isAlive) {
            hideHoverBadge();
            return;
        }

        const badgeEl = document.getElementById('chessHoverPieceBadge');
        const container = document.getElementById('chessCanvasContainer');
        if (!badgeEl || !container || !camera) return;

        const headPos = getPieceHeadPosition(hoveredPiece);
        if (!headPos) return;

        // Project 3D world coordinate to NDC (-1 to +1)
        const proj = headPos.clone().project(camera);

        // If behind camera plane, hide
        if (proj.z > 1.0) {
            badgeEl.classList.add('opacity-0');
            return;
        }

        // Convert NDC to canvas container coordinates
        const rect = container.getBoundingClientRect();
        const screenX = (proj.x * 0.5 + 0.5) * rect.width;
        const screenY = (-proj.y * 0.5 + 0.5) * rect.height;

        badgeEl.style.left = `${screenX}px`;
        badgeEl.style.top = `${screenY}px`;
        badgeEl.classList.remove('opacity-0');
    }

    function showHoverBadge(piece) {
        if (hideBadgeTimeout) {
            clearTimeout(hideBadgeTimeout);
            hideBadgeTimeout = null;
        }

        const badgeEl = document.getElementById('chessHoverPieceBadge');
        const card = document.getElementById('chessHoverBadgeCard');
        const pip = document.getElementById('chessHoverBadgePip');
        const icon = document.getElementById('chessHoverBadgeIcon');
        const title = document.getElementById('chessHoverBadgeTitle');
        const hero = document.getElementById('chessHoverBadgeHero');

        if (!badgeEl || !card || !pip || !icon || !title || !hero) return;

        const typeKey = (piece.type || 'p').toLowerCase();
        const info = PIECE_BADGE_INFO[typeKey] || { icon: 'fa-chess', name: 'Piece', hero: 'Hero' };
        const isLegion = piece.color === COLOR_WHITE;

        // Populate content
        title.innerText = info.name.toUpperCase();
        hero.innerText = `${info.hero} • ${isLegion ? 'Legion' : 'Hellbourne'}`;

        // Set faction styling
        if (isLegion) {
            card.className = "flex items-center gap-2 px-3 py-1.5 rounded-xl bg-slate-900/95 backdrop-blur-md border border-emerald-500/70 shadow-2xl shadow-emerald-950/60 text-xs font-mono font-bold whitespace-nowrap";
            pip.className = "w-2 h-2 mx-auto rotate-45 -mt-1 bg-slate-900 border-r border-b border-emerald-500/70";
            icon.className = `fas ${info.icon} text-sm text-emerald-400`;
            title.className = "uppercase tracking-wider text-emerald-300";
            hero.className = "text-[10px] text-emerald-200/80 font-normal";
        } else {
            card.className = "flex items-center gap-2 px-3 py-1.5 rounded-xl bg-slate-900/95 backdrop-blur-md border border-rose-500/70 shadow-2xl shadow-rose-950/60 text-xs font-mono font-bold whitespace-nowrap";
            pip.className = "w-2 h-2 mx-auto rotate-45 -mt-1 bg-slate-900 border-r border-b border-rose-500/70";
            icon.className = `fas ${info.icon} text-sm text-rose-400`;
            title.className = "uppercase tracking-wider text-rose-300";
            hero.className = "text-[10px] text-rose-200/80 font-normal";
        }

        badgeEl.classList.remove('hidden');
        updateHoverBadgePosition();
    }

    function hideHoverBadge() {
        hoveredPiece = null;
        const badgeEl = document.getElementById('chessHoverPieceBadge');
        if (badgeEl) {
            badgeEl.classList.add('opacity-0');
            if (hideBadgeTimeout) clearTimeout(hideBadgeTimeout);
            hideBadgeTimeout = setTimeout(() => {
                if (!hoveredPiece) {
                    badgeEl.classList.add('hidden');
                }
            }, 150);
        }
    }

    function onPointerMove(event) {
        if (isAnimationBusy || isGameOver) {
            hideHoverBadge();
            return;
        }

        const container = document.getElementById('chessCanvasContainer');
        if (!container || !camera || !renderer) return;

        const rect = container.getBoundingClientRect();
        pointer.x = ((event.clientX - rect.left) / container.clientWidth) * 2 - 1;
        pointer.y = -((event.clientY - rect.top) / container.clientHeight) * 2 + 1;

        raycaster.setFromCamera(pointer, camera);

        let candidatePiece = null;

        // 1. Raycast against board tiles first (ensures instant detection on any square)
        const tileCandidates = Object.values(tileMeshes);
        const tileIntersects = raycaster.intersectObjects(tileCandidates, false);
        if (tileIntersects.length > 0) {
            const tile = tileIntersects[0].object;
            const { row, col } = tile.userData;
            const p = piecesGrid[row]?.[col];
            if (p && p.isAlive) {
                candidatePiece = p;
            }
        }

        // 2. Raycast directly against piece 3D meshes (detects hovering over upper body / head / wings)
        if (!candidatePiece) {
            const alivePieces = pieces.filter(p => p.isAlive);
            const pieceMeshes = alivePieces.map(p => p.mesh);
            const pieceIntersects = raycaster.intersectObjects(pieceMeshes, true);
            if (pieceIntersects.length > 0) {
                for (const hit of pieceIntersects) {
                    let hitObj = hit.object;
                    while (hitObj && !hitObj.userData.piece && hitObj.parent) {
                        hitObj = hitObj.parent;
                    }
                    if (hitObj && hitObj.userData.piece && hitObj.userData.piece.isAlive) {
                        candidatePiece = hitObj.userData.piece;
                        break;
                    }
                }
            }
        }

        if (candidatePiece !== hoveredPiece) {
            hoveredPiece = candidatePiece;
            if (hoveredPiece) {
                showHoverBadge(hoveredPiece);
            } else {
                hideHoverBadge();
            }
        }
    }

    function onPointerLeave() {
        hideHoverBadge();
    }

    // =========================================================================
    // 9. Move Selection, Player Input & Strict Animation Gating
    // =========================================================================

    function onPointerClick(event) {
        if (isAnimationBusy || isGameOver) return; // STRICT LOCK

        const container = document.getElementById('chessCanvasContainer');
        if (!container) return;

        const rect = container.getBoundingClientRect();
        pointer.x = ((event.clientX - rect.left) / container.clientWidth) * 2 - 1;
        pointer.y = -((event.clientY - rect.top) / container.clientHeight) * 2 + 1;

        raycaster.setFromCamera(pointer, camera);

        // 1. Check if clicking on an active target move marker
        const markerIntersects = raycaster.intersectObjects(targetMarkerGroup.children, true);
        if (markerIntersects.length > 0) {
            const hitMarker = markerIntersects[0].object;
            const targetMove = hitMarker.userData.targetMove;
            if (targetMove) {
                handleUserAttemptMove(targetMove);
                return;
            }
        }

        // 2. Check if clicking on a board tile
        const tileCandidates = Object.values(tileMeshes);
        const tileIntersects = raycaster.intersectObjects(tileCandidates, false);
        if (tileIntersects.length > 0) {
            const tile = tileIntersects[0].object;
            const { row, col, square } = tile.userData;
            handleSquareClick(square, row, col);
        }
    }

    function handleSquareClick(square, row, col) {
        // If online 1v1, enforce turn and player slot
        if (isOnlineGame) {
            const currentTurnColor = chess.turn();
            if ((playerSlot === 1 && currentTurnColor !== 'w') || (playerSlot === 2 && currentTurnColor !== 'b')) {
                return;
            }
        }

        // If clicking on existing move target
        const matchedMove = legalMovesForSelected.find(m => m.to === square);
        if (matchedMove) {
            handleUserAttemptMove(matchedMove);
            return;
        }

        // Otherwise select piece on square if owned
        const piece = piecesGrid[row][col];
        if (piece && piece.isAlive && piece.color === chess.turn()) {
            selectedSquare = square;
            legalMovesForSelected = chess.moves({ square: square, verbose: true });

            // Play unit selection voiceline for local team only
            if (isLocalPlayerColor(piece.color)) {
                playPieceVoice(piece.type, 'select', piece.color);
            }

            // Position selection ring
            const pos = boardToWorld(row, col);
            selectionRing.position.set(pos.x, BOARD_HEIGHT + 0.012, pos.z);
            selectionRing.visible = true;

            highlightMoveTargets(legalMovesForSelected);
        } else {
            selectedSquare = null;
            legalMovesForSelected = [];
            clearMoveTargets();
        }
    }

    function handleUserAttemptMove(move) {
        // Check for Pawn Promotion: requires choosing promotion piece
        if (move.flags && move.flags.includes('p')) {
            pendingPromotionMove = move;
            document.getElementById('pawnPromotionModal')?.classList.remove('hidden');
            return;
        }

        executeValidatedMove(move.from, move.to, move.promotion || 'q');
    }

    function executeValidatedMove(from, to, promotion = 'q') {
        const fromPos = fileRankToRowCol(from);
        const movingPiece = piecesGrid[fromPos.row][fromPos.col];

        const moveResult = chess.move({
            from: from,
            to: to,
            promotion: promotion
        });

        if (!moveResult) return;

        // Play move confirmation voiceline for local team only
        if (movingPiece && isLocalPlayerColor(movingPiece.color)) {
            playPieceVoice(movingPiece.type, 'move', movingPiece.color);
        }

        clearMoveTargets();

        // Animate the piece in 3D
        animateMove(moveResult, () => {
            updateTurnBanner();

            // Broadcast to SignalR if in online match
            if (isOnlineGame && signalRConnection) {
                signalRConnection.invoke(
                    "SendMove",
                    currentRoomCode,
                    from,
                    to,
                    promotion,
                    chess.fen(),
                    chess.in_check(),
                    chess.in_checkmate()
                ).catch(err => console.error("[KongorChess] SendMove failed:", err));
            }

            // Trigger AI Bot if local solo match
            if (!isGameOver && gameMode.startsWith('vs_bot_') && chess.turn() === 'b') {
                triggerBotMove();
            }
        });
    }

    // =========================================================================
    // 9. Instant Solo Bot Engine (Minimax with Alpha-Beta)
    // =========================================================================

    const PIECE_VALUES = { p: 10, n: 30, b: 30, r: 50, q: 90, k: 900 };

    function evaluateBoard(currentChess) {
        let total = 0;
        const board = currentChess.board();
        for (let r = 0; r < 8; r++) {
            for (let c = 0; c < 8; c++) {
                const p = board[r][c];
                if (p) {
                    const val = PIECE_VALUES[p.type] || 0;
                    total += p.color === 'w' ? val : -val;
                }
            }
        }
        return total;
    }

    function minimax(currentChess, depth, alpha, beta, isMaximizing) {
        if (depth === 0 || currentChess.game_over()) {
            return evaluateBoard(currentChess);
        }

        const legal = currentChess.moves();
        if (isMaximizing) {
            let maxEval = -Infinity;
            for (const m of legal) {
                currentChess.move(m);
                const ev = minimax(currentChess, depth - 1, alpha, beta, false);
                currentChess.undo();
                maxEval = Math.max(maxEval, ev);
                alpha = Math.max(alpha, ev);
                if (beta <= alpha) break;
            }
            return maxEval;
        } else {
            let minEval = Infinity;
            for (const m of legal) {
                currentChess.move(m);
                const ev = minimax(currentChess, depth - 1, alpha, beta, true);
                currentChess.undo();
                minEval = Math.min(minEval, ev);
                beta = Math.min(beta, ev);
                if (beta <= alpha) break;
            }
            return minEval;
        }
    }

    function triggerBotMove() {
        const indicator = document.getElementById('botThinkingIndicator');
        if (indicator) indicator.classList.remove('hidden');

        const depth = gameMode === 'vs_bot_master' ? 3 : (gameMode === 'vs_bot_challenger' ? 2 : 1);

        setTimeout(() => {
            const moves = chess.moves({ verbose: true });
            if (moves.length === 0) {
                if (indicator) indicator.classList.add('hidden');
                return;
            }

            let bestMove = null;
            let bestScore = Infinity; // Bot is black (minimizing)

            for (const m of moves) {
                chess.move(m);
                const score = minimax(chess, depth, -Infinity, Infinity, true);
                chess.undo();

                if (score < bestScore) {
                    bestScore = score;
                    bestMove = m;
                }
            }

            if (indicator) indicator.classList.add('hidden');

            if (bestMove) {
                executeValidatedMove(bestMove.from, bestMove.to, bestMove.promotion || 'q');
            }
        }, 350);
    }

    // =========================================================================
    // 10. Turn Banner, HUD & Dual Chess Clocks
    // =========================================================================

    function updateTurnBanner() {
        const turnTitle = document.getElementById('turnTitle');
        const turnDot = document.getElementById('turnStatusDot');
        const isWhite = chess.turn() === 'w';

        if (turnTitle) {
            turnTitle.innerText = isWhite ? "Legion's Turn (White)" : "Hellbourne's Turn (Black)";
            turnTitle.className = isWhite
                ? "text-xs font-black tracking-wider uppercase font-mono text-emerald-300"
                : "text-xs font-black tracking-wider uppercase font-mono text-rose-400";
        }
        if (turnDot) {
            turnDot.className = isWhite
                ? "w-3 h-3 rounded-full bg-emerald-400 animate-pulse"
                : "w-3 h-3 rounded-full bg-rose-500 animate-pulse";
        }
    }

    function formatTime(ms) {
        if (ms === intMaxFallback(ms)) return "∞";
        const totalSec = Math.max(0, Math.floor(ms / 1000));
        const min = Math.floor(totalSec / 60);
        const sec = totalSec % 60;
        return `${min.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
    }

    function intMaxFallback(val) {
        return val > 3600000 ? val : -1;
    }

    function startClockTimer() {
        if (clockInterval) clearInterval(clockInterval);
        lastClockTick = Date.now();

        clockInterval = setInterval(() => {
            if (isGameOver || timeControl === 'unlimited') return;

            const now = Date.now();
            const elapsed = now - lastClockTick;
            lastClockTick = now;

            if (chess.turn() === 'w') {
                p1ClockMs = Math.max(0, p1ClockMs - elapsed);
                if (p1ClockMs <= 0 && !isGameOver) {
                    isGameOver = true;
                    showVictoryModal("Hellbourne", "White Timed Out");
                }
            } else {
                p2ClockMs = Math.max(0, p2ClockMs - elapsed);
                if (p2ClockMs <= 0 && !isGameOver) {
                    isGameOver = true;
                    showVictoryModal("Legion", "Black Timed Out");
                }
            }

            const c1 = document.getElementById('clockLegion');
            const c2 = document.getElementById('clockHellbourne');
            if (c1) c1.innerHTML = `<i class="fas fa-chess-king text-[9px] mr-1"></i>${formatTime(p1ClockMs)}`;
            if (c2) c2.innerHTML = `<i class="fas fa-fire text-[9px] mr-1"></i>${formatTime(p2ClockMs)}`;
        }, 200);
    }

    // =========================================================================
    // 11. WebRTC Camera AR Passthrough
    // =========================================================================

    async function toggleCameraAR() {
        if (isCameraActive) {
            stopCameraAR();
        } else {
            await startCameraAR();
        }
        updateBoardTransparency();
    }

    async function startCameraAR() {
        const video = document.getElementById('chessVideo');
        const btnText = document.getElementById('chessCameraBtnText');
        const btn = document.getElementById('btnChessToggleCamera');

        try {
            const constraints = {
                video: {
                    facingMode: { ideal: "environment" },
                    width: { ideal: 1920 },
                    height: { ideal: 1080 }
                }
            };
            mediaStream = await navigator.mediaDevices.getUserMedia(constraints);
            if (video) {
                video.srcObject = mediaStream;
                video.classList.remove('hidden');
            }

            isCameraActive = true;
            if (btnText) btnText.innerText = "Stop AR Camera";
            if (btn) btn.classList.add('border-emerald-500', 'bg-emerald-950/40');
            updateBoardTransparency();
        } catch (err) {
            alert("Camera access was denied or is unavailable. Continuing in 3D Studio Desk Mode!");
        }
    }

    function stopCameraAR() {
        if (mediaStream) {
            mediaStream.getTracks().forEach(t => t.stop());
            mediaStream = null;
        }
        const video = document.getElementById('chessVideo');
        if (video) video.classList.add('hidden');

        isCameraActive = false;
        const btnText = document.getElementById('chessCameraBtnText');
        const btn = document.getElementById('btnChessToggleCamera');
        if (btnText) btnText.innerText = "Enable AR Camera";
        if (btn) btn.classList.remove('border-emerald-500', 'bg-emerald-950/40');
        updateBoardTransparency();
    }

    // =========================================================================
    // 12. SignalR 1v1 Online Multiplayer Integration
    // =========================================================================

    async function connectSignalR() {
        if (signalRConnection && signalRConnection.state === signalR.HubConnectionState.Connected) {
            return;
        }

        signalRConnection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/kongor-chess")
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Information)
            .build();

        signalRConnection.on("OnRoomJoined", (roomCode, slot, p1Name, p2Name, status, tc, t1, t2) => {
            playerSlot = slot;
            currentRoomCode = roomCode;
            isOnlineGame = true;
            gameMode = 'online_1v1';

            const badge = document.getElementById('chessOnlineBadge');
            const label = document.getElementById('chessOnlineRoomLabel');
            if (badge && label) {
                badge.classList.remove('hidden');
                badge.classList.add('flex');
                label.innerText = `${roomCode} • ${slot === 1 ? 'Legion' : 'Hellbourne'}`;
            }

            document.getElementById('chessWaitingRoomCodeText').innerText = roomCode;
            document.getElementById('chessShareUrlText').innerText = `${window.location.origin}/ar-mini-games/chess?room=${roomCode}`;

            document.getElementById('chessOnlineLobbyModal')?.classList.add('hidden');

            if (status === 'WaitingForOpponent' || !p2Name) {
                document.getElementById('chessWaitingOverlay')?.classList.remove('hidden');
            } else if (status === 'WaitingForReady') {
                document.getElementById('chessWaitingOverlay')?.classList.add('hidden');
                document.getElementById('chessArReadyModal')?.classList.remove('hidden');
            }
        });

        signalRConnection.on("OnOpponentJoined", (slot, oppName) => {
            document.getElementById('chessWaitingOverlay')?.classList.add('hidden');
            document.getElementById('chessArReadyModal')?.classList.remove('hidden');
            showBannerNotification(`${oppName} ENTERED THE DUEL!`, "fa-user-check");
            playAudioFx('announcer', 'prepare_for_battle');
        });

        signalRConnection.on("OnMatchPromptReady", () => {
            document.getElementById('chessWaitingOverlay')?.classList.add('hidden');
            document.getElementById('chessArReadyModal')?.classList.remove('hidden');
        });

        signalRConnection.on("OnPlayerReadyStatus", (slot, isReady, wantsAr) => {
            const statusEl = document.getElementById(slot === 1 ? 'chessP1ReadyStatus' : 'chessP2ReadyStatus');
            if (statusEl) {
                statusEl.innerHTML = isReady
                    ? '<i class="fas fa-check text-emerald-400"></i> <span class="text-emerald-400">Ready!</span>'
                    : '<i class="fas fa-clock text-amber-400"></i> Not Ready';
            }
        });

        signalRConnection.on("OnMatchStarted", (p1Name, p2Name, startTurn, tc, t1, t2) => {
            document.getElementById('chessArReadyModal')?.classList.add('hidden');
            document.getElementById('chessWaitingOverlay')?.classList.add('hidden');
            showBannerNotification("MATCH STARTED! GOOD LUCK.", "fa-play");
            playAudioFx('announcer', 'prepare_for_battle');

            p1ClockMs = t1;
            p2ClockMs = t2;
            restartMatch();
        });

        signalRConnection.on("OnOpponentMove", (from, to, promotion, fen, isCheck, isCheckmate, t1, t2) => {
            p1ClockMs = t1;
            p2ClockMs = t2;
            executeValidatedMove(from, to, promotion || 'q');
        });

        signalRConnection.on("OnClockSync", (t1, t2, turn) => {
            p1ClockMs = t1;
            p2ClockMs = t2;
        });

        signalRConnection.on("OnGameOver", (winnerSlot, reason) => {
            isGameOver = true;
            const winner = winnerSlot === 1 ? 'Legion' : 'Hellbourne';
            showVictoryModal(winner, reason);
        });

        signalRConnection.on("OnOpponentLeft", (slot) => {
            showBannerNotification("Opponent disconnected.", "fa-user-slash");
        });

        signalRConnection.on("OnMatchReset", () => {
            restartMatch();
        });

        await signalRConnection.start();
    }

    // =========================================================================
    // 13. UI Modals & Event Listeners
    // =========================================================================

    function showVictoryModal(winner, reason) {
        const modal = document.getElementById('chessVictoryModal');
        const title = document.getElementById('victoryTitle');
        const subtitle = document.getElementById('victorySubtitle');
        const details = document.getElementById('victoryDetails');

        if (!modal) return;

        if (title) title.innerText = winner === 'Draw' ? "STALEMATE" : "CHECKMATE!";
        if (subtitle) {
            subtitle.innerText = winner === 'Draw'
                ? "The game ended in a draw."
                : `${winner} claims total victory over Newerth!`;
        }
        if (details) details.innerText = `Result: ${reason}`;

        modal.classList.remove('hidden');
    }

    function restartMatch() {
        chess.reset();
        isGameOver = false;
        hasFirstBloodOccurred = false;
        isAnimationBusy = false;
        hideHoverBadge();

        p1ClockMs = timeControl === '3m' ? 180000 : (timeControl === '5m' ? 300000 : (timeControl === '10m' ? 600000 : (timeControl === '30m' ? 1800000 : intMaxFallback(99999999))));
        p2ClockMs = p1ClockMs;

        document.getElementById('chessVictoryModal')?.classList.add('hidden');
        document.getElementById('pawnPromotionModal')?.classList.add('hidden');

        populateBoardFromChess();
        updateTurnBanner();
        startClockTimer();
    }

    function setCameraPreset(preset) {
        if (!camera || !controls) return;

        if (preset === 'tabletop') {
            camera.position.set(0, 1.45, 1.55);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'topdown') {
            camera.position.set(0, 2.4, 0.001);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'legion') {
            camera.position.set(0, 1.1, 1.6);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'hellbourne') {
            camera.position.set(0, 1.1, -1.6);
            controls.target.set(0, BOARD_HEIGHT, 0);
        }
        controls.update();
    }

    function initEventListeners() {
        const container = document.getElementById('chessCanvasContainer');
        if (container) {
            container.addEventListener('click', onPointerClick);
            container.addEventListener('pointermove', onPointerMove);
            container.addEventListener('pointerleave', onPointerLeave);
        }

        // Camera Presets
        document.getElementById('btnViewTabletop')?.addEventListener('click', () => setCameraPreset('tabletop'));
        document.getElementById('btnViewTopDown')?.addEventListener('click', () => setCameraPreset('topdown'));
        document.getElementById('btnViewLegion')?.addEventListener('click', () => setCameraPreset('legion'));
        document.getElementById('btnViewHellbourne')?.addEventListener('click', () => setCameraPreset('hellbourne'));

        // Controls
        document.getElementById('btnChessToggleCamera')?.addEventListener('click', toggleCameraAR);
        document.getElementById('btnChessReset')?.addEventListener('click', restartMatch);
        document.getElementById('btnModalPlayAgain')?.addEventListener('click', restartMatch);
        document.getElementById('btnModalDismiss')?.addEventListener('click', () => {
            document.getElementById('chessVictoryModal')?.classList.add('hidden');
        });

        // Game Mode & Time Control dropdowns
        document.getElementById('selectGameMode')?.addEventListener('change', (e) => {
            gameMode = e.target.value;
            if (gameMode === 'online_1v1') {
                document.getElementById('chessOnlineLobbyModal')?.classList.remove('hidden');
            } else {
                restartMatch();
            }
        });

        document.getElementById('selectTimeControl')?.addEventListener('change', (e) => {
            timeControl = e.target.value;
            restartMatch();
        });

        // Online Lobby Buttons
        document.getElementById('btnOpenOnlineLobby')?.addEventListener('click', () => {
            document.getElementById('chessOnlineLobbyModal')?.classList.remove('hidden');
        });

        document.getElementById('btnCloseOnlineLobby')?.addEventListener('click', () => {
            document.getElementById('chessOnlineLobbyModal')?.classList.add('hidden');
        });

        document.getElementById('btnGenerateRandomCode')?.addEventListener('click', () => {
            const input = document.getElementById('inputChessRoomCode');
            if (input) input.value = 'CH' + Math.floor(1000 + Math.random() * 9000);
        });

        document.getElementById('btnJoinOnlineRoom')?.addEventListener('click', async () => {
            const codeInput = document.getElementById('inputChessRoomCode');
            const nameInput = document.getElementById('inputChessPlayerName');
            const tcSelect = document.getElementById('selectLobbyTimeControl');

            const roomCode = (codeInput?.value || 'KNG1').toUpperCase().trim();
            playerName = nameInput?.value?.trim() || `Player_${Math.floor(Math.random() * 999)}`;
            timeControl = tcSelect?.value || '5m';

            await connectSignalR();
            signalRConnection.invoke("JoinRoom", roomCode, playerName, null, timeControl);
        });

        document.getElementById('btnConfirmReady')?.addEventListener('click', () => {
            const wantsAr = document.getElementById('cbReadyWantsAr')?.checked || false;
            if (signalRConnection && currentRoomCode) {
                signalRConnection.invoke("SetReady", currentRoomCode, wantsAr);
            }
        });

        document.getElementById('btnCopyChessShareLink')?.addEventListener('click', () => {
            const shareUrl = `${window.location.origin}/ar-mini-games/chess?room=${currentRoomCode || 'KNG1'}`;
            navigator.clipboard.writeText(shareUrl).then(() => {
                alert("Share link copied to clipboard!");
            });
        });

        // Pawn Promotion Modal Buttons
        document.querySelectorAll('.btnPromotionChoice').forEach((btn) => {
            btn.addEventListener('click', (e) => {
                const choice = e.currentTarget.dataset.piece;
                document.getElementById('pawnPromotionModal')?.classList.add('hidden');
                if (pendingPromotionMove) {
                    executeValidatedMove(pendingPromotionMove.from, pendingPromotionMove.to, choice);
                    pendingPromotionMove = null;
                }
            });
        });

        // Audio Toggle
        document.getElementById('btnChessToggleAudio')?.addEventListener('click', () => {
            isAudioMuted = !isAudioMuted;
            const icon = document.getElementById('chessAudioIcon');
            if (icon) {
                icon.className = isAudioMuted ? "fas fa-volume-mute text-rose-400" : "fas fa-volume-up text-emerald-400";
            }
        });

        // Undo (Local games)
        document.getElementById('btnChessUndo')?.addEventListener('click', () => {
            if (isOnlineGame || isAnimationBusy) return;
            chess.undo();
            if (gameMode.startsWith('vs_bot_')) chess.undo(); // Undo bot move too
            populateBoardFromChess();
            updateTurnBanner();
        });
    }

    // =========================================================================
    // 14. Initialization on Load
    // =========================================================================

    document.addEventListener('DOMContentLoaded', async () => {
        chess = new window.Chess();

        initThreeScene();
        await preloadAllModels();
        populateBoardFromChess();
        startClockTimer();

        // Check URL for direct room join or game mode
        const urlParams = new URLSearchParams(window.location.search);
        const urlRoom = urlParams.get('room');
        const urlMode = urlParams.get('mode');

        if (urlMode) {
            const modeSelect = document.getElementById('selectGameMode');
            if (modeSelect) modeSelect.value = urlMode;
            gameMode = urlMode;
            updateTurnBanner();
        }

        if (urlRoom) {
            const input = document.getElementById('inputChessRoomCode');
            if (input) input.value = urlRoom.toUpperCase();
            document.getElementById('chessOnlineLobbyModal')?.classList.remove('hidden');
        }
    });

    // Public window API
    window.KongorChess = {
        restartMatch: restartMatch,
        setCameraPreset: setCameraPreset,
        toggleCameraAR: toggleCameraAR,
        getChess: () => chess
    };

})();
