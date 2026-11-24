// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Project KONGOR: Monkey King Tabletop AR Checkers (Legion vs. Hellbourne)
 *
 * An authentic 3D Tabletop AR and Studio Desk Checkers experience starring
 * Monkey King, featuring:
 * - Borderless floating tabletop tiles for AR
 * - Dynamic AR transparency for squares we are not standing on
 * - Heavenly Vault acrobatic 360-degree front flips over enemy pieces (using W animation ability_2_roll / ability_2)
 * - First Blood announcer triggering exclusively on the first kill of the game
 * - Rich pool of authentic Monkey King unit interaction voice lines
 * - 3D King crowns, Minimax AI bot opponent, and classic HoN Caldavar aesthetic.
 */

(function () {
    "use strict";

    // --- Configuration & Constants ---
    const BOARD_SIZE = 8;
    const TILE_SIZE = 0.22; // In meters for tabletop scale
    const BOARD_HEIGHT = 0.012; // Sleek low-profile floating tiles on table
    const PIECE_BASE_RADIUS = 0.08;
    const TEAM_LEGION = 'legion';
    const TEAM_HELLBOURNE = 'hellbourne';

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
    let shockwaveGroup = null;

    // --- Asset & Figurine State ---
    let baseHeroGltf = null;
    let pieces = []; // Array of Piece objects
    let piecesGrid = Array(BOARD_SIZE).fill(null).map(() => Array(BOARD_SIZE).fill(null));

    // --- Gameplay State ---
    let currentTurn = TEAM_LEGION;
    let selectedPiece = null;
    let legalMovesForSelected = [];
    let isMultiJumping = false;
    let multiJumpingPiece = null;
    let moveHistory = [];
    let isGameOver = false;
    let moveCount = 0;
    let comboStreak = 0;
    let isAnimationBusy = false;
    let hasFirstBloodOccurred = false; // "Bloodlust" / First Blood only on first kill of match

    // --- Game Settings ---
    let gameMode = 'vs_bot_novice'; // 'vs_bot_novice' | 'vs_bot_challenger' | 'pass_and_play' | 'bot_vs_bot' | 'online_1v1'
    let isCameraActive = false;
    let mediaStream = null;
    let isAudioMuted = false;
    let activeAudio = null;
    const activeAudioSet = new Set();

    // --- SignalR Online Multiplayer State ---
    let signalRConnection = null;
    let currentRoomCode = null;
    let playerSlot = 1; // 1 = Legion (Green, Host), 2 = Hellbourne (Red, Challenger)
    let playerName = 'MonkeyKing';
    let isLocalReady = false;
    let isRemoteReady = false;
    let isOnlineGame = false;

    // --- Raycasting ---
    let raycaster = null;
    let pointer = null;

    // --- Rich Voice Line Pools for Unit Interaction ---
    const voicePools = {
        select: [
            'select_1', 'select_2', 'select_3', 'select_4', 'select_5',
            'select_flavour_1', 'select_flavour_2', 'taunt_prior'
        ],
        move: [
            'move_1', 'move_2', 'move_3', 'move_4', 'move_5',
            'ability_1'
        ],
        vault: [
            'ability_2', 'attack_1', 'attack_2', 'attack_3', 'attack_4',
            'taunt_after'
        ],
        death: [
            'death_1', 'death_grunt_1', 'death_grunt_2', 'death'
        ],
        king: [
            'taunt_1', 'taunt_after', 'hero_select', 'select_flavour_1'
        ]
    };
    let lastPlayedVoice = {};

    function playHeroVoice(type) {
        const pool = voicePools[type] || ['select_1'];
        const last = lastPlayedVoice[type];
        let candidates = pool.filter(v => v !== last);
        if (candidates.length === 0) candidates = pool;
        const chosen = candidates[Math.floor(Math.random() * candidates.length)];
        lastPlayedVoice[type] = chosen;
        playAudioFx('hero', chosen);
    }

    // =========================================================================
    // 1. Checkers Rules & Move Generation
    // =========================================================================

    function isValidSquare(row, col) {
        return row >= 0 && row < BOARD_SIZE && col >= 0 && col < BOARD_SIZE;
    }

    function isPlayableSquare(row, col) {
        return isValidSquare(row, col) && ((row + col) % 2 === 1);
    }

    function boardToWorld(row, col) {
        const x = (col - 3.5) * TILE_SIZE;
        const z = (row - 3.5) * TILE_SIZE;
        return new THREE.Vector3(x, BOARD_HEIGHT, z);
    }

    function getLegalMovesForPiece(piece, grid = piecesGrid, mustJumpOnly = false) {
        if (!piece || !piece.isAlive) return [];

        const moves = [];
        const r = piece.row;
        const c = piece.col;

        // Determine forward directions
        let forwardDirs = [];
        if (piece.isKing) {
            forwardDirs = [
                [-1, -1], [-1, 1], [1, -1], [1, 1]
            ];
        } else if (piece.team === TEAM_LEGION) {
            // Legion moves towards Hellbourne (rows 0 -> 7)
            forwardDirs = [[1, -1], [1, 1]];
        } else {
            // Hellbourne moves towards Legion (rows 7 -> 0)
            forwardDirs = [[-1, -1], [-1, 1]];
        }

        // 1. Captures / Jumps (distance 2)
        const jumpDirs = piece.isKing ? [
            [-1, -1], [-1, 1], [1, -1], [1, 1]
        ] : forwardDirs;

        for (const [dr, dc] of jumpDirs) {
            const midR = r + dr;
            const midC = c + dc;
            const destR = r + dr * 2;
            const destC = c + dc * 2;

            if (isPlayableSquare(destR, destC)) {
                const midPiece = grid[midR][midC];
                const destPiece = grid[destR][destC];

                if (midPiece && midPiece.isAlive && midPiece.team !== piece.team && !destPiece) {
                    moves.push({
                        from: { row: r, col: c },
                        to: { row: destR, col: destC },
                        isJump: true,
                        capturedPiece: midPiece,
                        capturedPos: { row: midR, col: midC }
                    });
                }
            }
        }

        // 2. Regular Slides (distance 1) - only if not restricted to jumping
        if (!mustJumpOnly && moves.length === 0) {
            for (const [dr, dc] of forwardDirs) {
                const destR = r + dr;
                const destC = c + dc;

                if (isPlayableSquare(destR, destC) && !grid[destR][destC]) {
                    moves.push({
                        from: { row: r, col: c },
                        to: { row: destR, col: destC },
                        isJump: false,
                        capturedPiece: null,
                        capturedPos: null
                    });
                }
            }
        }

        return moves;
    }

    function getAllLegalMoves(team, grid = piecesGrid) {
        let allJumps = [];
        let allSlides = [];

        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const p = grid[r][c];
                if (p && p.isAlive && p.team === team) {
                    const moves = getLegalMovesForPiece(p, grid, false);
                    for (const m of moves) {
                        if (m.isJump) allJumps.push({ piece: p, move: m });
                        else allSlides.push({ piece: p, move: m });
                    }
                }
            }
        }

        // If jumps exist, checkers rules prioritize jumps
        return allJumps.length > 0 ? allJumps : allSlides;
    }

    // =========================================================================
    // 2. Minimax AI Engine (Novice & Challenger)
    // =========================================================================

    function evaluateBoard(grid, botTeam) {
        let score = 0;
        const opponentTeam = botTeam === TEAM_LEGION ? TEAM_HELLBOURNE : TEAM_LEGION;

        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const p = grid[r][c];
                if (!p || !p.isAlive) continue;

                let val = p.isKing ? 185 : 100;

                // Center board control bonus
                if (r >= 2 && r <= 5 && c >= 2 && c <= 5) val += 15;

                // Advancement bonus
                if (!p.isKing) {
                    const advance = p.team === TEAM_LEGION ? r : (7 - r);
                    val += advance * 6;
                }

                // Back-rank defense bonus
                if ((p.team === TEAM_LEGION && r === 0) || (p.team === TEAM_HELLBOURNE && r === 7)) {
                    val += 18;
                }

                if (p.team === botTeam) score += val;
                else score -= val;
            }
        }
        return score;
    }

    function simulateMoveOnGrid(grid, moveItem) {
        const newGrid = grid.map(row => row.slice());
        const { piece, move } = moveItem;

        const clonedPiece = {
            ...piece,
            row: move.to.row,
            col: move.to.col,
            isKing: piece.isKing || (piece.team === TEAM_LEGION && move.to.row === 7) || (piece.team === TEAM_HELLBOURNE && move.to.row === 0)
        };

        newGrid[move.from.row][move.from.col] = null;
        newGrid[move.to.row][move.to.col] = clonedPiece;

        if (move.isJump && move.capturedPos) {
            newGrid[move.capturedPos.row][move.capturedPos.col] = null;
        }

        return newGrid;
    }

    function minimax(grid, depth, isMaximizing, botTeam, alpha, beta) {
        const currentTurnTeam = isMaximizing ? botTeam : (botTeam === TEAM_LEGION ? TEAM_HELLBOURNE : TEAM_LEGION);
        const legal = getAllLegalMoves(currentTurnTeam, grid);

        if (legal.length === 0) {
            return isMaximizing ? -99999 : 99999;
        }

        if (depth === 0) {
            return evaluateBoard(grid, botTeam);
        }

        if (isMaximizing) {
            let maxEval = -Infinity;
            for (const item of legal) {
                const nextGrid = simulateMoveOnGrid(grid, item);
                const evalVal = minimax(nextGrid, depth - 1, false, botTeam, alpha, beta);
                maxEval = Math.max(maxEval, evalVal);
                alpha = Math.max(alpha, evalVal);
                if (beta <= alpha) break;
            }
            return maxEval;
        } else {
            let minEval = Infinity;
            for (const item of legal) {
                const nextGrid = simulateMoveOnGrid(grid, item);
                const evalVal = minimax(nextGrid, depth - 1, true, botTeam, alpha, beta);
                minEval = Math.min(minEval, evalVal);
                beta = Math.min(beta, evalVal);
                if (beta <= alpha) break;
            }
            return minEval;
        }
    }

    function chooseBotMove(botTeam) {
        const legal = getAllLegalMoves(botTeam, piecesGrid);
        if (legal.length === 0) return null;

        // Novice: Fast, prioritize jump captures, or random tactical move
        if (gameMode === 'vs_bot_novice') {
            const jumps = legal.filter(l => l.move.isJump);
            if (jumps.length > 0) {
                return jumps[Math.floor(Math.random() * jumps.length)];
            }
            return legal[Math.floor(Math.random() * legal.length)];
        }

        // Challenger: 3-ply Minimax with Alpha-Beta Pruning
        const searchDepth = 3;
        let bestScore = -Infinity;
        let bestMove = legal[0];

        for (const item of legal) {
            const nextGrid = simulateMoveOnGrid(piecesGrid, item);
            const score = minimax(nextGrid, searchDepth - 1, false, botTeam, -Infinity, Infinity);
            if (score > bestScore) {
                bestScore = score;
                bestMove = item;
            }
        }

        return bestMove;
    }

    // =========================================================================
    // 3. Three.js Scene, Camera, & Floating Borderless AR Board Creation
    // =========================================================================

    function initThreeScene() {
        const container = document.getElementById('checkersCanvasContainer');
        const canvas = document.getElementById('checkersCanvas');
        if (!container || !canvas) return;

        clock = new THREE.Clock();
        raycaster = new THREE.Raycaster();
        pointer = new THREE.Vector2();

        // 1. Scene
        scene = new THREE.Scene();

        // 2. Camera (Perspective for 3D tabletop)
        const width = container.clientWidth || window.innerWidth;
        const height = container.clientHeight || window.innerHeight;
        camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 100);
        camera.position.set(0, 1.6, 2.2);

        // 3. WebGL Renderer with Alpha for live camera feed
        renderer = new THREE.WebGLRenderer({
            canvas: canvas,
            alpha: true,
            antialias: true,
            powerPreference: 'high-performance'
        });
        renderer.setSize(width, height);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.shadowMap.enabled = true;
        renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        if (window.KongorViewerCore) {
            window.KongorViewerCore.configureRendererColorSpace(renderer);
        } else if ('outputColorSpace' in renderer && THREE.SRGBColorSpace) {
            renderer.outputColorSpace = THREE.SRGBColorSpace;
        }
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.35;

        // 4. Orbit Controls
        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.06;
        controls.maxPolarAngle = Math.PI / 2 - 0.05; // Do not go below table
        controls.minDistance = 0.5;
        controls.maxDistance = 5.0;
        controls.target.set(0, BOARD_HEIGHT, 0);
        setCameraPreset('tabletop');

        // 5. Lighting Setup (Sunlight for Legion, Ominous Brimstone for Hellbourne)
        setupLighting();

        // 6. Build Borderless Floating Board (No chunky borders, transparent unoccupied tiles)
        buildCaldavarBoard();

        // 7. Visual Markers & Shockwaves
        setupVisualMarkers();

        // 8. Event Listeners
        window.addEventListener('resize', onWindowResize);
        bindInteractions();

        // 9. Start Render Loop
        renderer.setAnimationLoop(renderLoop);
        document.addEventListener('visibilitychange', () => {
            if (!renderer) return;
            if (document.hidden && (!renderer.xr || !renderer.xr.isPresenting)) {
                renderer.setAnimationLoop(null);
            } else if (!document.hidden) {
                if (clock) clock.getDelta();
                renderer.setAnimationLoop(renderLoop);
            }
        });
    }

    function setupLighting() {
        const cal = (val) => (window.KongorViewerCore && window.KongorViewerCore.calibrateLightIntensity)
            ? window.KongorViewerCore.calibrateLightIntensity(val)
            : val * Math.PI;

        // Hemisphere sky/ground balance + Ambient fill
        const hemiLight = new THREE.HemisphereLight(0xffffff, 0x334155, cal(1.0));
        hemiLight.position.set(0, 6.0, 0);
        scene.add(hemiLight);

        const ambientLight = new THREE.AmbientLight(0xffffff, cal(1.05));
        scene.add(ambientLight);

        // South Key Light: Sunlit Gold (Legion warmth)
        const legionSun = new THREE.DirectionalLight(0xfff3d4, cal(1.6));
        legionSun.position.set(1.5, 4.0, 3.5);
        legionSun.castShadow = true;
        legionSun.shadow.mapSize.width = 1024;
        legionSun.shadow.mapSize.height = 1024;
        legionSun.shadow.camera.near = 0.2;
        legionSun.shadow.camera.far = 10;
        legionSun.shadow.bias = -0.0002;
        scene.add(legionSun);

        // North Key/Fill Light: Balanced Warm-Rose Studio Light so Hellbourne Monkey Kings are brightly lit and never washed out in solid red
        const hellbourneFill = new THREE.DirectionalLight(0xfff1f2, cal(1.45));
        hellbourneFill.position.set(-1.5, 3.8, -3.5);
        scene.add(hellbourneFill);

        // Center Cyan River Fill
        const riverAura = new THREE.PointLight(0x38bdf8, cal(0.8), 3.0);
        riverAura.position.set(0, 0.5, 0);
        scene.add(riverAura);
    }

    function buildCaldavarBoard() {
        boardGroup = new THREE.Group();
        scene.add(boardGroup);

        const boardTotalWidth = BOARD_SIZE * TILE_SIZE; // ~1.76m

        // --- Clean Floating Tiles (No outer borders) ---
        // Thick outer stone slab and borders are REMOVED so the board sits cleanly on desk/table in AR!
        const tileGeo = new THREE.BoxGeometry(TILE_SIZE * 0.96, BOARD_HEIGHT, TILE_SIZE * 0.96);

        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const pos = boardToWorld(r, c);
                const isDark = (r + c) % 2 === 1;

                if (!isDark) {
                    // Light Non-Playable Squares: Checkers never stands on these!
                    // Kept completely invisible/transparent so the real desk or floor shows right through!
                    const ghostMat = new THREE.MeshBasicMaterial({ visible: false, transparent: true, opacity: 0 });
                    const tileMesh = new THREE.Mesh(tileGeo, ghostMat);
                    tileMesh.position.set(pos.x, BOARD_HEIGHT / 2, pos.z);
                    tileMesh.visible = false;
                    tileMesh.userData = { row: r, col: c, isDark: false };
                    boardGroup.add(tileMesh);
                    tileMeshes[`${r},${c}`] = tileMesh;
                    continue;
                }

                // Dark Playable Squares (Dynamically tinted to match occupying team: Legion Green vs Hellbourne Red)
                const tileMat = new THREE.MeshStandardMaterial({
                    color: 0x0f172a, // Neutral dark stone
                    roughness: 0.5,
                    metalness: 0.4,
                    emissive: 0x1e293b,
                    emissiveIntensity: 0.15,
                    transparent: true,
                    opacity: 0.22
                });

                const tileMesh = new THREE.Mesh(tileGeo, tileMat);
                tileMesh.position.set(pos.x, BOARD_HEIGHT / 2, pos.z);
                tileMesh.receiveShadow = true;
                tileMesh.userData = { row: r, col: c, isDark: true };

                // Add delicate glowing edge perimeter line
                const edgeGeo = new THREE.EdgesGeometry(tileGeo);
                const edgeMat = new THREE.LineBasicMaterial({
                    color: 0x334155,
                    transparent: true,
                    opacity: 0.35
                });
                const edgeLine = new THREE.LineSegments(edgeGeo, edgeMat);
                tileMesh.add(edgeLine);
                tileMesh.userData.edgeLine = edgeLine;

                boardGroup.add(tileMesh);
                tileMeshes[`${r},${c}`] = tileMesh;
            }
        }

        // --- Center Dividing Seam (River of Caldavar) ---
        const riverGeo = new THREE.BoxGeometry(boardTotalWidth, 0.003, 0.015);
        const riverMat = new THREE.MeshStandardMaterial({
            color: 0x0284c7,
            roughness: 0.1,
            metalness: 0.8,
            emissive: 0x0ea5e9,
            emissiveIntensity: 0.6,
            transparent: true,
            opacity: 0.75
        });
        const riverMesh = new THREE.Mesh(riverGeo, riverMat);
        riverMesh.position.set(0, BOARD_HEIGHT + 0.002, 0);
        boardGroup.add(riverMesh);

        // --- Tabletop Soft Contact Shadow Plane (Real tabletop shadow receiver) ---
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
     * - Squares occupied by LEGION: Solid 1.0 Radiant Emerald Jade (Green)
     * - Squares occupied by HELLBOURNE: Solid 1.0 Volcanic Crimson Obsidian (Red)
     * - Squares we are NOT standing on: Transparent neutral dark stone (opacity 0.08 in AR, 0.22 in 3D studio)
     */
    function updateBoardTransparency() {
        for (let r = 0; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                const tileMesh = tileMeshes[`${r},${c}`];
                if (!tileMesh) continue;

                const isDark = (r + c) % 2 === 1;
                if (!isDark) {
                    tileMesh.visible = false;
                    continue;
                }

                const occupyingPiece = piecesGrid[r][c];
                const isOccupied = occupyingPiece !== null && occupyingPiece.isAlive;

                tileMesh.material.transparent = true;

                if (isOccupied) {
                    tileMesh.material.opacity = 1.0;
                    const isLegion = occupyingPiece.team === TEAM_LEGION;

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
                    // Empty square: transparent for AR, neutral dark stone
                    tileMesh.material.opacity = isCameraActive ? 0.08 : 0.22;
                    tileMesh.material.color.setHex(0x0f172a);
                    tileMesh.material.emissive.setHex(0x1e293b);
                    tileMesh.material.emissiveIntensity = 0.15;
                    tileMesh.material.roughness = 0.5;
                    tileMesh.material.metalness = 0.4;
                    if (tileMesh.userData.edgeLine) {
                        tileMesh.userData.edgeLine.material.color.setHex(0x334155);
                        tileMesh.userData.edgeLine.material.opacity = isCameraActive ? 0.15 : 0.35;
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

        // Target Destination Markers Group
        targetMarkerGroup = new THREE.Group();
        scene.add(targetMarkerGroup);

        // Slam Shockwave Ring Group
        shockwaveGroup = new THREE.Group();
        scene.add(shockwaveGroup);
    }

    function highlightMoveTargets(legalMoves) {
        clearMoveTargets();

        const dotGeo = new THREE.CircleGeometry(0.065, 24);

        for (const m of legalMoves) {
            const pos = boardToWorld(m.to.row, m.to.col);
            const isCapture = m.isJump;

            // Make the destination tile itself glow brightly
            const destTile = tileMeshes[`${m.to.row},${m.to.col}`];
            if (destTile && destTile.material) {
                destTile.material.opacity = 0.85;
                destTile.material.needsUpdate = true;
            }

            // Emerald/Gold for slide, Fiery Crimson for jump/vault
            const dotMat = new THREE.MeshBasicMaterial({
                color: isCapture ? 0xef4444 : 0x10b981,
                side: THREE.DoubleSide,
                transparent: true,
                opacity: 0.9
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

    function createSlamShockwave(worldX, worldZ) {
        const ringGeo = new THREE.RingGeometry(0.02, 0.05, 32);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0x38bdf8,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 1.0
        });
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.set(worldX, BOARD_HEIGHT + 0.015, worldZ);
        shockwaveGroup.add(ring);

        // Expand and fade out
        let scale = 1.0;
        let opacity = 1.0;
        const expandInterval = setInterval(() => {
            scale += 0.35;
            opacity -= 0.12;
            ring.scale.set(scale, scale, scale);
            ringMat.opacity = Math.max(0, opacity);
            if (opacity <= 0) {
                clearInterval(expandInterval);
                shockwaveGroup.remove(ring);
            }
        }, 30);
    }

    // =========================================================================
    // 4. Monkey King Figurine Summoning & Animation
    // =========================================================================

    async function loadMonkeyKingAsset() {
        const loadingProgress = document.getElementById('checkersLoadingProgress');
        if (loadingProgress) loadingProgress.innerText = "Streaming Monkey King 3D Mesh...";

        const candidateUrls = [
            '/api/v1/assets/3d/hero/monkey_king',
            '/api/v1/assets/3d/hero/monkeyking',
            '/assets/prebaked/models/monkey_king_default.glb'
        ];

        let buffer = null;
        for (const url of candidateUrls) {
            try {
                const resp = await fetch(url);
                if (resp.ok) {
                    buffer = await resp.arrayBuffer();
                    break;
                }
            } catch (e) {}
        }

        if (buffer && buffer.byteLength > 0) {
            return new Promise((resolve) => {
                const loader = window.KongorViewerCore ? window.KongorViewerCore.createKongorGLTFLoader() : new THREE.GLTFLoader();
                loader.parse(buffer, '', (gltf) => {
                    baseHeroGltf = gltf;
                    resolve(gltf);
                }, () => resolve(null));
            });
        }
        return null;
    }

    function createProceduralFigurine(team) {
        // Fallback procedural battle figurine if 3D mesh buffer is unavailable
        const group = new THREE.Group();
        const color = team === TEAM_LEGION ? 0xf59e0b : 0xdc2626;

        // Staff
        const staffGeo = new THREE.CylinderGeometry(0.008, 0.008, 0.32);
        const staffMat = new THREE.MeshStandardMaterial({ color: 0xfbbf24, metalness: 0.8, roughness: 0.2 });
        const staff = new THREE.Mesh(staffGeo, staffMat);
        staff.position.set(0.06, 0.15, 0);
        staff.rotation.z = 0.2;
        group.add(staff);

        // Body
        const bodyGeo = new THREE.ConeGeometry(0.05, 0.18, 16);
        const bodyMat = new THREE.MeshStandardMaterial({ color: color, roughness: 0.4, metalness: 0.3 });
        const body = new THREE.Mesh(bodyGeo, bodyMat);
        body.position.y = 0.10;
        group.add(body);

        // Head
        const headGeo = new THREE.SphereGeometry(0.042, 16, 16);
        const headMat = new THREE.MeshStandardMaterial({ color: 0x78350f, roughness: 0.8 });
        const head = new THREE.Mesh(headGeo, headMat);
        head.position.y = 0.21;
        group.add(head);

        return group;
    }

    function create3DCrown(team) {
        const crownGroup = new THREE.Group();
        const isLegion = team === TEAM_LEGION;

        if (isLegion) {
            // Radiant Golden Tiara
            const ringGeo = new THREE.CylinderGeometry(0.042, 0.038, 0.02, 16);
            const goldMat = new THREE.MeshStandardMaterial({
                color: 0xfbbf24,
                metalness: 0.9,
                roughness: 0.2,
                emissive: 0xd97706,
                emissiveIntensity: 0.3
            });
            const ring = new THREE.Mesh(ringGeo, goldMat);
            crownGroup.add(ring);

            // Spikes
            for (let i = 0; i < 5; i++) {
                const angle = (i / 5) * Math.PI * 2;
                const spikeGeo = new THREE.ConeGeometry(0.009, 0.035, 8);
                const spike = new THREE.Mesh(spikeGeo, goldMat);
                spike.position.set(Math.sin(angle) * 0.04, 0.022, Math.cos(angle) * 0.04);
                crownGroup.add(spike);
            }
        } else {
            // Demonic Spiked Horn Crown
            const hornMat = new THREE.MeshStandardMaterial({
                color: 0x18181b,
                metalness: 0.6,
                roughness: 0.3,
                emissive: 0xef4444,
                emissiveIntensity: 0.7
            });
            const leftHorn = new THREE.Mesh(new THREE.ConeGeometry(0.012, 0.05, 8), hornMat);
            leftHorn.position.set(-0.03, 0.025, 0);
            leftHorn.rotation.z = -0.4;
            crownGroup.add(leftHorn);

            const rightHorn = new THREE.Mesh(new THREE.ConeGeometry(0.012, 0.05, 8), hornMat);
            rightHorn.position.set(0.03, 0.025, 0);
            rightHorn.rotation.z = 0.4;
            crownGroup.add(rightHorn);
        }

        crownGroup.position.set(0, 0.185, 0); // Positioned at head level above flipPivot
        crownGroup.visible = false;
        return crownGroup;
    }

    function createFlyingNimbusCloud(team) {
        const cloudGroup = new THREE.Group();
        const isLegion = team === TEAM_LEGION;

        // Celestial Nimbus material:
        // Legion: Radiant golden-ivory divine nimbus with amber energy
        // Hellbourne: Smoldering storm ash cloud with fiery crimson embers
        const cloudMat = new THREE.MeshStandardMaterial({
            color: isLegion ? 0xfffbeb : 0x1f2937,
            roughness: 0.35,
            metalness: 0.1,
            emissive: isLegion ? 0xf59e0b : 0xdc2626,
            emissiveIntensity: isLegion ? 0.45 : 0.6,
            transparent: true,
            opacity: 0.92
        });

        const auraMat = new THREE.MeshBasicMaterial({
            color: isLegion ? 0xfbbf24 : 0xef4444,
            transparent: true,
            opacity: 0.35,
            side: THREE.DoubleSide
        });

        // Billowing cluster of stylized puffy cumulus lobes
        const puffs = [
            { x: 0, y: 0, z: 0, r: 0.046, sx: 1.35, sy: 0.48, sz: 1.2 },
            { x: 0.036, y: 0.002, z: 0.02, r: 0.034, sx: 1.1, sy: 0.48, sz: 1.05 },
            { x: -0.036, y: 0.002, z: 0.02, r: 0.034, sx: 1.1, sy: 0.48, sz: 1.05 },
            { x: 0.032, y: -0.002, z: -0.028, r: 0.032, sx: 1.0, sy: 0.48, sz: 1.1 },
            { x: -0.032, y: -0.002, z: -0.028, r: 0.032, sx: 1.0, sy: 0.48, sz: 1.1 },
            { x: 0, y: 0.004, z: 0.042, r: 0.030, sx: 1.1, sy: 0.44, sz: 1.0 },
            { x: 0, y: -0.004, z: -0.046, r: 0.026, sx: 0.9, sy: 0.4, sz: 1.25 },
        ];

        const clusterGroup = new THREE.Group();
        for (const p of puffs) {
            const sphereGeo = new THREE.SphereGeometry(p.r, 14, 14);
            const sphereMesh = new THREE.Mesh(sphereGeo, cloudMat);
            sphereMesh.position.set(p.x, p.y, p.z);
            sphereMesh.scale.set(p.sx, p.sy, p.sz);
            sphereMesh.castShadow = true;
            clusterGroup.add(sphereMesh);
        }
        cloudGroup.add(clusterGroup);

        // Swirling vapor ring around the cloud perimeter
        const ringGeo = new THREE.TorusGeometry(0.075, 0.005, 8, 28);
        const ringMesh = new THREE.Mesh(ringGeo, auraMat);
        ringMesh.rotation.x = Math.PI / 2;
        ringMesh.position.y = -0.004;
        cloudGroup.add(ringMesh);

        // Radiant ground glow projection
        const glowGeo = new THREE.CircleGeometry(0.082, 24);
        const glowMat = new THREE.MeshBasicMaterial({
            color: isLegion ? 0xfde047 : 0xf87171,
            transparent: true,
            opacity: 0.22,
            side: THREE.DoubleSide
        });
        const glowDisk = new THREE.Mesh(glowGeo, glowMat);
        glowDisk.rotation.x = -Math.PI / 2;
        glowDisk.position.y = -0.015;
        cloudGroup.add(glowDisk);

        cloudGroup.position.set(0, 0.022, 0); // Positioned directly beneath Monkey King's feet
        cloudGroup.visible = false;
        cloudGroup.userData = { clusterGroup: clusterGroup, ringMesh: ringMesh };
        return cloudGroup;
    }

    function instantiatePiece(id, team, row, col) {
        const pieceGroup = new THREE.Group();
        const isLegion = team === TEAM_LEGION;

        // 1. Heading Group: Y-axis rotation handles the piece's facing direction
        const headingGroup = new THREE.Group();
        headingGroup.rotation.y = isLegion ? 0 : Math.PI;
        pieceGroup.add(headingGroup);

        // 2. Flip Pivot Group: Located at center of mass (waist/chest ~0.095m)
        // X-axis rotation produces a pure, athletic forward somersault along heading
        const flipPivot = new THREE.Group();
        flipPivot.position.set(0, 0.095, 0);
        headingGroup.add(flipPivot);

        // 3. 3D Figurine Mesh (Standing directly on the tile, zero pedestal platform)
        let figurineModel = null;
        let mixer = null;
        let actions = {};

        if (baseHeroGltf && window.THREE.SkeletonUtils) {
            figurineModel = window.THREE.SkeletonUtils.clone(baseHeroGltf.scene);
            const scale = 0.0016; // Tabletop figurine scale ~20cm
            figurineModel.scale.set(scale, scale, scale);
            // Offset downward so feet rest cleanly directly on the tile surface (y = 0)
            figurineModel.position.set(0, -0.095, 0);

            // Apply Faction Tints while preserving crisp Monkey King textures (never apply flat red emissive without an emissiveMap)
            figurineModel.traverse((node) => {
                if (node.isMesh) {
                    node.castShadow = true;
                    node.receiveShadow = true;
                    if (node.material) {
                        const applyMat = (m) => {
                            const cloned = m.clone();
                            cloned.roughness = 0.52;
                            cloned.metalness = 0.12;
                            if (cloned.map) {
                                if (window.KongorViewerCore) {
                                    window.KongorViewerCore.configureTextureColorSpace(cloned.map);
                                } else if ('colorSpace' in cloned.map && THREE.SRGBColorSpace) {
                                    cloned.map.colorSpace = THREE.SRGBColorSpace;
                                }
                            }
                            if (isLegion) {
                                cloned.color.setHex(0xffffff);
                                if (!cloned.emissiveMap) {
                                    cloned.emissive.setHex(0x000000);
                                    cloned.emissiveIntensity = 0.0;
                                }
                            } else {
                                cloned.color.setHex(0xf8fafc);
                                if (cloned.emissiveMap) {
                                    cloned.emissive.setHex(0xef4444);
                                    cloned.emissiveIntensity = 0.5;
                                } else {
                                    cloned.emissive.setHex(0x000000);
                                    cloned.emissiveIntensity = 0.0;
                                }
                            }
                            cloned.needsUpdate = true;
                            return cloned;
                        };
                        node.material = Array.isArray(node.material)
                            ? node.material.map(applyMat)
                            : applyMat(node.material);
                    }
                }
            });

            flipPivot.add(figurineModel);

            // Mixer & Actions
            mixer = new THREE.AnimationMixer(figurineModel);
            if (baseHeroGltf.animations) {
                baseHeroGltf.animations.forEach((clip) => {
                    actions[clip.name] = mixer.clipAction(clip);
                });
            }

            // Start Idle animation
            const idleAction = actions['idle'] || actions['dancing_idle'] || Object.values(actions)[0];
            if (idleAction) {
                idleAction.play();
                idleAction.time = Math.random() * 2.0;
            }
        } else {
            figurineModel = createProceduralFigurine(team);
            figurineModel.position.set(0, -0.095, 0);
            flipPivot.add(figurineModel);
        }

        // 4. 3D Crown for King Promotion (Mounted in flipPivot so it flips with the King)
        const crown = create3DCrown(team);
        flipPivot.add(crown);

        // 5. Flying Nimbus Cloud (Ult Somersault Cloud mounted directly under hero's feet)
        const cloud = createFlyingNimbusCloud(team);
        headingGroup.add(cloud);

        // Initial World Placement
        const pos = boardToWorld(row, col);
        pieceGroup.position.set(pos.x, BOARD_HEIGHT, pos.z);
        scene.add(pieceGroup);

        const pieceObj = {
            id: id,
            team: team,
            row: row,
            col: col,
            isKing: false,
            isAlive: true,
            group: pieceGroup,
            headingGroup: headingGroup,
            flipPivot: flipPivot,
            model: figurineModel,
            mixer: mixer,
            actions: actions,
            crown: crown,
            cloud: cloud,
            pedestal: null
        };

        // Attach reference for raycasting
        pieceGroup.userData = { piece: pieceObj };
        headingGroup.userData = { piece: pieceObj };
        flipPivot.userData = { piece: pieceObj };
        return pieceObj;
    }

    async function summonAllPieces() {
        // Clear existing pieces
        for (const p of pieces) {
            if (p.group && scene) scene.remove(p.group);
        }
        pieces = [];
        piecesGrid = Array(BOARD_SIZE).fill(null).map(() => Array(BOARD_SIZE).fill(null));
        graveyardLegion = [];
        graveyardHellbourne = [];
        hasFirstBloodOccurred = false;

        await loadMonkeyKingAsset();

        const loadingOverlay = document.getElementById('checkersLoading');
        if (loadingOverlay) loadingOverlay.classList.add('hidden');

        // Place Legion (Rows 0, 1, 2) on dark playable tiles
        let idCounter = 1;
        for (let r = 0; r < 3; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                if ((r + c) % 2 === 1) {
                    const p = instantiatePiece(`legion_${idCounter++}`, TEAM_LEGION, r, c);
                    pieces.push(p);
                    piecesGrid[r][c] = p;
                }
            }
        }

        // Place Hellbourne (Rows 5, 6, 7) on dark playable tiles
        for (let r = 5; r < BOARD_SIZE; r++) {
            for (let c = 0; c < BOARD_SIZE; c++) {
                if ((r + c) % 2 === 1) {
                    const p = instantiatePiece(`hellbourne_${idCounter++}`, TEAM_HELLBOURNE, r, c);
                    pieces.push(p);
                    piecesGrid[r][c] = p;
                }
            }
        }

        updateHUD();
        updateBoardTransparency();
        playAudioFx('announcer', 'prepare_for_battle');
    }

    // =========================================================================
    // 5. Acrobatic Movement & Heavenly Vault Locomotion (W Ability Flip)
    // =========================================================================

    function executeMove(piece, move, onComplete) {
        if (!piece || !move) return;
        isAnimationBusy = true;

        const fromPos = boardToWorld(move.from.row, move.from.col);
        const toPos = boardToWorld(move.to.row, move.to.col);

        // Turn piece towards destination
        const dx = toPos.x - fromPos.x;
        const dz = toPos.z - fromPos.z;
        const targetHeading = Math.atan2(dx, dz);
        if (piece.headingGroup) {
            piece.headingGroup.rotation.y = targetHeading;
        } else if (piece.model) {
            piece.model.rotation.y = targetHeading;
        }

        // Update Grid Reference immediately
        piecesGrid[move.from.row][move.from.col] = null;
        piecesGrid[move.to.row][move.to.col] = piece;
        piece.row = move.to.row;
        piece.col = move.to.col;

        // Record history for Undo
        moveHistory.push({
            piece: piece,
            from: move.from,
            to: move.to,
            isJump: move.isJump,
            capturedPiece: move.capturedPiece,
            wasKingBefore: piece.isKing,
            wasFirstBloodBefore: hasFirstBloodOccurred
        });
        moveCount++;

        if (move.isJump) {
            // --- HEAVENLY VAULT CAPTURE MOVE (W Ability) ---
            // Use in-game animation on W: ability_2_roll for acrobatic forward roll flip!
            const vaultClip = piece.actions['ability_2_roll'] ? 'ability_2_roll' :
                              (piece.actions['ability_2'] ? 'ability_2' : 'ability_1');
            playPieceAction(piece, vaultClip, 0.1, false);
            playHeroVoice('vault');

            const vaultHeight = 0.42; // Acrobatic leap height
            const duration = 700; // ms
            const startTime = performance.now();

            function animateVault(currentTime) {
                const elapsed = currentTime - startTime;
                const t = Math.min(elapsed / duration, 1.0);

                // Parabolic Arc: Y(t) = 4 * H * t * (1 - t)
                const arcY = 4 * vaultHeight * t * (1 - t);
                const currentX = THREE.MathUtils.lerp(fromPos.x, toPos.x, t);
                const currentZ = THREE.MathUtils.lerp(fromPos.z, toPos.z, t);

                piece.group.position.set(currentX, BOARD_HEIGHT + arcY, currentZ);

                // Natural acrobatic martial arts front somersault along local heading:
                // - t in [0, 0.12]: Leaping spring & takeoff gathering
                // - t in [0.12, 0.88]: Full 360-degree airborne tuck somersault with smooth sinusoidal easing
                // - t in [0.88, 1.0]: Unrolling legs and sticking the landing on both feet
                let flipAngle = 0;
                if (t <= 0.12) {
                    flipAngle = (t / 0.12) * (0.10 * Math.PI);
                } else if (t <= 0.88) {
                    const tau = (t - 0.12) / 0.76;
                    const eased = 0.5 - 0.5 * Math.cos(Math.PI * tau);
                    flipAngle = (0.10 * Math.PI) + (1.90 * Math.PI) * eased;
                } else {
                    flipAngle = Math.PI * 2;
                }

                if (piece.flipPivot) {
                    piece.flipPivot.rotation.x = flipAngle;
                } else if (piece.model) {
                    piece.model.rotation.x = flipAngle;
                }

                if (t < 1.0) {
                    requestAnimationFrame(animateVault);
                } else {
                    // Landing Impact!
                    if (piece.flipPivot) piece.flipPivot.rotation.x = 0;
                    if (piece.model) piece.model.rotation.x = 0; // Restore upright pitch
                    piece.group.position.set(toPos.x, BOARD_HEIGHT, toPos.z);
                    createSlamShockwave(toPos.x, toPos.z);
                    playPieceAction(piece, 'attack_1', 0.1, false);
                    playAudioFx('hero', 'ability_3'); // Slam impact sound

                    // Knockout Captured Enemy Piece
                    if (move.capturedPiece) {
                        eliminatePiece(move.capturedPiece);
                    }

                    // Announce Combos & First Blood
                    comboStreak++;
                    triggerComboAnnouncer(comboStreak);

                    // Check for King Promotion
                    checkKingAscension(piece);
                    updateBoardTransparency();

                    // Check Multi-Jump Sequence
                    const multiJumps = getLegalMovesForPiece(piece, piecesGrid, true);
                    if (multiJumps.length > 0) {
                        // Multi-jump available!
                        isMultiJumping = true;
                        multiJumpingPiece = piece;
                        selectedPiece = piece;
                        legalMovesForSelected = multiJumps;
                        highlightMoveTargets(multiJumps);
                        isAnimationBusy = false;
                        updateHUD();

                        // If Bot is multi-jumping, execute next jump after slight pause
                        const isBotTurn = (currentTurn === TEAM_HELLBOURNE && gameMode.startsWith('vs_bot')) || gameMode === 'bot_vs_bot';
                        if (isBotTurn) {
                            setTimeout(() => {
                                const nextJump = multiJumps[0];
                                executeMove(piece, nextJump, onComplete);
                            }, 550);
                        }
                        return;
                    }

                    // Turn complete
                    isMultiJumping = false;
                    multiJumpingPiece = null;
                    comboStreak = 0;
                    playPieceAction(piece, 'idle', 0.25, true);
                    isAnimationBusy = false;
                    if (onComplete) onComplete();
                }
            }
            requestAnimationFrame(animateVault);

        } else {
            // --- ILLUSIVE DASH SLIDE MOVE ---
            playPieceAction(piece, 'ability_1', 0.15, false); // Dash animation
            playHeroVoice('move'); // Random move voiceline

            const duration = 380; // ms
            const startTime = performance.now();

            function animateDash(currentTime) {
                const elapsed = currentTime - startTime;
                const t = Math.min(elapsed / duration, 1.0);

                const currentX = THREE.MathUtils.lerp(fromPos.x, toPos.x, t);
                const currentZ = THREE.MathUtils.lerp(fromPos.z, toPos.z, t);
                piece.group.position.set(currentX, BOARD_HEIGHT, currentZ);

                if (t < 1.0) {
                    requestAnimationFrame(animateDash);
                } else {
                    piece.group.position.set(toPos.x, BOARD_HEIGHT, toPos.z);
                    checkKingAscension(piece);
                    updateBoardTransparency();
                    playPieceAction(piece, 'idle', 0.2, true);
                    isAnimationBusy = false;
                    if (onComplete) onComplete();
                }
            }
            requestAnimationFrame(animateDash);
        }
    }

    function eliminatePiece(captured) {
        if (!captured || !captured.isAlive) return;

        captured.isAlive = false;
        piecesGrid[captured.row][captured.col] = null;

        // Knockout & death animation + varied death grunt
        playPieceAction(captured, 'death_1', 0.2, false);
        playHeroVoice('death');

        // Determine Graveyard Position on Sidelines
        const isLegion = captured.team === TEAM_LEGION;
        const tray = isLegion ? graveyardHellbourne : graveyardLegion;
        tray.push(captured);

        const trayX = isLegion ? 1.05 : -1.05; // East or West sideline
        const trayZ = -0.75 + (tray.length - 1) * 0.13;

        // Smoothly glide to sideline
        const startPos = captured.group.position.clone();
        const endPos = new THREE.Vector3(trayX, BOARD_HEIGHT, trayZ);
        const startTime = performance.now();
        const duration = 600;

        function animateGlide(now) {
            const elapsed = now - startTime;
            const t = Math.min(elapsed / duration, 1.0);
            captured.group.position.lerpVectors(startPos, endPos, t);
            if (t < 1.0) {
                requestAnimationFrame(animateGlide);
            } else {
                captured.group.position.copy(endPos);
            }
        }
        requestAnimationFrame(animateGlide);
    }

    function checkKingAscension(piece) {
        if (piece.isKing) return;

        const ascended = (piece.team === TEAM_LEGION && piece.row === 7) ||
                         (piece.team === TEAM_HELLBOURNE && piece.row === 0);

        if (ascended) {
            piece.isKing = true;
            if (piece.crown) piece.crown.visible = true;
            if (piece.cloud) piece.cloud.visible = true;
            if (piece.flipPivot) piece.flipPivot.position.y = 0.115; // Elevate hero to ride atop Flying Nimbus cloud
            playPieceAction(piece, 'taunt_1', 0.2, false);
            playHeroVoice('king');
            showBannerNotification(`👑 ${piece.team.toUpperCase()} MONKEY KING ASCENDED!`);
        }
    }

    function playPieceAction(piece, clipName, fade = 0.2, loop = true) {
        if (!piece || !piece.mixer || !piece.actions) return;

        const action = piece.actions[clipName] || piece.actions['idle'];
        if (!action) return;

        action.reset();
        action.setLoop(loop ? THREE.LoopRepeat : THREE.LoopOnce, loop ? Infinity : 1);
        action.clampWhenFinished = !loop;
        action.play();
    }

    // =========================================================================
    // 6. Turn Management & Bot Scheduling
    // =========================================================================

    function endTurn() {
        clearMoveTargets();
        selectedPiece = null;
        legalMovesForSelected = [];
        isMultiJumping = false;
        multiJumpingPiece = null;
        updateBoardTransparency();

        // Check Victory Conditions
        const legionAlive = pieces.filter(p => p.team === TEAM_LEGION && p.isAlive).length;
        const hellbourneAlive = pieces.filter(p => p.team === TEAM_HELLBOURNE && p.isAlive).length;

        if (hellbourneAlive === 0) {
            triggerVictory(TEAM_LEGION);
            return;
        }
        if (legionAlive === 0) {
            triggerVictory(TEAM_HELLBOURNE);
            return;
        }

        // Swap Turn
        currentTurn = (currentTurn === TEAM_LEGION) ? TEAM_HELLBOURNE : TEAM_LEGION;

        // Check if new player has legal moves (Stalemate check)
        const nextLegal = getAllLegalMoves(currentTurn, piecesGrid);
        if (nextLegal.length === 0) {
            const winner = currentTurn === TEAM_LEGION ? TEAM_HELLBOURNE : TEAM_LEGION;
            triggerVictory(winner);
            return;
        }

        updateHUD();

        // If Bot's Turn, Schedule AI Decision
        const isBotTurn = (currentTurn === TEAM_HELLBOURNE && gameMode.startsWith('vs_bot')) || gameMode === 'bot_vs_bot';
        if (isBotTurn && !isGameOver) {
            const botIndicator = document.getElementById('botThinkingIndicator');
            if (botIndicator) botIndicator.classList.remove('hidden');

            const thinkTime = 550 + Math.random() * 250;
            setTimeout(() => {
                if (isGameOver) return;
                const botDecision = chooseBotMove(currentTurn);
                if (botIndicator) botIndicator.classList.add('hidden');

                if (botDecision) {
                    executeMove(botDecision.piece, botDecision.move, () => {
                        endTurn();
                    });
                }
            }, thinkTime);
        }
    }

    function triggerVictory(winnerTeam) {
        isGameOver = true;
        const modal = document.getElementById('checkersVictoryModal');
        const title = document.getElementById('victoryTitle');
        const sub = document.getElementById('victorySubtitle');
        const movesEl = document.getElementById('victoryMovesCount');
        const capsEl = document.getElementById('victoryCapturesCount');
        const kingsEl = document.getElementById('victoryKingsCount');

        const isLegion = winnerTeam === TEAM_LEGION;
        if (title) {
            if (gameMode === 'pass_and_play') {
                title.innerText = isLegion ? "PLAYER 1 (LEGION) WINS!" : "PLAYER 2 (HELLBOURNE) WINS!";
            } else {
                title.innerText = isLegion ? "LEGION VICTORY!" : "HELLBOURNE CONQUEST!";
            }
            title.className = `text-2xl font-black uppercase tracking-wider font-mono ${isLegion ? 'text-emerald-400' : 'text-rose-500'}`;
        }
        if (sub) {
            if (gameMode === 'pass_and_play') {
                sub.innerText = isLegion
                    ? "Player 1 has conquered the board! All Hellbourne Monkey Kings have fallen."
                    : "Player 2 has conquered the board! All Legion Monkey Kings have fallen.";
            } else {
                sub.innerText = isLegion
                    ? "All Hellbourne forces were vaulted into the abyss of Caldavar."
                    : "The Legion ranks have fallen to the infernal Monkey Kings.";
            }
        }
        if (movesEl) movesEl.innerText = moveCount;
        if (capsEl) capsEl.innerText = 12 - (isLegion ? pieces.filter(p => p.team === TEAM_HELLBOURNE && p.isAlive).length : pieces.filter(p => p.team === TEAM_LEGION && p.isAlive).length);
        if (kingsEl) kingsEl.innerText = pieces.filter(p => p.isKing).length;

        if (modal) modal.classList.remove('hidden');

        playAudioFx('announcer', isLegion ? 'legion_wins' : 'annihilation');
    }

    function undoMove() {
        if (gameMode === 'online_1v1') {
            showToast("Undo is disabled during 1v1 online matches.", "fa-ban");
            return;
        }
        if (isAnimationBusy || moveHistory.length === 0 || isGameOver) return;

        // In vs Bot mode, undo twice to revert bot and player moves
        const undosToPerform = gameMode.startsWith('vs_bot') && moveHistory.length >= 2 ? 2 : 1;

        for (let i = 0; i < undosToPerform; i++) {
            if (moveHistory.length === 0) break;
            const last = moveHistory.pop();

            // Revert piece position
            piecesGrid[last.to.row][last.to.col] = null;
            piecesGrid[last.from.row][last.from.col] = last.piece;
            last.piece.row = last.from.row;
            last.piece.col = last.from.col;
            last.piece.isKing = last.wasKingBefore;
            if (last.piece.crown) last.piece.crown.visible = last.wasKingBefore;
            if (last.piece.cloud) last.piece.cloud.visible = last.wasKingBefore;
            if (last.piece.flipPivot) last.piece.flipPivot.position.y = last.wasKingBefore ? 0.115 : 0.095;

            const pos = boardToWorld(last.from.row, last.from.col);
            last.piece.group.position.set(pos.x, BOARD_HEIGHT, pos.z);
            if (last.piece.flipPivot) last.piece.flipPivot.rotation.x = 0;
            if (last.piece.model) last.piece.model.rotation.x = 0;

            // Revert first blood state if relevant
            hasFirstBloodOccurred = last.wasFirstBloodBefore;

            // Revert captured piece
            if (last.capturedPiece) {
                last.capturedPiece.isAlive = true;
                piecesGrid[last.capturedPiece.row][last.capturedPiece.col] = last.capturedPiece;

                // Remove from graveyard tray
                const tray = last.capturedPiece.team === TEAM_LEGION ? graveyardHellbourne : graveyardLegion;
                const idx = tray.indexOf(last.capturedPiece);
                if (idx !== -1) tray.splice(idx, 1);

                const capPos = boardToWorld(last.capturedPiece.row, last.capturedPiece.col);
                last.capturedPiece.group.position.set(capPos.x, BOARD_HEIGHT, capPos.z);
                playPieceAction(last.capturedPiece, 'idle', 0.2, true);
            }

            currentTurn = last.piece.team;
        }

        clearMoveTargets();
        selectedPiece = null;
        updateHUD();
        updateBoardTransparency();
    }

    // =========================================================================
    // 7. Raycasting & Interaction Handlers
    // =========================================================================

    function bindInteractions() {
        const container = document.getElementById('checkersCanvasContainer');
        if (!container) return;

        container.onpointerdown = (e) => {
            if (isAnimationBusy || isGameOver) return;

            // Only allow Left Click (button 0) for selection and moves
            if (e.button !== 0) return;

            const isBotTurn = (currentTurn === TEAM_HELLBOURNE && gameMode.startsWith('vs_bot')) || gameMode === 'bot_vs_bot';
            if (isBotTurn) return;

            // In 1v1 online multiplayer, only allow input during player's assigned team turn
            if (gameMode === 'online_1v1') {
                const myTeam = (playerSlot === 1) ? TEAM_LEGION : TEAM_HELLBOURNE;
                if (currentTurn !== myTeam) return;
            }

            const rect = renderer.domElement.getBoundingClientRect();
            pointer.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
            pointer.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

            raycaster.setFromCamera(pointer, camera);

            // 1. Check if clicking an active target destination marker
            const markerHits = raycaster.intersectObjects(targetMarkerGroup.children, true);
            if (markerHits.length > 0 && selectedPiece) {
                const targetMove = markerHits[0].object.userData.targetMove;
                if (targetMove) {
                    clearMoveTargets();

                    // Send move over SignalR in 1v1 online mode
                    if (gameMode === 'online_1v1' && signalRConnection && currentRoomCode) {
                        signalRConnection.invoke("SendMove", currentRoomCode, selectedPiece.row, selectedPiece.col, targetMove.to.row, targetMove.to.col, targetMove.isJump)
                            .catch(err => console.error("[CheckersHub] SendMove error:", err));
                    }

                    executeMove(selectedPiece, targetMove, () => {
                        endTurn();
                    });
                    return;
                }
            }

            // 2. If currently in a multi-jump sequence, cannot select other pieces
            if (isMultiJumping) return;

            // 3. Check if clicking a friendly Monkey King
            const pieceHits = raycaster.intersectObjects(pieces.map(p => p.group), true);
            if (pieceHits.length > 0) {
                // Traverse up to find root group with userData
                let obj = pieceHits[0].object;
                while (obj && (!obj.userData || !obj.userData.piece)) {
                    obj = obj.parent;
                }

                if (obj && obj.userData && obj.userData.piece) {
                    const clickedPiece = obj.userData.piece;
                    const myTeam = (gameMode === 'online_1v1') ? (playerSlot === 1 ? TEAM_LEGION : TEAM_HELLBOURNE) : currentTurn;
                    if (clickedPiece.team === currentTurn && clickedPiece.team === myTeam && clickedPiece.isAlive) {
                        selectPiece(clickedPiece);
                    }
                }
            }
        };

        // Camera Presets
        document.getElementById('btnViewTabletop')?.addEventListener('click', () => setCameraPreset('tabletop'));
        document.getElementById('btnViewTopDown')?.addEventListener('click', () => setCameraPreset('topdown'));
        document.getElementById('btnViewLegion')?.addEventListener('click', () => setCameraPreset('legion'));
        document.getElementById('btnViewHellbourne')?.addEventListener('click', () => setCameraPreset('hellbourne'));

        // Mode Selector
        const modeSelect = document.getElementById('selectGameMode');
        if (modeSelect) {
            modeSelect.onchange = (e) => {
                if (e.target.value === 'online_1v1') {
                    openOnlineLobby();
                    return;
                }
                if (gameMode === 'online_1v1') {
                    leaveOnlineRoom();
                }
                gameMode = e.target.value;
                if (gameMode === 'pass_and_play') {
                    showToast("Pass & Play: 2 players share this screen!", "fa-users");
                }
                restartMatch();
            };
        }

        // Camera Toggle
        document.getElementById('btnCheckersToggleCamera')?.addEventListener('click', toggleCameraAR);

        // Audio Toggle
        document.getElementById('btnCheckersToggleAudio')?.addEventListener('click', toggleAudio);

        // Undo Button
        document.getElementById('btnCheckersUndo')?.addEventListener('click', undoMove);

        // Hint Button (AI recommends move)
        document.getElementById('btnCheckersHint')?.addEventListener('click', giveTacticalHint);

        // Reset Match
        document.getElementById('btnCheckersReset')?.addEventListener('click', () => restartMatch(false));
        document.getElementById('btnVictoryRematch')?.addEventListener('click', () => restartMatch(false));
        document.getElementById('btnVictoryClose')?.addEventListener('click', () => {
            document.getElementById('checkersVictoryModal')?.classList.add('hidden');
        });

        // Online Multiplayer Lobby & Room Buttons
        document.getElementById('btnOpenOnlineLobby')?.addEventListener('click', openOnlineLobby);
        document.getElementById('btnCheckersRandomRoom')?.addEventListener('click', () => {
            const input = document.getElementById('inputCheckersRoomCode');
            if (input) input.value = generateRandomRoomCode();
        });
        document.getElementById('btnCheckersEnterRoom')?.addEventListener('click', () => {
            const nameInput = document.getElementById('inputCheckersPlayerName');
            const codeInput = document.getElementById('inputCheckersRoomCode');
            const name = nameInput ? nameInput.value : 'MonkeyKing';
            const code = codeInput ? codeInput.value : 'MK42';
            connectAndJoinRoom(code, name);
        });
        document.getElementById('btnCheckersPlaySolo')?.addEventListener('click', () => {
            closeOnlineLobby();
            const select = document.getElementById('selectGameMode');
            if (select) select.value = 'vs_bot_novice';
            gameMode = 'vs_bot_novice';
            restartMatch();
        });
        document.getElementById('btnCheckersCancelWaiting')?.addEventListener('click', () => {
            leaveOnlineRoom();
            showToast("Cancelled waiting for opponent.", "fa-arrow-left");
        });
        document.getElementById('btnCheckersShareCopy')?.addEventListener('click', () => {
            const textEl = document.getElementById('checkersShareUrlText');
            if (textEl && navigator.clipboard) {
                navigator.clipboard.writeText(textEl.innerText).then(() => {
                    showToast("Room invite link copied to clipboard!", "fa-copy");
                }).catch(() => {
                    showToast("Failed to copy link", "fa-triangle-exclamation");
                });
            }
        });

        // Pre-Match AR Ready Choice Buttons
        document.getElementById('btnCheckersArYes')?.addEventListener('click', async () => {
            await startCameraAR();
            isLocalReady = true;
            const btnYes = document.getElementById('btnCheckersArYes');
            const btnNo = document.getElementById('btnCheckersArNo');
            if (btnYes) {
                btnYes.disabled = true;
                btnYes.classList.add('opacity-50', 'pointer-events-none');
            }
            if (btnNo) {
                btnNo.disabled = true;
                btnNo.classList.add('opacity-50', 'pointer-events-none');
            }
            document.getElementById('checkersWaitingForOpponentReadyText')?.classList.remove('hidden');

            if (signalRConnection && currentRoomCode) {
                signalRConnection.invoke("SetReady", currentRoomCode, true)
                    .catch(e => console.error("SetReady error:", e));
            }
            updateReadyBadge(playerSlot, true, true);
        });

        document.getElementById('btnCheckersArNo')?.addEventListener('click', () => {
            stopCameraAR();
            isLocalReady = true;
            const btnYes = document.getElementById('btnCheckersArYes');
            const btnNo = document.getElementById('btnCheckersArNo');
            if (btnYes) {
                btnYes.disabled = true;
                btnYes.classList.add('opacity-50', 'pointer-events-none');
            }
            if (btnNo) {
                btnNo.disabled = true;
                btnNo.classList.add('opacity-50', 'pointer-events-none');
            }
            document.getElementById('checkersWaitingForOpponentReadyText')?.classList.remove('hidden');

            if (signalRConnection && currentRoomCode) {
                signalRConnection.invoke("SetReady", currentRoomCode, false)
                    .catch(e => console.error("SetReady error:", e));
            }
            updateReadyBadge(playerSlot, true, false);
        });

        // Room Full Modal Buttons
        document.getElementById('btnCheckersCreateNewFromFull')?.addEventListener('click', () => {
            const suggested = document.getElementById('checkersSuggestedNewCode')?.innerText || generateRandomRoomCode();
            document.getElementById('checkersRoomFullModal')?.classList.add('hidden');
            const nameInput = document.getElementById('inputCheckersPlayerName');
            const name = nameInput ? nameInput.value : 'MonkeyKing';
            connectAndJoinRoom(suggested, name);
        });
        document.getElementById('btnCheckersCloseRoomFull')?.addEventListener('click', () => {
            document.getElementById('checkersRoomFullModal')?.classList.add('hidden');
            openOnlineLobby();
        });
    }

    function selectPiece(piece) {
        selectedPiece = piece;
        legalMovesForSelected = getLegalMovesForPiece(piece, piecesGrid, false);

        // Position selection ring beneath piece
        if (selectionRing) {
            selectionRing.position.set(piece.group.position.x, BOARD_HEIGHT + 0.012, piece.group.position.z);
            selectionRing.visible = true;
        }

        // Highlight destination tiles
        highlightMoveTargets(legalMovesForSelected);

        // Play random unit selection voice line from pool!
        playHeroVoice('select');
    }

    function giveTacticalHint() {
        if (isAnimationBusy || isGameOver) return;
        const hintMove = chooseBotMove(currentTurn);
        if (hintMove) {
            selectPiece(hintMove.piece);
            showBannerNotification(`💡 TACTICAL HINT: Move to (${hintMove.move.to.row}, ${hintMove.move.to.col})`);
        }
    }

    function setCameraPreset(preset) {
        if (!camera || !controls) return;

        if (preset === 'tabletop') {
            camera.position.set(0, 1.6, 2.2);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'topdown') {
            camera.position.set(0, 2.4, 0.01);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'legion') {
            camera.position.set(0, 0.85, 1.7);
            controls.target.set(0, BOARD_HEIGHT, 0);
        } else if (preset === 'hellbourne') {
            camera.position.set(0, 0.85, -1.7);
            controls.target.set(0, BOARD_HEIGHT, 0);
        }
        controls.update();
    }

    // =========================================================================
    // 8. Audio System & Announcements
    // =========================================================================

    function playAudioFx(category, actionType) {
        if (isAudioMuted) return;

        let soundUrl = '';
        if (category === 'hero') {
            soundUrl = `/api/v1/assets/sound?hero=monkey_king&action=${encodeURIComponent(actionType)}&_t=${Date.now()}`;
        } else {
            soundUrl = `/api/v1/assets/sound?path=shared/sounds/announcer/${encodeURIComponent(actionType)}.ogg`;
        }

        const audio = new Audio(soundUrl);

        // Polyphonic mixing balance:
        // Announcer lines & slam impacts punch through clearly, voices layer concurrently
        if (category === 'announcer') {
            audio.volume = 0.95;
        } else if (actionType === 'ability_3') {
            audio.volume = 0.85; // Heavy slam impact SFX
        } else {
            audio.volume = 0.75; // Hero voicelines
        }

        activeAudio = audio;
        activeAudioSet.add(audio);

        const cleanup = () => {
            activeAudioSet.delete(audio);
            if (activeAudio === audio) {
                activeAudio = activeAudioSet.size > 0 ? Array.from(activeAudioSet).pop() : null;
            }
        };

        audio.addEventListener('ended', cleanup);
        audio.addEventListener('error', cleanup);

        audio.play().catch(() => {
            cleanup();
        });
    }

    function toggleAudio() {
        isAudioMuted = !isAudioMuted;
        if (isAudioMuted) {
            stopAllAudio();
        }
        const icon = document.getElementById('checkersAudioIcon');
        if (icon) {
            icon.className = isAudioMuted ? "fas fa-volume-mute text-rose-400" : "fas fa-volume-up text-emerald-400";
        }
    }

    function stopAllAudio() {
        activeAudioSet.forEach((a) => {
            try {
                a.pause();
                a.currentTime = 0;
            } catch (e) {}
        });
        activeAudioSet.clear();
        activeAudio = null;
    }

    function triggerComboAnnouncer(streak) {
        // First Blood occurs strictly on the FIRST kill of the entire game!
        if (!hasFirstBloodOccurred) {
            hasFirstBloodOccurred = true;
            showBannerNotification("🩸 FIRST BLOOD!");
            playAudioFx('announcer', 'first_blood');
        } else if (streak === 2) {
            showBannerNotification("⚡ DOUBLE VAULT! (DOUBLE KILL)");
            playAudioFx('announcer', 'double_kill');
        } else if (streak >= 3) {
            showBannerNotification("🔥 TRIPLE VAULT! (UNSTOPPABLE)");
            playAudioFx('announcer', '3_kills');
        } else {
            // Normal subsequent kill: do not say First Blood, play combat audio
            showBannerNotification("💥 HEAVENLY VAULT!");
            playHeroVoice('vault');
        }
    }

    function showBannerNotification(text) {
        const banner = document.getElementById('checkersAnnouncerBanner');
        const label = document.getElementById('checkersAnnouncerText');
        if (!banner || !label) return;

        label.innerText = text;
        banner.classList.remove('hidden');
        banner.classList.remove('opacity-0', 'scale-90');
        banner.classList.add('opacity-100', 'scale-100');

        setTimeout(() => {
            banner.classList.remove('opacity-100', 'scale-100');
            banner.classList.add('opacity-0', 'scale-90');
            setTimeout(() => banner.classList.add('hidden'), 350);
        }, 1800);
    }

    // =========================================================================
    // 9. WebRTC Camera AR Passthrough
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
        const video = document.getElementById('checkersVideo');
        const btnText = document.getElementById('checkersCameraBtnText');
        const btn = document.getElementById('btnCheckersToggleCamera');

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
            if (btnText) btnText.innerText = "Stop Camera";
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
        const video = document.getElementById('checkersVideo');
        if (video) {
            video.classList.add('hidden');
            video.srcObject = null;
        }

        isCameraActive = false;
        const btnText = document.getElementById('checkersCameraBtnText');
        const btn = document.getElementById('btnCheckersToggleCamera');
        if (btnText) btnText.innerText = "Enable Camera";
        if (btn) btn.classList.remove('border-emerald-500', 'bg-emerald-950/40');
        updateBoardTransparency();
    }

    // =========================================================================
    // 10. UI HUD & Lifecycle
    // =========================================================================

    function updateHUD() {
        const turnDot = document.getElementById('turnStatusDot');
        const turnTitle = document.getElementById('turnTitle');
        const scoreLegion = document.getElementById('boardScoreLegion');
        const scoreHellbourne = document.getElementById('boardScoreHellbourne');

        const isLegion = currentTurn === TEAM_LEGION;
        if (turnDot) {
            turnDot.className = `w-3 h-3 rounded-full animate-pulse ${isLegion ? 'bg-emerald-400' : 'bg-rose-500'}`;
        }
        if (turnTitle) {
            if (gameMode === 'online_1v1') {
                const myTeam = (playerSlot === 1) ? TEAM_LEGION : TEAM_HELLBOURNE;
                const isMyTurn = currentTurn === myTeam;
                turnTitle.innerText = isMyTurn
                    ? `Your Turn (${isLegion ? 'Legion' : 'Hellbourne'})`
                    : `Opponent's Turn (${isLegion ? 'Legion' : 'Hellbourne'})`;
                turnTitle.className = `text-xs font-black tracking-wider uppercase font-mono ${isMyTurn ? 'text-emerald-300' : 'text-slate-400'}`;
            } else if (gameMode === 'pass_and_play') {
                turnTitle.innerText = isLegion ? "Player 1 (Legion)'s Turn" : "Player 2 (Hellbourne)'s Turn";
                turnTitle.className = `text-xs font-black tracking-wider uppercase font-mono ${isLegion ? 'text-emerald-300' : 'text-rose-400'}`;
            } else if (gameMode === 'bot_vs_bot') {
                turnTitle.innerText = isLegion ? "Bot (Legion)'s Turn" : "Bot (Hellbourne)'s Turn";
                turnTitle.className = `text-xs font-black tracking-wider uppercase font-mono ${isLegion ? 'text-emerald-300' : 'text-rose-400'}`;
            } else {
                turnTitle.innerText = isLegion ? "Your Turn (Legion)" : "Bot Thinking (Hellbourne)...";
                turnTitle.className = `text-xs font-black tracking-wider uppercase font-mono ${isLegion ? 'text-emerald-300' : 'text-rose-400'}`;
            }
        }

        const legionCount = pieces.filter(p => p.team === TEAM_LEGION && p.isAlive).length;
        const hellCount = pieces.filter(p => p.team === TEAM_HELLBOURNE && p.isAlive).length;

        if (scoreLegion) scoreLegion.innerHTML = `<i class="fas fa-crown text-[9px] mr-0.5"></i>Legion: ${legionCount}`;
        if (scoreHellbourne) scoreHellbourne.innerHTML = `<i class="fas fa-fire text-[9px] mr-0.5"></i>Hellbourne: ${hellCount}`;
    }

    function restartMatch(isRemote = false) {
        if (gameMode === 'online_1v1' && !isRemote && signalRConnection && currentRoomCode) {
            signalRConnection.invoke("ResetMatch", currentRoomCode).catch(() => {});
        }

        isGameOver = false;
        currentTurn = TEAM_LEGION;
        selectedPiece = null;
        legalMovesForSelected = [];
        isMultiJumping = false;
        multiJumpingPiece = null;
        moveHistory = [];
        moveCount = 0;
        comboStreak = 0;
        isAnimationBusy = false;
        hasFirstBloodOccurred = false;

        stopAllAudio();
        document.getElementById('checkersVictoryModal')?.classList.add('hidden');
        clearMoveTargets();
        summonAllPieces();
    }

    function onWindowResize() {
        const container = document.getElementById('checkersCanvasContainer');
        if (!container || !camera || !renderer) return;

        const w = container.clientWidth || window.innerWidth;
        const h = container.clientHeight || window.innerHeight;
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        renderer.setSize(w, h);
    }

    function renderLoop() {
        const delta = clock.getDelta();
        const elapsed = clock ? clock.getElapsedTime() : 0;

        // Update active animation mixers and King Flying Nimbus clouds
        for (const p of pieces) {
            if (p.isAlive) {
                if (p.mixer) {
                    p.mixer.update(delta);
                }
                if (p.isKing && p.cloud && p.cloud.visible) {
                    // Gentle celestial floating bob under the king
                    p.cloud.position.y = 0.022 + Math.sin(elapsed * 2.8 + p.row * 1.5 + p.col) * 0.004;
                    if (p.cloud.userData && p.cloud.userData.ringMesh) {
                        p.cloud.userData.ringMesh.rotation.z += 0.016;
                    }
                    if (p.cloud.userData && p.cloud.userData.clusterGroup) {
                        p.cloud.userData.clusterGroup.rotation.y = Math.sin(elapsed * 1.2 + p.row) * 0.12;
                    }
                }
            }
        }

        if (controls && controls.enabled) controls.update();
        if (renderer && scene && camera) renderer.render(scene, camera);
    }

    // =========================================================================
    // 11. SignalR Real-Time Online Multiplayer & Ready System
    // =========================================================================

    function showToast(msg, iconClass = 'fa-info-circle') {
        const toastEl = document.getElementById('checkersToastNotification');
        const toastText = document.getElementById('checkersToastText');
        const toastIcon = document.getElementById('checkersToastIcon');
        if (!toastEl || !toastText) return;

        toastText.textContent = msg;
        if (toastIcon) toastIcon.className = `fas ${iconClass} text-amber-400`;
        toastEl.classList.remove('opacity-0');
        clearTimeout(toastEl._timer);
        toastEl._timer = setTimeout(() => toastEl.classList.add('opacity-0'), 2800);
    }

    function generateRandomRoomCode() {
        const prefixes = ['MK', 'HON', 'WAR', 'KING', 'CALD', 'DUEL', 'AR'];
        return prefixes[Math.floor(Math.random() * prefixes.length)] + Math.floor(10 + Math.random() * 89);
    }

    function openOnlineLobby() {
        const modal = document.getElementById('checkersLobbyModal');
        if (modal) modal.classList.remove('hidden');
    }

    function closeOnlineLobby() {
        const modal = document.getElementById('checkersLobbyModal');
        if (modal) modal.classList.add('hidden');
    }

    function leaveOnlineRoom() {
        if (signalRConnection) {
            try {
                signalRConnection.invoke("LeaveRoom").catch(() => {});
                signalRConnection.stop();
            } catch (e) {}
            signalRConnection = null;
        }
        currentRoomCode = null;
        isOnlineGame = false;
        playerSlot = 1;
        isLocalReady = false;
        isRemoteReady = false;

        document.getElementById('checkersOnlineBadge')?.classList.add('hidden');
        document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
        document.getElementById('checkersArReadyModal')?.classList.add('hidden');
        document.getElementById('checkersRoomFullModal')?.classList.add('hidden');

        const modeSelect = document.getElementById('selectGameMode');
        if (modeSelect && modeSelect.value === 'online_1v1') {
            modeSelect.value = 'vs_bot_novice';
            gameMode = 'vs_bot_novice';
        }
    }

    async function connectAndJoinRoom(code, name) {
        currentRoomCode = (code || 'MK42').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8);
        playerName = (name || 'MonkeyKing').trim().slice(0, 16);

        closeOnlineLobby();
        showToast(`Connecting to room ${currentRoomCode}...`, "fa-satellite-dish");

        if (signalRConnection) {
            try { await signalRConnection.stop(); } catch (e) {}
            signalRConnection = null;
        }

        // Build SignalR Connection to /hubs/monkey-checkers
        signalRConnection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/monkey-checkers")
            .withAutomaticReconnect([0, 1000, 2500, 5000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        setupSignalRHandlers();

        try {
            await signalRConnection.start();
            console.log("[MonkeyCheckers] SignalR Connected successfully");
            await signalRConnection.invoke("JoinRoom", currentRoomCode, playerName, null);
        } catch (err) {
            console.error("[MonkeyCheckers] SignalR Connection Failed:", err);
            showToast("Multiplayer connection failed. Please try again.", "fa-triangle-exclamation");
            openOnlineLobby();
        }
    }

    function setupSignalRHandlers() {
        if (!signalRConnection) return;

        // 1. OnRoomJoined
        signalRConnection.on("OnRoomJoined", (roomCode, slot, p1Name, p2Name, status) => {
            console.log(`[MonkeyCheckers] OnRoomJoined: Code=${roomCode}, Slot=${slot}, Status=${status}`);
            playerSlot = slot;
            currentRoomCode = roomCode;
            isOnlineGame = true;
            gameMode = 'online_1v1';

            const modeSelect = document.getElementById('selectGameMode');
            if (modeSelect) modeSelect.value = 'online_1v1';

            // Show active online room badge in HUD
            const onlineBadge = document.getElementById('checkersOnlineBadge');
            const roomLabel = document.getElementById('checkersOnlineRoomLabel');
            if (onlineBadge && roomLabel) {
                onlineBadge.classList.remove('hidden');
                onlineBadge.classList.add('flex');
                roomLabel.innerText = `${roomCode} • ${slot === 1 ? 'Legion' : 'Hellbourne'}`;
            }

            // Update share link in Waiting Overlay
            const shareUrl = `${window.location.origin}/ar-mini-games/monkey-checkers?room=${roomCode}`;
            const shareUrlEl = document.getElementById('checkersShareUrlText');
            if (shareUrlEl) shareUrlEl.innerText = shareUrl;

            const waitingRoomText = document.getElementById('checkersWaitingRoomCodeText');
            if (waitingRoomText) waitingRoomText.innerText = roomCode;

            // Player names in ready modal
            const p1Label = document.getElementById('checkersP1ReadyName');
            const p2Label = document.getElementById('checkersP2ReadyName');
            if (p1Label) p1Label.innerText = p1Name ? `${p1Name} (Legion)` : 'Legion (Host)';
            if (p2Label) p2Label.innerText = p2Name ? `${p2Name} (Hellbourne)` : 'Hellbourne (Challenger)';

            closeOnlineLobby();

            if (status === 'WaitingForOpponent' || (!p1Name || !p2Name)) {
                document.getElementById('checkersWaitingOverlay')?.classList.remove('hidden');
                document.getElementById('checkersArReadyModal')?.classList.add('hidden');
            } else if (status === 'WaitingForReady') {
                document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
                promptMatchReady();
            } else if (status === 'Playing') {
                document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
                document.getElementById('checkersArReadyModal')?.classList.add('hidden');
                restartMatch(true);
            }
        });

        // 2. OnRoomFull
        signalRConnection.on("OnRoomFull", (roomCode, message) => {
            console.warn(`[MonkeyCheckers] Room ${roomCode} is full.`);
            try { signalRConnection.stop(); } catch (e) {}

            const fullCodeEl = document.getElementById('checkersFullRoomCodeText');
            if (fullCodeEl) fullCodeEl.innerText = roomCode;

            const suggestedCode = generateRandomRoomCode();
            const suggestedEl = document.getElementById('checkersSuggestedNewCode');
            if (suggestedEl) suggestedEl.innerText = suggestedCode;

            const descEl = document.getElementById('checkersRoomFullDesc');
            if (descEl) {
                descEl.textContent = 'Room ';
                const codeSpan = document.createElement('span');
                codeSpan.className = 'font-mono font-bold text-rose-400';
                codeSpan.textContent = String(roomCode ?? '');
                descEl.append(codeSpan, ' already has 2 Monkey Kings dueling (2/2).');
            }

            document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
            document.getElementById('checkersArReadyModal')?.classList.add('hidden');
            document.getElementById('checkersRoomFullModal')?.classList.remove('hidden');

            showToast(`Room ${roomCode} is full (2/2)!`, "fa-users-slash");
        });

        // 3. OnOpponentJoined
        signalRConnection.on("OnOpponentJoined", (slot, oppName) => {
            console.log(`[MonkeyCheckers] Opponent joined: Slot ${slot}, Name: ${oppName}`);
            document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
            showToast(`${oppName} entered the room!`, "fa-user-check");
            playAudioFx('announcer', 'prepare_for_battle');

            const p2Label = document.getElementById('checkersP2ReadyName');
            if (p2Label) p2Label.innerText = `${oppName} (Hellbourne)`;

            promptMatchReady();
        });

        // 4. OnMatchPromptReady
        signalRConnection.on("OnMatchPromptReady", () => {
            console.log("[MonkeyCheckers] OnMatchPromptReady received");
            document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
            promptMatchReady();
        });

        // 5. OnPlayerReadyStatus
        signalRConnection.on("OnPlayerReadyStatus", (slot, isReady, wantsAr) => {
            console.log(`[MonkeyCheckers] OnPlayerReadyStatus: Slot ${slot}, Ready=${isReady}, AR=${wantsAr}`);
            updateReadyBadge(slot, isReady, wantsAr);
        });

        // 6. OnMatchStarted
        signalRConnection.on("OnMatchStarted", (p1Name, p2Name, startingTurn) => {
            console.log(`[MonkeyCheckers] Match started between ${p1Name} and ${p2Name}, Starting: ${startingTurn}`);
            document.getElementById('checkersWaitingOverlay')?.classList.add('hidden');
            document.getElementById('checkersArReadyModal')?.classList.add('hidden');

            showToast(`Match Started! ${startingTurn === 'legion' ? 'Legion' : 'Hellbourne'} moves first.`, "fa-play");
            playAudioFx('announcer', 'prepare_for_battle');

            // Slot 2 Hellbourne POV setup: rotate camera orientation so they face their opponent forward!
            if (playerSlot === 2 && !isCameraActive) {
                setCameraPreset('hellbourne');
            }

            restartMatch(true);
        });

        // 7. OnOpponentMove
        signalRConnection.on("OnOpponentMove", (fromRow, fromCol, toRow, toCol, isJump) => {
            console.log(`[MonkeyCheckers] OnOpponentMove received: (${fromRow},${fromCol}) -> (${toRow},${toCol}), jump=${isJump}`);
            handleOpponentMove(fromRow, fromCol, toRow, toCol, isJump);
        });

        // 8. OnOpponentLeft
        signalRConnection.on("OnOpponentLeft", (vacatedSlot) => {
            console.log(`[MonkeyCheckers] Opponent left (Slot ${vacatedSlot})`);
            showToast("Opponent left the room. Waiting for challenger...", "fa-user-slash");
            document.getElementById('checkersArReadyModal')?.classList.add('hidden');
            document.getElementById('checkersWaitingOverlay')?.classList.remove('hidden');

            isLocalReady = false;
            isRemoteReady = false;
            resetReadyModalUI();
        });

        // 9. OnMatchReset
        signalRConnection.on("OnMatchReset", () => {
            console.log("[MonkeyCheckers] Opponent reset match.");
            showToast("Match was reset by opponent.", "fa-rotate-right");
            restartMatch(true);
        });

        // 10. OnOpponentTaunt
        signalRConnection.on("OnOpponentTaunt", (tauntId) => {
            playHeroVoice('king');
        });
    }

    function promptMatchReady() {
        const modal = document.getElementById('checkersArReadyModal');
        if (!modal) return;
        modal.classList.remove('hidden');
        resetReadyModalUI();
    }

    function resetReadyModalUI() {
        isLocalReady = false;
        isRemoteReady = false;

        const btnYes = document.getElementById('btnCheckersArYes');
        const btnNo = document.getElementById('btnCheckersArNo');
        if (btnYes) {
            btnYes.disabled = false;
            btnYes.classList.remove('opacity-50', 'pointer-events-none');
        }
        if (btnNo) {
            btnNo.disabled = false;
            btnNo.classList.remove('opacity-50', 'pointer-events-none');
        }

        const waitText = document.getElementById('checkersWaitingForOpponentReadyText');
        if (waitText) waitText.classList.add('hidden');

        updateReadyBadge(1, false, false);
        updateReadyBadge(2, false, false);
    }

    function updateReadyBadge(slot, isReady, wantsAr) {
        const badge = document.getElementById(slot === 1 ? 'checkersP1ReadyBadge' : 'checkersP2ReadyBadge');
        const status = document.getElementById(slot === 1 ? 'checkersP1ReadyStatus' : 'checkersP2ReadyStatus');

        if (slot === playerSlot) {
            isLocalReady = isReady;
        } else {
            isRemoteReady = isReady;
        }

        if (badge && status) {
            if (isReady) {
                badge.className = slot === 1
                    ? "p-2 rounded-xl bg-emerald-950/60 border border-emerald-500/60 flex flex-col items-center justify-center gap-1 text-xs font-bold text-emerald-300"
                    : "p-2 rounded-xl bg-rose-950/60 border border-rose-500/60 flex flex-col items-center justify-center gap-1 text-xs font-bold text-rose-300";
                status.innerHTML = `<i class="fas fa-check text-emerald-400 mr-1"></i>${wantsAr ? 'AR Camera' : '3D Studio'}`;
            } else {
                badge.className = "p-2 rounded-xl bg-slate-950 border border-slate-800 flex flex-col items-center justify-center gap-1 text-xs font-bold text-slate-500";
                status.innerText = "Waiting...";
            }
        }

        const readyCount = (isLocalReady ? 1 : 0) + (isRemoteReady ? 1 : 0);
        const countText = document.getElementById('checkersReadyCountText');
        if (countText) countText.innerText = `${readyCount} / 2 Ready`;
    }

    function handleOpponentMove(fromRow, fromCol, toRow, toCol, isJump) {
        const piece = piecesGrid[fromRow][fromCol];
        if (!piece) {
            console.warn(`[MonkeyCheckers] Opponent move target piece not found at (${fromRow}, ${fromCol})`);
            return;
        }

        const legal = getLegalMovesForPiece(piece, piecesGrid, false);
        let move = legal.find(m => m.to.row === toRow && m.to.col === toCol);
        if (!move) {
            let captured = null;
            let capturedPos = null;
            if (isJump) {
                const midR = Math.floor((fromRow + toRow) / 2);
                const midC = Math.floor((fromCol + toCol) / 2);
                captured = piecesGrid[midR][midC];
                capturedPos = { row: midR, col: midC };
            }
            move = {
                from: { row: fromRow, col: fromCol },
                to: { row: toRow, col: toCol },
                isJump: isJump,
                capturedPiece: captured,
                capturedPos: capturedPos
            };
        }

        clearMoveTargets();
        executeMove(piece, move, () => {
            endTurn();
        });
    }

    // --- Global Init on Load ---
    document.addEventListener('DOMContentLoaded', () => {
        initThreeScene();
        summonAllPieces();

        const rootEl = document.getElementById('checkersGameRoot');
        const urlParams = new URLSearchParams(window.location.search);
        const urlRoom = urlParams.get('room');
        const urlMode = urlParams.get('mode');
        const initialRoom = urlRoom || (rootEl ? rootEl.dataset.initialRoom : null);

        if (urlMode) {
            const modeSelect = document.getElementById('selectGameMode');
            if (modeSelect) modeSelect.value = urlMode;
            gameMode = urlMode;
            if (gameMode === 'pass_and_play') {
                showToast("Pass & Play: 2 players share this screen!", "fa-users");
            }
        }

        if (urlRoom) {
            const input = document.getElementById('inputCheckersRoomCode');
            if (input && initialRoom) input.value = initialRoom.toUpperCase();
            openOnlineLobby();
        }
    });

    // --- Public API ---
    window.MonkeyKingCheckers = {
        restartMatch: restartMatch,
        undoMove: undoMove,
        toggleCamera: toggleCameraAR,
        setCameraPreset: setCameraPreset,
        getAllLegalMoves: getAllLegalMoves,
        getPieces: () => pieces,
        getBoard: () => piecesGrid,
        updateBoardTransparency: updateBoardTransparency,
        connectAndJoinRoom: connectAndJoinRoom,
        leaveOnlineRoom: leaveOnlineRoom,
        openOnlineLobby: openOnlineLobby
    };

})();
