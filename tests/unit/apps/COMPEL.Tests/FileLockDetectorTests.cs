// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Utilities;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the lock detector against the one process whose locks the test controls: itself.
/// </summary>
public sealed class FileLockDetectorTests
{
    [Fact]
    public void The_Current_Process_Is_Reported_When_It_Holds_A_File_Open()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compel-lock-{Guid.NewGuid():N}.bin");

        try
        {
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(path);

                lockingProcesses.Select(lockingProcess => lockingProcess.ProcessID).Should().Contain(Environment.ProcessId);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
