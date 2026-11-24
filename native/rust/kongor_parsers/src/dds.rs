// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

//! `"DDS "` / `"DX10"` header parser and integer-overflow-checked mipmap chain + cubemap validator.
//!
//! Parity reference: `VerifyDdsMipmapChainSize` in `native/proxies/native_guard/modern_archive_hook.cpp:242-314`.

use std::fmt;

pub const DDS_MAGIC: u32 = 0x2053_4444; // "DDS "
pub const DDS_HEADER_SIZE_FIELD: u32 = 124;
pub const DDS_BASE_HEADER_BYTES: usize = 128;
pub const DDS_DX10_HEADER_BYTES: usize = 148;
pub const DDS_MAX_DIMENSION: u32 = 16_384;
pub const DDS_MAX_MIPMAPS: u32 = 16;

pub const DDPF_FOURCC: u32 = 0x0000_0004;
pub const DDSCAPS2_CUBEMAP: u32 = 0x0000_0200;
pub const DDS_RESOURCE_MISC_TEXTURECUBE: u32 = 0x0000_0004;

pub const FOURCC_DX10: u32 = 0x3031_5844; // "DX10"
pub const FOURCC_DXT1: u32 = 0x3154_5844; // "DXT1"
pub const FOURCC_DXT2: u32 = 0x3254_5844; // "DXT2"
pub const FOURCC_DXT3: u32 = 0x3354_5844; // "DXT3"
pub const FOURCC_DXT4: u32 = 0x3454_5844; // "DXT4"
pub const FOURCC_DXT5: u32 = 0x3554_5844; // "DXT5"
pub const FOURCC_ATI1: u32 = 0x3149_5441; // "ATI1"
pub const FOURCC_ATI2: u32 = 0x3249_5441; // "ATI2"
pub const FOURCC_BC4U: u32 = 0x5534_4342; // "BC4U"
pub const FOURCC_BC4S: u32 = 0x5334_4342; // "BC4S"
pub const FOURCC_BC5U: u32 = 0x5535_4342; // "BC5U"
pub const FOURCC_BC5S: u32 = 0x5335_4342; // "BC5S"

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DdsInfo {
    pub width: u32,
    pub height: u32,
    pub mip_count: u32,
    pub header_bytes: u32,
    pub dxgi_format: u32,
    pub fourcc: u32,
    pub is_dx10: bool,
    pub is_cubemap: bool,
    pub is_block_compressed: bool,
    pub block_or_pixel_bytes: u8,
    pub expected_min_size: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DdsError {
    TruncatedHeader { actual: usize, required: usize },
    InvalidMagic(u32),
    InvalidHeaderSize(u32),
    InvalidDimensions { width: u32, height: u32 },
    SizeOverflow,
    TruncatedPayload {
        actual_size: u64,
        expected_min_size: u64,
        info: DdsInfo,
    },
}

impl fmt::Display for DdsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::TruncatedHeader { actual, required } => {
                write!(f, "DDS header truncated: have {actual} bytes, require {required}")
            }
            Self::InvalidMagic(m) => {
                write!(f, "invalid DDS magic 0x{m:08X} (expected 0x20534444 'DDS ')")
            }
            Self::InvalidHeaderSize(s) => {
                write!(f, "invalid DDS dwSize {s} (expected 124)")
            }
            Self::InvalidDimensions { width, height } => {
                write!(
                    f,
                    "invalid DDS dimensions {width}x{height} (must be 1..={DDS_MAX_DIMENSION})"
                )
            }
            Self::SizeOverflow => write!(f, "integer overflow calculating DDS mipmap chain size"),
            Self::TruncatedPayload {
                actual_size,
                expected_min_size,
                ..
            } => {
                write!(
                    f,
                    "truncated DDS mipmap chain: have {actual_size} bytes, need at least {expected_min_size}"
                )
            }
        }
    }
}

impl std::error::Error for DdsError {}

fn read_u32_le(buf: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes([
        buf[offset],
        buf[offset + 1],
        buf[offset + 2],
        buf[offset + 3],
    ])
}

/// Validates a complete DDS buffer (header + mipmap chain + cubemap faces).
pub fn verify_dds_mipmap_chain(data: &[u8]) -> Result<DdsInfo, DdsError> {
    verify_dds_mipmap_chain_with_total_size(data, data.len() as u64)
}

