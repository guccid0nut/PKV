// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Serialization;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LiveTelemetryAndCliOverrideTests
{
    private sealed class BlockingFakeHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Host1Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowHost1ToFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Host2Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.Contains("host1.test", StringComparison.OrdinalIgnoreCase))
            {
                Host1Started.TrySetResult(true);
                await AllowHost1ToFinish.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                LiveTelemetryData data1 = new()
                {
                    OnlinePlayers = 111,
                    MatchesInProgress = 11,
                    ServerStatus = MasterServerStatus.Active
                };
                string json1 = JsonSerializer.Serialize(data1, WillowmakerJsonSerializerContext.Default.LiveTelemetryData);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json1, Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("host2.test", StringComparison.OrdinalIgnoreCase))
            {
                LiveTelemetryData data2 = new()
                {
                    OnlinePlayers = 222,
                    MatchesInProgress = 22,
                    ServerStatus = MasterServerStatus.Active
                };
                string json2 = JsonSerializer.Serialize(data2, WillowmakerJsonSerializerContext.Default.LiveTelemetryData);
                HttpResponseMessage resp2 = new(HttpStatusCode.OK)
                {
                    Content = new StringContent(json2, Encoding.UTF8, "application/json")
                };
                Host2Finished.TrySetResult(true);
                return resp2;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task RapidStartPolling_SupercededTaskDoesNotFaultOrOverwriteFreshTelemetry()
    {
        // protects: Single-owner CTS cancellation and generation-guarded telemetry updates preventing PeriodicTimer races and stale endpoint state overwriting.
        // fails_when: Rapid back-to-back StartPolling calls share a PeriodicTimer, fault unobserved, or allow a delayed previous response to overwrite CurrentData.
        // seam: LiveTelemetryService.StartPolling, Interlocked.Exchange(_cts), and pollGeneration guard.

        BlockingFakeHandler fakeHandler = new();
        using LiveTelemetryService service = new(
            logger: _ => { },
            policyDirectory: null,
            httpHandler: fakeHandler,
            pollInterval: TimeSpan.FromMilliseconds(50));

        TaskCompletionSource<LiveTelemetryData> telemetryUpdated2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnTelemetryUpdated += data =>
        {
            if (data.OnlinePlayers == 222)
            {
                telemetryUpdated2.TrySetResult(data);
            }
        };

        // 1. Start polling host1 - this starts a request that will block in fakeHandler
        service.StartPolling("host1.test:55555");
        Task? task1 = service.ActivePollingTask;
        Assert.NotNull(task1);

        // Wait until host1 request has started
        await fakeHandler.Host1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 2. Immediately switch to host2
        service.StartPolling("host2.test:55555");
        Task? task2 = service.ActivePollingTask;
        Assert.NotNull(task2);
        Assert.NotSame(task1, task2);

        // 3. Now let host1 complete
        fakeHandler.AllowHost1ToFinish.TrySetResult(true);

        // Wait for host2 telemetry update
        LiveTelemetryData received = await telemetryUpdated2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(222, received.OnlinePlayers);

        // Allow task1 to settle
        try
        {
            await task1.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Neither task should be faulted
        Assert.False(task1.IsFaulted, $"Task 1 should not fault, but had exception: {task1.Exception}");
        Assert.False(task2.IsFaulted, $"Task 2 should not fault, but had exception: {task2.Exception}");

        // CurrentData must reflect host2 (222 online players), never stale host1 (111 online players)
        Assert.Equal(222, service.CurrentData.OnlinePlayers);
        Assert.Equal(22, service.CurrentData.MatchesInProgress);
        Assert.Equal(MasterServerStatus.Active, service.CurrentData.ServerStatus);
    }

    [Fact]
    public void CliMasterServerOverride_PreservesExistingSettingsOnDisk_UntilUserManuallyChangesDropdown()
    {
        // protects: CLI --masterserver argument takes effect in session without destructively clobbering user's persistent launcher_settings.json preference.
        // fails_when: Launching with --masterserver permanently overwrites the saved MasterServerAddress upon exit.
        // seam: MainViewModel.HasActiveCliMasterServerOverride and MainViewModel.SaveLauncherSettings.

        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor-willow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Write an initial settings file configured for newbs.io
            LauncherSettingsModel initialSettings = new()
            {
                MasterServerAddress = EndpointPairRegistry.MasterServerNewbsIo,
                SelectedCDNAddress = EndpointPairRegistry.CdnNewbsIo
            };
            LauncherSettingsService.Save(initialSettings, tempDir);

            // 2. Instantiate MainViewModel and simulate CLI override
            MainViewModel vm = new();
            vm.LoadLauncherSettings(tempDir);

            // Simulate CLI override applied
            const string cliOverride = "localhost:55555";
            vm.LoadedMasterServerAddress = cliOverride;
            vm.HasActiveCliMasterServerOverride = true;

            // 3. Save settings while CLI override is active
            vm.SaveLauncherSettings(tempDir);

            // 4. Assert disk settings STILL have newbs.io
            LauncherSettingsModel reloaded = LauncherSettingsService.Load(tempDir);
            Assert.Equal(EndpointPairRegistry.MasterServerNewbsIo, reloaded.MasterServerAddress);
            Assert.Equal(EndpointPairRegistry.CdnNewbsIo, reloaded.SelectedCDNAddress);

            // 5. If user deliberately changes dropdown in UI (not during initial view restore), CLI override clears
            List<ComboBoxItem> items =
            [
                new ComboBoxItem { Content = EndpointPairRegistry.MasterServerLocalOfflineAuto },
                new ComboBoxItem { Content = EndpointPairRegistry.MasterServerNewbsIo }
            ];
            vm.RegisterMasterServerComboItems(items);
            vm.MasterServerAddress = items[0]; // User manually picks Local/Offline Auto

            Assert.False(vm.HasActiveCliMasterServerOverride);

            // Now SaveLauncherSettings should save the user's explicit choice
            vm.SaveLauncherSettings(tempDir);
            LauncherSettingsModel reloadedAfterManual = LauncherSettingsService.Load(tempDir);
            Assert.Equal(EndpointPairRegistry.MasterServerLocalOfflineAuto, reloadedAfterManual.MasterServerAddress);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class RateLimitedPrimaryHandler : HttpMessageHandler
    {
        private int _primaryCallCount;
        public List<string> RequestedUrls { get; } = [];
        public TaskCompletionSource<bool> Saw429Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            lock (RequestedUrls)
            {
                RequestedUrls.Add(url);
            }

            if (url.Contains("primary.kongor.test", StringComparison.OrdinalIgnoreCase))
            {
                int call = Interlocked.Increment(ref _primaryCallCount);
                if (call == 1)
                {
                    LiveTelemetryData okData = new()
                    {
                        OnlinePlayers = 77,
                        MatchesInProgress = 7,
                        ServerStatus = MasterServerStatus.Active
                    };
                    string json = JsonSerializer.Serialize(okData, WillowmakerJsonSerializerContext.Default.LiveTelemetryData);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    });
                }

                Saw429Response.TrySetResult(true);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task FetchTelemetry_WhenPrimaryReturns429TooManyRequests_BacksOffWithoutCascadingToFallbackEndpoints()
    {
        RateLimitedPrimaryHandler handler = new();
        using LiveTelemetryService service = new(
            logger: _ => { },
            policyDirectory: null,
            httpHandler: handler,
            pollInterval: TimeSpan.FromMilliseconds(40));

        service.StartPolling("primary.kongor.test:55555");
        await handler.Saw429Response.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.StopPolling();

        List<string> snapshot;
        lock (handler.RequestedUrls)
        {
            snapshot = [.. handler.RequestedUrls];
        }

        Assert.All(snapshot, u => Assert.Contains("primary.kongor.test", u, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot, u => u.Contains("patcher.php", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MasterServerStatus.Active, service.CurrentData.ServerStatus);
        Assert.Equal(77, service.CurrentData.OnlinePlayers);
    }
}
