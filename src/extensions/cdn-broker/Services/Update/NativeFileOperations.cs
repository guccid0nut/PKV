// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeFileOperations.cs
// System: NEXUS CDN Broker - Low-Level OS File Operations & Shadow-Copy Primitives
// Subsystem: Strategic Initiative [SR-06] - Transactional Self-Updater
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WILLOWMAKER.Core.Services.Update;

/// <summary>
///     Low-level native OS primitives for atomic file movement, shadow-copy renaming,
///     and deferred reboot cleanup across Windows, Linux, and macOS.
/// </summary>
public static class NativeFileOperations
{
    [Flags]
    public enum MoveFileFlags : uint
    {
        None = 0x00000000,
        ReplaceExisting = 0x00000001,
        CopyAllowed = 0x00000002,
        DelayUntilReboot = 0x00000004,
        WriteThrough = 0x00000008,
        CreateHardlink = 0x00000010,
        FailIfNotTrackable = 0x00000020
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExNative(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileNative(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    ///     Renames or moves a file using native Win32 <c>MoveFileExW</c>.
    ///     On Windows, this allows renaming actively running executables and loaded DLLs within the same volume.
    ///     On Linux/macOS, delegates to atomic POSIX file system renames.
    /// </summary>
    public static bool MoveFileAtomic(string sourcePath, string destinationPath, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found for atomic move: {sourcePath}", sourcePath);

        if (OperatingSystem.IsWindows())
        {
            uint flags = (uint)MoveFileFlags.WriteThrough;
            if (overwrite)
            {
                flags |= (uint)MoveFileFlags.ReplaceExisting;
            }

            bool success = MoveFileExNative(sourcePath, destinationPath, flags);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                // If direct move with replace failed, try non-atomic fallback via File.Move
                try
                {
                    File.Move(sourcePath, destinationPath, overwrite);
                    return true;
                }
                catch
                {
                    throw new Win32Exception(error, $"Failed to move '{sourcePath}' to '{destinationPath}' (Win32 Error: {error})");
                }
            }
            return true;
        }

        // POSIX systems (Linux/macOS) natively allow renaming open inodes
        File.Move(sourcePath, destinationPath, overwrite);
        return true;
    }

    /// <summary>
    ///     Registers a file to be deleted upon the next system reboot or restart using
    ///     Win32 <c>MoveFileExW(..., NULL, MOVEFILE_DELAY_UNTIL_REBOOT)</c>.
    ///     On non-Windows systems, attempts an immediate deletion or logs a warning.
    /// </summary>
    public static bool ScheduleDeleteOnReboot(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (OperatingSystem.IsWindows())
        {
            // lpNewFileName is NULL when deleting on reboot
            bool success = MoveFileExNative(filePath, null, (uint)MoveFileFlags.DelayUntilReboot);
            return success;
        }

        // On POSIX, unlink removes the directory entry immediately while open descriptors remain valid
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Determines whether a file sharing violation or locked handle error has occurred.
    /// </summary>
    public static bool IsSharingViolation(Exception ex)
    {
        if (ex is Win32Exception win32Ex)
        {
            // 5 = ERROR_ACCESS_DENIED, 32 = ERROR_SHARING_VIOLATION, 183 = ERROR_ALREADY_EXISTS
            return win32Ex.NativeErrorCode is 5 or 32 or 183;
        }

        if (ex is IOException ioEx)
        {
            // HRESULT 0x80070020 = ERROR_SHARING_VIOLATION (The process cannot access the file because it is being used by another process)
            // HRESULT 0x80070005 = ERROR_ACCESS_DENIED (Often returned when attempting to write to an active executable)
            int hResult = ioEx.HResult;
            return hResult == unchecked((int)0x80070020) || hResult == unchecked((int)0x80070005);
        }

        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Gets the number of hard links pointing to the given file.
    ///     Returns 0 if the file does not exist.
    ///     On Windows, queries <c>BY_HANDLE_FILE_INFORMATION.nNumberOfLinks</c>.
    ///     On Linux/macOS, invokes <c>/usr/bin/stat</c> with <c>-c %h</c> (Linux) or <c>-f %l</c> (macOS).
    /// </summary>
    public static uint GetHardLinkCount(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            return 0;

        if (OperatingSystem.IsWindows())
        {
            IntPtr handle = CreateFileNative(
                filePath,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
            {
                return 1;
            }

            try
            {
                if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                {
                    return info.nNumberOfLinks;
                }

                return 1;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                using System.Diagnostics.Process proc = new();
                proc.StartInfo.FileName = "/usr/bin/stat";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;

                if (OperatingSystem.IsLinux())
                {
                    proc.StartInfo.ArgumentList.Add("-c");
                    proc.StartInfo.ArgumentList.Add("%h");
                }
                else
                {
                    proc.StartInfo.ArgumentList.Add("-f");
                    proc.StartInfo.ArgumentList.Add("%l");
                }

                proc.StartInfo.ArgumentList.Add(filePath);
                proc.Start();

                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);

                if (uint.TryParse(output, out uint linkCount))
                {
                    return linkCount;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[NativeFileOperations] POSIX stat hardlink probe failed for '{filePath}', defaulting to 1: {ex.Message}");
            }
        }

        return 1;
    }

    /// <summary>
    ///     Creates a hard link between an existing file and a new file path.
    /// </summary>
    public static bool CreateHardLink(string newFileName, string existingFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingFileName);

        if (OperatingSystem.IsWindows())
        {
            return CreateHardLinkNative(newFileName, existingFileName, IntPtr.Zero);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                using System.Diagnostics.Process proc = new();
                proc.StartInfo.FileName = "/bin/ln";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.ArgumentList.Add(existingFileName);
                proc.StartInfo.ArgumentList.Add(newFileName);
                proc.Start();
                proc.WaitForExit(2000);
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[NativeFileOperations] POSIX ln hardlink creation failed ('{existingFileName}' -> '{newFileName}'): {ex.Message}");
                return false;
            }
        }

        return false;
    }
}
