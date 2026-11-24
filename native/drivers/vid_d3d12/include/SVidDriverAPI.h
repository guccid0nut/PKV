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
    void* SetCamera;            // +0x078 (Discovered: const CCamera* / CSceneView* setup; previously hypothesized as Clear)

    // 0x080 - 0x0A0: 2D Immediate Primitives
    void* Add2dRect;            // +0x080
    void* Add2dQuad;            // +0x088
    void* Add2dTri;             // +0x090
    void* Add2dLine;            // +0x098
    void* Flush2D;              // +0x0A0

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
static_assert(offsetof(SVidDriverAPI, SetMode) == 0x030, "SetMode must be at offset 0x030");
static_assert(offsetof(SVidDriverAPI, BeginFrame) == 0x068, "BeginFrame must be at offset 0x068");
static_assert(offsetof(SVidDriverAPI, EndFrame) == 0x070, "EndFrame must be at offset 0x070");
static_assert(offsetof(SVidDriverAPI, SetMaxFPS) == 0x1F8, "SetMaxFPS must be at offset 0x1F8");
