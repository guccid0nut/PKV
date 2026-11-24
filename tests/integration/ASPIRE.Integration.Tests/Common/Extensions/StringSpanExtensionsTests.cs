// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions;

namespace ASPIRE.Tests.Common.Extensions;

public sealed class StringSpanExtensionsTests
{
    [Fact]
    public void GetPrefixSpan_Extracts_Prefix_Before_Delimiter()
    {
        const string input = "Hero_Legionnaire.Alt1";
        ReadOnlySpan<char> prefix = input.GetPrefixSpan('.');

        Assert.Equal("Hero_Legionnaire", prefix.ToString());
    }

    [Fact]
    public void GetPrefixSpan_Returns_Entire_Span_If_No_Delimiter()
    {
        const string input = "Hero_Legionnaire";
        ReadOnlySpan<char> prefix = input.GetPrefixSpan('.');

        Assert.Equal("Hero_Legionnaire", prefix.ToString());
    }

    [Fact]
    public void GetPrefixString_Extracts_Prefix_String()
    {
        const string input = "Hero_Pyromancer.Alt2";
        string result = input.GetPrefixString('.');

        Assert.Equal("Hero_Pyromancer", result);
    }

    [Fact]
    public void GetPrefixSpan_Handles_Null_And_Empty()
    {
        string? nullInput = null;
        ReadOnlySpan<char> nullSpan = nullInput.GetPrefixSpan('.');
        Assert.True(nullSpan.IsEmpty);

        string emptyInput = string.Empty;
        ReadOnlySpan<char> emptySpan = emptyInput.GetPrefixSpan('.');
        Assert.True(emptySpan.IsEmpty);
    }

    [Fact]
    public void GetPrefixString_Handles_Null_And_Empty()
    {
        string? nullInput = null;
        string nullResult = nullInput.GetPrefixString('.');
        Assert.Equal(string.Empty, nullResult);

        string emptyInput = string.Empty;
        string emptyResult = emptyInput.GetPrefixString('.');
        Assert.Equal(string.Empty, emptyResult);
    }
}
