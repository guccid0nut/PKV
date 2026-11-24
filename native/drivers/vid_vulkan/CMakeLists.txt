# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.20)
project(vid_vulkan_x64 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(KONGOR_ENABLE_TRACY "Enable local-only Tracy profiler in vid_vulkan_x64 (NEVER ship in playtest bundles)" OFF)
option(KONGOR_STAGE_DEV_TRACY_DLL "Allow copying local Tracy-instrumented vid_vulkan_x64.dll into games/client/" OFF)

add_library(vid_vulkan_x64 SHARED
    src/vid_vulkan_spy.cpp
    src/vulkan_context.cpp
    src/vulkan_context.h
    src/vulkan_2d.cpp
    src/vulkan_2d.h
    src/vulkan_3d.cpp
    src/vulkan_3d.h
    src/vulkan_devtools.cpp
    src/vulkan_devtools.h
    include/SVidDriverAPI.h
    vid_vulkan.def
)

target_include_directories(vid_vulkan_x64 PRIVATE
    include
    ${CMAKE_CURRENT_SOURCE_DIR}/../../vulkan-layer/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include
)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/lib/vulkan-1.lib")
    target_link_directories(vid_vulkan_x64 PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/lib
    )
    set(VID_VULKAN_LINK_LIB vulkan-1)
else()
    find_package(Vulkan QUIET)
    if(Vulkan_FOUND)
        set(VID_VULKAN_LINK_LIB Vulkan::Vulkan)
    elseif(MSVC)
        set(GENERATED_VULKAN_IMPORT_LIB "${CMAKE_CURRENT_BINARY_DIR}/vulkan-1.lib")
        add_custom_command(
            OUTPUT "${GENERATED_VULKAN_IMPORT_LIB}"
            COMMAND "${CMAKE_AR}" /NOLOGO "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/vulkan-1.def" /MACHINE:X64 "/OUT:${GENERATED_VULKAN_IMPORT_LIB}"
            DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/vulkan-1.def"
            COMMENT "Generating cleanroom vulkan-1.lib import library from vulkan-1.def"
        )
        add_custom_target(vid_vulkan_import_lib DEPENDS "${GENERATED_VULKAN_IMPORT_LIB}")
        add_dependencies(vid_vulkan_x64 vid_vulkan_import_lib)
        set(VID_VULKAN_LINK_LIB "${GENERATED_VULKAN_IMPORT_LIB}")
    else()
        set(VID_VULKAN_LINK_LIB vulkan-1)
    endif()
endif()

target_link_libraries(vid_vulkan_x64 PRIVATE
    ${VID_VULKAN_LINK_LIB}
    user32
    psapi
    advapi32
    delayimp
)

if(MSVC)
    target_link_options(vid_vulkan_x64 PRIVATE "/DELAYLOAD:vulkan-1.dll")
endif()

target_compile_definitions(vid_vulkan_x64 PRIVATE
    VK_USE_PLATFORM_WIN32_KHR
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

set_target_properties(vid_vulkan_x64 PROPERTIES
    OUTPUT_NAME "vid_vulkan_x64"
    PREFIX ""
    SUFFIX ".dll"
)

if(KONGOR_ENABLE_TRACY)
    message(WARNING "[KONGOR-TRACY] Building LOCAL-ONLY Tracy-instrumented vid_vulkan_x64.dll! NEVER distribute this binary. Clean restore command: cmake -S native -B native/build -DKONGOR_ENABLE_TRACY=OFF && cmake --build native/build --config Release --target vid_vulkan_x64")
    include(FetchContent)
    set(TRACY_ENABLE ON CACHE BOOL "" FORCE)
    set(TRACY_ON_DEMAND ON CACHE BOOL "" FORCE)
    set(TRACY_MANUAL_LIFETIME ON CACHE BOOL "" FORCE)
    set(TRACY_DELAYED_INIT ON CACHE BOOL "" FORCE)
    set(TRACY_ONLY_LOCALHOST ON CACHE BOOL "" FORCE)
    set(TRACY_STATIC ON CACHE BOOL "" FORCE)
    FetchContent_Declare(
        tracy
        GIT_REPOSITORY https://github.com/wolfpld/tracy.git
        GIT_TAG        5d542dc09f3d9378d005092a4ad446bd405f819a # v0.11.1 immutable commit SHA
    )
    FetchContent_MakeAvailable(tracy)
    target_link_libraries(vid_vulkan_x64 PRIVATE Tracy::TracyClient)
    target_compile_definitions(vid_vulkan_x64 PRIVATE KONGOR_ENABLE_TRACY=1)
endif()

option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy vid_vulkan_x64.dll to games/client/*" OFF)

if(KONGOR_DEPLOY_TO_GAMES AND (NOT KONGOR_ENABLE_TRACY OR KONGOR_STAGE_DEV_TRACY_DLL))
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client")
        add_custom_command(TARGET vid_vulkan_x64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:vid_vulkan_x64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/vid_vulkan_x64.dll"
            COMMENT "Deploying vid_vulkan_x64.dll to games/client/"
        )
    endif()
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64")
        add_custom_command(TARGET vid_vulkan_x64 POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:vid_vulkan_x64>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64/vid_vulkan_x64.dll"
            COMMENT "Deploying vid_vulkan_x64.dll to games/client/windows-x64/"
        )
    endif()
elseif(KONGOR_ENABLE_TRACY AND NOT KONGOR_STAGE_DEV_TRACY_DLL)
    message(STATUS "[KONGOR-TRACY] Suppressing automatic POST_BUILD copy to games/client/ (pass -DKONGOR_STAGE_DEV_TRACY_DLL=ON only for local profiling).")
endif()
