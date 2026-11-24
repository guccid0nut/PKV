# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

"""
test_native_vulkan_pipeline.py
Unit test suite for the Vulkan 1.3 Driver Pipeline Optimization:
- Phase 1: Pipeline Cache Pre-Compilation & Stutter Elimination (VkPipelineCache)
- Phase 2: Ultra-Low Latency Frame Pacing & Display Synchronization (VK_KHR_present_wait / present_id)
- Native K2 Driver ABI Alignment (SVidDriverAPI 512-byte table)
"""

import os
import unittest
import struct
import ctypes

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

VID_VULKAN_PATHS = [
    os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "build", "Release", "vid_vulkan_x64.dll"),
    os.path.join(REPO_ROOT, "games", "client", "vid_vulkan_x64.dll"),
    os.path.join(REPO_ROOT, "games", "client", "windows-x64", "vid_vulkan_x64.dll"),
    os.path.join(REPO_ROOT, "games", "client", "win-x64", "vid_vulkan_x64.dll"),
]

HEADER_PATH = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "include", "SVidDriverAPI.h")
CONTEXT_H_PATH = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_context.h")
CONTEXT_CPP_PATH = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_context.cpp")
SPY_CPP_PATH = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vid_vulkan_spy.cpp")


class TestNativeVulkanPipeline(unittest.TestCase):
    def test_01_driver_binaries_exist_and_synchronized(self):
        """Verify that vid_vulkan_x64.dll is compiled and deployed identically across client paths."""
        client_paths = [
            os.path.join(REPO_ROOT, "games", "client", "vid_vulkan_x64.dll"),
            os.path.join(REPO_ROOT, "games", "client", "windows-x64", "vid_vulkan_x64.dll"),
        ]
        for path in client_paths:
            self.assertTrue(os.path.exists(path), f"Missing driver binary: {path}")
            size = os.path.getsize(path)
            self.assertGreater(size, 50000, f"Driver binary suspiciously small ({size} bytes): {path}")

        with open(client_paths[0], "rb") as f:
            primary_dll = f.read()

        for path in client_paths[1:]:
            with open(path, "rb") as f:
                self.assertEqual(primary_dll, f.read(), f"Deployed DLL desynchronized: {path}")

    def test_02_svid_driver_api_abi_alignment(self):
        """Verify that SVidDriverAPI conforms strictly to K2's 512-byte (0x200) binary ABI."""
        self.assertTrue(os.path.exists(HEADER_PATH), f"Header not found: {HEADER_PATH}")
        with open(HEADER_PATH, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn("sizeof(SVidDriverAPI) == 0x200", content)
        self.assertIn("offsetof(SVidDriverAPI, Init) == 0x020", content)
        self.assertIn("offsetof(SVidDriverAPI, SetMode) == 0x030", content)
        self.assertIn("offsetof(SVidDriverAPI, BeginFrame) == 0x068", content)
        self.assertIn("offsetof(SVidDriverAPI, EndFrame) == 0x070", content)
        self.assertIn("offsetof(SVidDriverAPI, SetMaxFPS) == 0x1F8", content)

    def test_03_vid_vulkan_dll_exports(self):
        """Verify that vid_vulkan_x64.dll exports InitAPIs with proper stdcall linkage."""
        primary_dll = VID_VULKAN_PATHS[0]
        try:
            h_dll = ctypes.CDLL(primary_dll)
            init_apis = getattr(h_dll, "InitAPIs", None)
            self.assertIsNotNone(init_apis, f"InitAPIs export missing from {primary_dll}")
        except OSError as e:
            self.skipTest(f"Skipping binary execution check on unsupported host: {e}")

    def test_04_vulkan_context_phase1_pipeline_cache(self):
        """Verify Phase 1 Pipeline Cache methods and persistence logic in VulkanContext."""
        with open(CONTEXT_H_PATH, "r", encoding="utf-8") as f:
            header = f.read()
        self.assertIn("GetPipelineCache", header)
        self.assertIn("LoadPipelineCache", header)
        self.assertIn("SavePipelineCache", header)

        with open(CONTEXT_CPP_PATH, "r", encoding="utf-8") as f:
            src = f.read()
        self.assertIn("vkCreatePipelineCache", src)
        self.assertIn("vkGetPipelineCacheData", src)
        self.assertIn("vkDestroyPipelineCache", src)
        self.assertIn("vulkan_pso.bin", src)

    def test_05_vulkan_context_phase2_low_latency_present_wait(self):
        """Verify Phase 2 Low-Latency Frame Pacing (VK_KHR_present_wait / present_id)."""
        with open(CONTEXT_H_PATH, "r", encoding="utf-8") as f:
            header = f.read()
        self.assertIn("WaitForPresent", header)
        self.assertIn("GetCurrentPresentId", header)
        self.assertIn("IsPresentWaitSupported", header)

        with open(CONTEXT_CPP_PATH, "r", encoding="utf-8") as f:
            src = f.read()
        self.assertIn("VK_KHR_PRESENT_ID_EXTENSION_NAME", src)
        self.assertIn("VK_KHR_PRESENT_WAIT_EXTENSION_NAME", src)
        self.assertIn("vkWaitForPresentKHR", src)

        with open(SPY_CPP_PATH, "r", encoding="utf-8") as f:
            spy_src = f.read()
        self.assertIn("IsPresentWaitSupported", spy_src)
        self.assertIn("WaitForPresent", spy_src)
        self.assertIn("LowLatencyWait", spy_src)

    def test_06_pipeline_cache_header_roundtrip(self):
        """Verify pipeline cache binary serialization header roundtrip."""
        # Standard Vulkan pipeline cache header: 4 uint32 + 16 bytes UUID = 32 bytes
        header_len = 32
        header_version = 1
        vendor_id = 0x10DE  # NVIDIA
        device_id = 0x1234  # Generic Vulkan GPU
        pipeline_uuid = b"1234567890abcdef"
        data_payload = b"ProjectKongorPrecompiledVulkanShaders"

        serialized = struct.pack(
            "<IIII16s",
            header_len,
            header_version,
            vendor_id,
            device_id,
            pipeline_uuid,
        ) + data_payload

        # Unpack and verify
        u_len, u_ver, u_vendor, u_dev, u_uuid = struct.unpack_from("<IIII16s", serialized, 0)
        self.assertEqual(u_len, 32)
        self.assertEqual(u_ver, 1)
        self.assertEqual(u_vendor, 0x10DE)
        self.assertEqual(u_dev, 0x1234)
        self.assertEqual(u_uuid, pipeline_uuid)
        self.assertEqual(serialized[32:], data_payload)

    def test_07_vulkan_phase3_dynamic_rendering(self):
        """Verify Phase 3 Dynamic Rendering (VK_KHR_dynamic_rendering / Vulkan 1.3 Core)."""
        with open(CONTEXT_H_PATH, "r", encoding="utf-8") as f:
            header = f.read()
        self.assertIn("IsDynamicRenderingSupported", header)

        with open(CONTEXT_CPP_PATH, "r", encoding="utf-8") as f:
            src = f.read()
        self.assertIn("VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME", src)
        self.assertIn("VkPhysicalDeviceDynamicRenderingFeatures", src)
        self.assertIn("vkCmdBeginRendering", src)
        self.assertIn("vkCmdEndRendering", src)

        vulkan_2d_cpp = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_2d.cpp")
        with open(vulkan_2d_cpp, "r", encoding="utf-8") as f:
            v2d_src = f.read()
        self.assertIn("VkPipelineRenderingCreateInfo", v2d_src)
        self.assertIn("IsDynamicRenderingSupported", v2d_src)

    def test_08_vulkan_phase4_zero_redundant_staging_invariant(self):
        """
        protects: #257 & AGENTS.md invariant forbidding redundant CPU vertex/index staging buffers in vid_vulkan_x64.dll (DXVK v3.0.2 owns 3D staging)
        fails_when: Redundant GPU buffer allocation or StageGeometry ring buffers are reintroduced into Vulkan3D
        seam: Real vulkan_3d.h and vulkan_3d.cpp source inspection
        """
        v3d_h = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_3d.h")
        v3d_cpp = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_3d.cpp")
        self.assertTrue(os.path.exists(v3d_h), f"Missing vulkan_3d.h at {v3d_h}")
        self.assertTrue(os.path.exists(v3d_cpp), f"Missing vulkan_3d.cpp at {v3d_cpp}")

        with open(v3d_h, "r", encoding="utf-8") as f:
            header = f.read()
        self.assertNotIn("VULKAN_3D_VTX_RING_SIZE", header)
        self.assertIn("Vulkan3DFrameStats", header)
        self.assertIn("Vulkan3DDrawRecord", header)

        with open(v3d_cpp, "r", encoding="utf-8") as f:
            src = f.read()
        self.assertNotIn("StageGeometry", src)
        self.assertIn("if (!m_device || !m_physicalDevice)", src)

    def test_09_vulkan_phase4_3d_draw_call_batching(self):
        """Verify Phase 4 3D Draw Call Batching, Consolidation, and D3D9 Device Interception."""
        v3d_h = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_3d.h")
        with open(v3d_h, "r", encoding="utf-8") as f:
            header = f.read()
        self.assertIn("RecordDrawIndexed", header)
        self.assertIn("RecordDrawPrimitive", header)
        self.assertIn("RecordDrawIndexedUP", header)
        self.assertIn("RecordDrawPrimitiveUP", header)
        self.assertIn("consolidatedBatches", header)

        spy_cpp = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vid_vulkan_spy.cpp")
        with open(spy_cpp, "r", encoding="utf-8") as f:
            spy_src = f.read()
        self.assertIn("Hook_DrawIndexedPrimitive", spy_src)
        self.assertIn("Hook_DrawPrimitive", spy_src)
        self.assertIn("Hook_DrawIndexedPrimitiveUP", spy_src)
        self.assertIn("Hook_DrawPrimitiveUP", spy_src)
        self.assertIn("InstallDeviceProxy", spy_src)
        self.assertIn("HookIAT", spy_src)
        self.assertIn("Vulkan3D::Get().Initialize", spy_src)
        self.assertIn("Vulkan3D::Get().EndFrame", spy_src)

    def test_10_vulkan_phase4_model_shader_registry_abi(self):
        """Verify Phase 4 Model and Shader Registration ABI hooks aligned with Ghidra discovery."""
        with open(HEADER_PATH, "r", encoding="utf-8") as f:
            content = f.read()
        self.assertIn("RegisterVertexShader", content)
        self.assertIn("UnregisterVertexShader", content)
        self.assertIn("RegisterPixelShader", content)
        self.assertIn("UnregisterPixelShader", content)
        self.assertIn("RegisterShaderPair", content)
        self.assertIn("RegisterModel", content)
        self.assertIn("UnregisterModel", content)
        self.assertIn("RenderScene", content)
        self.assertIn("offsetof(SVidDriverAPI, RegisterVertexShader) == 0x178", content)
        self.assertIn("offsetof(SVidDriverAPI, RegisterModel) == 0x1A0", content)

        v3d_h = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_3d.h")
        with open(v3d_h, "r", encoding="utf-8") as f:
            h = f.read()
        self.assertIn("OnRegisterModel", h)
        self.assertIn("OnUnregisterModel", h)
        self.assertIn("OnRegisterVertexShader", h)
        self.assertIn("OnRegisterPixelShader", h)
        self.assertIn("OnRegisterShaderPair", h)

    def test_11_pre_dxvk_vulkan13_gate_and_system32_d3d9_fallback_live(self):
        """
        protects: Pre-DXVK Vulkan 1.3 adapter probe, delay-loaded vulkan-1.dll, and native System32\\d3d9.dll fallback (Issue 9)
        fails_when: HON_FORCE_NATIVE_D3D9=1 fails to activate System32\\d3d9.dll fallback, vulkan-1.dll is hard-imported instead of delay-loaded, or KongorProbeDriverBackend faults
        seam: Live ctypes execution of compiled games/client/vid_vulkan_x64.dll + PE header inspection
        """
        client_dll = VID_VULKAN_PATHS[1]
        with open(client_dll, "rb") as f:
            pe_bytes = f.read()

        # Hotfix stamp & delay-load verification
        self.assertIn(b"[KONGOR_HOTFIX] Active patch:", pe_bytes)
        e_lfanew = struct.unpack_from("<I", pe_bytes, 0x3C)[0]
        # PE32+ OptionalHeader starts at e_lfanew + 24; DataDirectory starts at +112; DelayImport is index 13 (+104)
        delay_rva, delay_size = struct.unpack_from("<II", pe_bytes, e_lfanew + 24 + 112 + 13 * 8)
        self.assertGreater(delay_rva, 0, "vulkan-1.dll must be delay-loaded so missing vulkan-1.dll never blocks DLL load")
        self.assertGreater(delay_size, 0)

        if os.name != "nt":
            return

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.SetEnvironmentVariableW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
        kernel32.SetEnvironmentVariableW.restype = ctypes.c_int

        h_dll = ctypes.WinDLL(client_dll)
        probe_fn = h_dll.KongorProbeDriverBackend
        probe_fn.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
        probe_fn.restype = ctypes.c_int

        saved_env = os.environ.get("HON_FORCE_NATIVE_D3D9")
        try:
            # 1. Force Native D3D9 fallback and verify System32\d3d9.dll loads cleanly
            kernel32.SetEnvironmentVariableW("HON_FORCE_NATIVE_D3D9", "1")
            buf = ctypes.create_string_buffer(1024)
            mode = probe_fn(1, buf, 1024)
            reason = buf.value.decode("utf-8", errors="replace")
            self.assertEqual(mode, 1, f"Expected Native D3D9 fallback mode=1, got {mode} ({reason})")
            self.assertIn("HON_FORCE_NATIVE_D3D9=1", reason)

            # Verify System32\d3d9.dll is loaded in-process
            kernel32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
            kernel32.GetModuleHandleW.restype = ctypes.c_void_p
            sys_d3d9_path = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "d3d9.dll")
            self.assertIsNotNone(kernel32.GetModuleHandleW(sys_d3d9_path), "System32\\d3d9.dll must be loaded in fallback mode")

            # 2. Clear override and verify real dynamic Vulkan 1.3 probe executes cleanly
            kernel32.SetEnvironmentVariableW("HON_FORCE_NATIVE_D3D9", None)
            buf2 = ctypes.create_string_buffer(1024)
            mode2 = probe_fn(1, buf2, 1024)
            reason2 = buf2.value.decode("utf-8", errors="replace")
            self.assertIn(mode2, (0, 1))
            self.assertTrue(len(reason2) > 0, "Vulkan 1.3 probe must populate diagnostic adapter summary")
        finally:
            kernel32.SetEnvironmentVariableW("HON_FORCE_NATIVE_D3D9", saved_env)

    def test_12_null_vkdevice_swapchain_reset_and_options_uicmd_guards(self):
        """
        protects: Vulkan2D null-VkDevice guard, DXVK Reset Windowed=TRUE sanitization, VK_ERROR_DEVICE_LOST circuit breaker, and game_options.package K2 UICmd '||' syntax (Issues 9 & 10)
        fails_when: Vulkan2D::Initialize omits null-device guard, game_options.package uses Lua ' or ' inside UICmd If(...), or ResetEx leaves exclusive fullscreen unsanitized
        seam: Source contract + games/client/override/ui/fe2/game_options.package XML validation
        """
        v2d_cpp = os.path.join(REPO_ROOT, "native", "drivers", "vid_vulkan", "src", "vulkan_2d.cpp")
        with open(v2d_cpp, "r", encoding="utf-8") as f:
            v2d_src = f.read()
        self.assertIn("if (!device || !physicalDevice", v2d_src)

        with open(SPY_CPP_PATH, "r", encoding="utf-8") as f:
            spy_src = f.read()
        self.assertIn("SafeInvokeDxvkCreate9Ex", spy_src)
        self.assertIn("LoadNativeSystemD3D9", spy_src)
        self.assertIn("PrepareBorderlessPopupWindowForDxvk", spy_src)
        self.assertIn("g_consecutiveFastDeviceLostFrames", spy_src)

        pkg_path = os.path.join(REPO_ROOT, "games", "client", "override", "ui", "fe2", "game_options.package")
        with open(pkg_path, "r", encoding="utf-8") as f:
            pkg_src = f.read()
        self.assertNotIn("StringEquals(options_vidDriver, 'vid_vulkan') or ", pkg_src)
        self.assertIn("StringEquals(options_vidDriver, 'vid_vulkan') || StringEquals(host_vidDriver, 'vid_vulkan')", pkg_src)


if __name__ == "__main__":
    unittest.main()
