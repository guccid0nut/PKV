// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kongor.Core.Network;

public sealed record FirewallUdpAuditReport(
    string ActiveProfile,
    int PortRangeOffset,
    int Instances,
    IReadOnlyList<int> RequiredUdpPorts,
    IReadOnlyList<int> UncoveredUdpPorts,
    string? WarningBanner,
    string? RemediationPowerShellCommand,
    string RouterPortForwardSummary)
{
    public bool IsFullyCovered => UncoveredUdpPorts.Count == 0;
}

/// <summary>
///     Audits inbound Windows Firewall UDP rules (<c>netsh advfirewall</c>) against the active
///     COMPEL ping responder, dedicated game server, and voice relay UDP port ranges.
/// </summary>
public static class WindowsFirewallUdpCoverageAuditor
{
    public const int VoiceRelayPort = 11600;
    private const int BasePingPort = 11234;
    private const int BaseGameServerPort = 11235;
    private const int BaseServerVoicePort = 11735;

    public static IReadOnlyList<int> ComputeRequiredUdpPorts(int portRangeOffset, int instances)
    {
        int clampedInstances = Math.Max(1, instances);
        SortedSet<int> ports = [];

        AddIfValidPort(ports, BasePingPort + portRangeOffset);
        for (int i = 0; i < clampedInstances; i++)
        {
            AddIfValidPort(ports, BaseGameServerPort + portRangeOffset + i);
            AddIfValidPort(ports, BaseServerVoicePort + portRangeOffset + i);
        }

        AddIfValidPort(ports, VoiceRelayPort);
        return [.. ports];
    }

    public static string FormatRouterPortForwardSummary(int portRangeOffset, int instances, string? lanIp = null)
    {
        int clampedInstances = Math.Max(1, instances);
        int pingPort = BasePingPort + portRangeOffset;
        int gameEnd = BaseGameServerPort + portRangeOffset + clampedInstances - 1;
        int voiceStart = BaseServerVoicePort + portRangeOffset;
        int voiceEnd = voiceStart + clampedInstances - 1;
        string targetIp = string.IsNullOrWhiteSpace(lanIp) || lanIp is "127.0.0.1" or "localhost" or "0.0.0.0"
            ? "<LAN_IP>"
            : lanIp.Trim();

        string voiceSpan = voiceStart == voiceEnd
            ? voiceStart.ToString(CultureInfo.InvariantCulture)
            : $"{voiceStart.ToString(CultureInfo.InvariantCulture)}-{voiceEnd.ToString(CultureInfo.InvariantCulture)}";

        return $"UDP {pingPort.ToString(CultureInfo.InvariantCulture)}-{gameEnd.ToString(CultureInfo.InvariantCulture)}, {VoiceRelayPort.ToString(CultureInfo.InvariantCulture)}, {voiceSpan} -> {targetIp}";
    }

    public static string BuildRemediationPowerShellCommand(int portRangeOffset, int instances)
    {
        int clampedInstances = Math.Max(1, instances);
        int pingPort = BasePingPort + portRangeOffset;
        int gameEnd = BaseGameServerPort + portRangeOffset + clampedInstances - 1;
        int voiceStart = BaseServerVoicePort + portRangeOffset;
        int voiceEnd = voiceStart + clampedInstances - 1;
        string voiceSpec = voiceStart == voiceEnd
            ? voiceStart.ToString(CultureInfo.InvariantCulture)
            : $"{voiceStart.ToString(CultureInfo.InvariantCulture)}-{voiceEnd.ToString(CultureInfo.InvariantCulture)}";

        string localPorts = $"{pingPort.ToString(CultureInfo.InvariantCulture)}-{gameEnd.ToString(CultureInfo.InvariantCulture)},{VoiceRelayPort.ToString(CultureInfo.InvariantCulture)},{voiceSpec}";
        return $"New-NetFirewallRule -DisplayName 'ProjectKongor-COMPEL-UDP' -Direction Inbound -Action Allow -Protocol UDP -Profile Any -LocalPort {localPorts}";
    }

    public static string ParseActiveProfileFromNetsh(string? currentProfileOutput)
    {
        if (string.IsNullOrWhiteSpace(currentProfileOutput)) return "Public";

        bool hasPublic = false;
        bool hasPrivate = false;
        bool hasDomain = false;

        foreach (string rawLine in currentProfileOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Contains("Public Profile Settings", StringComparison.OrdinalIgnoreCase)) hasPublic = true;
            else if (line.Contains("Private Profile Settings", StringComparison.OrdinalIgnoreCase)) hasPrivate = true;
            else if (line.Contains("Domain Profile Settings", StringComparison.OrdinalIgnoreCase)) hasDomain = true;
        }

