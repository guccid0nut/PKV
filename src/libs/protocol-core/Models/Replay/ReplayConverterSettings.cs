// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Models.Replay;

public class ReplayConverterSettings
{
    public string HonExecutablePath { get; set; } = string.Empty;
    public string FfmpegExecutablePath { get; set; } = "ffmpeg";
    public string OutputDirectory { get; set; } = "Data/Videos";
    public int ResolutionWidth { get; set; } = 1920;
    public int ResolutionHeight { get; set; } = 1080;
    public int Framerate { get; set; } = 60;
}

public class VideoStorageSettings
{
    public string Provider { get; set; } = "Local";
    public string BaseUrl { get; set; } = "/videos";
    public LocalVideoStorageSettings LocalDisk { get; set; } = new();
}

public class LocalVideoStorageSettings
{
    public string UploadDirectory { get; set; } = "Data/Videos";
}
