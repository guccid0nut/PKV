# K2 Video Driver Specification & Function Table (`SVidDriverAPI`)

> **Document Type:** Reverse-Engineered Binary Specification  
> **Source Binaries:** `vid_d3d9_x64.dll` (v4.x x64) & `vid_gl2_x64.dll` (v4.x x64)  
> **Engine Target:** K2 Engine Core (`k2_x64.dll`)  
> **Architecture:** Microsoft x64 Calling Convention (`__fastcall` / `__stdcall`)  

---

## 1. Overview & Export Architecture

All K2 video drivers (`vid_d3d9_x64.dll`, `vid_gl2_x64.dll`, `vid_vulkan_x64.dll`) are loaded dynamically by `k2_x64.dll` at runtime via `LoadLibraryW`.

### 1.1 The Sole Export: `InitAPIs`
Every K2 video driver exports **exactly one symbol**:

```cpp
extern "C" __declspec(dllexport) void __stdcall InitAPIs(
    SVidDriverAPI* pDriverAPI,   // RCX: Table populated by the driver with 60 function pointers
    void*          pCoreEngine,  // RDX: Engine callback pointer (passed to engine logging/cvar system)
    void*          pModManager   // R8:  Mod/File manager pointer
);
```

### 1.2 Driver Startup Sequence
1. The driver saves `pCoreEngine` and `pModManager` to global pointers.
2. The driver registers its descriptive name (e.g., `"Direct3D 9.0c [HLSL]"` or `"OpenGL 2.1 [GLSL]"`).
3. The driver populates `pDriverAPI` offsets `+0x020` through `+0x1F8` (60 contiguous function pointers).

---

## 2. The 60-Slot Function Pointer Table (`SVidDriverAPI`)

