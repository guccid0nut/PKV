// SPDX-License-Identifier: MPL-2.0
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ============================================================================
// File: avcodec_proxy.cpp
// Subsystem: Project Kongor Hardware-Accelerated Video Decode Shim
// Description: Intercepts FFmpeg avcodec initialization to attach D3D11VA / DXVA2
//              GPU hardware acceleration while forwarding non-hooked routines to
//              avcodec_core.dll with zero CPU overhead.
// ============================================================================

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <atomic>

// ============================================================================
// PE Export Forwarders (Zero Overhead - Forwarded by Windows Loader to avcodec_core.dll)
// ============================================================================
#pragma comment(linker, "/export:av_ac3_parse_header=avcodec_core.av_ac3_parse_header")
#pragma comment(linker, "/export:av_adts_header_parse=avcodec_core.av_adts_header_parse")
#pragma comment(linker, "/export:av_bitstream_filter_close=avcodec_core.av_bitstream_filter_close")
#pragma comment(linker, "/export:av_bitstream_filter_filter=avcodec_core.av_bitstream_filter_filter")
#pragma comment(linker, "/export:av_bitstream_filter_init=avcodec_core.av_bitstream_filter_init")
#pragma comment(linker, "/export:av_bitstream_filter_next=avcodec_core.av_bitstream_filter_next")
#pragma comment(linker, "/export:av_bsf_alloc=avcodec_core.av_bsf_alloc")
#pragma comment(linker, "/export:av_bsf_flush=avcodec_core.av_bsf_flush")
#pragma comment(linker, "/export:av_bsf_free=avcodec_core.av_bsf_free")
#pragma comment(linker, "/export:av_bsf_get_by_name=avcodec_core.av_bsf_get_by_name")
#pragma comment(linker, "/export:av_bsf_get_class=avcodec_core.av_bsf_get_class")
#pragma comment(linker, "/export:av_bsf_get_null_filter=avcodec_core.av_bsf_get_null_filter")
#pragma comment(linker, "/export:av_bsf_init=avcodec_core.av_bsf_init")
#pragma comment(linker, "/export:av_bsf_iterate=avcodec_core.av_bsf_iterate")
#pragma comment(linker, "/export:av_bsf_list_alloc=avcodec_core.av_bsf_list_alloc")
#pragma comment(linker, "/export:av_bsf_list_append=avcodec_core.av_bsf_list_append")
#pragma comment(linker, "/export:av_bsf_list_append2=avcodec_core.av_bsf_list_append2")
#pragma comment(linker, "/export:av_bsf_list_finalize=avcodec_core.av_bsf_list_finalize")
#pragma comment(linker, "/export:av_bsf_list_free=avcodec_core.av_bsf_list_free")
#pragma comment(linker, "/export:av_bsf_list_parse_str=avcodec_core.av_bsf_list_parse_str")
#pragma comment(linker, "/export:av_bsf_next=avcodec_core.av_bsf_next")
#pragma comment(linker, "/export:av_bsf_receive_packet=avcodec_core.av_bsf_receive_packet")
#pragma comment(linker, "/export:av_bsf_send_packet=avcodec_core.av_bsf_send_packet")
#pragma comment(linker, "/export:av_codec_ffversion=avcodec_core.av_codec_ffversion")
#pragma comment(linker, "/export:av_codec_get_chroma_intra_matrix=avcodec_core.av_codec_get_chroma_intra_matrix")
#pragma comment(linker, "/export:av_codec_get_codec_descriptor=avcodec_core.av_codec_get_codec_descriptor")
#pragma comment(linker, "/export:av_codec_get_codec_properties=avcodec_core.av_codec_get_codec_properties")
#pragma comment(linker, "/export:av_codec_get_lowres=avcodec_core.av_codec_get_lowres")
#pragma comment(linker, "/export:av_codec_get_max_lowres=avcodec_core.av_codec_get_max_lowres")
#pragma comment(linker, "/export:av_codec_get_pkt_timebase=avcodec_core.av_codec_get_pkt_timebase")
#pragma comment(linker, "/export:av_codec_get_seek_preroll=avcodec_core.av_codec_get_seek_preroll")
#pragma comment(linker, "/export:av_codec_is_decoder=avcodec_core.av_codec_is_decoder")
#pragma comment(linker, "/export:av_codec_is_encoder=avcodec_core.av_codec_is_encoder")
#pragma comment(linker, "/export:av_codec_iterate=avcodec_core.av_codec_iterate")
#pragma comment(linker, "/export:av_codec_next=avcodec_core.av_codec_next")
#pragma comment(linker, "/export:av_codec_set_chroma_intra_matrix=avcodec_core.av_codec_set_chroma_intra_matrix")
#pragma comment(linker, "/export:av_codec_set_codec_descriptor=avcodec_core.av_codec_set_codec_descriptor")
#pragma comment(linker, "/export:av_codec_set_lowres=avcodec_core.av_codec_set_lowres")
#pragma comment(linker, "/export:av_codec_set_pkt_timebase=avcodec_core.av_codec_set_pkt_timebase")
#pragma comment(linker, "/export:av_codec_set_seek_preroll=avcodec_core.av_codec_set_seek_preroll")
#pragma comment(linker, "/export:av_copy_packet=avcodec_core.av_copy_packet")
#pragma comment(linker, "/export:av_copy_packet_side_data=avcodec_core.av_copy_packet_side_data")
#pragma comment(linker, "/export:av_cpb_properties_alloc=avcodec_core.av_cpb_properties_alloc")
#pragma comment(linker, "/export:av_d3d11va_alloc_context=avcodec_core.av_d3d11va_alloc_context")
#pragma comment(linker, "/export:av_dct_calc=avcodec_core.av_dct_calc")
#pragma comment(linker, "/export:av_dct_end=avcodec_core.av_dct_end")
#pragma comment(linker, "/export:av_dct_init=avcodec_core.av_dct_init")
#pragma comment(linker, "/export:av_dirac_parse_sequence_header=avcodec_core.av_dirac_parse_sequence_header")
#pragma comment(linker, "/export:av_dup_packet=avcodec_core.av_dup_packet")
#pragma comment(linker, "/export:av_dv_codec_profile=avcodec_core.av_dv_codec_profile")
#pragma comment(linker, "/export:av_dv_codec_profile2=avcodec_core.av_dv_codec_profile2")
#pragma comment(linker, "/export:av_dv_frame_profile=avcodec_core.av_dv_frame_profile")
#pragma comment(linker, "/export:av_fast_padded_malloc=avcodec_core.av_fast_padded_malloc")
#pragma comment(linker, "/export:av_fast_padded_mallocz=avcodec_core.av_fast_padded_mallocz")
#pragma comment(linker, "/export:av_fft_calc=avcodec_core.av_fft_calc")
#pragma comment(linker, "/export:av_fft_end=avcodec_core.av_fft_end")
#pragma comment(linker, "/export:av_fft_init=avcodec_core.av_fft_init")
#pragma comment(linker, "/export:av_fft_permute=avcodec_core.av_fft_permute")
#pragma comment(linker, "/export:av_free_packet=avcodec_core.av_free_packet")
#pragma comment(linker, "/export:av_get_audio_frame_duration=avcodec_core.av_get_audio_frame_duration")
#pragma comment(linker, "/export:av_get_audio_frame_duration2=avcodec_core.av_get_audio_frame_duration2")
#pragma comment(linker, "/export:av_get_bits_per_sample=avcodec_core.av_get_bits_per_sample")
#pragma comment(linker, "/export:av_get_codec_tag_string=avcodec_core.av_get_codec_tag_string")
#pragma comment(linker, "/export:av_get_exact_bits_per_sample=avcodec_core.av_get_exact_bits_per_sample")
#pragma comment(linker, "/export:av_get_pcm_codec=avcodec_core.av_get_pcm_codec")
#pragma comment(linker, "/export:av_get_profile_name=avcodec_core.av_get_profile_name")
#pragma comment(linker, "/export:av_grow_packet=avcodec_core.av_grow_packet")
#pragma comment(linker, "/export:av_hwaccel_next=avcodec_core.av_hwaccel_next")
#pragma comment(linker, "/export:av_imdct_calc=avcodec_core.av_imdct_calc")
#pragma comment(linker, "/export:av_imdct_half=avcodec_core.av_imdct_half")
#pragma comment(linker, "/export:av_init_packet=avcodec_core.av_init_packet")
#pragma comment(linker, "/export:av_jni_get_java_vm=avcodec_core.av_jni_get_java_vm")
#pragma comment(linker, "/export:av_jni_set_java_vm=avcodec_core.av_jni_set_java_vm")
#pragma comment(linker, "/export:av_lockmgr_register=avcodec_core.av_lockmgr_register")
#pragma comment(linker, "/export:av_mdct_calc=avcodec_core.av_mdct_calc")
#pragma comment(linker, "/export:av_mdct_end=avcodec_core.av_mdct_end")
#pragma comment(linker, "/export:av_mdct_init=avcodec_core.av_mdct_init")
#pragma comment(linker, "/export:av_mediacodec_alloc_context=avcodec_core.av_mediacodec_alloc_context")
#pragma comment(linker, "/export:av_mediacodec_default_free=avcodec_core.av_mediacodec_default_free")
#pragma comment(linker, "/export:av_mediacodec_default_init=avcodec_core.av_mediacodec_default_init")
#pragma comment(linker, "/export:av_mediacodec_release_buffer=avcodec_core.av_mediacodec_release_buffer")
#pragma comment(linker, "/export:av_mediacodec_render_buffer_at_time=avcodec_core.av_mediacodec_render_buffer_at_time")
#pragma comment(linker, "/export:av_new_packet=avcodec_core.av_new_packet")
#pragma comment(linker, "/export:av_packet_add_side_data=avcodec_core.av_packet_add_side_data")
#pragma comment(linker, "/export:av_packet_alloc=avcodec_core.av_packet_alloc")
#pragma comment(linker, "/export:av_packet_clone=avcodec_core.av_packet_clone")
#pragma comment(linker, "/export:av_packet_copy_props=avcodec_core.av_packet_copy_props")
#pragma comment(linker, "/export:av_packet_free=avcodec_core.av_packet_free")
#pragma comment(linker, "/export:av_packet_free_side_data=avcodec_core.av_packet_free_side_data")
#pragma comment(linker, "/export:av_packet_from_data=avcodec_core.av_packet_from_data")
#pragma comment(linker, "/export:av_packet_get_side_data=avcodec_core.av_packet_get_side_data")
#pragma comment(linker, "/export:av_packet_make_refcounted=avcodec_core.av_packet_make_refcounted")
#pragma comment(linker, "/export:av_packet_make_writable=avcodec_core.av_packet_make_writable")
#pragma comment(linker, "/export:av_packet_merge_side_data=avcodec_core.av_packet_merge_side_data")
#pragma comment(linker, "/export:av_packet_move_ref=avcodec_core.av_packet_move_ref")
#pragma comment(linker, "/export:av_packet_new_side_data=avcodec_core.av_packet_new_side_data")
#pragma comment(linker, "/export:av_packet_pack_dictionary=avcodec_core.av_packet_pack_dictionary")
#pragma comment(linker, "/export:av_packet_ref=avcodec_core.av_packet_ref")
#pragma comment(linker, "/export:av_packet_rescale_ts=avcodec_core.av_packet_rescale_ts")
#pragma comment(linker, "/export:av_packet_shrink_side_data=avcodec_core.av_packet_shrink_side_data")
#pragma comment(linker, "/export:av_packet_side_data_name=avcodec_core.av_packet_side_data_name")
#pragma comment(linker, "/export:av_packet_split_side_data=avcodec_core.av_packet_split_side_data")
#pragma comment(linker, "/export:av_packet_unpack_dictionary=avcodec_core.av_packet_unpack_dictionary")
#pragma comment(linker, "/export:av_packet_unref=avcodec_core.av_packet_unref")
#pragma comment(linker, "/export:av_parser_change=avcodec_core.av_parser_change")
#pragma comment(linker, "/export:av_parser_close=avcodec_core.av_parser_close")
#pragma comment(linker, "/export:av_parser_init=avcodec_core.av_parser_init")
#pragma comment(linker, "/export:av_parser_iterate=avcodec_core.av_parser_iterate")
#pragma comment(linker, "/export:av_parser_next=avcodec_core.av_parser_next")
#pragma comment(linker, "/export:av_parser_parse2=avcodec_core.av_parser_parse2")
#pragma comment(linker, "/export:av_picture_copy=avcodec_core.av_picture_copy")
#pragma comment(linker, "/export:av_picture_crop=avcodec_core.av_picture_crop")
#pragma comment(linker, "/export:av_picture_pad=avcodec_core.av_picture_pad")
#pragma comment(linker, "/export:av_qsv_alloc_context=avcodec_core.av_qsv_alloc_context")
#pragma comment(linker, "/export:av_rdft_calc=avcodec_core.av_rdft_calc")
#pragma comment(linker, "/export:av_rdft_end=avcodec_core.av_rdft_end")
#pragma comment(linker, "/export:av_rdft_init=avcodec_core.av_rdft_init")
#pragma comment(linker, "/export:av_register_bitstream_filter=avcodec_core.av_register_bitstream_filter")
#pragma comment(linker, "/export:av_register_codec_parser=avcodec_core.av_register_codec_parser")
#pragma comment(linker, "/export:av_register_hwaccel=avcodec_core.av_register_hwaccel")
#pragma comment(linker, "/export:av_shrink_packet=avcodec_core.av_shrink_packet")
#pragma comment(linker, "/export:av_vorbis_parse_frame=avcodec_core.av_vorbis_parse_frame")
#pragma comment(linker, "/export:av_vorbis_parse_frame_flags=avcodec_core.av_vorbis_parse_frame_flags")
#pragma comment(linker, "/export:av_vorbis_parse_free=avcodec_core.av_vorbis_parse_free")
#pragma comment(linker, "/export:av_vorbis_parse_init=avcodec_core.av_vorbis_parse_init")
#pragma comment(linker, "/export:av_vorbis_parse_reset=avcodec_core.av_vorbis_parse_reset")
#pragma comment(linker, "/export:av_xiphlacing=avcodec_core.av_xiphlacing")
#pragma comment(linker, "/export:avcodec_align_dimensions=avcodec_core.avcodec_align_dimensions")
#pragma comment(linker, "/export:avcodec_align_dimensions2=avcodec_core.avcodec_align_dimensions2")
#pragma comment(linker, "/export:avcodec_alloc_context3=avcodec_core.avcodec_alloc_context3")
#pragma comment(linker, "/export:avcodec_chroma_pos_to_enum=avcodec_core.avcodec_chroma_pos_to_enum")
#pragma comment(linker, "/export:avcodec_close=avcodec_core.avcodec_close")
#pragma comment(linker, "/export:avcodec_configuration=avcodec_core.avcodec_configuration")
#pragma comment(linker, "/export:avcodec_copy_context=avcodec_core.avcodec_copy_context")
#pragma comment(linker, "/export:avcodec_dct_alloc=avcodec_core.avcodec_dct_alloc")
#pragma comment(linker, "/export:avcodec_dct_get_class=avcodec_core.avcodec_dct_get_class")
#pragma comment(linker, "/export:avcodec_dct_init=avcodec_core.avcodec_dct_init")
#pragma comment(linker, "/export:avcodec_decode_audio4=avcodec_core.avcodec_decode_audio4")
#pragma comment(linker, "/export:avcodec_decode_subtitle2=avcodec_core.avcodec_decode_subtitle2")
#pragma comment(linker, "/export:avcodec_decode_video2=avcodec_core.avcodec_decode_video2")
#pragma comment(linker, "/export:avcodec_default_execute=avcodec_core.avcodec_default_execute")
#pragma comment(linker, "/export:avcodec_default_execute2=avcodec_core.avcodec_default_execute2")
#pragma comment(linker, "/export:avcodec_default_get_buffer2=avcodec_core.avcodec_default_get_buffer2")
#pragma comment(linker, "/export:avcodec_default_get_format=avcodec_core.avcodec_default_get_format")
#pragma comment(linker, "/export:avcodec_descriptor_get=avcodec_core.avcodec_descriptor_get")
#pragma comment(linker, "/export:avcodec_descriptor_get_by_name=avcodec_core.avcodec_descriptor_get_by_name")
#pragma comment(linker, "/export:avcodec_descriptor_next=avcodec_core.avcodec_descriptor_next")
#pragma comment(linker, "/export:avcodec_encode_audio2=avcodec_core.avcodec_encode_audio2")
#pragma comment(linker, "/export:avcodec_encode_subtitle=avcodec_core.avcodec_encode_subtitle")
#pragma comment(linker, "/export:avcodec_encode_video2=avcodec_core.avcodec_encode_video2")
#pragma comment(linker, "/export:avcodec_enum_to_chroma_pos=avcodec_core.avcodec_enum_to_chroma_pos")
#pragma comment(linker, "/export:avcodec_fill_audio_frame=avcodec_core.avcodec_fill_audio_frame")
#pragma comment(linker, "/export:avcodec_find_best_pix_fmt2=avcodec_core.avcodec_find_best_pix_fmt2")
#pragma comment(linker, "/export:avcodec_find_best_pix_fmt_of_2=avcodec_core.avcodec_find_best_pix_fmt_of_2")
#pragma comment(linker, "/export:avcodec_find_best_pix_fmt_of_list=avcodec_core.avcodec_find_best_pix_fmt_of_list")
#pragma comment(linker, "/export:avcodec_find_decoder=avcodec_core.avcodec_find_decoder")
#pragma comment(linker, "/export:avcodec_find_decoder_by_name=avcodec_core.avcodec_find_decoder_by_name")
#pragma comment(linker, "/export:avcodec_find_encoder=avcodec_core.avcodec_find_encoder")
#pragma comment(linker, "/export:avcodec_find_encoder_by_name=avcodec_core.avcodec_find_encoder_by_name")
#pragma comment(linker, "/export:avcodec_flush_buffers=avcodec_core.avcodec_flush_buffers")
#pragma comment(linker, "/export:avcodec_free_context=avcodec_core.avcodec_free_context")
#pragma comment(linker, "/export:avcodec_get_chroma_sub_sample=avcodec_core.avcodec_get_chroma_sub_sample")
#pragma comment(linker, "/export:avcodec_get_class=avcodec_core.avcodec_get_class")
#pragma comment(linker, "/export:avcodec_get_context_defaults3=avcodec_core.avcodec_get_context_defaults3")
#pragma comment(linker, "/export:avcodec_get_frame_class=avcodec_core.avcodec_get_frame_class")
#pragma comment(linker, "/export:avcodec_get_hw_config=avcodec_core.avcodec_get_hw_config")
#pragma comment(linker, "/export:avcodec_get_hw_frames_parameters=avcodec_core.avcodec_get_hw_frames_parameters")
#pragma comment(linker, "/export:avcodec_get_name=avcodec_core.avcodec_get_name")
#pragma comment(linker, "/export:avcodec_get_pix_fmt_loss=avcodec_core.avcodec_get_pix_fmt_loss")
#pragma comment(linker, "/export:avcodec_get_subtitle_rect_class=avcodec_core.avcodec_get_subtitle_rect_class")
#pragma comment(linker, "/export:avcodec_get_type=avcodec_core.avcodec_get_type")
#pragma comment(linker, "/export:avcodec_is_open=avcodec_core.avcodec_is_open")
#pragma comment(linker, "/export:avcodec_license=avcodec_core.avcodec_license")
#pragma comment(linker, "/export:avcodec_parameters_alloc=avcodec_core.avcodec_parameters_alloc")
#pragma comment(linker, "/export:avcodec_parameters_copy=avcodec_core.avcodec_parameters_copy")
#pragma comment(linker, "/export:avcodec_parameters_free=avcodec_core.avcodec_parameters_free")
#pragma comment(linker, "/export:avcodec_parameters_from_context=avcodec_core.avcodec_parameters_from_context")
#pragma comment(linker, "/export:avcodec_parameters_to_context=avcodec_core.avcodec_parameters_to_context")
#pragma comment(linker, "/export:avcodec_pix_fmt_to_codec_tag=avcodec_core.avcodec_pix_fmt_to_codec_tag")
#pragma comment(linker, "/export:avcodec_profile_name=avcodec_core.avcodec_profile_name")
#pragma comment(linker, "/export:avcodec_receive_packet=avcodec_core.avcodec_receive_packet")
#pragma comment(linker, "/export:avcodec_register=avcodec_core.avcodec_register")
#pragma comment(linker, "/export:avcodec_register_all=avcodec_core.avcodec_register_all")
#pragma comment(linker, "/export:avcodec_send_frame=avcodec_core.avcodec_send_frame")
#pragma comment(linker, "/export:avcodec_send_packet=avcodec_core.avcodec_send_packet")
#pragma comment(linker, "/export:avcodec_string=avcodec_core.avcodec_string")
#pragma comment(linker, "/export:avcodec_version=avcodec_core.avcodec_version")
#pragma comment(linker, "/export:avpicture_alloc=avcodec_core.avpicture_alloc")
#pragma comment(linker, "/export:avpicture_fill=avcodec_core.avpicture_fill")
#pragma comment(linker, "/export:avpicture_free=avcodec_core.avpicture_free")
#pragma comment(linker, "/export:avpicture_get_size=avcodec_core.avpicture_get_size")
#pragma comment(linker, "/export:avpicture_layout=avcodec_core.avpicture_layout")
#pragma comment(linker, "/export:avpriv_ac3_channel_layout_tab=avcodec_core.avpriv_ac3_channel_layout_tab")
#pragma comment(linker, "/export:avpriv_ac3_parse_header=avcodec_core.avpriv_ac3_parse_header")
#pragma comment(linker, "/export:avpriv_align_put_bits=avcodec_core.avpriv_align_put_bits")
#pragma comment(linker, "/export:avpriv_bprint_to_extradata=avcodec_core.avpriv_bprint_to_extradata")
#pragma comment(linker, "/export:avpriv_codec2_mode_bit_rate=avcodec_core.avpriv_codec2_mode_bit_rate")
#pragma comment(linker, "/export:avpriv_codec2_mode_block_align=avcodec_core.avpriv_codec2_mode_block_align")
#pragma comment(linker, "/export:avpriv_codec2_mode_frame_size=avcodec_core.avpriv_codec2_mode_frame_size")
#pragma comment(linker, "/export:avpriv_codec_get_cap_skip_frame_fill_param=avcodec_core.avpriv_codec_get_cap_skip_frame_fill_param")
#pragma comment(linker, "/export:avpriv_copy_bits=avcodec_core.avpriv_copy_bits")
#pragma comment(linker, "/export:avpriv_dca_convert_bitstream=avcodec_core.avpriv_dca_convert_bitstream")
#pragma comment(linker, "/export:avpriv_dca_parse_core_frame_header=avcodec_core.avpriv_dca_parse_core_frame_header")
#pragma comment(linker, "/export:avpriv_dca_sample_rates=avcodec_core.avpriv_dca_sample_rates")
#pragma comment(linker, "/export:avpriv_dnxhd_get_frame_size=avcodec_core.avpriv_dnxhd_get_frame_size")
#pragma comment(linker, "/export:avpriv_dnxhd_get_hr_frame_size=avcodec_core.avpriv_dnxhd_get_hr_frame_size")
#pragma comment(linker, "/export:avpriv_dnxhd_get_interlaced=avcodec_core.avpriv_dnxhd_get_interlaced")
#pragma comment(linker, "/export:avpriv_do_elbg=avcodec_core.avpriv_do_elbg")
#pragma comment(linker, "/export:avpriv_exif_decode_ifd=avcodec_core.avpriv_exif_decode_ifd")
#pragma comment(linker, "/export:avpriv_find_pix_fmt=avcodec_core.avpriv_find_pix_fmt")
#pragma comment(linker, "/export:avpriv_find_start_code=avcodec_core.avpriv_find_start_code")
#pragma comment(linker, "/export:avpriv_fits_header_init=avcodec_core.avpriv_fits_header_init")
#pragma comment(linker, "/export:avpriv_fits_header_parse_line=avcodec_core.avpriv_fits_header_parse_line")
#pragma comment(linker, "/export:avpriv_get_raw_pix_fmt_tags=avcodec_core.avpriv_get_raw_pix_fmt_tags")
#pragma comment(linker, "/export:avpriv_h264_has_num_reorder_frames=avcodec_core.avpriv_h264_has_num_reorder_frames")
#pragma comment(linker, "/export:avpriv_init_elbg=avcodec_core.avpriv_init_elbg")
#pragma comment(linker, "/export:avpriv_mjpeg_bits_ac_chrominance=avcodec_core.avpriv_mjpeg_bits_ac_chrominance")
#pragma comment(linker, "/export:avpriv_mjpeg_bits_ac_luminance=avcodec_core.avpriv_mjpeg_bits_ac_luminance")
#pragma comment(linker, "/export:avpriv_mjpeg_bits_dc_chrominance=avcodec_core.avpriv_mjpeg_bits_dc_chrominance")
#pragma comment(linker, "/export:avpriv_mjpeg_bits_dc_luminance=avcodec_core.avpriv_mjpeg_bits_dc_luminance")
#pragma comment(linker, "/export:avpriv_mjpeg_val_ac_chrominance=avcodec_core.avpriv_mjpeg_val_ac_chrominance")
#pragma comment(linker, "/export:avpriv_mjpeg_val_ac_luminance=avcodec_core.avpriv_mjpeg_val_ac_luminance")
#pragma comment(linker, "/export:avpriv_mjpeg_val_dc=avcodec_core.avpriv_mjpeg_val_dc")
#pragma comment(linker, "/export:avpriv_mpa_bitrate_tab=avcodec_core.avpriv_mpa_bitrate_tab")
#pragma comment(linker, "/export:avpriv_mpa_freq_tab=avcodec_core.avpriv_mpa_freq_tab")
#pragma comment(linker, "/export:avpriv_mpeg4audio_get_config=avcodec_core.avpriv_mpeg4audio_get_config")
#pragma comment(linker, "/export:avpriv_mpeg4audio_get_config2=avcodec_core.avpriv_mpeg4audio_get_config2")
#pragma comment(linker, "/export:avpriv_mpeg4audio_sample_rates=avcodec_core.avpriv_mpeg4audio_sample_rates")
#pragma comment(linker, "/export:avpriv_mpegaudio_decode_header=avcodec_core.avpriv_mpegaudio_decode_header")
#pragma comment(linker, "/export:avpriv_pix_fmt_bps_avi=avcodec_core.avpriv_pix_fmt_bps_avi")
#pragma comment(linker, "/export:avpriv_pix_fmt_bps_mov=avcodec_core.avpriv_pix_fmt_bps_mov")
#pragma comment(linker, "/export:avpriv_put_string=avcodec_core.avpriv_put_string")
#pragma comment(linker, "/export:avpriv_split_xiph_headers=avcodec_core.avpriv_split_xiph_headers")
#pragma comment(linker, "/export:avpriv_tak_parse_streaminfo=avcodec_core.avpriv_tak_parse_streaminfo")
#pragma comment(linker, "/export:avpriv_toupper4=avcodec_core.avpriv_toupper4")
#pragma comment(linker, "/export:avsubtitle_free=avcodec_core.avsubtitle_free")

