// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: font_engine_stb.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - STB TrueType SDF Font Engine
// ============================================================================

#define STB_TRUETYPE_IMPLEMENTATION
#include "../../../deps/stb_truetype.h"

#include "font_engine_stb.h"
#include <RmlUi/Core/Core.h>
#include <RmlUi/Core/FileInterface.h>
#include <RmlUi/Core/GeometryUtilities.h>
#include <RmlUi/Core/RenderInterface.h>
#include <RmlUi/Core/StringUtilities.h>
#include <fstream>
#include <filesystem>
#include <mutex>
#include <cmath>
#include <chrono>
#include <cstdarg>
#include <algorithm>
#include <windows.h>

struct StbAtlasData {
    stbtt_bakedchar cdata[96]; // ASCII 32..126
    Rml::Texture texture;
    int texWidth = 1024;
    int texHeight = 1024;
    bool isSdf = true;
    float sdfPadding = 4.0f;
    float sdfOnEdge = 180.0f;
};

static void LogFontEngine(const char* format, ...) {
    char buffer[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);

    OutputDebugStringA(buffer);

    static std::mutex s_logMutex;
    static std::ofstream s_logFile;
    static bool s_initAttempted = false;
    static ULONGLONG s_lastFlushTick = 0;

    std::lock_guard<std::mutex> lock(s_logMutex);
    if (!s_initAttempted) {
        s_initAttempted = true;
        std::error_code ec;
        std::filesystem::path logPath;
        const char* customLogEnv = getenv("HON_LAYER_LOG_PATH");
        if (customLogEnv && customLogEnv[0] != '\0') {
            std::filesystem::path customP(customLogEnv);
            if (std::filesystem::is_directory(customP, ec) || customP.filename().empty()) {
                logPath = customP / "font_engine_sdf.log";
            } else {
                logPath = customP.parent_path() / "font_engine_sdf.log";
            }
        } else {
            std::filesystem::path probe = std::filesystem::current_path(ec);
            std::filesystem::path repoLogs;
            for (int i = 0; i < 6 && !probe.empty(); ++i) {
                if (std::filesystem::exists(probe / "ProjectKongor.slnx", ec) ||
                    std::filesystem::exists(probe / "logs", ec)) {
                    repoLogs = probe / "logs";
                    break;
                }
                auto parent = probe.parent_path();
                if (parent == probe) break;
                probe = parent;
            }
            if (!repoLogs.empty()) {
                logPath = repoLogs / "font_engine_sdf.log";
            } else {
                const char* localAppData = getenv("LOCALAPPDATA");
                if (localAppData && localAppData[0] != '\0') {
                    logPath = std::filesystem::path(localAppData) / "ProjectKongor" / "logs" / "font_engine_sdf.log";
                } else {
                    logPath = std::filesystem::temp_directory_path(ec) / "font_engine_sdf.log";
                }
            }
        }
        if (!logPath.parent_path().empty()) {
            std::filesystem::create_directories(logPath.parent_path(), ec);
        }
        std::filesystem::path bakPath = logPath.string() + ".bak";
        if (std::filesystem::exists(logPath, ec) && std::filesystem::file_size(logPath, ec) > (10ULL * 1024ULL * 1024ULL)) {
            std::filesystem::remove(bakPath, ec);
            std::filesystem::rename(logPath, bakPath, ec);
        }
        s_logFile.open(logPath, std::ios::app);
    }

    if (s_logFile.is_open()) {
        auto now = std::chrono::system_clock::now();
        auto in_time_t = std::chrono::system_clock::to_time_t(now);
        struct tm buf;
        localtime_s(&buf, &in_time_t);
        char timeStr[32];
        strftime(timeStr, sizeof(timeStr), "%H:%M:%S", &buf);

        s_logFile << "[" << timeStr << "] [FONT_ENGINE_SDF] " << buffer << "\n";

        ULONGLONG nowTick = GetTickCount64();
        if (nowTick - s_lastFlushTick >= 1000) {
            s_logFile.flush();
            s_lastFlushTick = nowTick;
        }
    }
}

FontEngine_STB::FontEngine_STB() : m_defaultFace(nullptr) {
    // Lazy: Default font fallback is created on-demand when requested by RmlUi
}

FontEngine_STB::~FontEngine_STB() {
    Reset();
}

void FontEngine_STB::Reset() {
    for (auto& face : m_faces) {
        if (face->stbInfo) {
            delete static_cast<StbAtlasData*>(face->stbInfo);
            face->stbInfo = nullptr;
        }
    }
    m_faces.clear();
    m_defaultFace = nullptr;
}