| Slot Offset | D3D9 RVA | GL2 RVA | Discovered Subsystem / Imports | Confirmed / Inferred Function Signature |
| :--- | :--- | :--- | :--- | :--- |
| **`+0x020`** | `0x000625F0` | `0x00075320` | `Direct3DCreate9Ex`, `"D3D_Init: failed"` | `bool Init(void* pInitParams)` |
| **`+0x028`** | `0x0005D4E0` | `0x000761C0` | `RegisterClass`, `CreateWindowEx`, `"K2_Direct3D"`, `"K2_OpenGL"` | `bool StartWindow(void)` |
| **`+0x030`** | `0x0005B030` | `0x000768C0` | `"D3D_SetMode settings: width: height:"` | `bool SetMode(int w, int h, int bpp, int rate, bool fs)` |
| **`+0x038`** | `0x0005AF90` | `0x0006F010` | Mode validation dispatch | `bool IsValidMode(int w, int h, int bpp)` |
| **`+0x040`** | `0x0005AF30` | `0x0006EFA0` | Aspect ratio query | `float GetAspectRatio(void)` |
| **`+0x048`** | `0x0005AF20` | `0x0006EF90` | Display capability flags | `uint32_t GetDriverFlags(void)` |
| **`+0x050`** | `0x0005A1D0` | `0x000774E0` | `"K2_Direct3D"`, `"K2_OpenGL"` | `void Shutdown(void)` |
| **`+0x058`** | `0x0005AD70` | `0x0006EF70` | Gamma / Contrast adjust | `void SetGamma(float gamma)` |
| **`+0x060`** | `0x00057FF0` | `0x0006E9A0` | Device Lost / Reset probe | `bool CheckDeviceStatus(void)` |
| **`+0x068`** | `0x000594E0` | `0x0006E950` | `"D3D_BeginFrame: BeginScene failed"` | `bool BeginFrame(void)` |
| **`+0x070`** | `0x00059270` | `0x00077690` | `GDI32.dll!SwapBuffers` | `void EndFrame(void)` |
| **`+0x078`** | `0x00074440` | `0x0006E7F0` | `movsd [r13+0x24]`, `CCamera*` setup, `glClear` | `void SetCamera(const CCamera* pCamera)` |
| **`+0x080`** | `0x000758C0` | `0x0006E1B0` | `"D3D_Add2dRect() - Invalid texture handle"` | `void Add2dRect(float x, float y, float w, float h, ...)` |
| **`+0x088`** | `0x00075A60` | `0x0006E130` | `"D3D_Add2dQuad() - Invalid texture handle"` | `void Add2dQuad(const CVec2f* v, const CVec2f* tc, uint32_t c)` |
| **`+0x090`** | `0x00075C30` | `0x0006E060` | `"D3D_Add2dTri() - Invalid texture handle"` | `void Add2dTri(const CVec2f* v, const CVec2f* tc, uint32_t c)` |
| **`+0x098`** | `0x00075DF0` | `0x0006E020` | 2D Line rendering dispatch | `void Add2dLine(float x1, float y1, float x2, float y2, uint32_t c)` |
| **`+0x0A0`** | `0x00075F40` | `0x0006DFE0` | 2D Batch flush | `void Flush2D(void)` |
| **`+0x0A8`** | `0x00076ED0` | `0x0006DF80` | Transform matrix setup | `void SetProjectionMatrix(const CMatrix4x4* pMat)` |
| **`+0x0B0`** | `0x000760A0` | `0x0006DF00` | View matrix setup | `void SetViewMatrix(const CMatrix4x4* pMat)` |
| **`+0x0B8`** | `0x00058DC0` | `0x0006DEC0` | World matrix setup | `void SetWorldMatrix(const CMatrix4x4* pMat)` |
| **`+0x0C0`** | `0x000585B0` | `0x0006E250` | Memory allocator deallocate hook | `void FreeRenderMemory(void* ptr)` |
| **`+0x0C8`** | `0x00058580` | `0x0006DCB0` | `glReadPixels`, `CBitmap::Alloc` | `bool ReadPixels(CBitmap* pOutBmp, int x, int y, int w, int h)` |
| **`+0x0D0`** | `0x000584E0` | `0x0006DCA0` | Depth buffer state | `void SetDepthTest(bool bEnable, bool bWrite)` |
| **`+0x0D8`** | `0x00058C30` | `0x0006E7E0` | Scissor rect setup | `void SetScissor(int x, int y, int w, int h)` |
| **`+0x0E0`** | `0x00058D60` | `0x0006DEA0` | Cull mode (`CULL_BACK`, `CULL_FRONT`, `NONE`) | `void SetCullMode(uint32_t mode)` |
| **`+0x0E8`** | `0x00058C40` | `0x0006DE70` | Blend mode setup (`SRC_ALPHA`, `ONE_MINUS_SRC_ALPHA`) | `void SetBlendMode(uint32_t srcBlend, uint32_t dstBlend)` |
| **`+0x0F0`** | `0x00058C60` | `0x0006DE40` | Alpha testing threshold setup | `void SetAlphaTest(bool bEnable, float refVal)` |
| **`+0x0F8`** | `0x000584D0` | `0x00077780` | Polygon offset / Depth bias | `void SetDepthBias(float bias, float slope)` |
| **`+0x100`** | `0x00058470` | `0x0006DC30` | `glColorMask`, `glDepthMask` | `void SetColorWriteMask(bool r, bool g, bool b, bool a)` |
| **`+0x108`** | `0x0009CF00` | `0x00044E60` | `CFileManager::GetTopModPath`, `"/textures.s2z"` | `bool OpenTextureArchive(const wchar_t* pArchive)` |
| **`+0x110`** | `0x0009CDD0` | `0x00044FA0` | `CArchive::~CArchive` | `void CloseTextureArchive(void)` |
| **`+0x118`** | `0x0009D250` | `0x00047370` | `GetFileList`, `"tga"` | `void EnumerateTextures(void* pCallback)` |
| **`+0x120`** | `0x0009D600` | `0x00044DD0` | Texture streaming / prefetch | `bool PrefetchTexture(const wchar_t* pPath)` |
| **`+0x128`** | `0x0009DA30` | `0x0006DC10` | `"_posx"`, `"_negx"`, `"_posy"`, `CBitmap::Load` | `ResHandle RegisterCubeTexture(const wchar_t* pBasename)` |
| **`+0x130`** | `0x0009F5F0` | `0x0006DBD0` | Volume / 3D texture registration | `ResHandle Register3DTexture(const wchar_t* pPath)` |
| **`+0x138`** | `0x0009F630` | `0x0006DA60` | `glGetTexLevelParameteriv` | `bool GetTextureDimensions(ResHandle hTex, int* w, int* h)` |
| **`+0x140`** | `0x0009F8E0` | `0x0006D9E0` | `"Freeing Texture Slot: "` | `void UnregisterTexture(ResHandle hTex)` |
| **`+0x148`** | `0x0009FD90` | `0x0006D9C0` | `"Using Texture Slot: "` | `ResHandle RegisterTexture(const CBitmap* pBmp, uint32_t flags)` |
| **`+0x150`** | `0x00057EB0` | `0x0006D8D0` | Render target allocation (RTT) | `ResHandle CreateRenderTarget(int w, int h, uint32_t fmt)` |
| **`+0x158`** | `0x000591B0` | `0x0006D890` | `"D3D_BeginRenderToTexture: BeginScene failed"` | `bool BeginRenderToTexture(ResHandle hRT)` |
| **`+0x160`** | `0x00059140` | `0x0006D840` | Render target completion | `void EndRenderToTexture(ResHandle hRT)` |
| **`+0x168`** | `0x00057DF0` | `0x0006D7F0` | `glViewport`, `glPopAttrib` | `void SetViewport(int x, int y, int w, int h)` |
| **`+0x170`** | `0x00058EC0` | `0x0006D7D0` | Viewport query | `void GetViewport(int* x, int* y, int* w, int* h)` |
| **`+0x178`** | `0x0007BDD0` | `0x0006D540` | Dynamic Vertex Buffer Bind | `void BindVertexBuffer(ResHandle hVB, uint32_t stride)` |
| **`+0x180`** | `0x0007BE00` | `0x0006D530` | Dynamic Index Buffer Bind | `void BindIndexBuffer(ResHandle hIB)` |
| **`+0x188`** | `0x0007BE10` | `0x0006D500` | Draw Indexed Primitives | `void DrawIndexed(uint32_t type, uint32_t startIdx, uint32_t count)` |
| **`+0x190`** | `0x0007BE40` | `0x0006D4F0` | Draw Non-Indexed Primitives | `void DrawPrimitives(uint32_t type, uint32_t startVtx, uint32_t count)` |
| **`+0x198`** | `0x0007BE50` | `0x0006D370` | Shader Program Bind | `void BindShader(ResHandle hShader)` |
| **`+0x1A0`** | `0x0006C380` | `0x0006D740` | `"D3D_RegisterModel(): Unknown model type"` | `bool RegisterModel(CK2Model* pModel)` |
| **`+0x1A8`** | `0x0006C410` | `0x0006D570` | `"D3D_UnregisterModel(): Unknown model type"`, `GetNumMeshes` | `void UnregisterModel(CK2Model* pModel)` |
| **`+0x1B0`** | `0x00058460` | `0x0006D360` | Lighting state toggle | `void SetLighting(bool bEnable)` |
| **`+0x1B8`** | `0x00076110` | `0x0006D0B0` | `CResourceManager::GetTexture`, `glClearColor` | `void SetClearColor(float r, float g, float b, float a)` |
| **`+0x1C0`** | `0x00076130` | `0x0006D0A0` | Clear depth/stencil value | `void SetClearDepth(float depth, uint8_t stencil)` |
| **`+0x1C8`** | `0x00058430` | `0x00077790` | `USER32.dll!ShowCursor` | `void ShowCursor(bool bShow)` |
| **`+0x1D0`** | `0x00058020` | `0x000777D0` | `GetCursor`, `GetBitmapPointer`, `CreateBitmap` | `void SetCursor(CCursor* pCursor)` |
| **`+0x1D8`** | `0x0009D650` | `0x0006CEA0` | `D3DXLoadSurfaceFromSurface`, `glBindTexture` | `void UpdateTexture(ResHandle hTex, const void* pData, int w, int h)` |
| **`+0x1E0`** | `0x000597E0` | `0x0006E120` | Texture Filter Quality | `void SetTextureFiltering(uint32_t filterMode, uint32_t aniso)` |
| **`+0x1E8`** | `0x00059790` | `0x0006E0D0` | `glGetIntegerv` (Driver capability query) | `uint32_t GetMaxTextureUnits(void)` |
| **`+0x1F0`** | `0x0005AD00` | `0x0006EEF0` | VSync / SwapInterval | `void SetVSync(bool bEnable)` |
| **`+0x1F8`** | `0x0005ACB0` | `0x0006EE90` | Frame Rate Throttle / Sleep | `void SetMaxFPS(uint32_t maxFps)` |