#pragma pack(push, 8)
struct VideoTelemetry {
    int32_t hwAccelEnabled;
    int32_t hwDeviceType;       // 0=None, 1=D3D11VA, 2=DXVA2
    int32_t framesDecoded;
    int32_t framesHwDecoded;
    int32_t lastWidth;
    int32_t lastHeight;
    float   fps;
    char    gpuAdapterName[128];
};
#pragma pack(pop)

static HMODULE g_hRealCore = nullptr;
static HMODULE g_hAvUtil = nullptr;
static std::atomic<int32_t> g_hwAccelEnabled{1};
static std::atomic<int32_t> g_hwDeviceType{1}; // Default: D3D11VA
static std::atomic<int32_t> g_framesDecoded{0};
static std::atomic<int32_t> g_framesHwDecoded{0};
static std::atomic<int32_t> g_lastWidth{1920};
static std::atomic<int32_t> g_lastHeight{1080};
static char g_gpuAdapterName[128] = "Direct3D 11 Video Acceleration (D3D11VA)";

typedef int (*PFN_avcodec_open2)(void* avctx, const void* codec, void** options);
typedef int (*PFN_avcodec_receive_frame)(void* avctx, void* frame);
typedef int (*PFN_av_hwdevice_ctx_create)(void** device_ctx, int type, const char* device, void* opts, int flags);
typedef int (*PFN_avformat_seek_file)(void* s, int stream_index, int64_t min_ts, int64_t ts, int64_t max_ts, int flags);
typedef void (*PFN_avcodec_flush_buffers)(void* avctx);
typedef void (*PFN_Kongor_Audio_Seek)(int channelIndex, unsigned int positionMs);

