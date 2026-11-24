// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoExportMissingFfmpegFailClosedTests.cs
// System: Replay Studio Desktop GUI Tool Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Export;
using Xunit;

namespace ReplayStudio.Tests;

[Trait("Category", "Experimental")]
public sealed class VideoExportMissingFfmpegFailClosedTests
{
    [Fact]
    public async Task ExportVideoAsync_WhenFfmpegMissingAndNoCustomStream_FailsClosedWithoutWritingFakeMp4()
    {
        // protects: User-facing MP4 video export honesty — never write UTF-8 placeholder text disguised as a valid .mp4 file.
        // fails_when: Missing FFmpeg executable returns Success=true or writes '[REPLAY_STUDIO_VIDEO_EXPORT_MP4_CONTAINER_...]' bytes to disk.
        // seam: VideoExportPipeline.ExportVideoAsync -> TryLaunchFfmpeg fallback branch.

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        string outputPath = Path.Combine(Path.GetTempPath(), $"missing_ffmpeg_{Guid.NewGuid():N}.mp4");

        try
        {
            ExportParameters parameters = new()
            {
                Preset = ExportPreset.Fast720p60,
                OutputFilePath = outputPath,
                InMarkerMs = 0,
                OutMarkerMs = 200,
                IncludeAudio = false,
                FfmpegExecutablePath = $"nonexistent_ffmpeg_binary_{Guid.NewGuid():N}"
            };

            ExportResult result = await pipeline.ExportVideoAsync(
                parameters,
                renderFrameCallback: null,
                combatEvents: null,
                progress: null,
                customOutputStream: null,
                ct: CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("FFmpeg executable");
            File.Exists(outputPath).Should().BeFalse("missing FFmpeg must never leave a corrupt fake .mp4 placeholder on disk");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
