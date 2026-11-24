// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Configuration;

namespace NEXUS.ControlPanel.GUI.Services;

public interface ISecretsManagerService
{
    Task<NexusSecrets> LoadSecretsAsync(CancellationToken cancellationToken = default);

    Task SaveSecretsAsync(NexusSecrets secrets, NexusSecrets? originalSecrets = null,
        CancellationToken cancellationToken = default);

    /// <summary>Compares vault ⇄ <c>.env</c> ⇄ OS environment. Default implementation reports no drift.</summary>
    Task<SecretsDriftReport> GetDriftReportAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SecretsDriftReport.None);
    }

    /// <summary>Regenerates <c>.env</c> from the vault (the vault always wins).</summary>
    async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        NexusSecrets current = await LoadSecretsAsync(cancellationToken);
        await SaveSecretsAsync(current, null, cancellationToken);
    }
}
