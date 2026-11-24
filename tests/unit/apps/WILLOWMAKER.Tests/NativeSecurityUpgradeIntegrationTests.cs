// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeSecurityUpgradeIntegrationTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 4 Modern TLS 1.3 ABI Proxy Shim & libcurl 8.8+ Upgrade
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using EnginePatchService = WILLOWMAKER.Core.Services.EnginePatchService;
using NativeSecurityUpgradeService = WILLOWMAKER.Core.Services.NativeSecurityUpgradeService;
using PeImportAddressTableService = WILLOWMAKER.Core.Services.PeImportAddressTableService;

namespace WILLOWMAKER.Tests;

public sealed class NativeSecurityUpgradeIntegrationTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "client", "windows-x64")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [Fact]
    public void FindSecurityTargets_AcrossRepositoryStructure_DiscoversAllClientAndServerTargets()
    {
        string repoRoot = FindRepoRoot();
        var targets = NativeSecurityUpgradeService.FindSecurityTargets(repoRoot);

        Assert.NotEmpty(targets);
        Assert.Contains(targets, t => t.EndsWith("libcurl_x64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.EndsWith("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.EndsWith("libcrypto-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpgradeSecurityBinaries_WithIatRedirection_PerformsAtomicUpgradeAndRedirectsK2()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_SecIatUpgrade_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client", "windows-x64");
        string candidateDir = Path.Combine(tempDir, "candidates");
        Directory.CreateDirectory(clientDir);
        Directory.CreateDirectory(candidateDir);

        string repoRoot = FindRepoRoot();
        string stagedCandDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");

        try
        {
            // 1. Stage synthetic targets
            byte[] baselineCurl = Encoding.UTF8.GetBytes("BASELINE_LIBCURL_7_71_1");
            byte[] baselineSsl = Encoding.UTF8.GetBytes("BASELINE_LIBSSL_1_1_1D");
            byte[] baselineCrypto = Encoding.UTF8.GetBytes("BASELINE_LIBCRYPTO_1_1_1D");

            string curlPath = Path.Combine(clientDir, "libcurl_x64.dll");
            string sslPath = Path.Combine(clientDir, "libssl-1_1-x64.dll");
            string cryptoPath = Path.Combine(clientDir, "libcrypto-1_1-x64.dll");

            File.WriteAllBytes(curlPath, baselineCurl);
            File.WriteAllBytes(sslPath, baselineSsl);
            File.WriteAllBytes(cryptoPath, baselineCrypto);

            // Create synthetic k2_x64.dll with imports for libssl and libcrypto
            var imports = new[]
            {
                ("libssl-1_1-x64.dll", new[] { "SSL_new", "SSL_connect" }),
                ("libcrypto-1_1-x64.dll", new[] { "BN_new", "SHA256_Init" }),
                ("KERNEL32.dll", new[] { "ExitProcess" })
            };
            byte[] k2Bytes = BuildSyntheticPe64(imports);
            string k2Path = Path.Combine(clientDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, k2Bytes);

            // 2. Stage candidates
            byte[] modernCurl = Encoding.UTF8.GetBytes("MODERN_LIBCURL_8_21_0_SCHANNEL_TLS13");
            byte[] modernSsl = Encoding.UTF8.GetBytes("MODERN_LIBSSL_1_1_1W_SECURE");
            byte[] modernCrypto = Encoding.UTF8.GetBytes("MODERN_LIBCRYPTO_1_1_1W_SECURE");

            File.WriteAllBytes(Path.Combine(candidateDir, "libcurl_x64.dll"), modernCurl);
            File.WriteAllBytes(Path.Combine(candidateDir, "libssl-1_1-x64.dll"), modernSsl);
            File.WriteAllBytes(Path.Combine(candidateDir, "libcrypto-1_1-x64.dll"), modernCrypto);

            var logs = new List<string>();

            // 3. Execute first upgrade with redirectIat = true
            var upgrade1 = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
                tempDir,
                candidateDir,
                skipProcessCheck: true,
                skipExportCheck: true,
                redirectIat: true,
                logAction: logs.Add);

            Assert.True(upgrade1.Success, upgrade1.Message);
            Assert.True(upgrade1.UpgradedPaths.Count >= 3, $"Expected at least 3 upgraded paths, got {upgrade1.UpgradedPaths.Count}");
            Assert.NotNull(upgrade1.RedirectedIatPaths);
            Assert.NotEmpty(upgrade1.RedirectedIatPaths);

            // Verify .bak created
            Assert.True(File.Exists(curlPath + ".bak"));
            Assert.True(File.Exists(sslPath + ".bak"));
            Assert.True(File.Exists(cryptoPath + ".bak"));
            Assert.True(File.Exists(k2Path + ".bak"));

            // Verify contents upgraded
            Assert.Equal(modernCurl, File.ReadAllBytes(curlPath));
            Assert.Equal(modernSsl, File.ReadAllBytes(sslPath));
            Assert.Equal(modernCrypto, File.ReadAllBytes(cryptoPath));

            // Verify K2 IAT redirected
            var k2Report = PeImportAddressTableService.GetImports(k2Path);
            Assert.True(k2Report.IsValidPe);
            Assert.Contains(k2Report.ImportedModules, m => m.DllName.Equals("kongor_ssl_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(k2Report.ImportedModules, m => m.DllName.Equals("kongor_crypto_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(k2Report.ImportedModules, m => m.DllName.Equals("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(k2Report.ImportedModules, m => m.DllName.Equals("libcrypto-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));

            // 4. Execute second upgrade to verify .bak -> .bak.old rotation
            byte[] cand2Data = Encoding.UTF8.GetBytes("CANDIDATE_V2_DATA");
            File.WriteAllBytes(Path.Combine(candidateDir, "libcurl_x64.dll"), cand2Data);

            var upgrade2 = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
                tempDir,
                candidateDir,
                skipProcessCheck: true,
                skipExportCheck: true,
                redirectIat: false);

            Assert.True(upgrade2.Success);
            Assert.True(File.Exists(curlPath + ".bak.old"), ".bak.old must be created on second upgrade.");
            Assert.Equal(baselineCurl, File.ReadAllBytes(curlPath + ".bak.old"));

            // 5. Rollback to 'previous' (.bak)
            var rollbackPrev = NativeSecurityUpgradeService.RollbackSecurityBinaries(
                tempDir, targetTier: "previous", skipProcessCheck: true);
            Assert.True(rollbackPrev.Success);
            Assert.Equal(modernCurl, File.ReadAllBytes(curlPath));

            // 6. Rollback to 'old' (.bak.old)
            var rollbackOld = NativeSecurityUpgradeService.RollbackSecurityBinaries(
                tempDir, targetTier: "old", skipProcessCheck: true);
            Assert.True(rollbackOld.Success);
            Assert.Equal(baselineCurl, File.ReadAllBytes(curlPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyPhase4SecurityUplift_ViaEnginePatchService_OrchestratesDeploymentSuccessfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_SecEngineUplift_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string curlPath = Path.Combine(tempDir, "libcurl_x64.dll");
            File.WriteAllBytes(curlPath, Encoding.UTF8.GetBytes("OLD_CURL"));

            string repoRoot = FindRepoRoot();
            string candDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");

            var logs = new List<string>();
            var result = EnginePatchService.ApplyPhase4SecurityUplift(
                tempDir, redirectIat: false, skipProcessCheck: true, logAction: logs.Add);

            if (File.Exists(Path.Combine(candDir, "libcurl_x64.dll")))
            {
                Assert.True(result.Success, result.Message);
                Assert.True(File.Exists(curlPath + ".bak"));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task VerifyTls13HandshakeAsync_WithLoopbackServer_NegotiatesTlsSuccessfully()
    {
        // Ephemeral self-signed certificate for local test listener
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("cn=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = TimeProvider.System.GetUtcNow();
        using var cert = certReq.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        byte[] pfxBytes = cert.Export(X509ContentType.Pfx, "temp");
        using var serverCert = X509CertificateLoader.LoadPkcs12(pfxBytes, "temp", X509KeyStorageFlags.Exportable);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCert,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
            }, cts.Token);

            // Read the HTTP request from the client before responding
            byte[] reqBuffer = new byte[1024];
            int read = await ssl.ReadAsync(reqBuffer, cts.Token);

            // Echo a minimal HTTP 200 response
            byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await ssl.WriteAsync(response, cts.Token);
            await ssl.FlushAsync(cts.Token);

            // Wait briefly for client to receive before disposing
            await Task.Delay(50, cts.Token);
        });

        string testUrl = $"https://127.0.0.1:{port}/";
        var result = await NativeSecurityUpgradeService.VerifyTls13HandshakeAsync(testUrl, cts.Token);

        await serverTask;
        listener.Stop();

        Assert.True(result.Success, result.Message);
        Assert.Contains("Negotiated", result.NegotiatedProtocol);
        Assert.True(result.RoundtripMs >= 0);
    }

    /// <summary>
    ///     Generates a valid synthetic 64-bit PE binary in-memory with custom import directory entries.
    /// </summary>
    private static byte[] BuildSyntheticPe64(
        (string DllName, string[] Functions)[] imports)
    {
        byte[] peBytes = new byte[4096];

        // 1. DOS Header
        peBytes[0] = 0x4D;
        peBytes[1] = 0x5A;
        int eLfanew = 0x80;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(peBytes.AsSpan(0x3C, 4), eLfanew);

        // 2. NT Signature
        peBytes[eLfanew] = (byte)'P';
        peBytes[eLfanew + 1] = (byte)'E';

        // 3. File Header
        int fileHeaderOffset = eLfanew + 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset, 2), 0x8664); // AMD64
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 2, 2), 2); // 2 sections
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 16, 2), 240);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 18, 2), 0x2022);

        // 4. Optional Header 64-bit
        int optHeaderOffset = eLfanew + 24;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset, 2), 0x020B);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 16, 4), 0x1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 32, 4), 0x1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 36, 4), 0x200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 56, 4), 0x4000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 60, 4), 0x400);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 108, 4), 16);

        // Import Data Directory
        uint importDirRva = 0x2000;
        uint importDirSize = (uint)((imports.Length + 1) * 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 120, 4), importDirRva);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 124, 4), importDirSize);

        // 5. Section Headers
        int sec0Offset = optHeaderOffset + 240;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(peBytes.AsSpan(sec0Offset, 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 8, 4), 0x200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 12, 4), 0x1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 16, 4), 0x200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 20, 4), 0x400);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 36, 4), 0x60000020);

        int sec1Offset = sec0Offset + 40;
        Encoding.ASCII.GetBytes(".rdata\0\0").CopyTo(peBytes.AsSpan(sec1Offset, 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 8, 4), 0x200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 12, 4), 0x2000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 16, 4), 0x600);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 20, 4), 0x600);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 36, 4), 0x40000040);

        // 6. Populate .rdata
        int descBase = 0x600;
        int payloadCursor = descBase + (imports.Length + 1) * 20;

        for (int i = 0; i < imports.Length; i++)
        {
            var (dllName, functions) = imports[i];
            int descOffset = descBase + i * 20;

            int nameOffset = payloadCursor;
            byte[] nameBytes = Encoding.ASCII.GetBytes(dllName);
            nameBytes.CopyTo(peBytes.AsSpan(nameOffset));
            peBytes[nameOffset + nameBytes.Length] = 0;
            payloadCursor += nameBytes.Length + 1;
            uint nameRva = 0x2000 + (uint)(nameOffset - 0x600);

            var hintRvas = new List<uint>();
            foreach (string fn in functions)
            {
                int hintOffset = payloadCursor;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(hintOffset, 2), 0);
                byte[] fnBytes = Encoding.ASCII.GetBytes(fn);
                fnBytes.CopyTo(peBytes.AsSpan(hintOffset + 2));
                peBytes[hintOffset + 2 + fnBytes.Length] = 0;
                payloadCursor += 2 + fnBytes.Length + 1;
                hintRvas.Add(0x2000 + (uint)(hintOffset - 0x600));
            }

            while (payloadCursor % 8 != 0) payloadCursor++;
            int thunkOffset = payloadCursor;
            for (int f = 0; f < hintRvas.Count; f++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + f * 8, 8), hintRvas[f]);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + hintRvas.Count * 8, 8), 0);
            payloadCursor += (hintRvas.Count + 1) * 8;
            uint thunkRva = 0x2000 + (uint)(thunkOffset - 0x600);

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset, 4), thunkRva);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 12, 4), nameRva);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 16, 4), thunkRva);
        }

        peBytes.AsSpan(descBase + imports.Length * 20, 20).Clear();
        return peBytes;
    }
}
