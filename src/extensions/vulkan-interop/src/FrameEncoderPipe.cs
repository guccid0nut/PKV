// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace ReplayVulkanRenderer;

public sealed class FrameEncoderPipe(uint width, uint height, int framerate, string outputPath) : IDisposable
{
    private readonly int _framerate = framerate;
    private readonly uint _height = height;
    private readonly string _outputPath = outputPath;
    private readonly uint _width = width;
    private Process? _ffmpegProcess;
    private Stream? _stdinStream;

    public void Dispose()
    {
        if (_stdinStream != null)
        {
            _stdinStream.Flush();
            _stdinStream.Close();
            _stdinStream = null;
        }

        if (_ffmpegProcess != null)
        {
            Console.WriteLine("[FrameEncoderPipe] Waiting for FFmpeg to finish encoding MP4 container...");
            _ffmpegProcess.WaitForExit(15000);
            if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill();

            Console.WriteLine(
                $"[FrameEncoderPipe] FFmpeg process finished cleanly with exit code {_ffmpegProcess.ExitCode}");
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        GC.SuppressFinalize(this);
    }

    public bool Start(bool useNv12Input = false)
    {
        string resolution = $"{_width}x{_height}";
        string fullOutputPath = Path.GetFullPath(_outputPath);
        string inputPixFmt = useNv12Input ? "nv12" : "rgba";
        Console.WriteLine(
            $"[FrameEncoderPipe] Starting FFmpeg MP4 Encoder ({resolution} @ {_framerate}fps, pix_fmt={inputPixFmt}) -> {fullOutputPath}");

        string? directory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

        ProcessStartInfo startInfo = new()
        {
            FileName = "ffmpeg",
            Arguments =
                $"-y -f rawvideo -pix_fmt {inputPixFmt} -s {resolution} -r {_framerate} -i - -c:v libx264 -preset fast -pix_fmt yuv420p \"{fullOutputPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            _ffmpegProcess = Process.Start(startInfo);
            if (_ffmpegProcess == null)
            {
                Console.WriteLine("[FrameEncoderPipe] ERROR: Failed to launch FFmpeg process.");
                return false;
            }

            _stdinStream = _ffmpegProcess.StandardInput.BaseStream;
            Console.WriteLine("[FrameEncoderPipe] FFmpeg encoder pipe established successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FrameEncoderPipe] ERROR launching FFmpeg: {ex.Message}");
            return false;
        }
    }

    public void WriteFrame(byte[] rgbaBytes)
    {
        if (_stdinStream == null) throw new InvalidOperationException("Encoder pipe is not open.");

        _stdinStream.Write(rgbaBytes, 0, rgbaBytes.Length);
    }

    public void WriteNv12Frame(ReadOnlySpan<byte> nv12Bytes)
    {
        if (_stdinStream == null) throw new InvalidOperationException("Encoder pipe is not open.");

        _stdinStream.Write(nv12Bytes);
    }
}
