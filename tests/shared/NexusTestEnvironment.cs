// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

global using ASPIRE.Tests.Infrastructure;

namespace ASPIRE.Tests.Infrastructure
{
    public static class NexusTestEnvironment
    {
        public const string ManualTestCategory = "Manual";
        public const string IntegrationTestCategory = "Integration";

        private static int _manualBypassedCount;
        public static int ManualBypassedCount => _manualBypassedCount;

        public static bool IsEnabledRaw =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUS_RUN_MANUAL_TESTS")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KONGOR_RUN_MANUAL_TESTS"));

        public static bool IsManualTestEnabled
        {
            get
            {
                bool isEnabled = IsEnabledRaw;
                if (!isEnabled) Interlocked.Increment(ref _manualBypassedCount);

                return isEnabled;
            }
        }

        private static string? _cachedRepoRoot;

        public static string RepoRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedRepoRoot)) return _cachedRepoRoot;

                System.IO.DirectoryInfo? dir = new(System.AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ProjectKongor.slnx")))
                    {
                        _cachedRepoRoot = dir.FullName;
                        return _cachedRepoRoot;
                    }
                    dir = dir.Parent;
                }

                _cachedRepoRoot = System.AppContext.BaseDirectory;
                return _cachedRepoRoot;
            }
        }

        /// <summary>
        /// True when local retail/override K2 client binaries or archives are mounted in the workspace;
        /// false in cleanroom source-only checkouts (even if a test or tool creates an empty games/client directory).
        /// </summary>
        public static bool HasGameAssets
        {
            get
            {
                string clientDir = System.IO.Path.Combine(RepoRoot, "games", "client");
                return System.IO.File.Exists(System.IO.Path.Combine(clientDir, "hon_x64.exe")) ||
                       System.IO.File.Exists(System.IO.Path.Combine(clientDir, "k2_x64.dll")) ||
                       System.IO.File.Exists(System.IO.Path.Combine(clientDir, "game", "resources0.s2z"));
            }
        }

        /// <summary>
        /// True when local packaged .honmod / mod-suite binary fixtures exist on disk.
        /// </summary>
        public static bool HasModSuiteAssets =>
            HasGameAssets &&
            (System.IO.Directory.Exists(System.IO.Path.Combine(RepoRoot, "data", "mods")) ||
             System.IO.Directory.Exists(System.IO.Path.Combine(RepoRoot, "mods")));

        /// <summary>
        /// Resolves a compiled ProjectKongor native C++ binary (.dll/.exe) across cleanroom CMake output
        /// directories (native/build/**/Release), native/bin, src/tools/native-binaries, and games/client.
        /// Returns null if the binary has not been compiled yet (e.g. Linux CI running managed-only unit tests).
        /// </summary>
        public static string? TryFindNativeBinary(string fileName)
        {
            string root = RepoRoot;
            string[] directCandidates =
            [
                System.IO.Path.Combine(root, "native", "bin", fileName),
                System.IO.Path.Combine(root, "src", "tools", "native-binaries", "windows-x64", fileName),
                System.IO.Path.Combine(root, "artifacts", "native-build", "release", fileName),
                System.IO.Path.Combine(root, "games", "client", fileName)
            ];

            foreach (string candidate in directCandidates)
            {
                if (System.IO.File.Exists(candidate)) return candidate;
            }

            string nativeBuildDir = System.IO.Path.Combine(root, "native", "build");
            if (System.IO.Directory.Exists(nativeBuildDir))
            {
                foreach (string match in System.IO.Directory.EnumerateFiles(nativeBuildDir, fileName, System.IO.SearchOption.AllDirectories))
                {
                    if (!match.Contains("CMakeFiles", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return match;
                    }
                }
            }

            return null;
        }
    }

    public sealed class ConsoleSilencer : IDisposable
    {
        private static readonly object _lock = new();
        private static int _activeCount;
        private static TextWriter? _originalOut;
        private static TextWriter? _originalError;
        public static TextWriter? OriginalOut => _originalOut;
        public static TextWriter? OriginalError => _originalError;
        private bool _disposed;

        public ConsoleSilencer()
        {
            lock (_lock)
            {
                if (_activeCount == 0)
                {
                    _originalOut ??= Console.Out;
                    _originalError ??= Console.Error;
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                }
                _activeCount++;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                _activeCount--;
                if (_activeCount <= 0)
                {
                    _activeCount = 0;
                    if (_originalOut != null) Console.SetOut(_originalOut);
                    if (_originalError != null) Console.SetError(_originalError);
                }
            }
        }
    }
}

namespace ASPIRE.Prebaking.Tests.Infrastructure
{
    internal static class NamespaceMarker {}
}

namespace WILLOWMAKER.Tests.Infrastructure
{
    internal static class NamespaceMarker {}
}

namespace Kongor.Modding.Tests.Infrastructure
{
    internal static class NamespaceMarker {}
}
