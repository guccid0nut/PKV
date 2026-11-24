// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Common.Services.EntityCache;

/// <summary>
///     Background pre-warming engine that warms entity cache and 3D preview assets
///     during application boot without blocking the main thread or UI responsiveness.
/// </summary>
public sealed class EntityCachePrewarmer(
    IPrebakedEntityRegistry registry,
    ILogger<EntityCachePrewarmer>? logger = null,
    IGameArchiveVfsService? vfsService = null,
    IConfiguration? configuration = null) : IHostedService, IDisposable
{
    private readonly IConfiguration? _configuration = configuration;
    private readonly ILogger<EntityCachePrewarmer> _logger = logger ?? NullLogger<EntityCachePrewarmer>.Instance;
    private readonly IPrebakedEntityRegistry _registry = registry;
    private readonly IGameArchiveVfsService? _vfsService = vfsService;

    private CancellationTokenSource? _cts;
    private Task? _prewarmTask;

    public void Dispose()
    {
        try
        {
            if (_cts != null)
            {
                if (!_cts.IsCancellationRequested) _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "[Prewarmer] CTS already disposed during Dispose.");
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "[Prewarmer] Ignored aggregate exception during Dispose.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Prewarmer] Starting background entity cache pre-warming engine...");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _prewarmTask = Task.Run(() => ExecutePrewarmingAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "[Prewarmer] CTS already disposed during StopAsync.");
            }

        if (_prewarmTask != null)
            try
            {
                await _prewarmTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Prewarmer] Prewarm task ended during StopAsync.");
            }
    }

    public async Task ExecutePrewarmingAsync(CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            // 1. Initialize & Deserialize Entity Cache Container into FrozenDictionaries
            await _registry.InitializeAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("[Prewarmer] Pre-baked registry warmed with {Total} entities in {Elapsed}ms.",
                _registry.TotalEntitiesLoaded, sw.ElapsedMilliseconds);

            // 2. Pre-warm high-priority preview assets (GLB models, icons) in background
            if (_vfsService != null && !ct.IsCancellationRequested)
            {
                int warmedAssets = 0;
                foreach (HeroDefinitionDto hero in _registry.GetAllHeroes())
                {
                    if (ct.IsCancellationRequested) break;

                    // Touch model & icon lookups to ensure OS disk cache & VFS memory cache residency
                    if (!string.IsNullOrWhiteSpace(hero.Name))
                        try
                        {
                            byte[]? glb = _vfsService.GetHeroModelGlb(hero.Name);
                            if (glb != null && glb.Length > 0) warmedAssets++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[Prewarmer] Non-critical warning pre-warming GLB for {Hero}",
                                hero.Name);
                        }

                    string icon = !string.IsNullOrWhiteSpace(hero.DefaultIcon) ? hero.DefaultIcon : hero.IconPath;
                    if (!string.IsNullOrWhiteSpace(icon))
                        try
                        {
                            _ = _vfsService.GetTextureBytes(icon);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[Prewarmer] Non-critical warning pre-warming icon for {Hero}",
                                hero.Name);
                        }
                }

                _logger.LogInformation(
                    "[Prewarmer] Pre-warmed {Count} hero 3D preview models and textures into VFS cache.", warmedAssets);
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "[Prewarmer] Host disposed while entity cache pre-warming was active.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Prewarmer] Entity cache pre-warming was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Prewarmer] Failed to complete background entity pre-warming.");
        }
    }
}