---

## 3. C++ Struct Definition (`SVidDriverAPI.h`)

This struct can be directly compiled into any drop-in driver to guarantee bit-for-bit memory and calling convention compatibility:

```cpp
#pragma once
#include <cstdint>

#pragma pack(push, 8)
struct SVidDriverAPI {
    // 0x000 - 0x018: Reserved header / vtable padding
    void* m_reserved0; // 0x000
    void* m_reserved1; // 0x008
    void* m_reserved2; // 0x010
    void* m_reserved3; // 0x018

    // 0x020 - 0x058: Device Lifecycle & Display
    void* Init;                 // +0x020
    void* StartWindow;          // +0x028
    void* SetMode;              // +0x030
    void* IsValidMode;          // +0x038
    void* GetAspectRatio;       // +0x040
    void* GetDriverFlags;       // +0x048
    void* Shutdown;             // +0x050
    void* SetGamma;             // +0x058
    void* CheckDeviceStatus;    // +0x060

    // 0x068 - 0x078: Frame Lifecycle & Camera Viewport Setup
    void* BeginFrame;           // +0x068
    void* EndFrame;             // +0x070
    void* SetCamera;            // +0x078 (Discovered: const CCamera* / CSceneView* setup; previously hypothesized as Clear)

    // 0x080 - 0x0A0: 2D Immediate Primitives
    void* Add2dRect;            // +0x080
    void* Add2dQuad;            // +0x088
    void* Add2dTri;             // +0x090
    void* Add2dLine;            // +0x098
    void* Flush2D;              // +0x0A0

    // 0x0A8 - 0x0B8: Matrices
    void* SetProjectionMatrix;  // +0x0A8
    void* SetViewMatrix;        // +0x0B0
    void* SetWorldMatrix;       // +0x0B8

    // 0x0C0 - 0x100: Render Pipeline States & Screen Capture
    void* FreeRenderMemory;     // +0x0C0
    void* ReadPixels;           // +0x0C8
    void* SetDepthTest;         // +0x0D0
    void* SetScissor;           // +0x0D8
    void* SetCullMode;          // +0x0E0
    void* SetBlendMode;         // +0x0E8
    void* SetAlphaTest;         // +0x0F0
    void* SetDepthBias;         // +0x0F8
    void* SetColorWriteMask;    // +0x100

    // 0x108 - 0x148: Texture Management
    void* OpenTextureArchive;   // +0x108
    void* CloseTextureArchive;  // +0x110
    void* EnumerateTextures;    // +0x118
    void* PrefetchTexture;      // +0x120
    void* RegisterCubeTexture;  // +0x128
    void* Register3DTexture;    // +0x130
    void* GetTextureDimensions; // +0x138
    void* UnregisterTexture;    // +0x140
    void* RegisterTexture;      // +0x148

    // 0x150 - 0x170: Render Targets (FBO / RTT) & Viewport
    void* CreateRenderTarget;   // +0x150
    void* BeginRenderToTexture; // +0x158
    void* EndRenderToTexture;   // +0x160
    void* SetViewport;          // +0x168
    void* GetViewport;          // +0x170

    // 0x178 - 0x198: Geometry Buffers & Shaders
    void* BindVertexBuffer;     // +0x178
    void* BindIndexBuffer;      // +0x180
    void* DrawIndexed;          // +0x188
    void* DrawPrimitives;       // +0x190
    void* BindShader;           // +0x198

    // 0x1A0 - 0x1A8: Model & Mesh Pipeline
    void* RegisterModel;        // +0x1A0
    void* UnregisterModel;      // +0x1A8

    // 0x1B0 - 0x1D0: Lighting, Clear Colors & Cursor
    void* SetLighting;          // +0x1B0
    void* SetClearColor;        // +0x1B8
    void* SetClearDepth;        // +0x1C0
    void* ShowCursor;           // +0x1C8
    void* SetCursor;            // +0x1D0

    // 0x1D8 - 0x1F8: Driver Settings & Synchronization
    void* UpdateTexture;        // +0x1D8
    void* SetTextureFiltering;  // +0x1E0
    void* GetMaxTextureUnits;   // +0x1E8
    void* SetVSync;             // +0x1F0
    void* SetMaxFPS;            // +0x1F8
};
#pragma pack(pop)

static_assert(sizeof(SVidDriverAPI) == 0x200, "SVidDriverAPI size must be exactly 0x200 (512 bytes)");
static_assert(offsetof(SVidDriverAPI, Init) == 0x020, "Init must be at offset 0x020");
static_assert(offsetof(SVidDriverAPI, SetMaxFPS) == 0x1F8, "SetMaxFPS must be at offset 0x1F8");
```

