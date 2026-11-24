// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

/**
 * Project Kongor: Standalone 3D Model & Animation Quick-Viewer
 * Built with Raylib 5.5 + Modern C++17
 *
 * Sub-second previewer for glTF/GLB/OBJ models, skeletal animations,
 * bone hierarchies, and materials exported from HoN model tools.
 */

#include <raylib.h>
#include <raymath.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "kongor_simd_rig.h"

namespace fs = std::filesystem;

#if defined(_WIN32)
extern "C" {
    __declspec(dllimport) int __stdcall SetForegroundWindow(void* hWnd);
    __declspec(dllimport) int __stdcall BringWindowToTop(void* hWnd);
    __declspec(dllimport) int __stdcall ShowWindow(void* hWnd, int nCmdShow);
    __declspec(dllimport) int __stdcall SetWindowPos(void* hWnd, void* hWndInsertAfter, int X, int Y, int cx, int cy, unsigned int uFlags);
    __declspec(dllimport) int __stdcall SwitchToThisWindow(void* hWnd, int fUnknown);
}
#endif

// ----------------------------------------------------------------------------
// Logging System (Kongor Telemetry Standard)
// ----------------------------------------------------------------------------
class Logger {
public:
    static void Init(const std::string& logFilePath) {
        fs::path logPath(logFilePath);
        if (logPath.has_parent_path()) {
            std::error_code ec;
            fs::create_directories(logPath.parent_path(), ec);
        }
        s_LogFile.open(logFilePath, std::ios::out | std::ios::app);
        Log(LOG_INFO, "==========================================================");
        Log(LOG_INFO, "  Kongor Model Viewer: Sub-second 3D Asset Inspector");
        Log(LOG_INFO, "==========================================================");
    }

    enum Level { LOG_DEBUG, LOG_INFO, LOG_WARN, LOG_ERR };

    static void Log(Level level, const std::string& msg) {
        auto now = std::chrono::system_clock::now();
        auto in_time_t = std::chrono::system_clock::to_time_t(now);
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;

        std::tm tm_buf;
#if defined(_WIN32)
        localtime_s(&tm_buf, &in_time_t);
#else
        localtime_r(&in_time_t, &tm_buf);
#endif

        const char* lvlStr = "[INFO]";
        if (level == LOG_DEBUG) lvlStr = "[DEBUG]";
        else if (level == LOG_WARN) lvlStr = "[WARN]";
        else if (level == LOG_ERR) lvlStr = "[ERROR]";

        std::ostringstream ss;
        ss << std::put_time(&tm_buf, "%Y-%m-%d %H:%M:%S.")
           << std::setfill('0') << std::setw(3) << ms.count()
           << " " << lvlStr << " " << msg;

        std::string line = ss.str();
        std::cout << line << std::endl;
        if (s_LogFile.is_open()) {
            s_LogFile << line << std::endl;
            s_LogFile.flush();
        }
    }

    static void Shutdown() {
        if (s_LogFile.is_open()) {
            Log(LOG_INFO, "Kongor Model Viewer shutting down cleanly.");
            s_LogFile.close();
        }
    }

private:
    static inline std::ofstream s_LogFile;
};

// ----------------------------------------------------------------------------
// Orbit Camera Controller
// ----------------------------------------------------------------------------
struct OrbitCamera {
    Camera3D camera;
    Vector3 targetPivot;
    float distance;
    float yaw;      // radians
    float pitch;    // radians
    float defaultDistance;
    Vector3 defaultPivot;

    void Init(Vector3 pivot, float initialDist) {
        defaultPivot = pivot;
        targetPivot = pivot;
        defaultDistance = initialDist;
        distance = initialDist;
        yaw = 45.0f * DEG2RAD;
        pitch = 30.0f * DEG2RAD;

        camera = { 0 };
        camera.up = Vector3{ 0.0f, 1.0f, 0.0f };
        camera.fovy = 45.0f;
        camera.projection = CAMERA_PERSPECTIVE;
        UpdateVectors();
    }

    void Reset() {
        targetPivot = defaultPivot;
        distance = defaultDistance;
        yaw = 45.0f * DEG2RAD;
        pitch = 30.0f * DEG2RAD;
        UpdateVectors();
        Logger::Log(Logger::LOG_INFO, "Camera reset to default framed pivot.");
    }