        if (hasPublic) return "Public";
        if (hasPrivate) return "Private";
        if (hasDomain) return "Domain";
        return "Public";
    }

    public static FirewallUdpAuditReport EvaluateNetshOutput(
        string? netshRulesOutput,
        string? activeProfile,
        int portRangeOffset,
        int instances,
        string? lanIp = null)
    {
        string normalizedProfile = string.IsNullOrWhiteSpace(activeProfile) ? "Public" : activeProfile.Trim();
        IReadOnlyList<int> requiredPorts = ComputeRequiredUdpPorts(portRangeOffset, instances);
        string routerSummary = FormatRouterPortForwardSummary(portRangeOffset, instances, lanIp);

        List<ParsedFirewallRule> allowUdpRules = ParseInboundAllowUdpRules(netshRulesOutput ?? string.Empty);

        List<int> uncovered = [];
        foreach (int port in requiredPorts)
        {
            bool covered = false;
            for (int i = 0; i < allowUdpRules.Count; i++)
            {
                ParsedFirewallRule rule = allowUdpRules[i];
                if (rule.MatchesProfile(normalizedProfile) && rule.CoversPort(port))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered) uncovered.Add(port);
        }

        if (uncovered.Count == 0)
        {
            return new FirewallUdpAuditReport(
                normalizedProfile,
                portRangeOffset,
                Math.Max(1, instances),
                requiredPorts,
                uncovered,
                null,
                null,
                routerSummary);
        }

        string remediation = BuildRemediationPowerShellCommand(portRangeOffset, instances);
        string? nearMissHint = BuildNearMissRuleHint(allowUdpRules, normalizedProfile);
        string uncoveredFormatted = FormatCompactPortList(uncovered);

        StringBuilder banner = new();
        banner.Append("[WARN] [WAN-REACHABILITY] Active Windows Firewall (profile: ")
            .Append(normalizedProfile)
            .Append(") does NOT allow inbound UDP on required COMPEL ports [")
            .Append(uncoveredFormatted)
            .Append("]. External LAN/WAN players may see 0 servers or fail UDP connect.");

        if (!string.IsNullOrEmpty(nearMissHint))
        {
            banner.Append(' ').Append(nearMissHint);
        }

        return new FirewallUdpAuditReport(
            normalizedProfile,
            portRangeOffset,
            Math.Max(1, instances),
            requiredPorts,
            uncovered,
            banner.ToString(),
            remediation,
            routerSummary);
    }

    public static async Task<FirewallUdpAuditReport?> RunAuditAsync(
        int portRangeOffset,
        int instances,
        string? lanIp = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            string? profileOut = await RunNetshCommandAsync("advfirewall show currentprofile", cts.Token).ConfigureAwait(false);
            string? rulesOut = await RunNetshCommandAsync("advfirewall firewall show rule name=all dir=in", cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rulesOut)) return null;

            string activeProfile = ParseActiveProfileFromNetsh(profileOut);
            return EvaluateNetshOutput(rulesOut, activeProfile, portRangeOffset, instances, lanIp);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsFirewallUdpCoverageAuditor] netsh firewall audit skipped due to exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> RunNetshCommandAsync(string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "netsh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? proc = Process.Start(psi);
        if (proc is null) return null;

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return stdout;
    }

    private static void AddIfValidPort(SortedSet<int> ports, int port)
    {
        if (port is >= 1 and <= 65535) ports.Add(port);
    }

    private static string? BuildNearMissRuleHint(List<ParsedFirewallRule> rules, string activeProfile)
    {
        foreach (ParsedFirewallRule rule in rules)
        {
            if (rule.Name.Contains("HON", StringComparison.OrdinalIgnoreCase) ||
                rule.Name.Contains("Kongor", StringComparison.OrdinalIgnoreCase) ||
                rule.Name.Contains("COMPEL", StringComparison.OrdinalIgnoreCase))
            {
                return $"Detected existing rule '{rule.Name}' (Profiles={rule.RawProfiles}, LocalPort={rule.RawLocalPort}) which does not cover all bound ports on '{activeProfile}'.";
            }
        }

        return null;
    }

    private static string FormatCompactPortList(IReadOnlyList<int> sortedPorts)
    {
        if (sortedPorts.Count == 0) return string.Empty;

        List<string> spans = [];
        int rangeStart = sortedPorts[0];
        int prev = rangeStart;

        for (int i = 1; i < sortedPorts.Count; i++)
        {
            int current = sortedPorts[i];
            if (current == prev + 1)
            {
                prev = current;
                continue;
            }

            spans.Add(rangeStart == prev
                ? rangeStart.ToString(CultureInfo.InvariantCulture)
                : $"{rangeStart.ToString(CultureInfo.InvariantCulture)}-{prev.ToString(CultureInfo.InvariantCulture)}");
            rangeStart = current;
            prev = current;
        }

        spans.Add(rangeStart == prev
            ? rangeStart.ToString(CultureInfo.InvariantCulture)
            : $"{rangeStart.ToString(CultureInfo.InvariantCulture)}-{prev.ToString(CultureInfo.InvariantCulture)}");

        return string.Join(", ", spans);
    }

    private static List<ParsedFirewallRule> ParseInboundAllowUdpRules(string output)
    {
        List<ParsedFirewallRule> results = [];
        string? currentName = null;
        string enabled = string.Empty;
        string direction = string.Empty;
        string profiles = string.Empty;
        string protocol = string.Empty;
        string localPort = string.Empty;
        string action = string.Empty;

        void FlushCurrent()
        {
            if (string.IsNullOrWhiteSpace(currentName)) return;

            bool isEnabled = enabled.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            bool isInbound = string.IsNullOrEmpty(direction) || direction.Equals("In", StringComparison.OrdinalIgnoreCase);
            bool isUdpOrAny = protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) ||
                              protocol.Equals("Any", StringComparison.OrdinalIgnoreCase);
            bool isAllow = action.Equals("Allow", StringComparison.OrdinalIgnoreCase);

            if (isEnabled && isInbound && isUdpOrAny && isAllow)
            {
                results.Add(new ParsedFirewallRule(
                    currentName,
                    profiles,
                    localPort,
                    ParsePortRanges(localPort)));
            }
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal)) continue;

            int colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            string key = line[..colonIdx].Trim();
            string val = line[(colonIdx + 1)..].Trim();

            if (key.Equals("Rule Name", StringComparison.OrdinalIgnoreCase))
            {
                FlushCurrent();
                currentName = val;
                enabled = string.Empty;
                direction = string.Empty;
                profiles = string.Empty;
                protocol = string.Empty;
                localPort = string.Empty;
                action = string.Empty;
            }
            else if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                enabled = val;
            }
            else if (key.Equals("Direction", StringComparison.OrdinalIgnoreCase))
            {
                direction = val;
            }
            else if (key.Equals("Profiles", StringComparison.OrdinalIgnoreCase))
            {
                profiles = val;
            }
            else if (key.Equals("Protocol", StringComparison.OrdinalIgnoreCase))
            {
                protocol = val;
            }
            else if (key.Equals("LocalPort", StringComparison.OrdinalIgnoreCase))
            {
                localPort = val;
            }
            else if (key.Equals("Action", StringComparison.OrdinalIgnoreCase))
            {
                action = val;
            }
        }

        FlushCurrent();
        return results;
    }

    private static List<(int Start, int End)> ParsePortRanges(string rawLocalPort)
    {
        if (string.IsNullOrWhiteSpace(rawLocalPort) ||
            rawLocalPort.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return [(1, 65535)];
        }

        List<(int Start, int End)> ranges = [];
        foreach (string token in rawLocalPort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dashIdx = token.IndexOf('-');
            if (dashIdx > 0)
            {
                if (int.TryParse(token[..dashIdx].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int start) &&
                    int.TryParse(token[(dashIdx + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int end) &&
                    start <= end)
                {
                    ranges.Add((start, end));
                }
            }
            else if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int singlePort))
            {
                ranges.Add((singlePort, singlePort));
            }
        }

        return ranges;
    }

    private sealed record ParsedFirewallRule(
        string Name,
        string RawProfiles,
        string RawLocalPort,
        List<(int Start, int End)> PortRanges)
    {
        public bool MatchesProfile(string activeProfile)
        {
            if (string.IsNullOrWhiteSpace(RawProfiles) ||
                RawProfiles.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string token in RawProfiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals(activeProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public bool CoversPort(int port)
        {
            for (int i = 0; i < PortRanges.Count; i++)
            {
                (int start, int end) = PortRanges[i];
                if (port >= start && port <= end) return true;
            }

            return false;
        }
    }
}
