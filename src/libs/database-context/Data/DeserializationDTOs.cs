// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Data;

// ReSharper disable NotAccessedPositionalProperty.Global
public static class DeserializationDTOs
{
    public record GuideGetDTO(
        int GuideID,
        string Name,
        string HeroName,
        string HeroIdentifier,
        string Intro,
        string Content,
        IList<string> StartingItems,
        IList<string> EarlyGameItems,
        IList<string> CoreItems,
        IList<string> LuxuryItems,
        IList<string> AbilityQueue,
        int AuthorID,
        float Rating,
        int UpVotes,
        int DownVotes,
        bool Public,
        bool Featured);
}