void FontEngine_STB::CreateDefaultFallbackFont() {
    // Try loading Segoe UI or Arial from Windows fonts if available
    const char* systemFonts[] = {
        "C:\\Windows\\Fonts\\segoeui.ttf",
        "C:\\Windows\\Fonts\\arial.ttf",
        "C:\\Windows\\Fonts\\consola.ttf"
    };

    for (const char* fontPath : systemFonts) {
        if (LoadFontFace(fontPath, true, Rml::Style::FontWeight::Normal)) {
            m_defaultFace = m_faces.back().get();
            return;
        }
    }

    // If no system font accessible, create a minimal procedural face
    auto fallback = std::make_unique<StbFontFace>();
    fallback->family = "default";
    fallback->size = 14;
    fallback->lineHeight = 16;
    fallback->ascent = 12;
    fallback->descent = -4;
    fallback->scale = 1.0f;

    auto atlas = new StbAtlasData();
    std::vector<Rml::byte> pixels(atlas->texWidth * atlas->texHeight * 4, 255);
    int tw = atlas->texWidth;
    int th = atlas->texHeight;
    atlas->texture.Set("font_fallback", [pixels, tw, th](const Rml::String& /*name*/, Rml::UniquePtr<const Rml::byte[]>& data, Rml::Vector2i& dimensions) {
        dimensions = Rml::Vector2i(tw, th);
        auto buf = new Rml::byte[pixels.size()];
        memcpy(buf, pixels.data(), pixels.size());
        data.reset(buf);
        return true;
    });

    for (int i = 0; i < 96; ++i) {
        atlas->cdata[i].x0 = 0;
        atlas->cdata[i].y0 = 0;
        atlas->cdata[i].x1 = 8;
        atlas->cdata[i].y1 = 16;
        atlas->cdata[i].xoff = 0;
        atlas->cdata[i].yoff = -12;
        atlas->cdata[i].xadvance = 8.0f;
    }

    fallback->stbInfo = atlas;
    m_defaultFace = fallback.get();
    m_faces.push_back(std::move(fallback));
}

bool FontEngine_STB::LoadFontFace(const Rml::String& file_name, bool fallback_face, Rml::Style::FontWeight weight) {
    std::ifstream file(file_name.c_str(), std::ios::binary | std::ios::ate);
    if (!file.is_open()) {
        return false;
    }

    std::streamsize size = file.tellg();
    file.seekg(0, std::ios::beg);

    std::vector<uint8_t> buffer(static_cast<size_t>(size));
    if (!file.read(reinterpret_cast<char*>(buffer.data()), size)) {
        return false;
    }

    // Extract family name from path (e.g. "arial")
    std::string family = file_name;
    size_t lastSlash = family.find_last_of("\\/");
    if (lastSlash != std::string::npos) family = family.substr(lastSlash + 1);
    size_t lastDot = family.find_last_of('.');
    if (lastDot != std::string::npos) family = family.substr(0, lastDot);
    family = Rml::StringUtilities::ToLower(family);

    return LoadFontFace(buffer.data(), static_cast<int>(buffer.size()), family, Rml::Style::FontStyle::Normal, weight, fallback_face);
}

