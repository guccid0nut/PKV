# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

import os
import re
import subprocess
import unittest
from pathlib import Path

class TestRoslynAnalyzerEnforcementAdversarial(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.project_root = Path(__file__).resolve().parents[2]
        cls.props_path = cls.project_root / "Directory.Build.props"
        cls.banned_symbols_path = cls.project_root / "BannedSymbols.txt"
        cls.test_target_proj = cls.project_root / "src" / "libs" / "contracts" / "NEXUS.Contracts.csproj"
        cls.test_injection_file = cls.project_root / "src" / "libs" / "contracts" / "AdversarialStressTest.cs"

        cls.props_content = cls.props_path.read_text(encoding="utf-8")
        cls.banned_symbols_content = cls.banned_symbols_path.read_text(encoding="utf-8")

    def tearDown(self):
        # Guarantee cleanup of test injection file
        if self.test_injection_file.exists():
            self.test_injection_file.unlink()

    def run_dotnet_build(self):
        result = subprocess.run(
            ["dotnet", "build", str(self.test_target_proj), "/p:UseSharedCompilation=false", "--no-incremental"],
            capture_output=True,
            text=True,
            cwd=str(self.project_root)
        )
        return result.returncode, result.stdout + "\n" + result.stderr

    def test_roslyn_rule_directory_build_props_analyzers_unsuppressed(self):
        """
        Verify that CA2016, CA2000, CA2200, CA2012 are not present in <NoWarn>
        and TreatWarningsAsErrors is true.
        """
        nowarn_match = re.search(r'<NoWarn>(.*?)</NoWarn>', self.props_content, re.DOTALL)
        self.assertIsNotNone(nowarn_match, "Directory.Build.props must contain a <NoWarn> element")
        nowarn_text = nowarn_match.group(1)

        banned_suppressions = ["CA2016", "CA2000", "CA2200", "CA2012"]
        for diag in banned_suppressions:
            self.assertNotIn(diag, nowarn_text, f"Diagnostic {diag} must not be suppressed in Directory.Build.props <NoWarn>")

        self.assertIn("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", self.props_content)
        self.assertIn('<PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers"', self.props_content)
        self.assertIn('<AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt"', self.props_content)

    def test_roslyn_rule_banned_symbols_completeness(self):
        """
        Verify that BannedSymbols.txt contains all required rules including expanded .NET 10 bans.
        """
        lines = [line.strip() for line in self.banned_symbols_content.splitlines() if line.strip() and not line.strip().startswith("#") and not line.strip().startswith("//")]
        self.assertGreaterEqual(len(lines), 13, f"BannedSymbols.txt must contain at least 13 rules, found {len(lines)}")

        expected_rules = [
            "P:System.DateTime.Now",
            "M:System.DateTime.get_Now",
            "P:System.DateTime.UtcNow",
            "M:System.DateTime.get_UtcNow",
            "P:System.DateTime.Today",
            "M:System.DateTime.get_Today",
            "P:System.DateTimeOffset.Now",
            "M:System.DateTimeOffset.get_Now",
            "P:System.DateTimeOffset.UtcNow",
            "M:System.DateTimeOffset.get_UtcNow",
            "M:System.Threading.Thread.Sleep(System.Int32)",
            "M:System.Threading.Thread.Sleep(System.TimeSpan)",
            "M:System.Random.#ctor",
            "M:System.Threading.Tasks.Task.WaitAll",
            "M:System.Threading.Tasks.Task.WaitAny",
            "M:System.Threading.Thread.Abort",
            "M:System.Security.Cryptography.RNGCryptoServiceProvider.#ctor",
            "M:System.Security.Cryptography.MD5.Create",
            "M:System.Security.Cryptography.SHA1.Create",
            "M:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter.#ctor",
            "M:System.Net.WebClient.#ctor",
            "M:System.Net.WebRequest.Create",
            "M:System.Threading.Tasks.Task.Wait",
            "P:System.Threading.Tasks.Task`1.Result",
            "M:System.Runtime.CompilerServices.TaskAwaiter.GetResult",
            "M:System.Runtime.CompilerServices.TaskAwaiter`1.GetResult",
            "M:System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult",
            "M:System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult",
        ]

        for expected in expected_rules:
            matched = any(line.startswith(expected) for line in lines)
            self.assertTrue(matched, f"BannedSymbols.txt must contain rule starting with '{expected}'")

    def test_roslyn_rule_empirical_clean_build_baseline(self):
        """
        Verify that NEXUS.Contracts builds cleanly with 0 warnings and 0 errors as a baseline.
        """
        code, output = self.run_dotnet_build()
        self.assertEqual(code, 0, f"Baseline build of NEXUS.Contracts failed:\n{output}")
        self.assertIn("Build succeeded.", output)

    def test_roslyn_rule_empirical_ca2016_violation(self):
        """
        Adversarially inject an unforwarded CancellationToken call site and verify CA2016 compiler error.
        """
        snippet = """using System;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressCa2016
{
    public static async Task BadAsync(CancellationToken cancellationToken)
    {
        // Violates CA2016 by ignoring cancellationToken
        await Task.Delay(50);
    }
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on CA2016 violation")
        self.assertTrue("CA2016" in output or "error CA2016" in output, f"Output should contain CA2016 error:\n{output}")

    def test_roslyn_rule_empirical_ca2000_violation(self):
        """
        Adversarially inject an undisposed IDisposable and verify CA2000 compiler error.
        """
        snippet = """using System;
using System.IO;

namespace NEXUS.Contracts.Adversarial;

public static class StressCa2000
{
    public static void BadDispose(string path)
    {
        // Violates CA2000: FileStream is created and not disposed before leaving scope
        FileStream fs = new FileStream(path, FileMode.Open);
        fs.ReadByte();
    }
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        result = subprocess.run(
            ["dotnet", "build", str(self.test_target_proj), "/p:WarningsAsErrors=CA2000", "--no-incremental"],
            capture_output=True,
            text=True,
            cwd=str(self.project_root)
        )
        code, output = result.returncode, result.stdout + "\n" + result.stderr
        self.assertNotEqual(code, 0, "Build must fail on CA2000 violation")
        self.assertTrue("CA2000" in output or "error CA2000" in output, f"Output should contain CA2000 error:\n{output}")

    def test_roslyn_rule_empirical_ca2200_violation(self):
        """
        Adversarially inject 'throw ex;' and verify CA2200 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressCa2200
{
    public static void BadRethrow()
    {
        try
        {
            int.Parse("invalid");
        }
        catch (Exception ex)
        {
            // Violates CA2200: Rethrowing exception directly loses stack trace
            throw ex;
        }
    }
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on CA2200 violation")
        self.assertTrue("CA2200" in output or "error CA2200" in output, f"Output should contain CA2200 error:\n{output}")

    def test_roslyn_rule_empirical_ca2012_violation(self):
        """
        Adversarially inject improper ValueTask consumption and verify CA2012 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressCa2012
{
    private static ValueTask<int> GetValAsync() => ValueTask.FromResult(10);

    public static void BadValueTask()
    {
        var vt = GetValAsync();
        _ = vt.GetAwaiter().GetResult();
        // Violates CA2012: Multiple awaits or GetResult on same ValueTask instance
        _ = vt.GetAwaiter().GetResult();
    }
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on CA2012 violation")
        self.assertTrue("CA2012" in output or "error CA2012" in output, f"Output should contain CA2012 error:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_datetime_utcnow(self):
        """
        Adversarially inject DateTime.UtcNow and verify RS0030 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedDateTimeUtcNow
{
    public static DateTime GetTime() => DateTime.UtcNow;
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on DateTime.UtcNow RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")
        self.assertTrue("DateTime.UtcNow" in output, f"Output should mention DateTime.UtcNow:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_datetime_now(self):
        """
        Adversarially inject DateTime.Now and verify RS0030 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedDateTimeNow
{
    public static DateTime GetTime() => DateTime.Now;
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on DateTime.Now RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_datetimeoffset_utcnow(self):
        """
        Adversarially inject DateTimeOffset.UtcNow and verify RS0030 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedDateTimeOffsetUtcNow
{
    public static DateTimeOffset GetTime() => DateTimeOffset.UtcNow;
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on DateTimeOffset.UtcNow RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_datetimeoffset_now(self):
        """
        Adversarially inject DateTimeOffset.Now and verify RS0030 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedDateTimeOffsetNow
{
    public static DateTimeOffset GetTime() => DateTimeOffset.Now;
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on DateTimeOffset.Now RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_thread_sleep(self):
        """
        Adversarially inject Thread.Sleep and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedThreadSleep
{
    public static void Block() => Thread.Sleep(10);
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on Thread.Sleep RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_roslyn_rule_empirical_banned_symbols_random_ctor(self):
        """
        Adversarially inject new Random() and verify RS0030 compiler error.
        """
        snippet = """using System;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedRandomCtor
{
    public static Random MakeRandom() => new Random();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on new Random() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13b_empirical_banned_symbols_md5_create(self):
        """
        Adversarially inject MD5.Create() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Security.Cryptography;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedMd5Create
{
    public static MD5 MakeMd5() => MD5.Create();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on MD5.Create() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13c_empirical_banned_symbols_webclient(self):
        """
        Adversarially inject new WebClient() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Net;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedWebClient
{
    public static WebClient MakeWebClient() => new WebClient();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on new WebClient() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13d_empirical_banned_symbols_task_waitall(self):
        """
        Adversarially inject Task.WaitAll() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedTaskWaitAll
{
    public static void BlockWait(Task t) => Task.WaitAll(t);
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on Task.WaitAll() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13e_empirical_banned_symbols_task_wait(self):
        """
        Adversarially inject Task.Wait() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedTaskWait
{
    public static void BlockWait(Task t) => t.Wait();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on Task.Wait() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13f_empirical_banned_symbols_task_result(self):
        """
        Adversarially inject Task<T>.Result and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedTaskResult
{
    public static int BlockResult(Task<int> t) => t.Result;
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on Task<T>.Result RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13g_empirical_banned_symbols_taskawaiter_getresult(self):
        """
        Adversarially inject Task.GetAwaiter().GetResult() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedTaskAwaiterGetResult
{
    public static void BlockGetResult(Task t) => t.GetAwaiter().GetResult();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on TaskAwaiter.GetResult() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_13h_empirical_banned_symbols_valuetaskawaiter_getresult(self):
        """
        Adversarially inject ValueTask.GetAwaiter().GetResult() and verify RS0030 compiler error.
        """
        snippet = """using System;
using System.Threading.Tasks;

namespace NEXUS.Contracts.Adversarial;

public static class StressBannedValueTaskAwaiterGetResult
{
    public static void BlockValueTask(ValueTask vt) => vt.GetAwaiter().GetResult();
}
"""
        self.test_injection_file.write_text(snippet, encoding="utf-8")
        code, output = self.run_dotnet_build()
        self.assertNotEqual(code, 0, "Build must fail on ValueTaskAwaiter.GetResult() RS0030 violation")
        self.assertTrue("RS0030" in output, f"Output should contain RS0030 error:\n{output}")

    def test_roslyn_rule_remediated_source_files_hygiene(self):
        """
        Verify that remediated files do not contain warning suppressions or throw ex.
        """
        target_files = [
            self.project_root / "src" / "services" / "master-server" / "Controllers" / "StorageController.cs",
            self.project_root / "src" / "extensions" / "modding" / "Core" / "Packaging" / "HonModArchiveReader.cs",
            self.project_root / "src" / "extensions" / "modding" / "Core" / "Packaging" / "S2zArchiveReader.cs",
            self.project_root / "src" / "apps" / "willowmaker" / "Services" / "IPC" / "WillowmakerIpcClient.cs",
            self.project_root / "src" / "apps" / "willowmaker" / "Services" / "IPC" / "WillowmakerIpcService.cs",
            self.project_root / "src" / "libs" / "protocol-core" / "IPC" / "WillowmakerIpcServer.cs",
            self.project_root / "src" / "services" / "master-server" / "Services" / "Storage" / "IReplayStorageProvider.cs",
            self.project_root / "src" / "services" / "master-server" / "Services" / "Storage" / "CloudStorageProvider.cs",
            self.project_root / "src" / "services" / "master-server" / "Services" / "Storage" / "LocalDiskStorageProvider.cs",
            self.project_root / "src" / "services" / "master-server" / "Services" / "Storage" / "SupabaseStorageProvider.cs",
            self.project_root / "src" / "services" / "master-server" / "Services" / "Replay" / "ReplayAutoUploadService.cs",
            self.project_root / "src" / "services" / "web-ui" / "Services" / "SupabaseRealtimeClient.cs",
        ]

        banned_patterns = [
            r'#pragma\s+warning\s+disable.*CA2000',
            r'#pragma\s+warning\s+disable.*CA2016',
            r'#pragma\s+warning\s+disable.*CA2200',
            r'#pragma\s+warning\s+disable.*CA2012',
            r'#pragma\s+warning\s+disable.*RS0030',
            r'throw\s+[a-zA-Z0-9_]+\s*;', # Matches throw ex;
        ]

        for file_path in target_files:
            self.assertTrue(file_path.exists(), f"File {file_path} must exist")
            content = file_path.read_text(encoding="utf-8")
            for pattern in banned_patterns:
                match = re.search(pattern, content)
                self.assertIsNone(match, f"File {file_path.name} contains banned pattern '{pattern}': {match.group(0) if match else ''}")

    def test_roslyn_rule_remediated_projects_zero_target_analyzer_violations(self):
        """
        Verify that remediated projects have 0 violations of CA2016, CA2000, CA2200, CA2012.
        """
        target_projects = [
            self.project_root / "src" / "extensions" / "modding" / "Kongor.Modding.csproj",
            self.project_root / "src" / "apps" / "willowmaker" / "WILLOWMAKER.csproj",
            self.project_root / "src" / "services" / "master-server" / "KONGOR.MasterServer.csproj",
            self.project_root / "src" / "services" / "web-api" / "ZORGATH.WebPortal.API.csproj",
            self.project_root / "src" / "services" / "web-ui" / "DAWNBRINGER.WebPortal.UI.csproj",
        ]

        target_diagnostics = ["CA2016", "CA2000", "CA2200", "CA2012"]

        for proj in target_projects:
            self.assertTrue(proj.exists(), f"Project file {proj} must exist")
            res = subprocess.run(
                ["dotnet", "build", str(proj), "--no-incremental"],
                capture_output=True,
                text=True,
                cwd=str(self.project_root)
            )
            output = res.stdout + "\n" + res.stderr
            for diag in target_diagnostics:
                pattern = rf'(error|warning)\s+{diag}\b'
                match = re.search(pattern, output)
                self.assertIsNone(match, f"Project {proj.name} emitted violation {diag}:\n{match.group(0) if match else ''}")

if __name__ == '__main__':
    unittest.main()

