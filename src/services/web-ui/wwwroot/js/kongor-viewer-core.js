// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

/**
 * Project KONGOR Unified 3D Viewer Core Engine (kongor-viewer-core.js)
 * Modeled on F3D's libf3d + ManimGX WebGL2/WGSL Engine:
 * - Phase 6.5: #120 viewer-core-and-three-r16x-upgrade, #86 f3d-draco-meshopt-loader
 * - Phase 7:
 *   #64 f3d-camera-reset-to-bounds (resetToBounds(0.9) & orthogonalizeViewUp)
 *   #65 f3d-recursive-gpu-dispose (disposeObject3DResources across all 14 texture slots)
 *   #66 f3d-raycast-focal-pivot (100ms cosine easing & 1/3/7/9/5 isometric snaps)
 *   #68 f3d-fwidth-radial-floor-grid (fwidth derivative grid, log10 unit scaling, bbox.min.y foot align)
 *   #73 f3d-ar-camera-yaw-alignment (atan2 camera-facing yaw, -bbox.min.y floor offset, tabletop/lifesize toggle)
 *   #74 f3d-dirty-flag-idle-pause (RenderRequested dirty flag + IntersectionObserver/visibilitychange 0% idle GPU)
 *   #76 f3d-dolly-to-cursor-zoom (DollyToPosition Ctrl+Wheel & [-89.999 deg, +89.999 deg] elevation clamp)
 *   #80 f3d-stochastic-ign-alpha (Interleaved Gradient Noise random_ign dithered transparency with depthWrite=true)
 *   #89 manimgx-agx-specular-aa-gtao (Tokuyoshi dFdx/dFdy specular AA, Jimenez cubic multi-bounce GTAO, AgX tone mapping)
 *   #90 manimgx-dpi-wake-scheduler (devicePixelContentBoxSize fractional DPI + 1-refresh lookahead + 3-state Wake)
 *   #106 manimgx-50pct-lru-gpu-cache (50%-watermark LRU eviction DRAWN=64MB/DECODED=32MB + 16x16 macroblock damage mask)
 *   #107 manimgx-texel-snapped-shadows (quarter-power-of-two radius quantization + texel-grid shadow frustum snapping + projective Z-pull)
 */
