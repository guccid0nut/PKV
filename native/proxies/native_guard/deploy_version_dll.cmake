# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
# Safe hot-deploy script for version.dll on Windows NTFS with 5-tier .bak rotation.
# Delegates to scripts/re/deploy_native_guard.ps1 so every CMake build preserves
# .bak -> .bak_old -> .bak_old2 -> .bak_old3 -> .bak_old4 rollback snapshots.

if(NOT DEFINED SRC OR NOT EXISTS "${SRC}")
    message(FATAL_ERROR "SRC version.dll not found: ${SRC}")
endif()

get_filename_component(SCRIPT_DIR "${CMAKE_CURRENT_LIST_FILE}" DIRECTORY)
get_filename_component(REPO_ROOT "${SCRIPT_DIR}/../../.." ABSOLUTE)
set(DEPLOY_PS1 "${REPO_ROOT}/scripts/re/deploy_native_guard.ps1")

if(EXISTS "${DEPLOY_PS1}")
    execute_process(
        COMMAND powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${DEPLOY_PS1}"
            -SourcePath "${SRC}"
            -TargetPaths "${DST1};${DST2};${DST3};${DST4}"
        RESULT_VARIABLE DEPLOY_RES
    )
    if(DEPLOY_RES EQUAL 0)
        return()
    endif()
    message(WARNING "[NATIVE_GUARD] deploy_native_guard.ps1 returned ${DEPLOY_RES}; falling back to direct NTFS hot-copy.")
endif()

foreach(DST IN ITEMS "${DST1}" "${DST2}" "${DST3}" "${DST4}")
    if(NOT "${DST}" STREQUAL "")
        get_filename_component(DST_DIR "${DST}" DIRECTORY)
        if(EXISTS "${DST_DIR}")
            execute_process(
                COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SRC}" "${DST}"
                RESULT_VARIABLE COPY_RES
                ERROR_QUIET
            )
            if(NOT COPY_RES EQUAL 0)
                file(REMOVE "${DST}.old")
                execute_process(
                    COMMAND powershell -NoProfile -NonInteractive -Command
                        "if (Test-Path '${DST}') { Move-Item -Path '${DST}' -Destination '${DST}.old' -Force -ErrorAction SilentlyContinue }; Copy-Item -Path '${SRC}' -Destination '${DST}' -Force"
                    RESULT_VARIABLE HOT_RES
                )
            endif()
        endif()
    endif()
endforeach()

