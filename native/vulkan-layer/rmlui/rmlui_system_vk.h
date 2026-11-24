// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rmlui_system_vk.h
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - System Interface
// ============================================================================

#pragma once

#include <RmlUi/Core/SystemInterface.h>
#include <windows.h>
#include <string>
#include <mutex>
#include <fstream>

class RmlUi_SystemInterface_VK : public Rml::SystemInterface {
public:
    RmlUi_SystemInterface_VK();
    ~RmlUi_SystemInterface_VK() override;

    void SetLogPath(const std::string& logPath);

    // -- Rml::SystemInterface implementation --
    double GetElapsedTime() override;
    bool LogMessage(Rml::Log::Type type, const Rml::String& message) override;
    void SetClipboardText(const Rml::String& text) override;
    void GetClipboardText(Rml::String& text) override;

private:
    LARGE_INTEGER m_qpcFrequency;
    LARGE_INTEGER m_qpcStart;
    std::mutex m_logMutex;
    std::ofstream m_logFile;
};
