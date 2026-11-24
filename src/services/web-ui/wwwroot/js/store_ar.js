// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Project KONGOR Webstore: Tabletop Augmented Reality (AR) Engine
 * Inspired by the Empath AR Companion.
 * Allows visitors to inspect any 3D hero avatar in live camera AR or 3D desk studio mode,
 * with real-time soft shadows, tap-to-walk locomotion, and interactive emotes.
 */

(function () {
    "use strict";

    // --- Module State ---
    let scene = null;
    let camera = null;
    let renderer = null;
    let controls = null;
    let clock = null;
    let animFrameId = null;

    let heroModel = null;
    let mixer = null;
    let actions = {};
    let activeAction = null;
    let defaultActionName = 'idle';
    let walkActionName = null;

    let groundPlane = null;
    let shadowPlane = null;
    let studioGrid = null;
    let pathLine = null;
    let destinationDot = null;

    // --- Locomotion State ---
    let isMoving = false;
    let targetPosition = null;
    const walkSpeed = 1.15; // Calibrated for smooth, steady tabletop walk pace
    const arrivalDistance = 0.07;
    let baseModelScale = 0.02; // Normalized figurine scale
    let userScaleMultiplier = 1.0;
    let modelYOffset = 0;

    // --- Hero Rotation & Drag State ---
    let isRotatingHero = false;
    let rotateStartX = 0;
    let rotateStartHeroY = 0;
    let heroDragDistance = 0;
    let isDraggingHeroPosition = false;

    // --- Audio System ---
    let isAudioMuted = false;
    let activeAudio = null;

    // --- Camera AR State ---
    let isCameraActive = false;
    let mediaStream = null;
    let currentHeroConfig = null;
    let isOpen = false;

    // --- Raycasting ---
    let raycaster = null;
    let pointer = null;

    // --- Dynamic Script Loader for Three.js r16x Local Vendored ESM + Kongor Viewer Core (#120, #86) ---
    async function ensureThreeLoaded() {
        const loadScript = (src) => new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = src;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });

        if (!window.KongorViewerCore) {
            await loadScript("/js/kongor-viewer-core.js");
        }
        if (window.KongorViewerCore && window.KongorViewerCore.ensureLocalThreeLoaded) {
            await window.KongorViewerCore.ensureLocalThreeLoaded();
            return;
        }
        if (!window.THREE || !window.THREE.GLTFLoader || !window.THREE.OrbitControls) {
            await loadScript("/lib/three/three.r16x.bundle.js");
        }
    }

    // --- Open AR Modal for Any Store Item ---
    async function open(config) {
        if (!config || !config.hero) {
            console.warn("[StoreAR] Invalid hero configuration passed to open().");
            return;
        }

        currentHeroConfig = {
            hero: (config.hero || "").replace(/^Hero_/, "").toLowerCase(),
            alt: config.alt || "",
            name: config.name || "Hero Figurine",
            resource: config.resource || ""
        };

        const modal = document.getElementById('storeArModal');
        if (!modal) {
            console.error("[StoreAR] #storeArModal element not found in DOM.");
            return;
        }

        if (!modal.dataset.kongorDialogBound) {
            modal.dataset.kongorDialogBound = '1';
            modal.addEventListener('close', () => {
                if (isOpen) close();
            });
        }
        modal.classList.remove('hidden');
        modal.classList.add('flex');
        if (typeof modal.showModal === 'function' && !modal.open) {
            try { modal.showModal(); } catch (_) {}
        }
        isOpen = true;

        // Update UI Title
        const titleEl = document.getElementById('storeArHeroTitle');
        if (titleEl) titleEl.innerText = currentHeroConfig.name;

        await ensureThreeLoaded();
        initScene();
        bindUIEvents();
        await loadHeroModel();

        // Attempt camera auto-activation on mobile / supported devices
        if (navigator.mediaDevices && navigator.mediaDevices.getUserMedia) {
            // Optional: User can tap "Enable Camera" or we can prompt if preferred
        }
    }

    // --- Close & Tear Down AR Viewer ---
    function close() {
        isOpen = false;
        stopCamera();

        if (activeAudio) {
            activeAudio.pause();
            activeAudio = null;
        }

        if (renderer) {
            renderer.setAnimationLoop(null);
        }

        const modal = document.getElementById('storeArModal');
        if (modal) {
            if (typeof modal.close === 'function' && modal.open) {
                try { modal.close(); } catch (_) {}
            }
            modal.classList.add('hidden');
            modal.classList.remove('flex');
        }

        // Clean up 3D meshes & mixer (#65 deterministic recursive GPU resource disposal)
        if (heroModel && scene) {
            KongorViewerCore.disposeObject3DResources(heroModel, renderer, mixer);
            scene.remove(heroModel);
            heroModel = null;
        }
        isRotatingHero = false;
        isDraggingHeroPosition = false;
        if (controls) controls.enabled = true;
        mixer = null;
        actions = {};
        activeAction = null;
    }

    // --- Initialize Three.js Viewport & Tabletop Setup ---
    function initScene() {
        const container = document.getElementById('storeArCanvasContainer');
        const canvas = document.getElementById('storeArCanvas');
        if (!container || !canvas) return;

        clock = new THREE.Clock();
        targetPosition = new THREE.Vector3();
        raycaster = new THREE.Raycaster();
        pointer = new THREE.Vector2();

        // 1. Scene
        scene = new THREE.Scene();

        // 2. Camera (Tabletop perspective ~45-50 degrees)
        const width = container.clientWidth || window.innerWidth;
        const height = container.clientHeight || window.innerHeight;
        camera = new THREE.PerspectiveCamera(48, width / height, 0.1, 100);
        resetCameraView();

        // 3. WebGL Renderer (Alpha enabled for live camera feed passthrough)
        renderer = new THREE.WebGLRenderer({
            canvas: canvas,
            alpha: true,
            antialias: true,
            powerPreference: "high-performance"
        });
        renderer.setSize(width, height);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.shadowMap.enabled = true;
        renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        KongorViewerCore.configureRendererColorSpace(renderer);
        renderer.toneMapping = THREE.AgXToneMapping !== undefined ? THREE.AgXToneMapping : THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.25;

        // 4. Orbit Controls (Anchored strictly to tabletop center)
        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.05;
        controls.maxPolarAngle = Math.PI / 2 - 0.03; // Disallow going below table
        controls.minDistance = 0.35;
        controls.maxDistance = 6.0;
        controls.enablePan = false;
        controls.target.set(0, 0.25, 0);

        // 5. Studio Lighting Rig
        setupLighting();

        // 6. Contact Shadow Receiver & Raycasting Planes
        setupPlanes();

        // 7. Dynamic Locomotion Visualizers (Dashed Path Line & Target Dot)
        setupPathVisualizer();
        setupDestinationMarker();

        // 8. Event Listeners & Phase 7 Fractional DPI ResizeObserver
        window.addEventListener('resize', onWindowResize);
        if (window.screen && window.screen.orientation) {
            window.screen.orientation.addEventListener('change', onWindowResize);
        }
        if (KongorViewerCore.observeDevicePixelCanvasResize) {
            KongorViewerCore.observeDevicePixelCanvasResize(container, renderer, camera);
        }

        // 9. Start Render Loop
        renderer.setAnimationLoop(renderLoop);
    }

    function resetCameraView() {
        if (!camera) return;
        camera.position.set(0, 1.4, 2.0);
        camera.lookAt(0, 0.25, 0);
        if (controls) {
            controls.target.set(0, 0.25, 0);
            controls.update();
        }
    }

    function setupLighting() {
        const hemiLight = new THREE.HemisphereLight(0xffffff, 0x334155, KongorViewerCore.calibrateLightIntensity(1.2));
        hemiLight.position.set(0, 10, 0);
        scene.add(hemiLight);

        const dirLight = new THREE.DirectionalLight(0xfffaed, KongorViewerCore.calibrateLightIntensity(1.8));
        dirLight.position.set(2.5, 5, 3.5);
        dirLight.castShadow = true;
        dirLight.shadow.mapSize.width = 1024;
        dirLight.shadow.mapSize.height = 1024;
        dirLight.shadow.bias = -0.0001;
        dirLight.shadow.camera.near = 0.1;
        dirLight.shadow.camera.far = 12;
        scene.add(dirLight);

        const auraLight = new THREE.PointLight(0x38bdf8, KongorViewerCore.calibrateLightIntensity(1.2), 5);
        auraLight.position.set(0, 0.8, 0);
        scene.add(auraLight);
    }

    function setupPlanes() {
        // Invisible shadow plane to receive realistic contact shadows on real table/desk
        const shadowGeo = new THREE.PlaneGeometry(25, 25);
        const shadowMat = new THREE.ShadowMaterial({ opacity: 0.38 });
        shadowPlane = new THREE.Mesh(shadowGeo, shadowMat);
        shadowPlane.rotation.x = -Math.PI / 2;
        shadowPlane.receiveShadow = true;
        scene.add(shadowPlane);

        // Infinite raycast plane for tabletop taps
        const groundGeo = new THREE.PlaneGeometry(40, 40);
        const groundMat = new THREE.MeshBasicMaterial({ visible: false });
        groundPlane = new THREE.Mesh(groundGeo, groundMat);
        groundPlane.rotation.x = -Math.PI / 2;
        scene.add(groundPlane);

        // Studio Desk Grid (Visible only in non-camera mode)
        studioGrid = new THREE.GridHelper(4, 20, 0x0284c7, 0x1e293b);
        studioGrid.position.y = -0.001;
        scene.add(studioGrid);
    }

    function setupPathVisualizer() {
        const pathGeo = new THREE.BufferGeometry();
        pathGeo.setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, 0)]);

        const pathMat = new THREE.LineDashedMaterial({
            color: 0x38bdf8,
            dashSize: 0.06,
            gapSize: 0.03,
            linewidth: 3,
            transparent: true,
            opacity: 0.9
        });

        pathLine = new THREE.Line(pathGeo, pathMat);
        pathLine.computeLineDistances();
        pathLine.visible = false;
        pathLine.position.y = 0.005;
        scene.add(pathLine);
    }

    function setupDestinationMarker() {
        const dotGeo = new THREE.CircleGeometry(0.045, 20);
        const dotMat = new THREE.MeshBasicMaterial({
            color: 0x38bdf8,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.85
        });
        destinationDot = new THREE.Mesh(dotGeo, dotMat);
        destinationDot.rotation.x = -Math.PI / 2;
        destinationDot.position.y = 0.006;
        destinationDot.visible = false;
        scene.add(destinationDot);
    }

    function updatePathVisualizer() {
        if (!isMoving || !heroModel || !pathLine) {
            if (pathLine) pathLine.visible = false;
            if (destinationDot) destinationDot.visible = false;
            return;
        }

        pathLine.visible = true;
        if (destinationDot) destinationDot.visible = true;

        const points = [
            new THREE.Vector3(heroModel.position.x, 0, heroModel.position.z),
            new THREE.Vector3(targetPosition.x, 0, targetPosition.z)
        ];
        pathLine.geometry.setFromPoints(points);
        pathLine.computeLineDistances();
    }

    // --- Load Hero Model into AR Viewport ---
    async function loadHeroModel() {
        const loading = document.getElementById('storeArLoading');
        const progressEl = document.getElementById('storeArLoadingProgress');
        if (loading) loading.classList.remove('hidden');
        if (progressEl) progressEl.innerText = "Streaming 3D Mesh Geometry...";

        const hero = currentHeroConfig.hero;
        const alt = currentHeroConfig.alt ? `/${encodeURIComponent(currentHeroConfig.alt)}` : '';
        const resourceQuery = currentHeroConfig.resource ? `?resource=${encodeURIComponent(currentHeroConfig.resource)}` : '';
        const candidateUrls = [
            `/api/v1/assets/3d/hero/${encodeURIComponent(hero)}${alt}${resourceQuery}`,
            `/api/v1/assets/3d/hero/${encodeURIComponent(hero)}`,
            `/assets/prebaked/models/${encodeURIComponent(hero)}_default.glb`
        ];

        let buffer = null;
        for (const url of candidateUrls) {
            try {
                const resp = await fetch(url);
                if (resp.ok) {
                    buffer = await resp.arrayBuffer();
                    break;
                }
            } catch (e) {
                // Try next candidate
            }
        }

        if (!buffer || buffer.byteLength === 0) {
            console.error("[StoreAR] Failed to load 3D model buffer.");
            if (progressEl) progressEl.innerHTML = `<span class="text-rose-400">Failed to load hero model.</span>`;
            return;
        }

        try {
            const loader = KongorViewerCore.createKongorGLTFLoader();
            loader.parse(
                buffer,
                '',
                (gltf) => {
                    if (heroModel && scene) {
                        KongorViewerCore.disposeObject3DResources(heroModel, renderer, mixer);
                        scene.remove(heroModel);
                    }

                    heroModel = gltf.scene;

                    // Configure Shadows & Authentic Textures
                    heroModel.traverse((node) => {
                        if (node.isMesh) {
                            node.castShadow = true;
                            node.receiveShadow = true;
                            if (node.material) {
                                node.material.side = THREE.DoubleSide;
                                node.material.roughness = 0.5;
                                node.material.metalness = 0.2;
                                if (node.material.map) {
                                    KongorViewerCore.configureTextureColorSpace(node.material.map);
                                }
                                node.material.needsUpdate = true;
                            }
                        }
                    });

                    // Phase 7 (#89 & #73): Apply Filament PBR shader upgrades and AR camera-facing yaw/floor alignment
                    if (KongorViewerCore.applyFilamentPbrShaderUpgrades) {
                        KongorViewerCore.applyFilamentPbrShaderUpgrades(heroModel, renderer);
                    }

                    // Setup Animation Mixer first so SkinnedMesh bones are posed in idle before measuring standing bounds
                    mixer = new THREE.AnimationMixer(heroModel);
                    actions = {};
                    walkActionName = null;
                    defaultActionName = 'idle';

                    const animSelect = document.getElementById('storeArAnimSelect');
                    if (animSelect) animSelect.innerHTML = '';

                    gltf.animations.forEach((clip) => {
                        const act = mixer.clipAction(clip);
                        actions[clip.name] = act;

                        const nameLower = clip.name.toLowerCase();
                        if (nameLower.includes('walk') || nameLower.includes('run')) {
                            if (!walkActionName) walkActionName = clip.name;
                        }
                        if (nameLower.includes('idle') && defaultActionName === 'idle') {
                            defaultActionName = clip.name;
                        }

                        if (animSelect) {
                            const opt = document.createElement('option');
                            opt.value = clip.name;
                            opt.textContent = clip.name.replace(/_/g, ' ').toUpperCase();
                            animSelect.appendChild(opt);
                        }
                    });

                    // Default to first animation if no explicit idle found
                    if (!actions[defaultActionName] && gltf.animations.length > 0) {
                        defaultActionName = gltf.animations[0].name;
                    }

                    playAction(defaultActionName);
                    mixer.update(0.001);

                    // Normalize figurine size across all heroes (~35cm tabletop height) and align yaw toward camera (#73)
                    if (KongorViewerCore.alignModelToCameraAndFloor) {
                        const alignRes = KongorViewerCore.alignModelToCameraAndFloor(
                            heroModel,
                            camera,
                            new THREE.Vector3(0, 0, 0),
                            'tabletop',
                            userScaleMultiplier
                        );
                        if (alignRes) {
                            baseModelScale = alignRes.baseScale;
                            modelYOffset = -alignRes.unscaledMinY;
                        }
                    } else {
                        const bbox = new THREE.Box3().setFromObject(heroModel, true);
                        const size = new THREE.Vector3();
                        bbox.getSize(size);
                        const maxDim = Math.max(size.x, size.y, size.z, 1.0);
                        baseModelScale = 0.35 / maxDim;
                        modelYOffset = -bbox.min.y;
                        applyScale();
                    }
                    scene.add(heroModel);

                    if (loading) loading.classList.add('hidden');

                    // Play initial summoning voice line
                    setTimeout(() => {
                        playHeroSound('select');
                    }, 400);
                },
                (parseErr) => {
                    console.error("[StoreAR] GLTF Parse Error:", parseErr);
                    if (progressEl) progressEl.innerHTML = `<span class="text-rose-400">Error parsing 3D model.</span>`;
                }
            );
        } catch (err) {
            console.error("[StoreAR] Exception loading model:", err);
            if (progressEl) {
                progressEl.className = 'text-xs text-rose-400 font-mono mt-1';
                progressEl.textContent = `Model load error: ${err && err.message ? err.message : String(err)}`;
            }
        }
    }

    function applyScale() {
        if (!heroModel) return;
        const s = baseModelScale * userScaleMultiplier;
        heroModel.scale.set(s, s, s);
        heroModel.position.y = modelYOffset * s;
    }

    function playAction(name, crossFadeDuration = 0.25, loop = true) {
        if (!mixer || !actions[name]) return;
        const nextAction = actions[name];
        if (activeAction === nextAction) return;

        nextAction.reset();
        nextAction.setLoop(loop ? THREE.LoopRepeat : THREE.LoopOnce, loop ? Infinity : 1);
        nextAction.clampWhenFinished = !loop;

        if (activeAction) {
            nextAction.crossFadeFrom(activeAction, crossFadeDuration, true);
        }
        nextAction.play();
        activeAction = nextAction;

        const select = document.getElementById('storeArAnimSelect');
        if (select && select.value !== name) {
            select.value = name;
        }
    }

    // --- Tap-to-Move & Hero Interaction ---
    function bindUIEvents() {
        const container = document.getElementById('storeArCanvasContainer');
        if (!container) return;

        // Prevent browser context menu so right-click cleanly repositions the hero
        container.oncontextmenu = (e) => e.preventDefault();

        container.onpointerdown = (e) => {
            if (!renderer || !camera) return;

            // Ignore non-standard mouse buttons (e.g. browser Back/Forward buttons 3 and 4) while allowing Left (0), Middle (1), and Right (2)
            if (e.button !== 0 && e.button !== 1 && e.button !== 2) {
                return;
            }

            const rect = renderer.domElement._kongorRect = renderer.domElement.getBoundingClientRect();
            pointer.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
            pointer.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

            raycaster.setFromCamera(pointer, camera);

            // 0. Middle-Click (e.button === 1): Raycast focal pivot onto hero or tabletop (#122 store-ar-middle-click)
            if (e.button === 1) {
                e.preventDefault();
                const targets = [];
                if (heroModel) targets.push(heroModel);
                if (groundPlane) targets.push(groundPlane);
                const hits = raycaster.intersectObjects(targets, true);
                if (hits.length > 0 && controls && controls.target) {
                    controls.target.copy(hits[0].point);
                    controls.update();
                    console.log(`[KongorStoreAR] [FOCAL-PIVOT] Centered orbit pivot at (${hits[0].point.x.toFixed(2)}, ${hits[0].point.y.toFixed(2)}, ${hits[0].point.z.toFixed(2)})`);
                }
                return;
            }

            // 1. Right-Click: Click & hold drag to reposition hero anywhere across tabletop
            if (e.button === 2) {
                e.preventDefault();
                isDraggingHeroPosition = true;
                if (controls) controls.enabled = false;
                try { container.setPointerCapture(e.pointerId); } catch (_) {}
                if (groundPlane) {
                    const hits = raycaster.intersectObject(groundPlane);
                    if (hits.length > 0) {
                        repositionHeroTo(hits[0].point);
                    }
                }
                return;
            }

            // 2. Left-Click (e.button === 0):
            if (e.button === 0) {
                // On Hero: Click and hold/drag to rotate hero directly in place
                if (heroModel) {
                    const heroHits = raycaster.intersectObject(heroModel, true);
                    if (heroHits.length > 0) {
                        isRotatingHero = true;
                        rotateStartX = e.clientX;
                        rotateStartHeroY = heroModel.rotation.y;
                        heroDragDistance = 0;
                        if (controls) controls.enabled = false;
                        try { container.setPointerCapture(e.pointerId); } catch (_) {}
                        return;
                    }
                }

                // On Tabletop: Direct hero to walk gracefully to target spot
                if (groundPlane) {
                    const hits = raycaster.intersectObject(groundPlane);
                    if (hits.length > 0) {
                        moveHeroTo(hits[0].point);
                    }
                }
            }
        };

        container.onpointermove = (e) => {
            const rect = renderer.domElement._kongorRect || (renderer.domElement._kongorRect = renderer.domElement.getBoundingClientRect());
            pointer.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
            pointer.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

            // Drag reposition hero across the ground plane
            if (isDraggingHeroPosition && heroModel && groundPlane && camera) {
                raycaster.setFromCamera(pointer, camera);
                const hits = raycaster.intersectObject(groundPlane);
                if (hits.length > 0) {
                    const p = hits[0].point;
                    heroModel.position.x = p.x;
                    heroModel.position.z = p.z;
                    targetPosition.set(p.x, 0, p.z);
                }
                return;
            }

            // Drag rotate hero around vertical Y axis
            if (isRotatingHero && heroModel) {
                const deltaX = e.clientX - rotateStartX;
                heroDragDistance += Math.abs(deltaX);
                heroModel.rotation.y = rotateStartHeroY + deltaX * 0.018;
            }
        };

        container.onpointerup = (e) => {
            try {
                if (container.hasPointerCapture && container.hasPointerCapture(e.pointerId)) {
                    container.releasePointerCapture(e.pointerId);
                }
            } catch (_) {}

            if (isDraggingHeroPosition) {
                isDraggingHeroPosition = false;
                if (controls) controls.enabled = true;
                playAction(defaultActionName, 0.2);
            }

            if (isRotatingHero) {
                isRotatingHero = false;
                if (controls) controls.enabled = true;
                // If clicked without significant drag (< 6px), trigger emote & voice line
                if (heroDragDistance < 6) {
                    onHeroTapped();
                }
                heroDragDistance = 0;
            }
        };

        container.onpointercancel = (e) => {
            try {
                if (container.hasPointerCapture && container.hasPointerCapture(e.pointerId)) {
                    container.releasePointerCapture(e.pointerId);
                }
            } catch (_) {}

            if (isDraggingHeroPosition) {
                isDraggingHeroPosition = false;
                if (controls) controls.enabled = true;
            }

            if (isRotatingHero) {
                isRotatingHero = false;
                if (controls) controls.enabled = true;
                heroDragDistance = 0;
            }
        };

        // Camera Toggle
        const btnToggleCam = document.getElementById('btnStoreArToggleCamera');
        if (btnToggleCam) btnToggleCam.onclick = toggleCameraAR;

        // Audio Toggle
        const btnToggleAudio = document.getElementById('btnStoreArToggleAudio');
        if (btnToggleAudio) btnToggleAudio.onclick = toggleAudio;

        // Scale Select
        const scaleSelect = document.getElementById('storeArScaleSelect');
        if (scaleSelect) {
            scaleSelect.onchange = (e) => {
                userScaleMultiplier = parseFloat(e.target.value) || 1.0;
                applyScale();
            };
        }

        // Animation Select
        const animSelect = document.getElementById('storeArAnimSelect');
        if (animSelect) {
            animSelect.onchange = (e) => {
                if (e.target.value) playAction(e.target.value);
            };
        }

        // Reset Placement
        const btnReset = document.getElementById('btnStoreArResetPlacement');
        if (btnReset) {
            btnReset.onclick = () => {
                if (heroModel) {
                    heroModel.position.set(0, modelYOffset * baseModelScale * userScaleMultiplier, 0);
                    targetPosition.set(0, 0, 0);
                    isMoving = false;
                    if (destinationDot) destinationDot.visible = false;
                    if (pathLine) pathLine.visible = false;
                    playAction(defaultActionName);
                    resetCameraView();
                }
            };
        }
    }

    function repositionHeroTo(point) {
        if (!heroModel) return;

        isMoving = false;
        targetPosition.set(point.x, 0, point.z);

        if (destinationDot) destinationDot.visible = false;
        if (pathLine) pathLine.visible = false;

        heroModel.position.x = point.x;
        heroModel.position.z = point.z;

        playAction(defaultActionName, 0.2);
        playHeroSound('select');
    }

    function onHeroTapped() {
        if (!heroModel) return;

        // Audition random emote action (taunt, bored, attack, ability)
        const candidateEmotes = Object.keys(actions).filter(k => {
            const l = k.toLowerCase();
            return l.includes('taunt') || l.includes('bored') || l.includes('attack') || l.includes('ability');
        });

        if (candidateEmotes.length > 0 && !isMoving) {
            const emote = candidateEmotes[Math.floor(Math.random() * candidateEmotes.length)];
            playAction(emote, 0.2, false);

            setTimeout(() => {
                if (!isMoving) playAction(defaultActionName, 0.3);
            }, 1900);
        }

        playHeroSound('taunt');
    }

    function moveHeroTo(destination) {
        if (!heroModel) return;

        targetPosition.set(destination.x, 0, destination.z);
        isMoving = true;

        destinationDot.position.set(targetPosition.x, 0.006, targetPosition.z);
        destinationDot.visible = true;

        // Rotate smoothly toward destination
        const dx = targetPosition.x - heroModel.position.x;
        const dz = targetPosition.z - heroModel.position.z;
        heroModel.rotation.y = Math.atan2(dx, dz);

        // Start walking animation
        const walkClip = walkActionName || Object.keys(actions).find(k => k.toLowerCase().includes('walk') || k.toLowerCase().includes('run')) || defaultActionName;
        playAction(walkClip, 0.15);

        // Play movement voice or step sound
        playHeroSound('move');
        updatePathVisualizer();
    }

    function updateMovement(delta) {
        if (!isMoving || !heroModel) return;

        const currentPos = heroModel.position;
        const targetFlat = new THREE.Vector3(targetPosition.x, currentPos.y, targetPosition.z);
        const dist = currentPos.distanceTo(targetFlat);

        if (dist <= arrivalDistance) {
            currentPos.x = targetPosition.x;
            currentPos.z = targetPosition.z;
            isMoving = false;
            if (destinationDot) destinationDot.visible = false;
            if (pathLine) pathLine.visible = false;
            playAction(defaultActionName, 0.25);
            return;
        }

        const step = walkSpeed * delta;
        const dir = new THREE.Vector3().subVectors(targetFlat, currentPos).normalize();
        heroModel.position.addScaledVector(dir, Math.min(step, dist));

        updatePathVisualizer();
    }

    // --- Audio Feedback ---
    function playHeroSound(actionType) {
        if (isAudioMuted || !currentHeroConfig) return;

        if (activeAudio) {
            activeAudio.pause();
            activeAudio = null;
        }

        const soundUrl = `/api/v1/assets/sound?hero=${encodeURIComponent(currentHeroConfig.hero)}&alt=${encodeURIComponent(currentHeroConfig.alt || '')}&action=${encodeURIComponent(actionType)}&_t=${Date.now()}`;
        const audio = new Audio(soundUrl);
        audio.volume = 0.75;
        activeAudio = audio;

        audio.play().catch(() => {
            // Gracefully ignore audio blocked by browser policy
        });

        audio.onended = () => {
            if (activeAudio === audio) activeAudio = null;
        };
    }

    function toggleAudio() {
        isAudioMuted = !isAudioMuted;
        if (isAudioMuted && activeAudio) {
            activeAudio.pause();
        }
        const icon = document.getElementById('storeArAudioIcon');
        if (icon) {
            icon.className = isAudioMuted ? "fas fa-volume-mute text-rose-400" : "fas fa-volume-up text-emerald-400";
        }
    }

    // --- Camera AR Passthrough Management ---
    async function toggleCameraAR() {
        if (isCameraActive) {
            stopCamera();
        } else {
            await startCamera();
        }
    }

    async function startCamera() {
        const video = document.getElementById('storeArVideo');
        const btnText = document.getElementById('storeArCameraBtnText');
        const btn = document.getElementById('btnStoreArToggleCamera');
        const subtitle = document.getElementById('storeArModeSubtitle');

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

            if (studioGrid) studioGrid.visible = false;

            // In camera mode: lock orbit tilt so taps strictly direct character walking
            if (controls) controls.enableRotate = false;
            resetCameraView();

            isCameraActive = true;
            if (btnText) btnText.innerText = "Stop Camera";
            if (btn) btn.classList.add('border-emerald-500', 'bg-emerald-950/40');
            if (subtitle) subtitle.innerText = "Live Camera AR";
        } catch (err) {
            console.warn("[StoreAR] Camera activation denied or unavailable:", err);
            alert("Camera access denied or unavailable. You can continue interacting in 3D Studio Desk Mode!");
        }
    }

    function stopCamera() {
        if (mediaStream) {
            mediaStream.getTracks().forEach(track => track.stop());
            mediaStream = null;
        }

        const video = document.getElementById('storeArVideo');
        if (video) {
            video.classList.add('hidden');
            video.srcObject = null;
        }

        if (studioGrid) studioGrid.visible = true;
        if (controls) controls.enableRotate = true;

        isCameraActive = false;
        const btnText = document.getElementById('storeArCameraBtnText');
        const btn = document.getElementById('btnStoreArToggleCamera');
        const subtitle = document.getElementById('storeArModeSubtitle');

        if (btnText) btnText.innerText = "Enable Camera";
        if (btn) btn.classList.remove('border-emerald-500', 'bg-emerald-950/40');
        if (subtitle) subtitle.innerText = "Studio Desk Mode";
    }

    function onWindowResize() {
        const container = document.getElementById('storeArCanvasContainer');
        if (!container || !camera || !renderer) return;

        const w = container.clientWidth || window.innerWidth;
        const h = container.clientHeight || window.innerHeight;
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        renderer.setSize(w, h);
        renderer.domElement._kongorRect = null;
    }

    // --- Main Render Loop ---
    function renderLoop() {
        if (!isOpen) return;

        const delta = clock.getDelta();

        if (mixer) mixer.update(delta);
        updateMovement(delta);

        if (controls && controls.enabled) controls.update();
        if (renderer && scene && camera) renderer.render(scene, camera);
    }

    // --- Global Public Export ---
    window.KongorStoreAR = {
        open: open,
        close: close,
        toggleCamera: toggleCameraAR,
        toggleAudio: toggleAudio,
        playAction: playAction,
        applyScale: applyScale,
        repositionHeroTo: repositionHeroTo
    };

})();
