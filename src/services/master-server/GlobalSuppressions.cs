// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditional",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "LoggingMessageIsConditional",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditionalWarning",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "StringInterpolationInLoggingIsExpensive",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "PassStringInterpolationToLog",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "LoggerMessageAttributeIsRecommended",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("ReSharper", "StructuredLoggingMethodOverloadIsRecommended",
        Justification = "Structured logging calls in MasterServer handlers and controllers")]
[assembly:
    SuppressMessage("Performance", "CA1848:Use the LoggerMessageDelegates",
        Justification = "MasterServer handlers and controllers use standard structured logging")]
[assembly:
    SuppressMessage("Usage", "CA2254:Template should be a static expression",
        Justification = "MasterServer handlers and controllers use standard structured logging")]
[assembly:
    SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification =
            "MasterServer legacy PHP string protocol handlers use default culture or explicit invariant string helpers")]
[assembly:
    SuppressMessage("Globalization", "CA1305:Specify CultureInfo",
        Justification =
            "MasterServer legacy PHP string protocol handlers use default culture or explicit invariant string helpers")]
[assembly:
    SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity",
        Justification = "MasterServer string parsing helpers")]
[assembly:
    SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version",
        Justification = "MasterServer legacy PHP string protocol handlers")]
[assembly:
    SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "Legacy HoN client protocol requires MD5 and SHA1 hashes for SRP auth and match stats verification")]
[assembly:
    SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "Legacy HoN client protocol requires MD5 and SHA1 hashes for SRP auth and match stats verification")]
