# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
cmake_minimum_required(VERSION 3.15)
project(hon_ping_shim LANGUAGES C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

if(UNIX)
    add_library(hon-ping-shim SHARED
        hon_ping_shim.c
    )

    set_target_properties(hon-ping-shim PROPERTIES
        OUTPUT_NAME "hon-ping-shim"
        PREFIX "lib"
        SUFFIX ".so"
    )

    target_compile_definitions(hon-ping-shim PRIVATE
        _GNU_SOURCE
    )

    target_link_libraries(hon-ping-shim PRIVATE
        ${CMAKE_DL_LIBS}
    )

    message(STATUS "Configured libhon-ping-shim.so target for Linux")
else()
    message(STATUS "Skipping libhon-ping-shim (Linux LD_PRELOAD shared object only)")
endif()
