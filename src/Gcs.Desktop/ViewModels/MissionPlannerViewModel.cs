using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Contracts.Missions;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// The mission planner: list of missions, the mission being edited (items, name), save with optimistic concurrency,
/// server-side validation results per row, and upload to the selected vehicle.
/// </summary>
public sealed partial class MissionPlannerViewModel : ObservableObject
{
    private const double EarthRadiusMetres = 6_371_000;

    private readonly IGcsApiClient _api;
    private readonly Func<VehicleItemViewModel?> _selectedVehicle;
    private Guid? _missionId;
    private int _version;

    public MissionPlannerViewModel(IGcsApiClient api, Func<VehicleItemViewModel?> selectedVehicle)
    {
        _api = api;
        _selectedVehicle = selectedVehicle;
        Items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>Raised whenever the route on the map must be redrawn (items added, removed, moved or edited).</summary>
    public event EventHandler? RouteChanged;

    public ObservableCollection<MissionSummaryResponse> Missions { get; } = [];

    public ObservableCollection<MissionItemViewModel> Items { get; } = [];

    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty]
    private MissionSummaryResponse? _selectedMission;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveItemCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private MissionItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _name = "New mission";

    [ObservableProperty]
    private bool _isFlyable;

    [ObservableProperty]
    private string? _status;

    /// <summary>When on, clicking the map adds a waypoint there.</summary>
    [ObservableProperty]
    private bool _isAddingWaypoints;

    public bool IsSaved => _missionId is not null;

    /// <summary>Live distance of the route, computed locally so it updates while editing.</summary>
    public string Distance
    {
        get
        {
            var positioned = Items.Where(i => i.HasPosition).ToList();
            var metres = positioned.Zip(positioned.Skip(1), Haversine).Sum();
            return metres >= 1000
                ? string.Create(CultureInfo.InvariantCulture, $"{metres / 1000:0.00} km")
                : string.Create(CultureInfo.InvariantCulture, $"{metres:0} m");
        }
    }

    public async Task LoadMissionsAsync(CancellationToken cancellationToken)
    {
        Missions.Clear();
        foreach (var mission in await _api.GetMissionsAsync(cancellationToken))
        {
            Missions.Add(mission);
        }

        SelectedMission = Missions.FirstOrDefault(m => m.Id == _missionId);
    }

    [RelayCommand]
    private void NewMission()
    {
        _missionId = null;
        _version = 0;
        Name = $"Mission {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
        Items.Clear();
        Items.Add(new MissionItemViewModel { Command = "Takeoff", Altitude = MissionItemViewModel.DefaultTakeoffAltitude });
        Items.Add(new MissionItemViewModel { Command = "ReturnToLaunch" });
        Issues.Clear();
        IsFlyable = false;
        SelectedMission = null;
        Status = "New mission: click the map to add waypoints.";
        IsAddingWaypoints = true;
    }

    async partial void OnSelectedMissionChanged(MissionSummaryResponse? value)
    {
        if (value is null || value.Id == _missionId)
        {
            return;
        }

        await RunAsync(async () => Show(await _api.GetMissionAsync(value.Id, CancellationToken.None), "Loaded."));
    }

    /// <summary>Adds a waypoint at a map position, before the final return/land item so the plan stays flyable.</summary>
    public void AddWaypointAt(double latitude, double longitude)
    {
        var waypoint = new MissionItemViewModel
        {
            Command = "Waypoint",
            Latitude = Math.Round(latitude, 7),
            Longitude = Math.Round(longitude, 7),
            Altitude = Items.LastOrDefault(i => i is { Command: "Waypoint" or "Loiter", Altitude: not null })?.Altitude
                ?? MissionItemViewModel.DefaultAltitude,
        };
        var insertAt = Items.Count > 0 && Items[^1].Command is "ReturnToLaunch" or "Land" ? Items.Count - 1 : Items.Count;
        Items.Insert(insertAt, waypoint);
        SelectedItem = waypoint;
    }

    [RelayCommand]
    private void AddItem(string command)
    {
        var item = new MissionItemViewModel
        {
            Command = command,
            Altitude = command == "Takeoff" ? MissionItemViewModel.DefaultTakeoffAltitude : null,
        };
        if (command == "Takeoff")
        {
            Items.Insert(0, item);
        }
        else
        {
            Items.Add(item);
        }

        SelectedItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveItem()
    {
        var index = Items.IndexOf(SelectedItem!);
        Items.RemoveAt(index);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(+1);

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var request = new SaveMissionRequest(Name, [.. Items.Select(i => i.ToDto())]);
        var saved = _missionId is { } id
            ? await _api.UpdateMissionAsync(id, _version, request, CancellationToken.None)
            : await _api.CreateMissionAsync(request, CancellationToken.None);
        Show(saved, saved.IsFlyable ? "Saved. Ready to upload." : "Saved as draft (see issues).");
        await LoadMissionsAsync(CancellationToken.None);
    });

