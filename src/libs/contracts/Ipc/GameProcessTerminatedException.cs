// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Ipc;

/// <summary>
/// Exception thrown when a monitored game process (hon_x64.exe client or dedicated server)
/// terminates unexpectedly while IPC requests are in-flight.
/// </summary>
public sealed class GameProcessTerminatedException : IOException
{
    public int ProcessId { get; }
    public int ExitCode { get; }

    public GameProcessTerminatedException(int processId, int exitCode)
        : base($"Game process (PID: {processId}) terminated unexpectedly with exit code {exitCode}.")
    {
        ProcessId = processId;
        ExitCode = exitCode;
    }

    public GameProcessTerminatedException(int processId, int exitCode, string message)
        : base(message)
    {
        ProcessId = processId;
        ExitCode = exitCode;
    }

    public GameProcessTerminatedException(int processId, int exitCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ProcessId = processId;
        ExitCode = exitCode;
    }
}