    void Update() {
        // Orbit with Right Mouse Button or Left Mouse Button + Alt
        if (IsMouseButtonDown(MOUSE_BUTTON_RIGHT) || (IsMouseButtonDown(MOUSE_BUTTON_LEFT) && IsKeyDown(KEY_LEFT_ALT))) {
            Vector2 delta = GetMouseDelta();
            yaw -= delta.x * 0.005f;
            pitch += delta.y * 0.005f;

            // Clamp pitch to avoid gimbal flip
            const float maxPitch = 89.0f * DEG2RAD;
            if (pitch > maxPitch) pitch = maxPitch;
            if (pitch < -maxPitch) pitch = -maxPitch;
        }

        // Pan with Middle Mouse Button or Shift + Right Mouse Button
        if (IsMouseButtonDown(MOUSE_BUTTON_MIDDLE) || (IsMouseButtonDown(MOUSE_BUTTON_RIGHT) && IsKeyDown(KEY_LEFT_SHIFT))) {
            Vector2 delta = GetMouseDelta();
            float panSpeed = distance * 0.0015f;

            Vector3 forward = Vector3Normalize(Vector3Subtract(targetPivot, camera.position));
            Vector3 right = Vector3Normalize(Vector3CrossProduct(forward, camera.up));
            Vector3 camUp = Vector3Normalize(Vector3CrossProduct(right, forward));

            targetPivot = Vector3Add(targetPivot, Vector3Scale(right, -delta.x * panSpeed));
            targetPivot = Vector3Add(targetPivot, Vector3Scale(camUp, delta.y * panSpeed));
        }

        // Zoom with Mouse Wheel
        float wheel = GetMouseWheelMove();
        if (wheel != 0.0f) {
            distance -= wheel * (distance * 0.12f);
            float minDist = std::max(0.0001f, defaultDistance * 0.01f);
            float maxDist = std::max(500.0f, defaultDistance * 50.0f);
            if (distance < minDist) distance = minDist;
            if (distance > maxDist) distance = maxDist;
        }

        UpdateVectors();
    }

    void UpdateVectors() {
        camera.target = targetPivot;
        camera.position.x = targetPivot.x + distance * cosf(pitch) * sinf(yaw);
        camera.position.y = targetPivot.y + distance * sinf(pitch);
        camera.position.z = targetPivot.z + distance * cosf(pitch) * cosf(yaw);
    }
};

// ----------------------------------------------------------------------------
// Model & Animation State
// ----------------------------------------------------------------------------
struct ViewerState {
    std::string currentFilePath;
    Model model = { 0 };
    bool isModelLoaded = false;
    BoundingBox bounds = { 0 };
    Vector3 boundsCenter = { 0 };
    Vector3 boundsSize = { 0 };
    int totalVertices = 0;
    int totalTriangles = 0;

    // Animations
    ModelAnimation* animations = nullptr;
    int animCount = 0;
    int currentAnimIndex = 0;
    float currentAnimFrame = 0.0f;
    bool isPlaying = true;
    bool isLooping = true;
    float playbackSpeed = 1.0f;
    float animFps = 30.0f;

    // View Options
    bool showWireframe = false;
    bool showBounds = false;
    bool showGrid = true;
    bool showAxes = true;
    bool showHud = true;
    bool autoNormalize = false;
    int bgThemeIndex = 0; // 0=Dark Charcoal, 1=Deep Slate, 2=Navy, 3=Light Gray

    void UnloadCurrent() {
        if (isModelLoaded) {
            Logger::Log(Logger::LOG_INFO, "Unloading active model: " + currentFilePath);
            if (animations != nullptr && animCount > 0) {
                UnloadModelAnimations(animations, animCount);
                animations = nullptr;
                animCount = 0;
                currentAnimIndex = 0;
                currentAnimFrame = 0.0f;
            }
            UnloadModel(model);
            model = { 0 };
            isModelLoaded = false;
            totalVertices = 0;
            totalTriangles = 0;
        }
    }

