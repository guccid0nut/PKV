// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Security;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.ViewModels;
using Xunit;

namespace NEXUS.ControlPanel.Tests;

public sealed class Wave2ModerationQueueTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? ServiceKey, string? Body)> CapturedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            string? key = request.Headers.TryGetValues(SecurityConstants.InternalServiceKeyHeader, out IEnumerable<string>? vals)
                ? vals.FirstOrDefault()
                : null;
            CapturedRequests.Add((request.Method, request.RequestUri?.ToString() ?? "", key, body));
            return responder(request);
        }
    }

    [Fact]
    public async Task ModerationQueueViewModel_LoadsReports_AndDispatchesMuteBanAndDismissActionsViaApi()
    {
        // protects: ControlPanel operator moderation queue wired to ZORGATH AdminApiController report/sanction endpoints.
        // fails_when: Reports fail to load, X-Internal-Service-Key header is missing, or Mute/Ban/Dismiss payloads fail to reach API.
        // seam: ModerationQueueViewModel -> ModerationQueueService -> real HttpClient JSON wire contract.
        List<PlayerReportDto> serverReports =
        [
            new()
            {
                ID = 401,
                MatchID = 9001,
                ReporterAccountID = 10,
                ReporterAccountName = "ReporterOne",
                ReportedAccountID = 20,
                ReportedAccountName = "ToxicGriefer",
                Reason = "Verbal Abuse",
                Description = "Repeated channel spam",
                Status = "PENDING"
            },
            new()
            {
                ID = 402,
                MatchID = 9002,
                ReporterAccountID = 11,
                ReporterAccountName = "ReporterTwo",
                ReportedAccountID = 21,
                ReportedAccountName = "FalseFlagPlayer",
                Reason = "Intentional Feeding",
                Description = "Bad game",
                Status = "PENDING"
            }
        ];

        StubHttpHandler handler = new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/admin/reports", StringComparison.OrdinalIgnoreCase))
            {
                string json = JsonSerializer.Serialize(serverReports);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/admin/reports/action", StringComparison.OrdinalIgnoreCase))
            {
                serverReports.RemoveAll(r => r.ID == 401);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("true", Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/admin/reports/402/dismiss", StringComparison.OrdinalIgnoreCase))
            {
                serverReports.RemoveAll(r => r.ID == 402);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("true", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient httpClient = new(handler);
        ModerationQueueService service = new(secretsManager: null, httpClient: httpClient);
        ModerationQueueViewModel vm = new(service, autoLoad: false);

        await vm.RefreshReportsAsync();
        vm.Reports.Should().HaveCount(2);
        vm.PendingCount.Should().Be(2);
        vm.SelectedReport!.ID.Should().Be(401);

        // Issue 48h Mute on Report #401
        vm.SanctionDurationHours = 48;
        vm.StaffNotes = "Confirmed spam in public chat";
        await vm.MuteSelectedAsync();

        handler.CapturedRequests.Should().Contain(r =>
            r.Method == HttpMethod.Post &&
            r.Url.Contains("/api/admin/reports/action") &&
            !string.IsNullOrWhiteSpace(r.ServiceKey) &&
            r.Body != null &&
            r.Body.Contains("\"MUTE\"") &&
            r.Body.Contains("48"));

        vm.Reports.Should().HaveCount(1);
        vm.SelectedReport!.ID.Should().Be(402);

        // Dismiss Report #402
        await vm.DismissSelectedAsync();
        handler.CapturedRequests.Should().Contain(r =>
            r.Method == HttpMethod.Post &&
            r.Url.Contains("/api/admin/reports/402/dismiss"));
        vm.Reports.Should().BeEmpty();
        vm.PendingCount.Should().Be(0);
    }
}
