# `vid_d3d12` Experimental Quarantine Notice

> **Status:** `[EXPERIMENTAL - QUARANTINED]`
> **Production Renderer:** `native/drivers/vid_vulkan/` (`vid_vulkan_x64.dll` + DXVK WSI)

`vid_d3d12` (`vid_d3d12_x64.dll` / `D3D9On12`) is an incomplete research prototype and is **disabled by default** in WILLOWMAKER so players cannot accidentally launch into an unimplemented DirectX 12 swapchain path.

To opt in during isolated driver research or `OBL-03` verification only, set:

```powershell
$env:KONGOR_EXPERIMENTAL_D3D12 = "1"
```

Without `KONGOR_EXPERIMENTAL_D3D12=1`, WILLOWMAKER automatically falls back to `DXVK (Vulkan WSI)` even if a legacy config file contains `D3D9On12`.
