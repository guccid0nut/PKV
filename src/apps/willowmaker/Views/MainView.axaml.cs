// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Linq;

namespace WILLOWMAKER.Core.Views;

public partial class MainView : UserControl
{
    // The Duration For Which The Play Button Must Be Held To Arm The Hidden "Launch Without Synchronisation" Gesture
    private const double HoldDurationSeconds = 1.5;

    // The Delay Before The Hold Fill Becomes Visible, So That A Regular Quick Click Does Not Briefly Flash The Fill
    private const double FillStartDelaySeconds = 0.5;

    // The Style Class Marking A Press That Has Not Yet Lasted Long Enough To Count As A Deliberate Hold; It Is Set For The Initial Window (And Thus For The Whole Of A Quick Click) And Removed Once The Delay Elapses, At Which Point The Theme's Default Pressed Appearance Takes Over As The "Loading" Cue
    private const string HoldPendingClass = "HoldPending";
    private readonly Stopwatch launchGameClientHoldStopwatch = new();

    private readonly DispatcherTimer launchGameClientHoldTimer;

    // Set Once The Hold Reaches Its Full Duration So The Subsequent Pointer Release Does Not Also Trigger A Normal Launch
    private bool launchGameClientHoldCompleted;

    public MainView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            MainViewModel? vm = DataContext as MainViewModel;
            LauncherSettingsModel settings = LauncherSettingsService.Load();
            string targetAddress = (vm != null && !string.IsNullOrWhiteSpace(vm.LoadedMasterServerAddress))
                ? vm.LoadedMasterServerAddress.Trim()
                : (settings.MasterServerAddress?.Trim() ?? string.Empty);

            if (vm != null && vm.HasActiveCliMasterServerOverride)
            {
                vm.Log(LogCategory.General, $"[ControlPanelOverride] MainView bound dropdown to CLI override '{targetAddress}'.");
            }
            else
            {
                vm?.Log(LogCategory.General, $"[MainView] Bound dropdown to saved master server setting '{targetAddress}'.");
            }

            if (!EndpointPairRegistry.IsKongorNetPkvEnabled && targetAddress.Contains("kongor.net", StringComparison.OrdinalIgnoreCase))
            {
                targetAddress = EndpointPairRegistry.MasterServerNewbsIo;
            }

            vm?.RegisterMasterServerComboItems(MasterServerAddressDropdown.Items.OfType<ComboBoxItem>());
            vm?.BeginInitialViewRestore();
            try
            {
                if (vm != null && !string.IsNullOrWhiteSpace(settings.CustomMasterServerAddress))
                {
                    vm.CustomMasterServerAddress = settings.CustomMasterServerAddress.Trim();
                }

                if (!string.IsNullOrWhiteSpace(targetAddress))
                {
                    bool isCustom = targetAddress.Contains("Custom", StringComparison.OrdinalIgnoreCase);
                    bool matched = false;

                    if (!isCustom)
                    {
                        for (int i = 0; i < MasterServerAddressDropdown.Items.Count; i++)
                        {
                            if (MasterServerAddressDropdown.Items[i] is ComboBoxItem item)
                            {
                                string itemText = item.Content?.ToString()?.Trim() ?? string.Empty;
                                if (itemText.Equals(targetAddress, StringComparison.OrdinalIgnoreCase) ||
                                    (itemText.Equals(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer, StringComparison.OrdinalIgnoreCase) &&
                                     (targetAddress.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                      targetAddress.Equals("local-auto", StringComparison.OrdinalIgnoreCase))) ||
                                    (itemText.Equals(LauncherSettingsModel.OfflineModeSelection, StringComparison.OrdinalIgnoreCase) &&
                                     targetAddress.Equals("offline", StringComparison.OrdinalIgnoreCase)) ||
                                    (itemText.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase) &&
                                     (targetAddress.Equals("127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) ||
                                      targetAddress.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase))))
                                {
                                    MasterServerAddressDropdown.SelectedIndex = i;
                                    matched = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!matched)
                    {
                        // Select "Custom Address ..." if present
                        for (int i = 0; i < MasterServerAddressDropdown.Items.Count; i++)
                        {
                            if (MasterServerAddressDropdown.Items[i] is ComboBoxItem item &&
                                item.Content?.ToString()?.Contains("Custom", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                MasterServerAddressDropdown.SelectedIndex = i;
                                if (vm != null)
                                {
                                    if ((string.IsNullOrWhiteSpace(vm.CustomMasterServerAddress) || vm.HasActiveCliMasterServerOverride) && !isCustom)
                                    {
                                        vm.CustomMasterServerAddress = targetAddress;
                                    }
                                    vm.CanShowCustomMasterServerAddressField = true;
                                    vm.MasterServerAddressIsValid = !string.IsNullOrWhiteSpace(vm.CustomMasterServerAddress);
                                }
                                matched = true;
                                break;
                            }
                        }
                    }

                    if (!matched && MasterServerAddressDropdown.SelectedIndex < 0)
                    {
                        MasterServerAddressDropdown.SelectedIndex = 0;
                    }
                }
                else if (MasterServerAddressDropdown.SelectedIndex < 0)
                {
                    MasterServerAddressDropdown.SelectedIndex = 0;
                }
            }
            finally
            {
                vm?.EndInitialViewRestore();
            }
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is MainViewModel mainVm)
            {
                mainVm.SaveLauncherSettings();
            }
        };

        launchGameClientHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        launchGameClientHoldTimer.Tick += LaunchGameClientHoldTimer_Tick;

        // "Button" Marks Its Pointer Press And Release As Handled In Its Own Class Handlers, So The Gesture Handlers Are Registered With "handledEventsToo" To Still Receive Them
        LaunchGameClientButton.AddHandler(PointerPressedEvent, LaunchGameClientButton_PointerPressed,
            RoutingStrategies.Bubble, true);
        LaunchGameClientButton.AddHandler(PointerReleasedEvent, LaunchGameClientButton_PointerReleased,
            RoutingStrategies.Bubble, true);
        LaunchGameClientButton.AddHandler(PointerExitedEvent, LaunchGameClientButton_PointerExited,
            RoutingStrategies.Bubble, true);
        LaunchGameClientButton.AddHandler(PointerCaptureLostEvent, LaunchGameClientButton_PointerCaptureLost,
            RoutingStrategies.Bubble, true);

        // Interactive 3D HRTF Spatial Radar Events
        SpatialRadarHost.AddHandler(PointerPressedEvent, SpatialRadar_PointerPressed, RoutingStrategies.Bubble, true);
        SpatialRadarHost.AddHandler(PointerMovedEvent, SpatialRadar_PointerMoved, RoutingStrategies.Bubble, true);
        SpatialRadarHost.AddHandler(PointerReleasedEvent, SpatialRadar_PointerReleased, RoutingStrategies.Bubble, true);
    }

