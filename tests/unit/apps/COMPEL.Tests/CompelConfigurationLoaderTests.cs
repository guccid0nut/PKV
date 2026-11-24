// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies that the configuration file round-trips its values, tolerates a null setting by falling back to its
///     default, and reports malformed JSON as a clean error.
/// </summary>
public sealed class CompelConfigurationLoaderTests
{
    private static string TemporaryPath()
    {
        return Path.Combine(Path.GetTempPath(), $"compel-configuration-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void A_Generated_Default_File_Loads_Back_With_Its_Default_Values()
    {
        string path = TemporaryPath();

        try
        {
            CompelConfigurationLoader.CreateDefault(path);

            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.False(file.UseProxy.Value);
            Assert.Equal("DEFAULT", file.RuntimeArtefactsPath.Value);
            Assert.Equal("...", file.AuthenticationToken.Value);
            Assert.Equal(8080, file.ControlPlanePort.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Setting_Explicitly_Set_To_Null_Falls_Back_To_Its_Default()
    {
        string path = TemporaryPath();

        try
        {
            File.WriteAllText(path, """{ "UserName": null }""");

            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            Assert.NotNull(file.UserName);
            Assert.Equal("USERNAME", file.UserName.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_JSON_Is_Reported_As_An_Invalid_Operation_Rather_Than_A_Raw_Parse_Error()
    {
        string path = TemporaryPath();

        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            bool threw = false;

            try
            {
                CompelConfigurationLoader.Load(path);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