    bool Load(std::string path, OrbitCamera& camera) {
        UnloadCurrent();

        // Strip enclosing quotes if present
        if (path.size() >= 2 && path.front() == '"' && path.back() == '"') {
            path = path.substr(1, path.size() - 2);
        }

        if (!fs::exists(path)) {
            Logger::Log(Logger::LOG_ERR, "File does not exist: " + path);
            return false;
        }

        Logger::Log(Logger::LOG_INFO, "Loading 3D asset: " + path);
        currentFilePath = path;

        model = LoadModel(path.c_str());
        if (model.meshCount <= 0) {
            Logger::Log(Logger::LOG_ERR, "Model load failed or contains 0 meshes: " + path);
            return false;
        }

        isModelLoaded = true;

        // Calculate statistics and bounding box
        bounds = GetModelBoundingBox(model);
        boundsCenter = Vector3Scale(Vector3Add(bounds.min, bounds.max), 0.5f);
        boundsSize = Vector3Subtract(bounds.max, bounds.min);

        totalVertices = 0;
        totalTriangles = 0;
        for (int i = 0; i < model.meshCount; i++) {
            totalVertices += model.meshes[i].vertexCount;
            totalTriangles += model.meshes[i].triangleCount;
        }

        Logger::Log(Logger::LOG_INFO, "Model Loaded Successfully:");
        Logger::Log(Logger::LOG_INFO, "  - Meshes: " + std::to_string(model.meshCount));
        Logger::Log(Logger::LOG_INFO, "  - Total Vertices: " + std::to_string(totalVertices));
        Logger::Log(Logger::LOG_INFO, "  - Total Triangles: " + std::to_string(totalTriangles));
        Logger::Log(Logger::LOG_INFO, "  - Materials: " + std::to_string(model.materialCount));
        Logger::Log(Logger::LOG_INFO, "  - Bone Count: " + std::to_string(model.boneCount));
        Logger::Log(Logger::LOG_INFO, "  - Bounds Size: (" +
            std::to_string(boundsSize.x) + ", " +
            std::to_string(boundsSize.y) + ", " +
            std::to_string(boundsSize.z) + ")");

        // Load Animations
        int count = 0;
        animations = LoadModelAnimations(path.c_str(), &count);
        animCount = count;
        currentAnimIndex = 0;
        currentAnimFrame = 0.0f;

        if (animCount > 0) {
            Logger::Log(Logger::LOG_INFO, "Discovered " + std::to_string(animCount) + " animation track(s):");
            for (int i = 0; i < animCount; i++) {
                std::string trackName = animations[i].name[0] ? animations[i].name : "Track_" + std::to_string(i);
                Logger::Log(Logger::LOG_INFO, "  [" + std::to_string(i + 1) + "/" + std::to_string(animCount) + "] " +
                    trackName + " (" + std::to_string(animations[i].frameCount) + " frames, " +
                    std::to_string(animations[i].boneCount) + " bones)");
            }
        } else {
            Logger::Log(Logger::LOG_INFO, "No skeletal animation tracks embedded in asset.");
        }

        // Check for unbaked armature scaling in glTF (e.g. Mixamo/Blender 0.01 scale)
        if (animCount > 0 && model.boneCount > 0) {
            for (int b = 0; b < model.boneCount; b++) {
                if (model.bones[b].parent == -1) {
                    float bpScale = model.bindPose[b].scale.x;
                    float fpScale = animations[0].framePoses[0][b].scale.x;
                    if (std::abs(bpScale - fpScale) > 1e-4f) {
                        Logger::Log(Logger::LOG_WARN, "Unbaked armature scale detected (bindPose scale: " +
                            std::to_string(bpScale) + ", framePose scale: " + std::to_string(fpScale) +
                            "). If skeletal deformation occurs, run 'python scripts/assets/fix_mixamo_glb.py --in-place " +
                            currentFilePath + "' to bake scale to 1.0.");
                        break;
                    }
                }
            }
        }

        // Compute bounds from actual vertex data
        auto ComputeMeshBounds = [](const Model& m) -> BoundingBox {
            BoundingBox box;
            box.min = Vector3{ 1e9f, 1e9f, 1e9f };
            box.max = Vector3{ -1e9f, -1e9f, -1e9f };
            for (int mi = 0; mi < m.meshCount; mi++) {
                const Mesh& mesh = m.meshes[mi];
                float* verts = mesh.animVertices ? mesh.animVertices : mesh.vertices;
                if (!verts) continue;
                for (int v = 0; v < mesh.vertexCount * 3; v += 3) {
                    box.min.x = std::min(box.min.x, verts[v]);
                    box.min.y = std::min(box.min.y, verts[v + 1]);
                    box.min.z = std::min(box.min.z, verts[v + 2]);
                    box.max.x = std::max(box.max.x, verts[v]);
                    box.max.y = std::max(box.max.y, verts[v + 1]);
                    box.max.z = std::max(box.max.z, verts[v + 2]);
                }
            }
            if (box.min.x > box.max.x) {
                box.min = Vector3{ -1.0f, -1.0f, -1.0f };
                box.max = Vector3{ 1.0f, 1.0f, 1.0f };
            }
            return box;
        };

        bounds = ComputeMeshBounds(model);
        boundsCenter = Vector3Scale(Vector3Add(bounds.min, bounds.max), 0.5f);
        boundsSize = Vector3Subtract(bounds.max, bounds.min);
        Logger::Log(Logger::LOG_INFO, "Initial Mesh Bounds: min=(" +
            std::to_string(bounds.min.x) + ", " + std::to_string(bounds.min.y) + ", " + std::to_string(bounds.min.z) + ") max=(" +
            std::to_string(bounds.max.x) + ", " + std::to_string(bounds.max.y) + ", " + std::to_string(bounds.max.z) + ")");

        // Test one animation frame if animation exists
        if (animCount > 0) {
            UpdateModelAnimation(model, animations[0], 0);
            BoundingBox animB = ComputeMeshBounds(model);
            Logger::Log(Logger::LOG_INFO, "Post-UpdateModelAnimation Bounds: min=(" +
                std::to_string(animB.min.x) + ", " + std::to_string(animB.min.y) + ", " + std::to_string(animB.min.z) + ") max=(" +
                std::to_string(animB.max.x) + ", " + std::to_string(animB.max.y) + ", " + std::to_string(animB.max.z) + ")");
            bounds = animB;
            boundsCenter = Vector3Scale(Vector3Add(bounds.min, bounds.max), 0.5f);
            boundsSize = Vector3Subtract(bounds.max, bounds.min);
        }

        // Auto-frame camera
        float maxDim = std::max({ boundsSize.x, boundsSize.y, boundsSize.z });
        if (maxDim < 0.1f && maxDim > 0.00001f) {
            autoNormalize = true;
            Logger::Log(Logger::LOG_INFO, "Miniature export units detected (bounds < 0.1m). Auto-normalized scale to 2.0m [Toggle: N].");
        } else {
            autoNormalize = false;
        }

        float effectiveDim = autoNormalize ? 2.0f : (maxDim < 0.01f ? 2.0f : maxDim);
        Vector3 effectiveCenter = autoNormalize ? Vector3{ 0.0f, 1.0f, 0.0f } : boundsCenter;
        camera.Init(effectiveCenter, effectiveDim * 2.2f);

        return true;
    }

