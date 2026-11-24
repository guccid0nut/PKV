// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Views;

public partial class UpdateDialog : Window
{
    public UpdateDialog()
    {
        InitializeComponent();
    }

    public UpdateDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void Update_Click(object? sender, RoutedEventArgs arguments)
    {
        Close(true);
    }

    private void Skip_Click(object? sender, RoutedEventArgs arguments)
    {
        Close(false);
    }
}
