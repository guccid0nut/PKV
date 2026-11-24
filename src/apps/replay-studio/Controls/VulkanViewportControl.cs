// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanViewportControl.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Controls;

/// <summary>
///     High-performance Avalonia custom Control hosting a <see cref="WriteableBitmap"/> with a
///     zero-allocation unmanaged staging ring (<see cref="NativeBufferRing"/>) render loop and
///     multi-perspective camera input handling.
/// </summary>
public sealed class VulkanViewportControl : Control, IDisposable
{
    private readonly ILogger<VulkanViewportControl> _logger;
    private readonly Lock _bitmapLock = new();

    private WriteableBitmap? _writeableBitmap;
    private NativeBufferRing? _bufferRing;
    private CameraDirector? _cameraDirector;

    private Point _lastPointerPosition;
    private bool _isLeftPressed;
    private bool _isRightPressed;
    private bool _isMiddlePressed;

    // Keyboard state tracking
    private bool _isWPressed;
    private bool _isAPressed;
    private bool _isSPressed;
    private bool _isDPressed;
    private bool _isSpacePressed;
    private bool _isCtrlPressed;
    private bool _isShiftPressed;

    private int _viewportWidth = 1280;
    private int _viewportHeight = 720;
    private bool _isDisposed;

    public NativeBufferRing? BufferRing => _bufferRing;
    public CameraDirector? CameraDirector
    {
        get => _cameraDirector;
        set => _cameraDirector = value;
    }

    public int ViewportWidth => _viewportWidth;
    public int ViewportHeight => _viewportHeight;

    public VulkanViewportControl() : this(null)
    {
    }

    public VulkanViewportControl(ILogger<VulkanViewportControl>? logger)
    {
        _logger = logger ?? NullLogger<VulkanViewportControl>.Instance;
        Focusable = true;
        ClipToBounds = true;

        InitializeBuffers(_viewportWidth, _viewportHeight);
        _logger.LogInformation("[VulkanViewportControl] Initialized viewport control at {Width}x{Height}", _viewportWidth, _viewportHeight);
    }

    public void AttachPipeline(NativeBufferRing bufferRing, CameraDirector cameraDirector)
    {
        _bufferRing = bufferRing;
        _cameraDirector = cameraDirector;
        _logger.LogInformation("[VulkanViewportControl] Attached pipeline components (BufferRing, CameraDirector)");
    }