    void UpdateAnimation(float dt) {
        if (!isModelLoaded || animCount == 0 || animations == nullptr) return;

        if (isPlaying) {
            currentAnimFrame += animFps * playbackSpeed * dt;
            int maxFrames = animations[currentAnimIndex].frameCount;

            if (currentAnimFrame >= static_cast<float>(maxFrames)) {
                if (isLooping) {
                    currentAnimFrame = fmodf(currentAnimFrame, static_cast<float>(maxFrames));
                } else {
                    currentAnimFrame = static_cast<float>(maxFrames - 1);
                    isPlaying = false;
                }
            } else if (currentAnimFrame < 0.0f) {
                if (isLooping) {
                    currentAnimFrame = static_cast<float>(maxFrames - 1);
                } else {
                    currentAnimFrame = 0.0f;
                    isPlaying = false;
                }
            }
        }

        int frameToApply = static_cast<int>(currentAnimFrame);
        UpdateModelAnimation(model, animations[currentAnimIndex], frameToApply);
    }
};

// ----------------------------------------------------------------------------
// Color Themes
// ----------------------------------------------------------------------------
Color GetBackgroundColor(int themeIndex) {
    switch (themeIndex) {
        case 1: return Color{ 24, 28, 36, 255 };    // Deep Slate
        case 2: return Color{ 12, 16, 28, 255 };    // Midnight Navy
        case 3: return Color{ 200, 204, 210, 255 }; // Light Studio Gray
        case 0:
        default: return Color{ 18, 18, 20, 255 };   // Dark Charcoal
    }
}

