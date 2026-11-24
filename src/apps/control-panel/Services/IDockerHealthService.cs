// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Docker.DotNet;
using Docker.DotNet.Models;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public interface IDockerHealthService
{
    IDockerClient Client { get; }

    Task<DockerStatus> CheckDockerStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> IsImageBuiltAsync(string imageName = "projectkongor/compel-dedicated:latest",
        CancellationToken cancellationToken = default);

    Task<bool> IsDedicatedImageStaleAsync(string workspaceRoot,
        string imageName = "projectkongor/compel-dedicated:latest",
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Output)> LaunchContainerAsync(CreateContainerParameters parameters,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Output)> StopContainerAsync(string containerName = ContainerLaunchCommandBuilder.RankedContainerName,
        CancellationToken cancellationToken = default);

    Task<bool> StartContainerAsync(string containerName, CancellationToken cancellationToken = default);

    Task<bool> EnsureImagePulledAsync(string imageName, CancellationToken cancellationToken = default);

    Task<(bool Success, string Output)> BuildImageAsync(string dockerfilePath, string contextPath,
        string tag = "projectkongor/compel-dedicated:latest", CancellationToken cancellationToken = default);
}
