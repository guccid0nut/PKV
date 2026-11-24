// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using Kongor.Core.Configuration;

namespace Kongor.Cli.Infrastructure;

/// <summary>
/// Per-invocation rolling NDJSON telemetry logger (<c>~/.kongor/logs/cli.ndjson</c>).
/// Logs command args (with sensitive secrets/passwords redacted), invocation CWD, resolved workspace,
/// resolution tier, duration, and exit code for rapid troubleshooting.
/// Never throws or writes to <see cref="Console.Out"/>.
/// </summary>
internal static class CliTelemetryLogger
{
    public const long MaxLogFileBytes = 10L * 1024L * 1024L;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void RecordInvocation(
        string[] rawArgs,
        string invocationCwd,
        KongorWorkspaceResult? workspace,
        long durationMs,
        int exitCode,
        string? errorMessage = null,
        string? userHomeOverride = null,
        TimeProvider? timeProvider = null)
    {
        try
        {
            TimeProvider clock = timeProvider ?? TimeProvider.System;
            string logsDir = KongorUserConfigStore.ResolveLogsDir(userHomeOverride);
            Directory.CreateDirectory(logsDir);
            string logPath = Path.Combine(logsDir, "cli.ndjson");

            RollIfOversized(logPath);

            string[] redactedArgs = RedactSensitiveArgs(rawArgs);
            var payload = new
            {
                timestampUtc = clock.GetUtcNow().ToString("O"),
                pid = Environment.ProcessId,
                command = redactedArgs.Length > 0 ? redactedArgs[0] : string.Empty,
                args = redactedArgs,
                invocationCwd,
                resolvedRoot = workspace?.Root,
                layoutKind = workspace?.Kind.ToString(),
                resolutionSource = workspace?.Source.ToString(),
                outOfTree = workspace?.IsOutOfTree,
                durationMs,
                exitCode,
                error = errorMessage
            };

            string line = JsonSerializer.Serialize(payload) + "\n";
            using FileStream fs = new(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using StreamWriter writer = new(fs, Utf8NoBom);
            writer.Write(line);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kongor-telemetry] Debug: Failed appending CLI telemetry log: {ex.Message}");
        }
    }

    public static string[] RedactSensitiveArgs(string[] rawArgs)
    {
        if (rawArgs.Length == 0)
        {
            return [];
        }

        string[] result = new string[rawArgs.Length];
        bool isSecretSet = rawArgs.Length >= 3 &&
            (rawArgs[0].Equals("secret", StringComparison.OrdinalIgnoreCase) ||
             rawArgs[0].Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
             rawArgs[0].Equals("config", StringComparison.OrdinalIgnoreCase)) &&
            rawArgs[1].Equals("set", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < rawArgs.Length; i++)
        {
            string arg = rawArgs[i];
            if (isSecretSet && i >= 3)
            {
                result[i] = "***REDACTED***";
                continue;
            }

            if (i > 0 && IsSensitiveFlag(rawArgs[i - 1]))
            {
                result[i] = "***REDACTED***";
                continue;
            }

            int eq = arg.IndexOf('=');
            if (eq > 0 && IsSensitiveFlag(arg[..eq]))
            {
                result[i] = arg[..(eq + 1)] + "***REDACTED***";
                continue;
            }

            result[i] = arg;
        }

        return result;
    }

    private static bool IsSensitiveFlag(string flag)
    {
        return flag.Equals("--password", StringComparison.OrdinalIgnoreCase) ||
               flag.Equals("--pass", StringComparison.OrdinalIgnoreCase) ||
               flag.Equals("--token", StringComparison.OrdinalIgnoreCase) ||
               flag.Equals("--secret", StringComparison.OrdinalIgnoreCase) ||
               flag.Equals("--api-key", StringComparison.OrdinalIgnoreCase);
    }

    private static void RollIfOversized(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        FileInfo info = new(logPath);
        if (info.Length < MaxLogFileBytes)
        {
            return;
        }

        string archivePath = logPath + ".1";
        try
        {
            File.Move(logPath, archivePath, overwrite: true);
        }
        catch (IOException)
        {
            // Another concurrent invocation may be rotating simultaneously; best effort.
        }
    }
}