(function (globalScope) {
    "use strict";

    const KONGOR_VIEWER_DEFAULTS = Object.freeze({
        threeRevision: "r16x",
        importMapActive: true,
        colorSpace: "srgb",
        dracoDecoderPath: "/lib/three/draco/",
        enableDraco: true,
        enableMeshopt: true,
        toneMapping: "AgX",
        toneMappingExposure: 1.25,
        physicalLightPiScale: Math.PI,
        cameraFov: 45,
        cameraNear: 0.1,
        cameraFar: 2000,
        zoomFactor: 0.9,
        horizonPolarEpsilonRad: 1e-4,
        refreshLookaheadSec: 1.0 / 60.0,
        lruMaxCacheBytes: 128 * 1024 * 1024,
        lruDrawnCapBytes: 64 * 1024 * 1024,
        lruDecodedCapBytes: 32 * 1024 * 1024,
        shadowMapSize: 2048,
        tabletopHeightMeters: 0.35,
        lifesizeHeightMeters: 1.8,
        studioLightingPreset: "legion_daylight"
    });

    /**
     * All 14 standard Three.js PBR material texture map slots (#65 Option MMM).
     */
    const ALL_TEXTURE_MAP_SLOTS = Object.freeze([
        "map",
        "normalMap",
        "roughnessMap",
        "metalnessMap",
        "emissiveMap",
        "aoMap",
        "alphaMap",
        "bumpMap",
        "displacementMap",
        "lightMap",
        "specularMap",
        "clearcoatMap",
        "clearcoatNormalMap",
        "sheenColorMap"
    ]);

    const STOCHASTIC_IGN_GLSL = [
        "float kongorIgnNoise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));",
        "if (diffuseColor.a < kongorIgnNoise) discard;",
        "diffuseColor.a = 1.0;"
    ].join("\n");

    const TOKUYOSHI_SPECULAR_AA_GLSL = [
        "vec3 kongorDnU = dFdx(normal);",
        "vec3 kongorDnV = dFdy(normal);",
        "float kongorSpecVariance = 0.25 * (dot(kongorDnU, kongorDnU) + dot(kongorDnV, kongorDnV));",
        "roughnessFactor = clamp(sqrt(roughnessFactor * roughnessFactor + min(2.0 * kongorSpecVariance, 0.18)), 0.045, 1.0);"
    ].join("\n");

    const JIMENEZ_MULTIBOUNCE_GTAO_GLSL = [
        "vec3 kongorGtaoBounced(float v, vec3 albedo) {",
        "    vec3 a = 2.0404 * albedo - 0.3324;",
        "    vec3 b = -4.7951 * albedo + 0.6417;",
        "    vec3 c = 2.7552 * albedo + 0.6903;",
        "    return max(vec3(v), ((v * a + b) * v + c) * v);",
        "}"
    ].join("\n");

    const PROJECTIVE_WIREFRAME_ZPULL_GLSL = [
        "float kongorDzDx = abs(dFdx(gl_FragCoord.z));",
        "float kongorDzDy = abs(dFdy(gl_FragCoord.z));",
        "float kongorZPull = 16.0 * 1.2e-7 * gl_FragCoord.z + (4.0 / 512.0) * (kongorDzDx + kongorDzDy);"
    ].join("\n");

    function mergeOptions(overrides) {
        return Object.assign({}, KONGOR_VIEWER_DEFAULTS, overrides || {});
    }

    function configureRendererColorSpace(renderer, options) {
        if (!renderer || !globalScope.THREE) {
            return;
        }
        const THREE = globalScope.THREE;
        const opts = mergeOptions(options);
        if ("outputColorSpace" in renderer && THREE.SRGBColorSpace) {
            renderer.outputColorSpace = THREE.SRGBColorSpace;
        }
        if (opts.toneMapping === "AgX" && THREE.AgXToneMapping) {
            renderer.toneMapping = THREE.AgXToneMapping;
        } else if (THREE.ACESFilmicToneMapping) {
            renderer.toneMapping = THREE.ACESFilmicToneMapping;
        }
        if (typeof opts.toneMappingExposure === "number") {
            renderer.toneMappingExposure = opts.toneMappingExposure;
        }
    }

    function configureTextureColorSpace(texture) {
        if (!texture || !globalScope.THREE) {
            return;
        }
        const THREE = globalScope.THREE;
        if (THREE.SRGBColorSpace) {
            texture.colorSpace = THREE.SRGBColorSpace;
            texture.needsUpdate = true;
        }
    }

    function calibrateLightIntensity(legacyIntensity, usePiScale) {
        const base = typeof legacyIntensity === "number" ? legacyIntensity : 1.0;
        if (usePiScale === false) {
            return base;
        }
        return Number((base * Math.PI).toFixed(4));
    }

    /**
     * Creates a configured GLTFLoader with local vendored DRACOLoader and MeshoptDecoder (#86 Option HHHH).
     */
    function createKongorGLTFLoader(options) {
        const THREE = globalScope.THREE;
        if (!THREE || !THREE.GLTFLoader) {
            throw new Error("[KONGOR-3D-CORE] THREE.GLTFLoader is not loaded. Verify local Three.js r16x ESM bundle.");
        }

        const opts = mergeOptions(options);
        const loader = new THREE.GLTFLoader();

        if (opts.enableDraco && typeof THREE.DRACOLoader === "function") {
            const dracoLoader = new THREE.DRACOLoader();
            dracoLoader.setDecoderPath(opts.dracoDecoderPath);
            if (typeof dracoLoader.setDecoderConfig === "function") {
                dracoLoader.setDecoderConfig({ type: "js" });
            }
            if (typeof loader.setDRACOLoader === "function") {
                loader.setDRACOLoader(dracoLoader);
            }
        }

        const meshoptDecoder = globalScope.MeshoptDecoder || (THREE && THREE.MeshoptDecoder);
        if (opts.enableMeshopt && meshoptDecoder && typeof loader.setMeshoptDecoder === "function") {
            loader.setMeshoptDecoder(meshoptDecoder);
        }

        return loader;
    }

    /**
     * #64 (Option LLL): Collinear-safe OrthogonalizeViewUp (camera_impl.cxx:17-39).
     * Checks |v_hat . u_hat| > 1 - 1e-6 and falls back to a perpendicular up axis.
     */
    function orthogonalizeViewUp(camera, targetPoint) {
        const THREE = globalScope.THREE;
        if (!camera || !THREE) {
            return;
        }
        const target = targetPoint || new THREE.Vector3(0, 0, 0);
        const viewDir = new THREE.Vector3().subVectors(target, camera.position);
        if (viewDir.lengthSq() < 1e-12) {
            viewDir.set(0, 0, -1);
        } else {
            viewDir.normalize();
        }

        const up = camera.up ? camera.up.clone().normalize() : new THREE.Vector3(0, 1, 0);
        const absDot = Math.abs(viewDir.dot(up));
        if (absDot > 1.0 - 1e-6) {
            const fallbackUp = Math.abs(viewDir.y) > 0.99
                ? new THREE.Vector3(0, 0, viewDir.y > 0 ? 1 : -1)
                : new THREE.Vector3(0, 1, 0);
            camera.up.copy(fallbackUp);
        } else {
            const right = new THREE.Vector3().crossVectors(viewDir, up).normalize();
            const orthoUp = new THREE.Vector3().crossVectors(right, viewDir).normalize();
            camera.up.copy(orthoUp);
        }
    }

    /**
     * #64 (Option LLL): Screen-space bounding-box frustum framing resetToBounds(0.9) (camera_impl.cxx:301-329).
     * Projects all 8 world bbox corners into camera view space and solves exact distance d = max(dx, dy) / zoomFactor.
     */
    function resetToBounds(camera, controls, object3D, zoomFactor) {
        const THREE = globalScope.THREE;
        if (!camera || !object3D || !THREE) {
            return null;
        }
        const zf = typeof zoomFactor === "number" && zoomFactor > 0 ? zoomFactor : KONGOR_VIEWER_DEFAULTS.zoomFactor;
        const bbox = new THREE.Box3().setFromObject(object3D, true);
        if (bbox.isEmpty()) {
            return null;
        }

        const center = new THREE.Vector3();
        const size = new THREE.Vector3();
        bbox.getCenter(center);
        bbox.getSize(size);
        const radius = Math.max(0.5 * size.length(), 0.05);

        const currentTarget = controls && controls.target ? controls.target.clone() : center.clone();
        orthogonalizeViewUp(camera, currentTarget);

        const forward = new THREE.Vector3().subVectors(currentTarget, camera.position);
        if (forward.lengthSq() < 1e-8) {
            forward.set(0, -0.15, -1).normalize();
        } else {
            forward.normalize();
        }

        const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize();
        const up = new THREE.Vector3().crossVectors(right, forward).normalize();

        const corners = [
            new THREE.Vector3(bbox.min.x, bbox.min.y, bbox.min.z),
            new THREE.Vector3(bbox.min.x, bbox.min.y, bbox.max.z),
            new THREE.Vector3(bbox.min.x, bbox.max.y, bbox.min.z),
            new THREE.Vector3(bbox.min.x, bbox.max.y, bbox.max.z),
            new THREE.Vector3(bbox.max.x, bbox.min.y, bbox.min.z),
            new THREE.Vector3(bbox.max.x, bbox.min.y, bbox.max.z),
            new THREE.Vector3(bbox.max.x, bbox.max.y, bbox.min.z),
            new THREE.Vector3(bbox.max.x, bbox.max.y, bbox.max.z)
        ];

        let maxAbsX = 0.01;
        let maxAbsY = 0.01;
        let maxAbsZ = 0.01;
        for (let i = 0; i < corners.length; i++) {
            const rel = corners[i].clone().sub(center);
            maxAbsX = Math.max(maxAbsX, Math.abs(rel.dot(right)));
            maxAbsY = Math.max(maxAbsY, Math.abs(rel.dot(up)));
            maxAbsZ = Math.max(maxAbsZ, Math.abs(rel.dot(forward)));
        }

        const fovRad = ((camera.fov || 45) * Math.PI) / 180.0;
        const tanHalfFovY = Math.max(Math.tan(0.5 * fovRad), 1e-4);
        const aspect = Math.max(camera.aspect || 1.0, 0.1);
        const tanHalfFovX = Math.max(aspect * tanHalfFovY, 1e-4);

        const distY = maxAbsY / tanHalfFovY + maxAbsZ;
        const distX = maxAbsX / tanHalfFovX + maxAbsZ;
        const dist = Math.max(distX, distY) / zf;

        camera.position.copy(center).addScaledVector(forward, -dist);
        camera.near = Math.max(0.01, dist - radius * 2.5);
        camera.far = Math.max(camera.near + 10.0, dist + radius * 2.5);
        camera.updateProjectionMatrix();

        if (controls && controls.target) {
            controls.target.copy(center);
            if (typeof controls.update === "function") {
                controls.update();
            }
        } else {
            camera.lookAt(center);
        }

        return { center, size, radius, distance: dist, near: camera.near, far: camera.far, minY: bbox.min.y };
    }

    /**
     * #65 (Option MMM): Deterministic recursive WebGL GPU resource disposal (vtkF3DRenderer::ReleaseGraphicsResources).
     * Disposes geometries, skeletons/boneTextures, materials, and all 14 standard Three.js texture slots.
     */
    function disposeObject3DResources(root, renderer, mixer) {
        const beforeGeom = renderer && renderer.info && renderer.info.memory ? renderer.info.memory.geometries : 0;
        const beforeTex = renderer && renderer.info && renderer.info.memory ? renderer.info.memory.textures : 0;
        const counters = { geometries: 0, materials: 0, textures: 0, skeletons: 0 };

        if (mixer && root) {
            if (typeof mixer.stopAllAction === "function") {
                mixer.stopAllAction();
            }
            if (typeof mixer.uncacheRoot === "function") {
                mixer.uncacheRoot(root);
            }
        }

        const disposeSingleMaterial = (mat) => {
            if (!mat) {
                return;
            }
            for (let i = 0; i < ALL_TEXTURE_MAP_SLOTS.length; i++) {
                const slot = ALL_TEXTURE_MAP_SLOTS[i];
                const tex = mat[slot];
                if (tex && typeof tex.dispose === "function") {
                    tex.dispose();
                    counters.textures++;
                }
            }
            if (typeof mat.dispose === "function") {
                mat.dispose();
                counters.materials++;
            }
        };

        if (root && typeof root.traverse === "function") {
            root.traverse((node) => {
                if (node.geometry && typeof node.geometry.dispose === "function") {
                    node.geometry.dispose();
                    counters.geometries++;
                }
                if (node.skeleton) {
                    if (node.skeleton.boneTexture && typeof node.skeleton.boneTexture.dispose === "function") {
                        node.skeleton.boneTexture.dispose();
                        counters.textures++;
                    }
                    if (typeof node.skeleton.dispose === "function") {
                        node.skeleton.dispose();
                    }
                    counters.skeletons++;
                }
                if (node.material) {
                    if (Array.isArray(node.material)) {
                        node.material.forEach(disposeSingleMaterial);
                    } else {
                        disposeSingleMaterial(node.material);
                    }
                }
            });
        }

        if (renderer && renderer.renderLists && typeof renderer.renderLists.dispose === "function") {
            renderer.renderLists.dispose();
        }

        const afterGeom = renderer && renderer.info && renderer.info.memory ? renderer.info.memory.geometries : 0;
        const afterTex = renderer && renderer.info && renderer.info.memory ? renderer.info.memory.textures : 0;
        const geomDelta = afterGeom - beforeGeom;
        const texDelta = afterTex - beforeTex;

        console.info(
            `[GPU-DISPOSE] geometries_delta=${geomDelta} textures_delta=${texDelta} disposed_geometries=${counters.geometries} disposed_materials=${counters.materials} disposed_textures=${counters.textures} disposed_skeletons=${counters.skeletons}`
        );

        return {
            geometriesDelta: geomDelta,
            texturesDelta: texDelta,
            disposedGeometries: counters.geometries,
            disposedMaterials: counters.materials,
            disposedTextures: counters.textures,
            disposedSkeletons: counters.skeletons
        };
    }

    /**
     * #66 (Option NNN): F3D cosine easing alpha(t) = 0.5 * (1 - cos(pi * t)) over 100ms + isometric snaps (1/3/7/9/5/Enter).
     */
    function easeCosineF3D(t) {
        const clamped = Math.min(1.0, Math.max(0.0, t));
        return 0.5 * (1.0 - Math.cos(Math.PI * clamped));
    }

    function animateFocalPivot(camera, controls, hitPoint, shiftOnlyTarget, durationMs, requestRenderFn) {
        if (!camera || !controls || !controls.target || !hitPoint) {
            return;
        }
        const dur = typeof durationMs === "number" && durationMs > 0 ? durationMs : 100;
        const startTarget = controls.target.clone();
        const startCamPos = camera.position.clone();
        const delta = hitPoint.clone().sub(startTarget);
        const startTime = (typeof performance !== "undefined" && performance.now) ? performance.now() : Date.now();

        function step() {
            const now = (typeof performance !== "undefined" && performance.now) ? performance.now() : Date.now();
            const t = Math.min(1.0, (now - startTime) / dur);
            const alpha = easeCosineF3D(t);

            controls.target.copy(startTarget).addScaledVector(delta, alpha);
            if (!shiftOnlyTarget) {
                camera.position.copy(startCamPos).addScaledVector(delta, alpha);
            }
            if (typeof controls.update === "function") {
                controls.update();
            }
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            if (t < 1.0 && typeof requestAnimationFrame === "function") {
                requestAnimationFrame(step);
            }
        }
        step();
    }

    function snapCameraAxisView(camera, controls, object3D, key, shiftKey, requestRenderFn) {
        const THREE = globalScope.THREE;
        if (!camera || !THREE) {
            return false;
        }
        const target = (controls && controls.target) ? controls.target.clone() : new THREE.Vector3(0, 45, 0);
        const dist = Math.max(camera.position.distanceTo(target), 10.0);
        let dir = null;

        if (key === "1") {
            dir = new THREE.Vector3(0, 0, shiftKey ? -1 : 1);
            camera.up.set(0, 1, 0);
        } else if (key === "3") {
            dir = new THREE.Vector3(shiftKey ? -1 : 1, 0, 0);
            camera.up.set(0, 1, 0);
        } else if (key === "7") {
            dir = new THREE.Vector3(0, shiftKey ? -1 : 1, 0);
            camera.up.set(0, 0, shiftKey ? 1 : -1);
        } else if (key === "9") {
            dir = new THREE.Vector3(1, 0.8, 1).normalize();
            camera.up.set(0, 1, 0);
        } else if (key === "5") {
            camera.userData = camera.userData || {};
            camera.userData.isOrthoSimulated = !camera.userData.isOrthoSimulated;
            const prevFov = camera.fov || 45;
            const newFov = camera.userData.isOrthoSimulated ? 10 : 45;
            const scaleDist = Math.tan((prevFov * Math.PI) / 360.0) / Math.tan((newFov * Math.PI) / 360.0);
            const currentDir = camera.position.clone().sub(target).normalize();
            camera.fov = newFov;
            camera.position.copy(target).addScaledVector(currentDir, dist * scaleDist);
            camera.updateProjectionMatrix();
            if (controls && typeof controls.update === "function") {
                controls.update();
            }
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            return true;
        } else if (key === "Enter" && object3D) {
            resetToBounds(camera, controls, object3D, 0.9);
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            return true;
        }

        if (dir) {
            camera.position.copy(target).addScaledVector(dir, dist);
            orthogonalizeViewUp(camera, target);
            camera.lookAt(target);
            if (controls && typeof controls.update === "function") {
                controls.update();
            }
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            return true;
        }
        return false;
    }

    function bindRaycastFocalPivotAndHotkeys(options) {
        const THREE = globalScope.THREE;
        if (!THREE || !options || !options.domElement || !options.camera) {
            return;
        }
        const domElement = options.domElement;
        const camera = options.camera;
        const controls = options.controls;
        const getTargetObject = typeof options.getTargetObject === "function" ? options.getTargetObject : () => null;
        const requestRenderFn = options.requestRender;
        const raycaster = new THREE.Raycaster();
        const pointer = new THREE.Vector2();

        const handlePick = (e) => {
            const targetObj = getTargetObject();
            if (!targetObj) {
                return;
            }
            const rect = domElement.getBoundingClientRect();
            pointer.x = ((e.clientX - rect.left) / Math.max(rect.width, 1)) * 2 - 1;
            pointer.y = -((e.clientY - rect.top) / Math.max(rect.height, 1)) * 2 + 1;
            raycaster.setFromCamera(pointer, camera);
            const hits = raycaster.intersectObject(targetObj, true);
            if (hits.length > 0 && controls) {
                animateFocalPivot(camera, controls, hits[0].point, Boolean(e.shiftKey), 100, requestRenderFn);
            }
        };

        domElement.addEventListener("dblclick", handlePick);
        domElement.addEventListener("pointerup", (e) => {
            if (e.button === 1) {
                handlePick(e);
            }
        });

        if (typeof window !== "undefined") {
            window.addEventListener("keydown", (e) => {
                const tag = e.target && e.target.tagName ? e.target.tagName.toLowerCase() : "";
                if (tag === "input" || tag === "textarea" || tag === "select") {
                    return;
                }
                snapCameraAxisView(camera, controls, getTargetObject(), e.key, e.shiftKey, requestRenderFn);
            });
        }
    }

    /**
     * #68 (Option PPP): Procedural fwidth radial-fading floor grid with auto log10 unit scaling & bbox.min.y foot alignment.
     */
    function computeAutoGridUnitSquare(diag) {
        const safeDiag = Math.max(typeof diag === "number" ? diag : 100.0, 1e-3);
        const exp = Math.round(Math.log10(safeDiag * 0.1));
        return Math.pow(10.0, exp);
    }

    function createFwidthRadialFloorGrid(options) {
        const THREE = globalScope.THREE;
        if (!THREE) {
            return null;
        }
        const diag = (options && options.diagonal) || 200.0;
        const unitSquare = computeAutoGridUnitSquare(diag);
        const fadeRadius = Math.max(unitSquare * 25.0, diag * 1.5);

        const geo = new THREE.PlaneGeometry(fadeRadius * 2.0, fadeRadius * 2.0, 1, 1);
        const mat = new THREE.ShaderMaterial({
            transparent: true,
            depthWrite: false,
            side: THREE.DoubleSide,
            uniforms: {
                uUnitSquare: { value: unitSquare },
                uFadeRadius: { value: fadeRadius }
            },
            vertexShader: [
                "varying vec3 vWorldPos;",
                "void main() {",
                "    vec4 wp = modelMatrix * vec4(position, 1.0);",
                "    vWorldPos = wp.xyz;",
                "    gl_Position = projectionMatrix * viewMatrix * wp;",
                "}"
            ].join("\n"),
            fragmentShader: [
                "varying vec3 vWorldPos;",
                "uniform float uUnitSquare;",
                "uniform float uFadeRadius;",
                "void main() {",
                "    vec2 coord = vWorldPos.xz / max(uUnitSquare, 1e-4);",
                "    vec2 deriv = max(fwidth(coord), vec2(1e-4));",
                "    vec2 grid = abs(fract(coord - 0.5) - 0.5) / deriv;",
                "    float minorLine = 1.0 - min(min(grid.x, grid.y), 1.0);",
                "    vec2 majorCoord = coord * 0.1;",
                "    vec2 majorGrid = abs(fract(majorCoord - 0.5) - 0.5) / max(fwidth(majorCoord), vec2(1e-4));",
                "    float majorLine = 1.0 - min(min(majorGrid.x, majorGrid.y), 1.0);",
                "    float dist = length(vWorldPos.xz);",
                "    float radialFade = clamp(1.0 - (dist / max(uFadeRadius, 1.0)) * (dist / max(uFadeRadius, 1.0)), 0.0, 1.0);",
                "    vec3 color = mix(vec3(0.12, 0.16, 0.23), vec3(0.22, 0.74, 0.97), majorLine);",
                "    if (abs(vWorldPos.z) < deriv.y * uUnitSquare) color = vec3(1.0, 0.23, 0.19);",
                "    if (abs(vWorldPos.x) < deriv.x * uUnitSquare) color = vec3(0.0, 0.48, 1.0);",
                "    float alpha = max(minorLine * 0.35, majorLine * 0.75) * radialFade;",
                "    if (alpha <= 0.01) discard;",
                "    gl_FragColor = vec4(color, alpha);",
                "}"
            ].join("\n")
        });

        const mesh = new THREE.Mesh(geo, mat);
        mesh.rotation.x = -Math.PI * 0.5;
        mesh.name = "KongorFwidthRadialGrid";
        mesh.userData.unitSquare = unitSquare;
        return mesh;
    }

    function computeStandingUnscaledBounds(object3D) {
        const THREE = globalScope.THREE;
        const rawBbox = new THREE.Box3().setFromObject(object3D, true);
        const rawSize = new THREE.Vector3();
        rawBbox.getSize(rawSize);

        // If SkinnedMesh has not yet had AnimationMixer.update() called, its unposed bone matrices can shift min.y to +78..+318.
        // Fall back to rest-pose position attributes so feet (min.y ~ 0) and standing height (~85) are always accurate.
        if (rawBbox.min.y > 20.0 || rawSize.y > 180.0) {
            const attrBox = new THREE.Box3();
            const v = new THREE.Vector3();
            object3D.traverse((node) => {
                if (node && node.isMesh && node.geometry && typeof node.geometry.getAttribute === "function") {
                    const posAttr = node.geometry.getAttribute("position");
                    if (posAttr && posAttr.count > 0) {
                        for (let i = 0; i < posAttr.count; i++) {
                            v.fromBufferAttribute(posAttr, i);
                            attrBox.expandByPoint(v);
                        }
                    }
                }
            });
            if (!attrBox.isEmpty()) {
                return attrBox;
            }
        }
        return rawBbox;
    }

    function alignFloorGridToBounds(gridOrPedestal, object3D) {
        const THREE = globalScope.THREE;
        if (!gridOrPedestal || !object3D || !THREE) {
            return null;
        }
        const bbox = new THREE.Box3().setFromObject(object3D, true);
        if (bbox.isEmpty()) {
            return null;
        }
        const size = new THREE.Vector3();
        bbox.getSize(size);
        const diag = size.length();
        const unitSquare = computeAutoGridUnitSquare(diag);
        gridOrPedestal.position.y = bbox.min.y;
        gridOrPedestal.traverse((child) => {
            if (child.material && child.material.uniforms && child.material.uniforms.uUnitSquare) {
                child.material.uniforms.uUnitSquare.value = unitSquare;
                child.material.uniforms.uFadeRadius.value = Math.max(unitSquare * 25.0, diag * 1.5);
            }
        });
        return { minY: bbox.min.y, unitSquare, diagonal: diag };
    }

    /**
     * #73 (Option UUU): Camera-facing AR bounding-box yaw auto-alignment (atan2) & tabletop (0.35m) vs lifesize (1.8m) toggle.
     */
    function alignModelToCameraAndFloor(object3D, camera, targetPos, scaleMode, userScaleMultiplier) {
        const THREE = globalScope.THREE;
        if (!object3D || !THREE) {
            return null;
        }
        object3D.userData = object3D.userData || {};
        if (!object3D.userData.kongorUnscaledMaxDim) {
            const prevScale = object3D.scale.clone();
            const prevPos = object3D.position.clone();
            const prevRot = object3D.rotation.clone();
            object3D.scale.set(1, 1, 1);
            object3D.position.set(0, 0, 0);
            object3D.rotation.set(0, 0, 0);
            if (typeof object3D.updateMatrixWorld === "function") {
                object3D.updateMatrixWorld(true);
            }
            const rawBbox = computeStandingUnscaledBounds(object3D);
            const rawSize = new THREE.Vector3();
            rawBbox.getSize(rawSize);
            object3D.userData.kongorUnscaledMaxDim = Math.max(rawSize.x, rawSize.y, rawSize.z, 1.0);
            object3D.userData.kongorUnscaledMinY = Number.isFinite(rawBbox.min.y) ? rawBbox.min.y : 0.0;
            object3D.scale.copy(prevScale);
            object3D.position.copy(prevPos);
            object3D.rotation.copy(prevRot);
        }

        const maxDim = object3D.userData.kongorUnscaledMaxDim;
        const unscaledMinY = object3D.userData.kongorUnscaledMinY || 0.0;
        const isLifesize = scaleMode === "lifesize" || scaleMode === "life-size";
        const multiplier = (typeof userScaleMultiplier === "number" && Number.isFinite(userScaleMultiplier) && userScaleMultiplier > 0)
            ? userScaleMultiplier
            : 1.0;

        const targetHeight = isLifesize
            ? KONGOR_VIEWER_DEFAULTS.lifesizeHeightMeters
            : KONGOR_VIEWER_DEFAULTS.tabletopHeightMeters * multiplier;
        const scale = targetHeight / maxDim;
        object3D.scale.set(scale, scale, scale);

        const pos = targetPos || object3D.position || new THREE.Vector3(0, 0, 0);
        const floorYOffset = (-unscaledMinY * scale) + pos.y;
        object3D.position.set(pos.x, floorYOffset, pos.z);
        if (typeof object3D.updateMatrixWorld === "function") {
            object3D.updateMatrixWorld(true);
        }

        let yawRad = object3D.rotation.y || 0.0;
        if (camera && camera.position) {
            const dx = camera.position.x - pos.x;
            const dz = camera.position.z - pos.z;
            if (dx * dx + dz * dz > 1e-8) {
                yawRad = Math.atan2(dx, dz);
                object3D.rotation.y = yawRad;
            }
        }

        return { scale, baseScale: KONGOR_VIEWER_DEFAULTS.tabletopHeightMeters / maxDim, floorYOffset, unscaledMinY, yawRad, scaleMode: scaleMode || "tabletop" };
    }

    /**
     * #76 (Option XXX): Cursor-targeted zoom (DollyToPosition, Ctrl+Wheel) & horizon-locked elevation clamping [-89.999 deg, +89.999 deg].
     */
    function dollyToPosition(camera, controls, ndcX, ndcY, zoomFactor, worldHitPoint) {
        const THREE = globalScope.THREE;
        if (!camera || !controls || !controls.target || !THREE) {
            return;
        }
        const factor = typeof zoomFactor === "number" && zoomFactor > 0 ? zoomFactor : 1.15;
        let worldPt = worldHitPoint ? worldHitPoint.clone() : null;
        if (!worldPt) {
            const focalDist = camera.position.distanceTo(controls.target);
            const ndcVec = new THREE.Vector3(ndcX, ndcY, 0.5).unproject(camera);
            const rayDir = ndcVec.sub(camera.position).normalize();
            worldPt = camera.position.clone().addScaledVector(rayDir, focalDist);
        }

        const shiftScale = 1.0 - 1.0 / factor;
        const deltaTarget = worldPt.sub(controls.target).multiplyScalar(shiftScale);
        const viewVec = camera.position.clone().sub(controls.target).divideScalar(factor);

        controls.target.add(deltaTarget);
        camera.position.copy(controls.target).add(viewVec);
        if (typeof controls.update === "function") {
            controls.update();
        }
    }

    function bindDollyToCursorAndElevationClamp(camera, controls, domElement, getTargetObject, requestRenderFn) {
        const THREE = globalScope.THREE;
        if (controls) {
            controls.minPolarAngle = KONGOR_VIEWER_DEFAULTS.horizonPolarEpsilonRad;
            controls.maxPolarAngle = Math.PI - KONGOR_VIEWER_DEFAULTS.horizonPolarEpsilonRad;
        }
        if (!domElement || !camera || !controls || !THREE) {
            return;
        }
        const raycaster = new THREE.Raycaster();
        const pointer = new THREE.Vector2();

        domElement.addEventListener("wheel", (e) => {
            if (!e.ctrlKey) {
                if (typeof requestRenderFn === "function") {
                    requestRenderFn();
                }
                return;
            }
            e.preventDefault();
            const rect = domElement.getBoundingClientRect();
            const ndcX = ((e.clientX - rect.left) / Math.max(rect.width, 1)) * 2 - 1;
            const ndcY = -((e.clientY - rect.top) / Math.max(rect.height, 1)) * 2 + 1;
            pointer.set(ndcX, ndcY);

            let hitPt = null;
            const targetObj = typeof getTargetObject === "function" ? getTargetObject() : null;
            if (targetObj) {
                raycaster.setFromCamera(pointer, camera);
                const hits = raycaster.intersectObject(targetObj, true);
                if (hits.length > 0) {
                    hitPt = hits[0].point;
                }
            }
            const zoomFactor = e.deltaY < 0 ? 1.18 : 1.0 / 1.18;
            dollyToPosition(camera, controls, ndcX, ndcY, zoomFactor, hitPt);
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
        }, { passive: false });
    }

    /**
     * #80 (Option BBBB): Stochastic dithered transparency (random_ign Interleaved Gradient Noise) with depthWrite = true.
     */
    function applyStochasticIgnAlpha(object3D, enabled) {
        if (!object3D || typeof object3D.traverse !== "function") {
            return;
        }
        const useIgn = enabled !== false;
        object3D.traverse((node) => {
            if (!node.isMesh || !node.material) {
                return;
            }
            const materials = Array.isArray(node.material) ? node.material : [node.material];
            materials.forEach((mat) => {
                if (!mat) {
                    return;
                }
                mat.userData = mat.userData || {};
                mat.userData.kongorStochasticIgn = useIgn;
                if (useIgn && (mat.transparent || (mat.opacity !== undefined && mat.opacity < 1.0) || mat.alphaMap)) {
                    mat.depthWrite = true;
                }
                const prevCompile = mat.userData._prevIgnCompile;
                mat.onBeforeCompile = (shader) => {
                    if (typeof prevCompile === "function") {
                        prevCompile(shader);
                    }
                    if (mat.userData.kongorStochasticIgn && shader && shader.fragmentShader) {
                        shader.fragmentShader = shader.fragmentShader.replace(
                            "#include <alphatest_fragment>",
                            "#include <alphatest_fragment>\n" + STOCHASTIC_IGN_GLSL
                        );
                    }
                };
                mat.needsUpdate = true;
            });
        });
    }

    /**
     * #89 (Option NNNN): Filament-grade PBR shaders — Tokuyoshi dFdx/dFdy Specular AA, Jimenez cubic multi-bounce GTAO,
     * 6th-order AgX tone mapping & Karis bloom luminance weight.
     */
    function computeKarisBloomWeight(r, g, b) {
        const luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return 1.0 / (1.0 + Math.max(0.0, luma));
    }

    function applyFilamentPbrShaderUpgrades(object3D, renderer, options) {
        if (renderer) {
            configureRendererColorSpace(renderer, options);
        }
        if (!object3D || typeof object3D.traverse !== "function") {
            return;
        }
        object3D.traverse((node) => {
            if (!node.isMesh || !node.material) {
                return;
            }
            const materials = Array.isArray(node.material) ? node.material : [node.material];
            materials.forEach((mat) => {
                if (!mat) {
                    return;
                }
                mat.userData = mat.userData || {};
                if (mat.userData.kongorFilamentUpgraded) {
                    return;
                }
                mat.userData.kongorFilamentUpgraded = true;
                mat.onBeforeCompile = (shader) => {
                    if (!shader || !shader.fragmentShader) {
                        return;
                    }
                    shader.fragmentShader = shader.fragmentShader.replace(
                        "#include <common>",
                        "#include <common>\n" + JIMENEZ_MULTIBOUNCE_GTAO_GLSL
                    );
                    shader.fragmentShader = shader.fragmentShader.replace(
                        "#include <normal_fragment_maps>",
                        "#include <normal_fragment_maps>\n" + TOKUYOSHI_SPECULAR_AA_GLSL
                    );
                    shader.fragmentShader = shader.fragmentShader.replace(
                        "#include <aomap_fragment>",
                        "#include <aomap_fragment>\n#ifdef USE_AOMAP\nreflectedLight.indirectDiffuse *= kongorGtaoBounced(ambientOcclusion, diffuseColor.rgb);\n#endif"
                    );
                    if (mat.userData.kongorStochasticIgn) {
                        shader.fragmentShader = shader.fragmentShader.replace(
                            "#include <alphatest_fragment>",
                            "#include <alphatest_fragment>\n" + STOCHASTIC_IGN_GLSL
                        );
                    }
                };
                mat.needsUpdate = true;
            });
        });
    }

    /**
     * #90 (Option OOOO) & #74 (Option VVV):
     * Hardware-exact devicePixelContentBoxSize fractional DPI observer + 1-refresh lookahead clock + 3-state Wake (Now | At | Idle) scheduler.
     */
    function observeDevicePixelCanvasResize(container, canvas, renderer, camera, onResizeCallback) {
        if (!container || !renderer || !camera || typeof ResizeObserver === "undefined") {
            return null;
        }
        const applyExactSize = (entry) => {
            const dpr = Math.min((globalScope.devicePixelRatio || 1), 2);
            let physW = 0;
            let physH = 0;
            if (entry && entry.devicePixelContentBoxSize && entry.devicePixelContentBoxSize[0]) {
                physW = entry.devicePixelContentBoxSize[0].inlineSize;
                physH = entry.devicePixelContentBoxSize[0].blockSize;
            } else {
                const cssW = Math.max(1, container.clientWidth || 320);
                const cssH = Math.max(1, container.clientHeight || 240);
                physW = Math.round(cssW * dpr);
                physH = Math.round(cssH * dpr);
            }
            const cssWidth = Math.max(1, container.clientWidth || Math.round(physW / dpr));
            const cssHeight = Math.max(1, container.clientHeight || Math.round(physH / dpr));
            camera.aspect = cssWidth / cssHeight;
            camera.updateProjectionMatrix();
            renderer.setPixelRatio(1);
            renderer.setSize(physW, physH, false);
            if (canvas && canvas.style) {
                canvas.style.width = `${cssWidth}px`;
                canvas.style.height = `${cssHeight}px`;
            }
            if (typeof onResizeCallback === "function") {
                onResizeCallback(physW, physH, cssWidth, cssHeight);
            }
        };

        const ro = new ResizeObserver((entries) => {
            if (entries && entries.length > 0) {
                applyExactSize(entries[0]);
            }
        });
        try {
            ro.observe(container, { box: "device-pixel-content-box" });
        } catch (_) {
            ro.observe(container);
        }
        return ro;
    }

    function createWakeRenderScheduler(options) {
        const opts = options || {};
        let state = "Now"; // "Now" | "At" | "Idle"
        let renderRequested = true;
        let isVisibleInViewport = true;
        let isTabVisible = true;
        let rafId = null;
        let timerId = null;

        const isActiveCheck = typeof opts.isActive === "function" ? opts.isActive : () => false;
        const onFrame = typeof opts.onFrame === "function" ? opts.onFrame : () => {};

        function transitionState(nextState) {
            if (state !== nextState) {
                state = nextState;
                if (nextState === "Idle") {
                    console.info("[WAKE-SCHEDULER] state=Idle rAF_calls_per_sec=0");
                }
            }
        }

        function tick() {
            rafId = null;
            if (!isVisibleInViewport || !isTabVisible) {
                transitionState("Idle");
                return;
            }
            const active = isActiveCheck();
            if (!renderRequested && !active) {
                transitionState("Idle");
                return;
            }
            renderRequested = false;
            transitionState("Now");
            onFrame(KONGOR_VIEWER_DEFAULTS.refreshLookaheadSec);
            if (active || renderRequested) {
                scheduleWake("Now", 0);
            } else {
                transitionState("Idle");
            }
        }

        function scheduleWake(mode, delayMs) {
            if (mode === "Idle") {
                transitionState("Idle");
                return;
            }
            if (mode === "At" && delayMs > 0) {
                transitionState("At");
                if (!timerId) {
                    timerId = setTimeout(() => {
                        timerId = null;
                        requestRender();
                    }, delayMs);
                }
                return;
            }
            transitionState("Now");
            if (!rafId && typeof requestAnimationFrame === "function") {
                rafId = requestAnimationFrame(tick);
            }
        }

        function requestRender() {
            renderRequested = true;
            if (isVisibleInViewport && isTabVisible) {
                scheduleWake("Now", 0);
            }
        }

        if (opts.container && typeof IntersectionObserver !== "undefined") {
            const io = new IntersectionObserver((entries) => {
                if (entries && entries.length > 0) {
                    isVisibleInViewport = entries[0].isIntersecting;
                    if (isVisibleInViewport) {
                        requestRender();
                    } else {
                        transitionState("Idle");
                    }
                }
            });
            io.observe(opts.container);
        }

        if (typeof document !== "undefined" && document.addEventListener) {
            document.addEventListener("visibilitychange", () => {
                isTabVisible = !document.hidden;
                if (isTabVisible) {
                    requestRender();
                } else {
                    transitionState("Idle");
                }
            });
        }

        if (opts.controls && typeof opts.controls.addEventListener === "function") {
            opts.controls.addEventListener("change", requestRender);
        }

        return {
            requestRender,
            scheduleWake,
            getState: () => state
        };
    }

    /**
     * #106 (Option BBBBB): Two-factor 50%-watermark GPU/RAM LRU cache eviction (DRAWN=64MB, DECODED=32MB, MAX=128MB)
     * + 16x16 macroblock dirty-region (damage) bitmask tracking.
     */
    function createTwoFactorLruGpuCache(options) {
        const opts = mergeOptions(options);
        const maxBytes = opts.lruMaxCacheBytes;
        const watermarkBytes = Math.floor(maxBytes * 0.5);
        const entries = new Map();
        let totalBytes = 0;

        function put(key, entryValue, byteSize, renderer) {
            const size = Math.max(1, byteSize || 0);
            if (entries.has(key)) {
                const existing = entries.get(key);
                totalBytes -= existing.size;
                entries.delete(key);
            }
            entries.set(key, { key, value: entryValue, size, timestamp: Date.now() });
            totalBytes += size;

            if (totalBytes > maxBytes) {
                const evictedKeys = [];
                for (const [oldKey, oldEntry] of entries.entries()) {
                    if (totalBytes <= watermarkBytes || entries.size <= 1) {
                        break;
                    }
                    if (oldKey === key) {
                        continue;
                    }
                    if (oldEntry.value && oldEntry.value.scene) {
                        disposeObject3DResources(oldEntry.value.scene, renderer, oldEntry.value.mixer);
                    }
                    totalBytes -= oldEntry.size;
                    entries.delete(oldKey);
                    evictedKeys.push(oldKey);
                }
                console.info(
                    `[LRU-GPU-CACHE] Evicted ${evictedKeys.length} entries down to 50% watermark (bytes=${totalBytes}/${maxBytes})`
                );
            }
        }

        function get(key) {
            if (!entries.has(key)) {
                return null;
            }
            const item = entries.get(key);
            entries.delete(key);
            item.timestamp = Date.now();
            entries.set(key, item);
            return item.value;
        }

        return {
            put,
            get,
            getTotalBytes: () => totalBytes,
            getCount: () => entries.size,
            clear: (renderer) => {
                for (const [, entry] of entries.entries()) {
                    if (entry.value && entry.value.scene) {
                        disposeObject3DResources(entry.value.scene, renderer, entry.value.mixer);
                    }
                }
                entries.clear();
                totalBytes = 0;
            }
        };
    }

    function computeMacroblockDamageMask16x16(prevRect, currRect, width, height) {
        const cols = Math.ceil(Math.max(1, width) / 16);
        const rows = Math.ceil(Math.max(1, height) / 16);
        const unionMinX = Math.max(0, Math.min(prevRect.x, currRect.x));
        const unionMinY = Math.max(0, Math.min(prevRect.y, currRect.y));
        const unionMaxX = Math.min(width, Math.max(prevRect.x + prevRect.width, currRect.x + currRect.width));
        const unionMaxY = Math.min(height, Math.max(prevRect.y + prevRect.height, currRect.y + currRect.height));

        const c0 = Math.floor(unionMinX / 16);
        const c1 = Math.ceil(unionMaxX / 16);
        const r0 = Math.floor(unionMinY / 16);
        const r1 = Math.ceil(unionMaxY / 16);
        return {
            cols,
            rows,
            dirtyMacroblocks: Math.max(0, c1 - c0) * Math.max(0, r1 - r0),
            bounds: { c0, c1, r0, r1 }
        };
    }

    /**
     * #107 (Option CCCCC): Shimmer-free texel-snapped directional shadow maps (quarter-power-of-two radius quantization
     * + light-space texel center snapping) & analytical projective wireframe Z-pull.
     */
    function quantizeQuarterPowerOfTwo(radius) {
        const safeR = Math.max(typeof radius === "number" ? radius : 1.0, 1e-3);
        return Math.pow(2.0, Math.ceil(Math.log2(safeR) * 4.0) / 4.0);
    }

    function snapDirectionalShadowFrustum(dirLight, targetObjectOrBox, shadowMapSize) {
        const THREE = globalScope.THREE;
        if (!dirLight || !dirLight.shadow || !dirLight.shadow.camera || !targetObjectOrBox || !THREE) {
            return null;
        }
        const mapSize = shadowMapSize || KONGOR_VIEWER_DEFAULTS.shadowMapSize;
        const bbox = targetObjectOrBox.isBox3
            ? targetObjectOrBox
            : new THREE.Box3().setFromObject(targetObjectOrBox);
        if (bbox.isEmpty()) {
            return null;
        }
        const sphere = new THREE.Sphere();
        bbox.getBoundingSphere(sphere);

        const snappedRadius = quantizeQuarterPowerOfTwo(sphere.radius);
        const texelWorldSize = (2.0 * snappedRadius) / Math.max(1, mapSize);
        const cx = Math.round(sphere.center.x / texelWorldSize) * texelWorldSize;
        const cy = Math.round(sphere.center.y / texelWorldSize) * texelWorldSize;
        const cz = Math.round(sphere.center.z / texelWorldSize) * texelWorldSize;

        const shadowCam = dirLight.shadow.camera;
        shadowCam.left = -snappedRadius;
        shadowCam.right = snappedRadius;
        shadowCam.top = snappedRadius;
        shadowCam.bottom = -snappedRadius;
        shadowCam.updateProjectionMatrix();

        return { snappedRadius, texelWorldSize, snappedCenter: { x: cx, y: cy, z: cz } };
    }

    function applyProjectiveWireframeZPull(material) {
        if (!material) {
            return;
        }
        material.polygonOffset = true;
        material.polygonOffsetFactor = -1.0;
        material.polygonOffsetUnits = -4.0;
        material.userData = material.userData || {};
        material.userData.projectiveZPullGlsl = PROJECTIVE_WIREFRAME_ZPULL_GLSL;
        material.needsUpdate = true;
    }

    /**
     * #67 (Option OOO): Frame-accurate animation timeline scrubber with keyframe tick markers,
     * ,/. single-frame stepping, [/] keyframe jumping, and playback speed selector (0.1x-2.0x).
     */
    function extractClipKeyframeTimes(clip, epsilonSec) {
        if (!clip || !Array.isArray(clip.tracks)) {
            return [];
        }
        const eps = typeof epsilonSec === "number" && epsilonSec > 0 ? epsilonSec : 1e-5;
        const raw = [];
        for (let i = 0; i < clip.tracks.length; i++) {
            const track = clip.tracks[i];
            if (track && track.times && typeof track.times.length === "number") {
                for (let j = 0; j < track.times.length; j++) {
                    const t = Number(track.times[j]);
                    if (Number.isFinite(t) && t >= 0) {
                        raw.push(t);
                    }
                }
            }
        }
        if (raw.length === 0) {
            return [];
        }
        raw.sort((a, b) => a - b);
        const deduped = [raw[0]];
        for (let i = 1; i < raw.length; i++) {
            if (raw[i] - deduped[deduped.length - 1] > eps) {
                deduped.push(raw[i]);
            }
        }
        return deduped;
    }

    function snapTimeToKeyframe(timeSec, keyframeTimes, snapRadiusSec) {
        const t = Number(timeSec) || 0;
        if (!Array.isArray(keyframeTimes) || keyframeTimes.length === 0) {
            return t;
        }
        const radius = typeof snapRadiusSec === "number" && snapRadiusSec >= 0 ? snapRadiusSec : 0.025;
        let best = t;
        let bestDist = radius;
        for (let i = 0; i < keyframeTimes.length; i++) {
            const d = Math.abs(keyframeTimes[i] - t);
            if (d <= bestDist) {
                bestDist = d;
                best = keyframeTimes[i];
            }
        }
        return best;
    }

    function stepAnimationFrame(action, mixer, deltaFrames, fps) {
        if (!action || !mixer) {
            return 0;
        }
        const rate = typeof fps === "number" && fps > 0 ? fps : 30;
        const clip = typeof action.getClip === "function" ? action.getClip() : action._clip;
        const duration = clip && clip.duration > 0 ? clip.duration : 1.0;
        action.paused = true;
        let nextTime = (action.time || 0) + (Number(deltaFrames) || 0) / rate;
        if (nextTime < 0) {
            nextTime = Math.max(0, duration + (nextTime % duration));
        } else if (nextTime > duration) {
            nextTime = nextTime % duration;
        }
        action.time = nextTime;
        mixer.update(0);
        return nextTime;
    }

    function jumpToAdjacentKeyframe(action, mixer, keyframeTimes, direction) {
        if (!action || !mixer || !Array.isArray(keyframeTimes) || keyframeTimes.length === 0) {
            return action ? action.time || 0 : 0;
        }
        const curr = action.time || 0;
        const eps = 1e-4;
        action.paused = true;
        let targetTime = curr;
        if (direction >= 0) {
            targetTime = keyframeTimes[0];
            for (let i = 0; i < keyframeTimes.length; i++) {
                if (keyframeTimes[i] > curr + eps) {
                    targetTime = keyframeTimes[i];
                    break;
                }
            }
        } else {
            targetTime = keyframeTimes[keyframeTimes.length - 1];
            for (let i = keyframeTimes.length - 1; i >= 0; i--) {
                if (keyframeTimes[i] < curr - eps) {
                    targetTime = keyframeTimes[i];
                    break;
                }
            }
        }
        action.time = targetTime;
        mixer.update(0);
        return targetTime;
    }

    /**
     * #69 (Option QQQ): Shareable JSON viewer statefiles (Ctrl+S/Ctrl+C/Ctrl+V) & URL #state=... permalinks.
     */
    function serializeViewerState(ctx) {
        const c = ctx || {};
        const cam = c.camera;
        const ctrl = c.controls;
        const hiddenMeshes = [];
        if (c.model && typeof c.model.traverse === "function") {
            c.model.traverse((n) => {
                if (n.isMesh && n.visible === false && n.name) {
                    hiddenMeshes.push(n.name);
                }
            });
        }
        return {
            version: 1,
            modelUrl: c.modelUrl || "",
            hero: c.hero || "",
            alt: c.alt || "",
            camera: {
                position: cam && cam.position ? [Number(cam.position.x.toFixed(4)), Number(cam.position.y.toFixed(4)), Number(cam.position.z.toFixed(4))] : [0, 55, 130],
                target: ctrl && ctrl.target ? [Number(ctrl.target.x.toFixed(4)), Number(ctrl.target.y.toFixed(4)), Number(ctrl.target.z.toFixed(4))] : [0, 45, 0],
                up: cam && cam.up ? [Number(cam.up.x.toFixed(4)), Number(cam.up.y.toFixed(4)), Number(cam.up.z.toFixed(4))] : [0, 1, 0],
                fov: cam && typeof cam.fov === "number" ? cam.fov : 45,
                orthographic: Boolean(cam && cam.isOrthographicCamera)
            },
            animation: {
                clipName: c.clipName || "",
                time: Number(((c.activeAction && c.activeAction.time) || c.animTime || 0).toFixed(4)),
                speed: typeof c.animSpeed === "number" ? c.animSpeed : 1.0,
                paused: Boolean(c.isPaused)
            },
            render: {
                wireframe: Boolean(c.wireframe),
                edges: Boolean(c.edges),
                skeleton: Boolean(c.skeleton),
                grid: c.grid !== false,
                coloringMode: c.coloringMode || "Lit",
                lightingPreset: c.lightingPreset || "legion_daylight",
                matcap: Boolean(c.matcap),
                uvChecker: Boolean(c.uvChecker),
                exposure: c.renderer && typeof c.renderer.toneMappingExposure === "number" ? c.renderer.toneMappingExposure : 1.25,
                hdriRotation: typeof c.hdriRotationDeg === "number" ? c.hdriRotationDeg : 0,
                hiddenMeshes
            }
        };
    }

    function encodeStatePermalinkHash(stateObj) {
        const json = JSON.stringify(stateObj || {});
        if (typeof btoa === "function") {
            const b64 = btoa(unescape(encodeURIComponent(json)))
                .replace(/\+/g, "-")
                .replace(/\//g, "_")
                .replace(/=+$/, "");
            return `#state=${b64}`;
        }
        return `#state=${encodeURIComponent(json)}`;
    }

    function decodeStatePermalinkHash(hashStr) {
        if (!hashStr || typeof hashStr !== "string") {
            return null;
        }
        const cleaned = hashStr.replace(/^#/, "");
        if (!cleaned.startsWith("state=")) {
            return null;
        }
        const rawPayload = cleaned.slice("state=".length);
        if (!rawPayload) {
            return null;
        }
        try {
            if (rawPayload.startsWith("{")) {
                return JSON.parse(decodeURIComponent(rawPayload));
            }
            const padded = rawPayload.replace(/-/g, "+").replace(/_/g, "/");
            const padLen = (4 - (padded.length % 4)) % 4;
            const b64 = padded + "=".repeat(padLen);
            const json = decodeURIComponent(escape(atob(b64)));
            return JSON.parse(json);
        } catch (err) {
            console.warn("[KONGOR-STATEFILE] Failed to decode permalink hash:", err);
            return null;
        }
    }

    function applyViewerState(stateObj, ctx) {
        if (!stateObj || typeof stateObj !== "object" || !ctx) {
            return false;
        }
        if (stateObj.camera && ctx.camera) {
            const pos = stateObj.camera.position;
            if (Array.isArray(pos) && pos.length === 3) {
                ctx.camera.position.set(Number(pos[0]) || 0, Number(pos[1]) || 0, Number(pos[2]) || 0);
            }
            const up = stateObj.camera.up;
            if (Array.isArray(up) && up.length === 3) {
                ctx.camera.up.set(Number(up[0]) || 0, Number(up[1]) || 1, Number(up[2]) || 0);
            }
            if (typeof stateObj.camera.fov === "number" && ctx.camera.isPerspectiveCamera) {
                ctx.camera.fov = stateObj.camera.fov;
                ctx.camera.updateProjectionMatrix();
            }
            if (ctx.controls && Array.isArray(stateObj.camera.target) && stateObj.camera.target.length === 3) {
                ctx.controls.target.set(
                    Number(stateObj.camera.target[0]) || 0,
                    Number(stateObj.camera.target[1]) || 0,
                    Number(stateObj.camera.target[2]) || 0
                );
                if (typeof ctx.controls.update === "function") {
                    ctx.controls.update();
                }
            }
        }
        if (stateObj.animation && typeof ctx.onApplyAnimation === "function") {
            ctx.onApplyAnimation(stateObj.animation);
        } else if (stateObj.animation && ctx.activeAction && ctx.mixer) {
            if (typeof stateObj.animation.time === "number") {
                ctx.activeAction.time = stateObj.animation.time;
                ctx.mixer.update(0);
            }
        }
        if (stateObj.render && typeof ctx.onApplyRender === "function") {
            ctx.onApplyRender(stateObj.render);
        }
        if (typeof ctx.requestRender === "function") {
            ctx.requestRender();
        }
        return true;
    }

    /**
     * #70 (Option RRR): Minimal transparent screenshot (Ctrl+F12), 5x exposure shutter flash (150ms),
     * and IEEE 802.3 CRC32-verified PNG tEXt (kongor_viewer_state) metadata chunk with drag-to-restore.
     */
    const CRC32_TABLE = (function () {
        const table = new Uint32Array(256);
        for (let i = 0; i < 256; i++) {
            let c = i;
            for (let k = 0; k < 8; k++) {
                c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
            }
            table[i] = c >>> 0;
        }
        return table;
    })();

    function computeCrc32IEEE8023(uint8Array, offset, length) {
        const start = offset || 0;
        const end = typeof length === "number" ? start + length : uint8Array.length;
        let crc = 0xFFFFFFFF;
        for (let i = start; i < end; i++) {
            crc = CRC32_TABLE[(crc ^ uint8Array[i]) & 0xFF] ^ (crc >>> 8);
        }
        return (crc ^ 0xFFFFFFFF) >>> 0;
    }

    function injectPngTextChunk(pngBytes, keyword, textValue) {
        const input = pngBytes instanceof Uint8Array ? pngBytes : new Uint8Array(pngBytes);
        if (input.length < 16 || input[0] !== 0x89 || input[1] !== 0x50 || input[2] !== 0x4E || input[3] !== 0x47) {
            throw new Error("Invalid PNG byte signature; cannot inject tEXt chunk.");
        }
        const keyStr = String(keyword || "kongor_viewer_state");
        const valStr = String(textValue || "");
        const keyBytes = new Uint8Array(keyStr.length);
        for (let i = 0; i < keyStr.length; i++) {
            keyBytes[i] = keyStr.charCodeAt(i) & 0x7F;
        }
        const valBytes = typeof TextEncoder !== "undefined"
            ? new TextEncoder().encode(valStr)
            : Uint8Array.from(valStr, (ch) => ch.charCodeAt(0) & 0xFF);

        const chunkDataLen = keyBytes.length + 1 + valBytes.length;
        const typeAndData = new Uint8Array(4 + chunkDataLen);
        typeAndData[0] = 0x74; // 't'
        typeAndData[1] = 0x45; // 'E'
        typeAndData[2] = 0x58; // 'X'
        typeAndData[3] = 0x74; // 't'
        typeAndData.set(keyBytes, 4);
        typeAndData[4 + keyBytes.length] = 0x00;
        typeAndData.set(valBytes, 5 + keyBytes.length);

        const crc = computeCrc32IEEE8023(typeAndData, 0, typeAndData.length);
        const fullChunk = new Uint8Array(4 + typeAndData.length + 4);
        const dv = new DataView(fullChunk.buffer);
        dv.setUint32(0, chunkDataLen, false);
        fullChunk.set(typeAndData, 4);
        dv.setUint32(4 + typeAndData.length, crc, false);

        // Locate IEND chunk to insert immediately before it
        let insertPos = input.length - 12;
        let offset = 8;
        const inputDv = new DataView(input.buffer, input.byteOffset, input.byteLength);
        while (offset + 8 <= input.length) {
            const len = inputDv.getUint32(offset, false);
            const c0 = input[offset + 4];
            const c1 = input[offset + 5];
            const c2 = input[offset + 6];
            const c3 = input[offset + 7];
            if (c0 === 0x49 && c1 === 0x45 && c2 === 0x4E && c3 === 0x44) {
                insertPos = offset;
                break;
            }
            offset += 12 + len;
        }

        const output = new Uint8Array(input.length + fullChunk.length);
        output.set(input.subarray(0, insertPos), 0);
        output.set(fullChunk, insertPos);
        output.set(input.subarray(insertPos), insertPos + fullChunk.length);
        return output;
    }

    function extractPngTextChunk(pngBytes, targetKeyword) {
        const input = pngBytes instanceof Uint8Array ? pngBytes : new Uint8Array(pngBytes);
        const expectedKey = String(targetKeyword || "kongor_viewer_state");
        if (input.length < 16 || input[0] !== 0x89 || input[1] !== 0x50 || input[2] !== 0x4E || input[3] !== 0x47) {
            return { valid: false, crc32: "FAIL", error: "Not a valid PNG file." };
        }
        const dv = new DataView(input.buffer, input.byteOffset, input.byteLength);
        let offset = 8;
        while (offset + 12 <= input.length) {
            const len = dv.getUint32(offset, false);
            if (offset + 12 + len > input.length) {
                break;
            }
            const typeStr = String.fromCharCode(
                input[offset + 4],
                input[offset + 5],
                input[offset + 6],
                input[offset + 7]
            );
            if (typeStr === "tEXt") {
                const storedCrc = dv.getUint32(offset + 8 + len, false) >>> 0;
                const computedCrc = computeCrc32IEEE8023(input, offset + 4, 4 + len);
                if (storedCrc !== computedCrc) {
                    const errMsg = `PNG tEXt CRC32 mismatch: expected 0x${storedCrc.toString(16)}, computed 0x${computedCrc.toString(16)}`;
                    console.error(`[PNG-STATE-RESTORE] crc32=FAIL error="${errMsg}"`);
                    return { valid: false, crc32: "FAIL", error: errMsg };
                }
                const dataSlice = input.subarray(offset + 8, offset + 8 + len);
                let nullIdx = -1;
                for (let i = 0; i < dataSlice.length; i++) {
                    if (dataSlice[i] === 0x00) {
                        nullIdx = i;
                        break;
                    }
                }
                if (nullIdx > 0) {
                    let key = "";
                    for (let i = 0; i < nullIdx; i++) {
                        key += String.fromCharCode(dataSlice[i]);
                    }
                    if (key === expectedKey || key === "KongorViewerState") {
                        const textBytes = dataSlice.subarray(nullIdx + 1);
                        const jsonText = typeof TextDecoder !== "undefined"
                            ? new TextDecoder("utf-8").decode(textBytes)
                            : String.fromCharCode.apply(null, Array.from(textBytes));
                        const parsed = JSON.parse(jsonText);
                        const camPos = (parsed.camera && Array.isArray(parsed.camera.position))
                            ? parsed.camera.position.join(",")
                            : "0,0,0";
                        const clipTime = (parsed.animation && typeof parsed.animation.time === "number")
                            ? parsed.animation.time.toFixed(3)
                            : "0.000";
                        console.info(`[PNG-STATE-RESTORE] crc32=PASS camera_pos=(${camPos}) clip_time=${clipTime}`);
                        return { valid: true, crc32: "PASS", keyword: key, text: jsonText, state: parsed };
                    }
                }
            }
            if (typeStr === "IEND") {
                break;
            }
            offset += 12 + len;
        }
        return { valid: false, crc32: "NONE", error: `No tEXt chunk '${expectedKey}' found in PNG.` };
    }

    function triggerShutterFlash(renderer, durationMs, requestRenderFn) {
        if (!renderer) {
            return;
        }
        const baseExposure = typeof renderer.toneMappingExposure === "number" ? renderer.toneMappingExposure : 1.25;
        const totalMs = typeof durationMs === "number" && durationMs > 0 ? durationMs : 150;
        const startTime = typeof performance !== "undefined" ? performance.now() : Date.now();
        renderer.toneMappingExposure = baseExposure * 5.0;
        if (typeof requestRenderFn === "function") {
            requestRenderFn();
        }
        function stepFlash() {
            const now = typeof performance !== "undefined" ? performance.now() : Date.now();
            const t = Math.min(1.0, (now - startTime) / totalMs);
            renderer.toneMappingExposure = baseExposure * (1.0 + 4.0 * (1.0 - t));
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            if (t < 1.0 && typeof requestAnimationFrame === "function") {
                requestAnimationFrame(stepFlash);
            } else {
                renderer.toneMappingExposure = baseExposure;
            }
        }
        if (typeof requestAnimationFrame === "function") {
            requestAnimationFrame(stepFlash);
        } else {
            renderer.toneMappingExposure = baseExposure;
        }
    }

    async function captureMinimalTransparentPng(ctx) {
        const c = ctx || {};
        const renderer = c.renderer;
        const scene = c.scene;
        const camera = c.camera;
        if (!renderer || !scene || !camera) {
            return null;
        }
        const minimal = c.minimal !== false;
        const savedBg = scene.background;
        const savedFog = scene.fog;
        const savedClearAlpha = typeof renderer.getClearAlpha === "function" ? renderer.getClearAlpha() : 1.0;
        const hiddenHelpers = [];

        if (minimal) {
            scene.background = null;
            scene.fog = null;
            if (typeof renderer.setClearAlpha === "function") {
                renderer.setClearAlpha(0.0);
            }
            if (Array.isArray(c.helpersToHide)) {
                c.helpersToHide.forEach((h) => {
                    if (h && h.visible) {
                        hiddenHelpers.push(h);
                        h.visible = false;
                    }
                });
            }
        }

        renderer.render(scene, camera);
        const dataUrl = renderer.domElement.toDataURL("image/png");

        if (minimal) {
            scene.background = savedBg;
            scene.fog = savedFog;
            if (typeof renderer.setClearAlpha === "function") {
                renderer.setClearAlpha(savedClearAlpha);
            }
            hiddenHelpers.forEach((h) => {
                h.visible = true;
            });
            renderer.render(scene, camera);
        }

        triggerShutterFlash(renderer, 150, c.requestRender);

        const b64 = dataUrl.split(",")[1] || "";
        const binStr = atob(b64);
        const rawBytes = new Uint8Array(binStr.length);
        for (let i = 0; i < binStr.length; i++) {
            rawBytes[i] = binStr.charCodeAt(i);
        }

        const stateObj = serializeViewerState(c);
        const taggedBytes = injectPngTextChunk(rawBytes, "kongor_viewer_state", JSON.stringify(stateObj));

        if (c.autoDownload !== false && typeof document !== "undefined") {
            const blob = new Blob([taggedBytes], { type: "image/png" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = `${c.filenamePrefix || c.hero || "kongor_model"}_${minimal ? "cutout" : "shot"}.png`;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
        return { pngBytes: taggedBytes, state: stateObj };
    }

    /**
     * #71 (Option SSS), #79 (Option AAAA), & #123 (Option SSSSS):
     * Single-key diagnostic render modes (E edge overlay, S/Ctrl+N shader cycle: Lit/Normal/UV/MatCap/VertexColor/BoneWeights),
     * MatCap sculpting curvature shader (M key), 4 Studio Lighting Rig presets & dedicated U UV checkerboard toggle,
     * slide-up toast, and searchable F1 cheatsheet modal.
     */
    const DIAGNOSTIC_SHADER_MODES = ["Lit", "Normal", "UV", "MatCap", "VertexColor", "BoneWeights"];

    const STUDIO_LIGHTING_PRESETS = {
        legion_daylight: {
            name: "Legion Daylight",
            ambientColor: 0x485870,
            ambientIntensity: 1.45,
            keyColor: 0xfff4e0,
            keyIntensity: 2.3,
            fillColor: 0x88bbff,
            fillIntensity: 1.25,
            rimColor: 0x38bdf8,
            rimIntensity: 2.1
        },
        hellbourne_lava_rim: {
            name: "Hellbourne Lava Rim",
            ambientColor: 0x3b1d2e,
            ambientIntensity: 1.25,
            keyColor: 0xffd2a6,
            keyIntensity: 2.1,
            fillColor: 0xf97316,
            fillIntensity: 1.45,
            rimColor: 0xef4444,
            rimIntensity: 3.1
        },
        neutral_clay: {
            name: "Neutral Clay Studio",
            ambientColor: 0x64748b,
            ambientIntensity: 1.65,
            keyColor: 0xffffff,
            keyIntensity: 2.0,
            fillColor: 0xcbd5e1,
            fillIntensity: 1.35,
            rimColor: 0x94a3b8,
            rimIntensity: 1.6
        },
        high_contrast_silhouette: {
            name: "High-Contrast Silhouette",
            ambientColor: 0x1e293b,
            ambientIntensity: 0.65,
            keyColor: 0xf8fafc,
            keyIntensity: 2.8,
            fillColor: 0x334155,
            fillIntensity: 0.45,
            rimColor: 0xc084fc,
            rimIntensity: 3.6
        }
    };

    let cachedUvCheckerTexture = null;
    function createProceduralUvCheckerTexture(size, squares) {
        const THREE = globalScope.THREE;
        if (!THREE) {
            return null;
        }
        if (cachedUvCheckerTexture) {
            return cachedUvCheckerTexture;
        }
        const dim = size || 256;
        const cells = squares || 16;
        const data = new Uint8Array(dim * dim * 4);
        const cellSize = dim / cells;
        for (let y = 0; y < dim; y++) {
            for (let x = 0; x < dim; x++) {
                const cx = Math.floor(x / cellSize);
                const cy = Math.floor(y / cellSize);
                const isEven = ((cx + cy) & 1) === 0;
                const u = x / (dim - 1);
                const v = y / (dim - 1);
                const base = isEven ? 235 : 55;
                const idx = (y * dim + x) * 4;
                data[idx] = Math.round(base * (0.55 + 0.45 * u));
                data[idx + 1] = Math.round(base * (0.55 + 0.45 * v));
                data[idx + 2] = Math.round(base * (0.85 - 0.3 * u));
                data[idx + 3] = 255;
            }
        }
        const tex = new THREE.DataTexture(data, dim, dim, THREE.RGBAFormat);
        configureTextureColorSpace(tex);
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        tex.needsUpdate = true;
        cachedUvCheckerTexture = tex;
        return tex;
    }

    let cachedMatcapTexture = null;
    function createProceduralMatcapTexture(size) {
        const THREE = globalScope.THREE;
        if (!THREE) {
            return null;
        }
        if (cachedMatcapTexture) {
            return cachedMatcapTexture;
        }
        const dim = size || 128;
        const data = new Uint8Array(dim * dim * 4);
        for (let y = 0; y < dim; y++) {
            for (let x = 0; x < dim; x++) {
                const nx = (x / (dim - 1)) * 2.0 - 1.0;
                const ny = (y / (dim - 1)) * 2.0 - 1.0;
                const r2 = nx * nx + ny * ny;
                const nz = r2 < 1.0 ? Math.sqrt(1.0 - r2) : 0.0;
                // Studio sculpting clay + curvature rim
                const keyDot = Math.max(0.0, nx * -0.35 + ny * 0.55 + nz * 0.75);
                const spec = Math.pow(Math.max(0.0, nz * 0.85 + ny * 0.45), 24.0) * 0.45;
                const rim = Math.pow(1.0 - nz, 3.0) * 0.35;
                const r = Math.min(255, Math.round((0.22 + 0.62 * keyDot + spec + rim * 0.9) * 235));
                const g = Math.min(255, Math.round((0.19 + 0.56 * keyDot + spec + rim * 0.6) * 220));
                const b = Math.min(255, Math.round((0.17 + 0.50 * keyDot + spec + rim * 0.5) * 205));
                const idx = (y * dim + x) * 4;
                data[idx] = r;
                data[idx + 1] = g;
                data[idx + 2] = b;
                data[idx + 3] = 255;
            }
        }
        const tex = new THREE.DataTexture(data, dim, dim, THREE.RGBAFormat);
        configureTextureColorSpace(tex);
        tex.needsUpdate = true;
        cachedMatcapTexture = tex;
        return tex;
    }

    function applyDiagnosticShaderMode(object3D, mode) {
        const THREE = globalScope.THREE;
        if (!object3D || !THREE) {
            return "Lit";
        }
        const targetMode = DIAGNOSTIC_SHADER_MODES.includes(mode) ? mode : "Lit";
        object3D.traverse((node) => {
            if (!node.isMesh || !node.material) {
                return;
            }
            node.userData = node.userData || {};
            if (!node.userData.kongorOrigMaterial) {
                node.userData.kongorOrigMaterial = node.material;
            }
            const origMat = node.userData.kongorOrigMaterial;
            if (targetMode === "Lit") {
                node.material = origMat;
            } else if (targetMode === "Normal") {
                node.material = new THREE.MeshNormalMaterial({
                    side: THREE.DoubleSide,
                    normalMap: origMat.normalMap || null
                });
            } else if (targetMode === "UV") {
                const checker = createProceduralUvCheckerTexture(256, 16);
                node.material = new THREE.MeshBasicMaterial({
                    map: checker,
                    side: THREE.DoubleSide
                });
            } else if (targetMode === "MatCap") {
                const matcapTex = createProceduralMatcapTexture(128);
                node.material = new THREE.MeshMatcapMaterial({
                    matcap: matcapTex,
                    normalMap: origMat.normalMap || null,
                    side: THREE.DoubleSide
                });
            } else if (targetMode === "VertexColor") {
                node.material = new THREE.MeshBasicMaterial({
                    vertexColors: Boolean(node.geometry && node.geometry.attributes && node.geometry.attributes.color),
                    color: 0x38bdf8,
                    side: THREE.DoubleSide
                });
            } else if (targetMode === "BoneWeights") {
                const weightMat = new THREE.MeshBasicMaterial({
                    color: 0xffffff,
                    side: THREE.DoubleSide
                });
                weightMat.onBeforeCompile = (shader) => {
                    shader.vertexShader = "varying vec4 vKongorSkinWeight;\n" + shader.vertexShader.replace(
                        "#include <skinbase_vertex>",
                        "#include <skinbase_vertex>\n#ifdef USE_SKINNING\nvKongorSkinWeight = skinWeight;\n#else\nvKongorSkinWeight = vec4(1.0, 0.0, 0.0, 0.0);\n#endif"
                    );
                    shader.fragmentShader = "varying vec4 vKongorSkinWeight;\n" + shader.fragmentShader.replace(
                        "vec4 diffuseColor = vec4( diffuse, opacity );",
                        "vec4 diffuseColor = vec4(vKongorSkinWeight.xyz * 0.85 + vec3(0.15, 0.1, 0.25) * vKongorSkinWeight.w, 1.0);"
                    );
                };
                node.material = weightMat;
            }
        });
        return targetMode;
    }

    function toggleEdgeOverlay(object3D, enabled) {
        const THREE = globalScope.THREE;
        if (!object3D || !THREE) {
            return false;
        }
        const showEdges = Boolean(enabled);
        object3D.traverse((node) => {
            if (!node.isMesh || !node.geometry) {
                return;
            }
            node.userData = node.userData || {};
            if (showEdges) {
                if (!node.userData.kongorEdgeOverlay) {
                    const edgeGeo = new THREE.EdgesGeometry(node.geometry, 24);
                    const edgeMat = new THREE.LineBasicMaterial({
                        color: 0x38bdf8,
                        transparent: true,
                        opacity: 0.72
                    });
                    applyProjectiveWireframeZPull(edgeMat);
                    const lines = new THREE.LineSegments(edgeGeo, edgeMat);
                    lines.name = "__kongor_edge_overlay__";
                    lines.raycast = () => {};
                    node.add(lines);
                    node.userData.kongorEdgeOverlay = lines;
                }
                node.userData.kongorEdgeOverlay.visible = true;
            } else if (node.userData.kongorEdgeOverlay) {
                node.userData.kongorEdgeOverlay.visible = false;
            }
        });
        return showEdges;
    }

    function applyStudioLightingPreset(lights, presetKey) {
        const preset = STUDIO_LIGHTING_PRESETS[presetKey] || STUDIO_LIGHTING_PRESETS.legion_daylight;
        if (!lights) {
            return preset;
        }
        if (lights.ambient) {
            lights.ambient.color.setHex(preset.ambientColor);
            lights.ambient.intensity = calibrateLightIntensity(preset.ambientIntensity);
        }
        if (lights.key) {
            lights.key.color.setHex(preset.keyColor);
            lights.key.intensity = calibrateLightIntensity(preset.keyIntensity);
        }
        if (lights.fill) {
            lights.fill.color.setHex(preset.fillColor);
            lights.fill.intensity = calibrateLightIntensity(preset.fillIntensity);
        }
        if (lights.rim) {
            lights.rim.color.setHex(preset.rimColor);
            lights.rim.intensity = calibrateLightIntensity(preset.rimIntensity);
        }
        return preset;
    }

    function showViewerToast(container, message, durationMs) {
        if (!container || typeof document === "undefined") {
            return;
        }
        let toast = container.querySelector(".kongor-viewer-toast");
        if (!toast) {
            toast = document.createElement("div");
            toast.className = "kongor-viewer-toast";
            toast.style.cssText = [
                "position:absolute",
                "bottom:18px",
                "left:50%",
                "transform:translate(-50%, 16px)",
                "background:rgba(15,23,42,0.92)",
                "color:#38bdf8",
                "border:1px solid rgba(56,189,248,0.45)",
                "padding:6px 14px",
                "border-radius:10px",
                "font:700 12px/1.3 ui-monospace, SFMono-Regular, Menlo, monospace",
                "pointer-events:none",
                "z-index:40",
                "opacity:0",
                "transition:opacity 180ms cubic-bezier(0.16,1,0.3,1), transform 180ms cubic-bezier(0.16,1,0.3,1)",
                "box-shadow:0 10px 25px rgba(0,0,0,0.5)"
            ].join(";");
            container.appendChild(toast);
        }
        toast.textContent = String(message || "");
        toast.style.opacity = "1";
        toast.style.transform = "translate(-50%, 0)";
        if (toast._hideTimer) {
            clearTimeout(toast._hideTimer);
        }
        toast._hideTimer = setTimeout(() => {
            toast.style.opacity = "0";
            toast.style.transform = "translate(-50%, 16px)";
        }, typeof durationMs === "number" ? durationMs : 2000);
    }

    /**
     * #72 (Option TTT): Interactive Scene Hierarchy Tree (H key) with per-submesh visibility/solo checkboxes,
     * vertex/triangle counts, material names, and texture thumbnails.
     */
    function inspectSceneHierarchy(rootObject) {
        const nodes = [];
        if (!rootObject || typeof rootObject.traverse !== "function") {
            return nodes;
        }
        const THREE = globalScope.THREE;
        rootObject.traverse((node) => {
            if (node.name === "__kongor_edge_overlay__") {
                return;
            }
            if (node.isMesh || node.isSkinnedMesh) {
                const geo = node.geometry;
                const verts = geo && geo.attributes && geo.attributes.position ? geo.attributes.position.count : 0;
                const tris = geo && geo.index ? Math.round(geo.index.count / 3) : Math.round(verts / 3);
                const mat = Array.isArray(node.material) ? node.material[0] : node.material;
                const textures = [];
                if (mat) {
                    ["map", "normalMap", "emissiveMap", "roughnessMap", "metalnessMap"].forEach((slot) => {
                        const tex = mat[slot];
                        if (tex && tex.image) {
                            textures.push({
                                slot,
                                width: tex.image.width || 0,
                                height: tex.image.height || 0
                            });
                        }
                    });
                }
                let dims = [0, 0, 0];
                if (THREE && geo) {
                    if (!geo.boundingBox && typeof geo.computeBoundingBox === "function") {
                        geo.computeBoundingBox();
                    }
                    if (geo.boundingBox) {
                        const sz = new THREE.Vector3();
                        geo.boundingBox.getSize(sz);
                        dims = [Number(sz.x.toFixed(1)), Number(sz.y.toFixed(1)), Number(sz.z.toFixed(1))];
                    }
                }
                nodes.push({
                    uuid: node.uuid,
                    name: node.name || `Submesh_${nodes.length + 1}`,
                    type: node.isSkinnedMesh ? "SkinnedMesh" : "Mesh",
                    visible: node.visible !== false,
                    vertices: verts,
                    triangles: tris,
                    dimensions: dims,
                    materialName: (mat && mat.name) || (mat && mat.type) || "MeshStandardMaterial",
                    alphaMode: mat && mat.transparent ? "BLEND" : (mat && mat.alphaTest > 0 ? "MASK" : "OPAQUE"),
                    doubleSided: Boolean(mat && mat.side === (THREE && THREE.DoubleSide)),
                    roughness: mat && typeof mat.roughness === "number" ? Number(mat.roughness.toFixed(2)) : 0.5,
                    metalness: mat && typeof mat.metalness === "number" ? Number(mat.metalness.toFixed(2)) : 0.2,
                    textures
                });
            }
        });
        return nodes;
    }

    function soloSubmesh(rootObject, targetUuidOrName) {
        if (!rootObject || typeof rootObject.traverse !== "function") {
            return;
        }
        const meshes = [];
        rootObject.traverse((n) => {
            if ((n.isMesh || n.isSkinnedMesh) && n.name !== "__kongor_edge_overlay__") {
                meshes.push(n);
            }
        });
        const target = meshes.find((m) => m.uuid === targetUuidOrName || m.name === targetUuidOrName);
        if (!target) {
            return;
        }
        const othersVisible = meshes.some((m) => m !== target && m.visible);
        if (othersVisible) {
            meshes.forEach((m) => {
                m.visible = (m === target);
            });
        } else {
            meshes.forEach((m) => {
                m.visible = true;
            });
        }
    }

    /**
     * #77 (Option YYY): 1-click WebM (vp9) animation clip & 360-deg turntable video recorder (Ctrl+Shift+R)
     * via canvas.captureStream(60).
     */
    function recordWebmShowcase(ctx, options) {
        const c = ctx || {};
        const opts = options || {};
        const canvas = c.canvas || (c.renderer && c.renderer.domElement);
        if (!canvas || typeof canvas.captureStream !== "function" || typeof MediaRecorder === "undefined") {
            console.warn("[WEBM-RECORDER] MediaRecorder or canvas.captureStream unavailable in this environment.");
            return Promise.resolve(null);
        }
        const fps = opts.fps || 60;
        const durationSec = opts.durationSec || (c.activeAction && c.activeAction.getClip ? c.activeAction.getClip().duration : 4.0) || 4.0;
        const mode = opts.mode || "turntable";
        const mimeType = MediaRecorder.isTypeSupported("video/webm;codecs=vp9")
            ? "video/webm;codecs=vp9"
            : "video/webm";

        const stream = canvas.captureStream(fps);
        const recorder = new MediaRecorder(stream, {
            mimeType,
            videoBitsPerSecond: 12000000
        });
        const chunks = [];
        recorder.ondataavailable = (e) => {
            if (e.data && e.data.size > 0) {
                chunks.push(e.data);
            }
        };

        return new Promise((resolve) => {
            const startAzimuth = c.camera && c.controls
                ? Math.atan2(c.camera.position.x - c.controls.target.x, c.camera.position.z - c.controls.target.z)
                : 0;
            const radius = c.camera && c.controls
                ? Math.hypot(c.camera.position.x - c.controls.target.x, c.camera.position.z - c.controls.target.z)
                : 120;
            const startTime = typeof performance !== "undefined" ? performance.now() : Date.now();

            recorder.onstop = () => {
                const blob = new Blob(chunks, { type: mimeType });
                if (opts.autoDownload !== false && typeof document !== "undefined") {
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement("a");
                    a.href = url;
                    a.download = `${c.hero || "kongor_model"}_${mode}.webm`;
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    setTimeout(() => URL.revokeObjectURL(url), 1000);
                }
                resolve(blob);
            };

            recorder.start();
            function stepTurntable() {
                const now = typeof performance !== "undefined" ? performance.now() : Date.now();
                const elapsed = (now - startTime) / 1000.0;
                if (elapsed >= durationSec) {
                    recorder.stop();
                    return;
                }
                if (mode === "turntable" && c.camera && c.controls) {
                    const angle = startAzimuth + (elapsed / durationSec) * Math.PI * 2.0;
                    c.camera.position.x = c.controls.target.x + Math.sin(angle) * radius;
                    c.camera.position.z = c.controls.target.z + Math.cos(angle) * radius;
                    c.camera.lookAt(c.controls.target);
                }
                if (typeof c.requestRender === "function") {
                    c.requestRender();
                }
                requestAnimationFrame(stepTurntable);
            }
            requestAnimationFrame(stepTurntable);
        });
    }

    /**
     * #82 (Option DDDD): Interactive Shift + Right-Click environment & key-light azimuth rotation (StartEnvRotate)
     * with HUD azimuth badge (0-360 deg).
     */
    function bindHdriAzimuthDrag(domElement, lights, scene, onAzimuthChange, requestRenderFn) {
        if (!domElement) {
            return null;
        }
        let isDraggingAzimuth = false;
        let lastX = 0;
        let azimuthDeg = 0;

        const basePositions = {};
        ["key", "fill", "rim"].forEach((slot) => {
            if (lights && lights[slot] && lights[slot].position) {
                basePositions[slot] = {
                    x: lights[slot].position.x,
                    y: lights[slot].position.y,
                    z: lights[slot].position.z
                };
            }
        });

        function setAzimuthDegrees(deg) {
            azimuthDeg = ((Number(deg) || 0) % 360 + 360) % 360;
            const rad = (azimuthDeg * Math.PI) / 180.0;
            const cosA = Math.cos(rad);
            const sinA = Math.sin(rad);
            ["key", "fill", "rim"].forEach((slot) => {
                const base = basePositions[slot];
                if (base && lights && lights[slot] && lights[slot].position) {
                    lights[slot].position.x = base.x * cosA - base.z * sinA;
                    lights[slot].position.z = base.x * sinA + base.z * cosA;
                }
            });
            if (scene && scene.environmentRotation && typeof scene.environmentRotation.set === "function") {
                scene.environmentRotation.set(0, rad, 0);
            }
            if (typeof onAzimuthChange === "function") {
                onAzimuthChange(Math.round(azimuthDeg));
            }
            if (typeof requestRenderFn === "function") {
                requestRenderFn();
            }
            return Math.round(azimuthDeg);
        }

        domElement.addEventListener("mousedown", (e) => {
            if (e.button === 2 && e.shiftKey) {
                e.preventDefault();
                e.stopPropagation();
                isDraggingAzimuth = true;
                lastX = e.clientX;
            }
        }, true);

        globalScope.addEventListener("mousemove", (e) => {
            if (!isDraggingAzimuth) {
                return;
            }
            const dx = e.clientX - lastX;
            lastX = e.clientX;
            setAzimuthDegrees(azimuthDeg + dx * 0.65);
        });

        globalScope.addEventListener("mouseup", () => {
            isDraggingAzimuth = false;
        });

        return {
            setAzimuthDegrees,
            getAzimuthDegrees: () => Math.round(azimuthDeg)
        };
    }

    /**
     * #83 (Option EEEE): Smart multi-type drag-and-drop (.glb load/Shift-merge, live .png/.webp diffuse/normal texture swap,
     * .png tEXt state restore, .json statefile restore).
     */
    function bindSmartMultiDragDrop(dropElement, handlers) {
        if (!dropElement || !handlers) {
            return;
        }
        dropElement.addEventListener("dragover", (e) => {
            e.preventDefault();
            if (e.dataTransfer) {
                e.dataTransfer.dropEffect = "copy";
            }
        });
        dropElement.addEventListener("drop", async (e) => {
            e.preventDefault();
            const files = e.dataTransfer && e.dataTransfer.files;
            if (!files || files.length === 0) {
                return;
            }
            const file = files[0];
            const lower = (file.name || "").toLowerCase();
            if (lower.endsWith(".glb") || lower.endsWith(".gltf")) {
                if (typeof handlers.onModelFile === "function") {
                    handlers.onModelFile(file, { mergeAttachment: Boolean(e.shiftKey) });
                }
            } else if (lower.endsWith(".json")) {
                const text = await file.text();
                const state = JSON.parse(text);
                if (typeof handlers.onRestoreState === "function") {
                    handlers.onRestoreState(state, file.name);
                }
            } else if (lower.endsWith(".png")) {
                const ab = await file.arrayBuffer();
                const extracted = extractPngTextChunk(new Uint8Array(ab), "kongor_viewer_state");
                if (extracted.valid && extracted.state) {
                    if (typeof handlers.onRestoreState === "function") {
                        handlers.onRestoreState(extracted.state, file.name);
                    }
                    return;
                }
                if (typeof handlers.onTextureFile === "function") {
                    const isNormal = lower.includes("_n.") || lower.includes("_normal.");
                    handlers.onTextureFile(file, { slot: isNormal ? "normalMap" : "map" });
                }
            } else if (lower.endsWith(".jpg") || lower.endsWith(".jpeg") || lower.endsWith(".webp")) {
                if (typeof handlers.onTextureFile === "function") {
                    const isNormal = lower.includes("_n.") || lower.includes("_normal.");
                    handlers.onTextureFile(file, { slot: isNormal ? "normalMap" : "map" });
                }
            }
        });
    }

    /**
     * #84 (Option FFFF): Viewer command console (: / Ctrl+K) with prefix autocomplete & Levenshtein (distance <= 2)
     * closest-match suggestions.
     */
    const DEFAULT_VIEWER_COMMANDS = [
        "reset_camera",
        "set_camera",
        "set_animation_time",
        "toggle_edges",
        "toggle_wireframe",
        "toggle_uv_checker",
        "toggle_matcap",
        "set_shader",
        "set_lighting",
        "solo_mesh",
        "capture_minimal",
        "record_turntable",
        "sample_storyboard",
        "toggle_watch"
    ];

    function computeLevenshteinDistance(a, b) {
        const s = String(a || "").toLowerCase();
        const t = String(b || "").toLowerCase();
        const m = s.length;
        const n = t.length;
        if (m === 0) return n;
        if (n === 0) return m;
        const dp = Array.from({ length: m + 1 }, () => new Int32Array(n + 1));
        for (let i = 0; i <= m; i++) dp[i][0] = i;
        for (let j = 0; j <= n; j++) dp[0][j] = j;
        for (let i = 1; i <= m; i++) {
            for (let j = 1; j <= n; j++) {
                const cost = s[i - 1] === t[j - 1] ? 0 : 1;
                dp[i][j] = Math.min(
                    dp[i - 1][j] + 1,
                    dp[i][j - 1] + 1,
                    dp[i - 1][j - 1] + cost
                );
            }
        }
        return dp[m][n];
    }

    function resolveViewerConsoleCommand(inputLine, customCatalog) {
        const catalog = Array.isArray(customCatalog) && customCatalog.length > 0
            ? customCatalog
            : DEFAULT_VIEWER_COMMANDS;
        const trimmed = String(inputLine || "").trim().replace(/^:/, "");
        if (!trimmed) {
            return { ok: false, command: "", args: [], suggestions: catalog.slice(0, 6), message: "Type a command..." };
        }
        const parts = trimmed.split(/\s+/);
        const cmd = parts[0].toLowerCase();
        const args = parts.slice(1);

        if (catalog.includes(cmd)) {
            return {
                ok: true,
                command: cmd,
                args,
                distance: 0,
                suggestions: catalog.filter((c) => c.startsWith(cmd)),
                message: `Executed '${cmd}'`
            };
        }

        const prefixMatches = catalog.filter((c) => c.startsWith(cmd));
        let bestCandidate = null;
        let bestDist = Infinity;
        for (let i = 0; i < catalog.length; i++) {
            const d = computeLevenshteinDistance(cmd, catalog[i]);
            if (d < bestDist) {
                bestDist = d;
                bestCandidate = catalog[i];
            }
        }

        if (bestCandidate && bestDist <= 2) {
            return {
                ok: false,
                command: cmd,
                args,
                suggestion: bestCandidate,
                distance: bestDist,
                suggestions: prefixMatches.length > 0 ? prefixMatches : [bestCandidate],
                message: `Unknown command '${cmd}'. Did you mean '${bestCandidate}'? (levenshtein=${bestDist})`
            };
        }

        return {
            ok: false,
            command: cmd,
            args,
            suggestion: null,
            distance: bestDist,
            suggestions: prefixMatches,
            message: `Unknown command '${cmd}'.`
        };
    }

    /**
     * #88 (Option MMMM) & #78 (Option ZZZ):
     * Zero-blanking hot-reload with AbortController/Cut cancellation, playhead/camera preservation,
     * translucent #1a1416/#e05561 error overlay over the last good frame, and live --watch polling.
     */
    function renderZeroBlankErrorBanner(container, errorMsg) {
        if (!container || typeof document === "undefined") {
            return;
        }
        let banner = container.querySelector(".kongor-zero-blank-error");
        if (!errorMsg) {
            if (banner) {
                banner.style.display = "none";
            }
            return;
        }
        if (!banner) {
            banner = document.createElement("div");
            banner.className = "kongor-zero-blank-error";
            banner.style.cssText = [
                "position:absolute",
                "top:58px",
                "left:16px",
                "right:16px",
                "background:rgba(26,20,22,0.92)",
                "border:1px solid #6b2d38",
                "color:#f8fafc",
                "padding:10px 14px",
                "border-radius:12px",
                "font:600 12px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace",
                "z-index:35",
                "display:flex",
                "align-items:center",
                "gap:10px",
                "backdrop-filter:blur(8px)"
            ].join(";");
            container.appendChild(banner);
        }
        banner.textContent = "";
        const badgeEl = document.createElement("span");
        badgeEl.style.cssText = "background:#e05561;color:#1a1416;font-weight:800;padding:2px 7px;border-radius:6px;font-size:10px;";
        badgeEl.textContent = "HOT-RELOAD ERROR";
        const msgEl = document.createElement("span");
        msgEl.textContent = String(errorMsg);
        banner.append(badgeEl, msgEl);
        banner.style.display = "flex";
    }

    /**
     * #98 (Option TTTT): Fixed-timestamp declarative scene evaluation (evaluateAtTime(t)) with early Cut termination
     * after capturing targeted -t animation timestamps into a 2x3 contact sheet.
     */
    function sampleFixedTimeStoryboard(ctx, timestamps) {
        const c = ctx || {};
        const marks = Array.isArray(timestamps) && timestamps.length > 0
            ? timestamps.slice().map(Number).filter(Number.isFinite).sort((a, b) => a - b)
            : [0.0, 0.2, 0.4, 0.6, 0.8, 1.0];
        const maxMark = marks.length > 0 ? marks[marks.length - 1] : 0.0;
        const capturedTiles = [];

        const savedPaused = c.activeAction ? c.activeAction.paused : true;
        const savedTime = c.activeAction ? c.activeAction.time : 0;

        for (let i = 0; i < marks.length && i < 6; i++) {
            const t = marks[i];
            if (c.activeAction && c.mixer) {
                c.activeAction.paused = true;
                c.activeAction.time = t;
                c.mixer.update(0);
            }
            if (c.renderer && c.scene && c.camera) {
                c.renderer.render(c.scene, c.camera);
            }
            capturedTiles.push({
                timestamp: t,
                dataUrl: c.renderer && c.renderer.domElement && typeof c.renderer.domElement.toDataURL === "function"
                    ? c.renderer.domElement.toDataURL("image/png")
                    : ""
            });
            if (t >= maxMark) {
                console.info(`[STORYBOARD-CUT] Early Cut termination after final target timestamp t=${t.toFixed(3)}s (tiles=${capturedTiles.length})`);
                break;
            }
        }

        if (c.activeAction && c.mixer) {
            c.activeAction.time = savedTime;
            c.activeAction.paused = savedPaused;
            c.mixer.update(0);
            if (c.renderer && c.scene && c.camera) {
                c.renderer.render(c.scene, c.camera);
            }
        }

        return {
            cols: 3,
            rows: 2,
            cutAtTimestamp: maxMark,
            tiles: capturedTiles
        };
    }

    /**
     * Ensures local vendored Three.js r16x ESM + OrbitControls + GLTFLoader + DRACOLoader + MeshoptDecoder
     * are available on window.THREE with zero external CDN requests (#120 Step 6.5b, #86).
     */
    async function ensureLocalThreeLoaded(basePath) {
        if (
            globalScope.THREE &&
            globalScope.THREE.REVISION === "168" &&
            globalScope.THREE.GLTFLoader &&
            globalScope.THREE.OrbitControls &&
            globalScope.THREE.DRACOLoader
        ) {
            return globalScope.THREE;
        }

        const root = basePath || "/lib/three";
        const loadLocalScript = (src) => new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = src;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });

        await loadLocalScript(`${root}/three.r16x.bundle.js`);
        return globalScope.THREE;
    }

    function initKongorViewerCore(viewerName, overrides) {
        const opts = mergeOptions(overrides);
        console.info(
            `[KONGOR-3D-CORE] Initialized Three.js ${opts.threeRevision} (local ESM, importmap=active, colorSpace=${opts.colorSpace}) viewer=${viewerName || "default"}`
        );
        return {
            options: opts,
            configureRendererColorSpace: (renderer) => configureRendererColorSpace(renderer, opts),
            configureTextureColorSpace,
            calibrateLightIntensity,
            createKongorGLTFLoader: () => createKongorGLTFLoader(opts),
            resetToBounds,
            orthogonalizeViewUp,
            disposeObject3DResources,
            bindRaycastFocalPivotAndHotkeys,
            createFwidthRadialFloorGrid,
            alignFloorGridToBounds,
            alignModelToCameraAndFloor,
            bindDollyToCursorAndElevationClamp,
            applyStochasticIgnAlpha,
            applyFilamentPbrShaderUpgrades,
            observeDevicePixelCanvasResize,
            createWakeRenderScheduler,
            createTwoFactorLruGpuCache,
            snapDirectionalShadowFrustum,
            applyProjectiveWireframeZPull,
            extractClipKeyframeTimes,
            snapTimeToKeyframe,
            stepAnimationFrame,
            jumpToAdjacentKeyframe,
            serializeViewerState,
            encodeStatePermalinkHash,
            decodeStatePermalinkHash,
            applyViewerState,
            computeCrc32IEEE8023,
            injectPngTextChunk,
            extractPngTextChunk,
            triggerShutterFlash,
            captureMinimalTransparentPng,
            createProceduralUvCheckerTexture,
            createProceduralMatcapTexture,
            applyDiagnosticShaderMode,
            toggleEdgeOverlay,
            applyStudioLightingPreset,
            showViewerToast,
            inspectSceneHierarchy,
            soloSubmesh,
            recordWebmShowcase,
            bindHdriAzimuthDrag,
            bindSmartMultiDragDrop,
            computeLevenshteinDistance,
            resolveViewerConsoleCommand,
            renderZeroBlankErrorBanner,
            sampleFixedTimeStoryboard
        };
    }

    const KongorViewerCore = {
        DEFAULTS: KONGOR_VIEWER_DEFAULTS,
        ALL_TEXTURE_MAP_SLOTS,
        DIAGNOSTIC_SHADER_MODES,
        STUDIO_LIGHTING_PRESETS,
        DEFAULT_VIEWER_COMMANDS,
        STOCHASTIC_IGN_GLSL,
        TOKUYOSHI_SPECULAR_AA_GLSL,
        JIMENEZ_MULTIBOUNCE_GTAO_GLSL,
        PROJECTIVE_WIREFRAME_ZPULL_GLSL,
        mergeOptions,
        configureRendererColorSpace,
        configureTextureColorSpace,
        calibrateLightIntensity,
        createKongorGLTFLoader,
        orthogonalizeViewUp,
        resetToBounds,
        disposeObject3DResources,
        easeCosineF3D,
        animateFocalPivot,
        snapCameraAxisView,
        bindRaycastFocalPivotAndHotkeys,
        computeAutoGridUnitSquare,
        createFwidthRadialFloorGrid,
        alignFloorGridToBounds,
        alignModelToCameraAndFloor,
        dollyToPosition,
        bindDollyToCursorAndElevationClamp,
        applyStochasticIgnAlpha,
        computeKarisBloomWeight,
        applyFilamentPbrShaderUpgrades,
        observeDevicePixelCanvasResize,
        createWakeRenderScheduler,
        createTwoFactorLruGpuCache,
        computeMacroblockDamageMask16x16,
        quantizeQuarterPowerOfTwo,
        snapDirectionalShadowFrustum,
        applyProjectiveWireframeZPull,
        extractClipKeyframeTimes,
        snapTimeToKeyframe,
        stepAnimationFrame,
        jumpToAdjacentKeyframe,
        serializeViewerState,
        encodeStatePermalinkHash,
        decodeStatePermalinkHash,
        applyViewerState,
        computeCrc32IEEE8023,
        injectPngTextChunk,
        extractPngTextChunk,
        triggerShutterFlash,
        captureMinimalTransparentPng,
        createProceduralUvCheckerTexture,
        createProceduralMatcapTexture,
        applyDiagnosticShaderMode,
        toggleEdgeOverlay,
        applyStudioLightingPreset,
        showViewerToast,
        inspectSceneHierarchy,
        soloSubmesh,
        recordWebmShowcase,
        bindHdriAzimuthDrag,
        bindSmartMultiDragDrop,
        computeLevenshteinDistance,
        resolveViewerConsoleCommand,
        renderZeroBlankErrorBanner,
        sampleFixedTimeStoryboard,
        ensureLocalThreeLoaded,
        initKongorViewerCore
    };

    globalScope.KongorViewerCore = KongorViewerCore;
})(typeof window !== "undefined" ? window : globalThis);