---

## 4. K2 Engine Driver Loading & Resolution Architecture

Through disassembling `k2_x64.dll` (RVAs `0x008BA800` through `0x00A9A75E`), the exact mechanism used by K2 to locate, resolve, and load video drivers has been reverse-engineered:

### 4.1 Cvar & Command-Line Binding
* **Engine Cvar:** `host_vidDriver` (Global Cvar object at RVA `0x1297CC0`).
  * Default Value: `"vid_d3d9"`
  * Registered at RVA `0x000B07D8` with default string `"vid_d3d9"`.
* **Command-Line Switch:** `-vid <name>`
  * Parsed at RVA `0x008BA845` in `k2_x64.dll`.
  * The token directly following `-vid` is assigned to `host_vidDriver`.
  * Example: `-vid vid_vulkan` sets `host_vidDriver` to `"vid_vulkan"`.

### 4.2 DLL Name Construction & VFS Resolution
At RVA `0x00A999E0` (`CVidDriver::LoadVidDriver`):
1. **Suffix Appending (RVA `0x00A99C0C`):**  
   The engine appends the wide string `L"_x64"` to the value of `host_vidDriver`.
   * Input `"vid_d3d9"` $\to$ `"vid_d3d9_x64"`
   * Input `"vid_vulkan"` $\to$ `"vid_vulkan_x64"`
   * Input `"vulkan"` $\to$ `"vulkan_x64"`