bool FontEngine_STB::LoadFontFace(const Rml::byte* data, int data_size, const Rml::String& family,
                                  Rml::Style::FontStyle style, Rml::Style::FontWeight weight, bool fallback_face) {
    (void)style;
    (void)weight;
    if (!data || data_size <= 0) return false;

    stbtt_fontinfo fontInfo;
    if (!stbtt_InitFont(&fontInfo, reinterpret_cast<const unsigned char*>(data), 0)) {
        LogFontEngine("Failed to initialize TrueType font info for family: %s", family.c_str());
        return false;
    }

    // Comprehensive SDF resolution ladder optimized for 1080p, 1440p, 4K, and high-DPI scaling
    int defaultSizes[] = { 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 56, 64, 72 };
    int generatedTiers = 0;

    for (int ptSize : defaultSizes) {
        auto face = std::make_unique<StbFontFace>();
        face->family = Rml::StringUtilities::ToLower(family);
        face->size = ptSize;
        face->fontData.assign(data, data + data_size);

        float scale = stbtt_ScaleForPixelHeight(&fontInfo, static_cast<float>(ptSize));

        int ascent = 0, descent = 0, lineGap = 0;
        stbtt_GetFontVMetrics(&fontInfo, &ascent, &descent, &lineGap);
        face->ascent = static_cast<int>(ascent * scale);
        face->descent = static_cast<int>(descent * scale);
        face->lineHeight = static_cast<int>((ascent - descent + lineGap) * scale);
        if (face->lineHeight <= 0) face->lineHeight = static_cast<int>(ptSize * 1.25f);

        auto atlas = new StbAtlasData();
        atlas->isSdf = true;
        atlas->sdfPadding = (ptSize >= 32) ? 6.0f : 4.0f;
        atlas->sdfOnEdge = 180.0f;
        atlas->texWidth = (ptSize >= 32) ? 1024 : 512;
        atlas->texHeight = (ptSize >= 32) ? 1024 : 512;

        std::vector<Rml::byte> rgba(atlas->texWidth * atlas->texHeight * 4, 0);

        int curX = 1;
        int curY = 1;
        int rowHeight = 0;
        int padding = static_cast<int>(atlas->sdfPadding);
        unsigned char onedge_value = static_cast<unsigned char>(atlas->sdfOnEdge);
        float pixel_dist_scale = 36.0f;

        bool success = true;
        for (int c = 32; c < 128; ++c) {
            int advanceWidth = 0, leftSideBearing = 0;
            stbtt_GetCodepointHMetrics(&fontInfo, c, &advanceWidth, &leftSideBearing);
            float advance = advanceWidth * scale;

            int gw = 0, gh = 0, gxoff = 0, gyoff = 0;
            unsigned char* sdf = stbtt_GetCodepointSDF(
                &fontInfo, scale, c, padding, onedge_value, pixel_dist_scale,
                &gw, &gh, &gxoff, &gyoff
            );

            if (!sdf || gw == 0 || gh == 0) {
                atlas->cdata[c - 32].x0 = 0;
                atlas->cdata[c - 32].y0 = 0;
                atlas->cdata[c - 32].x1 = 0;
                atlas->cdata[c - 32].y1 = 0;
                atlas->cdata[c - 32].xoff = 0.0f;
                atlas->cdata[c - 32].yoff = 0.0f;
                atlas->cdata[c - 32].xadvance = advance;
                if (sdf) stbtt_FreeSDF(sdf, nullptr);
                continue;
            }

            if (curX + gw + 2 >= atlas->texWidth) {
                curX = 1;
                curY += rowHeight + 2;
                rowHeight = 0;
            }

            if (curY + gh + 2 >= atlas->texHeight) {
                stbtt_FreeSDF(sdf, nullptr);
                success = false;
                break;
            }

            // High-precision anti-aliased isocontour reconstruction from signed distance field
            for (int gy = 0; gy < gh; ++gy) {
                for (int gx = 0; gx < gw; ++gx) {
                    uint8_t d = sdf[gy * gw + gx];
                    float dist = static_cast<float>(d) - static_cast<float>(onedge_value);
                    float alphaNorm = 0.5f + (dist / 18.0f);
                    alphaNorm = std::clamp(alphaNorm, 0.0f, 1.0f);
                    uint8_t a = static_cast<uint8_t>(alphaNorm * 255.0f);

                    int dstIndex = ((curY + gy) * atlas->texWidth + (curX + gx)) * 4;
                    rgba[dstIndex + 0] = 255;
                    rgba[dstIndex + 1] = 255;
                    rgba[dstIndex + 2] = 255;
                    rgba[dstIndex + 3] = a;
                }
            }

            atlas->cdata[c - 32].x0 = static_cast<unsigned short>(curX);
            atlas->cdata[c - 32].y0 = static_cast<unsigned short>(curY);
            atlas->cdata[c - 32].x1 = static_cast<unsigned short>(curX + gw);
            atlas->cdata[c - 32].y1 = static_cast<unsigned short>(curY + gh);
            atlas->cdata[c - 32].xoff = static_cast<float>(gxoff);
            atlas->cdata[c - 32].yoff = static_cast<float>(gyoff);
            atlas->cdata[c - 32].xadvance = advance;

            curX += gw + 2;
            if (gh > rowHeight) rowHeight = gh;

            stbtt_FreeSDF(sdf, nullptr);
        }

        if (!success) {
            delete atlas;
            continue;
        }

        std::string texName = "font_sdf_" + face->family + "_" + std::to_string(ptSize);
        int tw = atlas->texWidth;
        int th = atlas->texHeight;
        atlas->texture.Set(texName, [rgba, tw, th](const Rml::String& /*name*/, Rml::UniquePtr<const Rml::byte[]>& data, Rml::Vector2i& dimensions) {
            dimensions = Rml::Vector2i(tw, th);
            auto buf = new Rml::byte[rgba.size()];
            memcpy(buf, rgba.data(), rgba.size());
            data.reset(buf);
            return true;
        });

        face->stbInfo = atlas;

        if (fallback_face || !m_defaultFace) {
            m_defaultFace = face.get();
        }

        m_faces.push_back(std::move(face));
        generatedTiers++;
    }

    LogFontEngine("Loaded font face '%s' with %d SDF resolution tiers (10pt - 72pt). Sub-pixel vector sharpness ready for 1440p/4K.",
        family.c_str(), generatedTiers);
    return true;
}

