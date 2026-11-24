# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

cmake_minimum_required(VERSION 3.15)
project(kongor_audio_stub LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

add_library(fmodex64_null SHARED
    fmod_headless_stub.cpp
)

target_link_libraries(fmodex64_null PRIVATE
    kernel32
)

if(MSVC)
    set_target_properties(fmodex64_null PROPERTIES
        LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/fmodex64.def\""
        OUTPUT_NAME "fmodex64_null"
    )
endif()

add_custom_command(TARGET fmodex64_null POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:fmodex64_null>"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64/fmodex64_null.dll"
    COMMENT "Staging headless audio null stub fmodex64_null.dll to artifacts/native-binaries/windows-x64/"
)
