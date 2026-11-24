// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: tao_qr_matrix.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: High-Density Diagnostic QR Matrix Generator (Project Nayuki)
// ============================================================================

#pragma once

#include <string>
#include <vector>
#include <cstdint>
#include "overlay_bridge.h"

class TaoQrMatrix {
public:
    // Serializes a compact JSON payload encoding all distributed telemetry metrics
    static std::string BuildTelemetryJson(
        const SubsystemTelemetrySnapshot& snap,
        const std::vector<OverlayLogEntry>& logs,
        double fps,
        double frametimeMs,
        uint32_t pid,
        uint32_t width,
        uint32_t height);

    // Rasterizes the payload into a 32-bit RGBA pixel array with quiet zone and integer module scaling
    static bool GenerateQrRgba(
        const std::string& text,
        int targetWidth,
        int targetHeight,
        std::vector<uint32_t>& outPixels);
};
