// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma once
#include <cstdint>
#include <cstddef>

#pragma pack(push, 8)
struct SVidDriverAPI {
    // 0x000 - 0x018: Reserved engine header / vtable padding
    void* m_reserved0; // 0x000
    void* m_reserved1; // 0x008
    void* m_reserved2; // 0x010
    void* m_reserved3; // 0x018

    // 0x020 - 0x060: Device Lifecycle & Display
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
    union {
        void* SetCamera;        // +0x078 (Legacy alias)
        void* RenderScene;      // +0x078: CVid::RenderScene(CCamera*)
    };

    // 0x080 - 0x0A0: 2D Immediate Primitives
    void* Add2dRect;            // +0x080
    void* Add2dQuad;            // +0x088
    void* Add2dTri;             // +0x090
    void* Add2dLine;            // +0x098
    void* Add2dSmoothLine;      // +0x0A0 (Verified via Ghidra CVid::Add2dSmoothLine; previously hypothesized as Flush2D)

    // 0x0A8 - 0x0B8: Transform Matrices
    void* SetProjectionMatrix;  // +0x0A8
    void* SetViewMatrix;        // +0x0B0
    void* SetWorldMatrix;       // +0x0B8

    // 0x0C0 - 0x100: Pipeline States & Screen Capture
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
    void* RegisterCubeTexture;  // +0x128 (CVid::RegisterTexture(CTexture*))
    void* Register3DTexture;    // +0x130
    void* GetTextureDimensions; // +0x138
    void* UnregisterTexture;    // +0x140 (CVid::UnregisterTexture(CTexture*))
    void* RegisterTexture;      // +0x148 (RegisterTexture(const CBitmap*, uint32_t flags))

    // 0x150 - 0x170: Render Targets (FBO / RTT) & Viewport
    void* CreateRenderTarget;   // +0x150
    void* BeginRenderToTexture; // +0x158
    void* EndRenderToTexture;   // +0x160
    void* SetViewport;          // +0x168
    void* GetViewport;          // +0x170

    // 0x178 - 0x198: Geometry Shaders & Pipeline Registration (Verified via Ghidra CVid decompilation)
    union {
        void* BindVertexBuffer;      // +0x178 (Legacy alias)
        void* RegisterVertexShader;  // +0x178: CVid::RegisterVertexShader(CVertexShader*)
    };
    union {
        void* BindIndexBuffer;       // +0x180 (Legacy alias)
        void* UnregisterVertexShader;// +0x180: CVid::UnregisterVertexShader(CVertexShader*)
    };
    union {
        void* DrawIndexed;           // +0x188 (Legacy alias)
        void* RegisterPixelShader;   // +0x188: CVid::RegisterPixelShader(CPixelShader*)
    };
    union {
        void* DrawPrimitives;        // +0x190 (Legacy alias)
        void* UnregisterPixelShader; // +0x190: CVid::UnregisterPixelShader(CPixelShader*)
    };
    union {
        void* BindShader;            // +0x198 (Legacy alias)
        void* RegisterShaderPair;    // +0x198: CVid::RegisterShaderPair(CVertexShader*, CPixelShader*)
    };

    // 0x1A0 - 0x1A8: Model & Mesh Pipeline
    void* RegisterModel;        // +0x1A0: CVid::RegisterModel(CModel*)
    void* UnregisterModel;      // +0x1A8: CVid::UnregisterModel(CModel*)

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
    union {
        void* SetVSync;             // +0x1F0 (Legacy alias)
        void* GetMode;              // +0x1F0: bool (__fastcall *GetMode)(int modeIndex, void* pMode);
    };
    union {
        void* SetMaxFPS;            // +0x1F8 (Legacy alias)
        void* GetCurrentMode;       // +0x1F8: bool (__fastcall *GetCurrentMode)(void* pMode);
    };
};
#pragma pack(pop)

static_assert(sizeof(SVidDriverAPI) == 0x200, "SVidDriverAPI size must be exactly 0x200 (512 bytes)");
static_assert(offsetof(SVidDriverAPI, Init) == 0x020, "Init must be at offset 0x020");
static_assert(offsetof(SVidDriverAPI, SetMode) == 0x030, "SetMode must be at offset 0x030");
static_assert(offsetof(SVidDriverAPI, BeginFrame) == 0x068, "BeginFrame must be at offset 0x068");
static_assert(offsetof(SVidDriverAPI, EndFrame) == 0x070, "EndFrame must be at offset 0x070");
static_assert(offsetof(SVidDriverAPI, BindVertexBuffer) == 0x178, "BindVertexBuffer must be at offset 0x178");
static_assert(offsetof(SVidDriverAPI, RegisterVertexShader) == 0x178, "RegisterVertexShader must be at offset 0x178");
static_assert(offsetof(SVidDriverAPI, RegisterModel) == 0x1A0, "RegisterModel must be at offset 0x1A0");
static_assert(offsetof(SVidDriverAPI, SetVSync) == 0x1F0, "SetVSync must be at offset 0x1F0");
static_assert(offsetof(SVidDriverAPI, GetMode) == 0x1F0, "GetMode must be at offset 0x1F0");
static_assert(offsetof(SVidDriverAPI, SetMaxFPS) == 0x1F8, "SetMaxFPS must be at offset 0x1F8");
static_assert(offsetof(SVidDriverAPI, GetCurrentMode) == 0x1F8, "GetCurrentMode must be at offset 0x1F8");