    private void LaunchGameClientButton_PointerPressed(object? sender, PointerPressedEventArgs arguments)
    {
        // Only A Primary (Left) Button Press Begins The Hold; Other Buttons Are Ignored
        if (arguments.GetCurrentPoint(LaunchGameClientButton).Properties.IsLeftButtonPressed is false)
            return;

        launchGameClientHoldCompleted = false;

        if (LaunchGameClientButton.Classes.Contains(HoldPendingClass) is false)
            LaunchGameClientButton.Classes.Add(HoldPendingClass);

        arguments.Pointer.Capture(LaunchGameClientButton);

        launchGameClientHoldStopwatch.Restart();
        launchGameClientHoldTimer.Start();
    }

    private void LaunchGameClientButton_PointerReleased(object? sender, PointerReleasedEventArgs arguments)
    {
        double elapsedSeconds = launchGameClientHoldStopwatch.Elapsed.TotalSeconds;
        bool holdCompleted = launchGameClientHoldCompleted;

        CancelLaunchGameClientHold();

        launchGameClientHoldCompleted = false;

        // A Completed Hold Has Already Armed The Bypass Launch; And Once The Press Has Lasted Past The Pending Window It Is A Committed Hold That The User Released Early, So Neither Case Fires A Normal Launch
        if (holdCompleted || elapsedSeconds >= FillStartDelaySeconds)
            return;

        // A Quick Press And Release Over The Button Is A Normal Click: Launch The Game Client With Synchronisation
        if (arguments.InitialPressMouseButton is MouseButton.Left && IsPointerWithinLaunchGameClientButton(arguments) &&
            DataContext is MainViewModel viewModel)
            viewModel.LaunchGameClientCommand.Execute(null);
    }

    private void LaunchGameClientButton_PointerExited(object? sender, PointerEventArgs arguments)
    {
        CancelLaunchGameClientHold();
    }

