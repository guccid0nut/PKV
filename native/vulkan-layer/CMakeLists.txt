# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(VkLayer_hon_replay_capture LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Check target architecture
if(CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(STATUS "Configuring VkLayer_hon_replay_capture for 32-bit (x86)")
elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(STATUS "Configuring VkLayer_hon_replay_capture for 64-bit (x64)")
endif()

# RmlUi Modern In-Game HUD Subsystem
set(NO_FONT_INTERFACE_DEFAULT ON CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(RMLUI_SAMPLES OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../deps/rmlui" rmlui_build EXCLUDE_FROM_ALL)

set(LAYER_SOURCES
    layer_dispatch.cpp
    frame_capturer.cpp
    rcas_filter.cpp
    rmlui/rmlui_system_vk.cpp
    rmlui/font_engine_stb.cpp
    rmlui/rmlui_render_vk.cpp
    rmlui/overlay_input.cpp
    rmlui/overlay_bridge.cpp
    rmlui/overlay_manager.cpp
    rmlui/tao_qr_matrix.cpp
    deps/qrcodegen/qrcodegen.cpp
    layer_manifest.h
    frame_capturer.h
    rcas_filter.h
    rmlui/rmlui_system_vk.h
    rmlui/font_engine_stb.h
    rmlui/rmlui_render_vk.h
    rmlui/rmlui_vk_suballocator.h
    rmlui/overlay_input.h
    rmlui/overlay_bridge.h
    rmlui/overlay_manager.h
    rmlui/tao_qr_matrix.h
    deps/qrcodegen/qrcodegen.hpp
)

add_library(VkLayer_hon_replay_capture SHARED ${LAYER_SOURCES})

target_compile_definitions(VkLayer_hon_replay_capture PRIVATE
    VK_USE_PLATFORM_WIN32_KHR
    _CRT_SECURE_NO_WARNINGS
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    RMLUI_STATIC_LIB
)

target_include_directories(VkLayer_hon_replay_capture PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/rmlui
    ${CMAKE_CURRENT_SOURCE_DIR}/deps/qrcodegen
    ${CMAKE_CURRENT_SOURCE_DIR}/../deps/rmlui/Include
    ${CMAKE_CURRENT_SOURCE_DIR}/../deps/rmlui/Backends
)

target_link_libraries(VkLayer_hon_replay_capture PRIVATE RmlCore ws2_32)

target_compile_features(VkLayer_hon_replay_capture PRIVATE cxx_std_23)

if(MSVC)
    target_compile_options(VkLayer_hon_replay_capture PRIVATE
        /W4
        /MP
        /EHsc
        /std:c++latest
        /permissive-
    )
endif()

# Output dll name exactly as specified in the layer manifest
set_target_properties(VkLayer_hon_replay_capture PROPERTIES
    OUTPUT_NAME "VkLayer_hon_replay_capture"
    PREFIX ""
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF
)

option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy VkLayer_hon_replay_capture.dll and manifest to games/client/*" OFF)

# Copy the manifest JSON next to target binary; deploy DLL to game directories only when KONGOR_DEPLOY_TO_GAMES=ON
add_custom_command(TARGET VkLayer_hon_replay_capture POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/VkLayer_hon_replay_capture.json"
        "$<TARGET_FILE_DIR:VkLayer_hon_replay_capture>/VkLayer_hon_replay_capture.json"
    COMMENT "Copying VkLayer_hon_replay_capture.json to build output"
)
if(KONGOR_DEPLOY_TO_GAMES)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client")
        add_custom_command(TARGET VkLayer_hon_replay_capture POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:VkLayer_hon_replay_capture>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client/VkLayer_hon_replay_capture.dll"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${CMAKE_CURRENT_SOURCE_DIR}/VkLayer_hon_replay_capture.json"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client/VkLayer_hon_replay_capture.json"
            COMMENT "Deploying VkLayer_hon_replay_capture.dll and manifest to games/client/"
        )
    endif()
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client/windows-x64")
        add_custom_command(TARGET VkLayer_hon_replay_capture POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:VkLayer_hon_replay_capture>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client/windows-x64/VkLayer_hon_replay_capture.dll"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${CMAKE_CURRENT_SOURCE_DIR}/VkLayer_hon_replay_capture.json"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../games/client/windows-x64/VkLayer_hon_replay_capture.json"
            COMMENT "Deploying VkLayer_hon_replay_capture.dll and manifest to games/client/windows-x64/"
        )
    endif()
endif()
