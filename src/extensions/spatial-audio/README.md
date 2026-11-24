<div align="center">

# 🎧 Spatial Audio Extension (`NEXUS.SpatialAudio`)
### *OpenAL Soft Binaural HRTF P/Invoke & Coordinate Bridge*

[![.NET 11.0](https://img.shields.io/badge/.NET-11.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![HRTF](https://img.shields.io/badge/OpenAL%20Soft-ALC__SOFT__HRTF-blue?style=for-the-badge)](#)

**`NEXUS.SpatialAudio` provides the managed C# P/Invoke wrapper (`OpenAlNative`) and coordinate-space bridge (`SpatialAudioEngine`) for OpenAL Soft (`openal32.dll`) and the `ALC_SOFT_HRTF` binaural extension.**

[🌟 Key Features](#-key-features)

---

</div>

## 🌟 Key Features

* **🎧 OpenAL Soft HRTF Context Initialization (`SpatialAudioEngine`)**: Opens `openal32.dll` (`alcOpenDevice`), negotiates `ALC_HRTF_SOFT` attributes, and queries runtime binaural HRTF status (`ALC_HRTF_STATUS_SOFT`).
* **🧭 K2-to-OpenAL Coordinate Space Transformation**: Converts K2 engine right-handed Z-up listener coordinates (`+X Right, +Y Forward, +Z Up`) into OpenAL Soft right-handed Y-up space (`+X Right, +Y Up, -Z Forward`) with `AL_INVERSE_DISTANCE_CLAMPED` attenuation.
* **🔗 Complements Native `fmod_audio_proxy`**: Works alongside the native C++ `fmodex64.dll` proxy (`native/fmod_audio_proxy/`) for client audio diagnostics and spatialization experiments.