/// Validates DDS header bytes against a reported `actual_total_size` (enables zero-allocation
/// validation of large textures and overflow checks without materializing multi-GB payloads).
pub fn verify_dds_mipmap_chain_with_total_size(
    data: &[u8],
    actual_total_size: u64,
) -> Result<DdsInfo, DdsError> {
    if data.len() < DDS_BASE_HEADER_BYTES || actual_total_size < DDS_BASE_HEADER_BYTES as u64 {
        return Err(DdsError::TruncatedHeader {
            actual: data.len(),
            required: DDS_BASE_HEADER_BYTES,
        });
    }

    let magic = read_u32_le(data, 0);
    if magic != DDS_MAGIC {
        return Err(DdsError::InvalidMagic(magic));
    }

    let dw_size = read_u32_le(data, 4);
    if dw_size != DDS_HEADER_SIZE_FIELD {
        return Err(DdsError::InvalidHeaderSize(dw_size));
    }

    let height = read_u32_le(data, 12);
    let width = read_u32_le(data, 16);
    let mip_map_count = read_u32_le(data, 28);
    let pf_flags = read_u32_le(data, 80);
    let fourcc = read_u32_le(data, 84);
    let rgb_bit_count = read_u32_le(data, 88);
    let caps2 = read_u32_le(data, 112);

    if width == 0 || height == 0 || width > DDS_MAX_DIMENSION || height > DDS_MAX_DIMENSION {
        return Err(DdsError::InvalidDimensions { width, height });
    }

    // Matches VerifyDdsMipmapChainSize: 0 or > 16 collapses to 1 mip level.
    let mips = if mip_map_count == 0 || mip_map_count > DDS_MAX_MIPMAPS {
        1u32
    } else {
        mip_map_count
    };

    let mut header_bytes: u32 = DDS_BASE_HEADER_BYTES as u32;
    let mut is_dx10 = false;
    let mut dxgi_format: u32 = 0;
    let mut is_block_compressed = false;
    let mut block_or_pixel_bytes: u8 = 4;
    let mut is_cubemap = (caps2 & DDSCAPS2_CUBEMAP) != 0;
    let mut array_slices: u64 = 1;

    if (pf_flags & DDPF_FOURCC) != 0 {
        if fourcc == FOURCC_DX10 {
            header_bytes = DDS_DX10_HEADER_BYTES as u32;
            is_dx10 = true;
            if data.len() < DDS_DX10_HEADER_BYTES || actual_total_size < DDS_DX10_HEADER_BYTES as u64
            {
                return Err(DdsError::TruncatedHeader {
                    actual: data.len(),
                    required: DDS_DX10_HEADER_BYTES,
                });
            }
            dxgi_format = read_u32_le(data, 128);
            let misc_flag = read_u32_le(data, 136);
            let array_size = read_u32_le(data, 140);
            if (misc_flag & DDS_RESOURCE_MISC_TEXTURECUBE) != 0 {
                is_cubemap = true;
            }
            if array_size > 1 {
                array_slices = array_size as u64;
            }

            // DXGI BC1..BC7 block sizes:
            // - BC1 (70..=72) & BC4 (79..=81): 8 bytes per 4x4 block
            // - BC2 (73..=75), BC3 (76..=78), BC5 (82..=84), BC6H (94..=96), BC7 (97..=99): 16B/block
            match dxgi_format {
                70..=72 | 79..=81 => {
                    is_block_compressed = true;
                    block_or_pixel_bytes = 8;
                }
                73..=78 | 82..=84 | 94..=99 => {
                    is_block_compressed = true;
                    block_or_pixel_bytes = 16;
                }
                85..=86 => {
                    is_block_compressed = false;
                    block_or_pixel_bytes = 2;
                }
                _ => {
                    is_block_compressed = false;
                    block_or_pixel_bytes = 4;
                }
            }
        } else if matches!(fourcc, FOURCC_DXT1 | FOURCC_ATI1 | FOURCC_BC4U | FOURCC_BC4S) {
            is_block_compressed = true;
            block_or_pixel_bytes = 8;
        } else if matches!(
            fourcc,
            FOURCC_DXT2
                | FOURCC_DXT3
                | FOURCC_DXT4
                | FOURCC_DXT5
                | FOURCC_ATI2
                | FOURCC_BC5U
                | FOURCC_BC5S
        ) {
            is_block_compressed = true;
            block_or_pixel_bytes = 16;
        }
    } else if (8..=128).contains(&rgb_bit_count) {
        block_or_pixel_bytes = ((rgb_bit_count + 7) / 8) as u8;
    }

    let mut single_face_bytes: u64 = 0;
    let mut w = width;
    let mut h = height;
    for _ in 0..mips {
        let mw = w.max(1) as u64;
        let mh = h.max(1) as u64;
        let level_bytes = if is_block_compressed {
            let bx = (mw + 3) / 4;
            let by = (mh + 3) / 4;
            bx.checked_mul(by)
                .and_then(|blocks| blocks.checked_mul(block_or_pixel_bytes as u64))
                .ok_or(DdsError::SizeOverflow)?
        } else {
            mw.checked_mul(mh)
                .and_then(|pixels| pixels.checked_mul(block_or_pixel_bytes as u64))
                .ok_or(DdsError::SizeOverflow)?
        };
        single_face_bytes = single_face_bytes
            .checked_add(level_bytes)
            .ok_or(DdsError::SizeOverflow)?;
        w >>= 1;
        h >>= 1;
    }

    let face_count: u64 = if is_cubemap { 6 } else { 1 };
    let total_pixel_bytes = single_face_bytes
        .checked_mul(face_count)
        .and_then(|v| v.checked_mul(array_slices))
        .ok_or(DdsError::SizeOverflow)?;
    let expected_min_size = (header_bytes as u64)
        .checked_add(total_pixel_bytes)
        .ok_or(DdsError::SizeOverflow)?;

    let info = DdsInfo {
        width,
        height,
        mip_count: mips,
        header_bytes,
        dxgi_format,
        fourcc,
        is_dx10,
        is_cubemap,
        is_block_compressed,
        block_or_pixel_bytes,
        expected_min_size,
    };

    if actual_total_size < expected_min_size {
        return Err(DdsError::TruncatedPayload {
            actual_size: actual_total_size,
            expected_min_size,
            info,
        });
    }

    Ok(info)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_dds_header(
        width: u32,
        height: u32,
        mips: u32,
        pf_flags: u32,
        fourcc: u32,
        rgb_bits: u32,
        caps2: u32,
    ) -> Vec<u8> {
        let mut h = vec![0u8; DDS_BASE_HEADER_BYTES];
        h[0..4].copy_from_slice(&DDS_MAGIC.to_le_bytes());
        h[4..8].copy_from_slice(&DDS_HEADER_SIZE_FIELD.to_le_bytes());
        h[12..16].copy_from_slice(&height.to_le_bytes());
        h[16..20].copy_from_slice(&width.to_le_bytes());
        h[28..32].copy_from_slice(&mips.to_le_bytes());
        h[80..84].copy_from_slice(&pf_flags.to_le_bytes());
        h[84..88].copy_from_slice(&fourcc.to_le_bytes());
        h[88..92].copy_from_slice(&rgb_bits.to_le_bytes());
        h[112..116].copy_from_slice(&caps2.to_le_bytes());
        h
    }

    fn append_dx10(hdr: &mut Vec<u8>, dxgi_format: u32, misc_flag: u32, array_size: u32) {
        hdr.extend_from_slice(&dxgi_format.to_le_bytes());
        hdr.extend_from_slice(&3u32.to_le_bytes()); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        hdr.extend_from_slice(&misc_flag.to_le_bytes());
        hdr.extend_from_slice(&array_size.to_le_bytes());
        hdr.extend_from_slice(&0u32.to_le_bytes()); // miscFlags2
    }

    #[test]
    fn dds_dxt1_mipmap_chain_and_truncation() {
        // 8x8 DXT1 with 4 mips: 8x8 (32B) + 4x4 (8B) + 2x2 (8B) + 1x1 (8B) = 56B + 128B = 184B
        let mut buf = make_dds_header(8, 8, 4, DDPF_FOURCC, FOURCC_DXT1, 0, 0);
        buf.resize(184, 0xAA);
        let info = verify_dds_mipmap_chain(&buf).expect("valid 8x8 DXT1 4-mip DDS");
        assert_eq!(info.expected_min_size, 184);
        assert!(info.is_block_compressed);
        assert_eq!(info.block_or_pixel_bytes, 8);

        buf.pop(); // 183B -> truncated by 1 byte
        match verify_dds_mipmap_chain(&buf) {
            Err(DdsError::TruncatedPayload {
                actual_size: 183,
                expected_min_size: 184,
                ..
            }) => {}
            other => panic!("expected TruncatedPayload, got {other:?}"),
        }
    }

    #[test]
    fn dds_dx10_bc7_cubemap_and_bc1_bc4_block_math() {
        // 16x16 BC7 (DXGI 98) cubemap, 2 mips:
        // mip0: 4x4 blocks * 16B = 256B; mip1: 2x2 blocks * 16B = 64B => 320B/face * 6 = 1920B
        // Total = 148 + 1920 = 2068B
        let mut buf = make_dds_header(16, 16, 2, DDPF_FOURCC, FOURCC_DX10, 0, DDSCAPS2_CUBEMAP);
        append_dx10(&mut buf, 98, DDS_RESOURCE_MISC_TEXTURECUBE, 1);
        buf.resize(2068, 0);

        let info = verify_dds_mipmap_chain(&buf).expect("valid DX10 BC7 cubemap");
        assert!(info.is_dx10);
        assert!(info.is_cubemap);
        assert_eq!(info.header_bytes, 148);
        assert_eq!(info.dxgi_format, 98);
        assert_eq!(info.block_or_pixel_bytes, 16);
        assert_eq!(info.expected_min_size, 2068);

        // DX10 BC4 (DXGI 80): 4x4 = 1 block * 8B + 148B = 156B
        let mut bc4 = make_dds_header(4, 4, 1, DDPF_FOURCC, FOURCC_DX10, 0, 0);
        append_dx10(&mut bc4, 80, 0, 1);
        bc4.resize(156, 0);
        let bc4_info = verify_dds_mipmap_chain(&bc4).expect("valid DX10 BC4");
        assert_eq!(bc4_info.block_or_pixel_bytes, 8);
        assert_eq!(bc4_info.expected_min_size, 156);
    }

    #[test]
    fn dds_rejects_bad_magic_dwsize_dimensions_and_overflow() {
        let mut bad_magic = make_dds_header(4, 4, 1, DDPF_FOURCC, FOURCC_DXT5, 0, 0);
        bad_magic[0] = b'X';
        assert!(matches!(
            verify_dds_mipmap_chain(&bad_magic),
            Err(DdsError::InvalidMagic(_))
        ));

        let mut bad_size = make_dds_header(4, 4, 1, DDPF_FOURCC, FOURCC_DXT5, 0, 0);
        bad_size[4..8].copy_from_slice(&120u32.to_le_bytes());
        assert_eq!(
            verify_dds_mipmap_chain(&bad_size),
            Err(DdsError::InvalidHeaderSize(120))
        );

        let zero_dim = make_dds_header(0, 64, 1, 0, 0, 32, 0);
        assert_eq!(
            verify_dds_mipmap_chain(&zero_dim),
            Err(DdsError::InvalidDimensions {
                width: 0,
                height: 64,
            })
        );

        let huge_dim = make_dds_header(32768, 64, 1, 0, 0, 32, 0);
        assert_eq!(
            verify_dds_mipmap_chain(&huge_dim),
            Err(DdsError::InvalidDimensions {
                width: 32768,
                height: 64,
            })
        );

        // Overflow via 16384x16384 RGBA32 cubemap * u32::MAX array slices must return SizeOverflow.
        let mut ovf = make_dds_header(16384, 16384, 1, DDPF_FOURCC, FOURCC_DX10, 0, DDSCAPS2_CUBEMAP);
        append_dx10(&mut ovf, 28, DDS_RESOURCE_MISC_TEXTURECUBE, u32::MAX);
        assert_eq!(
            verify_dds_mipmap_chain_with_total_size(&ovf, u64::MAX),
            Err(DdsError::SizeOverflow)
        );
    }

    #[test]
    fn dds_uncompressed_rgb_and_dx10_bc2_bc3_bc5_bc6h_formats() {
        // Uncompressed 24-bit RGB 4x4, 1 mip: 128 + 4*4*3 = 176B
        let mut rgb24 = make_dds_header(4, 4, 1, 0, 0, 24, 0);
        rgb24.resize(176, 0);
        let info = verify_dds_mipmap_chain(&rgb24).expect("valid 24-bit uncompressed DDS");
        assert!(!info.is_block_compressed);
        assert_eq!(info.block_or_pixel_bytes, 3);
        assert_eq!(info.expected_min_size, 176);

        // DX10 BC1 (71) = 8B/block; BC2 (74), BC3 (77), BC5 (83), BC6H (95), BC7 (98) = 16B/block.
        for &(fmt, expected_block) in &[
            (71u32, 8u8),
            (74u32, 16u8),
            (77u32, 16u8),
            (83u32, 16u8),
            (95u32, 16u8),
            (98u32, 16u8),
        ] {
            let mut buf = make_dds_header(4, 4, 1, DDPF_FOURCC, FOURCC_DX10, 0, 0);
            append_dx10(&mut buf, fmt, 0, 1);
            buf.resize(DDS_DX10_HEADER_BYTES + expected_block as usize, 0);
            let parsed = verify_dds_mipmap_chain(&buf).expect("valid DX10 BC format");
            assert!(parsed.is_block_compressed);
            assert_eq!(parsed.block_or_pixel_bytes, expected_block);
            assert_eq!(
                parsed.expected_min_size,
                (DDS_DX10_HEADER_BYTES + expected_block as usize) as u64
            );
        }

        // mipMapCount > 16 collapses to 1 mip matching VerifyDdsMipmapChainSize.
        let mut many_mips = make_dds_header(4, 4, 99, DDPF_FOURCC, FOURCC_DXT5, 0, 0);
        many_mips.resize(128 + 16, 0);
        let parsed = verify_dds_mipmap_chain(&many_mips).expect("mip > 16 collapses to 1");
        assert_eq!(parsed.mip_count, 1);
        assert_eq!(parsed.expected_min_size, 144);
    }
}
