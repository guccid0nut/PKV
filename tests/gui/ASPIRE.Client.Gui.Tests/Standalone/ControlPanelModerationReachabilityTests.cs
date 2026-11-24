// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Client.Gui.Tests.Willowmaker;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.ControlPanel.GUI;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.ViewModels;
using NEXUS.ControlPanel.GUI.Views;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.Standalone;

public sealed class ControlPanelModerationReachabilityTests : IClassFixture<AvaloniaHeadlessAppFixture>
{
    public ControlPanelModerationReachabilityTests(AvaloniaHeadlessAppFixture _)
    {
        AvaloniaHeadlessAppFixture.EnsureInitialized();
    }

    [Fact]
    public async Task ModerationQueueView_MountsAndRendersActionControlsInAvaloniaHeadless()
    {
        // protects: Real Avalonia visual-tree reachability of the Control Panel Moderation Queue view and controls.
        // fails_when: ModerationQueueView.axaml fails XAML compile/load, ViewLocator breaks, or Mute/Ban/Dismiss buttons are missing.
        // seam: Avalonia.Headless Window -> ViewLocator -> ModerationQueueView + ModerationQueueViewModel.
        Mock<IModerationQueueService> mockService = new();
        mockService.Setup(s => s.GetReportsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PlayerReportDto
                {
                    ID = 777,
                    ReporterAccountName = "Reporter",
                    ReportedAccountName = "Offender",
                    Reason = "Hate Speech",
                    Description = "Channel abuse",
                    Status = "PENDING"
                }
            ]);

        ModerationQueueViewModel vm = new(mockService.Object, autoLoad: false);
        await vm.RefreshReportsAsync();

        AvaloniaHeadlessAppFixture.RunOnUiThread(() =>
        {
            ViewLocator locator = new();
            Control? builtView = locator.Build(vm);
            builtView.Should().BeOfType<ModerationQueueView>();

            Window host = new()
            {
                Width = 1020,
                Height = 760,
                Content = builtView,
                DataContext = vm
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                List<Button> buttons = host.GetVisualDescendants().OfType<Button>().ToList();
                buttons.Should().Contain(b => b.Content != null && b.Content.ToString()!.Contains("Mute", StringComparison.Ordinal));
                buttons.Should().Contain(b => b.Content != null && b.Content.ToString()!.Contains("Ban", StringComparison.Ordinal));
                buttons.Should().Contain(b => b.Content != null && b.Content.ToString()!.Contains("Dismiss", StringComparison.Ordinal));
                vm.PendingCount.Should().Be(1);
            }
            finally
            {
                host.Close();
            }
        });
    }
}
