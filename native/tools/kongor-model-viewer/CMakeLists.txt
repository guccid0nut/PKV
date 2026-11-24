# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(kongor_model_viewer LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

# Configure Raylib build settings
set(BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(BUILD_GAMES OFF CACHE BOOL "" FORCE)
set(CUSTOMIZE_BUILD ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_GLTF ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_OBJ ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_DDS ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_PNG ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_JPG ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_BMP ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_TGA ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_HDR ON CACHE BOOL "" FORCE)
set(SUPPORT_FILEFORMAT_KTX ON CACHE BOOL "" FORCE)

message(STATUS "[MODEL_VIEWER] Fetching Raylib 5.5 from GitHub...")
FetchContent_Declare(
    raylib
    GIT_REPOSITORY https://github.com/raysan5/raylib.git
    GIT_TAG 5.5
    GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(raylib)

# Ensure all texture decoders are active
target_compile_definitions(raylib PRIVATE
    SUPPORT_FILEFORMAT_JPG=1
    SUPPORT_FILEFORMAT_PNG=1
    SUPPORT_FILEFORMAT_BMP=1
    SUPPORT_FILEFORMAT_TGA=1
    SUPPORT_FILEFORMAT_DDS=1
    SUPPORT_FILEFORMAT_HDR=1
    SUPPORT_FILEFORMAT_KTX=1
    SUPPORT_FILEFORMAT_GLTF=1
    SUPPORT_FILEFORMAT_OBJ=1
)

add_executable(kongor-model-viewer
    src/main.cpp
)

target_include_directories(kongor-model-viewer PRIVATE
    ${raylib_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include
)

target_link_libraries(kongor-model-viewer PRIVATE
    raylib
)

if(MSVC)
    target_compile_definitions(kongor-model-viewer PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(kongor-model-viewer PRIVATE /W3 /O2 /MD /arch:AVX2)
elseif(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(kongor-model-viewer PRIVATE -mavx2 -mfma -O3)
endif()

# Deploy binary to gitignored .tools/bin/model-viewer/
add_custom_command(TARGET kongor-model-viewer POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/model-viewer"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:kongor-model-viewer>"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/model-viewer/$<TARGET_FILE_NAME:kongor-model-viewer>"
    COMMENT "Deploying kongor-model-viewer to .tools/bin/model-viewer/"
)
