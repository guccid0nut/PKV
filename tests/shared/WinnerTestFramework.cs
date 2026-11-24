// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

global using ASPIRE.Tests.Infrastructure;

using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ASPIRE.Tests.Infrastructure;

public static class WinnerBanner
{
    public const string AnsiBanner = "\e[1;38;2;57;255;20m  🏆 100% Passed - A WINNER IS YOU\e[0m";
    public const string PlainBanner = "  🏆 100% Passed - A WINNER IS YOU";

    public static async Task PrintAsync(int lines = 1, TextWriter? writer = null)
    {
        writer ??= ConsoleSilencer.OriginalError ?? Console.Error;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Ignore console encoding setup errors
        }

        for (int i = 0; i < lines; i++)
        {
            await writer.WriteLineAsync(AnsiBanner);
        }

        await writer.FlushAsync();
    }
}

public sealed class WinnerTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
    {
        return new WinnerTestFrameworkExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
    }
}

public sealed class WinnerTestFrameworkExecutor(
    AssemblyName assemblyName,
    ISourceInformationProvider sourceInformationProvider,
    IMessageSink diagnosticMessageSink)
    : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
{
    protected override async void RunTestCases(
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
    {
        try
        {
            using WinnerTestAssemblyRunner assemblyRunner = new(
                TestAssembly,
                testCases,
                DiagnosticMessageSink,
                executionMessageSink,
                executionOptions);

            await assemblyRunner.RunAsync();
        }
        catch (Exception ex)
        {
            DiagnosticMessageSink.OnMessage(new DiagnosticMessage($"Exception running test cases: {ex}"));
        }
    }
}

public sealed class WinnerTestAssemblyRunner(
    ITestAssembly testAssembly,
    IEnumerable<IXunitTestCase> testCases,
    IMessageSink diagnosticMessageSink,
    IMessageSink executionMessageSink,
    ITestFrameworkExecutionOptions executionOptions)
    : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
{
    protected override async Task<RunSummary> RunTestCollectionsAsync(
        IMessageBus messageBus,
        CancellationTokenSource cancellationTokenSource)
    {
        RunSummary summary;
        using (new ConsoleSilencer())
        {
            summary = await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
        }

        if (summary.Failed == 0 && summary.Total > 0)
        {
            int manualCount = NexusTestEnvironment.ManualBypassedCount;
            if (manualCount > 0)
            {
                TextWriter errorWriter = ConsoleSilencer.OriginalError ?? Console.Error;
                await errorWriter.WriteLineAsync($" ({summary.Total - manualCount} automated, {manualCount} manual gated)");
            }

            // Print exactly 1 line of the banner when this test section passes
            await WinnerBanner.PrintAsync(1);
        }

        return summary;
    }
}
