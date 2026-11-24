// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace NEXUS.ControlPanel.GUI.Controls;

public partial class GameServerChatConsoleControl : UserControl
{
    private GameServersViewModel? _viewModel;
    private GameServerInstanceDetailViewModel? _subscribedInstance;

    public GameServerChatConsoleControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        BindCurrentDataContext();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindCurrentDataContext();
    }

    private void BindCurrentDataContext()
    {
        UnsubscribeAll();

        _viewModel = DataContext as GameServersViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SubscribeToInstance(_viewModel.SelectedChatInstance);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeAll();
    }

    private void UnsubscribeAll()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        UnsubscribeCurrentInstance();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameServersViewModel.SelectedChatInstance))
        {
            UnsubscribeCurrentInstance();
            if (_viewModel != null)
            {
                SubscribeToInstance(_viewModel.SelectedChatInstance);
                ScrollChatToEnd();
            }
        }
    }

    private void SubscribeToInstance(GameServerInstanceDetailViewModel? instance)
    {
        _subscribedInstance = instance;
        if (_subscribedInstance != null)
        {
            _subscribedInstance.ChatMessages.CollectionChanged += OnChatMessagesCollectionChanged;
        }
    }

    private void UnsubscribeCurrentInstance()
    {
        if (_subscribedInstance != null)
        {
            _subscribedInstance.ChatMessages.CollectionChanged -= OnChatMessagesCollectionChanged;
            _subscribedInstance = null;
        }
    }

    private void OnChatMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll == true)
        {
            ScrollChatToEnd();
        }
    }

    private void ScrollChatToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ScrollViewer? scroll = this.FindControl<ScrollViewer>("ChatScrollViewer");
            scroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void CommandTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is GameServersViewModel vm)
            {
                _ = vm.SendCommandAsync();
                e.Handled = true;
                ScrollChatToEnd();
            }
        }
    }
}
