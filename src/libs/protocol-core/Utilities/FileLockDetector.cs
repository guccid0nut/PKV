// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ASPIRE.Common.Utilities;

/// <summary>
///     A single process holding a lock on a file, identified by its process ID and friendly application name.
/// </summary>
public sealed record FileLockingProcess(int ProcessID, string ApplicationName);

/// <summary>
///     Provides cross-platform methods for detecting which processes are currently holding file locks
///     (Windows Restart Manager rstrtmgr.dll, Linux /proc/*/fd, macOS lsof) (#266).
/// </summary>
public static class FileLockDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessID;
        public FILETIME FileTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionID;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, uint dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint dwSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles,
        string[] rgsFileNames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        out uint lpdwRebootReasons);

    /// <summary>
    ///     Retrieves the list of processes that currently hold a lock on the specified file.
    /// </summary>
    public static List<FileLockingProcess> GetLockingProcesses(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        if (OperatingSystem.IsWindows())
        {
            return GetLockingProcessesWindows(filePath);
        }

        if (OperatingSystem.IsLinux())
        {
            return GetLockingProcessesLinux(filePath);
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetLockingProcessesMacOS(filePath);
        }

        return [];
    }

    /// <summary>
    ///     Formats a human- and agent-actionable diagnostic string listing any processes locking <paramref name="filePath"/>.
    /// </summary>
    public static string FormatLockingProcessesDiagnostic(string filePath)
    {
        List<FileLockingProcess> lockers = GetLockingProcesses(filePath);
        if (lockers.Count == 0)
        {
            return $"No locking processes reported by OS for '{filePath}'.";
        }

        string formatted = string.Join(", ", lockers.Select(p => $"{p.ApplicationName} (PID {p.ProcessID})"));
        return $"File '{filePath}' is locked by {lockers.Count} process(es): {formatted}.";
    }

    private static List<FileLockingProcess> GetLockingProcessesWindows(string filePath)
    {
        List<FileLockingProcess> processes = [];

        string sessionKey = Guid.NewGuid().ToString();

        int result = RmStartSession(out uint sessionHandle, 0, sessionKey);
        if (result != 0)
        {
            return processes;
        }

        try
        {
            string[] resources = [filePath];

            result = RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (result != 0)
            {
                return processes;
            }

            uint procInfoNeeded = 0;
            uint procInfo = 0;

            result = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, null, out _);

            // 234 represents ERROR_MORE_DATA which is expected when querying the size of the process list
            if (result == 234 && procInfoNeeded > 0)
            {
                RM_PROCESS_INFO[] processInfos = new RM_PROCESS_INFO[procInfoNeeded];
                procInfo = procInfoNeeded;

                result = RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, processInfos, out _);
                if (result == 0)
                {
                    for (int processIndex = 0; processIndex < procInfo; processIndex++)
                    {
                        RM_PROCESS_INFO processInfo = processInfos[processIndex];
                        processes.Add(new FileLockingProcess(processInfo.Process.dwProcessID, processInfo.strAppName));
                    }
                }
            }
        }
        finally
        {
            _ = RmEndSession(sessionHandle);
        }

        return processes;
    }

    private static List<FileLockingProcess> GetLockingProcessesLinux(string filePath)
    {
        List<FileLockingProcess> processes = [];

        if (!Directory.Exists("/proc"))
        {
            return processes;
        }

        foreach (string processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out int processID))
            {
                continue;
            }

            try
            {
                string fdDir = Path.Combine(processDirectory, "fd");
                if (!Directory.Exists(fdDir))
                {
                    continue;
                }

                foreach (string fileDescriptor in Directory.EnumerateFileSystemEntries(fdDir))
                {
                    FileSystemInfo? target = File.ResolveLinkTarget(fileDescriptor, returnFinalTarget: false);
                    if (target is not null && string.Equals(target.FullName, filePath, StringComparison.Ordinal))
                    {
                        processes.Add(new FileLockingProcess(processID, ReadLinuxProcessName(processID)));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileLockDetector] Skipping /proc/{processID}/fd inspection: {ex.Message}");
            }
        }

        return processes;
    }

    private static string ReadLinuxProcessName(int processID)
    {
        try
        {
            return File.ReadAllText($"/proc/{processID}/comm").Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLockDetector] Could not read /proc/{processID}/comm: {ex.Message}");
            return "Unknown Process";
        }
    }

    private static List<FileLockingProcess> GetLockingProcessesMacOS(string filePath)
    {
        List<FileLockingProcess> processes = [];

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "lsof",
                Arguments = $@"-F pcn -- ""{filePath}""",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                int currentProcessID = 0;
                foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    char fieldType = line[0];
                    string fieldValue = line[1..];

                    if (fieldType is 'p' && int.TryParse(fieldValue, out int parsedProcessID))
                    {
                        currentProcessID = parsedProcessID;
                    }
                    else if (fieldType is 'c' && currentProcessID is not 0)
                    {
                        processes.Add(new FileLockingProcess(currentProcessID, fieldValue));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLockDetector] lsof invocation failed on macOS: {ex.Message}");
        }

        return processes;
    }
}
