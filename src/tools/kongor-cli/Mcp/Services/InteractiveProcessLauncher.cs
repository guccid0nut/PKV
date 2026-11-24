// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Launches GUI processes onto the active interactive Windows desktop (WinSta0\Default),
/// ensuring they are visible to the user even when the calling process runs in an isolated desktop.
/// </summary>
public static class InteractiveProcessLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool LockSetForegroundWindow(uint uCode);

    private const uint LSFW_LOCK = 1;
    private const uint LSFW_UNLOCK = 2;

    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int STARTF_USEPOSITION = 0x00000004;

    private const short SW_HIDE = 0;
    private const short SW_SHOWNORMAL = 1;
    private const short SW_SHOWNOACTIVATE = 4;

    /// <summary>
    /// Builds a Win32 CREATE_UNICODE_ENVIRONMENT block sorted ordinal-case-insensitively
    /// without mutating the current process's global environment variables.
    /// </summary>
    public static string BuildUnicodeEnvironmentBlock(
        bool headless,
        IDictionary<string, string>? environmentVariables,
        System.Collections.IDictionary? baseEnvironment = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        System.Collections.IDictionary sourceEnv = baseEnvironment ?? Environment.GetEnvironmentVariables();

        foreach (System.Collections.DictionaryEntry de in sourceEnv)
        {
            string? key = de.Key?.ToString();
            string? val = de.Value?.ToString();
            if (!string.IsNullOrEmpty(key) && val != null)
            {
                merged[key] = val;
            }
        }

        merged["KONGOR_HEADLESS"] = headless ? "1" : "0";

        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value != null)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }
        }

        var sortedVars = new List<string>(merged.Count);
        foreach (var kvp in merged)
        {
            sortedVars.Add($"{kvp.Key}={kvp.Value}");
        }
        sortedVars.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("\0", sortedVars) + "\0\0";
    }

    /// <summary>
    /// Starts an interactive GUI process explicitly targeted at WinSta0\Default.
    /// In headless mode or when stealFocus is false, prevents the launched process
    /// from stealing desktop focus from the active user window.
    /// </summary>
    public static Process? StartInteractiveProcess(
        string executablePath,
        string arguments,
        string workingDirectory,
        bool headless = false,
        bool stealFocus = false,
        IDictionary<string, string>? environmentVariables = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            IntPtr previousForeground = IntPtr.Zero;
            bool lockedFocus = false;
            IntPtr envBlockPtr = IntPtr.Zero;
            try
            {
                if (!stealFocus)
                {
                    previousForeground = GetForegroundWindow();
                    lockedFocus = LockSetForegroundWindow(LSFW_LOCK);
                }

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"WinSta0\Default";
                si.dwFlags = STARTF_USESHOWWINDOW;

                if (headless)
                {
                    si.dwFlags |= STARTF_USEPOSITION;
                    si.dwX = -10000;
                    si.dwY = -10000;
                    si.wShowWindow = SW_SHOWNOACTIVATE;
                }
                else
                {
                    si.wShowWindow = stealFocus ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE;
                }

                string fullBlock = BuildUnicodeEnvironmentBlock(headless, environmentVariables);
                envBlockPtr = Marshal.StringToHGlobalUni(fullBlock);

                    var pi = new PROCESS_INFORMATION();
                    string cmdLine = $"\"{executablePath}\" {arguments}";

                    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
                    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
                    uint creationFlags = CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT;

                    bool success = CreateProcess(
                        null,
                        cmdLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        creationFlags,
                        envBlockPtr,
                        workingDirectory,
                        ref si,
                        out pi
                    );

                    if (!success)
                    {
                        success = CreateProcess(
                            null,
                            cmdLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            CREATE_UNICODE_ENVIRONMENT,
                            envBlockPtr,
                            workingDirectory,
                            ref si,
                            out pi
                        );
                    }

                    if (success)
                    {
                        CloseHandle(pi.hThread);
                        CloseHandle(pi.hProcess);
                        Process proc = Process.GetProcessById(pi.dwProcessId);

                    // Focus Guardian: if focus was locked, monitor for up to 3 seconds
                    // to ensure focus does not get stolen by delayed window creation
                    if (!stealFocus && previousForeground != IntPtr.Zero)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                for (int i = 0; i < 15; i++)
                                {
                                    await Task.Delay(200).ConfigureAwait(false);
                                    if (proc.HasExited) break;

                                    IntPtr current = GetForegroundWindow();
                                    if (current != IntPtr.Zero && current != previousForeground && current == proc.MainWindowHandle)
                                    {
                                        SetForegroundWindow(previousForeground);
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                LockSetForegroundWindow(LSFW_UNLOCK);
                            }
                        });
                    }
                    else if (lockedFocus)
                    {
                        LockSetForegroundWindow(LSFW_UNLOCK);
                    }

                    return proc;
                }
            }
            catch
            {
                // Fallback to standard Process.Start below
            }
            finally
            {
                if (envBlockPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(envBlockPtr);
                }
                if (lockedFocus && (stealFocus || previousForeground == IntPtr.Zero))
                {
                    LockSetForegroundWindow(LSFW_UNLOCK);
                }
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = headless,
            WindowStyle = headless ? ProcessWindowStyle.Hidden : (stealFocus ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized)
        };
        psi.EnvironmentVariables["KONGOR_HEADLESS"] = headless ? "1" : "0";
        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }
        return Process.Start(psi);
    }
}
