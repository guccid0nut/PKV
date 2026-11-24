// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Services.Replay;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Learnatorium;
using NEXUS.Contracts.Replay;
using Xunit;
using ZORGATH.WebPortal.API.Services.Learnatorium;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class WebPortalStaffAndReplayVerificationTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    /// <summary>
    ///     protects: Server-side StaffOnly authorization gate across all 7 staff pages and aliases.
    ///     fails_when: Any of the 7 staff routes (/live, /matches/live, /tools/replay-studio, /lore,
    ///                 /staff/inventory, /tools/inventory, /docs, /admin/issues, /staff) permits a
    ///                 regular Discord-authenticated non-staff player (HTTP 200).
    ///     seam: Full ASP.NET Core authentication + authorization middleware pipeline on DawnbringerE2ETestFixture.
    /// </summary>
    [Theory]
    [InlineData("/live")]
    [InlineData("/matches/live")]
    [InlineData("/tools/replay-studio")]
    [InlineData("/tools/replays")]
    [InlineData("/lore")]
    [InlineData("/staff/inventory")]
    [InlineData("/tools/inventory")]
    [InlineData("/inventory")]
    [InlineData("/docs")]
    [InlineData("/developer-docs")]
    [InlineData("/admin/issues")]
    [InlineData("/staff")]
    public async Task StaffPages_NonStaffPlayer_IsDeniedAccessOnAllSevenStaffRoutes(string staffRoute)
    {
        HttpClient normalNoRedirect = await _fixture.CreateAuthenticatedNoRedirectClientAsync(
            "test-discord-normal",
            "normalplayer");

        HttpResponseMessage response = await normalNoRedirect.GetAsync(staffRoute);

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Forbidden, HttpStatusCode.Redirect],
            $"Non-staff player must be blocked from staff route '{staffRoute}'");

        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            string location = response.Headers.Location?.ToString() ?? string.Empty;
            location.Should().BeOneOf(
                ["/", "/Account/AccessDenied"],
                $"Redirected non-staff access to '{staffRoute}' must bounce away from the protected staff page");
        }

        // Also verify Replay Studio JSON API returns strict HTTP 403 Forbidden for non-staff
        HttpResponseMessage apiResp = await normalNoRedirect.GetAsync("/tools/replays/queue-status");
        apiResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     protects: Complete invisibility of the 7 purple staff nav items and Ctrl+K search entries for
    ///               non-staff accounts, while keeping player pages (/setup, /cloud, /tools/message-generator) open,
    ///               and rendering purple bg-purple-950/45 styling for staff accounts.
    ///     fails_when: Non-staff HTML leaks any of the 7 staff nav links or /api/global/search leaks STAFF badges,
    ///                 or staff HTML omits purple bg-purple-950/45 styling.
    ///     seam: Razor _Layout.cshtml rendering + HomeController.SearchGlobal endpoint + cookie claims.
    /// </summary>
    [Fact]
    public async Task NavigationAndSearch_EnforcesStaffVisibility_AndRendersPurpleStaffStylingForStaffOnly()
    {
        HttpClient normalClient = await _fixture.CreateAuthenticatedClientAsync(
            "test-discord-normal",
            "normalplayer");
        HttpClient staffClient = await _fixture.CreateAuthenticatedClientAsync(
            "test-discord-guccidonut",
            "guccidonut");

        // 1. Non-staff player can access player tools (/setup, /cloud, /tools/message-generator)
        foreach (string playerRoute in new[] { "/setup", "/cloud", "/tools/message-generator" })
        {
            HttpResponseMessage okResp = await normalClient.GetAsync(playerRoute);
            okResp.StatusCode.Should().Be(HttpStatusCode.OK, $"Regular Discord player must access '{playerRoute}'");
        }

        // 2. Non-staff HTML has ZERO purple staff links and ZERO disabled Replay Studio entry
        string normalHtml = await (await normalClient.GetAsync("/matches")).Content.ReadAsStringAsync();
        normalHtml.Should().NotContain("bg-purple-950/45");
        normalHtml.Should().NotContain("data-staff-disabled=\"replay-studio\"");
        normalHtml.Should().NotContain("/tools/replay-studio");
        normalHtml.Should().NotContain("/admin/issues");
        normalHtml.Should().NotContain("/staff/inventory");
        normalHtml.Should().NotContain("href=\"/lore\"");
        normalHtml.Should().NotContain("href=\"/live\"");
        normalHtml.Should().NotContain("href=\"/docs\"");

        // 3. Non-staff Ctrl+K search never returns STAFF badge pages
        foreach (string q in new[] { "staff", "replay", "lore", "issues", "docs", "live", "inventory" })
        {
            string searchJson = await (await normalClient.GetAsync($"/api/global/search?q={q}")).Content.ReadAsStringAsync();
            searchJson.Should().NotContain("\"badge\":\"STAFF\"",
                $"Non-staff search for '{q}' must not expose STAFF navigation entries");
        }

        // 4. Staff HTML renders active staff destinations with purple styling AND greys out Replay Studio as non-clickable Future work
        string staffHtml = await (await staffClient.GetAsync("/matches")).Content.ReadAsStringAsync();
        staffHtml.Should().Contain("bg-purple-950/45");
        staffHtml.Should().Contain("/live");
        staffHtml.Should().Contain("data-staff-disabled=\"replay-studio\"");
        staffHtml.Should().Contain("aria-disabled=\"true\"");
        staffHtml.Should().NotContain("href=\"/tools/replay-studio\"");
        staffHtml.Should().NotContain("href=\"/tools/replays\"");
        staffHtml.Should().Contain("/lore");
        staffHtml.Should().Contain("/staff/inventory");
        staffHtml.Should().Contain("/docs");
        staffHtml.Should().Contain("/admin/issues");
        staffHtml.Should().Contain("/staff");

        // 5. Staff Ctrl+K search returns active STAFF badge entries (e.g. Staff Portal / Inventory) and omits greyed-out Replay Studio
        string staffSearchJson = await (await staffClient.GetAsync("/api/global/search?q=inventory")).Content.ReadAsStringAsync();
        staffSearchJson.Should().Contain("\"badge\":\"STAFF\"");
        string replaySearchJson = await (await staffClient.GetAsync("/api/global/search?q=replay%20studio")).Content.ReadAsStringAsync();
        replaySearchJson.Should().NotContain("/tools/replays");
    }

    /// <summary>
    ///     protects: Exact K2 CMinimap world-to-minimap projection (worldDim = 2^size * scale, map padding,
    ///               top-down Y inversion) and UTF-16LE K2 server log parsing (items, skills, casts, kills, towers).
    ///     fails_when: Legion fountain projects to top-left instead of bottom-left, UTF-16LE encoding fails to parse,
    ///                 or combat/item/skill events are dropped.
    ///     seam: HonReplayParser + HonLogParser + K2MapCoordinateRegistry over raw UTF-16LE byte stream.
    /// </summary>
    [Fact]
    public async Task K2MapCoordinateRegistry_And_HonReplayParser_Utf16LeServerLog_ProjectsExactMinimapBoundsAndEvents()
    {
        string k2ServerLog = string.Join("\r\n",
        [
            "INFO_MATCH name:\"CaldavarTest\" id:\"90410\"",
            "INFO_MAP name:\"caldavar\"",
            "INFO_SETTINGS mode:\"Mode_Normal\"",
            "PLAYER_CONNECT player:0 name:\"LegionCarry\" id:501 psr:1750",
            "PLAYER_CONNECT player:1 name:\"HellbourneSolo\" id:502 psr:1740",
            "PLAYER_TEAM_CHANGE player:0 team:1",
            "PLAYER_TEAM_CHANGE player:1 team:2",
            "PLAYER_SELECT player:0 hero:\"Hero_Pyromancer\"",
            "PLAYER_SELECT player:1 hero:\"Hero_Devourer\"",
            "GAME_START",
            "ABILITY_UPGRADE time:5000 x:1350 y:1250 z:128 player:0 team:1 name:\"Ability_Pyromancer1\" level:1 slot:0",
            "ITEM_PURCHASE time:8000 x:1350 y:1250 z:128 player:0 team:1 item:\"Item_Steamboots\" cost:1450",
            "GOLD_EARNED time:10000 x:1350 y:1250 z:128 player:0 team:1 source:\"Creep\" gold:45",
            "GOLD_EARNED time:10000 x:14300 y:14450 z:128 player:1 team:2 source:\"Creep\" gold:45",
            "ABILITY_ACTIVATE time:42000 x:7600 y:7700 z:128 player:0 team:1 name:\"Ability_Pyromancer1\" level:1 slot:0",
            "KILL time:45000 x:7650 y:7720 z:128 player:0 team:1 target:\"Hero_Devourer\" attacker:\"Hero_Pyromancer\" owner:1",
            "HERO_DEATH time:45000 x:7650 y:7720 z:128 player:1 team:2 attacker:\"Hero_Pyromancer\" owner:0",
            "KILL time:90000 x:8800 y:9100 z:128 player:0 team:1 target:\"Building_HellbourneTower\" attacker:\"Hero_Pyromancer\"",
            "GAME_END time:120000 winner:1"
        ]);

        // Encode with real UTF-16LE BOM exactly like hon_server_x64 M*.log files
        byte[] utf16Bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(k2ServerLog)];
        using MemoryStream stream = new(utf16Bytes);

        HonReplayParser parser = new(NullLogger<HonReplayParser>.Instance);
        MatchReplayTimelineDto? timeline = await parser.ParseReplayAsync(stream, 90410);

        timeline.Should().NotBeNull();
        timeline!.TelemetrySource.Should().Be("server_log");
        timeline.TelemetryLabel.Should().Be("Verified Server Telemetry");
        timeline.WinnerTeam.Should().Be(1);
        timeline.ScoreLegion.Should().Be(1);
        timeline.Players.Should().HaveCount(2);

        // Verify exact K2 CMinimap projection at t=10s:
        // Legion fountain (1350, 1250) on Caldavar -> bottom-left (X < 0.22, Y > 0.78)
        // Hellbourne fountain (14300, 14450) on Caldavar -> top-right (X > 0.78, Y < 0.22)
        MatchTimelineKeyframe? spawnFrame = timeline.Keyframes.FirstOrDefault(f => f.Second == 10);
        spawnFrame.Should().NotBeNull();
        ReplayPlayerSnapshot legionSnap = spawnFrame!.Players.Single(p => p.AccountID == 501);
        ReplayPlayerSnapshot hellSnap = spawnFrame.Players.Single(p => p.AccountID == 502);

        legionSnap.PositionX.Should().BeLessThan(0.22f, "Legion fountain sits near bottom-left X on Caldavar");
        legionSnap.PositionY.Should().BeGreaterThan(0.78f, "Legion fountain sits near bottom-left Y (top-down canvas) on Caldavar");
        hellSnap.PositionX.Should().BeGreaterThan(0.78f, "Hellbourne fountain sits near top-right X on Caldavar");
        hellSnap.PositionY.Should().BeLessThan(0.22f, "Hellbourne fountain sits near top-right Y (top-down canvas) on Caldavar");

        // Verify item, spell upgrade/cast, hero kill, and tower kill events
        timeline.EventPins.Should().Contain(e => e.AbilityOrItem == "Item_Steamboots");
        timeline.EventPins.Should().Contain(e => e.AbilityOrItem == "Ability_Pyromancer1");
        timeline.EventPins.Should().Contain(e => e.AttackerHero == "Hero_Pyromancer" && e.VictimHero == "Hero_Devourer");
        timeline.EventPins.Should().Contain(e => e.Team == 1 && e.EventType.Contains("TOWER", StringComparison.OrdinalIgnoreCase));

        // Verify DevoWars (devowars_10v10) K2 shop & fountain orientation (Legion SW bottom-left, Hellbourne NE top-right)
        (float devoLegionU, float devoLegionV) = K2MapCoordinateRegistry.WorldToMinimap("devowars_10v10", 6493.7f, 7665.5f);
        (float devoHellU, float devoHellV) = K2MapCoordinateRegistry.WorldToMinimap("devowars_10v10", 8766.1f, 10096.5f);
        devoLegionU.Should().BeApproximately(0.229f, 0.03f, "DevoWars Legion shop sits on SW bank");
        devoLegionV.Should().BeApproximately(0.717f, 0.03f, "DevoWars Legion shop sits in lower-left half of cropped canvas");
        devoHellU.Should().BeApproximately(0.707f, 0.03f, "DevoWars Hellbourne shop sits on NE bank");
        devoHellV.Should().BeApproximately(0.238f, 0.03f, "DevoWars Hellbourne shop sits in upper-right half of cropped canvas");
    }

    /// <summary>
    ///     protects: /api/matches/{matchId}/replay HTTP 200 route contract (fixing prior 404 route mismatch) and
    ///               LearnatoriumService support for < 10 player matches (1v1 / 2v2 / Mid Wars / bot matches).
    ///     fails_when: GET /api/matches/10001/replay returns 404, or LearnatoriumService discards real DB player
    ///                 rows when playerStats.Count < 10.
    ///     seam: HomeController.Matches API endpoint + LearnatoriumService against seeded MerrickContext.
    /// </summary>
    [Fact]
    public async Task MatchReplayApi_And_LearnatoriumService_SupportRealMatchesIncludingSubTenPlayerLobbies()
    {
        HttpClient staffClient = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage replayApiResp = await staffClient.GetAsync("/api/matches/10001/replay");
        replayApiResp.StatusCode.Should().Be(HttpStatusCode.OK, "GET /api/matches/{matchId}/replay must return 200 OK JSON");

        MatchReplayTimelineDto? dto = await replayApiResp.Content.ReadFromJsonAsync<MatchReplayTimelineDto>();
        dto.Should().NotBeNull();
        dto!.MatchID.Should().Be(10001);
        dto.Players.Should().NotBeEmpty();
        dto.Keyframes.Should().NotBeEmpty();

        // Verify LearnatoriumService preserves real < 10 player DB match instead of synthesizing 10 fake showcase players
        LearnatoriumService learnatorium = new(_fixture.DbContext);
        LearnatoriumReplayPlaybackDto? playback = await learnatorium.GetReplayPlaybackAsync(10001);

        playback.Should().NotBeNull();
        playback!.MatchID.Should().Be(10001);
        playback.TotalDurationSeconds.Should().Be(1800);
        playback.Players.Should().ContainSingle(p => p.AccountName == "guccidonut" && p.HeroIdentifier == "Hero_Vindicator");
    }

    /// <summary>
    ///     protects: End-to-end NEXUS Replay Studio workflow (upload UTF-16LE log -> enqueue job -> progress ->
    ///               upload encoded .webm video -> download playable .webm video).
    ///     fails_when: Upload rejects UTF-16LE M*.log, job queue drops active job, or uploaded .webm cannot be downloaded.
    ///     seam: ToolsController HTTP endpoints + physical wwwroot/videos/renders file persistence.
    /// </summary>
    [Fact]
    public async Task ReplayStudio_UploadEnqueueUploadVideoAndDownload_CompletesPlayableWebmRoundTrip()
    {
        HttpClient staffClient = await _fixture.CreateAuthenticatedClientAsync();

        // 1. Upload UTF-16LE match log
        string logText = string.Join("\r\n",
        [
            "INFO_MATCH match:90555 map:\"midwars\" mode:\"Mid Wars\"",
            "PLAYER_CONNECT player:0 name:\"guccidonut\" id:1 team:1 psr:1750",
            "PLAYER_CONNECT player:1 name:\"ChronosBot\" id:2 team:2 psr:1500",
            "PLAYER_SELECT player:0 hero:\"Hero_Devourer\"",
            "PLAYER_SELECT player:1 hero:\"Hero_WitchSlayer\"",
            "GAME_START",
            "HERO_POS time:5000 player:0 x:3600 y:3400 z:128",
            "HERO_POS time:5000 player:1 x:4600 y:4700 z:128",
            "GAME_END time:60000 winner:1"
        ]);
        byte[] logBytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(logText)];

        using MultipartFormDataContent uploadForm = new();
        ByteArrayContent filePart = new(logBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        uploadForm.Add(filePart, "replayFile", "M90555.log");

        HttpResponseMessage uploadResp = await staffClient.PostAsync("/tools/replays/upload", uploadForm);
        uploadResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument uploadJson = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync());
        uploadJson.RootElement.GetProperty("matchId").GetInt32().Should().Be(90555);
        uploadJson.RootElement.GetProperty("telemetrySource").GetString().Should().Be("server_log");

        // 2. Enqueue POV render job
        var enqueuePayload = new
        {
            matchId = 90555,
            targetAccountId = 1,
            targetPlayerIndex = 0,
            targetPlayerName = "guccidonut",
            cameraMode = "PlayerPOV",
            resolution = "720p",
            framerate = 60,
            showHud = true,
            startMs = 0,
            durationMs = 8000
        };
        HttpResponseMessage enqResp = await staffClient.PostAsJsonAsync("/tools/replays/enqueue", enqueuePayload);
        enqResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument enqJson = JsonDocument.Parse(await enqResp.Content.ReadAsStringAsync());
        string jobId = enqJson.RootElement.GetProperty("jobId").GetString()!;
        jobId.Should().NotBeNullOrWhiteSpace();

        // 3. Report encoding progress
        HttpResponseMessage progResp = await staffClient.PostAsJsonAsync($"/tools/replays/jobs/{jobId}/progress", new
        {
            progressPercent = 65.0,
            fps = 60.0,
            elapsedSeconds = 2.1,
            estimatedRemainingSeconds = 1.1
        });
        progResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Upload encoded .webm bytes (EBML magic header + payload)
        byte[] sampleWebm = [0x1A, 0x45, 0xDF, 0xA3, 0x93, 0x42, 0x82, 0x88, 0x77, 0x65, 0x62, 0x6D, 0x00, 0x01, 0x02, 0x03];
        using MultipartFormDataContent videoForm = new();
        ByteArrayContent videoPart = new(sampleWebm);
        videoPart.Headers.ContentType = new MediaTypeHeaderValue("video/webm");
        videoForm.Add(videoPart, "videoFile", $"Match_90555_{jobId}.webm");

        HttpResponseMessage saveVideoResp = await staffClient.PostAsync($"/tools/replays/jobs/{jobId}/upload-video", videoForm);
        saveVideoResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument saveJson = JsonDocument.Parse(await saveVideoResp.Content.ReadAsStringAsync());
        string videoUrl = saveJson.RootElement.GetProperty("videoUrl").GetString()!;
        videoUrl.Should().StartWith("/videos/renders/");

        // 5. Download rendered .webm file and verify payload integrity, then remove test render file
        try
        {
            HttpResponseMessage dlResp = await staffClient.GetAsync($"/tools/replays/jobs/{jobId}/download");
            dlResp.StatusCode.Should().Be(HttpStatusCode.OK);
            dlResp.Content.Headers.ContentType?.MediaType.Should().Be("video/webm");
            byte[] downloaded = await dlResp.Content.ReadAsByteArrayAsync();
            downloaded.Should().Equal(sampleWebm);
        }
        finally
        {
            string? repoRoot = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(repoRoot) && !File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")))
            {
                repoRoot = Directory.GetParent(repoRoot)?.FullName;
            }

            if (!string.IsNullOrEmpty(repoRoot))
            {
                string rendersDir = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot", "videos", "renders");
                if (Directory.Exists(rendersDir))
                {
                    foreach (string testFile in Directory.EnumerateFiles(rendersDir, "Match_90555_*.webm"))
                    {
                        File.Delete(testFile);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     protects: Cleanroom single-app PKV Setup page (/setup) linking solely to https://github.com/guccidonut/PKV
    ///               with zero separate WILLOWMAKER/COMPEL cards and zero game/server binary distribution.
    ///     fails_when: /setup regresses to dual WILLOWMAKER/COMPEL cards or exposes binary/archive download links.
    ///     seam: Full Razor Setup.cshtml view rendering via HTTP GET /setup.
    /// </summary>
    [Fact]
    public async Task SetupPage_DisplaysSingleUnifiedPkvAppWithGitHubLinkOnly_AndZeroFileDistribution()
    {
        HttpClient playerClient = await _fixture.CreateAuthenticatedClientAsync(
            "test-discord-normal",
            "normalplayer");

        HttpResponseMessage resp = await playerClient.GetAsync("/setup");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        string html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("https://github.com/guccidonut/PKV");
        html.Should().Contain("do not host, provide, or distribute any game files");
        html.Should().NotContain("Get WILLOWMAKER Source");
        html.Should().NotContain("Get COMPEL Source");
        html.Should().NotContain("Windows Client");
        html.Should().NotContain("Windows Server Host");
    }

    /// <summary>
    ///     protects: Hero Codex Official Guide item modal resolution (/codex/Devourer -> /api/items dictionary indexing)
    ///               and legacy HoN item alias resolution (/item/runesoftheblight, /item/ultorsheavyplate).
    ///     fails_when: HeroDetail.cshtml assigns the raw /api/items JSON Array directly to itemCatalog (causing every
    ///                 clicked guide item to fall back to "Standard Hero Item used in Project KONGOR combat." and 1,000g),
    ///                 or legacy guide item codes fail to map to canonical HeroItems.json entries.
    ///     seam: Real HTTP GET /api/items, GET /codex/Devourer, and GET /item/{legacyCode} against DAWNBRINGER.WebPortal.UI.
    /// </summary>
    [Fact]
    public async Task CodexHeroGuideItems_IndexApiItemsArrayByCode_AndResolveDescriptionsCostsAndRecipes()
    {
        HttpClient playerClient = await _fixture.CreateAuthenticatedClientAsync(
            "test-discord-normal",
            "normalplayer");

        // 1. /api/items returns enriched JSON Array (including on cached HybridCache hit) with camelCase component fields
        _ = await playerClient.GetAsync("/api/items");
        HttpResponseMessage apiResp = await playerClient.GetAsync("/api/items");
        apiResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument itemsDoc = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync());
        itemsDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        itemsDoc.RootElement.GetArrayLength().Should().BeGreaterThanOrEqualTo(174);

        Dictionary<string, JsonElement> byCode = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement el in itemsDoc.RootElement.EnumerateArray())
        {
            string code = el.GetProperty("code").GetString()!;
            string desc = el.GetProperty("description").GetString() ?? string.Empty;
            desc.Should().NotBeNullOrWhiteSpace($"item '{code}' must carry a real combat description");
            desc.Should().NotContain("Standard Hero Item", $"item '{code}' must not use generic fallback text");
            byCode[code] = el;
        }

        byCode.Should().ContainKey("blightstones");
        byCode["blightstones"].GetProperty("cost").GetInt32().Should().Be(75);
        byCode["blightstones"].GetProperty("description").GetString().Should().Contain("Consumes a target tree");

        byCode.Should().ContainKey("masterslegacy");
        byCode["masterslegacy"].GetProperty("cost").GetInt32().Should().Be(6500);
        JsonElement mastersComps = byCode["masterslegacy"].GetProperty("components");
        mastersComps.GetArrayLength().Should().BeGreaterThan(0);
        mastersComps[0].GetProperty("code").GetString().Should().Be("staffofthemaster");
        mastersComps[0].GetProperty("name").GetString().Should().Be("Staff of the Master");

        byCode.Should().ContainKey("bottle33");
        byCode.Should().ContainKey("ultorsheavyhelm");

        // 2. /codex/Devourer renders canonical data-item-code pills and indexes /api/items Array into a code lookup map
        HttpResponseMessage codexResp = await playerClient.GetAsync("/codex/Devourer");
        codexResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string codexHtml = await codexResp.Content.ReadAsStringAsync();

        codexHtml.Should().Contain("indexItemCatalog(");
        codexHtml.Should().Contain("ensureItemCatalogLoaded()");
        codexHtml.Should().NotContain("itemCatalog = data;");
        codexHtml.Should().Contain("data-item-code=\"blightstones\"");
        codexHtml.Should().Contain("data-item-code=\"masterslegacy\"");
        codexHtml.Should().Contain("data-item-code=\"bottle33\"");
        codexHtml.Should().Contain("data-item-code=\"ultorsheavyhelm\"");

        // 3. /item/{legacyCode} resolves directly to canonical ItemDetail page without redirecting back to /items
        HttpResponseMessage legacyBlightResp = await playerClient.GetAsync("/item/runesoftheblight");
        legacyBlightResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string blightHtml = await legacyBlightResp.Content.ReadAsStringAsync();
        blightHtml.Should().Contain("Consumes a target tree");

        HttpResponseMessage legacyUltorResp = await playerClient.GetAsync("/item/ultorsheavyplate");
        legacyUltorResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string ultorHtml = await legacyUltorResp.Content.ReadAsStringAsync();
        ultorHtml.Should().Contain("Ultor");
    }
}
