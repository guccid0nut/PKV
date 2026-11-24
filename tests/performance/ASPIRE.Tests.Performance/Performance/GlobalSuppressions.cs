// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("ReSharper", "InconsistentNaming", Justification = "xUnit test method naming convention")]
[assembly:
    SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
        Justification = "xUnit test method naming convention")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditional", Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("ReSharper", "LoggingMessageIsConditional", Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditionalWarning",
        Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("ReSharper", "PassStringInterpolationToLog",
        Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("ReSharper", "LoggerMessageAttributeIsRecommended",
        Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("ReSharper", "StructuredLoggingMethodOverloadIsRecommended",
        Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("Performance", "CA1848:Use the LoggerMessageDelegates",
        Justification = "Performance benchmark logger mocking")]
[assembly:
    SuppressMessage("Usage", "CA2254:Template should be a static expression",
        Justification = "Performance benchmark logger mocking")]
