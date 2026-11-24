// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

public class FragEvent
{
    public int SourceID { get; set; }

    public int TargetID { get; set; }

    public int GameTimeSeconds { get; set; }

    public List<int>? SupporterIDs { get; set; } = [];

    [NotMapped]
    public int KillerAccountID
    {
        get => SourceID;
        set => SourceID = value;
    }

    [NotMapped]
    public int VictimAccountID
    {
        get => TargetID;
        set => TargetID = value;
    }

    [NotMapped]
    public int GameTime
    {
        get => GameTimeSeconds;
        set => GameTimeSeconds = value;
    }

    [NotMapped]
    public List<int>? AssisterAccountIDs
    {
        get => SupporterIDs;
        set => SupporterIDs = value;
    }

    [NotMapped]
    public List<int>? AssistAccountIDs
    {
        get => SupporterIDs;
        set => SupporterIDs = value;
    }
}
