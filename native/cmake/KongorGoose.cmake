# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
#
# Opt-in CMake integration for standalone Goose CLI spikes (aardappel/goose, Apache-2.0).
# Hard Boundary: Standalone CLI executables ONLY. Shared library (.dll/.so/.dylib) targets
# inside hon_x64.exe or .NET host processes are prohibited until ADR approval.
#
# Architecture:
#   Uses `goose --emit-runtime` + `goose -o <target>.c` so CMake's own C compiler (MSVC x64/ARM64,
#   clang-cl, clang, gcc) compiles both the shared runtime and each tool translation unit,
#   preserving `/PDBALTPATH:%_PDB%`, sanitizer flags, cross-compilation, and native static links.

option(KONGOR_GOOSE_DEBUG "Enable Goose GS_DEBUG overflow/range/tag checks in generated C" OFF)

function(kongor_find_goose OUT_GOOSE_EXE OUT_GOOSE_HOME)
    get_filename_component(_repo_root "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/../.." ABSOLUTE)
    if(WIN32)
        set(_exe_name "goose.exe")
    else()
        set(_exe_name "goose")
    endif()

    file(GLOB_RECURSE _local_candidates "${_repo_root}/.tools/goose/bin/${_exe_name}")
    if(_local_candidates)
        list(GET _local_candidates 0 _found_goose)
    else()
        find_program(_found_goose NAMES goose)
    endif()

    if(NOT _found_goose)
        message(FATAL_ERROR
            "[KONGOR-GOOSE] KONGOR_ENABLE_GOOSE=ON, but 'goose' compiler was not found. "
            "Install locally via PowerShell: .\\scripts\\Install-OptionalAgentTools.ps1 -Tool goose")
    endif()

    set(_goose_home "${_repo_root}/.tools/goose/home")
    file(MAKE_DIRECTORY "${_goose_home}")

    set(${OUT_GOOSE_EXE} "${_found_goose}" PARENT_SCOPE)
    set(${OUT_GOOSE_HOME} "${_goose_home}" PARENT_SCOPE)
endfunction()

function(kongor_ensure_goose_runtime)
    if(TARGET kongor_goose_runtime)
        return()
    endif()

    kongor_find_goose(_GOOSE_EXE _GOOSE_HOME)
    set(_rt_dir "${CMAKE_BINARY_DIR}/goose_generated")
    file(MAKE_DIRECTORY "${_rt_dir}")
    set(_rt_c "${_rt_dir}/goose_runtime.c")

    add_custom_command(
        OUTPUT "${_rt_c}"
        COMMAND "${CMAKE_COMMAND}" -E env "GOOSE_HOME=${_GOOSE_HOME}"
                "${_GOOSE_EXE}" --emit-runtime "${_rt_c}"
        DEPENDS "${_GOOSE_EXE}"
        COMMENT "[KONGOR-GOOSE] Emitting portable C99 Goose runtime (goose_runtime.c)"
        VERBATIM
    )

    add_library(kongor_goose_runtime STATIC "${_rt_c}")
    find_package(Threads REQUIRED)
    target_link_libraries(kongor_goose_runtime PUBLIC Threads::Threads)
    if(UNIX)
        target_link_libraries(kongor_goose_runtime PUBLIC m ${CMAKE_DL_LIBS})
    endif()
    if(MSVC)
        target_compile_options(kongor_goose_runtime PRIVATE /W3 /wd4100 /wd4101 /wd4189 /wd4244 /wd4267)
    else()
        target_compile_options(kongor_goose_runtime PRIVATE -Wno-unused-parameter -Wno-unused-variable -Wno-unused-function)
    endif()
endfunction()