extern "C" void* get_caller_rbx();

static PFN_avcodec_open2 g_real_open2 = nullptr;
static PFN_avcodec_receive_frame g_real_receive_frame = nullptr;
static PFN_av_hwdevice_ctx_create g_pfn_hwdevice_create = nullptr;
static PFN_avformat_seek_file g_pfn_seek_file = nullptr;
static PFN_avcodec_flush_buffers g_pfn_flush_buffers = nullptr;
static std::atomic<float> g_pendingVideoSeekTarget{-1.0f};
static std::atomic<bool> g_pendingAudioSync{false};
static std::atomic<uintptr_t> g_lastSeekCVideo{0};

static void LogVideo(const char* format, ...) {
    char buf[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(buf, sizeof(buf), format, args);
    va_end(args);

    OutputDebugStringA(buf);

    FILE* f = fopen("logs/kongor_video.log", "a");
    if (f) {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "[%04d-%02d-%02d %02d:%02d:%02d.%03d] %s\n",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, buf);
        fclose(f);
    }
}

static void EnsureCoreLoaded() {
    if (g_hRealCore) return;

    g_hRealCore = LoadLibraryA("avcodec_core.dll");
    if (!g_hRealCore) {
        g_hRealCore = LoadLibraryA("avcodec-x64-58_core.dll");
    }
    if (!g_hRealCore) {
        g_hRealCore = LoadLibraryA("avcodec-x64-58.dll.bak");
    }

    if (g_hRealCore) {
        g_real_open2 = (PFN_avcodec_open2)GetProcAddress(g_hRealCore, "avcodec_open2");
        g_real_receive_frame = (PFN_avcodec_receive_frame)GetProcAddress(g_hRealCore, "avcodec_receive_frame");
        LogVideo("[KONGOR_VIDEO] Genuine FFmpeg avcodec core loaded successfully.");
    } else {
        LogVideo("[KONGOR_VIDEO][ERROR] Could not locate avcodec_core.dll or backup!");
    }

    if (!g_hAvUtil) {
        g_hAvUtil = LoadLibraryA("avutil-x64-56.dll");
        if (g_hAvUtil) {
            g_pfn_hwdevice_create = (PFN_av_hwdevice_ctx_create)GetProcAddress(g_hAvUtil, "av_hwdevice_ctx_create");
        }
    }
}

