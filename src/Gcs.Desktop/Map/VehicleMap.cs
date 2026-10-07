using Gcs.Contracts.Vehicles;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;

namespace Gcs.Desktop.Map;

/// <summary>A positioned mission item to draw: its number in the plan and whether it is selected in the editor.</summary>
public readonly record struct MissionMapPoint(double Latitude, double Longitude, string Label, bool IsSelected);

/// <summary>
/// Map content for the selected vehicle: OpenStreetMap tiles, a breadcrumb trail of recent positions, the home
/// position (first fix) and the vehicle marker rotated to its heading. Positions arrive in WGS84 degrees and are
/// projected to Web Mercator, the projection web map tiles use. The planned mission is drawn on its own layer.
/// </summary>
public sealed class VehicleMap : IDisposable
{
    public const string UserAgent = "uav-ground-control-station/1.0 (+https://github.com/mustafaklee/uav-ground-control-station)";
    public const string Attribution = "© OpenStreetMap contributors";
    private const int MaxTrailPoints = 600;
    private const int FollowZoomLevel = 16;

    private readonly MemoryLayer _trailLayer = new() { Name = "Trail", Style = null };
    private readonly MemoryLayer _homeLayer = new() { Name = "Home", Style = null };
    private readonly MemoryLayer _missionLayer = new() { Name = "Mission", Style = null };
    private readonly MemoryLayer _vehicleLayer = new() { Name = "Vehicle", Style = null };
    private readonly LinkedList<MPoint> _trail = new();
    private MPoint? _home;
    private bool _centeredOnce;

    public VehicleMap()
    {
        // OSM tile usage policy: identify the application with a User-Agent and show the attribution.
        Map.Layers.Add(OpenStreetMap.CreateTileLayer(UserAgent));
        Map.Layers.Add(_trailLayer);
        Map.Layers.Add(_homeLayer);
        Map.Layers.Add(_missionLayer);
        Map.Layers.Add(_vehicleLayer);
    }

    public Mapsui.Map Map { get; } = new();

    /// <summary>Keep the vehicle in view as it moves.</summary>
    public bool FollowVehicle { get; set; } = true;

    public void Update(PositionDto position, double? headingDegrees)
    {
        ArgumentNullException.ThrowIfNull(position);
        var (x, y) = SphericalMercator.FromLonLat(position.Longitude, position.Latitude);
        var point = new MPoint(x, y);

        _home ??= point;
        _trail.AddLast(point);
        while (_trail.Count > MaxTrailPoints)
        {
            _trail.RemoveFirst();
        }

        _trailLayer.Features = [.. _trail.Select(p => new PointFeature(p) { Styles = { TrailStyle } })];
        _homeLayer.Features = [new PointFeature(_home) { Styles = { HomeStyle } }];
        _vehicleLayer.Features = [new PointFeature(point) { Styles = { VehicleStyle(headingDegrees ?? 0) } }];
        _trailLayer.DataHasChanged();
        _homeLayer.DataHasChanged();
        _vehicleLayer.DataHasChanged();

        if (!_centeredOnce)
        {
            Map.Navigator.CenterOnAndZoomTo(point, Map.Navigator.Resolutions[FollowZoomLevel]);
            _centeredOnce = true;
        }
        else if (FollowVehicle)
        {
            Map.Navigator.CenterOn(point);
        }
    }

