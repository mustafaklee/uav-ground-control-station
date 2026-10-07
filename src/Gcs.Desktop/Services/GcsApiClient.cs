using System.Net.Http.Json;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;

namespace Gcs.Desktop.Services;

/// <summary>REST operations the desktop client needs. An interface so view models can be tested without a server.</summary>
public interface IGcsApiClient
{
    Task<IReadOnlyList<VehicleResponse>> GetActiveVehiclesAsync(CancellationToken cancellationToken);

    Task<VehicleLinkResponse> GetLinkAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task<TelemetryResponse?> GetLatestTelemetryAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task ConnectAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task DisconnectAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <summary>Typed HTTP client for the GCS REST API (v1).</summary>
public sealed class GcsApiClient(HttpClient http) : IGcsApiClient
{
    private const string Vehicles = "api/v1/vehicles";
    private const int PageSize = 100;

    public async Task<IReadOnlyList<VehicleResponse>> GetActiveVehiclesAsync(CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<PagedResponse<VehicleResponse>>(
            $"{Vehicles}?status=Active&pageSize={PageSize}", cancellationToken);
        return page?.Items ?? [];
    }

    public async Task<VehicleLinkResponse> GetLinkAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<VehicleLinkResponse>($"{Vehicles}/{vehicleId}/connection", cancellationToken)
        ?? throw new InvalidOperationException("Empty link status response.");

    public async Task<TelemetryResponse?> GetLatestTelemetryAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri($"{Vehicles}/{vehicleId}/telemetry", UriKind.Relative), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TelemetryResponse>(cancellationToken)
            : null; // 404 telemetry.not_available: nothing received yet
    }

    public async Task ConnectAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(new Uri($"{Vehicles}/{vehicleId}/connection", UriKind.Relative), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisconnectAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(new Uri($"{Vehicles}/{vehicleId}/connection", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
