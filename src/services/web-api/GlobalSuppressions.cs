// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

[assembly:
    SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
[assembly:
    SuppressMessage("Globalization", "CA1305:Specify CultureInfo",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
[assembly:
    SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
[assembly:
    SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
[assembly:
    SuppressMessage("ReSharper", "SpecifyACultureInStringConversionExplicitly",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
[assembly:
    SuppressMessage("ReSharper", "SpecifyCultureInfo",
        Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
