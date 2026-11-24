# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(kongor_crypto LANGUAGES CXX ASM_MASM)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

add_library(kongor_crypto_x64 SHARED
    dllmain.cpp
)

target_link_libraries(kongor_crypto_x64 PRIVATE
    bcrypt
    crypt32
    ws2_32
)

if(MSVC)
    set_target_properties(kongor_crypto_x64 PROPERTIES
        LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/kongor_crypto.def\""
        OUTPUT_NAME "kongor_crypto_x64"
    )
endif()
