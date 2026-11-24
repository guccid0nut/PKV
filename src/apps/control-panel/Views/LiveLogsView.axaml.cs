// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace NEXUS.ControlPanel.GUI.Views;

public partial class LiveLogsView : UserControl
{
    private LiveLogsViewModel? _viewModel;
    private bool _isScrollPending;

    public LiveLogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.FilteredLogs.CollectionChanged -= OnFilteredLogsCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as LiveLogsViewModel;

        if (_viewModel != null)
        {
            _viewModel.FilteredLogs.CollectionChanged += OnFilteredLogsCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            if (_viewModel.AutoScroll && _viewModel.FilteredLogs.Count > 0)
            {
                ScheduleScrollToBottom();
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveLogsViewModel.AutoScroll) && _viewModel?.AutoScroll == true)
        {
            ScheduleScrollToBottom();
        }
    }

    private void OnFilteredLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll == true)
        {
            ScheduleScrollToBottom();
        }
    }

    private void ScheduleScrollToBottom()
    {
        if (_isScrollPending) return;
        _isScrollPending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _isScrollPending = false;
            if (_viewModel == null || _viewModel.FilteredLogs.Count == 0) return;

            var listBox = this.FindControl<ListBox>("LogListBox");
            if (listBox != null && _viewModel.FilteredLogs.Count > 0)
            {
                var lastItem = _viewModel.FilteredLogs[^1];
                listBox.ScrollIntoView(lastItem);
            }
        }, DispatcherPriority.Background);
    }
}
