using CommunityToolkit.Mvvm.ComponentModel;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// Shell view model. The full GCS layout (map, telemetry, mission planner) is built in Phase 5.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Backend: not connected";

    public string Title { get; } = "UAV Ground Control Station";
}
