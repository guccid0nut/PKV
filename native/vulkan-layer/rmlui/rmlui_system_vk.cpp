// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: rmlui_system_vk.cpp
// System: ProjectKongor - Native Vulkan Layer (VkLayer_hon_replay_capture)
// Subsystem: Modern In-Game HUD via RmlUi - System Interface
// ============================================================================

#include "rmlui_system_vk.h"
#include "../layer_manifest.h"
#include <iostream>

RmlUi_SystemInterface_VK::RmlUi_SystemInterface_VK() {
    QueryPerformanceFrequency(&m_qpcFrequency);
    QueryPerformanceCounter(&m_qpcStart);

    // Default log file path in logs directory
    CreateDirectoryA("logs", nullptr);
    m_logFile.open("logs/rmlui_overlay.log", std::ios::out | std::ios::app);
    if (m_logFile.is_open()) {
        m_logFile << "[RmlUi] SystemInterface initialized. QPC frequency: " 
                  << m_qpcFrequency.QuadPart << " Hz." << std::endl;
    }
}

RmlUi_SystemInterface_VK::~RmlUi_SystemInterface_VK() {
    std::lock_guard<std::mutex> lock(m_logMutex);
    if (m_logFile.is_open()) {
        m_logFile << "[RmlUi] SystemInterface shutting down." << std::endl;
        m_logFile.close();
    }
}

void RmlUi_SystemInterface_VK::SetLogPath(const std::string& logPath) {
    std::lock_guard<std::mutex> lock(m_logMutex);
    if (m_logFile.is_open()) {
        m_logFile.close();
    }
    m_logFile.open(logPath, std::ios::out | std::ios::app);
}

double RmlUi_SystemInterface_VK::GetElapsedTime() {
    LARGE_INTEGER current;
    QueryPerformanceCounter(&current);
    return static_cast<double>(current.QuadPart - m_qpcStart.QuadPart) / static_cast<double>(m_qpcFrequency.QuadPart);
}

bool RmlUi_SystemInterface_VK::LogMessage(Rml::Log::Type type, const Rml::String& message) {
    const char* typeStr = "INFO";
    switch (type) {
        case Rml::Log::LT_ERROR:   typeStr = "ERROR"; break;
        case Rml::Log::LT_ASSERT:  typeStr = "ASSERT"; break;
        case Rml::Log::LT_WARNING: typeStr = "WARN"; break;
        case Rml::Log::LT_INFO:    typeStr = "INFO"; break;
        case Rml::Log::LT_DEBUG:   typeStr = "DEBUG"; break;
        default: break;
    }

    {
        std::lock_guard<std::mutex> lock(m_logMutex);
        if (m_logFile.is_open()) {
            m_logFile << "[RmlUi][" << typeStr << "] " << message << std::endl;
            m_logFile.flush();
        }
    }

    if (type == Rml::Log::LT_ERROR || type == Rml::Log::LT_ASSERT) {
        HON_LOG_ERROR("[RmlUi] %s", message.c_str());
    } else if (type == Rml::Log::LT_WARNING) {
        HON_LOG_WARN("[RmlUi] %s", message.c_str());
    } else {
        HON_LOG_DEBUG("[RmlUi] %s", message.c_str());
    }

    return true;
}

void RmlUi_SystemInterface_VK::SetClipboardText(const Rml::String& text) {
    if (!OpenClipboard(nullptr)) return;
    EmptyClipboard();

    HGLOBAL hGlob = GlobalAlloc(GMEM_MOVEABLE, text.size() + 1);
    if (hGlob) {
        char* pBuf = static_cast<char*>(GlobalLock(hGlob));
        if (pBuf) {
            memcpy(pBuf, text.c_str(), text.size() + 1);
            GlobalUnlock(hGlob);
            SetClipboardData(CF_TEXT, hGlob);
        }
    }
    CloseClipboard();
}

void RmlUi_SystemInterface_VK::GetClipboardText(Rml::String& text) {
    text.clear();
    if (!OpenClipboard(nullptr)) return;

    HANDLE hData = GetClipboardData(CF_TEXT);
    if (hData) {
        char* pText = static_cast<char*>(GlobalLock(hData));
        if (pText) {
            text = pText;
            GlobalUnlock(hData);
        }
    }
    CloseClipboard();
}
