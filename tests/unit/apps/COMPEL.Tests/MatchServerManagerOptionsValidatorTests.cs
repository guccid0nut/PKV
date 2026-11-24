// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies the start-up validation of the host-facing options: the placeholder-credential guard, the supported
///     locations and artefacts-path alias, and the port-range-offset boundary.
/// </summary>
public sealed class MatchServerManagerOptionsValidatorTests
{
    private static MatchServerManagerOptions ValidOptions()
    {
        return new MatchServerManagerOptions
        {
            UserName = "KONGOR",
            Password = "secret",
            Instances = 1,
            Gateway = "kongor.net",
            Location = "EU",
            ServerNamePrefix = "KONGOR ARENA",
            UseProxy = true,
            PortRangeOffset = 0,
            RuntimeArtefactsPath = "DEFAULT"
        };
    }

    private static ValidateOptionsResult Validate(MatchServerManagerOptions options)
    {
        return new MatchServerManagerOptionsValidator().Validate(null, options);
    }

    [Fact]
    public void A_Fully_Configured_Set_Of_Options_Passes()
    {
        Assert.True(Validate(ValidOptions()).Succeeded);
    }

    [Fact]
    public void The_Default_Placeholder_User_Name_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UserName = "USERNAME";

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void The_Default_Placeholder_Password_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Password = "PASSWORD";

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void An_Unsupported_Location_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Location = "MARS";

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void The_Default_Artefacts_Path_Alias_Is_Accepted_But_A_Legacy_Alias_Is_Not()
    {
        MatchServerManagerOptions accepted = ValidOptions();
        accepted.RuntimeArtefactsPath = "DEFAULT";

        MatchServerManagerOptions rejected = ValidOptions();
        rejected.RuntimeArtefactsPath = "TEMP";

        Assert.True(Validate(accepted).Succeeded);
        Assert.True(Validate(rejected).Failed);
    }

    [Fact]
    public void A_Port_Range_Offset_That_Bleeds_Outside_The_Window_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.PortRangeOffset = 300; // 300 is the 301st port in a 300-port window (0..299), so it must be rejected
        Assert.True(Validate(options).Failed);

        MatchServerManagerOptions edgeValid = ValidOptions();
        edgeValid.PortRangeOffset = 299; // 299 with 1 instance sits on the 300th port (11534 / 21534), so it must succeed
        Assert.True(Validate(edgeValid).Succeeded);

        MatchServerManagerOptions fullTierValid = ValidOptions();
        fullTierValid.PortRangeOffset = 200;
        fullTierValid.Instances = 100; // 200..299 occupies exactly ports 200 through 299, so it must succeed
        Assert.True(Validate(fullTierValid).Succeeded);

        MatchServerManagerOptions tierBleed = ValidOptions();
        tierBleed.PortRangeOffset = 201;
        tierBleed.Instances = 100; // 201..300 bleeds to port 300, so it must be rejected
        Assert.True(Validate(tierBleed).Failed);
    }

    [Fact]
    public void Standard_Fleet_Tier_Offsets_Are_Accepted()
    {
        MatchServerManagerOptions local = ValidOptions();
        local.PortRangeOffset = 0;
        Assert.True(Validate(local).Succeeded);

        MatchServerManagerOptions docker = ValidOptions();
        docker.PortRangeOffset = 100;
        Assert.True(Validate(docker).Succeeded);

        MatchServerManagerOptions k3s = ValidOptions();
        k3s.PortRangeOffset = 200;
        Assert.True(Validate(k3s).Succeeded);
    }

    [Fact]
    public void Instances_Exceeding_TierPartitionSize_Are_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Instances = 101;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void A_Negative_Port_Range_Offset_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.PortRangeOffset = -1;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void WarmInstancesTarget_Negative_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.WarmInstancesTarget = -1;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void WarmInstancesTarget_Exceeding_Instances_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Instances = 2;
        options.WarmInstancesTarget = 3;

        Assert.True(Validate(options).Failed);
    }

    [Fact]
    public void WarmInstancesTarget_Valid_Succeeds()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Instances = 4;
        options.WarmInstancesTarget = 2;

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Enabling_ZstdNetcodeCompression_With_UseProxy_Fails()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UseProxy = true;
        options.EnableZstdNetcodeCompression = true;

        ValidateOptionsResult result = Validate(options);
        Assert.True(result.Failed);
        Assert.Contains("EnableZstdNetcodeCompression", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Enabling_ZstdNetcodeCompression_Without_UseProxy_Succeeds()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UseProxy = false;
        options.EnableZstdNetcodeCompression = true;

        Assert.True(Validate(options).Succeeded);
    }
}
