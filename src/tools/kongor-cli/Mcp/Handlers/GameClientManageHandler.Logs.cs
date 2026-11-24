// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class GameClientManageHandler
{
    public async Task<ToolCallResult> GetClientLogsAsync(
        int maxLines,
        string logType,
        string level,
        string? customWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string baseDir = customWorkingDirectory
            ?? _lastWorkingDirectory
            ?? Path.Combine(FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory), "games", "client");

        List<(string Source, string FilePath)> targetLogFiles = [];

        if (logType is "console" or "all")
        {
            string? newestConsole = null;
            DateTime newestTime = DateTime.MinValue;

            string[] searchRoots =
            [
                Path.Combine(baseDir, "userdata", "Heroes of Newerth x64"),
                Path.Combine(baseDir, "userdataKONGOR"),
                Path.Combine(baseDir, "userdata"),
                baseDir
            ];

            foreach (string root in searchRoots)
            {
                if (Directory.Exists(root))
                {
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(root, "console.log", SearchOption.AllDirectories))
                        {
                            DateTime lastWrite = File.GetLastWriteTimeUtc(file);
                            if (lastWrite > newestTime)
                            {
                                newestTime = lastWrite;
                                newestConsole = file;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[GameClientManage] Skipped inaccessible log directory '{root}': {ex.Message}");
                    }
                }
            }

            if (newestConsole != null)
            {
                targetLogFiles.Add(("console", newestConsole));
            }
            else
            {
                string direct = Path.Combine(baseDir, "console.log");
                if (File.Exists(direct)) targetLogFiles.Add(("console", direct));
            }
        }

        if (logType is "dxvk" or "all")
        {
            string d3d9Log = Path.Combine(baseDir, "hon_x64_d3d9.log");
            if (File.Exists(d3d9Log)) targetLogFiles.Add(("dxvk-d3d9", d3d9Log));

            string dxgiLog = Path.Combine(baseDir, "hon_x64_dxgi.log");
            if (File.Exists(dxgiLog)) targetLogFiles.Add(("dxvk-dxgi", dxgiLog));
        }

        List<ClientLogEntryDto> resultLines = [];

        // 1. Read on-disk log files safely with FileShare.ReadWrite | FileShare.Delete
        foreach (var (src, path) in targetLogFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Encoding encoding = Encoding.UTF8;
                if (fs.Length >= 2)
                {
                    byte[] header = new byte[Math.Min(64, (int)fs.Length)];
                    int readBytes = fs.Read(header, 0, header.Length);
                    if ((readBytes >= 2 && header[0] == 0xFF && header[1] == 0xFE) ||
                        (readBytes >= 4 && header[1] == 0x00 && header[3] == 0x00))
                    {
                        encoding = Encoding.Unicode;
                    }
                    fs.Position = 0;
                }
                using StreamReader reader = new(fs, encoding, detectEncodingFromByteOrderMarks: true);

                List<string> fileLines = [];
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (line.Contains('\0', StringComparison.Ordinal)) line = line.Replace("\0", string.Empty, StringComparison.Ordinal);
                    if (!string.IsNullOrWhiteSpace(line))
                        fileLines.Add(line);
                }

                int start = Math.Max(0, fileLines.Count - (maxLines * 4));
                for (int i = start; i < fileLines.Count; i++)
                {
                    string raw = fileLines[i];
                    string parsedLevel = ParseClientLogLevel(raw);
                    if (MatchesSeverityFilter(parsedLevel, level))
                    {
                        resultLines.Add(new ClientLogEntryDto
                        {
                            Source = src,
                            Level = parsedLevel,
                            Message = raw
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Could not read log file '{path}': {ex.Message}");
            }
        }

        // 2. Fallback to live stdout ring buffer if no files exist or if requested
        if (targetLogFiles.Count == 0 && (logType is "console" or "all") && !_outputBuffer.IsEmpty)
        {
            var ringLines = _outputBuffer.GetLines(maxLines * 4);
            for (int i = 0; i < ringLines.Count; i++)
            {
                string raw = ringLines[i];
                string parsedLevel = ParseClientLogLevel(raw);
                if (MatchesSeverityFilter(parsedLevel, level))
                {
                    resultLines.Add(new ClientLogEntryDto
                    {
                        Source = "stdout-buffer",
                        Level = parsedLevel,
                        Message = raw
                    });
                }
            }
        }

        var finalLines = resultLines.TakeLast(maxLines).ToList();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "logs",
            logType,
            level,
            workingDirectory = baseDir,
            discoveredSources = targetLogFiles.Select(t => t.Source).ToArray(),
            totalLines = finalLines.Count,
            lines = finalLines,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    public static string ParseClientLogLevel(string text)
        => ClientOutputRingBuffer.ParseClientLogLevel(text);

    public static bool MatchesSeverityFilter(string actualLevel, string filterLevel)
        => ClientOutputRingBuffer.MatchesSeverityFilter(actualLevel, filterLevel);

    public static bool SetClientWindowHeadless(IntPtr hwnd, int width = 1280, int height = 720)
        => new ClientProcessSupervisor().SetClientWindowHeadless(hwnd, width, height);

    public static void StartOffscreenWindowWatchdog(Process process)
        => new ClientProcessSupervisor().StartOffscreenWindowWatchdog(process);

    public static void StopOffscreenWindowWatchdog()
        => new ClientProcessSupervisor().StopOffscreenWindowWatchdog();

    public static void HideAllProcessWindows(int processId)
        => new ClientProcessSupervisor().HideAllProcessWindows(processId);

    public static bool SetClientWindowForeground(IntPtr hwnd, int x = 0, int y = 0, int width = 1920, int height = 1080, bool activate = true)
        => new ClientProcessSupervisor().SetClientWindowForeground(hwnd, x, y, width, height, activate);

    public static bool SetClientWindowMinimized(IntPtr hwnd)
        => new ClientProcessSupervisor().SetClientWindowMinimized(hwnd);

    public static bool IsWindowOffscreen(IntPtr hwnd)
        => new ClientProcessSupervisor().IsWindowOffscreen(hwnd);

    public static int TryScrapeLatestGamePhaseFromLog(string? customWorkingDirectory = null)
        => ClientOutputRingBuffer.TryScrapeLatestGamePhaseFromLog(customWorkingDirectory);

    public static string? TryScrapeLatestHeroAssertFromLog(string? customWorkingDirectory = null)
        => ClientOutputRingBuffer.TryScrapeLatestHeroAssertFromLog(customWorkingDirectory);

    private static string ReadTailOfLogFile(string path, int maxBytes = 65536)
        => ClientOutputRingBuffer.ReadTailOfLogFile(path, maxBytes);

    private McpServerIpcBridge? ResolveMcpServerIpcBridge()
    {
        if (_gameIpcBridge is McpServerIpcBridge direct) return direct;
        if (_gameIpcBridge is CompositeGameIpcBridge composite)
        {
            if (composite.Primary is McpServerIpcBridge primary) return primary;
            if (composite.Secondary is McpServerIpcBridge secondary) return secondary;
            if (composite.Tertiary is McpServerIpcBridge tertiary) return tertiary;
        }
        return null;
    }
}


/// <summary>
/// Individual structured log entry returned by kongor_game_client_manage action 'logs'.
/// </summary>
public sealed class ClientLogEntryDto
{
    public string Source { get; set; } = string.Empty;
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
}
