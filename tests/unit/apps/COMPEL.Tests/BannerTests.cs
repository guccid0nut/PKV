// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Tests;

/// <summary>
///     Verifies the log-file session header: the banner opens a fresh file, and each subsequent session is separated by
///     its own marker without the banner being repeated.
/// </summary>
public sealed class BannerTests
{
    private const string SessionMarker = "COMPEL Session Started At";

    private static string TemporaryLogPath()
    {
        return Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");
    }

    [Fact]
    public async Task A_Fresh_Log_File_Opens_With_The_Banner_And_A_Session_Marker()
    {
        string path = TemporaryLogPath();

        try
        {
            Banner.WriteToLogFile(path);

            string content = await File.ReadAllTextAsync(path);

            // The Banner Art Is The Only Content Containing Backslashes.
            Assert.Contains(@"\", content);
            Assert.Contains(SessionMarker, content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_Second_Session_Is_Separated_By_Its_Own_Marker_Without_Repeating_The_Banner()
    {
        string path = TemporaryLogPath();

        try
        {
            Banner.WriteToLogFile(path);
            Banner.WriteToLogFile(path);

            string content = await File.ReadAllTextAsync(path);

            int markerCount = (content.Length - content.Replace(SessionMarker, string.Empty).Length) /
                              SessionMarker.Length;

            int firstMarker = content.IndexOf(SessionMarker, StringComparison.Ordinal);
            string afterFirstSession = content[(firstMarker + SessionMarker.Length)..];

            Assert.Equal(2, markerCount);

            // The Banner (The Only Content With Backslashes) Must Not Appear Again After The First Session.
            Assert.DoesNotContain(@"\", afterFirstSession);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
