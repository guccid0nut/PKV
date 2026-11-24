<div align="center">

# 🌋 Vulkan Interop & Frame Receiver (`NEXUS.VulkanInterop`)
### *Shared-Memory Swapchain Frame Capture, Vulkan Device Discovery & glTF 2.0 Loader*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Vulkan 1.3](https://img.shields.io/badge/Vulkan-1.3-E74C3C?style=for-the-badge&logo=vulkan&logoColor=white)](https://www.vulkan.org/)
[![glTF 2.0](https://img.shields.io/badge/glTF%202.0-SharpGLTF-2ECC71?style=for-the-badge)](#)

**`NEXUS.VulkanInterop` provides managed Vulkan physical device discovery (`IVulkanDeviceContext`), shared-memory swapchain frame reception (`VulkanMemoryMappedReceiver`) from `VkLayer_hon_replay_capture.dll`, and glTF 2.0 `.glb` mesh/skeletal animation loading (`GlbModelLoader`, `SkeletalAnimationEngine`).**

[🌟 Key Features](#-key-features)

---

</div>

## 🌟 Key Features

* **📸 Shared-Memory Swapchain Frame Receiver (`VulkanMemoryMappedReceiver`)**: Consumes uncompressed triple-buffered BGRA8 frames from Win32 Named Shared Memory (`Local\HonReplayFrameBuffer_{pid}` / `Global\HonReplay_Frames_{pid}`) written by `VkLayer_hon_replay_capture.dll` with Win32 `EventWaitHandle` synchronization.
* **📊 Vulkan Physical Device Discovery (`IVulkanDeviceContext` / `VkbDeviceInfo`)**: Enumerates Vulkan physical devices, VRAM budgets, and driver versions via `Silk.NET.Vulkan` / `Vortice.Vulkan`.
* **🗿 glTF 2.0 Mesh & Skeletal Loader (`GlbModelLoader` & `SkeletalAnimationEngine`)**: Loads `.glb` 3D hero models, vertex buffers (`Vertex3D`), and bone animation poses (`SkeletalPose`) for replay and viewport rendering.
