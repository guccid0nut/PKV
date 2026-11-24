// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeProcessInjectionEnvironmentTests.cs
// System: Kongor Core Unit Tests - Native Process Supervision
// Subsystem: Win32 CREATE_UNICODE_ENVIRONMENT Block Serialization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Core.Tests.Native;

public sealed class NativeProcessInjectionEnvironmentTests
{
    [Fact]
    public void BuildUnicodeEnvironmentBlock_SortsCaseInsensitivelyEncodesUtf16LeDoubleNullTerminated_AndPreservesParentAndOverrides()
    {
        // Protects: Win32 CreateProcessW CREATE_UNICODE_ENVIRONMENT block sorting, UTF-16LE encoding,
        // double-null terminator, and full propagation of inherited parent + WILLOWMAKER child overrides.
        ProcessStartInfo startInfo = new();
        startInfo.EnvironmentVariables["z_custom_last"] = "tail";
        startInfo.EnvironmentVariables["HON_MODERN_HUD"] = "1";
        startInfo.EnvironmentVariables["HON_FSR_ENABLE"] = "1";
        startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = @"C:\Kongor\userdataKONGOR\dxvk.conf";
        startInfo.EnvironmentVariables["a_custom_first"] = "head";

        byte[] block = NativeProcessInjectionService.BuildUnicodeEnvironmentBlock(startInfo.EnvironmentVariables);

        Assert.True(block.Length >= 8, "Environment block must contain serialized UTF-16LE entries plus double-null terminator.");
        Assert.Equal(0, block.Length % 2);

        // Verify double UTF-16LE null terminator (\0\0 -> 4 trailing zero bytes)
        Assert.Equal(0x00, block[^4]);
        Assert.Equal(0x00, block[^3]);
        Assert.Equal(0x00, block[^2]);
        Assert.Equal(0x00, block[^1]);

        string decoded = Encoding.Unicode.GetString(block);
        Assert.EndsWith("\0\0", decoded, StringComparison.Ordinal);

        string[] entries = decoded[..^2].Split('\0', StringSplitOptions.None);
        Assert.True(entries.Length >= 5);

        // Verify strict case-insensitive alphabetical ordering by key as required by Win32 CreateProcessW
        string? previousKey = null;
        HashSet<string> entrySet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in entries)
        {
            int eqIdx = entry.IndexOf('=', entry.StartsWith('=') ? 1 : 0);
            Assert.True(eqIdx > 0, $"Malformed environment entry without key=value separator: '{entry}'");

            string key = entry[..eqIdx];
            if (previousKey != null)
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(previousKey, key);
                Assert.True(cmp <= 0, $"Environment block not sorted case-insensitively: '{previousKey}' appeared before '{key}'.");
            }
            previousKey = key;
            entrySet.Add(entry);
        }

        // Verify overridden launcher child variables are preserved with exact values
        Assert.Contains("HON_MODERN_HUD=1", entrySet);
        Assert.Contains("HON_FSR_ENABLE=1", entrySet);
        Assert.Contains(@"DXVK_CONFIG_FILE=C:\Kongor\userdataKONGOR\dxvk.conf", entrySet);
        Assert.Contains("a_custom_first=head", entrySet);
        Assert.Contains("z_custom_last=tail", entrySet);

        // Verify at least one inherited parent process variable (e.g. PATH) survived alongside overrides
        string? parentPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(parentPath))
        {
            Assert.Contains(entries, e =>
                e.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase) &&
                e.Length > 5);
        }
    }

    [Fact]
    public void BuildUnicodeEnvironmentBlock_DictionaryOverload_SkipsEmptyKeysNormalizesNullValuesAndSortsCaseInsensitively()
    {
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            [""] = "ignored",
            ["HON_MODERN_HUD"] = "1",
            ["hon_fsr_enable"] = "1",
            ["DXVK_CONFIG_FILE"] = @"C:\Kongor\dxvk.conf",
            ["EMPTY_VALUE_VAR"] = null,
            ["b_second"] = "two",
            ["A_FIRST"] = "one"
        };

        byte[] block = NativeProcessInjectionService.BuildUnicodeEnvironmentBlock(env);

        Assert.Equal(0, block.Length % 2);
        Assert.Equal(0x00, block[^4]);
        Assert.Equal(0x00, block[^3]);
        Assert.Equal(0x00, block[^2]);
        Assert.Equal(0x00, block[^1]);

        string decoded = Encoding.Unicode.GetString(block);
        string[] entries = decoded[..^2].Split('\0', StringSplitOptions.None);

        Assert.Equal(
            [
                "A_FIRST=one",
                "b_second=two",
                @"DXVK_CONFIG_FILE=C:\Kongor\dxvk.conf",
                "EMPTY_VALUE_VAR=",
                "hon_fsr_enable=1",
                "HON_MODERN_HUD=1"
            ],
            entries);
    }

    [Fact]
    public void BuildUnicodeEnvironmentBlock_EmptyInput_ReturnsDoubleNullTerminatedUtf16Le()
    {
        Dictionary<string, string?> empty = new();
        byte[] block = NativeProcessInjectionService.BuildUnicodeEnvironmentBlock(empty);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, block);
    }
}
