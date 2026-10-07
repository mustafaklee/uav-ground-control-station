using System.ComponentModel;
using Avalonia.Controls;
using Gcs.Desktop.Map;
using Gcs.Desktop.ViewModels;

namespace Gcs.Desktop.Views;

/// <summary>
/// The map control is not bindable in MVVM style, so the view wires it up: it watches the telemetry view model and
/// moves the vehicle marker. Everything else is pure data binding.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia windows are not IDisposable; the map is disposed in the Closed event.")]
public sealed partial class MainWindow : Window
{
    private readonly VehicleMap _map = new();
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        MapControl.Map = _map.Map;
        AttributionText.Text = VehicleMap.Attribution;
        FollowCheckBox.IsCheckedChanged += (_, _) => _map.FollowVehicle = FollowCheckBox.IsChecked == true;
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        Closed += (_, _) => _map.Dispose();
    }

    private void Attach(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Telemetry.PropertyChanged -= OnTelemetryChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Telemetry.PropertyChanged += OnTelemetryChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
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
        }
    }
}
