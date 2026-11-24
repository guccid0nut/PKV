# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import unittest
import ctypes
import zlib
import os
import pefile

class TestNativeZlibLibdeflateProxy(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.proxy_path = os.path.abspath('games/client/zlibwapi_x64.dll')
        cls.real_path = os.path.abspath('games/client/zlibwapi_real_x64.dll')
        
        cls.dll_real = ctypes.CDLL(cls.real_path)
        cls.dll_proxy = ctypes.CDLL(cls.proxy_path)

    def test_01_exports_and_ordinals_parity(self):
        pe_orig = pefile.PE(self.real_path)
        pe_new = pefile.PE(self.proxy_path)

        orig_exports = {exp.ordinal: exp.name.decode() if exp.name else '' for exp in pe_orig.DIRECTORY_ENTRY_EXPORT.symbols}
        new_exports = {exp.ordinal: exp.name.decode() if exp.name else '' for exp in pe_new.DIRECTORY_ENTRY_EXPORT.symbols}

        self.assertEqual(len(orig_exports), 132, "Original DLL must have 132 exports")
        self.assertEqual(len(new_exports), 132, "Proxy DLL must have 132 exports")

        for ord_val, name in orig_exports.items():
            self.assertIn(ord_val, new_exports, f"Missing ordinal {ord_val} ({name})")
            self.assertEqual(new_exports[ord_val], name, f"Name mismatch at ordinal {ord_val}")

    def test_02_uncompress_zlib_stream_correctness(self):
        raw_payload = b"<entity name='Hero_WitchSlayer'><ability name='Ability1' level='4'/>" * 5000
        compressed = zlib.compress(raw_payload)

        out_buf = ctypes.create_string_buffer(len(raw_payload) + 512)
        out_len = ctypes.c_ulong(len(raw_payload) + 512)

        res = self.dll_proxy.uncompress(out_buf, ctypes.byref(out_len), compressed, len(compressed))
        self.assertEqual(res, 0, f"Proxy uncompress failed with code {res}")
        self.assertEqual(out_len.value, len(raw_payload))
        self.assertEqual(out_buf.raw[:out_len.value], raw_payload)

    def test_03_uncompress_raw_deflate_correctness(self):
        raw_payload = b"<particle_emitter rate='500' lifetime='1.5' speed='250'>" * 4000
        compressed_zlib = zlib.compress(raw_payload)
        compressed_raw = compressed_zlib[2:-4] # strip 2-byte header and 4-byte Adler-32

        out_buf = ctypes.create_string_buffer(len(raw_payload) + 512)
        out_len = ctypes.c_ulong(len(raw_payload) + 512)

        res = self.dll_proxy.uncompress(out_buf, ctypes.byref(out_len), compressed_raw, len(compressed_raw))
        self.assertEqual(res, 0, f"Proxy uncompress on raw deflate failed with code {res}")
        self.assertEqual(out_len.value, len(raw_payload))
        self.assertEqual(out_buf.raw[:out_len.value], raw_payload)

    def test_04_crc32_and_adler32_hardware_acceleration(self):
        test_payload = b"KONGOR_ESPORTS_DETERMINISTIC_CHECKSUM_TEST_STRING_1234567890" * 200

        crc_real = self.dll_real.crc32(0, test_payload, len(test_payload))
        crc_proxy = self.dll_proxy.crc32(0, test_payload, len(test_payload))
        self.assertEqual(crc_real, crc_proxy, f"CRC32 mismatch: {crc_real} vs {crc_proxy}")

        adler_real = self.dll_real.adler32(1, test_payload, len(test_payload))
        adler_proxy = self.dll_proxy.adler32(1, test_payload, len(test_payload))
        self.assertEqual(adler_real, adler_proxy, f"Adler32 mismatch: {adler_real} vs {adler_proxy}")

    def test_05_corrupt_data_graceful_handling(self):
        corrupt_data = b"\x78\x9C" + b"\xFF" * 128
        out_buf = ctypes.create_string_buffer(4096)
        out_len = ctypes.c_ulong(4096)

        res = self.dll_proxy.uncompress(out_buf, ctypes.byref(out_len), corrupt_data, len(corrupt_data))
        self.assertNotEqual(res, 0, "Corrupt data must not report success (Z_OK)")

if __name__ == '__main__':
    unittest.main()
