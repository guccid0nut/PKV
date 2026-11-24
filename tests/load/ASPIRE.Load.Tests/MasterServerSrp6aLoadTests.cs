// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.SRP;
using SecureRemotePassword;
using Xunit;

namespace ASPIRE.Load.Tests;

public class MasterServerSrp6aLoadTests
{
    private static (SRPAuthenticationSessionDataStageOne StageOne, SrpClient Client, SrpEphemeral ClientEphemeral, SrpSession ClientSession) SimulateStageOne(
        string accountName,
        string passwordHash)
    {
        SrpParameters parameters = PrecomputedSrpParameters.PaddedGenerator;
        SrpClient client = new(parameters);
        SrpEphemeral clientEphemeral = client.GenerateEphemeral();

        string sessionSalt = SRPAuthenticationSessionDataStageOne.GenerateSRPSessionSalt();
        string verifier = SRPAuthenticationSessionDataStageOne.ComputeVerifier(sessionSalt, accountName, passwordHash);
        (string serverPrivateKey, string serverPublicKey) = SRPAuthenticationSessionDataStageOne.ComputeServerEphemeral(verifier);

        string privateClientKey = client.DerivePrivateKey(sessionSalt, accountName, passwordHash);
        SrpSession clientSession = client.DeriveSession(
            clientEphemeral.Secret,
            serverPublicKey,
            sessionSalt,
            accountName,
            privateClientKey);

        SRPAuthenticationSessionDataStageOne stageOne = new()
        {
            LoginIdentifier = accountName,
            SessionSalt = sessionSalt,
            PasswordSalt = "test_password_salt_12345",
            PasswordHash = passwordHash,
            ClientPublicEphemeral = clientEphemeral.Public,
            Verifier = verifier,
            ServerPrivateEphemeral = serverPrivateKey,
            ServerPublicEphemeral = serverPublicKey
        };

        return (stageOne, client, clientEphemeral, clientSession);
    }

    [Fact]
    public async Task MasterServerSrp6a_ConcurrentHandshakes_ProcessesUnderLoadWithoutFailure()
    {
        const int concurrentHandshakes = 100;
        const string passwordHash = "d41d8cd98f00b204e9800998ecf8427e";

        // Pre-warm SRP generator and static caches
        {
            var warmup = SimulateStageOne("WarmupUser", passwordHash);
            SRPAuthenticationSessionDataStageTwo stageTwoWarmup = new(warmup.StageOne, warmup.ClientSession.Proof);
            stageTwoWarmup.ServerProof.Should().NotBeNull();
        }

        Stopwatch sw = Stopwatch.StartNew();

        Task[] tasks = new Task[concurrentHandshakes];
        for (int i = 0; i < concurrentHandshakes; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                string accountName = $"Player_{index:D4}";
                var (stageOne, client, clientEphemeral, clientSession) = SimulateStageOne(accountName, passwordHash);

                SRPAuthenticationSessionDataStageTwo stageTwo = new(stageOne, clientSession.Proof);
                stageTwo.ServerProof.Should().NotBeNull("Valid client proof must produce non-null server proof");

                // Verify mutual authentication from client side (throws SecurityException on mismatch)
                client.VerifySession(clientEphemeral.Public, clientSession, stageTwo.ServerProof!);
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2500, "100 concurrent SRP-6a handshakes must complete in sub-seconds using precomputed SRP parameters");
    }

    [Fact]
    public void MasterServerSrp6a_TamperedClientProof_RejectsAuthentication()
    {
        const string accountName = "LegitPlayer";
        const string passwordHash = "d41d8cd98f00b204e9800998ecf8427e";

        var (stageOne, _, _, clientSession) = SimulateStageOne(accountName, passwordHash);

        // Corrupt client proof M1
        string tamperedProof = clientSession.Proof.Length > 10
            ? clientSession.Proof[..^4] + "ffff"
            : "corrupted_proof_value";

        SRPAuthenticationSessionDataStageTwo stageTwo = new(stageOne, tamperedProof);
        stageTwo.ServerProof.Should().BeNull("Tampered or invalid client proof must be rejected with null ServerProof");
    }

    [Trait("Category", "Manual")]
    [Fact]
    [Trait("Category", NexusTestEnvironment.ManualTestCategory)]
    public async Task MasterServerSrp6a_HighLoadStressTest_Executes500ConcurrentHandshakes()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const int stressHandshakes = 500;
        const string passwordHash = "d41d8cd98f00b204e9800998ecf8427e";

        Stopwatch sw = Stopwatch.StartNew();
        Task[] tasks = new Task[stressHandshakes];

        for (int i = 0; i < stressHandshakes; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                string accountName = $"StressUser_{index:D5}";
                var (stageOne, client, clientEphemeral, clientSession) = SimulateStageOne(accountName, passwordHash);

                SRPAuthenticationSessionDataStageTwo stageTwo = new(stageOne, clientSession.Proof);
                stageTwo.ServerProof.Should().NotBeNull();
                client.VerifySession(clientEphemeral.Public, clientSession, stageTwo.ServerProof!);
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "500 concurrent handshakes must complete within stress threshold");
    }
}