function(add_kongor_goose_cli TARGET_NAME ROOT_GOOSE_FILE)
    cmake_parse_arguments(
        ARG
        ""
        "OUTPUT_DIR"
        "C_SOURCES;INCLUDE_DIRS;INCLUDE_HEADERS;LINK_LIBRARIES;DEPENDS_FILES;GOOSE_FLAGS"
        ${ARGN}
    )

    kongor_find_goose(_GOOSE_EXE _GOOSE_HOME)
    kongor_ensure_goose_runtime()

    get_filename_component(_repo_root "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/../.." ABSOLUTE)
    get_filename_component(_abs_goose "${ROOT_GOOSE_FILE}" ABSOLUTE BASE_DIR "${CMAKE_CURRENT_SOURCE_DIR}")
    # Pass repo-relative source path from _repo_root so embedded gs_file0[] strings never leak workstation paths.
    file(RELATIVE_PATH _rel_goose "${_repo_root}" "${_abs_goose}")

    if(KONGOR_GOOSE_OUTPUT_DIR)
        set(_out_dir "${KONGOR_GOOSE_OUTPUT_DIR}")
    elseif(ARG_OUTPUT_DIR)
        set(_out_dir "${ARG_OUTPUT_DIR}")
    else()
        set(_out_dir "${CMAKE_SOURCE_DIR}/bin")
    endif()
    file(MAKE_DIRECTORY "${_out_dir}")

    set(_gen_dir "${CMAKE_CURRENT_BINARY_DIR}/goose_gen")
    file(MAKE_DIRECTORY "${_gen_dir}")
    set(_gen_c "${_gen_dir}/${TARGET_NAME}.c")

    set(_goose_codegen_flags ${ARG_GOOSE_FLAGS})
    if(KONGOR_GOOSE_DEBUG OR CMAKE_BUILD_TYPE STREQUAL "Debug")
        list(APPEND _goose_codegen_flags "--debug")
    endif()

    foreach(_inc_hdr IN LISTS ARG_INCLUDE_HEADERS)
        list(APPEND _goose_codegen_flags "--include" "${_inc_hdr}")
    endforeach()

    set(_sanitize_cmake "${_gen_dir}/sanitize_goose_c.cmake")
    file(WRITE "${_sanitize_cmake}" [=[
file(READ "${GEN_C}" _c_text)
file(TO_CMAKE_PATH "${REPO_ROOT}" _root_fwd)
string(REPLACE "${_root_fwd}/" "" _c_text "${_c_text}")
string(REPLACE "/" "\\\\" _root_escaped "${_root_fwd}")
string(REPLACE "${_root_escaped}\\\\" "" _c_text "${_c_text}")
file(WRITE "${GEN_C}" "${_c_text}")
]=])

    add_custom_command(
        OUTPUT "${_gen_c}"
        COMMAND "${CMAKE_COMMAND}" -E env "GOOSE_HOME=${_GOOSE_HOME}"
                "${_GOOSE_EXE}" ${_goose_codegen_flags} -o "${_gen_c}" "${_rel_goose}"
        COMMAND "${CMAKE_COMMAND}" "-DGEN_C=${_gen_c}" "-DREPO_ROOT=${_repo_root}" -P "${_sanitize_cmake}"
        DEPENDS "${_abs_goose}" ${ARG_DEPENDS_FILES} "${_GOOSE_EXE}" "${_sanitize_cmake}"
        WORKING_DIRECTORY "${_repo_root}"
        COMMENT "[KONGOR-GOOSE] Transpiling & sanitizing ${_rel_goose} -> ${TARGET_NAME}.c"
        VERBATIM
    )

    add_executable(${TARGET_NAME} "${_gen_c}" ${ARG_C_SOURCES})
    target_link_libraries(${TARGET_NAME} PRIVATE kongor_goose_runtime ${ARG_LINK_LIBRARIES})

    if(ARG_INCLUDE_DIRS)
        target_include_directories(${TARGET_NAME} PRIVATE ${ARG_INCLUDE_DIRS})
    endif()

    if(MSVC)
        target_compile_options(${TARGET_NAME} PRIVATE /W3 /wd4100 /wd4101 /wd4189 /wd4244 /wd4267 /wd4273 /wd4702)
    else()
        target_compile_options(${TARGET_NAME} PRIVATE -Wno-unused-parameter -Wno-unused-variable -Wno-unused-function)
    endif()

    set_target_properties(${TARGET_NAME} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${_out_dir}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${_out_dir}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${_out_dir}"
        RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${_out_dir}"
        RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${_out_dir}"
    )
endfunction()
