// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ViewportViewModel.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.ViewModels;

/// <summary>
///     ViewModel managing the 3D interactive Vulkan viewport, camera modality switching,
///     POV lock target, and dynamic lighting/shader preset tuning.
/// </summary>
public partial class ViewportViewModel : ObservableObject
{
    private readonly ILogger<ViewportViewModel> _logger;
    private readonly CameraDirector _cameraDirector;
    private readonly LightingController _lightingController;

    [ObservableProperty] private CameraMode _currentCameraMode = CameraMode.PlayerPov;
    [ObservableProperty] private float _fovDegrees = 55.0f;
    [ObservableProperty] private string _cameraPositionReadout = "X: 0.0, Y: -14.0, Z: 18.0";
    [ObservableProperty] private string _cameraHeadingReadout = "Heading: 90° | Pitch: -30°";
    [ObservableProperty] private int _lockedHeroSlot = 0;
    [ObservableProperty] private string _lockedHeroName = "Player 1";
    [ObservableProperty] private bool _isPovLocked = true;
    [ObservableProperty] private LightingPreset _selectedLightingPreset = LightingPreset.MiddaySun;
    [ObservableProperty] private float _sunAzimuth = 45.0f;
    [ObservableProperty] private float _sunElevation = 65.0f;
    [ObservableProperty] private float _ambientFactor = 0.40f;
    [ObservableProperty] private float _bloomThreshold = 1.20f;
    [ObservableProperty] private float _specularFactor = 0.85f;

    public CameraMode[] AvailableCameraModes => Enum.GetValues<CameraMode>();
    public LightingPreset[] AvailableLightingPresets => Enum.GetValues<LightingPreset>();

    public CameraDirector Director => _cameraDirector;
    public LightingController Lighting => _lightingController;

    public event Action<CameraMode>? CameraModeChanged;
    public event Action<LightingPreset>? LightingPresetChanged;

    public ViewportViewModel(CameraDirector cameraDirector, LightingController lightingController, ILogger<ViewportViewModel>? logger = null)
    {
        _cameraDirector = cameraDirector ?? throw new ArgumentNullException(nameof(cameraDirector));
        _lightingController = lightingController ?? throw new ArgumentNullException(nameof(lightingController));
        _logger = logger ?? NullLogger<ViewportViewModel>.Instance;

        _currentCameraMode = _cameraDirector.Mode;
        _fovDegrees = _cameraDirector.FovDegrees;
    }

    [RelayCommand]
    public void SetCameraMode(CameraMode mode)
    {
        _cameraDirector.Mode = mode;
        CurrentCameraMode = mode;
        _logger.LogInformation("[ViewportViewModel] Switched camera mode to '{Mode}'", mode);
        CameraModeChanged?.Invoke(mode);
    }

    [RelayCommand]
    public void SetLightingPreset(LightingPreset preset)
    {
        _lightingController.LoadPreset(preset);
        SelectedLightingPreset = preset;
        AmbientFactor = _lightingController.AmbientFactor;
        SpecularFactor = _lightingController.SpecularFactor;
        BloomThreshold = _lightingController.BloomThreshold;
        _logger.LogInformation("[ViewportViewModel] Switched lighting preset to '{Preset}'", preset);
        LightingPresetChanged?.Invoke(preset);
    }

    [RelayCommand]
    public void ResetCamera()
    {
        _cameraDirector.ResetPosition(new Vector3(0, -14, 18), Vector3.Zero);
        _logger.LogInformation("[ViewportViewModel] Reset camera to default origin");
    }

    [RelayCommand]
    public void FocusHero(int slot)
    {
        LockedHeroSlot = slot;
        IsPovLocked = (slot >= 0);
        _logger.LogInformation("[ViewportViewModel] Focused hero slot {Slot} (Locked: {Locked})", slot, IsPovLocked);
    }

    /// <summary>
    ///     Updates the reactive readouts for viewport coordinates and direction.
    /// </summary>
    public void UpdateReadouts(Vector3 eye, Vector3 target, float fov, float headingDeg, float pitchDeg)
    {
        CameraPositionReadout = $"X: {eye.X:F1}, Y: {eye.Y:F1}, Z: {eye.Z:F1}";
        CameraHeadingReadout = $"Heading: {headingDeg:F0}° | Pitch: {pitchDeg:F0}°";
        FovDegrees = fov;
    }
}