    /// <summary>Redraws the planned route: a dashed line through the positioned items and a numbered marker on each.</summary>
    public void ShowMission(IReadOnlyList<MissionMapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var projected = points.Select(p => (Point: p, Mercator: SphericalMercator.FromLonLat(p.Longitude, p.Latitude))).ToList();
        var features = new List<IFeature>();
        if (projected.Count >= 2)
        {
            var line = new LineString([.. projected.Select(p => new Coordinate(p.Mercator.x, p.Mercator.y))]);
            features.Add(new GeometryFeature(line) { Styles = { RouteStyle } });
        }

        features.AddRange(projected.Select(p => new PointFeature(p.Mercator.x, p.Mercator.y)
        {
            Styles = { p.Point.IsSelected ? SelectedWaypointStyle : WaypointStyle, WaypointLabel(p.Point.Label) },
        }));
        _missionLayer.Features = features;
        _missionLayer.DataHasChanged();

        if (!_centeredOnce && projected.Count > 0)
        {
            var (x, y) = projected[0].Mercator;
            Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), Map.Navigator.Resolutions[FollowZoomLevel]);
            _centeredOnce = true;
        }
    }

    /// <summary>Fits the view to the given mission points, with a margin so the outer markers are not on the edge.</summary>
    public void ZoomToMission(IReadOnlyList<MissionMapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return;
        }

        var projected = points.Select(p => SphericalMercator.FromLonLat(p.Longitude, p.Latitude)).ToList();
        var box = new MRect(projected.Min(p => p.x), projected.Min(p => p.y), projected.Max(p => p.x), projected.Max(p => p.y));
        var margin = Math.Max(Math.Max(box.Width, box.Height) * 0.4, 100);
        Map.Navigator.ZoomToBox(box.Grow(margin), MBoxFit.Fit);
        _centeredOnce = true;
    }

    /// <summary>Converts a point on the map control (device-independent pixels) to WGS84 degrees.</summary>
    public (double Latitude, double Longitude) ToLatLon(double screenX, double screenY)
    {
        var world = Map.Navigator.Viewport.ScreenToWorld(screenX, screenY);
        var (lon, lat) = SphericalMercator.ToLonLat(world.X, world.Y);
        return (lat, lon);
    }

    public void Dispose()
    {
        Map.Dispose();
        _missionLayer.Dispose();
        _trailLayer.Dispose();
        _homeLayer.Dispose();
        _vehicleLayer.Dispose();
    }

    /// <summary>Another vehicle was selected: forget the previous track.</summary>
    public void Clear()
    {
        _trail.Clear();
        _home = null;
        _centeredOnce = false;
        _trailLayer.Features = [];
        _homeLayer.Features = [];
        _vehicleLayer.Features = [];
        _trailLayer.DataHasChanged();
        _homeLayer.DataHasChanged();
        _vehicleLayer.DataHasChanged();
    }

    private static readonly SymbolStyle TrailStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.12,
        Fill = new Brush(Color.FromArgb(200, 0, 170, 255)),
        Outline = null,
    };

    private static readonly VectorStyle RouteStyle = new()
    {
        Line = new Pen(Color.FromArgb(230, 241, 196, 15), 3) { PenStyle = PenStyle.Dash },
    };

    private static readonly SymbolStyle WaypointStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.5,
        Fill = new Brush(Color.FromArgb(255, 241, 196, 15)),
        Outline = new Pen(Color.FromArgb(255, 15, 23, 32), 2),
    };

    private static readonly SymbolStyle SelectedWaypointStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.7,
        Fill = new Brush(Color.FromArgb(255, 230, 126, 34)),
        Outline = new Pen(Color.White, 3),
    };

    private static LabelStyle WaypointLabel(string text) => new()
    {
        Text = text,
        ForeColor = Color.White,
        BackColor = new Brush(Color.FromArgb(200, 15, 23, 32)),
        Font = new Font { Size = 11, Bold = true },
        Offset = new Offset(0, -20),
    };

    private static readonly SymbolStyle HomeStyle = new()
    {
        SymbolType = SymbolType.Rectangle,
        SymbolScale = 0.45,
        Fill = new Brush(Color.FromArgb(255, 46, 204, 113)),
        Outline = new Pen(Color.White, 2),
    };

    /// <summary>
    /// Long, narrow arrow pointing up (north) at rotation 0; Mapsui rotates image symbols clockwise in degrees, the same
    /// convention as heading. The shape matters: an equilateral triangle or a wide notched arrow has no unambiguous nose,
    /// and at angles such as 32° operators read it as pointing the wrong way (verified with rendered test images).
    /// </summary>
    private const string VehicleArrowSvg = "svg-content://<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32' viewBox='0 0 32 32'>"
        + "<path d='M16 1 L23 30 L16 26 L9 30 Z' fill='#E74C3C' stroke='#FFFFFF' stroke-width='2' stroke-linejoin='round'/></svg>";

    private static ImageStyle VehicleStyle(double headingDegrees) => new()
    {
        Image = VehicleArrowSvg,
        SymbolScale = 1.4,
        SymbolRotation = headingDegrees,
    };
}
