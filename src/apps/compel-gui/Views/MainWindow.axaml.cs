// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using COMPEL.GUI.ViewModels;

namespace COMPEL.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void CommandTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is CompelChatViewModel vm && !vm.IsSendingCommand)
            {
                _ = vm.SendCommandAsync();
                e.Handled = true;

                Dispatcher.UIThread.Post(() =>
                {
                    var scroll = this.FindControl<ScrollViewer>("ChatScrollViewer");
                    scroll?.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
        }
    }
}
