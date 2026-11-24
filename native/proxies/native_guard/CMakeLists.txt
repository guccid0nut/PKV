# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)

if(WIN32)
    project(version LANGUAGES C CXX ASM_MASM)

    option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy compiled native binaries to games/*" OFF)
    option(KONGOR_DEPLOY_NATIVE_GUARD "Auto-deploy version.dll to games/*" ${KONGOR_DEPLOY_TO_GAMES})
    option(KONGOR_PLAYTEST_BUILD "Compile out dev Lua/command/memory pipe mutation opcodes for retail/playtest anti-cheat hardening" OFF)
    option(KONGOR_TRACY_ENABLE "Enable dev-only Tracy profiler zones (DO NOT SHIP)" OFF)

    set(CMAKE_CXX_STANDARD 17)
    set(CMAKE_CXX_STANDARD_REQUIRED True)

    # libdeflate static library configuration
    set(LIBDEFLATE_BUILD_STATIC_LIB ON CACHE BOOL "" FORCE)
    set(LIBDEFLATE_BUILD_SHARED_LIB OFF CACHE BOOL "" FORCE)
    set(LIBDEFLATE_BUILD_GZIP OFF CACHE BOOL "" FORCE)
    set(LIBDEFLATE_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(LIBDEFLATE_INSTALL OFF CACHE BOOL "" FORCE)

    if(NOT TARGET libdeflate_static)
        add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../../deps/libdeflate" "${CMAKE_CURRENT_BINARY_DIR}/libdeflate_build")
    endif()

    set(KONGOR_VERSION_C_CXX_SOURCES
        dllmain.cpp
        shm_telemetry.cpp
        lua_eval_queue.cpp
        vfs_and_hooks.cpp
        pipe_server.cpp
        cursor_confinement.cpp
        simd_ray_bridge.cpp
        veh_crash_shield.cpp
        d3d9_texture_healer.cpp
        runtime_patch_controller.cpp
        asset_prewarmer.cpp
        vfs_loose_cache.cpp
        job_pool.cpp
        predictive_pathing.cpp
        modern_archive_hook.cpp
        kongor_lua_shared_mem.cpp
        queue_arcade_hooks.cpp
        "${CMAKE_CURRENT_SOURCE_DIR}/../../deps/miniz/miniz.c"
    )

    add_library(version SHARED
        ${KONGOR_VERSION_C_CXX_SOURCES}
        proxy.asm
    )

    target_include_directories(version PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/../../deps/miniz"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../deps/libdeflate"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../include"
    )

    if(KONGOR_PLAYTEST_BUILD)
        target_compile_definitions(version PRIVATE KONGOR_PLAYTEST_BUILD=1)
    else()
        target_compile_definitions(version PRIVATE KONGOR_DEV_BUILD=1)
    endif()

    if(KONGOR_TRACY_ENABLE)
        target_compile_definitions(version PRIVATE KONGOR_TRACY_ENABLE=1)
    endif()

    target_link_libraries(version PRIVATE
        libdeflate_static
        advapi32
        dbghelp
        psapi
        winmm
        user32
        ws2_32
    )

    if(MSVC)
        get_filename_component(KONGOR_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../.." ABSOLUTE)
        file(TO_NATIVE_PATH "${KONGOR_REPO_ROOT}" KONGOR_REPO_ROOT_NATIVE)

        set(KONGOR_REPRO_COMPILE_OPTIONS
            "/Brepro"
            "/d1trimfile:${KONGOR_REPO_ROOT_NATIVE}\\"
        )
        set_source_files_properties(${KONGOR_VERSION_C_CXX_SOURCES} PROPERTIES
            COMPILE_OPTIONS "${KONGOR_REPRO_COMPILE_OPTIONS}"
        )
        if(TARGET libdeflate_static)
            target_compile_options(libdeflate_static PRIVATE ${KONGOR_REPRO_COMPILE_OPTIONS})
        endif()

        set_target_properties(version PROPERTIES
            LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/version.def\" /BREPRO /PDBALTPATH:%_PDB%"
        )
    endif()

    if(KONGOR_DEPLOY_NATIVE_GUARD AND NOT KONGOR_TRACY_ENABLE)
        add_custom_command(TARGET version POST_BUILD
            COMMAND ${CMAKE_COMMAND}
                "-DSRC=$<TARGET_FILE:version>"
                "-DDST1=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/version.dll"
                "-DDST2=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/windows-x64/version.dll"
                "-DDST3=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/version.dll"
                "-DDST4=${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/windows-x64/version.dll"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/deploy_version_dll.cmake"
            COMMENT "Deploying version.dll to games/client/ and games/server/ (NTFS hot-deploy safe)"
        )
    endif()

    add_executable(native_guard_unit_tests
        tests/native_guard_unit_tests.cpp
        asset_prewarmer.cpp
        job_pool.cpp
        cursor_confinement.cpp
        d3d9_texture_healer.cpp
        veh_crash_shield.cpp
        runtime_patch_controller.cpp
        vfs_loose_cache.cpp
        kongor_lua_shared_mem.cpp
        queue_arcade_hooks.cpp
    )

    target_include_directories(native_guard_unit_tests PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../include"
    )

    target_link_libraries(native_guard_unit_tests PRIVATE
        advapi32
        dbghelp
        psapi
        winmm
        user32
        ws2_32
    )

    if(KONGOR_TRACY_ENABLE)
        target_compile_definitions(native_guard_unit_tests PRIVATE KONGOR_TRACY_ENABLE=1)
    endif()

    add_test(NAME native_guard_unit_tests COMMAND native_guard_unit_tests)
elseif(APPLE)
    project(kongor_native_guard LANGUAGES C CXX OBJCXX)

    option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy compiled native binaries to games/*" OFF)
    option(KONGOR_DEPLOY_NATIVE_GUARD "Auto-deploy libkongor_native_guard.dylib to games/client/macos-x64/compatibility" ${KONGOR_DEPLOY_TO_GAMES})

    set(CMAKE_CXX_STANDARD 17)
    set(CMAKE_CXX_STANDARD_REQUIRED True)
    set(CMAKE_OBJCXX_STANDARD 17)

    add_library(kongor_native_guard SHARED
        macos_native_guard.mm
    )

    target_compile_options(kongor_native_guard PRIVATE
        -O2
        -Wall
        -Wextra
        -Wno-deprecated-declarations
    )

    set_target_properties(kongor_native_guard PROPERTIES
        OUTPUT_NAME "kongor_native_guard"
        PREFIX "lib"
        SUFFIX ".dylib"
    )

    target_link_libraries(kongor_native_guard PRIVATE
        "-framework Cocoa"
        "-framework CoreGraphics"
        "-framework CoreFoundation"
        "-framework OpenGL"
    )

    if(KONGOR_DEPLOY_NATIVE_GUARD AND EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/macos-x64")
        add_custom_command(TARGET kongor_native_guard POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/macos-x64/compatibility"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/macos-x64/compatibility/libkongor_native_guard.dylib"
            COMMENT "Deploying libkongor_native_guard.dylib to games/client/macos-x64/compatibility/"
        )
    endif()
else()
    project(kongor_native_guard LANGUAGES C CXX)

    option(KONGOR_DEPLOY_TO_GAMES "Auto-deploy compiled native binaries to games/*" OFF)
    option(KONGOR_DEPLOY_NATIVE_GUARD "Auto-deploy libkongor_native_guard.so to games/server/*" ${KONGOR_DEPLOY_TO_GAMES})
    option(KONGOR_TRACY_ENABLE "Enable dev-only Tracy profiler zones (DO NOT SHIP)" OFF)

    set(CMAKE_CXX_STANDARD 17)
    set(CMAKE_CXX_STANDARD_REQUIRED True)

    add_library(kongor_native_guard SHARED
        linux_native_guard.cpp
    )

    target_include_directories(kongor_native_guard PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/../../include"
    )

    target_compile_options(kongor_native_guard PRIVATE
        -O2
        -fno-exceptions
        -fno-rtti
        -Wall
        -Wextra
    )

    # Link with C driver (gcc/clang) rather than C++ driver so libstdc++.so.6
    # is never pulled into DT_NEEDED alongside HoN's LLVM libc++.so.1.
    set_target_properties(kongor_native_guard PROPERTIES
        LINKER_LANGUAGE C
        OUTPUT_NAME "kongor_native_guard"
        PREFIX "lib"
        SUFFIX ".so"
    )

    if(KONGOR_TRACY_ENABLE)
        target_compile_definitions(kongor_native_guard PRIVATE KONGOR_TRACY_ENABLE=1)
    endif()

    target_link_libraries(kongor_native_guard PRIVATE
        pthread
        rt
        dl
        m
        c
    )

    if(KONGOR_DEPLOY_NATIVE_GUARD AND NOT KONGOR_TRACY_ENABLE AND EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server")
        add_custom_command(TARGET kongor_native_guard POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/linux-x64"
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/compatibility"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/linux-x64/libkongor_native_guard.so"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/linux-x64/libk2_native_guard.so"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/compatibility/libkongor_native_guard.so"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/server/compatibility/libk2_native_guard.so"
            COMMENT "Deploying libk2_native_guard.so and libkongor_native_guard.so to games/server/"
        )
    endif()

    if(KONGOR_DEPLOY_NATIVE_GUARD AND NOT KONGOR_TRACY_ENABLE AND EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/linux-x64")
        add_custom_command(TARGET kongor_native_guard POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/linux-x64/compatibility"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/linux-x64/compatibility/libkongor_native_guard.so"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:kongor_native_guard>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../../../games/client/linux-x64/compatibility/libk2_native_guard.so"
            COMMENT "Deploying libk2_native_guard.so and libkongor_native_guard.so to games/client/linux-x64/compatibility/"
        )
    endif()

    enable_testing()

    add_executable(linux_queue_arcade_tests
        tests/linux_queue_arcade_tests.cpp
    )

    target_include_directories(linux_queue_arcade_tests PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../include"
    )

    target_compile_options(linux_queue_arcade_tests PRIVATE
        -O2
        -fno-exceptions
        -fno-rtti
        -Wall
        -Wextra
    )

    target_link_libraries(linux_queue_arcade_tests PRIVATE
        kongor_native_guard
        pthread
        rt
        dl
        m
        c
    )

    if(KONGOR_TRACY_ENABLE)
        target_compile_definitions(linux_queue_arcade_tests PRIVATE KONGOR_TRACY_ENABLE=1)
    endif()

    add_test(NAME linux_queue_arcade_tests COMMAND linux_queue_arcade_tests)
endif()