    [RelayCommand]
    private Task UploadAsync() => RunAsync(async () =>
    {
        if (_selectedVehicle() is not { } vehicle)
        {
            Status = "Select a vehicle in the Flight tab first.";
            return;
        }

        await SaveAsync();
        if (_missionId is not { } id || !IsFlyable)
        {
            return;
        }

        var result = await _api.UploadMissionAsync(id, vehicle.Id, CancellationToken.None);
        Show(result, $"Uploaded to {vehicle.Callsign} at {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}.");
    });

    [RelayCommand]
    private Task DownloadFromVehicleAsync() => RunAsync(async () =>
    {
        if (_selectedVehicle() is not { } vehicle)
        {
            Status = "Select a vehicle in the Flight tab first.";
            return;
        }

        var onVehicle = await _api.DownloadVehicleMissionAsync(vehicle.Id, CancellationToken.None);
        _missionId = null;
        _version = 0;
        Name = $"From {vehicle.Callsign}";
        ReplaceItems(onVehicle.Items);
        Issues.Clear();
        Status = $"Downloaded {onVehicle.Items.Count} items from {vehicle.Callsign}. Save to keep it.";
    });

    [RelayCommand]
    private Task ArchiveAsync() => RunAsync(async () =>
    {
        if (_missionId is not { } id)
        {
            return;
        }

        await _api.ArchiveMissionAsync(id, CancellationToken.None);
        await LoadMissionsAsync(CancellationToken.None);
        NewMission();
        Status = "Mission archived.";
    });

    private bool HasSelection() => SelectedItem is not null;

    private bool CanMoveUp() => SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;

    private bool CanMoveDown() => SelectedItem is not null && Items.IndexOf(SelectedItem) < Items.Count - 1;

    private void Move(int offset)
    {
        var index = Items.IndexOf(SelectedItem!);
        Items.Move(index, index + offset);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void Show(MissionResponse mission, string status)
    {
        _missionId = mission.Id;
        _version = mission.Version;
        Name = mission.Name;
        ReplaceItems(mission.Items);
        Issues.Clear();
        foreach (var issue in mission.Issues)
        {
            Issues.Add(issue.ItemIndex is { } i ? $"Item {i + 1}: {issue.Message}" : issue.Message);
            if (issue.ItemIndex is { } index && index < Items.Count)
            {
                Items[index].Issue = issue.Message;
            }
        }

        IsFlyable = mission.IsFlyable;
        OnPropertyChanged(nameof(IsSaved));
        Status = mission.LastUpload is { Succeeded: false } failed && status.StartsWith("Loaded", StringComparison.Ordinal)
            ? $"Loaded. Last upload failed: {failed.Error}"
            : status;
    }

    private void ReplaceItems(IEnumerable<MissionItemDto> items)
    {
        Items.Clear();
        foreach (var dto in items)
        {
            Items.Add(MissionItemViewModel.From(dto));
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiProblemException ex)
        {
            Status = ex.Describe();
        }
        catch (HttpRequestException ex)
        {
            Status = $"Backend unreachable: {ex.Message}";
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (MissionItemViewModel item in e.OldItems ?? Array.Empty<MissionItemViewModel>())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        foreach (MissionItemViewModel item in e.NewItems ?? Array.Empty<MissionItemViewModel>())
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Index = i;
        }

        OnRouteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MissionItemViewModel.Latitude) or nameof(MissionItemViewModel.Longitude)
            or nameof(MissionItemViewModel.Command) or nameof(MissionItemViewModel.Altitude))
        {
            OnRouteChanged();
        }
    }

    private void OnRouteChanged()
    {
        OnPropertyChanged(nameof(Distance));
        RouteChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double Haversine(MissionItemViewModel a, MissionItemViewModel b)
    {
        var lat1 = a.Latitude!.Value * Math.PI / 180;
        var lat2 = b.Latitude!.Value * Math.PI / 180;
        var dLon = (b.Longitude!.Value - a.Longitude!.Value) * Math.PI / 180;
        var h = Math.Pow(Math.Sin((lat2 - lat1) / 2), 2) + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadiusMetres * Math.Asin(Math.Sqrt(h));
    }
}