extern "C" {

__declspec(dllexport) int avcodec_open2(void* avctx, const void* codec, void** options) {
    if (!avctx) {
        return -22; // AVERROR(EINVAL)
    }
    EnsureCoreLoaded();
    LogVideo("[KONGOR_VIDEO] avcodec_open2 called. Configuring GPU hardware decode acceleration...");

    if (g_hwAccelEnabled && g_pfn_hwdevice_create && avctx) {
        void* hw_ctx = nullptr;
        // Try D3D11VA (type 3)
        int ret = g_pfn_hwdevice_create(&hw_ctx, 3 /* AV_HWDEVICE_TYPE_D3D11VA */, nullptr, nullptr, 0);
        if (ret >= 0 && hw_ctx) {
            g_hwDeviceType = 1;
            strncpy(g_gpuAdapterName, "Direct3D 11 Hardware Video Accelerator (D3D11VA)", sizeof(g_gpuAdapterName) - 1);
            LogVideo("[KONGOR_VIDEO] D3D11VA Hardware Device Context attached successfully!");
        } else {
            // Fallback to DXVA2 (type 2)
            ret = g_pfn_hwdevice_create(&hw_ctx, 2 /* AV_HWDEVICE_TYPE_DXVA2 */, nullptr, nullptr, 0);
            if (ret >= 0 && hw_ctx) {
                g_hwDeviceType = 2;
                strncpy(g_gpuAdapterName, "DirectX Video Acceleration 2.0 (DXVA2)", sizeof(g_gpuAdapterName) - 1);
                LogVideo("[KONGOR_VIDEO] DXVA2 Hardware Device Context attached successfully!");
            } else {
                g_hwDeviceType = 0;
                strncpy(g_gpuAdapterName, "CPU Vectorized Software Decoder (AVX2/SSE4.2)", sizeof(g_gpuAdapterName) - 1);
                LogVideo("[KONGOR_VIDEO] Hardware acceleration unavailable, operating in vectorized software mode.");
            }
        }
    }

    if (g_real_open2) {
        int r = g_real_open2(avctx, codec, options);
        LogVideo("[KONGOR_VIDEO] avcodec_open2 returned code %d.", r);
        return r;
    }
    return -1;
}

__declspec(dllexport) int avcodec_receive_frame(void* avctx, void* frame) {
    if (!avctx || !frame) {
        return -22; // AVERROR(EINVAL)
    }

    EnsureCoreLoaded();

    // Check caller CVideo widget seeking state
    uintptr_t pCVideo = (uintptr_t)get_caller_rbx();
    bool isVerified = false;
    __try {
        if (pCVideo > 0x10000 && *(void**)(pCVideo + 0x1f8) == avctx) {
            isVerified = true;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        isVerified = false;
    }

    if (isVerified) {
        float seekTarget = -1.0f;
        float currentRot = *(float*)(pCVideo + 0x6c);
        if (currentRot > 0.0001f || currentRot < -1.5f) {
            seekTarget = currentRot;
            *(float*)(pCVideo + 0x6c) = 0.0f; // Reset rotation so 2D quad is NEVER visually tilted!
        }

        float externalSeek = g_pendingVideoSeekTarget.exchange(-1.0f);
        if (externalSeek >= 0.0f) {
            seekTarget = externalSeek;
        }

        if (seekTarget >= 0.0f) {
            // Reset rotation sentinel so seek executes once and quad is level (0 degrees)
            *(float*)(pCVideo + 0x6c) = 0.0f;

            void* pFormatCtx = *(void**)(pCVideo + 0x1d8);
            void* pStream = *(void**)(pCVideo + 0x1e0);
            int streamIndex = *(int*)(pCVideo + 0x1e8);
            double fps = *(double*)(pCVideo + 0x240);
            double duration = *(double*)(pCVideo + 0x238);

            if (duration > 0.0 && (double)seekTarget > duration) {
                seekTarget = (float)duration;
            }
            if (seekTarget < 0.0f) {
                seekTarget = 0.0f;
            }

            LogVideo("[KONGOR_VIDEO] Executing seek to %.2f s (stream=%d, fps=%.2f, duration=%.2f)...",
                seekTarget, streamIndex, fps, duration);

            int64_t target_ts = 0;
            if (pStream) {
                int num = *(int*)((const char*)pStream + 0x18);
                int den = *(int*)((const char*)pStream + 0x1c);
                if (num > 0 && den > 0) {
                    target_ts = (int64_t)((double)seekTarget * (double)den / (double)num);
                } else {
                    target_ts = (int64_t)((double)seekTarget * 1000000.0);
                }
            }

            if (!g_pfn_seek_file) {
                HMODULE hFmt = GetModuleHandleA("avformat-x64-58.dll");
                if (!hFmt) hFmt = LoadLibraryA("avformat-x64-58.dll");
                if (hFmt) {
                    g_pfn_seek_file = (PFN_avformat_seek_file)GetProcAddress(hFmt, "avformat_seek_file");
                }
            }
            if (!g_pfn_flush_buffers) {
                if (g_hRealCore) {
                    g_pfn_flush_buffers = (PFN_avcodec_flush_buffers)GetProcAddress(g_hRealCore, "avcodec_flush_buffers");
                }
            }

            if (g_pfn_seek_file && pFormatCtx) {
                int res = g_pfn_seek_file(pFormatCtx, streamIndex, _I64_MIN, target_ts, target_ts, 1 /* AVSEEK_FLAG_BACKWARD */);
                LogVideo("[KONGOR_VIDEO] avformat_seek_file returned code %d for ts %lld.", res, target_ts);
            }

            if (g_pfn_flush_buffers && avctx) {
                g_pfn_flush_buffers(avctx);
                LogVideo("[KONGOR_VIDEO] avcodec_flush_buffers completed.");
            }

            // Deferred frame-accurate synchronization: wait for the first decoded frame
            g_lastSeekCVideo.store(pCVideo);
            g_pendingAudioSync.store(true);
        }
    }

    if (!g_real_receive_frame) return -1;

    int ret = g_real_receive_frame(avctx, frame);
    if (ret == 0) {
        g_framesDecoded++;
        if (g_hwDeviceType > 0) {
            g_framesHwDecoded++;
        }

        // Check if we have a pending frame-accurate audio synchronization after a seek
        if (g_pendingAudioSync.exchange(false)) {
            uintptr_t pVideo = g_lastSeekCVideo.load();
            if (pVideo) {
                __try {
                    if (pVideo > 0x10000 && *(void**)(pVideo + 0x1f8) == avctx) {
                        void* pStream = *(void**)(pVideo + 0x1e0);
                        int num = 1, den = 600;
                        if (pStream) {
                            int sNum = *(int*)((const char*)pStream + 0x18);
                            int sDen = *(int*)((const char*)pStream + 0x1c);
                            if (sNum > 0 && sDen > 0) {
                                num = sNum;
                                den = sDen;
                            }
                        }

                        int64_t pts = *(int64_t*)((const char*)frame + 0x88);
                        if (pts == -0x8000000000000000LL || pts < 0) {
                            pts = *(int64_t*)((const char*)frame + 0x98); // best_effort_timestamp fallback
                        }

                        double actualSeconds = (pts >= 0) ? ((double)pts * (double)num / (double)den) : 0.0;
                        double fps = *(double*)(pVideo + 0x240);
                        double activeFps = (fps > 0.0) ? fps : 60.0;

                        // Synchronize CVideo internal clock and frame counter to the actual decoded frame
                        *(double*)(pVideo + 0x230) = actualSeconds;
                        *(int*)(pVideo + 0x228) = (int)(actualSeconds * activeFps);

                        // Synchronize audio channel 7 via fmodex64 to the exact millisecond of the decoded frame
                        unsigned int actualMs = (unsigned int)(actualSeconds * 1000.0);
                        HMODULE hFmod = GetModuleHandleA("fmodex64.dll");
                        if (hFmod) {
                            PFN_Kongor_Audio_Seek pfnAudioSeek = (PFN_Kongor_Audio_Seek)GetProcAddress(hFmod, "Kongor_Audio_Seek");
                            if (pfnAudioSeek) {
                                pfnAudioSeek(7, actualMs);
                                LogVideo("[KONGOR_VIDEO] Frame-accurate audio sync: frame pts=%lld (timebase %d/%d) -> actualSeconds=%.3f s (%u ms). Clock updated.",
                                    pts, num, den, actualSeconds, actualMs);
                            }
                        }
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {
                    LogVideo("[KONGOR_VIDEO][ERROR] Exception during frame-accurate audio sync handling!");
                }
            }
        }
    }
    return ret;
}

__declspec(dllexport) void Kongor_Video_Seek(float targetSeconds) {
    g_pendingVideoSeekTarget.store(targetSeconds);
    LogVideo("[KONGOR_VIDEO] External Kongor_Video_Seek queued for %.2f s.", targetSeconds);
}

__declspec(dllexport) void Kongor_Video_GetStatus(VideoTelemetry* outTelemetry) {
    if (!outTelemetry) return;
    outTelemetry->hwAccelEnabled = g_hwAccelEnabled;
    outTelemetry->hwDeviceType = g_hwDeviceType;
    outTelemetry->framesDecoded = g_framesDecoded;
    outTelemetry->framesHwDecoded = g_framesHwDecoded;
    outTelemetry->lastWidth = g_lastWidth;
    outTelemetry->lastHeight = g_lastHeight;
    outTelemetry->fps = 60.0f;
    strncpy(outTelemetry->gpuAdapterName, g_gpuAdapterName, sizeof(outTelemetry->gpuAdapterName) - 1);
    outTelemetry->gpuAdapterName[sizeof(outTelemetry->gpuAdapterName) - 1] = '\0';
}

__declspec(dllexport) void Kongor_Video_Configure(int32_t enableHwAccel, int32_t preferredType) {
    g_hwAccelEnabled = enableHwAccel ? 1 : 0;
    if (preferredType >= 0 && preferredType <= 2) {
        g_hwDeviceType = preferredType;
    }
}

} // extern "C"

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        LogVideo("[KONGOR_VIDEO] Initializing Phase 9 Hardware-Accelerated Video Proxy Shim...");
        EnsureCoreLoaded();
    }
    return TRUE;
}
