# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(kongor-metagen LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(kongor-metagen
    main.cpp
)

target_include_directories(kongor-metagen PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../../include"
)

set_target_properties(kongor-metagen PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
)

option(KONGOR_ENABLE_GOOSE "Build experimental Goose A/B CLI spikes (standalone processes only)" OFF)
if(KONGOR_ENABLE_GOOSE)
    if(NOT COMMAND add_kongor_goose_cli)
        include("${CMAKE_CURRENT_SOURCE_DIR}/../../cmake/KongorGoose.cmake")
    endif()
    add_kongor_goose_cli(kongor-metagen-goose
        "${CMAKE_CURRENT_SOURCE_DIR}/goose/main.goose"
        OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
        DEPENDS_FILES
            "${CMAKE_CURRENT_SOURCE_DIR}/../../goose/pkg/kongor_log/kongor_log.goose"
    )
endif()
