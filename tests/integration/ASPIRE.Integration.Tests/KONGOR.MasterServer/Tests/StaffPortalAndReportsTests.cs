// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using ZORGATH.WebPortal.API.Features.Admin;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StaffPortalAndReportsTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private static AdminApiController CreateAdminApiController(MerrickContext dbContext, IDatabase? cache = null)

    {
        AccountRepository accountRepo = new(dbContext);

        return new AdminApiController(new LocalAdminMediator(dbContext, accountRepo));
    }

    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie, Account Reporter, Account
            Target)> SetupReportTestAsync()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();

        IServiceScope scope = factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string reporterName = $"Reporter_{Guid.NewGuid().ToString("N")[..6]}";

        string targetName = $"Target_{Guid.NewGuid().ToString("N")[..6]}";


        User reporterUser = new()

        {
            EmailAddress = $"{reporterName}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new EntityRole { Name = UserRoles.User }
        };


        Account reporterAccount = new()

        {
            Name = reporterName,
            User = reporterUser,
            Type = AccountType.Normal,
            IsMain = true
        };


        User targetUser = new()

        {
            EmailAddress = $"{targetName}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new EntityRole { Name = UserRoles.User }
        };


        Account targetAccount = new()

        {
            Name = targetName,
            User = targetUser,
            Type = AccountType.Normal,
            IsMain = true
        };


        await dbContext.Users.AddRangeAsync(reporterUser, targetUser);

        await dbContext.Accounts.AddRangeAsync(reporterAccount, targetAccount);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, reporterAccount.Name);


        return (client, dbContext, cache, cookie, reporterAccount, targetAccount);
    }


    [Fact]
    public async Task StaffPortalAndReports_SubmitReport_PersistsPlayerReportToDatabase()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        Dictionary<string, string> formData = new()

        {
            ["cookie"] = cookie,

            ["reported_nickname"] = target.Name,

            ["match_id"] = "998877",

            ["reason"] = "Griefing",

            ["comment"] = "Player deliberately walked down mid and fed kills."
        };


        FormUrlEncodedContent content = new(formData);


        // Act

        HttpResponseMessage response = await client.PostAsync("/rap/report2.php", content);


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        byte[] body = await response.Content.ReadAsByteArrayAsync();

        object? deserialized = PhpDeserializer.Deserialize(body);

        PhpHashtable result = Assert.IsType<PhpHashtable>(deserialized);


        Assert.Equal("OK", result["status"]?.ToString());

        Assert.True(result.ContainsKey("report_id"));


        int reportId = int.Parse(result["report_id"]?.ToString()!);

        PlayerReport? savedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == reportId);


        Assert.NotNull(savedReport);

        Assert.Equal(reporter.Name, savedReport.ReporterAccountName);

        Assert.Equal(reporter.ID, savedReport.ReporterAccountID);

        Assert.Equal(target.Name, savedReport.ReportedAccountName);

        Assert.Equal(target.ID, savedReport.ReportedAccountID);

        Assert.Equal(998877, savedReport.MatchID);

        Assert.Equal("Griefing", savedReport.Reason);

        Assert.Equal("Player deliberately walked down mid and fed kills.", savedReport.Description);

        Assert.Equal("PENDING", savedReport.Status);
    }


    [Fact]
    public async Task SubmitReport_WithHoNClientArrayFormat_ParsesAllFieldsAccurately()

    {
        // Arrange (Exact payload structure sent by HoN client: offenderName[], match_id[], type[], body[], time[])

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        Dictionary<string, string> formData = new()

        {
            ["account_id"] = reporter.ID.ToString(),

            ["cookie"] = cookie,

            ["offenderName[]"] = target.Name,

            ["match_id[]"] = "2060568725",

            ["type[]"] = "1",

            ["body[]"] = "meanie",

            ["time[]"] = "27"
        };


        FormUrlEncodedContent content = new(formData);


        // Act

        HttpResponseMessage response = await client.PostAsync("/rap/report2.php", content);


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        byte[] body = await response.Content.ReadAsByteArrayAsync();

        object? deserialized = PhpDeserializer.Deserialize(body);

        PhpHashtable result = Assert.IsType<PhpHashtable>(deserialized);


        Assert.Equal("OK", result["status"]?.ToString());

        Assert.True(result.ContainsKey("report_id"));


        int reportId = int.Parse(result["report_id"]?.ToString()!);

        PlayerReport? savedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == reportId);


        Assert.NotNull(savedReport);

        Assert.Equal(reporter.Name, savedReport.ReporterAccountName);

        Assert.Equal(reporter.ID, savedReport.ReporterAccountID);

        Assert.Equal(target.Name, savedReport.ReportedAccountName);

        Assert.Equal(target.ID, savedReport.ReportedAccountID);

        Assert.Equal(2060568725, savedReport.MatchID);

        Assert.Equal("Verbal Abuse", savedReport.Reason);

        Assert.NotNull(savedReport.Description);

        Assert.Contains("meanie", savedReport.Description);

        Assert.Contains("27m", savedReport.Description);

        Assert.Equal("PENDING", savedReport.Status);
    }


    [Fact]
    public async Task SubmitReport_ByAccountId_ResolvesTargetAndPersists()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        Dictionary<string, string> formData = new()

        {
            ["cookie"] = cookie,

            ["reported_account_id"] = target.ID.ToString(),

            ["match_id"] = "445566",

            ["reason_id"] = "Verbal Abuse",

            ["reason_description"] = "Repeated vulgar insults in team chat."
        };


        FormUrlEncodedContent content = new(formData);


        // Act

        HttpResponseMessage response = await client.PostAsync("/rap/report.php", content);


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        byte[] body = await response.Content.ReadAsByteArrayAsync();

        object? deserialized = PhpDeserializer.Deserialize(body);

        PhpHashtable result = Assert.IsType<PhpHashtable>(deserialized);


        Assert.Equal("OK", result["status"]?.ToString());


        int reportId = int.Parse(result["report_id"]?.ToString()!);

        PlayerReport? savedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == reportId);


        Assert.NotNull(savedReport);

        Assert.Equal(reporter.Name, savedReport.ReporterAccountName);

        Assert.Equal(target.Name, savedReport.ReportedAccountName);

        Assert.Equal(target.ID, savedReport.ReportedAccountID);

        Assert.Equal(445566, savedReport.MatchID);

        Assert.Equal("Verbal Abuse", savedReport.Reason);

        Assert.Equal("Repeated vulgar insults in team chat.", savedReport.Description);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task StaffPortalAndReports_EnsureStaffAccountsAsync_GrantsIsStaffToGuccidonut()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        User gucciUser = new()

        {
            EmailAddress = "guccidonut@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new EntityRole { Name = UserRoles.User }
        };


        Account gucciAccount = new()

        {
            Name = "guccidonut",
            User = gucciUser,
            Type = AccountType.Normal,
            IsMain = true
        };


        await dbContext.Users.AddAsync(gucciUser);

        await dbContext.Accounts.AddAsync(gucciAccount);

        await dbContext.SaveChangesAsync();


        // Act

        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);


        // Assert

        Account? refreshed = await dbContext.Accounts.FirstOrDefaultAsync(a => a.Name == "guccidonut");

        Assert.NotNull(refreshed);

        Assert.True(refreshed.IsStaff);

        Assert.Equal(AccountType.Staff, refreshed.Type);
    }


    [Fact]
    public async Task StaffController_ReportsDashboard_FilteringAndResolution()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report1 = new()

        {
            ReporterAccountName = "PlayerA",

            ReportedAccountName = "GrieferX",

            MatchID = 1111,

            Reason = "AFK",

            Description = "AFK at base",

            Status = "PENDING"
        };


        PlayerReport report2 = new()

        {
            ReporterAccountName = "PlayerB",

            ReportedAccountName = "FeederY",

            MatchID = 2222,

            Reason = "Feeding",

            Description = "Running into tower",

            Status = "RESOLVED"
        };


        await dbContext.PlayerReports.AddRangeAsync(report1, report2);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Act 1: Query All Reports

        IActionResult allResult = await adminApi.GetPlayerReports(null, null, null, null, null, 1, 50);

        OkObjectResult viewResult = Assert.IsType<OkObjectResult>(allResult);

        List<PlayerReportDto> allReports = Assert.IsType<List<PlayerReportDto>>(viewResult.Value);


        Assert.True(allReports.Count >= 2);


        // Act 2: Query Filtered by PENDING

        IActionResult pendingResult = await adminApi.GetPlayerReports("PENDING", null, null, null, null, 1, 50);

        OkObjectResult pendingView = Assert.IsType<OkObjectResult>(pendingResult);

        List<PlayerReportDto> pendingReports = Assert.IsType<List<PlayerReportDto>>(pendingView.Value);

        Assert.All(pendingReports, r => Assert.Equal("PENDING", r.Status));


        // Act 3: Resolve Report 1

        IActionResult updateResult =
            await adminApi.ActionReport(new ActionReportRequestDto(report1.ID, 1, "RESOLVED",
                "Banned player for 48 hours", 48, "Banned player for 48 hours"));

        Assert.NotNull(updateResult);


        PlayerReport? updatedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report1.ID);

        Assert.NotNull(updatedReport);

        Assert.Equal("RESOLVED", updatedReport.Status);

        Assert.Equal("Banned player for 48 hours", updatedReport.StaffNotes);

        Assert.NotNull(updatedReport.TimestampReviewed);
    }


    [Fact]
    public async Task StaffJudgeReport_ApplyMute_RestrictsTargetAccount()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report = new()

        {
            ReporterAccountName = reporter.Name,

            ReporterAccountID = reporter.ID,

            ReportedAccountName = target.Name,

            ReportedAccountID = target.ID,

            MatchID = 556677,

            Reason = "Abuse",

            Description = "Foul language in chat",

            Status = "PENDING"
        };


        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Act: Apply 24 Hour Mute Judgment

        IActionResult result = await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "MUTE",
            "24h mute for abusive chat", 24, "24h mute for abusive chat"));

        Assert.NotNull(result);


        // Assert: Verify Report status and Account punishment

        PlayerReport? refreshedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);

        Account? refreshedTarget = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);


        Assert.NotNull(refreshedReport);

        Assert.NotNull(refreshedTarget);


        Assert.Equal("RESOLVED", refreshedReport.Status);

        Assert.Equal("MUTE", refreshedReport.PunishmentType);

        Assert.Equal(24, refreshedReport.PunishmentDurationHours);

        Assert.NotNull(refreshedReport.PunishmentExpiration);

        Assert.Equal("24h mute for abusive chat", refreshedReport.StaffNotes);


        Assert.True(refreshedTarget.IsMuted);

        Assert.NotNull(refreshedTarget.MuteExpiration);

        Assert.True(refreshedTarget.MuteExpiration.Value > TimeProvider.System.GetUtcNow());

        Assert.Equal("24h mute for abusive chat", refreshedTarget.MuteReason);
    }


    [Fact]
    public async Task StaffJudgeReport_ApplySuspension_SetsAccountSuspension()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report = new()

        {
            ReporterAccountName = reporter.Name,

            ReporterAccountID = reporter.ID,

            ReportedAccountName = target.Name,

            ReportedAccountID = target.ID,

            MatchID = 123999,

            Reason = "AFK",

            Description = "Left game early",

            Status = "PENDING"
        };


        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Act: Apply 72 Hour Suspension Judgment (Public matches only)

        IActionResult result =
            await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "SUSPEND",
                "3 day matchmaking suspension for AFK", 72, "3 day matchmaking suspension for AFK"));

        Assert.NotNull(result);


        // Assert

        PlayerReport? refreshedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);

        Account? refreshedTarget = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);


        Assert.NotNull(refreshedReport);

        Assert.NotNull(refreshedTarget);


        Assert.Equal("RESOLVED", refreshedReport.Status);

        Assert.Equal("SUSPEND", refreshedReport.PunishmentType);

        Assert.Equal(72, refreshedReport.PunishmentDurationHours);

        Assert.True(refreshedTarget.IsSuspended);

        Assert.NotNull(refreshedTarget.SuspensionExpiration);

        Assert.Equal("3 day matchmaking suspension for AFK", refreshedTarget.SuspensionReason);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task StaffJudgeReport_ApplyBan_DisablesAccountAndBlocksLogin()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report = new()

        {
            ReporterAccountName = reporter.Name,

            ReporterAccountID = reporter.ID,

            ReportedAccountName = target.Name,

            ReportedAccountID = target.ID,

            MatchID = 999888,

            Reason = "Cheating",

            Description = "Third party tool detected",

            Status = "PENDING"
        };


        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Act: Apply Ban Judgment

        IActionResult result = await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "BAN",
            "7-day ban for cheating", 168, "7-day ban for cheating"));

        Assert.NotNull(result);


        // Assert 1: Database state

        PlayerReport? refreshedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);

        Account? refreshedTarget = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);


        Assert.NotNull(refreshedReport);

        Assert.NotNull(refreshedTarget);


        Assert.Equal("RESOLVED", refreshedReport.Status);

        Assert.Equal("BAN", refreshedReport.PunishmentType);

        Assert.True(refreshedTarget.IsBanned);

        Assert.Equal(AccountType.Disabled, refreshedTarget.Type);


        // Assert 2: Game Client Login Attempt Blocked

        Dictionary<string, string> preAuthPayload = ClientRequestPayloads.PreAuth(target.Name);

        FormUrlEncodedContent content = new(preAuthPayload);

        HttpResponseMessage response = await client.PostAsync("/client_requester.php?f=pre_auth", content);


        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task StaffJudgeReport_Dismiss_LeavesAccountClean()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report = new()

        {
            ReporterAccountName = reporter.Name,

            ReporterAccountID = reporter.ID,

            ReportedAccountName = target.Name,

            ReportedAccountID = target.ID,

            MatchID = 112233,

            Reason = "Other",

            Description = "Player picked hero I wanted",

            Status = "PENDING"
        };


        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Act: Dismiss Report

        IActionResult result = await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "DISMISS",
            "Not a punishable offense", 0, "Not a punishable offense"));

        Assert.NotNull(result);


        // Assert

        PlayerReport? refreshedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);

        Account? refreshedTarget = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);


        Assert.NotNull(refreshedReport);

        Assert.NotNull(refreshedTarget);


        Assert.Equal("DISMISSED", refreshedReport.Status);

        Assert.Equal("DISMISS", refreshedReport.PunishmentType);

        Assert.False(refreshedTarget.IsMuted);

        Assert.False(refreshedTarget.IsSuspended);

        Assert.False(refreshedTarget.IsBanned);
    }


    [Fact]
    public async Task StaffJudgeReport_ReviseBanToMute_RestoresAccountTypeAndAppliesMute()

    {
        // Arrange

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        PlayerReport report = new()

        {
            ReporterAccountName = reporter.Name,

            ReporterAccountID = reporter.ID,

            ReportedAccountName = target.Name,

            ReportedAccountID = target.ID,

            MatchID = 554433,

            Reason = "Abuse",

            Description = "Initial ban candidate",

            Status = "PENDING"
        };


        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // Step 1: Initial Ban

        IActionResult banResult =
            await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "BAN", "Temporary 24h ban", 24,
                "Temporary 24h ban"));

        Assert.NotNull(banResult);


        Account? bannedAccount = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);

        Assert.NotNull(bannedAccount);

        Assert.True(bannedAccount.IsBanned);

        Assert.Equal(AccountType.Disabled, bannedAccount.Type);


        // Step 2: Staff reconsiders and revises to 24h MUTE

        IActionResult muteResult = await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "MUTE",
            "Downgraded to 24h mute", 24, "Downgraded to 24h mute"));

        Assert.NotNull(muteResult);


        Account? unbannedAndMuted = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);

        PlayerReport? updatedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);


        Assert.NotNull(unbannedAndMuted);

        Assert.NotNull(updatedReport);


        Assert.Equal("RESOLVED", updatedReport.Status);

        Assert.Equal("MUTE", updatedReport.PunishmentType);

        Assert.False(unbannedAndMuted.IsBanned);

        Assert.NotEqual(AccountType.Disabled, unbannedAndMuted.Type);

        Assert.Null(unbannedAndMuted.BanExpiration);

        Assert.True(unbannedAndMuted.IsMuted);

        Assert.NotNull(unbannedAndMuted.MuteExpiration);
    }


    [Fact]
    public async Task StaffPortalAndReports_StaffCreateDirectReport_AppliesSanctionWithoutMatchID()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        IActionResult result = await adminApi.DirectPunishAccount(new DirectPunishRequestDto(
            target.Name,
            1,
            "SUSPEND",
            "Direct Staff Action",
            72,
            "72h matchmaking lockout issued by staff"));


        Assert.NotNull(result);


        PlayerReport? report = await dbContext.PlayerReports.FirstOrDefaultAsync(r =>
            r.ReportedAccountName == target.Name && r.Reason == "Direct Staff Action");

        Account? updatedTarget = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);


        Assert.NotNull(report);

        Assert.NotNull(updatedTarget);


        Assert.Null(report.MatchID); // Direct sanction without match ID

        Assert.Equal("RESOLVED", report.Status);

        Assert.Equal("SUSPEND", report.PunishmentType);

        Assert.Equal(72, report.PunishmentDurationHours);

        Assert.True(updatedTarget.IsSuspended);

        Assert.NotNull(updatedTarget.SuspensionExpiration);

        Assert.Equal("72h matchmaking lockout issued by staff", updatedTarget.SuspensionReason);
    }


    [Fact]
    public async Task StaffPortalAndReports_StaffAppealReport_OverturnsSanctionsAndClearsAccountLock()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // 1. First apply a Ban

        IActionResult directResult = await adminApi.DirectPunishAccount(new DirectPunishRequestDto(
            target.Name,
            1,
            "BAN",
            "Griefing",
            168,
            "7 day ban"));

        Assert.NotNull(directResult);


        Account? bannedAccount = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);

        PlayerReport? report =
            await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ReportedAccountName == target.Name);

        Assert.NotNull(bannedAccount);

        Assert.NotNull(report);

        Assert.True(bannedAccount.IsBanned);

        Assert.Equal(AccountType.Disabled, bannedAccount.Type);


        // 2. Appeal decision

        IActionResult appealResult =
            await adminApi.DismissReport(report.ID,
                new DismissReportRequestDto(1, "Replay reviewed: intentional feed was false positive."));

        Assert.NotNull(appealResult);


        Account? exoneratedAccount = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == target.ID);

        PlayerReport? appealedReport = await dbContext.PlayerReports.FirstOrDefaultAsync(r => r.ID == report.ID);


        Assert.NotNull(exoneratedAccount);

        Assert.NotNull(appealedReport);


        Assert.Equal("APPEALED", appealedReport.Status);

        Assert.Contains("Replay reviewed", appealedReport.StaffNotes ?? "");

        Assert.False(exoneratedAccount.IsBanned);

        Assert.Equal(AccountType.Legacy, exoneratedAccount.Type);

        Assert.Null(exoneratedAccount.BanExpiration);

        Assert.Null(exoneratedAccount.BanReason);

        Assert.False(exoneratedAccount.IsSuspended);

        Assert.False(exoneratedAccount.IsMuted);
    }


    [Fact]
    public async Task StaffReportsQuery_WithReportId_ReturnsMatchingReport()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext);


        PlayerReport report = new()

        {
            ID = 1006,

            ReportedAccountID = target.ID,

            ReportedAccountName = target.Name,

            ReporterAccountID = reporter.ID,

            ReporterAccountName = reporter.Name,

            Reason = "AFK / Leaving",

            Description = "went afk (Match Time: 30m)",

            Status = "PENDING"
        };

        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        // Query with "1006"

        IActionResult result = await adminApi.GetPlayerReports(null, null, "1006", null, null);

        OkObjectResult viewResult = Assert.IsType<OkObjectResult>(result);

        List<PlayerReportDto> model = Assert.IsType<List<PlayerReportDto>>(viewResult.Value);


        Assert.NotNull(model);

        Assert.Single(model);

        Assert.Equal(1006, model[0].ID);


        // Query with "#1006"

        IActionResult resultWithHash = await adminApi.GetPlayerReports(null, null, "#1006", null, null);

        OkObjectResult viewResultHash = Assert.IsType<OkObjectResult>(resultWithHash);

        List<PlayerReportDto> modelHash = Assert.IsType<List<PlayerReportDto>>(viewResultHash.Value);


        Assert.NotNull(modelHash);

        Assert.Single(modelHash);

        Assert.Equal(1006, modelHash[0].ID);
    }


    [Fact]
    public async Task StaffGetReportDetails_BrowserRequest_ReturnsFilteredReportsView()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        PlayerReport report = new()

        {
            ID = 2005,

            ReportedAccountID = target.ID,

            ReportedAccountName = target.Name,

            ReporterAccountID = reporter.ID,

            ReporterAccountName = reporter.Name,

            Reason = "Abusive Language",

            Description = "Flamed teammates",

            Status = "PENDING"
        };

        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        IActionResult result = await adminApi.GetPlayerReportById(2005);

        OkObjectResult viewResult = Assert.IsType<OkObjectResult>(result);

        PlayerReportDto? model = viewResult.Value as PlayerReportDto;


        Assert.NotNull(model);

        Assert.Equal(2005, model.ID);
    }


    [Fact]
    public async Task StaffGetReportDetails_JsonRequest_ReturnsJsonPayload()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account target) = await SetupReportTestAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        PlayerReport report = new()

        {
            ID = 2006,

            ReportedAccountID = target.ID,

            ReportedAccountName = target.Name,

            ReporterAccountID = reporter.ID,

            ReporterAccountName = reporter.Name,

            Reason = "Cheating",

            Description = "Maphacking",

            Status = "PENDING"
        };

        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        IActionResult result = await adminApi.GetPlayerReportById(2006);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);

        PlayerReportDto? reportElem = okResult.Value as PlayerReportDto;

        Assert.NotNull(reportElem);

        Assert.Equal(2006, reportElem.ID);
    }


    [Fact]
    public async Task StaffJudgment_WhenAppliedToSubAccount_PunishesAllAssociatedAccountsUnderUser()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account mainTarget) = await SetupReportTestAsync();


        // Create a subaccount for mainTarget's User

        Account subTarget = new()

        {
            Name = "TargetSub1",
            User = mainTarget.User,
            IsMain = false,
            Type = AccountType.Legacy
        };

        await dbContext.Accounts.AddAsync(subTarget);

        await dbContext.SaveChangesAsync();


        PlayerReport report = new()

        {
            ReportedAccountID = subTarget.ID,

            ReportedAccountName = subTarget.Name,

            ReporterAccountID = reporter.ID,

            ReporterAccountName = reporter.Name,

            Reason = "Toxicity",

            Description = "Abusive chat on alt",

            Status = "PENDING"
        };

        await dbContext.PlayerReports.AddAsync(report);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        // 1. Ban subaccount -> both subaccount AND main account must be banned!

        IActionResult banResult = await adminApi.ActionReport(new ActionReportRequestDto(report.ID, 1, "BAN",
            "Banned for toxic alt", 72, "Banned for toxic alt"));

        Assert.NotNull(banResult);


        Account? refreshedSub = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == subTarget.ID);

        Account? refreshedMain = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == mainTarget.ID);


        Assert.NotNull(refreshedSub);

        Assert.NotNull(refreshedMain);

        Assert.True(refreshedSub.IsBanned);

        Assert.True(refreshedMain.IsBanned);

        Assert.Equal(AccountType.Disabled, refreshedSub.Type);

        Assert.Equal(AccountType.Disabled, refreshedMain.Type);

        Assert.NotNull(refreshedSub.BanExpiration);

        Assert.NotNull(refreshedMain.BanExpiration);


        // 2. Appeal report -> both subaccount AND main account must be restored!

        IActionResult appealResult =
            await adminApi.DismissReport(report.ID, new DismissReportRequestDto(1, "Exonerated upon review"));

        Assert.NotNull(appealResult);


        Account? exoneratedSub = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == subTarget.ID);

        Account? exoneratedMain = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == mainTarget.ID);


        Assert.NotNull(exoneratedSub);

        Assert.NotNull(exoneratedMain);

        Assert.False(exoneratedSub.IsBanned);

        Assert.False(exoneratedMain.IsBanned);

        Assert.Equal(AccountType.Legacy, exoneratedSub.Type);

        Assert.Equal(AccountType.Legacy, exoneratedMain.Type);

        Assert.Null(exoneratedSub.BanExpiration);

        Assert.Null(exoneratedMain.BanExpiration);
    }


    [Fact]
    public async Task StaffDirectReport_WhenAppliedToMain_PunishesAllSubAccounts()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, Account reporter,
            Account mainTarget) = await SetupReportTestAsync();


        Account subTarget = new()

        {
            Name = "TargetSub2",
            User = mainTarget.User,
            IsMain = false,
            Type = AccountType.Legacy
        };

        await dbContext.Accounts.AddAsync(subTarget);

        await dbContext.SaveChangesAsync();


        AdminApiController adminApi = CreateAdminApiController(dbContext, cache);


        IActionResult directResult = await adminApi.DirectPunishAccount(new DirectPunishRequestDto(
            mainTarget.Name,
            1,
            "SUSPEND",
            "Cheating",
            48,
            "48h suspension across all accounts"));

        Assert.NotNull(directResult);


        Account? refreshedSub = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == subTarget.ID);

        Account? refreshedMain = await dbContext.Accounts.FirstOrDefaultAsync(a => a.ID == mainTarget.ID);


        Assert.NotNull(refreshedSub);

        Assert.NotNull(refreshedMain);

        Assert.True(refreshedMain.IsSuspended);

        Assert.True(refreshedSub.IsSuspended);

        Assert.Equal("48h suspension across all accounts", refreshedMain.SuspensionReason);

        Assert.Equal("48h suspension across all accounts", refreshedSub.SuspensionReason);
    }
}
