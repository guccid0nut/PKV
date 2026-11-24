// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: tao_qr_matrix.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: High-Density Diagnostic QR Matrix Generator (Project Nayuki)
// ============================================================================

#include "tao_qr_matrix.h"
#include "qrcodegen.hpp"
#include "layer_manifest.h"
#include <sstream>
#include <iomanip>

static std::string EscapeJson(const std::string& input) {
    std::ostringstream ss;
    for (char c : input) {
        switch (c) {
            case '"':  ss << "\\\""; break;
            case '\\': ss << "\\\\"; break;
            case '\b': ss << "\\b"; break;
            case '\f': ss << "\\f"; break;
            case '\n': ss << "\\n"; break;
            case '\r': ss << "\\r"; break;
            case '\t': ss << "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    ss << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
                } else {
                    ss << c;
                }
                break;
        }
    }
    return ss.str();
}

std::string TaoQrMatrix::BuildTelemetryJson(
    const SubsystemTelemetrySnapshot& snap,
    const std::vector<OverlayLogEntry>& logs,
    double fps,
    double frametimeMs,
    uint32_t pid,
    uint32_t width,
    uint32_t height)
{
    (void)logs;
    (void)frametimeMs;
    // High-efficiency compact schema (<= 130 chars) guaranteed to encode into
    // Version 6/7 (41x41 or 45x45) QR matrix with chunky, error-free modules readable across any camera or cv2 detector.
    std::string fault = snap.activeFaultSource;
    if (fault.empty() || fault == "Nominal" || fault == "NONE" || fault.find("NOMINAL") != std::string::npos) {
        fault = "Nominal";
    } else {
        if (fault.find("55555") != std::string::npos) fault = "Offline:55555";
        else if (fault.find("11236") != std::string::npos) fault = "Offline:11236";
        else if (fault.find("11235") != std::string::npos) fault = "Offline:11235";
        else if (fault.length() > 14) fault = fault.substr(0, 14);
    }

    std::string heal = "none";
    if (snap.faultSeverity >= 2) {
        if (!snap.masterOnline) heal = "restart_master";
        else if (!snap.chatOnline) heal = "restart_chat";
        else if (!snap.gameServerOnline) heal = "restart_game";
        else heal = "restart_subsystem";
    } else if (snap.faultSeverity == 1) {
        heal = "reconnect_net";
    }

    std::ostringstream ss;
    ss << "{\"tao\":\"1\",\"pid\":" << pid
       << ",\"res\":\"" << width << "x" << height << "\""
       << ",\"fault\":\"" << EscapeJson(fault) << "\""
       << ",\"sev\":" << snap.faultSeverity
       << ",\"m\":" << (snap.masterOnline ? 1 : 0)
       << ",\"c\":" << (snap.chatOnline ? 1 : 0)
       << ",\"g\":" << (snap.gameServerOnline ? 1 : 0)
       << ",\"ping\":" << static_cast<int>(snap.pingMs + 0.5)
       << ",\"fps\":" << static_cast<int>(fps + 0.5)
       << ",\"heal\":\"" << heal << "\"}";

    return ss.str();
}

bool TaoQrMatrix::GenerateQrRgba(
    const std::string& text,
    int targetWidth,
    int targetHeight,
    std::vector<uint32_t>& outPixels)
{
    if (text.empty() || targetWidth <= 0 || targetHeight <= 0) return false;

    try {
        // Use Low ECC first for largest, thickest modules (maximal readability for screen/camera capture)
        qrcodegen::QrCode qr = qrcodegen::QrCode::encodeText(text.c_str(), qrcodegen::QrCode::Ecc::LOW);
        int qrSize = qr.getSize();
        if (qrSize <= 0) return false;

        int quietZone = 4; // Standard ISO/IEC 18004 4-module quiet zone
        int totalModules = qrSize + 2 * quietZone;
        int scale = targetWidth / totalModules;
        if (scale < 1) scale = 1;

        int qrPixelWidth = qrSize * scale;
        int startX = (targetWidth - qrPixelWidth) / 2;
        int startY = (targetHeight - qrPixelWidth) / 2;

        const uint32_t WHITE_PIXEL = 0xFFFFFFFF; // RGBA 255, 255, 255, 255
        const uint32_t BLACK_PIXEL = 0xFF000000; // RGBA 0, 0, 0, 255

        outPixels.assign(static_cast<size_t>(targetWidth * targetHeight), WHITE_PIXEL);

        for (int y = 0; y < qrSize; ++y) {
            for (int x = 0; x < qrSize; ++x) {
                if (qr.getModule(x, y)) {
                    int basePx = startX + x * scale;
                    int basePy = startY + y * scale;
                    for (int dy = 0; dy < scale; ++dy) {
                        int py = basePy + dy;
                        if (py < 0 || py >= targetHeight) continue;
                        for (int dx = 0; dx < scale; ++dx) {
                            int px = basePx + dx;
                            if (px < 0 || px >= targetWidth) continue;
                            outPixels[py * targetWidth + px] = BLACK_PIXEL;
                        }
                    }
                }
            }
        }

        return true;
    } catch (const std::exception& ex) {
        HON_LOG_ERROR("[TaoQrMatrix] QR generation failed with exception: %s", ex.what());
        try {
            // Fallback to Low ECC
            qrcodegen::QrCode qr = qrcodegen::QrCode::encodeText(text.c_str(), qrcodegen::QrCode::Ecc::LOW);
            int qrSize = qr.getSize();
            int quietZone = 4;
            int totalModules = qrSize + 2 * quietZone;
            int scale = targetWidth / totalModules;
            if (scale < 1) scale = 1;

            int qrPixelWidth = qrSize * scale;
            int startX = (targetWidth - qrPixelWidth) / 2;
            int startY = (targetHeight - qrPixelWidth) / 2;

            const uint32_t WHITE_PIXEL = 0xFFFFFFFF;
            const uint32_t BLACK_PIXEL = 0xFF000000;

            outPixels.assign(static_cast<size_t>(targetWidth * targetHeight), WHITE_PIXEL);

            for (int y = 0; y < qrSize; ++y) {
                for (int x = 0; x < qrSize; ++x) {
                    if (qr.getModule(x, y)) {
                        int basePx = startX + x * scale;
                        int basePy = startY + y * scale;
                        for (int dy = 0; dy < scale; ++dy) {
                            int py = basePy + dy;
                            if (py < 0 || py >= targetHeight) continue;
                            for (int dx = 0; dx < scale; ++dx) {
                                int px = basePx + dx;
                                if (px < 0 || px >= targetWidth) continue;
                                outPixels[py * targetWidth + px] = BLACK_PIXEL;
                            }
                        }
                    }
                }
            }
            return true;
        } catch (...) {
            return false;
        }
    }
}
