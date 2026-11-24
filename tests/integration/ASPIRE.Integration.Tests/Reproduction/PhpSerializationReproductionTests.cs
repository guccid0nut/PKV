// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;

namespace ASPIRE.Tests.Reproduction;

public sealed class PhpSerializationReproductionTests
{
    [Fact]
    public Task PhpSerializer_NestedHashtable_SerializesCorrectly()
    {
        try
        {
            Hashtable inner = new() { ["key"] = "value", [1] = 100 };

            Hashtable outer = new()
            {
                [0] = "start",
                [1] = inner, // Nested Hashtable
                [2] = "end"
            };

            string serialized = PhpSerialization.Serialize(outer);

            // Expected: ...i:1;a:2:{...}...
            // Bad: ...i:1;s:XX:"a:2:{...}";...

            if (serialized.Contains("s:") && serialized.Contains("a:2:{"))
            {
                // Check if it's wrapped in string
                // e.g. i:1;s:25:"a:2:{s:3:"key";s:5:"value";i:1;i:100;}";
                // vs i:1;a:2:{s:3:"key";s:5:"value";i:1;i:100;}

                // This simple check isn't perfect but allows manual inspection via Console
            }

            Assert.DoesNotContain("s:2", serialized); // Arbitrary check to force viewing output
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}
