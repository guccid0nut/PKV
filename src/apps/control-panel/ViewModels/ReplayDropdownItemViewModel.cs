// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed class ReplayDropdownItemViewModel
{
    public ReplayDropdownItemViewModel(string filePath, string directoryType = "Userdata")
    {
        FilePath = filePath;
        DirectoryType = directoryType;
        FileInfo info = new(filePath);
        FileName = info.Name;
        FileSizeBytes = info.Exists ? info.Length : 0;
        LastModified = info.Exists ? info.LastWriteTime : DateTime.MinValue;

        FileSizeDisplay = $"{FileSizeBytes / 1024.0:F1} KB";
        ModifiedDateDisplay = LastModified != DateTime.MinValue
            ? LastModified.ToString("MMM dd, yyyy HH:mm")
            : "Unknown Date";

        DisplayName = $"{FileName}  [{FileSizeDisplay} • {LastModified:MMM dd}]";
    }

    public string FileName { get; }
    public string FilePath { get; }
    public string DirectoryType { get; }
    public long FileSizeBytes { get; }
    public string FileSizeDisplay { get; }
    public DateTime LastModified { get; }
    public string ModifiedDateDisplay { get; }
    public string DisplayName { get; }

    public override string ToString()
    {
        return DisplayName;
    }
}
