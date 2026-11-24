# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

cmake_minimum_required(VERSION 3.15)
project(kongor_video LANGUAGES CXX ASM_MASM)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

add_library(avcodec-x64-58 SHARED
    avcodec_proxy.cpp
    get_rbx.asm
)
target_link_libraries(avcodec-x64-58 PRIVATE kernel32 user32)
set_target_properties(avcodec-x64-58 PROPERTIES OUTPUT_NAME "avcodec-x64-58")

add_library(avfilter-x64-7 SHARED
    avfilter_stub.cpp
)
target_link_libraries(avfilter-x64-7 PRIVATE kernel32 user32)
set_target_properties(avfilter-x64-7 PROPERTIES OUTPUT_NAME "avfilter-x64-7")

add_library(avdevice-x64-58 SHARED
    avdevice_stub.cpp
)
target_link_libraries(avdevice-x64-58 PRIVATE kernel32 user32)
set_target_properties(avdevice-x64-58 PROPERTIES OUTPUT_NAME "avdevice-x64-58")

option(KONGOR_DEPLOY_TO_GAMES "Auto-stage video proxy DLLs to artifacts/native-binaries/*" OFF)

if(KONGOR_DEPLOY_TO_GAMES)
    set(_kongor_video_stage_dir "${CMAKE_CURRENT_SOURCE_DIR}/../../../artifacts/native-binaries/windows-x64/video")

    foreach(_vid_target IN ITEMS avcodec-x64-58 avfilter-x64-7 avdevice-x64-58)
        add_custom_command(TARGET ${_vid_target} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E make_directory "${_kongor_video_stage_dir}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:${_vid_target}>"
                "${_kongor_video_stage_dir}/$<TARGET_FILE_NAME:${_vid_target}>"
            COMMENT "Staging ${_vid_target} to artifacts/native-binaries/windows-x64/video/"
        )
    endforeach()
endif()
