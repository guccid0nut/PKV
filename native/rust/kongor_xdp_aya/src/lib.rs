// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! Pure-Rust `#![no_std]` + `#![forbid(unsafe_code)]` XDP ingress filter logic for Project Kongor.
//!
//! Designed so the exact same packet inspection state machine compiles inside Linux `aya-ebpf`
//! (`ebpf/src/main.rs`) AND executes natively under host `cargo test` on Windows and Linux without
//! `clang` or `bpf-linker`.

#![no_std]
#![forbid(unsafe_code)]

#[cfg(test)]
extern crate std;

pub mod filter_logic;
