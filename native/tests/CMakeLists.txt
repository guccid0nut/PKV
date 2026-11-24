# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(KongorNativeTests LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(test_arena_and_string test_arena_and_string.cpp)
target_include_directories(test_arena_and_string PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
if(WIN32)
    target_link_libraries(test_arena_and_string PRIVATE dbghelp)
endif()

add_executable(test_shared_mem test_shared_mem.cpp)
target_include_directories(test_shared_mem PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")

add_executable(test_simd_rig test_simd_rig.cpp)
target_include_directories(test_simd_rig PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
if(MSVC)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
        target_compile_options(test_simd_rig PRIVATE /arch:AVX2 /O2)
    else()
        target_compile_options(test_simd_rig PRIVATE /O2)
    endif()
elseif(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
        target_compile_options(test_simd_rig PRIVATE -mavx2 -mfma -O3)
    else()
        target_compile_options(test_simd_rig PRIVATE -O3)
    endif()
endif()

add_executable(test_veh_unwind_ping_shim_and_proxy_guards test_veh_unwind_ping_shim_and_proxy_guards.cpp)
target_include_directories(test_veh_unwind_ping_shim_and_proxy_guards PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
if(WIN32)
    foreach(dep_target IN ITEMS kongor_native_guard kongor_ssl kongor_crypto kongor_curl kongor_zlib)
        if(TARGET ${dep_target})
            add_dependencies(test_veh_unwind_ping_shim_and_proxy_guards ${dep_target})
        endif()
    endforeach()
endif()

add_executable(test_eval_lua_shared_mem_and_ui_dispatch test_eval_lua_shared_mem_and_ui_dispatch.cpp)
target_include_directories(test_eval_lua_shared_mem_and_ui_dispatch PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
if(WIN32)
    if(TARGET kongor_native_guard)
        add_dependencies(test_eval_lua_shared_mem_and_ui_dispatch kongor_native_guard)
    endif()
endif()

add_executable(test_cas_and_metagen test_cas_and_metagen.cpp)
target_include_directories(test_cas_and_metagen PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
if(WIN32)
    target_link_libraries(test_cas_and_metagen PRIVATE bcrypt)
endif()

option(KONGOR_ENABLE_RUST_PARSERS "Build opt-in safe Rust untrusted-boundary parser staticlibs & C++ FFI tests (w4-rust-boundary)" OFF)
if(KONGOR_ENABLE_RUST_PARSERS)
    if(NOT TARGET kongor_parsers_ffi)
        include("${CMAKE_CURRENT_SOURCE_DIR}/../cmake/KongorRust.cmake")
    endif()
    add_executable(test_rust_parsers_ffi test_rust_parsers_ffi.cpp)
    target_include_directories(test_rust_parsers_ffi PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../include")
    target_link_libraries(test_rust_parsers_ffi PRIVATE kongor_parsers_ffi)
    if(MSVC)
        target_compile_options(test_rust_parsers_ffi PRIVATE /W4)
    else()
        target_compile_options(test_rust_parsers_ffi PRIVATE -Wall -Wextra)
    endif()
endif()

enable_testing()
add_test(NAME test_arena_and_string COMMAND test_arena_and_string)
add_test(NAME test_shared_mem COMMAND test_shared_mem)
add_test(NAME test_simd_rig COMMAND test_simd_rig)
if(NOT CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    add_test(NAME test_veh_unwind_ping_shim_and_proxy_guards COMMAND test_veh_unwind_ping_shim_and_proxy_guards)
    add_test(NAME test_eval_lua_shared_mem_and_ui_dispatch COMMAND test_eval_lua_shared_mem_and_ui_dispatch)
endif()
if(KONGOR_ENABLE_RUST_PARSERS AND TARGET test_rust_parsers_ffi)
    add_test(NAME test_rust_parsers_ffi COMMAND test_rust_parsers_ffi)
endif()
add_test(
    NAME test_cas_and_metagen
    COMMAND test_cas_and_metagen
        "${CMAKE_CURRENT_SOURCE_DIR}/../bin/kongor-cas${CMAKE_EXECUTABLE_SUFFIX}"
        "${CMAKE_CURRENT_SOURCE_DIR}/../bin/kongor-metagen${CMAKE_EXECUTABLE_SUFFIX}"
)
