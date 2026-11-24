# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(kongor_zlib LANGUAGES C CXX ASM_MASM)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# libdeflate static library configuration
set(LIBDEFLATE_BUILD_STATIC_LIB ON CACHE BOOL "" FORCE)
set(LIBDEFLATE_BUILD_SHARED_LIB OFF CACHE BOOL "" FORCE)
set(LIBDEFLATE_BUILD_GZIP OFF CACHE BOOL "" FORCE)
set(LIBDEFLATE_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LIBDEFLATE_INSTALL OFF CACHE BOOL "" FORCE)

if(NOT TARGET libdeflate_static)
    add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../../deps/libdeflate" "${CMAKE_CURRENT_BINARY_DIR}/libdeflate_build")
endif()

add_library(zlibwapi_x64 SHARED
    proxy_zlib.cpp
    proxy_zlib.asm
)

target_include_directories(zlibwapi_x64 PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../deps/libdeflate"
)

target_link_libraries(zlibwapi_x64 PRIVATE
    libdeflate_static
)

if(MSVC)
    set_target_properties(zlibwapi_x64 PROPERTIES
        LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/zlibwapi.def\""
        OUTPUT_NAME "zlibwapi_x64"
    )
endif()

option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy zlibwapi_x64.dll to games/client/*" OFF)

# Deploy zlibwapi_x64.dll to games/client/ and games/client/windows-x64/ only when explicitly requested (NTFS hot-deploy + .bak safe)
if(KONGOR_DEPLOY_TO_GAMES AND EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client")
    add_custom_command(TARGET zlibwapi_x64 POST_BUILD
        COMMAND ${CMAKE_COMMAND}
            "-DSRC=$<TARGET_FILE:zlibwapi_x64>"
            "-DDST1=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/zlibwapi_x64.dll"
            "-DDST2=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64/zlibwapi_x64.dll"
            "-DDST3="
            "-DDST4="
            -P "${CMAKE_CURRENT_SOURCE_DIR}/../native_guard/deploy_version_dll.cmake"
        COMMENT "Deploying optimized zlibwapi_x64.dll to games/client/ (NTFS hot-deploy safe)"
    )
endif()
