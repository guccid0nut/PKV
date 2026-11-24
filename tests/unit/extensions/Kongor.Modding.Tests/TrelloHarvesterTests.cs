// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Modding.Harvester;
using Kongor.Modding.Harvester.Downloader;
using Kongor.Modding.Harvester.Manifest;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Harvester.Resolvers;
using Kongor.Modding.Harvester.Scraper;
using Xunit;

namespace Kongor.Modding.Tests;

public class TrelloHarvesterTests : IDisposable
{
    private readonly string _tempDir;

    public TrelloHarvesterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorHarvesterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore */
            }
    }

    [Fact]
    public async Task DropboxResolver_WhenInvoked_RewritesSharingUrlsToDirectDownload()
    {
        DropboxResolver resolver = new();
        HttpClient client = new();

        string url1 = "https://www.dropbox.com/s/f5d91sspzwu6wks/Team-aggro%20Tower%20Effect.honmod?dl=0";
        resolver.CanResolve(url1).Should().BeTrue();
        ResolvedDownloadTarget target1 = await resolver.ResolveAsync(url1, client);
        target1.DirectDownloadUrl.Should().Contain("dl=1");
        target1.SuggestedFileName.Should().Be("Team-aggro Tower Effect.honmod");
        target1.HostType.Should().Be(DownloadHostType.Dropbox);

        string url2 =
            "https://www.dropbox.com/scl/fi/u14tuhbw2tkjcxephy5ft/SmartcastPreset.honmod?rlkey=40qprzl8x6k8f7x1fitbmklcl";
        ResolvedDownloadTarget target2 = await resolver.ResolveAsync(url2, client);
        target2.DirectDownloadUrl.Should().Contain("dl=1");
        target2.DirectDownloadUrl.Should().Contain("rlkey=40qprzl8x6k8f7x1fitbmklcl");
        target2.SuggestedFileName.Should().Be("SmartcastPreset.honmod");

        string url3 = "https://dl.dropboxusercontent.com/s/xyz/mod.honmod";
        resolver.CanResolve(url3).Should().BeTrue();
    }

    [Fact]
    public async Task GitHubReleaseResolver_WhenInvoked_NormalizesReleaseAndRawUrls()
    {
        GitHubReleaseResolver resolver = new();
        HttpClient client = new();

        string releaseUrl =
            "https://github.com/mrhappyasthma/HoN-Buy-Back-Gold-Mod/releases/download/Latest/bbgold.honmod";
        resolver.CanResolve(releaseUrl).Should().BeTrue();
        ResolvedDownloadTarget target1 = await resolver.ResolveAsync(releaseUrl, client);
        target1.DirectDownloadUrl.Should().Be(releaseUrl);
        target1.SuggestedFileName.Should().Be("bbgold.honmod");
        target1.HostType.Should().Be(DownloadHostType.GitHubRelease);

        string rawUrl =
            "https://github.com/SoulInfernoDE/translate-heroes-of-newerth-to-german/raw/honmod/german/Deutsch.honmod";
        ResolvedDownloadTarget target2 = await resolver.ResolveAsync(rawUrl, client);
        target2.DirectDownloadUrl.Should()
            .Be(
                "https://raw.githubusercontent.com/SoulInfernoDE/translate-heroes-of-newerth-to-german/honmod/german/Deutsch.honmod");
        target2.SuggestedFileName.Should().Be("Deutsch.honmod");
    }

    [Fact]
    public async Task GoogleDriveResolver_WhenInvoked_ExtractsFileIdAndCreatesExportUrl()
    {
        GoogleDriveResolver resolver = new();
        HttpClient client = new(new StubHttpMessageHandler());

        string url1 = "https://drive.google.com/u/0/uc?id=1LQAjpSoaOUhpXrlP06BG6m9sPCd54wQX&export=download";
        resolver.CanResolve(url1).Should().BeTrue();
        ResolvedDownloadTarget target1 = await resolver.ResolveAsync(url1, client);
        target1.DirectDownloadUrl.Should().Contain("id=1LQAjpSoaOUhpXrlP06BG6m9sPCd54wQX");
        target1.DirectDownloadUrl.Should().Contain("confirm=t");
        target1.HostType.Should().Be(DownloadHostType.GoogleDrive);

        string url2 = "https://drive.google.com/file/d/1ABCxyz789_test/view?usp=sharing";
        ResolvedDownloadTarget target2 = await resolver.ResolveAsync(url2, client);
        target2.DirectDownloadUrl.Should()
            .Be("https://drive.google.com/uc?export=download&id=1ABCxyz789_test&confirm=t");
        target2.SuggestedFileName.Should().Be("gdrive_1ABCxyz789_test.honmod");
    }

    [Fact]
    public async Task MediaFireResolver_WhenInvoked_ExtractsFileNameFromUrl()
    {
        MediaFireResolver resolver = new();
        HttpClient client = new(new StubHttpMessageHandler());

        string url = "http://www.mediafire.com/file/d7nugfrl85eopl4/release_hero_selection_sounds.honmod/file";
        resolver.CanResolve(url).Should().BeTrue();
        ResolvedDownloadTarget target = await resolver.ResolveAsync(url, client);
        target.SuggestedFileName.Should().Be("release_hero_selection_sounds.honmod");
        target.HostType.Should().Be(DownloadHostType.MediaFire);
    }

    [Fact]
    public async Task MailRuResolver_WhenInvoked_ExtractsFolderAndFileKeys()
    {
        MailRuResolver resolver = new();
        HttpClient client = new(new StubHttpMessageHandler());

        string url = "https://cloud.mail.ru/public/mXkp/hDyzFqxPY";
        resolver.CanResolve(url).Should().BeTrue();
        ResolvedDownloadTarget target = await resolver.ResolveAsync(url, client);
        target.SuggestedFileName.Should().Be("mailru_hDyzFqxPY.honmod");
        target.HostType.Should().Be(DownloadHostType.MailRu);
    }

    [Fact]
    public async Task TrelloAttachmentResolver_WhenInvoked_IdentifiesTrelloAttachmentUrls()
    {
        TrelloAttachmentResolver resolver = new();
        HttpClient client = new(new StubHttpMessageHandler());

        string url = "https://trello.com/1/cards/5a3cb042/attachments/5a3cb055/download/CustomMod.honmod";
        resolver.CanResolve(url).Should().BeTrue();
        ResolvedDownloadTarget target = await resolver.ResolveAsync(url, client);
        target.SuggestedFileName.Should().Be("CustomMod.honmod");
        target.HostType.Should().Be(DownloadHostType.TrelloAttachment);
    }

    [Fact]
    public async Task DirectHttpResolver_WhenInvoked_ResolvesDirectDownloadUrls()
    {
        DirectHttpResolver resolver = new();
        HttpClient client = new(new StubHttpMessageHandler());

        string url = "https://ape3000.com/~anakonda/honmods/BardUIpp.honmod";
        resolver.CanResolve(url).Should().BeTrue();
        ResolvedDownloadTarget target = await resolver.ResolveAsync(url, client);
        target.SuggestedFileName.Should().Be("BardUIpp.honmod");
        target.HostType.Should().Be(DownloadHostType.DirectHttp);
    }

    [Fact]
    public void MultiHostDownloadResolver_WhenInvoked_IdentifiesHostTypesAccurately()
    {
        MultiHostDownloadResolver resolver = new();

        resolver.IdentifyHost("https://www.dropbox.com/s/123/mod.honmod").Should().Be(DownloadHostType.Dropbox);
        resolver.IdentifyHost("https://github.com/owner/repo/releases/download/v1/mod.honmod").Should()
            .Be(DownloadHostType.GitHubRelease);
        resolver.IdentifyHost("https://drive.google.com/uc?id=123").Should().Be(DownloadHostType.GoogleDrive);
        resolver.IdentifyHost("http://www.mediafire.com/file/123/mod.honmod/file").Should()
            .Be(DownloadHostType.MediaFire);
        resolver.IdentifyHost("https://cloud.mail.ru/public/a/b").Should().Be(DownloadHostType.MailRu);
        resolver.IdentifyHost("https://trello.com/1/cards/1/attachments/2/download/m.honmod").Should()
            .Be(DownloadHostType.TrelloAttachment);
        resolver.IdentifyHost("https://example.com/downloads/mod.honmod").Should().Be(DownloadHostType.DirectHttp);
        resolver.IdentifyHost("not-a-url").Should().Be(DownloadHostType.Unknown);
    }

    [Fact]
    public async Task MultiHostDownloadResolver_WithInvalidInput_ThrowsOnEmptyUrl()
    {
        MultiHostDownloadResolver resolver = new();
        Func<Task> act = async () => await resolver.ResolveAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TrelloBoardScraper_ParsesFullCatalog_WithAll99Mods()
    {
        string? repoRoot = TrelloBoardScraper.FindRepoRoot();
        string catalogPath = TrelloBoardScraper.ResolveDefaultCatalogPath(repoRoot)
            ?? Path.Combine(Directory.GetCurrentDirectory(), "trello_mods_catalog.json");
        if (!File.Exists(catalogPath)) return;

        File.Exists(catalogPath).Should().BeTrue("Catalog file must exist for full community testing.");

        TrelloBoardScraper scraper = new();
        List<CommunityModEntry> mods = await scraper.ScrapeBoardAsync(catalogPath);

        mods.Count.Should().Be(99, "Total community mods cataloged across 6 categories must be 99.");

        // Check category distributions
        mods.Count(m => m.Category == "Working mods").Should().Be(65);
        mods.Count(m => m.Category == "Broken/Outdated mods").Should().Be(7);
        mods.Count(m => m.Category == "Incompatible mods").Should().Be(13);
        mods.Count(m => m.Category == "Pending...").Should().Be(3);
        mods.Count(m => m.Category == "Old sources").Should().Be(6);
        mods.Count(m => m.Category == "In develop").Should().Be(5);

        // Check key mods
        CommunityModEntry? smartcast = mods.FirstOrDefault(m => m.Name == "SmartcastPreset");
        smartcast.Should().NotBeNull();
        smartcast!.Author.Should().Be("Chaca");
        smartcast.Category.Should().Be("Working mods");
        smartcast.ResolvedHost.Should().Be(DownloadHostType.Dropbox);

        CommunityModEntry? bbgold = mods.FirstOrDefault(m => m.Name == "BB gold");
        bbgold.Should().NotBeNull();
        bbgold!.ResolvedHost.Should().Be(DownloadHostType.GitHubRelease);

        CommunityModEntry? towerDenier = mods.FirstOrDefault(m => m.Name == "Tower Denier");
        towerDenier.Should().NotBeNull();
        towerDenier!.ResolvedHost.Should().Be(DownloadHostType.MailRu);
    }

    [Fact]
    public void TrelloBoardScraper_ParsesMarkdownDescriptions_WithScreenshotsFiltered()
    {
        TrelloBoardScraper scraper = new();
        TrelloCard card = new()
        {
            Id = "test_card_123",
            Name = "Test Custom UI Mod (v2.5)",
            Desc =
                "## Test Custom UI Mod\n\n### [[Download]](https://www.dropbox.com/s/12345/TestCustomUI.honmod?dl=1 \"‌\")\n\nMade by KongorMaster\n\n**Screenshots:**\n![screenshot.png](https://www.dropbox.com/s/999/image.png?dl=1)",
            Labels =
            [
                new TrelloLabel { Name = "Works fine" },
                new TrelloLabel { Name = "User Interface" }
            ]
        };

        CommunityModEntry entry = scraper.ParseCard(card, "Working mods");
        entry.Name.Should().Be("Test Custom UI Mod (v2.5)");
        entry.Author.Should().Be("KongorMaster");
        entry.Version.Should().Be("2.5");
        entry.Status.Should().Be("Works fine, User Interface");
        entry.PrimaryDownloadUrl.Should().Be("https://www.dropbox.com/s/12345/TestCustomUI.honmod?dl=1");
        entry.AllDownloadUrls.Should().NotContain(u => u.Url.Contains("image.png"));
    }

    [Fact]
    public async Task CommunityCatalogGenerator_WithValidArguments_GeneratesValidJsonAndMarkdown()
    {
        List<CommunityModEntry> entries =
        [
            new()
            {
                Id = "card1",
                Name = "TestMod1",
                Category = "Working mods",
                Author = "DevA",
                Version = "1.0.0",
                Status = "Works fine",
                PrimaryDownloadUrl = "https://www.dropbox.com/s/1/TestMod1.honmod?dl=1",
                ResolvedDownloadUrl = "https://www.dropbox.com/s/1/TestMod1.honmod?dl=1",
                ResolvedHost = DownloadHostType.Dropbox,
                FileSizeBytes = 10240,
                Sha256Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                Desc = "Test mod description 1"
            },
            new()
            {
                Id = "card2",
                Name = "TestMod2",
                Category = "Broken/Outdated mods",
                Author = "DevB",
                Version = "0.9.0",
                Status = "Broken",
                PrimaryDownloadUrl = "",
                ResolvedHost = DownloadHostType.Unknown,
                Desc = "Broken mod without binary"
            }
        ];

        CommunityCatalogGenerator generator = new();
        string jsonPath = Path.Combine(_tempDir, "test_catalog.json");
        string mdPath = Path.Combine(_tempDir, "test_catalog.md");

        await generator.SaveManifestsAsync(entries, jsonPath, mdPath);

        File.Exists(jsonPath).Should().BeTrue();
        File.Exists(mdPath).Should().BeTrue();

        string json = await File.ReadAllTextAsync(jsonPath);
        List<CommunityModEntry>? deserialized =
            JsonSerializer.Deserialize<List<CommunityModEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        deserialized.Should().NotBeNull();
        deserialized!.Count.Should().Be(2);

        string md = await File.ReadAllTextAsync(mdPath);
        md.Should().Contain("# HoN Community Mods Repository — Master Catalog Manifest");
        md.Should().Contain("Working mods (1 Mods)");
        md.Should().Contain("Broken/Outdated mods (1 Mods)");
        md.Should().Contain("TestMod1");
        md.Should().Contain("TestMod2");
        md.Should().Contain("DevA");
    }

    [Fact]
    public async Task StreamingDownloadManager_WhenInvoked_CalculatesSha256AndDetectsCacheHit()
    {
        // 1. Create a dummy valid .honmod zip file
        string modContent = "Hello Kongor Modding Test Stream";
        byte[] modBytes = Encoding.UTF8.GetBytes(modContent);

        byte[] fakeHonmodZip;
        using (MemoryStream ms = new())
        {
            using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
            {
                ZipArchiveEntry entry = archive.CreateEntry("mod.xml");
                using Stream s = entry.Open();
                byte[] manifestBytes = Encoding.UTF8.GetBytes("<mod name=\"TestStreamMod\" version=\"1.0\"/>");
                s.Write(manifestBytes, 0, manifestBytes.Length);
            }

            fakeHonmodZip = ms.ToArray();
        }

        string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(fakeHonmodZip));

        // Mock HttpMessageHandler returning fakeHonmodZip
        MockHttpMessageHandler mockHandler = new(fakeHonmodZip);
        HttpClient mockClient = new(mockHandler);

        MultiHostDownloadResolver resolver = new();
        StreamingDownloadManager manager = new(resolver, mockClient);

        CommunityModEntry entryToDownload = new()
        {
            Id = "stream1",
            Name = "StreamTestMod",
            Category = "Working mods",
            PrimaryDownloadUrl = "https://www.dropbox.com/s/123/StreamTestMod.honmod?dl=1"
        };

        // First Download: downloads from mock HTTP stream
        DownloadedModInfo info1 = await manager.DownloadModAsync(entryToDownload, _tempDir);
        info1.WasCached.Should().BeFalse();
        info1.FileSizeBytes.Should().Be(fakeHonmodZip.Length);
        info1.Sha256Hash.Should().Be(expectedSha256);
        info1.IsValidHonMod.Should().BeTrue();
        info1.FileName.Should().Be("StreamTestMod.honmod");
        File.Exists(info1.LocalPath).Should().BeTrue();

        // Second Download: should be a Cache Hit
        DownloadedModInfo info2 = await manager.DownloadModAsync(entryToDownload, _tempDir);
        info2.WasCached.Should().BeTrue();
        info2.FileSizeBytes.Should().Be(fakeHonmodZip.Length);
        info2.Sha256Hash.Should().Be(expectedSha256);

        // Third Download with OverwriteExisting = true: should re-download
        DownloadedModInfo info3 = await manager.DownloadModAsync(entryToDownload, _tempDir, true);
        info3.WasCached.Should().BeFalse();
        info3.FileSizeBytes.Should().Be(fakeHonmodZip.Length);
    }

    [Fact]
    public void StreamingDownloadManager_ValidatesCorruptedArchive_ReturnsFalse()
    {
        string corruptPath = Path.Combine(_tempDir, "corrupt.honmod");
        File.WriteAllBytes(corruptPath, [0x00, 0x01, 0x02, 0x03]);

        bool isValid = StreamingDownloadManager.ValidateArchive(corruptPath, out string msg);
        isValid.Should().BeFalse();
        msg.Should().Contain("Not a valid ZIP/honmod archive");
    }

    [Fact]
    public async Task TrelloModHarvester_DryRunHarvest_ExecutesEndToEnd()
    {
        string? repoRoot = TrelloBoardScraper.FindRepoRoot();
        string catalogPath = TrelloBoardScraper.ResolveDefaultCatalogPath(repoRoot)
            ?? Path.Combine(Directory.GetCurrentDirectory(), "trello_mods_catalog.json");
        if (!File.Exists(catalogPath)) return;

        TrelloModHarvester harvester = new(httpClient: new HttpClient(new StubHttpMessageHandler()));
        HarvestOptions options = new()
        {
            OutputDirectory = Path.Combine(_tempDir, "dry_run_harvest"),
            DryRun = true,
            CustomCatalogPath = catalogPath,
            GenerateManifests = true
        };

        HarvestResult result = await harvester.HarvestAsync(options);

        result.Success.Should().BeTrue();
        result.TotalCataloged.Should().Be(99);
        result.TotalDiscovered.Should().Be(99);
        result.TotalSkipped.Should().Be(27);
        result.ManifestPathJson.Should().NotBeNull();
        result.ManifestPathMd.Should().NotBeNull();
        File.Exists(result.ManifestPathJson).Should().BeTrue();
        File.Exists(result.ManifestPathMd).Should().BeTrue();
    }

    [Fact]
    public async Task TrelloModHarvester_CategoryFilter_HarvestsOnlyTargetCategory()
    {
        string? repoRoot = TrelloBoardScraper.FindRepoRoot();
        string catalogPath = TrelloBoardScraper.ResolveDefaultCatalogPath(repoRoot)
            ?? Path.Combine(Directory.GetCurrentDirectory(), "trello_mods_catalog.json");
        if (!File.Exists(catalogPath)) return;

        TrelloModHarvester harvester = new(httpClient: new HttpClient(new StubHttpMessageHandler()));
        HarvestOptions options = new()
        {
            OutputDirectory = Path.Combine(_tempDir, "category_harvest"),
            CategoryFilter = "Broken/Outdated mods",
            DryRun = true,
            CustomCatalogPath = catalogPath,
            GenerateManifests = true
        };

        HarvestResult result = await harvester.HarvestAsync(options);

        result.Success.Should().BeTrue();
        result.TotalCataloged.Should().Be(7);
        result.Catalog.Should().OnlyContain(m => m.Category == "Broken/Outdated mods");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private class MockHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        private readonly byte[] _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };
            return Task.FromResult(response);
        }
    }
}