    private void LaunchGameClientButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs arguments)
    {
        CancelLaunchGameClientHold();
    }

    private void LaunchGameClientHoldTimer_Tick(object? sender, EventArgs arguments)
    {
        double elapsedSeconds = launchGameClientHoldStopwatch.Elapsed.TotalSeconds;

        // The Fill Stays Empty Until The Start Delay Elapses, Then Grows Across The Remaining Hold Window So It Begins From Zero And Reaches Full Exactly At The Trigger
        double fillFraction =
            Math.Clamp((elapsedSeconds - FillStartDelaySeconds) / (HoldDurationSeconds - FillStartDelaySeconds), 0, 1);

        LaunchGameClientHoldFill.Width = fillFraction * LaunchGameClientHoldFillHost.Bounds.Width;

        // Once The Delay Has Elapsed The Press Counts As A Deliberate Hold, Drop The Pending Marker And Let The Theme's Default Pressed Appearance Show Alongside The Fill As A "Loading" Cue
        if (elapsedSeconds >= FillStartDelaySeconds)
            LaunchGameClientButton.Classes.Remove(HoldPendingClass);

        if (elapsedSeconds < HoldDurationSeconds)
            return;

        launchGameClientHoldCompleted = true;

        CancelLaunchGameClientHold();

        if (DataContext is MainViewModel viewModel)
            viewModel.LaunchGameClientWithoutSynchronisationCommand.Execute(null);
    }

    private void CancelLaunchGameClientHold()
    {
        launchGameClientHoldTimer.Stop();
        launchGameClientHoldStopwatch.Reset();

        LaunchGameClientHoldFill.Width = 0;

        LaunchGameClientButton.Classes.Remove(HoldPendingClass);
    }

    private bool IsPointerWithinLaunchGameClientButton(PointerReleasedEventArgs arguments)
    {
        Point position = arguments.GetPosition(LaunchGameClientButton);

        return position.X >= 0 && position.Y >= 0 && position.X <= LaunchGameClientButton.Bounds.Width &&
               position.Y <= LaunchGameClientButton.Bounds.Height;
    }

    private void CustomMasterServerAddress_LostFocus(object? sender, RoutedEventArgs arguments)
    {
        if (DataContext is MainViewModel viewModel) viewModel.LogCustomMasterServerAddressCommand.Execute(null);
    }

    private void CustomMasterServerAddress_KeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Enter && sender is InputElement inputElement)
        {
            Visual? parent = inputElement.GetVisualParent();

            while (parent is not null)
            {
                if (parent is InputElement focusableParent && focusableParent.Focusable)
                {
                    focusableParent.Focus();

                    break;
                }

                parent = parent.GetVisualParent();
            }

            arguments.Handled = true;
        }
    }

    private void CustomCDNAddress_LostFocus(object? sender, RoutedEventArgs arguments)
    {
        // Custom CDN address lost focus - trigger validation if needed
    }

    private void CustomCDNAddress_KeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Enter && sender is InputElement inputElement)
        {
            Visual? parent = inputElement.GetVisualParent();

            while (parent is not null)
            {
                if (parent is InputElement focusableParent && focusableParent.Focusable)
                {
                    focusableParent.Focus();

                    break;
                }

                parent = parent.GetVisualParent();
            }

            arguments.Handled = true;
        }
    }

    private void Background_PointerPressed(object? sender, RoutedEventArgs arguments)
    {
        if (sender is InputElement element) element.Focus();
    }

    private void SpatialRadar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(SpatialRadarHost).Properties.IsLeftButtonPressed &&
            DataContext is MainViewModel viewModel)
        {
            e.Pointer.Capture(SpatialRadarHost);
            Point pt = e.GetPosition(SpatialRadarHost);
            UpdateSpatialRadarCoordinates(pt, viewModel);
        }
    }

    private void SpatialRadar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(SpatialRadarHost).Properties.IsLeftButtonPressed &&
            DataContext is MainViewModel viewModel)
        {
            Point pt = e.GetPosition(SpatialRadarHost);
            UpdateSpatialRadarCoordinates(pt, viewModel);
        }
    }

    private void SpatialRadar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
    }

    private void UpdateSpatialRadarCoordinates(Point pt, MainViewModel viewModel)
    {
        double width = SpatialRadarHost.Bounds.Width;
        double height = SpatialRadarHost.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            width = 130;
            height = 130;
        }

        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double radius = Math.Min(centerX, centerY) - 8.0;

        double dx = (pt.X - centerX) / radius;
        double dy = (pt.Y - centerY) / radius;

        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > 1.0)
        {
            dx /= dist;
            dy /= dist;
            dist = 1.0;
        }

        // dy < 0 is Front (0°), dx > 0 is Right (+90°), dy > 0 is Behind (180°), dx < 0 is Left (-90°)
        float angleDeg = (float)(Math.Atan2(dx, -dy) * (180.0 / Math.PI));

        // Canvas beacon placement (beacon circle is 14x14, center offset is 7)
        double beaconX = centerX + (dx * radius) - 7.0;
        double beaconY = centerY + (dy * radius) - 7.0;

        viewModel.SetSpatialRadarCoordinates((float)dx, (float)dy, angleDeg, (float)dist, beaconX, beaconY);
    }
}
