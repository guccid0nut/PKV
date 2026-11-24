// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.Patch;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public sealed class SourceGeneratedSerializerTests
{
    [Fact]
    public void PatchDetailsForResponse_Implements_IPhpSerializable()
    {
        PatchDetailsForResponse patch = new()
        {
            PatchVersion = "4.10.1",
            LatestPatchVersion = "4.10.1",
            OperatingSystem = "wac",
            Architecture = "x86_64",
            LatestManifestArchiveSHA1Hash = "abc123hash",
            LatestManifestArchiveSizeInBytes = "1000",
            PrimaryDownloadURL = "http://cdn.nexus.com/patch",
            SecondaryDownloadURL = "http://cdn2.nexus.com/patch"
        };

        Assert.NotNull(patch);
    }

    [Fact]
    public void PatchDetailsForResponse_Produces_Expected_Output()
    {
        PatchDetailsForResponse patch = new()
        {
            PatchVersion = "4.10.1",
            LatestPatchVersion = "4.10.1",
            OperatingSystem = "wac",
            Architecture = "x86_64",
            LatestManifestArchiveSHA1Hash = "abc123hash",
            LatestManifestArchiveSizeInBytes = "1000",
            PrimaryDownloadURL = "http://cdn.nexus.com/patch",
            SecondaryDownloadURL = "http://cdn2.nexus.com/patch"
        };

        string output = PhpSerializer.SerializeToString(patch);

        Assert.Contains("s:7:\"version\";s:6:\"4.10.1\";", output);
        Assert.Contains("s:2:\"os\";s:3:\"wac\";", output);
        Assert.Contains("s:4:\"arch\";s:6:\"x86_64\";", output);
    }
}
