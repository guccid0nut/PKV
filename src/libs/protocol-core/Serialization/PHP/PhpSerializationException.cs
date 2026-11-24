// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

public class PhpSerializationException : Exception
{
    public PhpSerializationException()
    {
    }

    public PhpSerializationException(string message) : base(message)
    {
    }

    public PhpSerializationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