    private void InitializeBuffers(int width, int height)
    {
        lock (_bitmapLock)
        {
            _writeableBitmap?.Dispose();
            _writeableBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Premul);

            if (_bufferRing == null)
            {
                _bufferRing = new NativeBufferRing(width, height);
            }
            else
            {
                _bufferRing.Resize(width, height);
            }

            _viewportWidth = width;
            _viewportHeight = height;
        }
    }

    /// <summary>
    ///     Performs zero-allocation blit from unmanaged NativeBufferRing front buffer
    ///     into the locked Avalonia WriteableBitmap framebuffer and triggers visual invalidation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlitFrameAndInvalidate()
    {
        if (_isDisposed || _bufferRing == null || _writeableBitmap == null)
        {
            return;
        }

        lock (_bitmapLock)
        {
            if (_isDisposed || _bufferRing == null || _writeableBitmap == null)
            {
                return;
            }

            using (ILockedFramebuffer fb = _writeableBitmap.Lock())
            {
                _bufferRing.BlitFrontBuffer(fb);
            }
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_isDisposed || _writeableBitmap == null)
        {
            return;
        }

        lock (_bitmapLock)
        {
            if (_writeableBitmap != null)
            {
                Rect destRect = new(0, 0, Bounds.Width, Bounds.Height);
                context.DrawImage(_writeableBitmap, destRect);
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        int newWidth = Math.Max(16, (int)Bounds.Width);
        int newHeight = Math.Max(16, (int)Bounds.Height);

        if (newWidth != _viewportWidth || newHeight != _viewportHeight)
        {
            _logger.LogInformation("[VulkanViewportControl] Viewport resized from {OldW}x{OldH} to {NewW}x{NewH}", _viewportWidth, _viewportHeight, newWidth, newHeight);
            InitializeBuffers(newWidth, newHeight);
        }
    }

    #region Pointer Input Handling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        _lastPointerPosition = e.GetPosition(this);

        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        _isLeftPressed = props.IsLeftButtonPressed;
        _isRightPressed = props.IsRightButtonPressed;
        _isMiddlePressed = props.IsMiddleButtonPressed;

        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point currentPos = e.GetPosition(this);
        Avalonia.Vector delta = currentPos - _lastPointerPosition;
        _lastPointerPosition = currentPos;

        if (_cameraDirector == null)
        {
            return;
        }

        float deltaX = (float)delta.X;
        float deltaY = (float)delta.Y;

        if (_isLeftPressed || _isRightPressed)
        {
            if (_cameraDirector.Mode == CameraMode.FreeSpectator)
            {
                // Free Spectator: Mouse drag orbits pitch and yaw
                float yawDelta = deltaX * 0.25f;
                float pitchDelta = -deltaY * 0.25f;

                _cameraDirector.SetFreeSpectatorOrientation(
                    _cameraDirector.FreeYawDeg + yawDelta,
                    _cameraDirector.FreePitchDeg + pitchDelta);
            }
            else if (_cameraDirector.Mode == CameraMode.RtsIsometric)
            {
                // RTS Mode: Drag pans camera in the XY ground plane
                float panSensitivity = 0.05f * (_cameraDirector.RtsDistance / 28.0f);
                Vector3 pan = new(-deltaX * panSensitivity, deltaY * panSensitivity, 0.0f);
                _cameraDirector.ResetPosition(
                    _cameraDirector.CurrentEye + pan,
                    _cameraDirector.CurrentTarget + pan);
            }
            else if (_cameraDirector.Mode == CameraMode.PlayerPov)
            {
                // Chase mode: Left drag adjusts chase distance/height
                _cameraDirector.ChaseHeight = Math.Clamp(_cameraDirector.ChaseHeight - deltaY * 0.05f, 4.0f, 40.0f);
            }
        }
        else if (_isMiddlePressed)
        {
            // Middle click pan in any mode
            Vector3 pan = new(-deltaX * 0.05f, deltaY * 0.05f, 0.0f);
            _cameraDirector.ResetPosition(
                _cameraDirector.CurrentEye + pan,
                _cameraDirector.CurrentTarget + pan);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _isLeftPressed = false;
        _isRightPressed = false;
        _isMiddlePressed = false;
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_cameraDirector == null)
        {
            return;
        }

        float wheelDelta = (float)e.Delta.Y;

        switch (_cameraDirector.Mode)
        {
            case CameraMode.RtsIsometric:
                _cameraDirector.RtsDistance = Math.Clamp(_cameraDirector.RtsDistance - wheelDelta * 2.0f, 6.0f, 60.0f);
                break;

            case CameraMode.PlayerPov:
                _cameraDirector.ChaseDistance = Math.Clamp(_cameraDirector.ChaseDistance - wheelDelta * 1.5f, 4.0f, 35.0f);
                break;

            case CameraMode.FreeSpectator:
                _cameraDirector.FovDegrees = Math.Clamp(_cameraDirector.FovDegrees - wheelDelta * 2.0f, 20.0f, 110.0f);
                break;

            default:
                _cameraDirector.FovDegrees = Math.Clamp(_cameraDirector.FovDegrees - wheelDelta * 1.5f, 25.0f, 95.0f);
                break;
        }

        e.Handled = true;
    }

    #endregion

    #region Keyboard Input Handling

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UpdateKeyState(e.Key, true);

        if (_cameraDirector != null && _cameraDirector.Mode == CameraMode.FreeSpectator)
        {
            ProcessFreeSpectatorMovement(0.016f);
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        UpdateKeyState(e.Key, false);
        e.Handled = true;
    }

    private void UpdateKeyState(Key key, bool isPressed)
    {
        switch (key)
        {
            case Key.W: _isWPressed = isPressed; break;
            case Key.A: _isAPressed = isPressed; break;
            case Key.S: _isSPressed = isPressed; break;
            case Key.D: _isDPressed = isPressed; break;
            case Key.Space: _isSpacePressed = isPressed; break;
            case Key.LeftCtrl:
            case Key.RightCtrl: _isCtrlPressed = isPressed; break;
            case Key.LeftShift:
            case Key.RightShift: _isShiftPressed = isPressed; break;
        }
    }

    public void ProcessFreeSpectatorMovement(float dt)
    {
        if (_cameraDirector == null || _cameraDirector.Mode != CameraMode.FreeSpectator)
        {
            return;
        }

        Vector3 move = Vector3.Zero;
        if (_isWPressed) move += Vector3.UnitY;
        if (_isSPressed) move -= Vector3.UnitY;
        if (_isDPressed) move += Vector3.UnitX;
        if (_isAPressed) move -= Vector3.UnitX;
        if (_isSpacePressed) move += Vector3.UnitZ; // Up
        if (_isCtrlPressed) move -= Vector3.UnitZ;  // Down

        if (move.LengthSquared() > 0.001f)
        {
            _cameraDirector.TranslateFreeSpectator(move, dt, sprint: _isShiftPressed);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        lock (_bitmapLock)
        {
            _writeableBitmap?.Dispose();
            _writeableBitmap = null;

            _bufferRing?.Dispose();
            _bufferRing = null;
        }

        _logger.LogInformation("[VulkanViewportControl] Disposed viewport control resources");
        GC.SuppressFinalize(this);
    }
}
