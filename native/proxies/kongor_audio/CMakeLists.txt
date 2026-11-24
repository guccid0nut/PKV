# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(kongor_audio LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

add_library(fmodex64 SHARED
    fmod_audio_proxy.cpp
)

target_include_directories(fmodex64 PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}"
)

target_link_libraries(fmodex64 PRIVATE
    kernel32
    user32
    ole32
)

if(MSVC)
    set_target_properties(fmodex64 PROPERTIES
        LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/fmodex64.def\""
        OUTPUT_NAME "fmodex64"
    )
endif()

option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy fmodex64.dll to games/client/* and artifacts/native-binaries/*" OFF)

# Deploy fmodex64.dll to games/client/, games/client/windows-x64/, and staging only when explicitly requested
if(KONGOR_DEPLOY_TO_GAMES)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client")
        add_custom_command(TARGET fmodex64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:fmodex64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/fmodex64.dll"
            COMMENT "Deploying modernized 256-voice low-latency fmodex64.dll to games/client/"
        )
    endif()
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64")
        add_custom_command(TARGET fmodex64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:fmodex64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64/fmodex64.dll"
            COMMENT "Deploying modernized 256-voice low-latency fmodex64.dll to games/client/windows-x64/"
        )
    endif()
    add_custom_command(TARGET fmodex64 POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:fmodex64>"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64/fmodex64_spatial.dll"
        COMMENT "Deploying modernized 256-voice low-latency fmodex64_spatial.dll to artifacts/native-binaries/windows-x64/"
    )
endif()
