# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Opt-in CMake + Cargo integration for Project Kongor safe Rust parser staticlibs
# (Wave 4 Track 3: w4-rust-boundary).
#
# Usage:
#   cmake -S native -B native/build_rust_spike -DKONGOR_ENABLE_RUST_PARSERS=ON
#   cmake --build native/build_rust_spike --config Release --target test_rust_parsers_ffi
#   ctest --test-dir native/build_rust_spike -C Release -R test_rust_parsers_ffi --output-on-failure

if(TARGET kongor_parsers_ffi)
    return()
endif()

get_filename_component(_kongor_native_root "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
set(KONGOR_RUST_WORKSPACE_DIR "${_kongor_native_root}/rust")
set(KONGOR_RUST_MANIFEST_PATH "${KONGOR_RUST_WORKSPACE_DIR}/Cargo.toml")

if(NOT EXISTS "${KONGOR_RUST_MANIFEST_PATH}")
    message(FATAL_ERROR
        "[KONGOR-RUST] Missing Cargo workspace manifest at '${KONGOR_RUST_MANIFEST_PATH}'. "
        "Verify repository checkout before setting KONGOR_ENABLE_RUST_PARSERS=ON.")
endif()

find_program(KONGOR_CARGO_EXE
    NAMES cargo
    HINTS
        "$ENV{CARGO_HOME}/bin"
        "$ENV{USERPROFILE}/.cargo/bin"
        "$ENV{HOME}/.cargo/bin"
)
find_program(KONGOR_RUSTC_EXE
    NAMES rustc
    HINTS
        "$ENV{CARGO_HOME}/bin"
        "$ENV{USERPROFILE}/.cargo/bin"
        "$ENV{HOME}/.cargo/bin"
)

if(NOT KONGOR_CARGO_EXE OR NOT KONGOR_RUSTC_EXE)
    message(FATAL_ERROR
        "[KONGOR-RUST] KONGOR_ENABLE_RUST_PARSERS=ON, but 'cargo'/'rustc' were not found on PATH. "
        "Install Rust (>= 1.78.0, x86_64-pc-windows-msvc on Windows) or pass -DKONGOR_ENABLE_RUST_PARSERS=OFF.")
endif()

execute_process(
    COMMAND "${KONGOR_RUSTC_EXE}" --version
    OUTPUT_VARIABLE _kongor_rustc_version
    OUTPUT_STRIP_TRAILING_WHITESPACE
    ERROR_QUIET
)
message(STATUS "[KONGOR-RUST] Found Cargo: ${KONGOR_CARGO_EXE} (${_kongor_rustc_version})")

set(KONGOR_RUST_TARGET_DIR "${CMAKE_BINARY_DIR}/cargo_target" CACHE PATH
    "Isolated Cargo target directory for CMake Rust staticlib builds")

if(WIN32)
    set(_kongor_rust_lib_name "kongor_parsers_ffi.lib")
else()
    set(_kongor_rust_lib_name "libkongor_parsers_ffi.a")
endif()

set(_kongor_rust_profile_arg "$<IF:$<CONFIG:Debug>,dev,release>")
set(_kongor_rust_profile_dir "$<IF:$<CONFIG:Debug>,debug,release>")
set(_kongor_rust_cargo_lib "${KONGOR_RUST_TARGET_DIR}/${_kongor_rust_profile_dir}/${_kongor_rust_lib_name}")
set(_kongor_rust_staged_dir "${CMAKE_BINARY_DIR}/rust_ffi/$<CONFIG>")
set(_kongor_rust_staged_lib "${_kongor_rust_staged_dir}/${_kongor_rust_lib_name}")

# Always invoke Cargo offline (zero crates.io network dependencies); Cargo's own incremental
# fingerprinting handles fast sub-second no-op and touch-one-file rebuilds.
add_custom_target(kongor_parsers_ffi_cargo
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${_kongor_rust_staged_dir}"
    COMMAND "${KONGOR_CARGO_EXE}" build
        --offline
        --manifest-path "${KONGOR_RUST_MANIFEST_PATH}"
        --target-dir "${KONGOR_RUST_TARGET_DIR}"
        --package kongor_parsers_ffi
        --profile "${_kongor_rust_profile_arg}"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
        "${_kongor_rust_cargo_lib}"
        "${_kongor_rust_staged_lib}"
    WORKING_DIRECTORY "${KONGOR_RUST_WORKSPACE_DIR}"
    COMMENT "[KONGOR-RUST] Cargo build kongor_parsers_ffi (${_kongor_rust_profile_arg}, offline)"
    VERBATIM
)

add_library(kongor_parsers_ffi STATIC IMPORTED GLOBAL)
add_dependencies(kongor_parsers_ffi kongor_parsers_ffi_cargo)

set_target_properties(kongor_parsers_ffi PROPERTIES
    IMPORTED_LOCATION "${CMAKE_BINARY_DIR}/rust_ffi/$<CONFIG>/${_kongor_rust_lib_name}"
    IMPORTED_LOCATION_DEBUG "${CMAKE_BINARY_DIR}/rust_ffi/Debug/${_kongor_rust_lib_name}"
    IMPORTED_LOCATION_RELEASE "${CMAKE_BINARY_DIR}/rust_ffi/Release/${_kongor_rust_lib_name}"
    IMPORTED_LOCATION_RELWITHDEBINFO "${CMAKE_BINARY_DIR}/rust_ffi/RelWithDebInfo/${_kongor_rust_lib_name}"
    IMPORTED_LOCATION_MINSIZEREL "${CMAKE_BINARY_DIR}/rust_ffi/MinSizeRel/${_kongor_rust_lib_name}"
    INTERFACE_INCLUDE_DIRECTORIES "${_kongor_native_root}/include"
)

if(WIN32)
    # Exact native-static-libs reported by `rustc 1.78.0 --print=native-static-libs` on x86_64-pc-windows-msvc.
    target_link_libraries(kongor_parsers_ffi INTERFACE
        ws2_32
        userenv
        bcrypt
        ntdll
        advapi32
        kernel32
    )
else()
    target_link_libraries(kongor_parsers_ffi INTERFACE
        pthread
        dl
        m
    )
endif()
