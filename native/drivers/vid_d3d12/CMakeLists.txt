# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(vid_d3d12_x64 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(vid_d3d12_x64 SHARED
    src/vid_d3d12_spy.cpp
    src/d3d12_bridge.cpp
    src/d3d12_bridge.h
    include/SVidDriverAPI.h
    vid_d3d12.def
)

target_include_directories(vid_d3d12_x64 PRIVATE
    include
)

target_compile_definitions(vid_d3d12_x64 PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_link_libraries(vid_d3d12_x64 PRIVATE
    d3d12.lib
    dxgi.lib
    dxguid.lib
)

set_target_properties(vid_d3d12_x64 PROPERTIES
    OUTPUT_NAME "vid_d3d12_x64"
    PREFIX ""
    SUFFIX ".dll"
)
