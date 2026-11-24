# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(luac51 C)

set(CMAKE_C_STANDARD 99)

set(LUA_CORE_SOURCES
    lua-5.1.5/src/lapi.c
    lua-5.1.5/src/lcode.c
    lua-5.1.5/src/ldebug.c
    lua-5.1.5/src/ldo.c
    lua-5.1.5/src/ldump.c
    lua-5.1.5/src/lfunc.c
    lua-5.1.5/src/lgc.c
    lua-5.1.5/src/llex.c
    lua-5.1.5/src/lmem.c
    lua-5.1.5/src/lobject.c
    lua-5.1.5/src/lopcodes.c
    lua-5.1.5/src/lparser.c
    lua-5.1.5/src/lstate.c
    lua-5.1.5/src/lstring.c
    lua-5.1.5/src/ltable.c
    lua-5.1.5/src/ltm.c
    lua-5.1.5/src/lundump.c
    lua-5.1.5/src/lvm.c
    lua-5.1.5/src/lzio.c
    lua-5.1.5/src/lauxlib.c
)

add_executable(luac51
    ${LUA_CORE_SOURCES}
    lua-5.1.5/src/print.c
    lua-5.1.5/src/luac.c
)

target_include_directories(luac51 PRIVATE lua-5.1.5/src)

if(MSVC)
    target_compile_definitions(luac51 PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

# Deploy binary to gitignored .tools/bin/lua/ and artifacts/native-binaries/
if(WIN32)
    add_custom_command(TARGET luac51 POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/lua"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:luac51>"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/lua/luac51.exe"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:luac51>"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64/luac51.exe"
        COMMENT "Deploying luac51.exe to .tools/bin/lua/ and artifacts/native-binaries/windows-x64/"
    )
else()
    add_custom_command(TARGET luac51 POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/lua"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:luac51>"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../.tools/bin/lua/$<TARGET_FILE_NAME:luac51>"
        COMMENT "Deploying luac51 to .tools/bin/lua/"
    )
endif()
