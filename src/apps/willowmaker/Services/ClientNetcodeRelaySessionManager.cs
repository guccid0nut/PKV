// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientNetcodeRelaySessionManager.cs
// System: WILLOWMAKER - Transparent Client-Side UDP Netcode Relay Session Manager
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Net;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Manages transparent client-side UDP netcode relay sessions for WILLOWMAKER game client connections.
///     Binds <see cref="ClientNetcodeRelay"/> using <see cref="IOptionsMonitor{NetcodeRelayOptions}"/> and
///     exposes <see cref="EffectiveClientConnectEndPoint"/> for game server connections when
///     <see cref="EnableClientZstdRelay"/> is enabled (default <c>false</c> with 500ms handshake auto-bypass).
/// </summary>
public sealed class ClientNetcodeRelaySessionManager : IAsyncDisposable, IDisposable
{
    private readonly IOptionsMonitor<NetcodeRelayOptions>? _optionsMonitor;
    private readonly ILogger<ClientNetcodeRelay>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _syncLock = new();

    private ClientNetcodeRelay? _activeRelay;
    private IPEndPoint? _lastRemoteServerEndPoint;
    private bool _enableClientZstdRelay;
    private bool _isDisposed;

    /// <summary>
    ///     Gets or sets whether transparent client-side Zstandard compression relay is enabled.
    ///     Defaults to false until explicitly enabled via options or launcher configuration.
    /// </summary>
    public bool EnableClientZstdRelay
    {
        get => _enableClientZstdRelay || (_optionsMonitor?.CurrentValue.RelayEnabled ?? false);
        set => _enableClientZstdRelay = value;
    }

    /// <summary>
    ///     Gets the active relay instance, or null if no relay is currently running.
    /// </summary>
    public ClientNetcodeRelay? ActiveRelay
    {
        get
        {
            lock (_syncLock)
            {
                return _activeRelay;
            }
        }
    }

    /// <summary>
    ///     Gets the effective connect endpoint that the game client should target.
    ///     Returns the remote server endpoint directly when disabled or bypassed,
    ///     or the local loopback relay endpoint when the relay is actively running.
    /// </summary>
    public IPEndPoint? EffectiveClientConnectEndPoint
    {
        get
        {
            lock (_syncLock)
            {
                if (_activeRelay != null)
                {
                    return _activeRelay.EffectiveClientConnectEndPoint;
                }

                return _lastRemoteServerEndPoint;
            }
        }
    }

    public ClientNetcodeRelaySessionManager(
        IOptionsMonitor<NetcodeRelayOptions>? optionsMonitor = null,
        ILogger<ClientNetcodeRelay>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enableClientZstdRelay = _optionsMonitor?.CurrentValue.RelayEnabled ?? false;
    }

    /// <summary>
    ///     Resolves the effective endpoint that the game client launcher should pass to the client.
    ///     If <see cref="EnableClientZstdRelay"/> is enabled, initializes and starts a <see cref="ClientNetcodeRelay"/>
    ///     and returns its loopback <see cref="ClientNetcodeRelay.EffectiveClientConnectEndPoint"/>.
    ///     Otherwise returns <paramref name="remoteServerEndPoint"/> directly.
    /// </summary>
    public IPEndPoint ResolveEffectiveConnectEndPoint(IPEndPoint remoteServerEndPoint, int localListenPort = 0)
    {
        ArgumentNullException.ThrowIfNull(remoteServerEndPoint);

        lock (_syncLock)
        {
            _lastRemoteServerEndPoint = remoteServerEndPoint;

            if (!EnableClientZstdRelay)
            {
                if (_activeRelay != null)
                {
                    _activeRelay.Dispose();
                    _activeRelay = null;
                }

                return remoteServerEndPoint;
            }

            if (_activeRelay != null && _activeRelay.RemoteServerEndPoint.Equals(remoteServerEndPoint) && !_activeRelay.IsBypassed)
            {
                return _activeRelay.EffectiveClientConnectEndPoint;
            }

            _activeRelay?.Dispose();

            TimeSpan handshakeTimeout = TimeSpan.FromMilliseconds(
                _optionsMonitor?.CurrentValue.HandshakeTimeoutMs is > 0
                    ? _optionsMonitor.CurrentValue.HandshakeTimeoutMs
                    : 500);

            _activeRelay = new ClientNetcodeRelay(
                remoteServerEndPoint,
                _optionsMonitor,
                localListenPort,
                handshakeTimeout,
                _logger,
                _timeProvider);

            _activeRelay.Start();
            return _activeRelay.EffectiveClientConnectEndPoint;
        }
    }

    /// <summary>
    ///     Stops and disposes any running relay session.
    /// </summary>
    public void StopRelay()
    {
        lock (_syncLock)
        {
            _activeRelay?.Dispose();
            _activeRelay = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopRelay();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        ClientNetcodeRelay? relay;
        lock (_syncLock)
        {
            relay = _activeRelay;
            _activeRelay = null;
        }

        if (relay != null)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }
}
