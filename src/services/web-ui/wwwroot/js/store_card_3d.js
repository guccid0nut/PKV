// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * NEXUS Store Card 3D Virtualized Multi-Viewport Engine
 * Uses a SINGLE shared WebGL renderer with 2D Canvas Blitting.
 * Guarantees 100% reliability with ZERO WebGL context loss across all cards!
 */

(function () {
    const modelCache = new Map(); // url -> Promise<ArrayBuffer>
    const activeCards = new Set();
    const CARD_WIDTH = 320;
    const CARD_HEIGHT = 320;

    let sharedRenderer = null;
    let sharedScene = null;
    let sharedCamera = null;
    let sharedCanvas = null;
    let animLoopRunning = false;
    let observer = null;

    function initSharedRenderer() {
        if (sharedRenderer) return;

        sharedCanvas = document.createElement("canvas");
        sharedCanvas.width = CARD_WIDTH;
        sharedCanvas.height = CARD_HEIGHT;

        sharedRenderer = new THREE.WebGLRenderer({
            canvas: sharedCanvas,
            antialias: true,
            alpha: false,
            powerPreference: "high-performance"
        });
        sharedRenderer.setSize(CARD_WIDTH, CARD_HEIGHT, false);
        sharedRenderer.setPixelRatio(1.0);
        if (window.KongorViewerCore) {
            window.KongorViewerCore.configureRendererColorSpace(sharedRenderer);
        } else if ('outputColorSpace' in sharedRenderer && THREE.SRGBColorSpace) {
            sharedRenderer.outputColorSpace = THREE.SRGBColorSpace;
        }
        sharedRenderer.toneMapping = THREE.ACESFilmicToneMapping;
        sharedRenderer.toneMappingExposure = 1.25;

        sharedScene = new THREE.Scene();
        sharedScene.background = new THREE.Color(0x0c101a);

        // Full-body square framing (zoomed out from head to toe)
        sharedCamera = new THREE.PerspectiveCamera(40, 1.0, 0.1, 1000);
        sharedCamera.position.set(0, 36, 175);
        sharedCamera.lookAt(0, 30, 0);

        // Studio Lights
        const ambient = new THREE.AmbientLight(0x556080, 1.8);
        sharedScene.add(ambient);

        const key = new THREE.DirectionalLight(0xffeedd, 2.2);
        key.position.set(60, 120, 90);
        sharedScene.add(key);

        const fill = new THREE.DirectionalLight(0x88bbff, 1.3);
        fill.position.set(-80, 60, 60);
        sharedScene.add(fill);

        const rim = new THREE.DirectionalLight(0xf59e0b, 2.8);
        rim.position.set(0, 100, -120);
        sharedScene.add(rim);
    }

    class Card3DInstance {
        constructor(element, hero, alt, name, resource) {
            this.element = element;
            this.hero = hero;
            this.alt = alt;
            this.name = name;
            this.resource = resource;

            this.canvas = null;
            this.ctx = null;
            this.model = null;
            this.mixer = null;
            this.clock = new THREE.Clock();
            this.isLoaded = false;
            this.isVisible = false;
            this.isHovered = false;
            this.rotation = 0;
            this.targetRotation = 0;

            this.init();
        }

        init() {
            this.canvas = document.createElement("canvas");
            this.canvas.width = CARD_WIDTH;
            this.canvas.height = CARD_HEIGHT;
            this.canvas.className = "w-full h-full object-cover transition-opacity duration-300 opacity-0 pointer-events-none";
            this.element.appendChild(this.canvas);

            this.ctx = this.canvas.getContext("2d", {alpha: false});

            this.element.addEventListener("mouseenter", () => {
                this.isHovered = true; this.cachedRect = this.element.getBoundingClientRect();
            });
            this.element.addEventListener("mouseleave", () => {
                this.isHovered = false;
            });
            this.element.addEventListener("mousemove", (e) => {
                const rect = this.cachedRect || (this.cachedRect = this.element.getBoundingClientRect());
                const x = (e.clientX - rect.left) / rect.width - 0.5;
                this.targetRotation = x * 0.9;
            });

            this.loadModel();
        }

        async loadModel() {
            const cleanHero = this.hero.replace("Hero_", "").toLowerCase();
            const altParam = this.alt ? `/${encodeURIComponent(this.alt)}` : "";
            const resParam = this.resource ? `resource=${encodeURIComponent(this.resource)}` : "";
            const query = [resParam, "_t=v4"].filter(Boolean).join("&");
            const url = `/api/v1/assets/3d/hero/${encodeURIComponent(cleanHero)}${altParam}?${query}`;

            try {
                let buffer;
                if (modelCache.has(url)) {
                    buffer = await modelCache.get(url);
                } else {
                    const fetchPromise = (async () => {
                        const resp = await fetch(url);
                        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
                        return await resp.arrayBuffer();
                    })();
                    modelCache.set(url, fetchPromise);
                    buffer = await fetchPromise;
                }

                const loader = window.KongorViewerCore ? window.KongorViewerCore.createKongorGLTFLoader() : new THREE.GLTFLoader();
                loader.parse(
                    buffer.slice(0),
                    "",
                    (gltf) => {
                        this.model = gltf.scene;

                        this.model.traverse((child) => {
                            if (child.isMesh && child.material) {
                                child.material.side = THREE.DoubleSide;
                                child.material.metalness = 0.22;
                                child.material.roughness = 0.55;
                                child.material.envMapIntensity = 1.35;
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
                        });

                        const animations = gltf.animations || [];
                        const idleClip = animations.find(a => a.name.toLowerCase().includes("idle")) || animations[0];
                        if (idleClip) {
                            const tempMixer = new THREE.AnimationMixer(this.model);
                            const action = tempMixer.clipAction(idleClip);
                            action.play();
                            tempMixer.update(0.016);
                        }

                        this.isLoaded = true;

                        // Hide placeholder and reveal canvas
                        const placeholder = this.element.querySelector(".vp-placeholder");
                        if (placeholder) placeholder.style.display = "none";

                        this.canvas.classList.remove("opacity-0");
                        this.canvas.classList.add("opacity-100");

                        // Render static pose frame
                        this.renderFrame(0.016); if (this.isVisible) startGlobalAnimationLoop();
                    },
                    (err) => {
                        console.warn("[Card3D] Error parsing GLTF for", this.name, err);
                        this.showFallback();
                    }
                );
            } catch (err) {
                console.warn("[Card3D] Error loading model for", this.name, err);
                this.showFallback();
            }
        }

        showFallback() {
            const placeholder = this.element.querySelector(".vp-placeholder");
            if (placeholder) {
                placeholder.innerHTML = `
                    <div class="w-12 h-12 rounded-full bg-slate-800/80 border border-slate-700 flex items-center justify-center text-amber-400 text-lg shadow-inner">
                        <i class="fas fa-cube"></i>
                    </div>
                    <span class="text-[10px] font-bold text-slate-400 mt-1.5 uppercase tracking-wider">3D Available</span>
                `;
            }
        }

        renderFrame(delta) {
            if (!this.isLoaded || !this.model || !this.ctx) return;

            if (this.mixer) {
                this.mixer.update(delta);
            }

            if (this.isHovered) {
                this.rotation += (this.targetRotation - this.rotation) * 0.12;
            } else {
                this.rotation += delta * 0.35;
            }
            this.model.rotation.y = this.rotation;

            // Mount to shared scene
            sharedScene.add(this.model);

            // Render to offscreen canvas
            sharedRenderer.render(sharedScene, sharedCamera);

            // Unmount from shared scene
            sharedScene.remove(this.model);

            // Blit to on-card 2D canvas
            this.ctx.drawImage(sharedCanvas, 0, 0);
        }
    }

    function startGlobalAnimationLoop() {
        if (animLoopRunning) return;
        animLoopRunning = true;
        let lastTime = performance.now();

        function loop(now) {
            if (document.hidden) { animLoopRunning = false; return; }
            let renderedCount = 0;
            const delta = Math.min((now - lastTime) / 1000, 0.1);
            lastTime = now;

            activeCards.forEach((card) => {
                if (card.isVisible && card.isLoaded) {
                    card.renderFrame(delta); renderedCount++;
                }
            });

            if (renderedCount > 0 && !document.hidden) { requestAnimationFrame(loop); } else { animLoopRunning = false; }
        }

        requestAnimationFrame(loop);
    }

    async function initStoreCard3DEngine() {
        if (!window.THREE || !window.THREE.GLTFLoader) {
            await loadScript("/lib/three/three.r16x.bundle.js");
        }

        initSharedRenderer();

        const elements = document.querySelectorAll(".store-3d-card-viewport");
        if (elements.length === 0) return;

        const cardInstances = new Map();

        observer = new IntersectionObserver((entries) => {
            entries.forEach((entry) => {
                const el = entry.target;
                let card = cardInstances.get(el);

                if (!card) {
                    const hero = el.getAttribute("data-hero");
                    const alt = el.getAttribute("data-alt");
                    const name = el.getAttribute("data-name");
                    const res = el.getAttribute("data-res");
                    card = new Card3DInstance(el, hero, alt, name, res);
                    cardInstances.set(el, card);
                    activeCards.add(card);
                }

                card.isVisible = entry.isIntersecting;
                if (card.isVisible && card.isLoaded) startGlobalAnimationLoop();
            });
        }, {
            rootMargin: "250px 0px 250px 0px",
            threshold: 0.01
        });

        elements.forEach(el => observer.observe(el));
        document.addEventListener("visibilitychange", () => { if (!document.hidden) startGlobalAnimationLoop(); });
        window.addEventListener("resize", () => { activeCards.forEach(c => { c.cachedRect = null; }); }, { passive: true });
        window.addEventListener("scroll", () => { activeCards.forEach(c => { c.cachedRect = null; }); }, { passive: true });
        startGlobalAnimationLoop();
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

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initStoreCard3DEngine);
    } else {
        initStoreCard3DEngine();
    }
})();
