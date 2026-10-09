using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Gcs.Desktop.Map;
using Gcs.Desktop.ViewModels;

namespace Gcs.Desktop.Views;

/// <summary>
/// The map control is not bindable in MVVM style, so the view wires it up: it watches the telemetry view model and
/// moves the vehicle marker, redraws the planned route when the mission changes, and turns map clicks into waypoints.
/// Everything else is pure data binding.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia windows are not IDisposable; the map is disposed in the Closed event.")]
public sealed partial class MainWindow : Window
{
    /// <summary>A press and release closer than this (in pixels) is a click; anything further is a pan.</summary>
    private const double ClickTolerance = 4;

    /// <summary>Window width under which secondary top-bar details are hidden instead of being clipped.</summary>
    private const double CompactWidth = 1380;

    private readonly VehicleMap _map = new();
    private MainWindowViewModel? _viewModel;
    private Point? _pressedAt;

    public MainWindow()
    {
        InitializeComponent();
        MapControl.Map = _map.Map;
        AttributionText.Text = VehicleMap.Attribution;
        FollowToggle.IsCheckedChanged += (_, _) => _map.FollowVehicle = FollowToggle.IsChecked == true;
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        Closed += (_, _) => _map.Dispose();

        // Below this width the top bar keeps only the vehicle, its link and armed state (Themes/Controls.axaml).
        SizeChanged += (_, e) => Classes.Set("compact", e.NewSize.Width < CompactWidth);

        // The map control handles pointer events itself (panning), so listen to handled events too.
        MapControl.AddHandler(PointerPressedEvent, OnMapPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        MapControl.AddHandler(PointerReleasedEvent, OnMapPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Attach(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Telemetry.PropertyChanged -= OnTelemetryChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Planner.RouteChanged -= OnRouteChanged;
            _viewModel.Planner.PropertyChanged -= OnPlannerChanged;
            _viewModel.Planner.MissionOpened -= OnMissionOpened;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Telemetry.PropertyChanged += OnTelemetryChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Planner.RouteChanged += OnRouteChanged;
            _viewModel.Planner.PropertyChanged += OnPlannerChanged;
            _viewModel.Planner.MissionOpened += OnMissionOpened;
            DrawRoute();
        }
    }

    private void OnTelemetryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelemetryViewModel.Position) && _viewModel?.Telemetry is { Position: { } position } telemetry)
        {
            _map.Update(position, telemetry.HeadingDegrees);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedVehicle))
        {
            _map.Clear();
            DrawRoute();
        }
    }

    private void OnPlannerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MissionPlannerViewModel.SelectedItem):
                DrawRoute();
                break;
            case nameof(MissionPlannerViewModel.IsAddingWaypoints) when _viewModel?.Planner.IsAddingWaypoints == true:
                // A map that keeps re-centring on the vehicle is impossible to click on.
                FollowToggle.IsChecked = false;
                break;
        }
    }

    private void OnRouteChanged(object? sender, EventArgs e) => DrawRoute();

    private void OnMissionOpened(object? sender, EventArgs e)
    {
        // Show the whole route; following the vehicle would immediately pan away from it.
        FollowToggle.IsChecked = false;
        _map.ZoomToMission(MissionPoints());
    }

    private void DrawRoute() => _map.ShowMission(MissionPoints());

    private List<MissionMapPoint> MissionPoints()
    {
        if (_viewModel?.Planner is not { } planner)
        {
            return [];
        }

        return [.. planner.Items
            .Where(i => i.HasPosition)
            .Select(i => new MissionMapPoint(
                i.Latitude!.Value,
                i.Longitude!.Value,
                (i.Index + 1).ToString(CultureInfo.InvariantCulture),
                ReferenceEquals(i, planner.SelectedItem)))];
    }

    private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _pressedAt = e.GetCurrentPoint(MapControl).Properties.IsLeftButtonPressed ? e.GetPosition(MapControl) : null;

    private void OnMapPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAt is not { } pressed || _viewModel?.Planner is not { IsAddingWaypoints: true } planner)
        {
            return;
        }

        _pressedAt = null;
        var released = e.GetPosition(MapControl);
        if (Math.Abs(released.X - pressed.X) > ClickTolerance || Math.Abs(released.Y - pressed.Y) > ClickTolerance)
        {
            return;
        }

        var (latitude, longitude) = _map.ToLatLon(released.X, released.Y);
        planner.AddWaypointAt(latitude, longitude);
    }
}
