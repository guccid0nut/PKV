// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public interface IDatabaseHealthService
{
    Task<DatabaseStatus> CheckDatabaseHealthAsync(ISecretsManagerService? secretsManager = null,
        CancellationToken cancellationToken = default);

    Task<DatabaseStatus> CheckSqlServerHealthAsync(int port = 1433, CancellationToken cancellationToken = default);
    Task<DatabaseStatus> CheckRedisHealthAsync(CancellationToken cancellationToken = default);
    Task<DatabaseStatus> CheckPyroscopeHealthAsync(int port = 4040, CancellationToken cancellationToken = default);
    Task<DatabaseStatus> CheckDotnetMonitorHealthAsync(int port = 52323, CancellationToken cancellationToken = default);
}
