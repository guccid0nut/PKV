// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Tests;

/// <summary>
///     Verifies the port arithmetic that the supervisor, proxy, and ping responder all depend on.
/// </summary>
public sealed class PortPlanTests
{
    [Fact]
    public void Without_Proxy_The_Public_Ports_Equal_The_Local_Ports()
    {
        PortPlan plan = new(3, 0, false);

        Assert.Equal(11235, plan.LocalGameStart);
        Assert.Equal(11237, plan.LocalGameEnd);
        Assert.Equal(11735, plan.LocalVoiceStart);
        Assert.Equal(11737, plan.LocalVoiceEnd);
        Assert.Equal(11235, plan.PublicGameStart);
        Assert.Equal(11735, plan.PublicVoiceStart);
        Assert.Equal(11234, plan.PingPort);
    }

    [Fact]
    public void With_Proxy_The_Public_Ports_Are_Offset_By_10000_Above_The_Local_Ports()
    {
        PortPlan plan = new(2, 0, true);

        Assert.Equal(11235, plan.LocalGameStart);
        Assert.Equal(21235, plan.PublicGameStart);
        Assert.Equal(21236, plan.PublicGameEnd);
        Assert.Equal(21735, plan.PublicVoiceStart);
        Assert.Equal(21234, plan.PingPort);
    }

    [Fact]
    public void The_Offset_Shifts_Every_Port_By_The_Same_Amount()
    {
        PortPlan plan = new(1, 5, false);

        Assert.Equal(11240, plan.LocalGameStart);
        Assert.Equal(11240, plan.LocalGameEnd);
        Assert.Equal(11740, plan.LocalVoiceStart);
        Assert.Equal(11239, plan.PingPort);
    }

    [Fact]
    public void TierPartitions_AreDisjoint_AcrossLocalDockerAndK3s()
    {
        PortPlan local = new(100, 0, false);
        PortPlan docker = new(100, 100, false);
        PortPlan k3s = new(100, 200, false);

        // Control plane / Ping ports
        Assert.Equal(11234, local.PingPort);
        Assert.Equal(11334, docker.PingPort);
        Assert.Equal(11434, k3s.PingPort);

        // Game ports: Local 11235..11334, Docker 11335..11434, K3s 11435..11534
        Assert.Equal(11235, local.LocalGameStart);
        Assert.Equal(11334, local.LocalGameEnd);
        Assert.Equal(11335, docker.LocalGameStart);
        Assert.Equal(11434, docker.LocalGameEnd);
        Assert.Equal(11435, k3s.LocalGameStart);
        Assert.Equal(11534, k3s.LocalGameEnd);

        Assert.True(local.LocalGameEnd < docker.LocalGameStart);
        Assert.True(docker.LocalGameEnd < k3s.LocalGameStart);

        // Voice relay separation: BaseVoiceRelayPort (11600) sits safely between K3s game (11534) and Local voice (11735)
        Assert.True(k3s.LocalGameEnd < PortPlan.BaseVoiceRelayPort);
        Assert.True(PortPlan.BaseVoiceRelayPort < local.LocalVoiceStart);
        Assert.Equal(11600, PortPlan.BaseVoiceRelayPort);

        // Dedicated voice ports: Local 11735..11834, Docker 11835..11934, K3s 11935..12034
        Assert.Equal(11735, local.LocalVoiceStart);
        Assert.Equal(11834, local.LocalVoiceEnd);
        Assert.Equal(11835, docker.LocalVoiceStart);
        Assert.Equal(11934, docker.LocalVoiceEnd);
        Assert.Equal(11935, k3s.LocalVoiceStart);
        Assert.Equal(12034, k3s.LocalVoiceEnd);

        Assert.True(local.LocalVoiceEnd < docker.LocalVoiceStart);
        Assert.True(docker.LocalVoiceEnd < k3s.LocalVoiceStart);

        // Proxied mode deconfliction
        PortPlan localProxy = new(100, 0, true);
        PortPlan dockerProxy = new(100, 100, true);
        PortPlan k3sProxy = new(100, 200, true);

        Assert.Equal(21234, localProxy.PingPort);
        Assert.Equal(21334, dockerProxy.PingPort);
        Assert.Equal(21434, k3sProxy.PingPort);

        Assert.Equal(21235, localProxy.PublicGameStart);
        Assert.Equal(21334, localProxy.PublicGameEnd);
        Assert.Equal(21335, dockerProxy.PublicGameStart);
        Assert.Equal(21434, dockerProxy.PublicGameEnd);
        Assert.Equal(21435, k3sProxy.PublicGameStart);
        Assert.Equal(21534, k3sProxy.PublicGameEnd);

        Assert.Equal(21735, localProxy.PublicVoiceStart);
        Assert.Equal(21834, localProxy.PublicVoiceEnd);
        Assert.Equal(21835, dockerProxy.PublicVoiceStart);
        Assert.Equal(21934, dockerProxy.PublicVoiceEnd);
        Assert.Equal(21935, k3sProxy.PublicVoiceStart);
        Assert.Equal(22034, k3sProxy.PublicVoiceEnd);
    }

    [Fact]
    public void ClientP2pPracticeAndBotMatchBands_AreDisjoint_FromAllCompelTiersAndVoiceRelay()
    {
        PortPlan local = new(100, 0, false);
        PortPlan docker = new(100, 100, false);
        PortPlan k3s = new(100, 200, false);

        const int p2pGameStart = 11550;
        const int p2pGameEnd = 11579;
        const int practiceGameStart = 11580;
        const int practiceGameEnd = 11589;
        const int botGameStart = 11590;
        const int botGameEnd = 11599;

        const int p2pVoiceStart = 12050;
        const int p2pVoiceEnd = 12079;
        const int practiceVoiceStart = 12080;
        const int practiceVoiceEnd = 12089;
        const int botVoiceStart = 12090;
        const int botVoiceEnd = 12099;

        // Game bands sit strictly between K3s game end (11534) and COMPEL VoiceRelay (11600)
        Assert.True(k3s.LocalGameEnd < p2pGameStart);
        Assert.True(p2pGameEnd < practiceGameStart);
        Assert.True(practiceGameEnd < botGameStart);
        Assert.True(botGameEnd < PortPlan.BaseVoiceRelayPort);

        // Voice bands sit strictly above K3s voice end (12034)
        Assert.True(k3s.LocalVoiceEnd < p2pVoiceStart);
        Assert.True(p2pVoiceEnd < practiceVoiceStart);
        Assert.True(practiceVoiceEnd < botVoiceStart && botVoiceStart < botVoiceEnd && botVoiceEnd < 12100);
        Assert.True(local.LocalGameEnd < p2pGameStart && docker.LocalGameEnd < p2pGameStart);
    }
}
