// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.PatcherController;
using KONGOR.MasterServer.Handlers.Patch;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Models.RequestResponse.Patch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class PatcherControllerAndHandlersTests
{
    private readonly Mock<IDatabase> _redisDatabaseMock = new();

    private PatcherController CreateController()
    {
        OperationalConfiguration config = new()
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "http://127.0.0.1:55555",
                PrimaryPatchURL = "http://127.0.0.1:55555/patch/",
                SecondaryPatchURL = "http://127.0.0.1:55555/patch/"
            }
        };

        return new PatcherController(
            NullLogger<PatcherController>.Instance,
            _redisDatabaseMock.Object,
            Options.Create(config));
    }

    [Fact]
    public void PatchDetails_ConstructionInvariants_RejectTransposedOrMalformedSha1AndSize()
    {
        // Protects: PatchDetails construction-time validation so ManifestArchiveSHA1Hash and ManifestArchiveSizeInBytes
        // can never be transposed (as previously occurred on 'mac' in PatchHandlers.cs).
        Action createWithNumericHash = () => _ = new PatchDetails
        {
            DistributionIdentifier = "mac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "3933009",
            ManifestArchiveSizeInBytes = "ce18408b94a14a968736bb39213daac9a09e4026",
            Latest = true
        };
        createWithNumericHash.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(PatchDetails.ManifestArchiveSHA1Hash));

        Action createWithHexSize = () => _ = new PatchDetails
        {
            DistributionIdentifier = "mac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "ce18408b94a14a968736bb39213daac9a09e4026",
            ManifestArchiveSizeInBytes = "ce18408b94a14a968736bb39213daac9a09e4026",
            Latest = true
        };
        createWithHexSize.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(PatchDetails.ManifestArchiveSizeInBytes));

        Action createWithZeroSize = () => _ = new PatchDetails
        {
            DistributionIdentifier = "mac",
            Version = "5.0.0",
            FullVersion = "5.0.0.0",
            ManifestArchiveSHA1Hash = "ce18408b94a14a968736bb39213daac9a09e4026",
            ManifestArchiveSizeInBytes = "0",
            Latest = true
        };
        createWithZeroSize.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(PatchDetails.ManifestArchiveSizeInBytes));
    }

    [Fact]
    public void AllDistributionVersions_SatisfySha1AndSizeInvariants_AndMatchMacGroundTruth()
    {
        IReadOnlyList<PatchDetails> allEntries = PatchHandlers.GetAllDistributionVersions();
        allEntries.Should().NotBeEmpty();

        foreach (PatchDetails entry in allEntries)
        {
            entry.DistributionIdentifier.Should().NotBeNullOrWhiteSpace();
            entry.Version.Should().NotBeNullOrWhiteSpace();
            entry.FullVersion.Should().NotBeNullOrWhiteSpace();
            entry.ManifestArchiveSHA1Hash.Should().MatchRegex("^[0-9a-f]{40}$");
            long.TryParse(entry.ManifestArchiveSizeInBytes, NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                .Should().BeTrue($"ManifestArchiveSizeInBytes for {entry.DistributionIdentifier} ({entry.Version}) must be a base-10 integer");
            size.Should().BeGreaterThan(100_000L);
        }

        PatchDetails? macLatest = PatchHandlers.GetLatestClientPatchDetails("mac");
        macLatest.Should().NotBeNull();
        macLatest!.ManifestArchiveSHA1Hash.Should().Be("ce18408b94a14a968736bb39213daac9a09e4026");
        macLatest.ManifestArchiveSizeInBytes.Should().Be("3933009");

        PatchDetails? mac410 = PatchHandlers.GetClientPatchDetails("mac", "4.10.1.0");
        mac410.Should().NotBeNull();
        mac410!.ManifestArchiveSHA1Hash.Should().Be("ce18408b94a14a968736bb39213daac9a09e4026");
        mac410.ManifestArchiveSizeInBytes.Should().Be("3933009");
    }

    [Fact]
    public void SupportedClientDistributions_EveryOsHasLatestAndLegacyEntriesInPatchHandlers()
    {
        // Protects against PatcherController allowing an OS in SupportedClientDistributions that returns null
        // from PatchHandlers.GetLatestClientPatchDetails(os) and triggers an unhandled HTTP 500.
        PatcherController.SupportedClientDistributions.Should().NotBeEmpty();

        foreach ((string os, string arch) in PatcherController.SupportedClientDistributions)
        {
            arch.Should().NotBeNullOrWhiteSpace();
            PatchDetails? latest = PatchHandlers.GetLatestClientPatchDetails(os);
            latest.Should().NotBeNull($"Supported distribution ({os}, {arch}) must have a Latest=true entry in PatchHandlers");

            PatchDetails? v410 = PatchHandlers.GetClientPatchDetails(os, "4.10.1.0");
            v410.Should().NotBeNull($"Supported distribution ({os}, {arch}) must resolve legacy 4.10.1.0 metadata");
        }
    }

    [Fact]
    public void OnDiskManifestArchives_WhenPresentInCheckout_MatchPatchHandlersSha1AndByteLength()
    {
        string? repoRoot = TryFindRepoRoot();
        repoRoot.Should().NotBeNull();

        (string Distribution, string RelativePath)[] expectedDiskManifests =
        [
            ("wac", Path.Combine("games", "client", "4.10.1.manifest.xml.zip.disabled")),
            ("lac", Path.Combine("games", "client", "linux-x64", "4.10.1.0.manifest.xml.zip.disabled")),
            ("mac", Path.Combine("games", "client", "macos-x64", "4.10.1.0.manifest.xml.zip.disabled")),
            ("was-crIac6LASwoafrl8FrOa", Path.Combine("games", "server", "4.10.1.manifest.xml.zip")),
            ("las-crIac6LASwoafrl8FrOa", Path.Combine("games", "server", "linux-x64", "4.10.1.0.manifest.xml.zip.disabled"))
        ];

        foreach ((string distribution, string relativePath) in expectedDiskManifests)
        {
            string fullPath = Path.Combine(repoRoot!, relativePath);
            if (!File.Exists(fullPath))
            {
                // Cleanroom CI checkouts may omit hydrated games/ archives; verify all files that exist on disk.
                continue;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            string actualSha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            string actualSize = bytes.Length.ToString(CultureInfo.InvariantCulture);

            PatchDetails? latest = PatchHandlers.GetLatestClientPatchDetails(distribution);
            latest.Should().NotBeNull();
            latest!.ManifestArchiveSHA1Hash.Should().Be(actualSha1, $"On-disk manifest '{relativePath}' SHA-1 must match PatchHandlers for '{distribution}'");
            latest.ManifestArchiveSizeInBytes.Should().Be(actualSize, $"On-disk manifest '{relativePath}' byte size must match PatchHandlers for '{distribution}'");
        }
    }

    [Theory]
    [InlineData("universal-64")]
    [InlineData("arm64")]
    [InlineData("x86_64")]
    public async Task LatestPatch_MacClientArchitectures_ReturnsSerializedPhpWithCorrectSha1AndSize(string architecture)
    {
        PatcherController controller = CreateController();
        LatestPatchRequestForm form = new()
        {
            OperatingSystem = "mac",
            Architecture = architecture,
            CurrentPatchVersion = "4.10.1.0"
        };

        IActionResult actionResult = await controller.LatestPatch(form, CancellationToken.None);

        OkObjectResult ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        string serialized = ok.Value.Should().BeOfType<string>().Subject;

        serialized.Should().Contain("ce18408b94a14a968736bb39213daac9a09e4026");
        serialized.Should().Contain("3933009");
        serialized.Should().Contain(architecture);
    }

    [Fact]
    public async Task LatestPatch_UnsupportedMacServerDistribution_ReturnsBadRequestInsteadOfHttp500()
    {
        PatcherController controller = CreateController();
        LatestPatchRequestForm form = new()
        {
            OperatingSystem = "mas-crIac6LASwoafrl8FrOa",
            Architecture = "x86_64",
            CurrentPatchVersion = "4.10.1.0"
        };

        IActionResult actionResult = await controller.LatestPatch(form, CancellationToken.None);

        actionResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("S2 Games/Heroes of Newerth/4.10.1.0/mac/universal-64")]
    [InlineData("S2 Games/Heroes of Newerth/4.10.1.0/mac/arm64")]
    [InlineData("S2 Games/Heroes of Newerth/4.10.1.0/wac/x86_64")]
    [InlineData("S2 Games/Heroes of Newerth/4.10.1.0/lac/x86-biarch")]
    public void UserAgentRegex_MatchesMacUniversal64AndArm64Clients(string userAgent)
    {
        Log.UserAgentRegex().IsMatch(userAgent).Should().BeTrue();
    }

    private static string? TryFindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Kongor.sln")) ||
                Directory.Exists(Path.Combine(dir, "src", "services", "master-server")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }
}
