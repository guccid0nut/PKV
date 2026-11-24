// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: font_engine_stb.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - STB TrueType Font Engine
// ============================================================================

#pragma once

#include <RmlUi/Core/FontEngineInterface.h>
#include <RmlUi/Core/Types.h>
#include <vector>
#include <unordered_map>
#include <memory>
#include <string>

struct StbGlyph {
    int advance = 0;
    int bearingX = 0;
    int bearingY = 0;
    int width = 0;
    int height = 0;
    Rml::TextureHandle texture = 0;
};

struct StbFontFace {
    std::string family;
    int size = 16;
    int ascent = 0;
    int descent = 0;
    int lineGap = 0;
    int lineHeight = 16;
    float scale = 1.0f;
    std::vector<uint8_t> fontData;
    void* stbInfo = nullptr; // stbtt_fontinfo
    std::unordered_map<Rml::Character, StbGlyph> glyphCache;
};

class FontEngine_STB : public Rml::FontEngineInterface {
public:
    FontEngine_STB();
    ~FontEngine_STB() override;
    void Reset();

    bool LoadFontFace(const Rml::String& file_name, bool fallback_face, Rml::Style::FontWeight weight) override;
    bool LoadFontFace(const Rml::byte* data, int data_size, const Rml::String& family, Rml::Style::FontStyle style, Rml::Style::FontWeight weight, bool fallback_face) override;

    Rml::FontFaceHandle GetFontFaceHandle(const Rml::String& family, Rml::Style::FontStyle style, Rml::Style::FontWeight weight, int size) override;
    Rml::FontEffectsHandle PrepareFontEffects(Rml::FontFaceHandle handle, const Rml::FontEffectList& font_effects) override;

    int GetSize(Rml::FontFaceHandle handle) override;
    int GetXHeight(Rml::FontFaceHandle handle) override;
    int GetLineHeight(Rml::FontFaceHandle handle) override;
    int GetBaseline(Rml::FontFaceHandle handle) override;
    float GetUnderline(Rml::FontFaceHandle handle, float& thickness) override;
    int GetStringWidth(Rml::FontFaceHandle handle, const Rml::String& string, Rml::Character prior_character) override;

    int GenerateString(Rml::FontFaceHandle handle, Rml::FontEffectsHandle font_effects_handle,
                       const Rml::String& string, const Rml::Vector2f& position,
                       const Rml::Colourb& colour, float opacity, Rml::GeometryList& geometry) override;

    int GetVersion(Rml::FontFaceHandle handle) override;

private:
    StbGlyph& GetOrCreateGlyph(StbFontFace* face, Rml::Character codepoint);
    void CreateDefaultFallbackFont();

    std::vector<std::unique_ptr<StbFontFace>> m_faces;
    StbFontFace* m_defaultFace;
};
