// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * DAWNBRINGER Web Portal - 3D Main Hero Banner Avatar Engine
 * Isolated, transparent 3D character viewport in the welcome banner.
 * Cycles 10s IDLE -> 10s WALKING (20s total per avatar).
 * Every 20s, randomly transitions between main hero alt avatars using 5 dynamic cinematic effects:
 * 1. "smoke_cloud" - Explosive anime/ninja smoke & magical sparkle puff.
 * 2. "portal_warp" - Cosmic dimensional portal vortex spin & bounce-warp summon.
 * 3. "lightning_strike" - Shockwave thunderbolt strike with superhero ground-pound impact.
 * 4. "infernal_burst" - Hellfire combustion explosion with rising flame embers.
 * 5. "glacial_shatter" - Frost crystal freeze & diamond ice shard shatter bloom.
 *
 * Interactive Controls:
 * - Click & Drag on avatar: 360° free horizontal yaw rotation with momentum decay.
 * - Mouse movement (idle): Avatar actively turns and looks directly towards the user's cursor across the screen.
 */
(() => {
    'use strict';

    document.addEventListener('DOMContentLoaded', async () => {
        const container = document.getElementById('banner-hero-3d-container');
        const canvas = document.getElementById('banner-hero-3d-canvas');
        if (!container || !canvas) return;

        console.log('[Banner3D] Initializing Banner 3D Avatar Engine...');

        const MODEL_BASE_Y = -18; // Ground level baseline offset to sit lower in banner card

        let avatarList = [];
        try {
            const rawAvatars = container.getAttribute('data-avatars');
            if (rawAvatars) {
                const parsed = JSON.parse(rawAvatars);
                if (Array.isArray(parsed)) {
                    avatarList = parsed.map(av => {
                        const url = av.glbUrl || av.GlbUrl || av.url || av.Url || (typeof av === 'string' ? av : '');
                        const name = av.name || av.Name || 'Main Hero';
                        return {name, glbUrl: url};
                    }).filter(av => Boolean(av.glbUrl));
                }
            }
        } catch (e) {
            console.warn('[Banner3D] Failed to parse data-avatars attribute', e);
        }

        const fallbackModelUrl = container.getAttribute('data-model-url') || '/api/v1/assets/3d/hero/bubbles';
        const fallbackName = container.getAttribute('data-avatar-name') || 'Main Hero';

        if (avatarList.length === 0) {
            avatarList = [{
                name: fallbackName,
                glbUrl: fallbackModelUrl
            }];
        }

        console.log(`[Banner3D] Loaded ${avatarList.length} avatar(s) for rotation:`, avatarList);

        async function ensureThreeLoaded() {
            if (window.THREE && window.THREE.GLTFLoader) {
                return;
            }
            if (window.KongorViewerCore && window.KongorViewerCore.ensureLocalThreeLoaded) {
                await window.KongorViewerCore.ensureLocalThreeLoaded();
                return;
            }
            await loadScript("/lib/three/three.r16x.bundle.js");
        }

        function loadScript(src) {
            return new Promise((resolve, reject) => {
                const script = document.createElement("script");
                script.src = src;
                script.onload = resolve;
                script.onerror = (err) => reject(err);
                document.head.appendChild(script);
            });
        }

        try {
            await ensureThreeLoaded();
        } catch (e) {
            console.error('[Banner3D] Failed to load Three.js dependencies', e);
            return;
        }

        const width = container.clientWidth || 340;
        const height = container.clientHeight || 280;

        // Scene (100% transparent, no borders, no pedestals)
        const scene = new THREE.Scene();
        scene.background = null;

        // Camera - positioned to bring the character lower in the card frame
        const camera = new THREE.PerspectiveCamera(40, width / height, 0.1, 1000);
        camera.position.set(0, 36, 190);
        camera.lookAt(0, 32, 0);

        // Renderer with ACESFilmic tone mapping & high-performance GPU pipeline
        const renderer = new THREE.WebGLRenderer({
            canvas: canvas,
            antialias: true,
            alpha: true,
            powerPreference: 'high-performance'
        });
        renderer.setSize(width, height, false);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        if (window.KongorViewerCore) {
            window.KongorViewerCore.configureRendererColorSpace(renderer);
        } else if ('outputColorSpace' in renderer && THREE.SRGBColorSpace) {
            renderer.outputColorSpace = THREE.SRGBColorSpace;
        }
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.25;

        // Cinematic Studio Lighting Rig (Key, Fill, Rim & Ambient)
        const ambientLight = new THREE.AmbientLight(0xffffff, 1.4);
        scene.add(ambientLight);

        const keyLight = new THREE.DirectionalLight(0xfffaed, 2.4);
        keyLight.position.set(50, 90, 70);
        scene.add(keyLight);

        const fillLight = new THREE.DirectionalLight(0xa855f7, 1.6); // Atmospheric violet fill
        fillLight.position.set(-65, 45, 45);
        scene.add(fillLight);

        const rimLight = new THREE.DirectionalLight(0xfbbf24, 2.8); // Brilliant gold rim sheen
        rimLight.position.set(0, 80, -85);
        scene.add(rimLight);

        // Ground Contact Shadow Projection
        const shadowCanvas = document.createElement('canvas');
        shadowCanvas.width = 128;
        shadowCanvas.height = 128;
        const shadowCtx = shadowCanvas.getContext('2d');
        if (shadowCtx) {
            const gradient = shadowCtx.createRadialGradient(64, 64, 0, 64, 64, 64);
            gradient.addColorStop(0, 'rgba(0, 0, 0, 0.7)');
            gradient.addColorStop(0.45, 'rgba(0, 0, 0, 0.3)');
            gradient.addColorStop(1, 'rgba(0, 0, 0, 0)');
            shadowCtx.fillStyle = gradient;
            shadowCtx.fillRect(0, 0, 128, 128);

            const shadowTexture = new THREE.CanvasTexture(shadowCanvas);
            const shadowGeo = new THREE.PlaneGeometry(65, 65);
            const shadowMat = new THREE.MeshBasicMaterial({
                map: shadowTexture,
                transparent: true,
                opacity: 0.85,
                depthWrite: false
            });
            const shadowMesh = new THREE.Mesh(shadowGeo, shadowMat);
            shadowMesh.rotation.x = -Math.PI / 2;
            shadowMesh.position.y = MODEL_BASE_Y + 0.3;
            scene.add(shadowMesh);
        }

        const clock = new THREE.Clock();
        const modelBufferCache = new Map(); // url -> Promise<ArrayBuffer>

        let currentAvatarIndex = 0;
        let currentInstance = null; // { model, mixer, idleAction, walkAction }
        let isVisible = true;
        let isUserPaused = Boolean(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
        let animFrameId = null;
        let cachedContainerRect = container.getBoundingClientRect();
        function refreshCachedContainerRect() {
            cachedContainerRect = container.getBoundingClientRect();
        }
        if ('ResizeObserver' in window) {
            new ResizeObserver(refreshCachedContainerRect).observe(container);
        }
        window.addEventListener('scroll', refreshCachedContainerRect, { passive: true });
        let isTransitioning = false;

        // --- Interactive Spin & Look-At Tracking State ---
        let baseRotationY = -0.35; // Baseline orientation angled slightly towards text
        let currentRotationY = -0.35;
        let currentRotationX = 0;
        let targetLookYaw = 0;
        let targetLookPitch = 0;
        let isDragging = false;
        let previousPointerX = 0;
        let dragVelocityX = 0;

        // Mouse & Touch Drag-to-Spin 360 Handlers
        function onPointerDown(e) {
            isDragging = true;
            dragVelocityX = 0;
            previousPointerX = e.clientX !== undefined ? e.clientX : (e.touches && e.touches[0] ? e.touches[0].clientX : 0);
            container.style.cursor = 'grabbing';
            canvas.style.cursor = 'grabbing';
        }

        function onPointerMove(e) {
            const clientX = e.clientX !== undefined ? e.clientX : (e.touches && e.touches[0] ? e.touches[0].clientX : null);
            const clientY = e.clientY !== undefined ? e.clientY : (e.touches && e.touches[0] ? e.touches[0].clientY : null);

            if (clientX === null || clientY === null) return;

            if (isDragging) {
                const deltaX = clientX - previousPointerX;
                previousPointerX = clientX;
                baseRotationY += deltaX * 0.018; // Free 360 drag rotation
                dragVelocityX = deltaX * 0.018;
            } else {
                // Active cursor follower across the entire window
                const rect = cachedContainerRect;
                const containerCenterX = rect.left + rect.width / 2;
                const containerCenterY = rect.top + rect.height / 2;

                const dx = clientX - containerCenterX;
                const dy = clientY - containerCenterY;

                // Dynamic angular tracking towards mouse
                const angleX = Math.atan2(dx, Math.max(140, Math.abs(dy) * 0.4));
                targetLookYaw = angleX * 1.2;

                const angleY = Math.atan2(-dy, 320);
                targetLookPitch = Math.max(-0.25, Math.min(0.25, angleY));
            }
        }

        function onPointerUp() {
            if (isDragging) {
                isDragging = false;
                container.style.cursor = 'grab';
                canvas.style.cursor = 'grab';
            }
        }

        canvas.addEventListener('mousedown', onPointerDown);
        canvas.addEventListener('touchstart', onPointerDown, {passive: true});

        window.addEventListener('mousemove', onPointerMove);
        window.addEventListener('touchmove', onPointerMove, {passive: true});

        window.addEventListener('mouseup', onPointerUp);
        window.addEventListener('touchend', onPointerUp);

        // Robust Fetch + Parse GLTF helper
        async function fetchModelBuffer(url) {
            if (modelBufferCache.has(url)) {
                return modelBufferCache.get(url);
            }

            const fetchPromise = (async () => {
                console.log(`[Banner3D] Fetching 3D asset from ${url}...`);
                const resp = await fetch(url);
                if (!resp.ok) {
                    throw new Error(`HTTP ${resp.status}`);
                }
                return await resp.arrayBuffer();
            })();

            modelBufferCache.set(url, fetchPromise);
            return fetchPromise;
        }

        async function parseGltfFromBuffer(arrayBuffer) {
            return new Promise((resolve, reject) => {
                const loader = new THREE.GLTFLoader();
                loader.parse(
                    arrayBuffer.slice(0),
                    "",
                    (gltf) => {
                        resolve(gltf);
                    },
                    (err) => {
                        reject(err);
                    }
                );
            });
        }

        async function loadAvatarGltf(url) {
            try {
                const buffer = await fetchModelBuffer(url);
                return await parseGltfFromBuffer(buffer);
            } catch (err) {
                console.warn(`[Banner3D] Asset not available at ${url} (${err.message}). Removing from pool.`);
                avatarList = avatarList.filter(a => a.glbUrl !== url);
                return null;
            }
        }

        // Preload other avatars in the background
        avatarList.forEach((av) => {
            if (av.glbUrl) {
                fetchModelBuffer(av.glbUrl).catch(() => {
                });
            }
        });

        function setupAvatarInstance(gltf) {
            const model = gltf.scene.clone(true);
            const mixer = new THREE.AnimationMixer(model);

            model.position.set(0, MODEL_BASE_Y, 0); // Positioned lower for natural ground baseline
            model.rotation.y = baseRotationY;

            model.traverse((child) => {
                if (child.isMesh) {
                    child.castShadow = true;
                    child.receiveShadow = true;
                    if (child.material) {
                        child.material.side = THREE.DoubleSide;
                        child.material.metalness = 0.0;
                        child.material.roughness = 0.85;
                        if (child.material.map) {
                            if (window.KongorViewerCore) {
                                window.KongorViewerCore.configureTextureColorSpace(child.material.map);
                            } else if ('colorSpace' in child.material.map && THREE.SRGBColorSpace) {
                                child.material.map.colorSpace = THREE.SRGBColorSpace;
                            }
                            child.material.map.needsUpdate = true;
                        }
                        child.material.needsUpdate = true;
                    }
                }
            });

            const clips = gltf.animations || [];
            const idleClip = clips.find(c => /idle/i.test(c.name)) ||
                clips.find(c => /bored/i.test(c.name)) ||
                clips[0];

            const walkClip = clips.find(c => /walk/i.test(c.name)) ||
                clips.find(c => /run/i.test(c.name)) ||
                clips.find(c => /move/i.test(c.name)) ||
                (clips.length > 1 ? clips[1] : idleClip);

            let idleAction = null;
            let walkAction = null;

            if (idleClip) {
                idleAction = mixer.clipAction(idleClip, model);
                idleAction.setEffectiveWeight(1.0);
            }
            if (walkClip && walkClip !== idleClip) {
                walkAction = mixer.clipAction(walkClip, model);
                walkAction.setEffectiveWeight(1.0);
            }

            return {
                model,
                mixer,
                idleAction,
                walkAction
            };
        }

        // Particle Spawner Helper for Smoke / Portal / Spark / Lightning / Fire / Ice Effects
        function spawnTransitionParticles(type) {
            let count = 24;
            let glyphs = ['✨', '🌟', '✦'];

            if (type === 'smoke') {
                count = 26;
                glyphs = ['💨', '☁️', '💨', '✨', '⚡', '✦'];
            } else if (type === 'portal') {
                count = 20;
                glyphs = ['🌀', '✨', '💫', '⭐', '🌟', '🔮', '✦'];
            } else if (type === 'lightning') {
                count = 28;
                glyphs = ['⚡', '⚡', '💥', '✨', '🌟', '⚡', '⚡'];
            } else if (type === 'inferno') {
                count = 30;
                glyphs = ['🔥', '💥', '☄️', '🔥', '✨', '💥', '🌋'];
            } else if (type === 'glacial') {
                count = 26;
                glyphs = ['❄️', '💎', '🧊', '✨', '💠', '❄️', '✦'];
            }

            for (let i = 0; i < count; i++) {
                const p = document.createElement('div');
                p.className = 'banner-3d-particle';
                p.textContent = glyphs[Math.floor(Math.random() * glyphs.length)];

                const angle = Math.random() * Math.PI * 2;
                const distance = 40 + Math.random() * 95;
                const tx = Math.cos(angle) * distance;
                const ty = Math.sin(angle) * (distance * 0.7) - (type === 'smoke' || type === 'inferno' ? 30 : 0);
                const scale = 0.8 + Math.random() * 1.5;
                const rot = (Math.random() - 0.5) * 360;
                const duration = 0.7 + Math.random() * 0.6;
                const delay = Math.random() * 0.15;

                p.style.left = '50%';
                p.style.top = '52%';
                p.style.fontSize = `${14 + Math.random() * 20}px`;
                p.style.setProperty('--px', `${tx}px`);
                p.style.setProperty('--py', `${ty}px`);
                p.style.setProperty('--pscale', `${scale}`);
                p.style.setProperty('--prot', `${rot}deg`);
                p.style.animationDuration = `${duration}s`;
                p.style.animationDelay = `${delay}s`;

                container.appendChild(p);

                setTimeout(() => {
                    if (p.parentNode) {
                        p.parentNode.removeChild(p);
                    }
                }, (duration + delay + 0.1) * 1000);
            }
        }

        // Run 20s Cycle on an Instance: 10s Idle -> 10s Walk -> Trigger Next Avatar
        function startAvatarCycle(instance) {
            console.log('[Banner3D] Starting avatar cycle: 10s IDLE');
            if (instance.idleAction) {
                instance.idleAction.reset().play();
            } else if (instance.walkAction) {
                instance.walkAction.reset().play();
            }

            // Step 1: After 10s, cross-fade Idle -> Walk
            const toWalkTimeout = setTimeout(() => {
                if (isTransitioning) return;
                console.log('[Banner3D] Transitioning to 10s WALKING');
                if (instance.idleAction && instance.walkAction && instance.idleAction !== instance.walkAction) {
                    instance.walkAction.reset();
                    instance.walkAction.setEffectiveTimeScale(1.0);
                    instance.walkAction.setEffectiveWeight(1.0);
                    instance.walkAction.crossFadeFrom(instance.idleAction, 0.6, true);
                    instance.walkAction.play();
                }
            }, 10000);

            // Step 2: After 20s total, trigger transition to next alt avatar!
            const toNextTimeout = setTimeout(async () => {
                if (isTransitioning) return;
                await transitionToNextAvatar();
            }, 20000);

            instance.cycleTimeouts = [toWalkTimeout, toNextTimeout];
        }

        function clearAvatarCycle(instance) {
            if (instance && instance.cycleTimeouts) {
                instance.cycleTimeouts.forEach(t => clearTimeout(t));
                instance.cycleTimeouts = [];
            }
        }

        // Transition selector (Randomly picks among the 5 explosive cinematic effects)
        async function transitionToNextAvatar() {
            if (isTransitioning) return;
            if (avatarList.length <= 1) {
                // If only 1 avatar exists, just loop Walk -> Idle
                if (currentInstance && currentInstance.idleAction && currentInstance.walkAction) {
                    currentInstance.idleAction.reset();
                    currentInstance.idleAction.setEffectiveTimeScale(1.0);
                    currentInstance.idleAction.setEffectiveWeight(1.0);
                    currentInstance.idleAction.crossFadeFrom(currentInstance.walkAction, 0.6, true);
                    currentInstance.idleAction.play();
                    startAvatarCycle(currentInstance);
                }
                return;
            }

            isTransitioning = true;
            clearAvatarCycle(currentInstance);

            let nextGltf = null;
            let attempts = 0;

            while (!nextGltf && avatarList.length > 0 && attempts < 5) {
                attempts++;
                let nextIndex = Math.floor(Math.random() * (avatarList.length - 1));
                if (nextIndex >= currentAvatarIndex) {
                    nextIndex++;
                }
                currentAvatarIndex = nextIndex % avatarList.length;
                const nextAvatarData = avatarList[currentAvatarIndex];
                console.log(`[Banner3D] Attempting switch to avatar [${currentAvatarIndex + 1}/${avatarList.length}]: ${nextAvatarData.name} (${nextAvatarData.glbUrl})`);

                nextGltf = await loadAvatarGltf(nextAvatarData.glbUrl);
            }

            if (!nextGltf) {
                console.warn('[Banner3D] Could not load next alt avatar. Retaining current instance.');
                isTransitioning = false;
                if (currentInstance) startAvatarCycle(currentInstance);
                return;
            }

            const nextInstance = setupAvatarInstance(nextGltf);

            // Pick randomly from the 5 cinematic transitions
            const styles = ['smoke_cloud', 'portal_warp', 'lightning_strike', 'infernal_burst', 'glacial_shatter'];
            const chosenStyle = styles[Math.floor(Math.random() * styles.length)];
            console.log(`[Banner3D] Triggering transition effect: "${chosenStyle}"`);

            if (chosenStyle === 'smoke_cloud') {
                await executeSmokeTransition(currentInstance, nextInstance);
            } else if (chosenStyle === 'portal_warp') {
                await executePortalWarpTransition(currentInstance, nextInstance);
            } else if (chosenStyle === 'lightning_strike') {
                await executeLightningTransition(currentInstance, nextInstance);
            } else if (chosenStyle === 'infernal_burst') {
                await executeInfernoTransition(currentInstance, nextInstance);
            } else {
                await executeGlacialTransition(currentInstance, nextInstance);
            }

            currentInstance = nextInstance;
            isTransitioning = false;
            startAvatarCycle(currentInstance);
        }

        // Transition 1: Ninja Smoke Poof (Smoke Bomb Eruption)
        function executeSmokeTransition(oldInst, newInst) {
            return new Promise((resolve) => {
                spawnTransitionParticles('smoke');

                // Swap models at peak smoke density (0.25s)
                setTimeout(() => {
                    if (oldInst && oldInst.model.parent) {
                        scene.remove(oldInst.model);
                    }

                    scene.add(newInst.model);

                    // Quick settle bounce
                    newInst.model.scale.set(0.7, 1.25, 0.7);
                    const startTime = performance.now();

                    function settle(now) {
                        const elapsed = (now - startTime) / 400;
                        if (elapsed < 1.0) {
                            const factor = 1.0 + Math.sin(elapsed * Math.PI) * 0.15;
                            newInst.model.scale.set(factor, 2.0 - factor, factor);
                            requestAnimationFrame(settle);
                        } else {
                            newInst.model.scale.set(1, 1, 1);
                            resolve();
                        }
                    }

                    requestAnimationFrame(settle);
                }, 250);
            });
        }

        // Transition 2: Cosmic Portal Vortex Warp (Spin & Bounce-Warp)
        function executePortalWarpTransition(oldInst, newInst) {
            return new Promise((resolve) => {
                spawnTransitionParticles('portal');

                const warpDuration = 550;
                const startTime = performance.now();

                // Spin and shrink old model into portal
                function warpOut(now) {
                    const p = Math.min((now - startTime) / warpDuration, 1.0);
                    const scale = 1.0 - p;

                    if (oldInst) {
                        oldInst.model.scale.set(scale, scale * (1 + p), scale);
                        oldInst.model.rotation.y += 0.25;
                    }

                    if (p < 1.0) {
                        requestAnimationFrame(warpOut);
                    } else {
                        if (oldInst && oldInst.model.parent) {
                            scene.remove(oldInst.model);
                        }

                        // Spawn new model emerging out of portal
                        scene.add(newInst.model);
                        spawnTransitionParticles('portal');

                        const inStartTime = performance.now();

                        function warpIn(inNow) {
                            const inp = Math.min((inNow - inStartTime) / warpDuration, 1.0);
                            const scaleIn = Math.sin(inp * Math.PI * 0.5) * 1.0;
                            newInst.model.scale.set(scaleIn, scaleIn, scaleIn);
                            newInst.model.rotation.y -= 0.15 * (1 - inp);

                            if (inp < 1.0) {
                                requestAnimationFrame(warpIn);
                            } else {
                                newInst.model.scale.set(1, 1, 1);
                                newInst.model.rotation.y = baseRotationY;
                                resolve();
                            }
                        }

                        requestAnimationFrame(warpIn);
                    }
                }

                requestAnimationFrame(warpOut);
            });
        }

        // Transition 3: Thunderbolt Strike & Superhero Impact Ground-Pound
        function executeLightningTransition(oldInst, newInst) {
            return new Promise((resolve) => {
                spawnTransitionParticles('lightning');

                // Quick flash
                if (oldInst) {
                    oldInst.model.position.y += 5;
                }

                setTimeout(() => {
                    if (oldInst && oldInst.model.parent) {
                        scene.remove(oldInst.model);
                    }

                    scene.add(newInst.model);
                    spawnTransitionParticles('lightning');

                    // Descend with impactful landing
                    const startTime = performance.now();
                    const duration = 350;

                    function impact(now) {
                        const elapsed = Math.min((now - startTime) / duration, 1.0);
                        if (elapsed < 0.6) {
                            // Falling down rapidly
                            const p = elapsed / 0.6;
                            newInst.model.position.y = MODEL_BASE_Y + (1 - p) * 60;
                            newInst.model.scale.set(0.9, 1.2, 0.9);
                            requestAnimationFrame(impact);
                        } else {
                            // Impact ground pound squash & rebound
                            const bounceElapsed = (elapsed - 0.6) / 0.4;
                            const squash = Math.sin(bounceElapsed * Math.PI);
                            newInst.model.position.y = MODEL_BASE_Y - squash * 4;
                            newInst.model.scale.set(1.0 + squash * 0.35, 1.0 - squash * 0.3, 1.0 + squash * 0.35);

                            if (bounceElapsed < 1.0) {
                                requestAnimationFrame(impact);
                            } else {
                                newInst.model.position.set(0, MODEL_BASE_Y, 0);
                                newInst.model.scale.set(1, 1, 1);
                                resolve();
                            }
                        }
                    }

                    requestAnimationFrame(impact);
                }, 140);
            });
        }

        // Transition 4: Infernal Combustion / Hellfire Burst
        function executeInfernoTransition(oldInst, newInst) {
            return new Promise((resolve) => {
                spawnTransitionParticles('inferno');

                const startTime = performance.now();
                const burnDuration = 280;

                function burnOut(now) {
                    const p = Math.min((now - startTime) / burnDuration, 1.0);
                    if (oldInst) {
                        oldInst.model.position.x = (Math.random() - 0.5) * 4; // Fiery tremor
                        oldInst.model.scale.set(1.0 + p * 0.3, 1.0 - p * 0.8, 1.0 + p * 0.3);
                    }

                    if (p < 1.0) {
                        requestAnimationFrame(burnOut);
                    } else {
                        if (oldInst && oldInst.model.parent) {
                            scene.remove(oldInst.model);
                        }

                        scene.add(newInst.model);
                        spawnTransitionParticles('inferno');

                        // Emerge from fire flames
                        const inStart = performance.now();

                        function burstIn(inNow) {
                            const inp = Math.min((inNow - inStart) / 400, 1.0);
                            const scale = Math.sin(inp * Math.PI * 0.5) * 1.0;
                            newInst.model.scale.set(scale, scale, scale);
                            newInst.model.position.y = MODEL_BASE_Y - (1 - inp) * 20;

                            if (inp < 1.0) {
                                requestAnimationFrame(burstIn);
                            } else {
                                newInst.model.position.set(0, MODEL_BASE_Y, 0);
                                newInst.model.scale.set(1, 1, 1);
                                resolve();
                            }
                        }

                        requestAnimationFrame(burstIn);
                    }
                }

                requestAnimationFrame(burnOut);
            });
        }

        // Transition 5: Glacial Ice Crystal Shatter & Bloom
        function executeGlacialTransition(oldInst, newInst) {
            return new Promise((resolve) => {
                spawnTransitionParticles('glacial');

                setTimeout(() => {
                    if (oldInst && oldInst.model.parent) {
                        scene.remove(oldInst.model);
                    }

                    scene.add(newInst.model);
                    spawnTransitionParticles('glacial');

                    // Diamond bloom materialization
                    const inStartTime = performance.now();

                    function bloomIn(now) {
                        const elapsed = Math.min((now - inStartTime) / 450, 1.0);
                        const scale = Math.sin(elapsed * Math.PI * 0.5);
                        newInst.model.scale.set(scale, scale, scale);
                        newInst.model.rotation.y = baseRotationY + (1 - elapsed) * 0.8;

                        if (elapsed < 1.0) {
                            requestAnimationFrame(bloomIn);
                        } else {
                            newInst.model.scale.set(1, 1, 1);
                            newInst.model.rotation.y = baseRotationY;
                            resolve();
                        }
                    }

                    requestAnimationFrame(bloomIn);
                }, 200);
            });
        }

        // Responsive Resize
        function onResize() {
            if (!container || !renderer || !camera) return;
            const w = container.clientWidth || 340;
            const h = container.clientHeight || 280;
            if (w === 0 || h === 0) return;

            camera.aspect = w / h;
            camera.updateProjectionMatrix();
            renderer.setSize(w, h, false);
        }

        window.addEventListener('resize', onResize);

        // Initialize First Avatar (with fallback progression)
        let initialGltf = null;
        for (let i = 0; i < avatarList.length; i++) {
            const candidate = avatarList[i];
            console.log(`[Banner3D] Attempting initial load: ${candidate.name} (${candidate.glbUrl})`);
            initialGltf = await loadAvatarGltf(candidate.glbUrl);
            if (initialGltf) {
                currentAvatarIndex = i;
                break;
            }
        }

        // Fallback to default if all failed
        if (!initialGltf && fallbackModelUrl) {
            console.log(`[Banner3D] Attempting fallback model: ${fallbackModelUrl}`);
            initialGltf = await loadAvatarGltf(fallbackModelUrl);
        }

        if (initialGltf) {
            currentInstance = setupAvatarInstance(initialGltf);
            scene.add(currentInstance.model);

            startAvatarCycle(currentInstance);
            onResize();
            console.log('[Banner3D] 3D Hero avatar successfully mounted and playing!');
        } else {
            console.warn('[Banner3D] No 3D model could be loaded for the banner.');
        }

        function shouldAnimate() {
            return isVisible && !document.hidden && !isUserPaused;
        }
        function ensureAnimating() {
            if (shouldAnimate() && animFrameId === null) {
                clock.getDelta();
                animFrameId = requestAnimationFrame(animate);
            }
        }
        const pauseBtn = document.getElementById('banner-hero-pause-btn');
        if (pauseBtn) {
            const syncPauseBtn = () => {
                pauseBtn.setAttribute('aria-label', isUserPaused ? 'Resume 3D hero animation' : 'Pause 3D hero animation');
                pauseBtn.textContent = isUserPaused ? '▶ Resume 3D' : '⏸ Pause 3D';
            };
            syncPauseBtn();
            pauseBtn.addEventListener('click', (ev) => {
                ev.stopPropagation();
                isUserPaused = !isUserPaused;
                syncPauseBtn();
                if (isUserPaused) {
                    if (animFrameId !== null) {
                        cancelAnimationFrame(animFrameId);
                        animFrameId = null;
                    }
                } else {
                    ensureAnimating();
                }
            });
        }
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden) ensureAnimating();
        });
        // Render Loop with Active Cursor Tracking + Drag-to-Spin
        function animate() {
            animFrameId = null;
            if (!shouldAnimate()) return;
            animFrameId = requestAnimationFrame(animate);

            if (!isVisible) return;

            const delta = clock.getDelta();
            if (currentInstance && currentInstance.mixer) {
                currentInstance.mixer.update(delta);
            }

            if (currentInstance && currentInstance.model) {
                // Decay drag momentum when released
                if (!isDragging && Math.abs(dragVelocityX) > 0.0001) {
                    baseRotationY += dragVelocityX;
                    dragVelocityX *= 0.92; // Momentum friction
                }

                // Smooth look-at tracking toward mouse
                const targetY = baseRotationY + targetLookYaw;
                const targetX = targetLookPitch;

                currentRotationY += (targetY - currentRotationY) * 0.12;
                currentRotationX += (targetX - currentRotationX) * 0.12;

                currentInstance.model.rotation.y = currentRotationY;
                currentInstance.model.rotation.x = currentRotationX;
            }

            renderer.render(scene, camera);
        }

        // Render initial pose once even when reduced-motion / paused, then start loop if active
        renderer.render(scene, camera);
        ensureAnimating();

        // Pause when out of view
        if ('IntersectionObserver' in window) {
            const observer = new IntersectionObserver((entries) => {
                entries.forEach((entry) => {
                    isVisible = entry.isIntersecting;
                    if (isVisible) { refreshCachedContainerRect(); ensureAnimating(); }
                });
            }, {threshold: 0.05});

            observer.observe(container);
        }
    });
})();
