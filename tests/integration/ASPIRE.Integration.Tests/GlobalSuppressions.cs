// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

[assembly:
    SuppressMessage("ReSharper", "InconsistentNaming",
        Justification =
            "xUnit test method naming convention uses underscores for readability (Method_Scenario_Behavior)")]
[assembly:
    SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
        Justification =
            "xUnit test method naming convention uses underscores for readability (Method_Scenario_Behavior)")]
[assembly:
    SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "xUnit CollectionDefinition class naming convention uses Collection suffix")]
[assembly:
    SuppressMessage("Naming", "CA1725:Parameter names should match base declaration",
        Justification = "Redis mock stub auto-implementation parameter naming")]
[assembly:
    SuppressMessage("Performance", "CA1835:Prefer the 'Memory'-based overload for 'ReadAsync'",
        Justification = "Test helper byte buffer stream reading")]
[assembly:
    SuppressMessage("ReSharper", "UseMemoryWithStreamReadAsync",
        Justification = "Test helper byte buffer stream reading")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditional",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("ReSharper", "LoggingMessageIsConditional",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("ReSharper", "LogMessageIsConditionalWarning",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("ReSharper", "PassStringInterpolationToLog",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("ReSharper", "LoggerMessageAttributeIsRecommended",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("ReSharper", "StructuredLoggingMethodOverloadIsRecommended",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly: SuppressMessage("ReSharper", "UseRegexSourceGenerator", Justification = "Dynamic regex test validation")]
[assembly:
    SuppressMessage("Performance", "CA1848:Use the LoggerMessageDelegates",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("Usage", "CA2254:Template should be a static expression",
        Justification = "Test assertions and diagnostic log mocking")]
[assembly:
    SuppressMessage("xUnit", "xUnit1004:Test methods should not be skipped",
        Justification = "Performance benchmark tests are skipped during standard CI test runs")]
[assembly:
    SuppressMessage("SYSLIB", "SYSLIB0052:Type or member is obsolete",
        Justification = "Test suite regex pattern testing")]
[assembly:
    SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments",
        Justification = "Test payload assertions")]
[assembly:
    SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
        Justification = "Test fixture variables")]
[assembly:
    SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Legacy HoN client protocol testing")]
[assembly:
    SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Legacy HoN client protocol testing")]
