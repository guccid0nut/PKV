// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Replay;

namespace NEXUS.Contracts.Services;

/// <summary>
///     Client contract for interacting with the Replay Video Renderer service.
/// </summary>
public interface IReplayRendererClient
{
    /// <summary>
    ///     Submits a match replay video rendering job.
    /// </summary>
    Task<ReplayRenderJobResponseDto> SubmitRenderJobAsync(ReplayRenderJobRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves the execution status of a previously submitted rendering job.
    /// </summary>
    Task<ReplayRenderJobResponseDto?> GetRenderJobStatusAsync(string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels an in-progress or queued rendering job.
    /// </summary>
    Task<bool> CancelRenderJobAsync(string jobId, CancellationToken cancellationToken = default);
}
