// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditional",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "LoggingMessageIsConditional",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditionalWarning",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "StringInterpolationInLoggingIsExpensive",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "PassStringInterpolationToLog",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "LoggerMessageAttributeIsRecommended",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("ReSharper", "StructuredLoggingMethodOverloadIsRecommended",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("Performance", "CA1848:Use the LoggerMessageDelegates",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("Usage", "CA2254:Template should be a static expression",
        Justification = "Structured logging calls in ChatServer session handlers")]
[assembly:
    SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Legacy HoN client protocol requirement")]
[assembly:
    SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Legacy HoN client protocol requirement")]
