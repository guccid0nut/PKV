// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class LoggingVerificationTests

{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task LoggingVerification_WhenEventsLogged_LogsAreWrittenToFile()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;


        // Arrange

        // We will check the "logs" location relative to working dir.

        string logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");


        // Act

        // Scope the factory to ensure it is disposed before we try to read the file

        {
            await using WebApplicationFactory<KONGORAssemblyMarker> factory =
                KONGORServiceProvider.CreateOrchestratedInstance("UsingLoggingTest");

            using HttpClient client = factory.CreateClient();


            // Make a request to trigger some logs (even a 404 is fine as it logs the request)

            await client.GetAsync("/health");
        }


        // Assert

        // Allow a small delay for the async file sink to flush and handle release

        await Task.Delay(20);


        // Check if directory exists

        Assert.True(Directory.Exists(logDir));


        // Use directory enumeration to find the logs and get the latest one

        DirectoryInfo directoryInfo = new(logDir);

        FileInfo? latestLogFile = directoryInfo.GetFiles("master_server*.log")
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();


        Assert.NotNull(latestLogFile);


        // Read file using FileShare.ReadWrite to avoid locking issues if the process is still holding it

        string content = "";

        for (int i = 0; i < 10; i++)

        {
            await using (FileStream fs = File.Open(latestLogFile.FullName, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite))

            using (StreamReader sr = new(fs))

            {
                content = await sr.ReadToEndAsync();
            }


            if (!string.IsNullOrWhiteSpace(content)) break;


            await Task.Delay(20);
        }


        Assert.NotEmpty(content);


        // No cleanup - allow logs to persist as per user request/environment stability
    }
}
