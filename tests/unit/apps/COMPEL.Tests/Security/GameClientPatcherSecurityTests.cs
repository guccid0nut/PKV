// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using ASPIRE.Common.Patching;
using COMPEL.Services.Patching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests.Security;

public sealed class GameClientPatcherSecurityTests
{
    [Theory]
    [InlineData("../escaped.dll")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    [InlineData("game/../../../outside.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task AuditAndApplyPatch_WhenManifestContainsPathTraversal_BlocksWithSecurityException(string maliciousRelativePath)
    {
        // protects: GameClientPatcherService filesystem boundary during manifest audit and differential binary patch application.
        // fails_when: Path.Combine allows '..' traversal or rooted/drive paths to read or overwrite files outside clientDirectory.
        // seam: GameClientPatcherService.AuditClientDirectoryAsync / ApplyPatchToFileAsync -> ResolveSafeClientFilePath.

        BinaryDeltaPatchEngine deltaEngine = new();
        GameClientPatcherService patcher = new(deltaEngine, NullLogger<GameClientPatcherService>.Instance);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_patcher_sec_{Guid.NewGuid():N}");
        string clientDir = Path.Combine(tempRoot, "client");
        Directory.CreateDirectory(clientDir);

        try
        {
            string dummySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            PatchManifest manifest = new(
                GameVersion: "4.10.2.0",
                Variant: "client-win64",
                Channel: "release",
                Timestamp: 1726700000,
                Files: [new PatchFileEntry(maliciousRelativePath, 64, dummySha)],
                Deltas: []);

            InvalidOperationException auditEx = await Assert.ThrowsAsync<InvalidOperationException>(
                () => patcher.AuditClientDirectoryAsync(clientDir, manifest));
            Assert.Contains("[SECURITY-ALERT]", auditEx.Message);

            OutdatedFileAction action = new(
                RelativePath: maliciousRelativePath,
                Status: FilePatchStatus.Missing,
                CurrentSha256: null,
                TargetSha256: dummySha,
                TargetSizeBytes: 64,
                DeltaUrl: null);

            using MemoryStream stream = new([1, 2, 3]);
            InvalidOperationException applyEx = await Assert.ThrowsAsync<InvalidOperationException>(
                () => patcher.ApplyPatchToFileAsync(clientDir, action, stream));
            Assert.Contains("[SECURITY-ALERT]", applyEx.Message);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