2. **VFS Root Prefixing (RVA `0x00A981C6`):**  
   The helper at RVA `0x00A980E0` prepends `L":/"` (the K2 virtual root directory representing `games/client/`):
   * `"vid_vulkan_x64"` $\to$ `":/vid_vulkan_x64"`
3. **Module Loading (RVA `0x00A981F4`):**  
   K2 calls `CFileManager::LoadLibrary` (RVA `0x00A049C0`), which automatically appends `L".dll"` (RVA `0x00A04ACD`), resulting in `":/vid_vulkan_x64.dll"`.

### 4.3 Fallback Safety Sequence (RVA `0x00A9A700`)
If the specified driver DLL fails to load (e.g., file not found or export missing):
* K2 catches the failure gracefully.
* It reverts `host_vidDriver` to the previous driver (usually `vid_d3d9`).
* It reloads `vid_d3d9_x64.dll` and continues booting, preventing a fatal crash.

### 4.4 Driver Naming Best Practices
To ensure seamless loading across both CLI flags (`-vid vid_vulkan` and `-vid vulkan`) as well as configuration files (`autoexec.cfg`, `startup.cfg`):
* Always deploy both `vid_vulkan_x64.dll` and a symlink/copy `vulkan_x64.dll`.
* Configure Willowmaker to supply `-vid vid_vulkan +Set host_vidDriver vid_vulkan`.

---

## 5. Case Study: 64-Bit Calling Convention, Pointer Truncation & SSE Registers

During live integration testing of the Phase 1 spy driver (`vid_vulkan_x64.dll`), a crash occurred on Frame #2 that demonstrated the critical subtleties of the 64-bit Windows C++ ABI.

### 5.1 The Crash Incident
On game boot, `InitAPIs()` and `StartWindow()` completed successfully, Frame #1 rendered, and 20+ models were registered. However, on Frame #2 during scene rendering, Kongor Native Guard captured an immediate access violation:

```text
Exception:      EXCEPTION_ACCESS_VIOLATION (0xC0000005)
Fault Address:  0x00007FF9AEE544FE (vid_d3d9_x64.dll + 0x744FE)
Fault Register: R13 = 0x000000002B83D5D8
Fault Target:   [R13 + 0x24] -> 0x000000002B83D5FC (Unmapped Memory)
```

### 5.2 Disassembly Ground Truth (`vid_d3d9_x64.dll` at RVA `0x74440`)
Disassembling the target function for Slot **`+0x078`** (`0x00074440`):
```asm
0x00074440: mov      rax, rsp
0x00074443: mov      qword ptr [rax + 8], rcx
...
0x000744A6: mov      r13, rcx
...
0x000744FE: movsd    xmm0, qword ptr [r13 + 0x24]  ; <-- CRASH: dereferencing R13 + 0x24
0x00074504: movsd    qword ptr [rip + 0x3bfa14], xmm0
0x0007450C: mov      eax, dword ptr [r13 + 0x2c]
0x00074510: mov      dword ptr [rip + 0x3bfa12], eax
0x00074516: mov      r14d, dword ptr [r13 + 0x5c]
```

### 5.3 Root Cause: The 32-Bit Truncation Bug
* **Initial Assumption:** Slot `+0x078` was hypothesized to be `Clear(uint32_t flags, uint32_t color, float depth)`.
* **The Reality:** Slot `+0x078` is actually `SetCamera(const CCamera* pCamera)` where `RCX` is a 64-bit struct pointer:
  * `+0x24`: Camera FOV / Near-Far Clipping planes (double precision float `movsd`).
  * `+0x2c`: Viewport width / height integer (`mov eax`).
  * `+0x5c`: Camera render flags (`mov r14d`).
* **The ABI Mechanism of Failure:**
  1. The K2 Engine called slot `+0x078` with a valid 64-bit pointer in `RCX`: `0x000002962B83D5D8`.
  2. The spy wrapper signature was typed as `Hook_Clear(uint32_t flags, uint32_t color, float depth)`.
  3. In MSVC x64 ABI, when a parameter is typed as `uint32_t`, the compiler emits code using 32-bit `ecx`, clearing the upper 32 bits (`0x00000296` $\to$ `0x00000000`).
  4. The forwarded call arrived at `vid_d3d9_x64.dll` with `RCX = 0x000000002B83D5D8`.
  5. The instruction at `0x744FE` attempted to read `[0x000000002B83D5D8 + 0x24] = 0x2B83D5FC`, generating `0xC0000005`.

### 5.4 The SSE / AVX Register Clobbering Hazard
A secondary discovery concerned the use of generic C++ forwarding thunks:
* In Windows x64 ABI, floating-point arguments are passed in `XMM0`, `XMM1`, `XMM2`, `XMM3`.
* A generic C++ function typed as `void* Thunk(uint64_t a1, uint64_t a2, ...)` only forwards integer registers (`RCX`, `RDX`, `R8`, `R9`).
* If a thunk calls standard C library functions (like `Log(...)`, `vfprintf`, or `GetLocalTime`) before forwarding, the compiler and CRT clobber volatile `XMM0..XMM5` registers.
* Consequently, functions like `Add2dRect(float x, float y, float w, float h)` or `SetGamma(float gamma)` would receive corrupted coordinates and color values.

### 5.5 Architectural Solution: Hybrid Selective Forwarding
To ensure 100% native stability and zero register clobbering:
1. **Direct Backend Initialisation:** Stock `vid_d3d9_x64.dll!InitAPIs` is invoked directly on the engine's `SVidDriverAPI` table first, populating all 60 slots and `std::wstring sDriverName`.
2. **Selective Lifecycle Interception:** Only high-level lifecycle methods (`Init`, `StartWindow`, `SetMode`, `BeginFrame`, `EndFrame`, `SetVSync`, `SetMaxFPS`, `Shutdown`) are hooked.
3. **Pristine Direct Execution:** All 52 inner rendering, geometry, texture, shader, matrix, and viewport slots remain pointing **directly** to stock `vid_d3d9_x64.dll` machine code. No intermediate stack frames, zero pointer truncation, and zero float register distortion.


