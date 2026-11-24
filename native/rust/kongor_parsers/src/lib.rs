// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! Safe, zero-dependency untrusted byte-boundary parsers for Project Kongor.
//!
//! Covers all 4 native untrusted ingress surfaces:
//! - [`pipe`]: Named-pipe `'MW'` 12-byte binary framing and safe JSON-RPC 2.0 string/token extraction.
//! - [`s2z`]: VFS nasty-path firewall and ZIP/S2Z EOCD + Central Directory ZIP64 (`0x0001`) validation.
//! - [`dds`]: `"DDS "` / `"DX10"` header & overflow-checked mipmap chain + cubemap size verification.
//! - [`udp`]: 6-byte K2 ping probe, loopback `"map:<name>"` guard, and COMPEL 40-byte watermark envelope.

#![forbid(unsafe_code)]
#![deny(clippy::all)]

pub mod dds;
pub mod pipe;
pub mod s2z;
pub mod udp;
