// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit and mutant negative-control verification suite for Phase 17 (#210-222):
/// OBS-Studio Capture, Vulkan &amp; Audio Gems (obsproject/obs-studio).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ObsStudioWave4CaptureTests
{
    /// <summary>
    /// #210 (Gem 3A):
    /// protects: 32-byte AVX-aligned 3-stage GPU readback to 2-slot shared-memory ping-pong without render-thread stalls or torn frames.
    /// fails_when: Align32 leaves unaligned strides, or reader observes torn/half-written slot data when writer updates alternating slots.
    /// seam: VulkanMemoryMappedFrameGrabber.Align32 + ObsAvx3StageTo2SlotPingPongBuffer.
    /// </summary>
    [Fact]
    public void Item210_Avx3StageTo2SlotShmemPingPong_Aligns32_And_PreventsTearing()
    {
        Assert.Equal(32, VulkanMemoryMappedFrameGrabber.Align32(1));
        Assert.Equal(32, VulkanMemoryMappedFrameGrabber.Align32(31));
        Assert.Equal(32, VulkanMemoryMappedFrameGrabber.Align32(32));
        Assert.Equal(64, VulkanMemoryMappedFrameGrabber.Align32(33));
        Assert.Equal(3686400L, VulkanMemoryMappedFrameGrabber.Align32(1280L * 720L * 4L));

        using ObsAvx3StageTo2SlotPingPongBuffer pipeline = new ObsAvx3StageTo2SlotPingPongBuffer(
            payloadSizeBytes: 100,
            startBackgroundCopyThread: false);

        Assert.Equal(100, pipeline.PayloadSizeBytes);
        Assert.Equal(128, pipeline.AlignedStrideBytes);
        Assert.Equal(0, pipeline.AlignedStrideBytes % 32);
        Assert.Equal(-1, pipeline.LastPublishedShmemSlot);

        // Negative control: invalid stage index or undersized payload rejected
        byte[] undersized = new byte[50];
        Assert.False(pipeline.SubmitStageBuffer(0, undersized, 1UL, pumpSynchronously: true));
        Assert.False(pipeline.SubmitStageBuffer(-1, new byte[100], 1UL, pumpSynchronously: true));
        Assert.False(pipeline.SubmitStageBuffer(3, new byte[100], 1UL, pumpSynchronously: true));

        // Submit 3 stages and verify ping-pong slot alternation (0 -> 1 -> 0)
        byte[] frame1 = new byte[100];
        Array.Fill(frame1, (byte)0xAA);
        Assert.True(pipeline.SubmitStageBuffer(0, frame1, 101UL, pumpSynchronously: true));
        Assert.Equal(0, pipeline.LastPublishedShmemSlot);

        byte[] readBuf = new byte[100];
        Assert.True(pipeline.TryReadLatestSlot(readBuf, out int slot1, out ulong fIdx1));
        Assert.Equal(0, slot1);
        Assert.Equal(101UL, fIdx1);
        Assert.All(readBuf, b => Assert.Equal(0xAA, b));

        byte[] frame2 = new byte[100];
        Array.Fill(frame2, (byte)0xBB);
        Assert.True(pipeline.SubmitStageBuffer(1, frame2, 102UL, pumpSynchronously: true));
        Assert.Equal(1, pipeline.LastPublishedShmemSlot);

        Assert.True(pipeline.TryReadLatestSlot(readBuf, out int slot2, out ulong fIdx2));
        Assert.Equal(1, slot2);
        Assert.Equal(102UL, fIdx2);
        Assert.All(readBuf, b => Assert.Equal(0xBB, b));

        byte[] frame3 = new byte[100];
        Array.Fill(frame3, (byte)0xCC);
        Assert.True(pipeline.SubmitStageBuffer(2, frame3, 103UL, pumpSynchronously: true));
        Assert.Equal(0, pipeline.LastPublishedShmemSlot);

        Assert.True(pipeline.TryReadLatestSlot(readBuf, out int slot3, out ulong fIdx3));
        Assert.Equal(0, slot3);
        Assert.Equal(103UL, fIdx3);
        Assert.All(readBuf, b => Assert.Equal(0xCC, b));
    }

    /// <summary>
    /// #211 (Gem 3B):
    /// protects: Vulkan Loader Dispatch Table pointer keying (GET_LDT(x) = *(void**)x) and thread-local QueuePresentKHR re-entrancy guard.
    /// fails_when: Wrapped VkQueue/VkCommandBuffer handles fail to resolve parent VkDevice, or recursive QueuePresentKHR enters infinite capture loop.
    /// seam: VulkanLdtDispatchAndReentrancyGuard + VulkanManageHandler ("ldt_reentrancy_diagnostics").
    /// </summary>
    [Fact]
    public unsafe void Item211_VulkanLdtKeying_And_PresentReentrancyGuard_BlocksRecursivePresent()
    {
        VulkanLdtDispatchAndReentrancyGuard guard = new VulkanLdtDispatchAndReentrancyGuard();

        // Simulate two distinct Vulkan dispatchable handles sharing the same Loader Dispatch Table pointer at offset 0
        nint sharedLdtPointer = unchecked((nint)0x7FFE_1234_5678L);
        nint* fakeVkDevice = stackalloc nint[2];
        fakeVkDevice[0] = sharedLdtPointer;
        fakeVkDevice[1] = (nint)0x1111;

        nint* fakeVkQueue = stackalloc nint[2];
        fakeVkQueue[0] = sharedLdtPointer;
        fakeVkQueue[1] = (nint)0x2222;

        nint* unrelatedQueue = stackalloc nint[2];
        unrelatedQueue[0] = unchecked((nint)0x7FFE_9999_9999L);
        unrelatedQueue[1] = (nint)0x3333;

        Assert.Equal(nint.Zero, VulkanLdtDispatchAndReentrancyGuard.GetLoaderDispatchTableKey(nint.Zero));
        Assert.True(guard.RegisterDeviceByLdt((nint)fakeVkDevice, "HoN_DXVK_Device0"));
        Assert.Equal(1, guard.RegisteredLdtCount);

        // Child VkQueue with distinct handle address resolves parent VkDevice via identical *(void**)x LDT key
        Assert.True(guard.TryLookupDeviceByChildHandle((nint)fakeVkQueue, out nint resolvedKey, out string? devName));
        Assert.Equal(sharedLdtPointer, resolvedKey);
        Assert.Equal("HoN_DXVK_Device0", devName);

        // Negative control: unrelated LDT key is rejected
        Assert.False(guard.TryLookupDeviceByChildHandle((nint)unrelatedQueue, out _, out _));

        // Per-swapchain-image non-blocking fence state
        Assert.True(guard.IsSwapchainImageFenceSignaled(0));
        guard.SetSwapchainImageFenceStatus(0, isSignaled: false);
        Assert.False(guard.IsSwapchainImageFenceSignaled(0));
        guard.SetSwapchainImageFenceStatus(0, isSignaled: true);
        Assert.True(guard.IsSwapchainImageFenceSignaled(0));

        // Thread-local QueuePresentKHR re-entrancy guard
        Assert.Equal(0, guard.CurrentThreadPresentingDepth);
        using (VulkanLdtDispatchAndReentrancyGuard.PresentReentrancyScope outer = guard.EnterQueuePresent())
        {
            Assert.True(outer.IsPrimaryEntry);
            Assert.Equal(1, outer.Depth);
            Assert.Equal(1, guard.CurrentThreadPresentingDepth);

            using (VulkanLdtDispatchAndReentrancyGuard.PresentReentrancyScope inner = guard.EnterQueuePresent())
            {
                Assert.False(inner.IsPrimaryEntry);
                Assert.Equal(2, inner.Depth);
                Assert.Equal(1L, guard.ReentrantPresentBlockedCount);
            }

            Assert.Equal(1, guard.CurrentThreadPresentingDepth);
        }

        Assert.Equal(0, guard.CurrentThreadPresentingDepth);
    }

    /// <summary>
    /// #212 (Gem 3K):
    /// protects: Keep-alive mutex watchdog (`Local\KongorCaptureKeepAlive_<PID>`) preventing orphaned GPU staging after host crash.
    /// fails_when: ProbeHostKeepAliveMutex returns true after KeepAliveMutexManager is disposed or for nonexistent PID.
    /// seam: KeepAliveMutexManager.
    /// </summary>
    [Fact]
    public void Item212_KeepAliveMutexWatchdog_DetectsActiveAndDisposedHostSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        int fakePid = 890000 + (Environment.ProcessId % 50000);
        Assert.False(KeepAliveMutexManager.ProbeHostKeepAliveMutex(fakePid));

        using (KeepAliveMutexManager manager = new KeepAliveMutexManager(fakePid))
        {
            Assert.True(manager.IsActive);
            Assert.Equal($"Local\\KongorCaptureKeepAlive_{fakePid}", manager.MutexName);
            Assert.True(KeepAliveMutexManager.ProbeHostKeepAliveMutex(fakePid));
        }

        // Once disposed (simulating host exit), OpenMutexW probe returns false so game layer disarms staging
        Assert.False(KeepAliveMutexManager.ProbeHostKeepAliveMutex(fakePid));

        // Verify VulkanMemoryMappedFrameGrabber.EnsureKeepAliveForProcess holds the mutex until grabber Dispose
        using (VulkanMemoryMappedFrameGrabber grabber = new VulkanMemoryMappedFrameGrabber())
        {
            Assert.True(grabber.EnsureKeepAliveForProcess(fakePid)!.IsActive);
            Assert.True(KeepAliveMutexManager.ProbeHostKeepAliveMutex(fakePid));
        }

        Assert.False(KeepAliveMutexManager.ProbeHostKeepAliveMutex(fakePid));
    }

    /// <summary>
    /// #213 (Gem 3M):
    /// protects: OBS `frame_ready(uint64_t interval_ns)` nanosecond frame-pacing drop-frame throttle and drift clamp.
    /// fails_when: High-FPS render loops (e.g. 300 FPS) are not throttled to target interval, or monotonic drift accumulates after a long stall.
    /// seam: ObsNanosecondFramePacer.
    /// </summary>
    [Fact]
    public void Item213_NanosecondFramePacer_ThrottlesSubIntervalFrames_And_ClampsDrift()
    {
        const ulong interval60FpsNs = 16_666_667UL; // ~16.67 ms
        ObsNanosecondFramePacer pacer = new ObsNanosecondFramePacer(interval60FpsNs);

        // First frame at t = 100ms is accepted and seeds lastFrameTimeNs
        Assert.True(pacer.FrameReady(100_000_000UL));
        Assert.Equal(100_000_000UL, pacer.LastFrameTimeNs);

        // Frame 3.3ms later (300 FPS tick) is dropped
        Assert.False(pacer.FrameReady(103_333_333UL));
        Assert.Equal(1UL, pacer.DroppedCount);

        // Frame at +17ms is accepted and advances lastFrameTimeNs by exactly 1 interval (116_666_667)
        Assert.True(pacer.FrameReady(117_000_000UL));
        Assert.Equal(100_000_000UL + interval60FpsNs, pacer.LastFrameTimeNs);

        // Frame after a 100ms stall (>= 2 * interval) clamps lastFrameTimeNs to current timestamp to prevent burst catch-up
        Assert.True(pacer.FrameReady(250_000_000UL));
        Assert.Equal(250_000_000UL, pacer.LastFrameTimeNs);
        Assert.Equal(3UL, pacer.AcceptedCount);

        // Live QPC 0-arg overload accepts initial sample
        ObsNanosecondFramePacer livePacer = new ObsNanosecondFramePacer(interval60FpsNs);
        Assert.True(livePacer.FrameReady());
    }

    /// <summary>
    /// #214 (Gem 3I):
    /// protects: Pre-resize / pre-recreation swapchain destruction notifier (`NotifySwapchainResizePreRelease`).
    /// fails_when: Shared-memory views and captured backbuffer references are not released before swapchain resize executes.
    /// seam: VulkanMemoryMappedFrameGrabber.NotifySwapchainResizePreRelease.
    /// </summary>
    [Fact]
    public void Item214_SwapchainResizePreReleaseNotifier_ReleasesViewsBeforeRecreate()
    {
        using VulkanMemoryMappedFrameGrabber grabber = new VulkanMemoryMappedFrameGrabber();
        bool viewReleasedBeforeResize = false;

        long count = grabber.NotifySwapchainResizePreRelease(4242, () => viewReleasedBeforeResize = true);
        Assert.Equal(1L, count);
        Assert.Equal(1L, grabber.SwapchainResizePreReleaseCount);
        Assert.True(viewReleasedBeforeResize);
    }

    /// <summary>
    /// #215 (Gem 3C):
    /// protects: 2-tier D3D9Ex struct byte flip (`is_d3d9ex`) and conditional-branch opcode patch escalation with RAII restore.
    /// fails_when: Tier 1 fails to restore original struct byte after creation, or Tier 2 fails to restore original branch opcodes.
    /// seam: WindowCapturer.TryEscalateD3D9ExSharedTexture.
    /// </summary>
    [Fact]
    public void Item215_D3D9ExByteFlip_And_OpcodeBranchPatch_RestoresOriginalMemoryInBothTiers()
    {
        byte[] deviceStruct = [0x10, 0x00, 0x30, 0x40];
        byte[] branchOpcodes = [0x74, 0x1A]; // JE rel8

        // Case 1: Tier 1 (struct byte flip at offset 1) succeeds and restores deviceStruct[1] back to 0x00
        byte observedDuringTier1 = 0;
        bool ok1 = WindowCapturer.TryEscalateD3D9ExSharedTexture(
            deviceStruct,
            isD3D9ExOffset: 1,
            branchOpcodes,
            (devSpan, _) =>
            {
                observedDuringTier1 = devSpan[1];
                return true;
            },
            out WindowCapturer.D3D9SharedTextureEscalationTier tier1);

        Assert.True(ok1);
        Assert.Equal(WindowCapturer.D3D9SharedTextureEscalationTier.ByteFlipD3D9Ex, tier1);
        Assert.Equal((byte)1, observedDuringTier1);
        Assert.Equal((byte)0x00, deviceStruct[1]); // Restored!

        // Case 2: Tier 1 rejected by driver -> escalates to Tier 2 (opcode patch 0x74 -> 0xEB) and restores 0x74 afterward
        byte observedOpcodeDuringTier2 = 0;
        bool ok2 = WindowCapturer.TryEscalateD3D9ExSharedTexture(
            deviceStruct,
            isD3D9ExOffset: 1,
            branchOpcodes,
            (_, opSpan) =>
            {
                if (opSpan[0] == 0x74)
                {
                    return false; // Force fallback from Tier 1 to Tier 2
                }
                observedOpcodeDuringTier2 = opSpan[0];
                return true;
            },
            out WindowCapturer.D3D9SharedTextureEscalationTier tier2);

        Assert.True(ok2);
        Assert.Equal(WindowCapturer.D3D9SharedTextureEscalationTier.OpcodeBranchPatch, tier2);
        Assert.Equal((byte)0xEB, observedOpcodeDuringTier2);
        Assert.Equal((byte)0x74, branchOpcodes[0]); // Restored!
        Assert.Equal((byte)0x00, deviceStruct[1]); // Restored!
    }

    /// <summary>
    /// #216 (Gem 3D):
    /// protects: 88-byte out-of-process graphics vtable offset cache and DLL version fingerprint invalidation.
    /// fails_when: OS update changes d3d9.dll/dxgi.dll/d3d11.dll version fingerprint or corrupts checksum without invalidating cache.
    /// seam: NativeHookService (GraphicsVtableOffsetCache + ValidateVtableOffsetCache + TryLoadValidatedGraphicsVtableOffsetCache).
    /// </summary>
    [Fact]
    public void Item216_OutOfProcessVtableOffsetFingerprintCache_Validates88ByteLayout_And_RejectsStaleDlls()
    {
        Assert.Equal(16, Marshal.SizeOf<GraphicsModuleVersionFingerprint>());
        Assert.Equal(88, Marshal.SizeOf<GraphicsVtableOffsetCache>());
        Assert.Equal((nint)0, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.Magic)));
        Assert.Equal((nint)4, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.Version)));
        Assert.Equal((nint)8, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.D3d9)));
        Assert.Equal((nint)24, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.Dxgi)));
        Assert.Equal((nint)40, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.D3d11)));
        Assert.Equal((nint)56, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.D3d9PresentOffset)));
        Assert.Equal((nint)84, Marshal.OffsetOf<GraphicsVtableOffsetCache>(nameof(GraphicsVtableOffsetCache.ChecksumFnv1a32)));

        GraphicsModuleVersionFingerprint d3d9Fp = new GraphicsModuleVersionFingerprint
        {
            FileVersionMs = 0x000A0000U,
            FileVersionLs = 0x4A610100U,
            ImageSizeBytes = 0x001B0000U,
            PeTimeDateStamp = 0x65001122U
        };
        GraphicsModuleVersionFingerprint dxgiFp = new GraphicsModuleVersionFingerprint
        {
            FileVersionMs = 0x000A0000U,
            FileVersionLs = 0x4A610200U,
            ImageSizeBytes = 0x000F4000U,
            PeTimeDateStamp = 0x65003344U
        };
        GraphicsModuleVersionFingerprint d3d11Fp = new GraphicsModuleVersionFingerprint
        {
            FileVersionMs = 0x000A0000U,
            FileVersionLs = 0x4A610300U,
            ImageSizeBytes = 0x00280000U,
            PeTimeDateStamp = 0x65005566U
        };

        GraphicsVtableOffsetCache cache = new GraphicsVtableOffsetCache
        {
            D3d9 = d3d9Fp,
            Dxgi = dxgiFp,
            D3d11 = d3d11Fp,
            D3d9PresentOffset = 17U * 8U,
            D3d9PresentExOffset = 121U * 8U,
            D3d9SwapPresentOffset = 3U * 8U,
            D3d9ResetOffset = 16U * 8U,
            D3d9ResetExOffset = 132U * 8U,
            DxgiPresentOffset = 8U * 8U,
            DxgiResizeBuffersOffset = 13U * 8U
        };

        NativeHookService service = new NativeHookService(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"kongor_vtbl_test_{Guid.NewGuid():N}.bin");
        try
        {
            Assert.True(service.SaveGraphicsVtableOffsetCache(tempFile, ref cache));
            Assert.True(service.TryLoadValidatedGraphicsVtableOffsetCache(
                tempFile,
                in d3d9Fp,
                in dxgiFp,
                in d3d11Fp,
                out GraphicsVtableOffsetCache loaded,
                out string? err));
            Assert.Null(err);
            Assert.Equal(17U * 8U, loaded.D3d9PresentOffset);

            // Mutant 1: OS update changes dxgi.dll build number -> cache MUST invalidate
            GraphicsModuleVersionFingerprint updatedDxgi = dxgiFp;
            updatedDxgi.FileVersionLs += 1U;
            Assert.False(service.TryLoadValidatedGraphicsVtableOffsetCache(
                tempFile,
                in d3d9Fp,
                in updatedDxgi,
                in d3d11Fp,
                out _,
                out string? dxgiErr));
            Assert.Contains("dxgi.dll", dxgiErr ?? string.Empty);

            // Mutant 2: Unaligned Present offset -> cache MUST invalidate
            GraphicsVtableOffsetCache unaligned = loaded;
            unaligned.D3d9PresentOffset = 135U; // Not 4-byte aligned
            unaligned.ChecksumFnv1a32 = NativeHookService.ComputeVtableCacheChecksum(in unaligned);
            Assert.False(NativeHookService.ValidateVtableOffsetCache(
                in unaligned,
                in d3d9Fp,
                in dxgiFp,
                in d3d11Fp,
                out string? alignErr));
            Assert.Contains("aligned", alignErr ?? string.Empty);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// #217 (Gem 3E):
    /// protects: WGC `DWMWA_EXTENDED_FRAME_BOUNDS` border-free client sub-box calculation and texture clamping.
    /// fails_when: Titlebar/Aero shadow margins are included in client capture or sub-box exceeds WGC frame pool texture dimensions.
    /// seam: WindowCapturer.ComputeBorderFreeWgcClientSubBox.
    /// </summary>
    [Fact]
    public void Item217_WgcExtendedFrameBounds_ComputesBorderFreeClientSubBox_And_ClampsToTexture()
    {
        // Window with 8px left border and 31px titlebar inside DWM extended frame bounds (100, 100)..(1396, 859)
        WindowCapturer.RECT extBounds = new WindowCapturer.RECT { Left = 100, Top = 100, Right = 1396, Bottom = 859 };
        WindowCapturer.RECT clientRect = new WindowCapturer.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
        WindowCapturer.POINT clientScreenOrigin = new WindowCapturer.POINT { X = 108, Y = 131 };

        WindowCapturer.WgcClientSubBox box = WindowCapturer.ComputeBorderFreeWgcClientSubBox(
            extBounds,
            clientRect,
            clientScreenOrigin,
            wgcTextureWidth: 1296,
            wgcTextureHeight: 759);

        Assert.Equal(8, box.Left);
        Assert.Equal(31, box.Top);
        Assert.Equal(1288, box.Right);
        Assert.Equal(751, box.Bottom);
        Assert.Equal(1280, box.Width);
        Assert.Equal(720, box.Height);
        Assert.False(box.ClampedToTexture);

        // Negative control: resized/smaller WGC texture clamps right/bottom to prevent CopySubresourceRegion out-of-bounds crash
        WindowCapturer.WgcClientSubBox clamped = WindowCapturer.ComputeBorderFreeWgcClientSubBox(
            extBounds,
            clientRect,
            clientScreenOrigin,
            wgcTextureWidth: 1200,
            wgcTextureHeight: 700);

        Assert.True(clamped.ClampedToTexture);
        Assert.Equal(1200, clamped.Right);
        Assert.Equal(700, clamped.Bottom);
    }

    /// <summary>
    /// #218 (Gem 3G):
    /// protects: Penalty-scored window rating heuristic and `DWMWA_CLOAKED=14` filter in WindowCapturer and ClientProcessRegistry.
    /// fails_when: Cloaked UWP shells, ConsoleWindowClass, or dummy helper windows outscore the genuine K2_Direct3D game window.
    /// seam: WindowCapturer.ComputeObsWindowRating + ClientProcessRegistry.SelectBestClientWindowCandidate.
    /// </summary>
    [Fact]
    public void Item218_PenaltyScoredWindowRating_RejectsCloakedAndDummyWindows_And_SelectsK2RenderWindow()
    {
        Assert.Equal(14, WindowCapturer.DWMWA_CLOAKED);

        ClientProcessRegistry registry = new ClientProcessRegistry(_ => { });
        List<ClientProcessRegistry.WindowCandidateDescriptor> candidates = new List<ClientProcessRegistry.WindowCandidateDescriptor>
        {
            new ClientProcessRegistry.WindowCandidateDescriptor(
                WindowHandle: (IntPtr)0x1001,
                ExecutableName: "hon_x64.exe",
                ClassName: "ConsoleWindowClass",
                WindowTitle: "HoN Dedicated Console",
                IsCloaked: false),
            new ClientProcessRegistry.WindowCandidateDescriptor(
                WindowHandle: (IntPtr)0x1002,
                ExecutableName: "hon_x64.exe",
                ClassName: "K2_Direct3D",
                WindowTitle: "Heroes of Newerth",
                IsCloaked: true), // Cloaked on inactive virtual desktop -> must be rejected!
            new ClientProcessRegistry.WindowCandidateDescriptor(
                WindowHandle: (IntPtr)0x1003,
                ExecutableName: "hon_x64.exe",
                ClassName: "DummyDWMListenerWindow",
                WindowTitle: "Heroes of Newerth",
                IsCloaked: false),
            new ClientProcessRegistry.WindowCandidateDescriptor(
                WindowHandle: (IntPtr)0x1004,
                ExecutableName: "hon_x64.exe",
                ClassName: "K2_Direct3D",
                WindowTitle: "Heroes of Newerth",
                IsCloaked: false)
        };

        ClientProcessRegistry.WindowCandidateDescriptor? winner = registry.SelectBestClientWindowCandidate(
            candidates,
            expectedExe: "hon_x64.exe",
            expectedClass: "K2_Direct3D",
            expectedTitle: "Heroes of Newerth",
            out int bestScore);

        Assert.NotNull(winner);
        Assert.Equal((IntPtr)0x1004, winner!.WindowHandle);
        Assert.True(bestScore >= 1400);
    }

    /// <summary>
    /// #219 (Gem 3L):
    /// protects: Hardware cursor HICON mask cache and 1-bpp AND/XOR monochrome inversion + alpha blending onto BGRA captures.
    /// fails_when: Monochrome AND=1/XOR=1 I-beam pixels fail to invert background RGB, or hotspot offset is misapplied.
    /// seam: HardwareCursorIconMaskCacheBlender.
    /// </summary>
    [Fact]
    public void Item219_HardwareCursorIconMaskCacheBlender_DecodesAndXorMasks_And_BlendsWithHotspot()
    {
        HardwareCursorIconMaskCacheBlender blender = new HardwareCursorIconMaskCacheBlender(maxCacheEntries: 2);

        // 4x1 monochrome cursor:
        // pixel 0: AND=0, XOR=0 -> opaque black
        // pixel 1: AND=0, XOR=1 -> opaque white
        // pixel 2: AND=1, XOR=0 -> transparent
        // pixel 3: AND=1, XOR=1 -> inverted background pixel
        // Binary bits for top 4 bits of byte:
        // AND: 0, 0, 1, 1 -> 0b0011_0000 = 0x30
        // XOR: 0, 1, 0, 1 -> 0b0101_0000 = 0x50
        byte[] andBits = [0x30];
        byte[] xorBits = [0x50];

        HardwareCursorIconMaskCacheBlender.CachedCursorBitmap cursor = blender.GetOrAddCursorBitmap(
            (nint)0xABCD,
            h => HardwareCursorIconMaskCacheBlender.DecodeMonochromeAndXorCursorMasks(h, 4, 1, hotspotX: 1, hotspotY: 0, andBits, xorBits));

        // Second lookup hits MRU cache
        _ = blender.GetOrAddCursorBitmap(
            (nint)0xABCD,
            _ => throw new InvalidOperationException("Should hit cursor cache"));
        Assert.Equal(1L, blender.CacheHits);
        Assert.Equal(1L, blender.CacheMisses);

        // 6x1 BGRA frame initialized to (B=10, G=20, R=30, A=255)
        byte[] frame = new byte[6 * 4];
        for (int i = 0; i < 6; i++)
        {
            frame[(i * 4) + 0] = 10;
            frame[(i * 4) + 1] = 20;
            frame[(i * 4) + 2] = 30;
            frame[(i * 4) + 3] = 255;
        }

        // Cursor at screen X=102, client origin X=100, hotspotX=1 -> draw starts at dstX = 102 - 100 - 1 = 1
        int blended = HardwareCursorIconMaskCacheBlender.BlendCursorOntoBgraFrame(
            frame,
            frameWidth: 6,
            frameHeight: 1,
            cursor,
            cursorScreenX: 102,
            cursorScreenY: 50,
            clientScreenOriginX: 100,
            clientScreenOriginY: 50);

        Assert.Equal(3, blended); // 3 visible/inverted pixels (pixel 2 is transparent)
        // dstX=1 (cursor px 0): opaque black
        Assert.Equal(0, frame[(1 * 4) + 0]);
        Assert.Equal(0, frame[(1 * 4) + 1]);
        Assert.Equal(0, frame[(1 * 4) + 2]);
        // dstX=2 (cursor px 1): opaque white
        Assert.Equal(255, frame[(2 * 4) + 0]);
        Assert.Equal(255, frame[(2 * 4) + 1]);
        Assert.Equal(255, frame[(2 * 4) + 2]);
        // dstX=3 (cursor px 2): transparent (unchanged: 10, 20, 30)
        Assert.Equal(10, frame[(3 * 4) + 0]);
        Assert.Equal(20, frame[(3 * 4) + 1]);
        Assert.Equal(30, frame[(3 * 4) + 2]);
        // dstX=4 (cursor px 3): inverted background (255-10=245, 255-20=235, 255-30=225)
        Assert.Equal(245, frame[(4 * 4) + 0]);
        Assert.Equal(235, frame[(4 * 4) + 1]);
        Assert.Equal(225, frame[(4 * 4) + 2]);

        // Verify 32-bpp color cursor DIB decoding (DecodeColorBgraCursorDib) and alpha blending
        byte[] colorDib = [200, 100, 50, 255];
        HardwareCursorIconMaskCacheBlender.CachedCursorBitmap colorCursor =
            HardwareCursorIconMaskCacheBlender.DecodeColorBgraCursorDib((nint)0xDCBA, 1, 1, 0, 0, colorDib);
        Assert.Equal((byte)0, colorCursor.InvertMask[0]);
        int colorBlended = HardwareCursorIconMaskCacheBlender.BlendCursorOntoBgraFrame(
            frame,
            frameWidth: 6,
            frameHeight: 1,
            colorCursor,
            cursorScreenX: 100,
            cursorScreenY: 50,
            clientScreenOriginX: 100,
            clientScreenOriginY: 50);
        Assert.Equal(1, colorBlended);
        Assert.Equal(200, frame[0]);
        Assert.Equal(100, frame[1]);
        Assert.Equal(50, frame[2]);
    }

    /// <summary>
    /// #220 (Gem 3F):
    /// protects: Anti-cheat safe `WH_GETMESSAGE` (`3`) hook injection wakeup loop (`WM_USER + 432`) and OBS 64-bit rolling-XOR cipher.
    /// fails_when: Rolling-XOR 4-bit left key rotation fails round-trip deobfuscation or leaves plaintext ASCII symbol in obfuscated buffer.
    /// seam: NativeProcessInjectionService (ObfuscateObsRollingXorString / DeobfuscateObsRollingXorString / PlanWhGetMessageSafeInjection).
    /// </summary>
    [Fact]
    public void Item220_SafeWhGetMessage_And_RollingXorInjector_RoundTripsSymbols_And_DrivesWakeup()
    {
        NativeProcessInjectionService.WhGetMessageInjectionPlan plan =
            NativeProcessInjectionService.PlanWhGetMessageSafeInjection(targetThreadId: 7788U, maxWakeupAttempts: 25);

        Assert.True(plan.AntiCheatSafeMode);
        Assert.Equal(3, plan.HookId);
        Assert.Equal(0x0400U + 432U, plan.WakeupMessageId);
        Assert.Equal(7788U, plan.TargetThreadId);
        Assert.Equal(25, plan.MaxWakeupAttempts);

        // Obfuscated bytes must not match plaintext ASCII
        byte[] plainBytes = Encoding.ASCII.GetBytes("SetWindowsHookExA");
        Assert.NotEqual(plainBytes, plan.ObfuscatedSetWindowsHookExA);

        // Round-trip deobfuscation with same key recovers exact symbol names
        Assert.Equal("SetWindowsHookExA", NativeProcessInjectionService.DeobfuscateObsRollingXorString(plan.ObfuscatedSetWindowsHookExA));
        Assert.Equal("PostThreadMessageW", NativeProcessInjectionService.DeobfuscateObsRollingXorString(plan.ObfuscatedPostThreadMessageW));
        Assert.Equal("UnhookWindowsHookEx", NativeProcessInjectionService.DeobfuscateObsRollingXorString(plan.ObfuscatedUnhookWindowsHookEx));

        // Negative control: wrong rolling-XOR key produces garbage
        Assert.NotEqual(
            "SetWindowsHookExA",
            NativeProcessInjectionService.DeobfuscateObsRollingXorString(plan.ObfuscatedSetWindowsHookExA, initialKey: 0xDEAD_BEEF_CAFE_BABEUL));

        // Verify PostThreadMessageW wakeup loop dispatches WM_USER + 432
        int postedCalls = 0;
        int delivered = NativeProcessInjectionService.ExecuteWhGetMessageWakeupLoop(
            targetThreadId: 7788U,
            hookHandle: (nint)0x55AA,
            maxAttempts: 4,
            postThreadMessageOverride: (tid, msg, _, lParam) =>
            {
                Assert.Equal(7788U, tid);
                Assert.Equal(NativeProcessInjectionService.WM_OBS_HOOK_WAKEUP, msg);
                Assert.Equal((nint)0x55AA, lParam);
                postedCalls++;
                return true;
            });

        Assert.Equal(4, delivered);
        Assert.Equal(4, postedCalls);

        // Verify PlanOrExecuteWhGetMessageInjection dry-run mode
        NativeProcessInjectionService.WhGetMessageInjectionResult dryResult =
            NativeProcessInjectionService.PlanOrExecuteWhGetMessageInjection(
                targetThreadId: 7788U,
                executeLiveHook: false);
        Assert.Equal(3, dryResult.Plan.HookId);
        Assert.Equal(0, dryResult.WakeupMessagesDelivered);
    }

    /// <summary>
    /// #221 (Gem 3J):
    /// protects: WASAPI `VAD\Process_Loopback` 12-byte `AUDIOCLIENT_ACTIVATION_PARAMS` binary activation blob builder.
    /// fails_when: PID 0 is accepted, or 12-byte little-endian layout drifts from Windows 10 2004+ `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`.
    /// seam: AudioStudioService.BuildProcessIsolatedLoopbackActivationBlob.
    /// </summary>
    [Fact]
    public void Item221_WasapiProcessIsolatedLoopbackAudio_Builds12ByteActivationBlob_And_RejectsZeroPid()
    {
        Assert.Equal("VAD\\Process_Loopback", AudioStudioService.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK);
        Assert.Equal(0U, AudioStudioService.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);
        Assert.Equal(1U, AudioStudioService.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE);
        Assert.Equal(12, Marshal.SizeOf<AudioStudioService.AudioClientActivationParamsBlob>());

        // Negative control: PID 0 must be rejected
        Assert.False(AudioStudioService.BuildProcessIsolatedLoopbackParams(
            0U,
            includeTargetProcessTree: true,
            out byte[] emptyBlob,
            out _));
        Assert.Empty(emptyBlob);

        // Positive control: PID 13579 in IncludeTargetProcessTree mode
        Assert.True(AudioStudioService.BuildProcessIsolatedLoopbackParams(
            13579U,
            includeTargetProcessTree: true,
            out byte[] blob,
            out AudioStudioService.AudioClientActivationParamsBlob structured));

        Assert.Equal(12, blob.Length);
        Assert.Equal(1U, structured.ActivationType);
        Assert.Equal(13579U, structured.ProcessLoopbackParams.TargetProcessId);
        Assert.Equal(AudioStudioService.ProcessLoopbackMode.IncludeTargetProcessTree, structured.ProcessLoopbackParams.ProcessLoopbackMode);

        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4)));
        Assert.Equal(13579U, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8, 4)));
    }

    /// <summary>
    /// #222 (Gem 3H):
    /// protects: Hierarchical QPC microsecond call-tree profiler (`KongorQpcCallTreeProfiler`) with 1.0ms (1000us) UI budget and 5.0ms (5000us) hitch counters.
    /// fails_when: Nested parent/child scope linkage, p99 calculation, or >1000us / >5000us violation counts are inaccurate.
    /// seam: KongorQpcCallTreeProfiler.
    /// </summary>
    [Fact]
    public void Item222_QpcMicrosecondCallTreeProfiler_TracksHierarchyP99_And_1MsBudgetViolations()
    {
        KongorQpcCallTreeProfiler profiler = new KongorQpcCallTreeProfiler();

        profiler.BeginScope("CInterface::Frame", budgetUs: 1000L);
        profiler.BeginScope("Lua_ScoreboardTick", budgetUs: 1000L);
        profiler.EndScope(overrideElapsedUs: 450L);
        profiler.EndScope(overrideElapsedUs: 820L);

        // Record an over-budget sample (1400 us > 1000 us) and a hitch sample (6200 us > 5000 us)
        profiler.RecordScopeSample("Lua_ScoreboardTick", "CInterface::Frame", elapsedUs: 1400L, budgetUs: 1000L);
        profiler.RecordScopeSample("Lua_ScoreboardTick", "CInterface::Frame", elapsedUs: 6200L, budgetUs: 1000L);

        IReadOnlyList<QpcCallTreeScopeSummary> snapshots = profiler.GetSnapshots();
        Assert.Equal(2, snapshots.Count);

        QpcCallTreeScopeSummary? child = null;
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].ScopeName == "Lua_ScoreboardTick")
            {
                child = snapshots[i];
                break;
            }
        }

        Assert.NotNull(child);
        Assert.Equal("CInterface::Frame", child!.ParentScopeName);
        Assert.Equal(3L, child.CallCount);
        Assert.Equal(450L, child.MinUs);
        Assert.Equal(6200L, child.MaxUs);
        Assert.Equal(6200L, child.P99Us);
        Assert.Equal(2L, child.OverbudgetCount);
        Assert.Equal(1L, child.Hitch5MsCount);

        string csv = profiler.ExportCsvSnapshot();
        Assert.Contains("Lua_ScoreboardTick,CInterface::Frame,3,450,6200", csv);

        string json = profiler.ExportJsonSnapshot();
        Assert.Contains("\"ScopeName\":\"Lua_ScoreboardTick\"", json);
    }
}