// ----------------------------------------------------------------------------
// Main Application Entry
// ----------------------------------------------------------------------------
int main(int argc, char* argv[]) {
    // Determine log file path
    std::string logPath = "logs/kongor_model_viewer.log";
    Logger::Init(logPath);

    std::string initialModelPath = "";
    int targetAnimIndex = 0;
    float initialSpeed = 1.0f;
    bool initialWireframe = false;
    bool initialGrid = true;
    bool headlessTest = false;

    std::string screenshotPath = "";

    // Parse command line arguments
    for (int i = 1; i < argc; i++) {
        std::string arg = argv[i];
        if (arg == "--help" || arg == "-h") {
            std::cout << "==========================================================\n"
                      << "  Kongor Model Viewer: Sub-Second 3D Asset Inspector\n"
                      << "==========================================================\n"
                      << "Usage: kongor-model-viewer.exe [model_path] [options]\n\n"
                      << "Options:\n"
                      << "  [path]              Path to .glb, .gltf, .obj file to open\n"
                      << "  --anim <idx>        Initial animation track index (0-based)\n"
                      << "  --speed <float>     Initial playback speed (e.g. 1.0, 0.5)\n"
                      << "  --wireframe         Start in wireframe mode\n"
                      << "  --no-grid           Disable ground grid\n"
                      << "  --headless-test     Run 10 verification frames and exit (CI/Test)\n"
                      << "  --screenshot <path> Capture frame to PNG and exit\n"
                      << "  --benchmark-rig [n] Benchmark SIMD vs Scalar bone rigging and exit\n"
                      << "  --help, -h          Show this help message\n\n";
            return 0;
        } else if (arg == "--benchmark-rig") {
            int iters = 100000;
            if (i + 1 < argc && argv[i + 1][0] != '-') {
                iters = std::atoi(argv[++i]);
            }
            Kongor::SimdRig rig;
            rig.InitSyntheticHumanoid(64);
            auto res = rig.RunBenchmark(iters);
            std::cout << "{\"status\":\"SUCCESS\",\"backend\":\"" << res.simdBackend
                      << "\",\"bones\":" << res.boneCount
                      << ",\"iterations\":" << res.iterations
                      << ",\"scalar_ms\":" << res.scalarElapsedMs
                      << ",\"simd_ms\":" << res.simdElapsedMs
                      << ",\"speedup\":" << res.speedup
                      << ",\"max_error\":" << res.maxError << "}\n";
            return 0;
        } else if (arg == "--anim" && i + 1 < argc) {
            targetAnimIndex = std::atoi(argv[++i]);
        } else if (arg == "--speed" && i + 1 < argc) {
            initialSpeed = static_cast<float>(std::atof(argv[++i]));
        } else if (arg == "--wireframe") {
            initialWireframe = true;
        } else if (arg == "--no-grid") {
            initialGrid = false;
        } else if (arg == "--headless-test") {
            headlessTest = true;
        } else if (arg == "--screenshot" && i + 1 < argc) {
            screenshotPath = argv[++i];
        } else if (arg[0] != '-') {
            initialModelPath = arg;
        }
    }

    // Configure Raylib window
    SetConfigFlags(FLAG_WINDOW_RESIZABLE | FLAG_MSAA_4X_HINT);
    if (headlessTest) {
        SetConfigFlags(FLAG_WINDOW_HIDDEN);
    }

    const int screenWidth = 1440;
    const int screenHeight = 900;
    InitWindow(screenWidth, screenHeight, "Kongor Model & Animation Quick-Viewer [Raylib 5.5]");
    SetTargetFPS(144);

    // Center window on current monitor
    int monitor = GetCurrentMonitor();
    int monitorW = GetMonitorWidth(monitor);
    int monitorH = GetMonitorHeight(monitor);
    if (monitorW > screenWidth && monitorH > screenHeight) {
        SetWindowPosition((monitorW - screenWidth) / 2, (monitorH - screenHeight) / 2);
    }

#if defined(_WIN32)
    void* nativeHwnd = GetWindowHandle();
    if (nativeHwnd != nullptr && !headlessTest) {
        // Bring window to absolute top over PyCharm / terminal
        SetWindowPos(nativeHwnd, (void*)(intptr_t)-1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST
        SetWindowPos(nativeHwnd, (void*)(intptr_t)-2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_NOTOPMOST
        ShowWindow(nativeHwnd, 1); // SW_SHOWNORMAL
        BringWindowToTop(nativeHwnd);
        SetForegroundWindow(nativeHwnd);
        SwitchToThisWindow(nativeHwnd, 1);
        Logger::Log(Logger::LOG_INFO, "Window centered & brought to foreground (HWND: " + std::to_string((uintptr_t)nativeHwnd) + ")");
    }
#endif

    Logger::Log(Logger::LOG_INFO, "Window initialized: " + std::to_string(screenWidth) + "x" + std::to_string(screenHeight));

    OrbitCamera camera;
    camera.Init(Vector3{ 0.0f, 1.0f, 0.0f }, 10.0f);

    ViewerState state;
    state.showWireframe = initialWireframe;
    state.showGrid = initialGrid;
    state.playbackSpeed = initialSpeed;

    // Load initial model if specified
    if (!initialModelPath.empty()) {
        if (state.Load(initialModelPath, camera)) {
            if (targetAnimIndex > 0 && targetAnimIndex < state.animCount) {
                state.currentAnimIndex = targetAnimIndex;
            }
        }
    } else {
        // Fallback: search for first available GLB in output/
        std::vector<std::string> candidateDirs = {
            "output",
            "output/Abyss_Warlord",
            "output/meshy_generated_assets"
        };
        for (const auto& dir : candidateDirs) {
            if (fs::exists(dir)) {
                for (const auto& entry : fs::directory_iterator(dir)) {
                    if (entry.is_regular_file() && entry.path().extension() == ".glb") {
                        initialModelPath = entry.path().string();
                        Logger::Log(Logger::LOG_INFO, "Auto-discovered candidate model: " + initialModelPath);
                        state.Load(initialModelPath, camera);
                        break;
                    }
                }
            }
            if (state.isModelLoaded) break;
        }
    }

    // If headless test, execute verification loop and terminate
    if (headlessTest) {
        Logger::Log(Logger::LOG_INFO, "[HEADLESS_TEST] Executing verification loop...");
        for (int frame = 0; frame < 15; frame++) {
            state.UpdateAnimation(0.016f);
            BeginDrawing();
            ClearBackground(DARKGRAY);
            BeginMode3D(camera.camera);
            if (state.isModelLoaded) {
                DrawModel(state.model, Vector3{ 0.0f, 0.0f, 0.0f }, 1.0f, WHITE);
            }
            EndMode3D();
            EndDrawing();
        }

        std::cout << "{\"status\":\"SUCCESS\",\"loaded\":" << (state.isModelLoaded ? "true" : "false")
                  << ",\"meshes\":" << (state.isModelLoaded ? state.model.meshCount : 0)
                  << ",\"animations\":" << state.animCount << "}\n";
        state.UnloadCurrent();
        CloseWindow();
        Logger::Shutdown();
        return 0;
    }

    // Main Interactive Loop
    while (!WindowShouldClose()) {
        float dt = GetFrameTime();

        // Check for Drag & Drop files
        if (IsFileDropped()) {
            FilePathList droppedFiles = LoadDroppedFiles();
            if (droppedFiles.count > 0) {
                std::string droppedPath = droppedFiles.paths[0];
                Logger::Log(Logger::LOG_INFO, "Detected Drag & Drop file: " + droppedPath);
                state.Load(droppedPath, camera);
            }
            UnloadDroppedFiles(droppedFiles);
        }

        // Input Handling
        if (IsKeyPressed(KEY_SPACE)) {
            state.isPlaying = !state.isPlaying;
            Logger::Log(Logger::LOG_INFO, std::string("Playback: ") + (state.isPlaying ? "PLAY" : "PAUSE"));
        }
        if (IsKeyPressed(KEY_RIGHT)) {
            state.isPlaying = false;
            state.currentAnimFrame += 1.0f;
            state.UpdateAnimation(0.0f);
        }
        if (IsKeyPressed(KEY_LEFT)) {
            state.isPlaying = false;
            state.currentAnimFrame -= 1.0f;
            state.UpdateAnimation(0.0f);
        }
        if (IsKeyPressed(KEY_RIGHT_BRACKET) || IsKeyPressed(KEY_PAGE_DOWN)) {
            if (state.animCount > 0) {
                state.currentAnimIndex = (state.currentAnimIndex + 1) % state.animCount;
                state.currentAnimFrame = 0.0f;
                Logger::Log(Logger::LOG_INFO, "Switched to animation: " + std::to_string(state.currentAnimIndex + 1) + "/" + std::to_string(state.animCount));
            }
        }
        if (IsKeyPressed(KEY_LEFT_BRACKET) || IsKeyPressed(KEY_PAGE_UP)) {
            if (state.animCount > 0) {
                state.currentAnimIndex = (state.currentAnimIndex - 1 + state.animCount) % state.animCount;
                state.currentAnimFrame = 0.0f;
                Logger::Log(Logger::LOG_INFO, "Switched to animation: " + std::to_string(state.currentAnimIndex + 1) + "/" + std::to_string(state.animCount));
            }
        }
        if (IsKeyPressed(KEY_W)) {
            state.showWireframe = !state.showWireframe;
            Logger::Log(Logger::LOG_INFO, std::string("Wireframe: ") + (state.showWireframe ? "ON" : "OFF"));
        }
        if (IsKeyPressed(KEY_B)) {
            state.showBounds = !state.showBounds;
            Logger::Log(Logger::LOG_INFO, std::string("Bounding Box: ") + (state.showBounds ? "ON" : "OFF"));
        }
        if (IsKeyPressed(KEY_G)) {
            state.showGrid = !state.showGrid;
            Logger::Log(Logger::LOG_INFO, std::string("Grid: ") + (state.showGrid ? "ON" : "OFF"));
        }
        if (IsKeyPressed(KEY_A)) {
            state.showAxes = !state.showAxes;
        }
        if (IsKeyPressed(KEY_R)) {
            camera.Reset();
        }
        if (IsKeyPressed(KEY_N)) {
            state.autoNormalize = !state.autoNormalize;
            Logger::Log(Logger::LOG_INFO, std::string("Auto-Normalize Scale: ") + (state.autoNormalize ? "ON" : "OFF"));
            float maxDim = std::max({ state.boundsSize.x, state.boundsSize.y, state.boundsSize.z });
            float effectiveDim = state.autoNormalize ? 2.0f : (maxDim < 0.01f ? 2.0f : maxDim);
            Vector3 effectiveCenter = state.autoNormalize ? Vector3{ 0.0f, 1.0f, 0.0f } : state.boundsCenter;
            camera.Init(effectiveCenter, effectiveDim * 2.2f);
        }
        if (IsKeyPressed(KEY_T)) {
            state.bgThemeIndex = (state.bgThemeIndex + 1) % 4;
        }
        if (IsKeyPressed(KEY_F) || IsKeyPressed(KEY_F11)) {
            ToggleFullscreen();
        }
        if (IsKeyPressed(KEY_F12)) {
            TakeScreenshot("screenshot_manual.png");
            Logger::Log(Logger::LOG_INFO, "User captured screenshot to screenshot_manual.png");
        }
        if (IsKeyPressed(KEY_H) || IsKeyPressed(KEY_F1)) {
            state.showHud = !state.showHud;
        }
        if (IsKeyPressed(KEY_EQUAL) || IsKeyPressed(KEY_KP_ADD)) {
            state.playbackSpeed = std::min(state.playbackSpeed + 0.25f, 4.0f);
        }
        if (IsKeyPressed(KEY_MINUS) || IsKeyPressed(KEY_KP_SUBTRACT)) {
            state.playbackSpeed = std::max(state.playbackSpeed - 0.25f, 0.25f);
        }

        // Update Camera & Animation
        camera.Update();
        state.UpdateAnimation(dt);

        // Render Frame
        BeginDrawing();
        ClearBackground(GetBackgroundColor(state.bgThemeIndex));

        BeginMode3D(camera.camera);

        // Ground Grid
        if (state.showGrid) {
            float gridExtent = std::max(10.0f, camera.distance * 1.5f);
            int slices = 20;
            DrawGrid(slices, gridExtent / static_cast<float>(slices));
        }

        // Coordinate Axes at Origin
        if (state.showAxes) {
            float axisLength = std::max(1.0f, camera.distance * 0.1f);
            DrawLine3D(Vector3{ 0, 0, 0 }, Vector3{ axisLength, 0, 0 }, RED);
            DrawLine3D(Vector3{ 0, 0, 0 }, Vector3{ 0, axisLength, 0 }, GREEN);
            DrawLine3D(Vector3{ 0, 0, 0 }, Vector3{ 0, 0, axisLength }, BLUE);
        }

        // Model Rendering
        if (state.isModelLoaded) {
            float maxDim = std::max({ state.boundsSize.x, state.boundsSize.y, state.boundsSize.z });
            float modelScale = (state.autoNormalize && maxDim > 0.00001f) ? (2.0f / maxDim) : 1.0f;
            Vector3 modelPos = state.autoNormalize ?
                Vector3{ -state.boundsCenter.x * modelScale, -state.bounds.min.y * modelScale, -state.boundsCenter.z * modelScale } :
                Vector3{ 0.0f, 0.0f, 0.0f };

            if (state.showWireframe) {
                DrawModelWires(state.model, modelPos, modelScale, Color{ 70, 220, 255, 255 });
            } else {
                DrawModel(state.model, modelPos, modelScale, WHITE);
            }

            // Bounding Box
            if (state.showBounds) {
                if (state.autoNormalize) {
                    BoundingBox scaledBounds = {
                        Vector3{ -state.boundsSize.x * 0.5f * modelScale, 0.0f, -state.boundsSize.z * 0.5f * modelScale },
                        Vector3{ state.boundsSize.x * 0.5f * modelScale, 2.0f, state.boundsSize.z * 0.5f * modelScale }
                    };
                    DrawBoundingBox(scaledBounds, Color{ 255, 200, 40, 255 });
                } else {
                    DrawBoundingBox(state.bounds, Color{ 255, 200, 40, 255 });
                }
            }
        }

        EndMode3D();

        // 2D HUD / Telemetry Overlay
        if (state.showHud) {
            // Semi-transparent background card for HUD
            DrawRectangle(15, 15, 460, state.animCount > 0 ? 210 : 160, Color{ 10, 12, 16, 210 });
            DrawRectangleLines(15, 15, 460, state.animCount > 0 ? 210 : 160, Color{ 50, 60, 75, 255 });

            DrawText("KONGOR 3D ASSET INSPECTOR", 28, 26, 16, Color{ 255, 200, 50, 255 });

            if (state.isModelLoaded) {
                fs::path p(state.currentFilePath);
                std::string filename = p.filename().string();
                if (filename.length() > 36) filename = filename.substr(0, 33) + "...";

                DrawText(TextFormat("File: %s", filename.c_str()), 28, 52, 14, WHITE);
                DrawText(TextFormat("Meshes: %d  |  Triangles: %d  |  Vertices: %d",
                    state.model.meshCount, state.totalTriangles, state.totalVertices), 28, 72, 13, Color{ 190, 200, 215, 255 });
                DrawText(TextFormat("Materials: %d  |  Bones: %d",
                    state.model.materialCount, state.model.boneCount), 28, 90, 13, Color{ 190, 200, 215, 255 });

                if (state.animCount > 0) {
                    const char* trackName = state.animations[state.currentAnimIndex].name;
                    if (!trackName || !trackName[0]) trackName = "Default";
                    int maxFrames = state.animations[state.currentAnimIndex].frameCount;
                    int curFrame = static_cast<int>(state.currentAnimFrame);

                    DrawText(TextFormat("Anim [%d/%d]: %s", state.currentAnimIndex + 1, state.animCount, trackName),
                        28, 115, 14, Color{ 100, 220, 255, 255 });
                    DrawText(TextFormat("Frame: %d / %d  |  Speed: %.2fx  %s",
                        curFrame, maxFrames, state.playbackSpeed, state.isPlaying ? "[PLAYING]" : "[PAUSED]"),
                        28, 135, 13, state.isPlaying ? Color{ 120, 255, 120, 255 } : Color{ 255, 160, 100, 255 });

                    // Mini timeline progress bar
                    float progress = maxFrames > 1 ? static_cast<float>(curFrame) / static_cast<float>(maxFrames - 1) : 0.0f;
                    DrawRectangle(28, 160, 430, 6, Color{ 40, 45, 55, 255 });
                    DrawRectangle(28, 160, static_cast<int>(430.0f * progress), 6, Color{ 80, 180, 255, 255 });
                }

                // FPS & Frame latency
                float frameMs = dt * 1000.0f;
                DrawText(TextFormat("%d FPS (%.1f ms)", GetFPS(), frameMs),
                    350, 26, 14, frameMs > 10.0f ? Color{ 255, 120, 100, 255 } : Color{ 120, 255, 150, 255 });
            } else {
                DrawText("No model loaded. Drag & drop a .glb file here.", 28, 60, 14, Color{ 200, 100, 100, 255 });
            }

            // Keybindings bar at bottom
            int screenH = GetScreenHeight();
            int screenW = GetScreenWidth();
            DrawRectangle(0, screenH - 32, screenW, 32, Color{ 12, 14, 18, 230 });
            DrawText("[SPACE] Play/Pause  |  [<- / ->] Frame  |  [[ / ]] Anim  |  [W] Wire  |  [B] Bounds  |  [G] Grid  |  [N] Norm Scale  |  [R] Reset  |  [H] HUD",
                20, screenH - 22, 12, Color{ 175, 185, 200, 255 });
        }

        EndDrawing();

        if (!screenshotPath.empty()) {
            static int s_FrameCount = 0;
            if (++s_FrameCount == 10) {
                TakeScreenshot(screenshotPath.c_str());
                Logger::Log(Logger::LOG_INFO, "Captured requested screenshot to " + screenshotPath);
                break;
            }
        }
    }

    // Cleanup
    state.UnloadCurrent();
    CloseWindow();
    Logger::Shutdown();

    return 0;
}
