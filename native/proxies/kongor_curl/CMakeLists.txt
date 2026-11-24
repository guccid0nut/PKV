# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(libcurl_x64 LANGUAGES CXX ASM_MASM)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

add_library(libcurl_x64 SHARED
    proxy_curl.cpp
    proxy_curl.asm
)

if(MSVC)
    set_target_properties(libcurl_x64 PROPERTIES
        LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/libcurl.def\""
        OUTPUT_NAME "libcurl_x64"
    )
endif()

option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy libcurl_x64.dll to games/client/*" OFF)

if(KONGOR_DEPLOY_TO_GAMES)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client")
        add_custom_command(TARGET libcurl_x64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:libcurl_x64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/libcurl_x64.dll"
            COMMENT "Deploying libcurl_x64.dll to games/client/"
        )
    endif()
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64")
        add_custom_command(TARGET libcurl_x64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:libcurl_x64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64/libcurl_x64.dll"
            COMMENT "Deploying libcurl_x64.dll to games/client/windows-x64/"
        )
    endif()
endif()
