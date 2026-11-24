// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * NEXUS 3D In-Browser Vanity Booth (Three.js WebGL Engine)
 * Renders on-demand K2 glTF models with studio lighting, arena pedestal, orbit controls, and animation playback.
 */

(function () {
    let scene, camera, renderer, controls, mixer, clock, modelGroup;
    let currentModel = null;
    let currentActions = {};
    let activeAction = null;
    let isInitialized = false;
    let isAutoRotating = false;
    let isPaused = false;
    let animSpeed = 1.0;
    let currentHero = "";
    let currentAlt = "";
    let isSoundMuted = false;
    let currentAudio = null;
    let vanityLoadCounter = 0;
    let vanityFetchController = null;

    async function ensureThreeLoaded() {
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

    function loadScript(src) {
        return new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = src;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
    }

    function initViewport(container) {
        const width = container.clientWidth || 800;
        const height = container.clientHeight || 550;

        // Scene
        scene = new THREE.Scene();
        scene.background = new THREE.Color(0x0c0f17);
        scene.fog = new THREE.FogExp2(0x0c0f17, 0.005);

        modelGroup = new THREE.Group();
        scene.add(modelGroup);

        // Camera
        camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 2000);
        camera.position.set(0, 80, 220);

        // Renderer with ACESFilmic tone mapping & high-performance GPU pipeline
        renderer = new THREE.WebGLRenderer({antialias: true, alpha: false, preserveDrawingBuffer: true, powerPreference: "high-performance"});
        renderer.setSize(width, height);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.shadowMap.enabled = true;
        renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        KongorViewerCore.configureRendererColorSpace(renderer);
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.25;

        container.innerHTML = "";
        container.appendChild(renderer.domElement);

        // Orbit Controls
        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.05;
        controls.maxDistance = 600;
        controls.minDistance = 30;
        controls.target.set(0, 50, 0);

        // Lighting Studio
        setupStudioLighting();

        // Arena Pedestal + fwidth Radial Grid (#68)
        setupArenaPedestal();

        // Clock & Wake Render Scheduler (#74, #90)
        clock = new THREE.Clock();
        vanityLruCache = KongorViewerCore.createTwoFactorLruGpuCache();
        vanityWakeScheduler = KongorViewerCore.createWakeRenderScheduler({
            container: container,
            controls: controls,
            isActive: () => Boolean((mixer && !isPaused) || isAutoRotating),
            onFrame: () => {
                const delta = clock.getDelta();
                if (mixer && !isPaused) {
                    mixer.update(delta * animSpeed);
                }
                if (controls) {
                    controls.autoRotate = isAutoRotating;
                    controls.autoRotateSpeed = 2.0;
                    controls.update();
                }
                if (renderer && scene && camera) {
                    renderer.render(scene, camera);
                }
            }
        });
        KongorViewerCore.observeDevicePixelCanvasResize(container, renderer.domElement, renderer, camera, () => {
            if (vanityWakeScheduler) vanityWakeScheduler.requestRender();
        });
        KongorViewerCore.bindRaycastFocalPivotAndHotkeys({
            domElement: renderer.domElement,
            camera: camera,
            controls: controls,
            getTargetObject: () => currentModel,
            requestRender: () => vanityWakeScheduler && vanityWakeScheduler.requestRender()
        });
        KongorViewerCore.bindDollyToCursorAndElevationClamp(
            camera,
            controls,
            renderer.domElement,
            () => currentModel,
            () => vanityWakeScheduler && vanityWakeScheduler.requestRender()
        );
        KongorViewerCore.bindHdriAzimuthDrag({
            domElement: renderer.domElement,
            scene: scene,
            lights: vanityStudioLights,
            requestRender: () => vanityWakeScheduler && vanityWakeScheduler.requestRender()
        });

        window.addEventListener("resize", () => {
            if (!container || !renderer || !camera) return;
            const w = container.clientWidth;
            const h = container.clientHeight;
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
            renderer.setSize(w, h);
            if (vanityWakeScheduler) vanityWakeScheduler.requestRender();
        });

        isInitialized = true;
    }

    let vanityKeyLight = null;
    let vanityStudioLights = null;
    let vanityWakeScheduler = null;
    let vanityLruCache = null;
    let vanityGridGroup = null;

    function setupStudioLighting() {
        // Ambient Light
        const ambientLight = new THREE.AmbientLight(0xffffff, KongorViewerCore.calibrateLightIntensity(1.2));
        scene.add(ambientLight);

        // Key Light (Warm Key)
        const keyLight = new THREE.DirectionalLight(0xfffaed, KongorViewerCore.calibrateLightIntensity(2.0));
        keyLight.position.set(80, 160, 120);
        keyLight.castShadow = true;
        keyLight.shadow.mapSize.width = 2048;
        keyLight.shadow.mapSize.height = 2048;
        keyLight.shadow.bias = -0.0001;
        vanityKeyLight = keyLight;
        scene.add(keyLight);

        // Fill Light (Atmospheric Violet)
        const fillLight = new THREE.DirectionalLight(0xa855f7, KongorViewerCore.calibrateLightIntensity(1.1));
        fillLight.position.set(-120, 80, 80);
        scene.add(fillLight);

        // Rim Light (Brilliant Cyan Rim)
        const rimLight = new THREE.DirectionalLight(0x38bdf8, KongorViewerCore.calibrateLightIntensity(1.5));
        rimLight.position.set(0, 120, -150);
        scene.add(rimLight);

        // Soft ground bounce light
        const bounceLight = new THREE.HemisphereLight(0xffffff, 0x1e1b4b, KongorViewerCore.calibrateLightIntensity(0.7));
        scene.add(bounceLight);

        vanityStudioLights = {
            ambient: ambientLight,
            key: keyLight,
            fill: fillLight,
            rim: rimLight
        };
    }

    function setupArenaPedestal() {
        vanityGridGroup = new THREE.Group();
        // Obsidian Pedestal Cylinder
        const pedestalGeo = new THREE.CylinderGeometry(70, 75, 10, 64);
        const pedestalMat = new THREE.MeshStandardMaterial({
            color: 0x161922,
            metalness: 0.85,
            roughness: 0.25
        });
        const pedestal = new THREE.Mesh(pedestalGeo, pedestalMat);
        pedestal.position.y = -5;
        pedestal.receiveShadow = true;
        vanityGridGroup.add(pedestal);

        // Glowing Arena Ring
        const ringGeo = new THREE.RingGeometry(72, 75, 64);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0x00f0ff,
            side: THREE.DoubleSide
        });
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.y = 0.1;
        vanityGridGroup.add(ring);

        // Procedural fwidth radial floor grid (#68)
        const fwidthGrid = KongorViewerCore.createFwidthRadialFloorGrid({ diagonal: 140 });
        if (fwidthGrid) {
            fwidthGrid.position.y = 0.15;
            vanityGridGroup.add(fwidthGrid);
        }
        scene.add(vanityGridGroup);
    }

    function animate() {
        if (vanityWakeScheduler) {
            vanityWakeScheduler.requestRender();
        }
    }

    const TEXTURE_MAP_SLOTS = (window.KongorViewerCore && window.KongorViewerCore.ALL_TEXTURE_MAP_SLOTS) || [];

    async function loadHeroModel(heroName, altName = null) {
        currentHero = heroName;
        currentAlt = altName;
        const altSuffix = altName ? `/${encodeURIComponent(altName)}` : "";
        const url = `/api/v1/assets/3d/hero/${encodeURIComponent(heroName)}${altSuffix}`;
        return loadModelUrl(url, heroName);
    }

    async function loadModelUrl(url, displayLabel = "") {
        if (!isInitialized) return;

        const thisLoadId = ++vanityLoadCounter;
        if (vanityFetchController) {
            try { vanityFetchController.abort(); } catch (_) {}
        }
        vanityFetchController = typeof AbortController !== "undefined" ? new AbortController() : null;

        hideError();
        // Zero-blanking (#88): keep previous model visible during hot-reload
        if (!currentModel) {
            showLoading(true, displayLabel ? `Loading ${displayLabel}...` : "Loading 3D Model...");
        }

        try {
            let arrayBuffer = vanityLruCache ? vanityLruCache.get(url) : null;
            if (!arrayBuffer) {
                const fetchOpts = vanityFetchController ? { signal: vanityFetchController.signal } : {};
                const response = await fetch(url, fetchOpts);
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                arrayBuffer = await response.arrayBuffer();
                if (vanityLruCache && arrayBuffer) {
                    vanityLruCache.put(url, arrayBuffer, arrayBuffer.byteLength, renderer);
                }
            }

            if (thisLoadId !== vanityLoadCounter) {
                return;
            }

            const loader = KongorViewerCore.createKongorGLTFLoader();
            loader.parse(
                arrayBuffer,
                "",
                (gltf) => {
                    if (thisLoadId !== vanityLoadCounter) {
                        return;
                    }

                    showLoading(false);

                    // Deterministically dispose previous model GPU buffers before clearing modelGroup (#65 f3d-recursive-gpu-dispose)
                    clearCurrentModel();
                    if (modelGroup) {
                        modelGroup.clear();
                    }

                    currentModel = gltf.scene;

                    // Configure Shadows & Materials for all sub-meshes
                    currentModel.traverse((child) => {
                        if (child.isMesh) {
                            child.castShadow = true;
                            child.receiveShadow = true;
                            if (child.material) {
                                const configureMaterial = (mat) => {
                                    mat.side = THREE.DoubleSide;
                                    mat.metalness = 0.22;
                                    mat.roughness = 0.55;
                                    mat.envMapIntensity = 1.35;

                                    if (mat.blending === THREE.AdditiveBlending || (mat.opacity !== undefined && mat.opacity < 1.0)) {
                                        mat.transparent = true;
                                        mat.depthWrite = false;
                                    } else {
                                        mat.transparent = false;
                                        mat.depthWrite = true;
                                    }

                                    if (mat.map) {
                                        KongorViewerCore.configureTextureColorSpace(mat.map);
                                        mat.map.generateMipmaps = true;
                                        mat.map.minFilter = THREE.LinearMipmapLinearFilter;
                                        mat.map.magFilter = THREE.LinearFilter;
                                        mat.map.needsUpdate = true;
                                    }
                                    if (mat.emissiveMap) {
                                        KongorViewerCore.configureTextureColorSpace(mat.emissiveMap);
                                        mat.emissive = new THREE.Color(0xffffff);
                                        mat.emissiveIntensity = 1.2;
                                        mat.emissiveMap.needsUpdate = true;
                                    }
                                    mat.needsUpdate = true;
                                };

                                if (Array.isArray(child.material)) {
                                    child.material.forEach(configureMaterial);
                                } else {
                                    configureMaterial(child.material);
                                }
                            }
                        }
                    });

                    KongorViewerCore.applyFilamentPbrShaderUpgrades(currentModel, renderer);
                    KongorViewerCore.applyStochasticIgnAlpha(currentModel, true);

                    if (modelGroup) {
                        modelGroup.add(currentModel);
                    } else {
                        scene.add(currentModel);
                    }

                    // Screen-space bounding-box auto-framing (#64), floor grid alignment (#68), and texel-snapped shadows (#107)
                    KongorViewerCore.resetToBounds(camera, controls, currentModel, 0.9);
                    if (vanityGridGroup) {
                        KongorViewerCore.alignFloorGridToBounds(vanityGridGroup, currentModel);
                    }
                    if (vanityKeyLight) {
                        KongorViewerCore.snapDirectionalShadowFrustum(vanityKeyLight, currentModel, 2048);
                    }

                    // Setup Animations
                    setupAnimations(gltf.animations);
                    if (vanityWakeScheduler) {
                        vanityWakeScheduler.requestRender();
                    }
                },
                (error) => {
                    console.error("[VanityBooth3D] Failed to parse GLTF model:", error);
                    showLoading(false);
                    showError(`Failed to load 3D model: ${error.message || "Model format error"}`);
                }
            );
        } catch (err) {
            if (err.name !== 'AbortError') {
                console.error("[VanityBooth3D] Fetch error:", err);
                showLoading(false);
                showError(`Network error loading model: ${err.message}`);
            }
        }
    }

    function setupAnimations(animations) {
        currentActions = {};
        const select = document.getElementById("vanityAnimSelect");
        if (select) {
            select.innerHTML = "";
        }

        if (!animations || animations.length === 0) {
            if (select) {
                const opt = document.createElement("option");
                opt.text = "Static Pose (No Anim)";
                select.appendChild(opt);
                select.disabled = true;
            }
            return;
        }

        mixer = new THREE.AnimationMixer(currentModel);

        if (select) {
            const staticOpt = document.createElement("option");
            staticOpt.value = "__static__";
            staticOpt.text = "Rest Pose (Static)";
            select.appendChild(staticOpt);
        }

        animations.forEach((clip, index) => {
            const action = mixer.clipAction(clip);
            const animName = clip.name || `Animation ${index + 1}`;
            currentActions[animName] = action;

            if (select) {
                const opt = document.createElement("option");
                opt.value = animName;
                opt.text = formatAnimName(animName);
                select.appendChild(opt);
            }
        });

        if (select) {
            select.disabled = false;
            // Default to 'idle' or first animation
            const defaultAnim = Object.keys(currentActions).find(k => k.toLowerCase().includes("idle")) || Object.keys(currentActions)[0] || "__static__";
            select.value = defaultAnim;
            playAnimation(defaultAnim);
        }
    }

    function playAnimation(name) {
        if (name === "__static__") {
            if (activeAction) {
                activeAction.stop();
                activeAction = null;
            }
            if (mixer) {
                mixer.stopAllAction();
            }
            if (currentModel) {
                currentModel.traverse((node) => {
                    if (node.isMesh && node.morphTargetInfluences) {
                        for (let i = 0; i < node.morphTargetInfluences.length; i++) {
                            node.morphTargetInfluences[i] = 0;
                        }
                    }
                });
            }
            return;
        }

        const newAction = currentActions[name];
        if (!newAction) return;

        if (activeAction && activeAction !== newAction) {
            activeAction.stop();
        }

        newAction.reset();
        newAction.play();
        activeAction = newAction;
        playHeroSound(name);
    }

    function playHeroSound(animName) {
        if (isSoundMuted || !currentHero || animName === "__static__") return;
        try {
            if (currentAudio) {
                currentAudio.pause();
                currentAudio.currentTime = 0;
            }
            const cleanAnim = animName.toLowerCase();
            const soundUrl = `/api/v1/assets/sound?hero=${encodeURIComponent(currentHero)}&alt=${encodeURIComponent(currentAlt || "")}&action=${encodeURIComponent(cleanAnim)}&_t=${Date.now()}`;
            currentAudio = new Audio(soundUrl);
            currentAudio.volume = 0.7;
            currentAudio.loop = true;
            currentAudio.playbackRate = animSpeed;
            currentAudio.play().catch(() => {
                // Ignore audio play policy or missing sound
            });
        } catch (e) {
            // Ignore audio exception
        }
    }

    function formatAnimName(name) {
        return name
            .replace(/_/g, " ")
            .replace(/\b\w/g, c => c.toUpperCase());
    }

    function clearCurrentModel() {
        if (vanityFetchController) {
            vanityFetchController.abort();
            vanityFetchController = null;
        }
        if (currentAudio) {
            currentAudio.pause();
            currentAudio = null;
        }
        if (activeAction) {
            activeAction.stop();
            activeAction = null;
        }
        if (modelGroup) {
            console.debug("[VanityBooth3D] [GPU-DISPOSE] Disposing modelGroup GPU resources");
            KongorViewerCore.disposeObject3DResources(modelGroup, renderer, mixer);
            modelGroup.clear();
        } else if (currentModel) {
            console.debug("[VanityBooth3D] [GPU-DISPOSE] Disposing currentModel GPU resources");
            KongorViewerCore.disposeObject3DResources(currentModel, renderer, mixer);
        }
        if (mixer) {
            mixer.stopAllAction();
            mixer = null;
        }
        if (currentModel) {
            if (scene) scene.remove(currentModel);
            currentModel = null;
        }
        currentActions = {};
    }

    function showLoading(show, msg) {
        const spinner = document.getElementById("vanityLoading");
        const msgEl = document.getElementById("vanityLoadingMsg");
        if (spinner) {
            spinner.style.display = show ? "flex" : "none";
        }
        if (msgEl && msg) {
            msgEl.innerText = msg;
        }
    }

    function updateLoadingProgress(txt) {
        const sub = document.getElementById("vanityLoadingSub");
        if (sub) {
            sub.innerText = txt;
        }
    }

    function showError(msg) {
        const vp = document.getElementById("vanityViewport");
        if (currentModel && vp && window.KongorViewerCore && KongorViewerCore.renderZeroBlankErrorBanner) {
            KongorViewerCore.renderZeroBlankErrorBanner(vp, msg);
            return;
        }
        const errEl = document.getElementById("vanityError");
        if (errEl) {
            errEl.innerText = msg;
            errEl.style.display = "block";
        }
    }

    function hideError() {
        const errEl = document.getElementById("vanityError");
        if (errEl) {
            errEl.style.display = "none";
        }
    }

    // Public API attached to window
    function bindModalLifecycle(modal) {
        if (!modal || modal.dataset.kongorDialogBound === "1") return;
        modal.dataset.kongorDialogBound = "1";

        modal.addEventListener("close", () => {
            modal.returnValue = "";
            console.debug("[VanityBooth3D] Dialog closed; releasing active 3D model, fetch stream, and audio.");
            clearCurrentModel();
        });

        // Progressive-enhancement light-dismiss fallback where native <dialog closedby="any"> is unsupported (e.g. Safari)
        if (typeof HTMLDialogElement !== "undefined" && !("closedBy" in HTMLDialogElement.prototype)) {
            const isOutsideDialog = (e) => {
                const r = modal.getBoundingClientRect();
                return e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom;
            };
            let pressedOutside = false;
            modal.addEventListener("pointerdown", (e) => {
                pressedOutside = e.target === modal && isOutsideDialog(e);
            });
            modal.addEventListener("pointerup", (e) => {
                if (pressedOutside && e.target === modal && isOutsideDialog(e) && modal.open) {
                    modal.close();
                }
                pressedOutside = false;
            });
        }
    }

    function openModalElement(modal) {
        if (!modal) return;
        bindModalLifecycle(modal);
        if (typeof modal.showModal === "function") {
            if (!modal.open) {
                modal.showModal();
            }
        } else {
            modal.setAttribute("open", "");
            modal.style.display = "block";
        }
    }

    window.VanityBooth3D = {
        openHero: async function (heroName, altAvatar, title, resourcePath) {
            await ensureThreeLoaded();
            currentHero = heroName;
            currentAlt = altAvatar || "";
            const modal = document.getElementById("vanityBoothModal");
            const container = document.getElementById("vanityViewport");
            const titleEl = document.getElementById("vanityTitle");

            openModalElement(modal);
            if (titleEl) titleEl.innerText = title || `${heroName} ${altAvatar ? `(${altAvatar})` : ""}`;

            if (!isInitialized) {
                initViewport(container);
            }

            const cleanHero = heroName.replace("Hero_", "").toLowerCase();
            const altParam = altAvatar ? `/${encodeURIComponent(altAvatar)}` : "";
            const queryParams = resourcePath ? `resource=${encodeURIComponent(resourcePath)}` : "";
            const queryString = queryParams ? `?${queryParams}` : "";
            const url = `/api/v1/assets/3d/hero/${encodeURIComponent(cleanHero)}${altParam}${queryString}`;

            loadModelUrl(url, title || heroName);
        },

        openModel: async function (mdfPath, title) {
            await ensureThreeLoaded();
            currentHero = "";
            currentAlt = "";
            const modal = document.getElementById("vanityBoothModal");
            const container = document.getElementById("vanityViewport");
            const titleEl = document.getElementById("vanityTitle");

            openModalElement(modal);
            if (titleEl) titleEl.innerText = title || mdfPath;

            if (!isInitialized) {
                initViewport(container);
            }

            const url = `/api/v1/assets/3d/model?path=${encodeURIComponent(mdfPath)}`;
            loadModelUrl(url, title || mdfPath);
        },

        close: function () {
            const modal = document.getElementById("vanityBoothModal");
            if (modal && typeof modal.close === "function" && modal.open) {
                modal.close();
                return;
            }
            if (modal) {
                modal.removeAttribute("open");
                modal.style.display = "";
            }
            clearCurrentModel();
        },

        setAnimation: function (name) {
            playAnimation(name);
        },

        toggleSound: function () {
            isSoundMuted = !isSoundMuted;
            const btn = document.getElementById("vanitySoundBtn");
            if (btn) {
                btn.innerHTML = isSoundMuted ? '<i class="fas fa-volume-mute text-rose-400"></i> SFX: Muted' : '<i class="fas fa-volume-up text-emerald-400"></i> SFX: On';
            }
            if (isSoundMuted && currentAudio) {
                currentAudio.pause();
            }
        },

        togglePlayPause: function () {
            isPaused = !isPaused;
            const btn = document.getElementById("vanityPlayPauseBtn");
            if (btn) {
                btn.innerHTML = isPaused ? '<i class="fas fa-play"></i> Play' : '<i class="fas fa-pause"></i> Pause';
            }
        },

        toggleAutoRotate: function () {
            isAutoRotating = !isAutoRotating;
            const btn = document.getElementById("vanityRotateBtn");
            if (btn) {
                btn.classList.toggle("bg-amber-500", isAutoRotating);
                btn.classList.toggle("bg-slate-700", !isAutoRotating);
            }
        },

        setSpeed: function (speed) {
            animSpeed = parseFloat(speed) || 1.0;
            const label = document.getElementById("vanitySpeedLabel");
            if (label) label.innerText = `${animSpeed.toFixed(2)}x`;
            if (currentAudio) {
                currentAudio.playbackRate = animSpeed;
            }
        },

        resetCamera: function () {
            if (controls && camera) {
                if (currentModel && KongorViewerCore.resetToBounds) {
                    KongorViewerCore.resetToBounds(camera, controls, currentModel, 0.9);
                } else {
                    camera.position.set(0, 80, 220);
                    controls.target.set(0, 50, 0);
                    controls.update();
                }
                if (vanityWakeScheduler) vanityWakeScheduler.requestRender();
            }
        },

        captureTransparentPng: function () {
            if (!renderer || !scene || !camera) return null;
            return KongorViewerCore.captureMinimalTransparentPng({
                renderer: renderer,
                scene: scene,
                camera: camera,
                controls: controls,
                mixer: mixer,
                activeAction: activeAction,
                model: currentModel,
                hero: currentHero,
                alt: currentAlt,
                helpersToHide: [vanityGridGroup],
                requestRender: () => vanityWakeScheduler && vanityWakeScheduler.requestRender()
            });
        },

        recordTurntableWebm: function (durationSec) {
            return KongorViewerCore.recordWebmShowcase({
                renderer: renderer,
                camera: camera,
                controls: controls,
                activeAction: activeAction,
                hero: currentHero || "vanity_hero",
                requestRender: () => vanityWakeScheduler && vanityWakeScheduler.requestRender()
            }, { mode: "turntable", durationSec: durationSec || 4.0 });
        },

        setLightingPreset: function (presetKey) {
            if (!vanityStudioLights) return;
            KongorViewerCore.applyStudioLightingPreset(vanityStudioLights, presetKey);
            if (vanityWakeScheduler) vanityWakeScheduler.requestRender();
        },

        toggleUvChecker: function (enabled) {
            if (!currentModel) return;
            KongorViewerCore.applyDiagnosticShaderMode(currentModel, enabled ? "UV" : "Lit");
            if (vanityWakeScheduler) vanityWakeScheduler.requestRender();
        },

        exportViewerState: function () {
            return KongorViewerCore.serializeViewerState({
                renderer: renderer,
                camera: camera,
                controls: controls,
                activeAction: activeAction,
                model: currentModel,
                hero: currentHero,
                alt: currentAlt
            });
        }
    };
})();

