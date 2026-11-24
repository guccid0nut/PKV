# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
cmake_minimum_required(VERSION 3.20)
project(FmodExSpatialProxy LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(fmodex64 SHARED
    src/FmodExProxy.cpp
    src/FmodExProxy.h
    src/OpenAlSpatialEngine.cpp
    src/OpenAlSpatialEngine.h
)

target_include_directories(fmodex64 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(fmodex64 PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

target_link_libraries(fmodex64 PRIVATE
    OpenAL32
)
