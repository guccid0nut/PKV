# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(kongor_physics_sandbox LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(kongor-physics-sandbox
    src/main.cpp
)

target_include_directories(kongor-physics-sandbox PRIVATE
    ${raylib_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(kongor-physics-sandbox PRIVATE
    raylib
)

if(MSVC)
    target_compile_definitions(kongor-physics-sandbox PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(kongor-physics-sandbox PRIVATE /W3 /O2 /MD)
endif()

# Deploy binary to gitignored .tools/bin/physics-sandbox/
add_custom_command(TARGET kongor-physics-sandbox POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/physics-sandbox"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:kongor-physics-sandbox>"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/physics-sandbox/$<TARGET_FILE_NAME:kongor-physics-sandbox>"
    COMMENT "Deploying kongor-physics-sandbox to .tools/bin/physics-sandbox/"
)