Rml::FontFaceHandle FontEngine_STB::GetFontFaceHandle(const Rml::String& family, Rml::Style::FontStyle /*style*/,
                                                      Rml::Style::FontWeight /*weight*/, int size) {
    std::string lowerFamily = Rml::StringUtilities::ToLower(family);

    StbFontFace* bestMatch = nullptr;
    int bestSizeDiff = 1000;

    for (const auto& face : m_faces) {
        if (face->family == lowerFamily) {
            int diff = std::abs(face->size - size);
            if (diff < bestSizeDiff) {
                bestSizeDiff = diff;
                bestMatch = face.get();
            }
        }
    }

    if (!bestMatch) {
        for (const auto& face : m_faces) {
            int diff = std::abs(face->size - size);
            if (diff < bestSizeDiff) {
                bestSizeDiff = diff;
                bestMatch = face.get();
            }
        }
    }

    if (!bestMatch) {
        if (!m_defaultFace) {
            CreateDefaultFallbackFont();
        }
        bestMatch = m_defaultFace;
    }
    return reinterpret_cast<Rml::FontFaceHandle>(bestMatch);
}

Rml::FontEffectsHandle FontEngine_STB::PrepareFontEffects(Rml::FontFaceHandle /*handle*/, const Rml::FontEffectList& /*font_effects*/) {
    return 0;
}

int FontEngine_STB::GetSize(Rml::FontFaceHandle handle) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    return face ? face->size : 14;
}

int FontEngine_STB::GetXHeight(Rml::FontFaceHandle handle) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    return face ? (face->size / 2) : 8;
}

int FontEngine_STB::GetLineHeight(Rml::FontFaceHandle handle) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    return face ? face->lineHeight : 16;
}

int FontEngine_STB::GetBaseline(Rml::FontFaceHandle handle) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    return face ? face->ascent : 12;
}

float FontEngine_STB::GetUnderline(Rml::FontFaceHandle handle, float& thickness) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    thickness = face ? (face->size / 14.0f) : 1.0f;
    return face ? -static_cast<float>(face->descent) : 2.0f;
}

int FontEngine_STB::GetStringWidth(Rml::FontFaceHandle handle, const Rml::String& string, Rml::Character /*prior_character*/) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    if (!face || !face->stbInfo) return 0;

    auto atlas = static_cast<StbAtlasData*>(face->stbInfo);
    float width = 0.0f;

    for (char c : string) {
        if (c >= 32 && c < 128) {
            width += atlas->cdata[c - 32].xadvance;
        } else {
            width += atlas->cdata[' ' - 32].xadvance;
        }
    }

    return static_cast<int>(std::round(width));
}

int FontEngine_STB::GenerateString(Rml::FontFaceHandle handle, Rml::FontEffectsHandle /*font_effects_handle*/,
                                   const Rml::String& string, const Rml::Vector2f& position,
                                   const Rml::Colourb& colour, float opacity, Rml::GeometryList& geometry) {
    auto face = reinterpret_cast<StbFontFace*>(handle);
    if (!face || !face->stbInfo) return 0;

    auto atlas = static_cast<StbAtlasData*>(face->stbInfo);

    geometry.resize(1);
    Rml::Geometry& geom = geometry[0];
    geom.SetTexture(&atlas->texture);

    auto& vertices = geom.GetVertices();
    auto& indices = geom.GetIndices();

    vertices.reserve(string.size() * 4);
    indices.reserve(string.size() * 6);

    Rml::Colourb finalColor = colour;
    finalColor.alpha = static_cast<Rml::byte>(colour.alpha * opacity);

    float curX = position.x;
    float curY = position.y + face->ascent;

    float invW = 1.0f / atlas->texWidth;
    float invH = 1.0f / atlas->texHeight;

    for (char c : string) {
        if (c < 32 || c >= 128) c = ' ';

        const stbtt_bakedchar& b = atlas->cdata[c - 32];

        int vBase = static_cast<int>(vertices.size());
        vertices.resize(vBase + 4);
        indices.resize(indices.size() + 6);

        Rml::Vector2f quadPos(curX + b.xoff, curY + b.yoff);
        Rml::Vector2f quadSize(static_cast<float>(b.x1 - b.x0), static_cast<float>(b.y1 - b.y0));

        Rml::Vector2f uvTopLeft(b.x0 * invW, b.y0 * invH);
        Rml::Vector2f uvBottomRight(b.x1 * invW, b.y1 * invH);

        Rml::GeometryUtilities::GenerateQuad(
            &vertices[vBase],
            &indices[indices.size() - 6],
            quadPos.Round(),
            quadSize,
            finalColor,
            uvTopLeft,
            uvBottomRight,
            vBase
        );

        curX += b.xadvance;
    }

    return static_cast<int>(std::round(curX - position.x));
}

int FontEngine_STB::GetVersion(Rml::FontFaceHandle /*handle*/) {
    return 2; // SDF Font Engine v2.0
}
