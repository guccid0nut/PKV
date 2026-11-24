// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;

namespace Kongor.Modding.Conditions;

/// <summary>
///     Evaluates version range constraints against candidate version strings.
///     Supports exact (1.2.0), range (1.0-2.0), relational (>=1.0, <2.0, !=1.0), and wildcards (*, 1.2.*).
/// </summary>
public sealed class VersionRangeExpression
{
    private readonly Version? _exactVersion;
    private readonly Version? _maxVersion;
    private readonly Version? _minVersion;

    private readonly MatchMode _mode;
    private readonly string? _wildcardPrefix;

    private VersionRangeExpression(
        MatchMode mode,
        string rawExpression,
        Version? exact = null,
        Version? min = null,
        Version? max = null,
        string? wildcardPrefix = null)
    {
        _mode = mode;
        RawExpression = rawExpression;
        _exactVersion = exact;
        _minVersion = min;
        _maxVersion = max;
        _wildcardPrefix = wildcardPrefix;
    }

    public string RawExpression { get; }

    public static VersionRangeExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        string raw = expression.Trim().Trim('[', ']');
        string cleaned = raw.Trim().TrimStart('v', 'V');

        if (cleaned == "*" || string.IsNullOrWhiteSpace(cleaned))
            return new VersionRangeExpression(MatchMode.Any, expression);

        if (cleaned.EndsWith(".*"))
        {
            string prefix = cleaned[..^2];
            return new VersionRangeExpression(MatchMode.WildcardPrefix, expression, wildcardPrefix: prefix);
        }

        if (cleaned.StartsWith(">="))
        {
            Version v = ParseCleanVersion(cleaned[2..]);
            return new VersionRangeExpression(MatchMode.GreaterThanOrEqual, expression, min: v);
        }

        if (cleaned.StartsWith('>'))
        {
            Version v = ParseCleanVersion(cleaned[1..]);
            return new VersionRangeExpression(MatchMode.GreaterThan, expression, min: v);
        }

        if (cleaned.StartsWith("<="))
        {
            Version v = ParseCleanVersion(cleaned[2..]);
            return new VersionRangeExpression(MatchMode.LessThanOrEqual, expression, max: v);
        }

        if (cleaned.StartsWith('<'))
        {
            Version v = ParseCleanVersion(cleaned[1..]);
            return new VersionRangeExpression(MatchMode.LessThan, expression, max: v);
        }

        if (cleaned.StartsWith("!="))
        {
            Version v = ParseCleanVersion(cleaned[2..]);
            return new VersionRangeExpression(MatchMode.NotEqual, expression, v);
        }

        if (cleaned.StartsWith("=="))
        {
            Version v = ParseCleanVersion(cleaned[2..]);
            return new VersionRangeExpression(MatchMode.Exact, expression, v);
        }

        // Check for range with dash or double dot: 1.0-2.0 or 1.0..2.0
        string[] rangeSplit = cleaned.Contains("..") ? cleaned.Split("..", 2) : cleaned.Split('-', 2);
        if (rangeSplit.Length == 2 && !string.IsNullOrWhiteSpace(rangeSplit[0]) &&
            !string.IsNullOrWhiteSpace(rangeSplit[1]))
        {
            Version min = ParseCleanVersion(rangeSplit[0]);
            Version max = ParseCleanVersion(rangeSplit[1]);
            return new VersionRangeExpression(MatchMode.Range, expression, min: min, max: max);
        }

        // Exact version match
        Version exactVer = ParseCleanVersion(cleaned);
        return new VersionRangeExpression(MatchMode.Exact, expression, exactVer);
    }

    public bool IsSatisfiedBy(string? candidateVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion)) return _mode == MatchMode.Any;

        string cleanCandidate = candidateVersion.Trim().TrimStart('v', 'V');

        if (_mode == MatchMode.Any) return true;

        if (_mode == MatchMode.WildcardPrefix)
            return _wildcardPrefix != null &&
                   cleanCandidate.StartsWith(_wildcardPrefix, StringComparison.OrdinalIgnoreCase);

        Version candVer = ParseCleanVersion(cleanCandidate);

        return _mode switch
        {
            MatchMode.Exact => CompareVersions(candVer, _exactVersion) == 0,
            MatchMode.NotEqual => CompareVersions(candVer, _exactVersion) != 0,
            MatchMode.GreaterThan => CompareVersions(candVer, _minVersion) > 0,
            MatchMode.GreaterThanOrEqual => CompareVersions(candVer, _minVersion) >= 0,
            MatchMode.LessThan => CompareVersions(candVer, _maxVersion) < 0,
            MatchMode.LessThanOrEqual => CompareVersions(candVer, _maxVersion) <= 0,
            MatchMode.Range => CompareVersions(candVer, _minVersion) >= 0 && CompareVersions(candVer, _maxVersion) <= 0,
            _ => true
        };
    }

    private static Version ParseCleanVersion(string raw)
    {
        string trimmed = raw.Trim().TrimStart('v', 'V');
        // If string contains non-numeric parts like 1.0.1b, strip trailing characters
        Match match = Regex.Match(trimmed, @"^(\d+(\.\d+){0,3})");
        if (match.Success)
        {
            string numStr = match.Groups[1].Value;
            string[] parts = numStr.Split('.');
            int major = int.Parse(parts[0]);
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            int revision = parts.Length > 3 ? int.Parse(parts[3]) : 0;
            return new Version(major, minor, build, revision);
        }

        if (Version.TryParse(trimmed, out Version? v)) return v;

        return new Version(0, 0, 0, 0);
    }

    private static int CompareVersions(Version? a, Version? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int major = a.Major.CompareTo(b.Major);
        if (major != 0) return major;

        int minor = Math.Max(0, a.Minor).CompareTo(Math.Max(0, b.Minor));
        if (minor != 0) return minor;

        int build = Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build));
        if (build != 0) return build;

        int rev = Math.Max(0, a.Revision).CompareTo(Math.Max(0, b.Revision));
        return rev;
    }

    private enum MatchMode
    {
        Any,
        Exact,
        Range,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        NotEqual,
        WildcardPrefix
    }
}
