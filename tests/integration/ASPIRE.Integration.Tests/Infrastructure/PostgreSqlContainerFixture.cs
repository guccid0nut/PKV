// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ASPIRE.Tests.Infrastructure;

/// <summary>
///     Provides a shared, managed PostgreSQL container instance for authentic relational database integration testing.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal is managed asynchronously via IAsyncLifetime")]
public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    public static readonly PostgreSqlContainerFixture Shared = new();
    private static readonly SemaphoreSlim InitLock = new(1, 1);

    private readonly PostgreSqlContainer? _container;
    private readonly ILogger<PostgreSqlContainerFixture> _logger;
    private bool _isContainerStarted;

    public PostgreSqlContainerFixture(ILogger<PostgreSqlContainerFixture>? logger = null)
    {
        _logger = logger ?? NullLogger<PostgreSqlContainerFixture>.Instance;

        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("kongor_test")
                .WithUsername("kongor_user")
                .WithPassword("kongor_pass_2026")
                .WithCleanUp(true)
                .WithLogger(NullLogger.Instance)
                .Build();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Testcontainers] Could not initialize PostgreSQL container builder. Container runtime may not be available.");
            _container = null;
        }
    }

    private static bool _initAttempted;

    public static async Task<string?> EnsureStartedAsync()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return null;
        }

        if (_initAttempted)
        {
            return Shared.ConnectionString;
        }

        await InitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_initAttempted)
            {
                _initAttempted = true;
                await Shared.InitializeAsync().ConfigureAwait(false);
            }
            return Shared.ConnectionString;
        }
        finally
        {
            InitLock.Release();
        }
    }

    public bool IsAvailable => _isContainerStarted && _container is not null;

    public string? ConnectionString => _isContainerStarted && _container is not null
        ? _container.GetConnectionString()
        : null;

    public async Task InitializeAsync()
    {
        if (_container is null)
        {
            return;
        }

        try
        {
            await _container.StartAsync().ConfigureAwait(false);
            _isContainerStarted = true;
            _logger.LogInformation("[Testcontainers] PostgreSQL container successfully started at {ConnectionString}", _container.GetConnectionString());
        }
        catch (Exception ex)
        {
            _isContainerStarted = false;
            _logger.LogWarning(ex, "[Testcontainers] Failed to start PostgreSQL container. Falling back to test database provider.");
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null && _isContainerStarted)
        {
            try
            {
                await _container.DisposeAsync().ConfigureAwait(false);
                _isContainerStarted = false;
                _logger.LogInformation("[Testcontainers] PostgreSQL container stopped and cleaned up.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Testcontainers] Error during PostgreSQL container cleanup.");
            }
        }
    }

    /// <summary>
    ///     Creates a configured DbContextOptionsBuilder targeting the real PostgreSQL container (or in-memory fallback if unavailable).
    /// </summary>
    public DbContextOptionsBuilder<MerrickContext> CreateDbContextOptionsBuilder(string? fallbackDatabaseName = null)
    {
        DbContextOptionsBuilder<MerrickContext> builder = new();
        if (IsAvailable && !string.IsNullOrWhiteSpace(ConnectionString))
        {
            builder.UseNpgsql(ConnectionString);
        }
        else
        {
            builder.UseInMemoryDatabase(fallbackDatabaseName ?? Guid.NewGuid().ToString("N"));
        }

        return builder;
    }
}
