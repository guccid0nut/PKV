// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable LogMessageIsConditional

using KONGOR.MasterServer.Logging;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.PatcherController;

[ApiController]
[Route("patcher/patcher.php")]
[Consumes("application/x-www-form-urlencoded")]
public class PatcherController(
    ILogger<PatcherController> logger,
    IDatabase distributedCache,
    IOptions<OperationalConfiguration> configuration) : ControllerBase
{
    private ILogger Logger { get; } = logger;
    private IDatabase DistributedCache { get; } = distributedCache;
    private OperationalConfiguration Configuration { get; } = configuration.Value;

    // Canonical supported (OS, Architecture) tuples. Every OperatingSystem entry here MUST have a corresponding
    // Latest = true entry in PatchHandlers.DistributionVersions. Note: macOS HoN64 compiles K2_ARCH = "universal-64"
    // in both x86_64 and arm64 slices, and we also accept "arm64" / "x86_64" defensively. No native macOS dedicated
    // server ("mas-*") manifest exists, so "mas-*" is intentionally excluded to return 400 BadRequest instead of 500.
    public static IReadOnlyList<(string OperatingSystem, string Architecture)> SupportedClientDistributions { get; } =
    [
        ("wac", "x86_64"),
        ("lac", "x86-biarch"),
        ("mac", "universal-64"),
        ("mac", "arm64"),
        ("mac", "x86_64"),
        ("was-crIac6LASwoafrl8FrOa", "x86_64"),
        ("las-crIac6LASwoafrl8FrOa", "x86_64"),
        ("las-crIac6LASwoafrl8FrOa", "x86-biarch")
    ];

    [HttpPost(Name = "Patcher")]
    public async Task<IActionResult> LatestPatch([FromForm] LatestPatchRequestForm form,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(form.Cookie))
        {
            (bool isValid, _) = await DistributedCache.ValidateAccountSessionCookie(form.Cookie);
            if (!isValid)
            {
                MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(form.Cookie);
                MatchServerManager? matchServerManager = matchServer == null
                    ? await DistributedCache.GetMatchServerManagerBySessionCookie(form.Cookie)
                    : null;

                if (matchServer == null && matchServerManager == null)
                {
                    if (Logger.IsEnabled(LogLevel.Warning))
                    {
                        string remoteIp = Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN";
                        Logger.LogForgedCookie(remoteIp, SecurityMasking.MaskToken(form.Cookie));
                    }

                    return Unauthorized($@"Unrecognized Cookie ""{SecurityMasking.MaskToken(form.Cookie)}""");
                }
            }
        }

        // Default to Windows/x64 if not specified (for 'latest' checks)
        string os = form.OperatingSystem ?? "wac";
        string arch = form.Architecture ?? "x86_64";
        string requestedCurrentVersion = form.CurrentPatchVersion ?? string.Empty;

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogPatcherRequestReceived(os, arch, requestedCurrentVersion, !string.IsNullOrEmpty(form.Cookie));
        }

        if (SupportedClientDistributions.Contains((os, arch)).Equals(false))
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogPatcherUnsupportedClient(os, arch, requestedCurrentVersion);
            }

            return BadRequest($@"Unsupported Client: Operating System ""{os}"", Architecture ""{arch}""");
        }

        // The Current Client's Version Number Needs To Include The Revision Number Even If It Is Zero (e.g. "4.10.1.0" rather than just "4.10.1")
        PatchDetails? currentPatch = null;
        if (!string.IsNullOrEmpty(form.CurrentPatchVersion))
        {
            currentPatch = PatchHandlers.GetClientPatchDetails(os, form.CurrentPatchVersion);

            if (currentPatch is null)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                {
                    Logger.LogPatcherCurrentVersionNotFound(os, arch, requestedCurrentVersion);
                }

                return NotFound($@"Current Patch Details Not Found For Version ""{form.CurrentPatchVersion}""");
            }
        }

        // Unlike The Current Client's Version, The Revision Number Is Excluded From The Version Number Of The Latest Client If It Is Zero (e.g. "4.10.1.0" becomes just "4.10.1")
        PatchDetails? latestPatch = PatchHandlers.GetLatestClientPatchDetails(os);

        if (latestPatch is null)
        {
            if (Logger.IsEnabled(LogLevel.Error)) Logger.LogLatestPatchNotFound(os);

            return StatusCode(500, "Internal Server Error: Latest Patch Details Not Found");
        }

        // Handle case where we don't have current patch details (e.g. fresh install check or 'latest' probe)
        // If currentPatch is null, we assume they are on 0.0.0.0 or just want latest info.
        // But LatestPatchResponse expects non-null strings? Let's check model.
        // Assuming we can return "0.0.0.0" as current if unknown.

        LatestPatchResponse response = new()
        {
            PatchVersion = currentPatch?.FullVersion ?? "0.0.0.0",
            CurrentPatchVersion = currentPatch?.FullVersion ?? "0.0.0.0",
            CurrentManifestArchiveSHA1Hash = currentPatch?.ManifestArchiveSHA1Hash ?? string.Empty,
            CurrentManifestArchiveSizeInBytes = currentPatch?.ManifestArchiveSizeInBytes ?? "0",
            PatchDetails = new PatchDetailsForResponse
            {
                OperatingSystem = os,
                Architecture = arch,
                PatchVersion = latestPatch.Version,
                LatestPatchVersion = latestPatch.Version,
                LatestManifestArchiveSHA1Hash = latestPatch.ManifestArchiveSHA1Hash,
                LatestManifestArchiveSizeInBytes = latestPatch.ManifestArchiveSizeInBytes,
                PrimaryDownloadURL = Configuration.CDN.PrimaryPatchURL,
                SecondaryDownloadURL = Configuration.CDN.SecondaryPatchURL
            }
        };

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogPatcherResponseDispatched(
                os,
                arch,
                requestedCurrentVersion,
                latestPatch.Version,
                latestPatch.ManifestArchiveSHA1Hash,
                latestPatch.ManifestArchiveSizeInBytes);
        }

        return Ok(PhpSerialization.Serialize(response));
    }
}
