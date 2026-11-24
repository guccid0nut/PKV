// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Single source of truth for product-name string literals, deployment file naming conventions, and the
///     platform-specific executable and native library names that WILLOWMAKER ships with and launches.
/// </summary>
public static class DeploymentManifest
{
    public const string ApplicationName = "WILLOWMAKER";
    public const string LogFileName = "WILLOWMAKER.log";
    public const string UpdateArchiveFileName = "WILLOWMAKER.update.zip";
    public const string UpdateExtractDirectoryName = "WILLOWMAKER.update";

    /// <summary>
    ///     The name of the WILLOWMAKER executable on the current platform.
    /// </summary>
    public static string ApplicationExecutableFileName =>
        OperatingSystem.IsWindows() ? $"{ApplicationName}.exe"
        : OperatingSystem.IsLinux() ? ApplicationName
        : OperatingSystem.IsMacOS() ? ApplicationName
        : throw new PlatformNotSupportedException($@"Unsupported Operating System: {Environment.OSVersion.Platform}");

    /// <summary>
    ///     The exact native library file names that the Native AOT release archive ships alongside the executable on the
    ///     current platform.
    ///     Used by the location guard's ship-files whitelist.
    ///     Any file in the working directory not on this list (and not the executable itself) is treated as foreign.
    /// </summary>
    public static string[] NativeLibrariesFileNames =>
        OperatingSystem.IsWindows()
            ?
            [
                "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll", "d3d9.dll", "dxgi.dll", "mimalloc.dll",
                "mimalloc-override.dll", "mimalloc-redirect.dll"
            ]
            : OperatingSystem.IsLinux()
                ? ["libHarfBuzzSharp.so", "libSkiaSharp.so", "libdxvk_d3d9.so", "libmimalloc.so"]
                : OperatingSystem.IsMacOS()
                    ? ["libAvaloniaNative.dylib", "libHarfBuzzSharp.dylib", "libSkiaSharp.dylib"]
                    : throw new PlatformNotSupportedException(
                        $@"Unsupported Operating System: {Environment.OSVersion.Platform}");

    /// <summary>
    ///     File names that the location guard ignores when assessing a baseline deployment directory.
    ///     These are not part of the distribution payload and the guard does not require their presence; their presence simply
    ///     does not classify the directory as unsafe.
    /// </summary>
    public static string[] IgnoredFileNames =>
    [
        LogFileName, "dxvk.conf", "dxvk.log", "rtx.conf", "rtx.log", "rtx-options.conf", "d3d9.log", "hon_d3d9.log",
        "hon_dxgi.log", "hon_x64_d3d9.log", "k2_x64.dll.bak", "hon_x64.exe.bak", "discord_rpc_debug.log", "userdata",
        "userdata.bak", "playtest_policy.json", "build_manifest.json", "launcher_settings.json", "THIRD_PARTY_NOTICES.md"
    ];

    /// <summary>
    ///     The Heroes Of Newerth game client executable name on the current platform.
    /// </summary>
    public static string HeroesOfNewerthExecutableFileName =>
        OperatingSystem.IsWindows() ? "hon_x64.exe"
        : OperatingSystem.IsLinux() ? "hon-x86_64"
        : OperatingSystem.IsMacOS() ? "HoN64"
        : throw new PlatformNotSupportedException($@"Unsupported Operating System: {Environment.OSVersion.Platform}");

    /// <summary>
    ///     Indicates whether the current build is a development build (any configuration other than Release) rather than a
    ///     release build (the Release configuration, which is how releases are published).
    /// </summary>
    public static bool IsDevelopmentBuild =>
#if DEVELOPMENT
    true;
#else
        false;
#endif
}
