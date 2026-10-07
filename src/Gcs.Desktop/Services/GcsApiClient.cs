using System.Net.Http.Json;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Common;
using Gcs.Contracts.Missions;
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

    Task<IReadOnlyList<MissionSummaryResponse>> GetMissionsAsync(CancellationToken cancellationToken);

    Task<MissionResponse> GetMissionAsync(Guid missionId, CancellationToken cancellationToken);

    Task<MissionResponse> CreateMissionAsync(SaveMissionRequest request, CancellationToken cancellationToken);

    Task<MissionResponse> UpdateMissionAsync(Guid missionId, int version, SaveMissionRequest request, CancellationToken cancellationToken);

    Task ArchiveMissionAsync(Guid missionId, CancellationToken cancellationToken);

    Task<MissionResponse> UploadMissionAsync(Guid missionId, Guid vehicleId, CancellationToken cancellationToken);

    Task<VehicleMissionResponse> DownloadVehicleMissionAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task<CommandLeaseResponse> GetCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Takes control, or renews control this operator already has.</summary>
    Task<CommandLeaseResponse> AcquireCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task ReleaseCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetFlightModesAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Returns the audit entry when the vehicle accepted; throws <see cref="ApiProblemException"/> otherwise.</summary>
    Task<CommandAuditResponse> SendCommandAsync(Guid vehicleId, SendCommandRequest command, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommandAuditResponse>> GetCommandHistoryAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <summary>Typed HTTP client for the GCS REST API (v1).</summary>
public sealed class GcsApiClient(HttpClient http) : IGcsApiClient
{
    private const string Vehicles = "api/v1/vehicles";
    private const string Missions = "api/v1/missions";
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
        await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
    }

    public async Task DisconnectAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(new Uri($"{Vehicles}/{vehicleId}/connection", UriKind.Relative), cancellationToken);
        await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<MissionSummaryResponse>> GetMissionsAsync(CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<PagedResponse<MissionSummaryResponse>>($"{Missions}?pageSize={PageSize}", cancellationToken);
        return page?.Items ?? [];
    }

    public Task<MissionResponse> GetMissionAsync(Guid missionId, CancellationToken cancellationToken) =>
        SendAsync<MissionResponse>(new HttpRequestMessage(HttpMethod.Get, $"{Missions}/{missionId}"), cancellationToken);

    public Task<MissionResponse> CreateMissionAsync(SaveMissionRequest request, CancellationToken cancellationToken) =>
        SendAsync<MissionResponse>(new HttpRequestMessage(HttpMethod.Post, Missions) { Content = JsonContent.Create(request) }, cancellationToken);

    public Task<MissionResponse> UpdateMissionAsync(Guid missionId, int version, SaveMissionRequest request, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, $"{Missions}/{missionId}") { Content = JsonContent.Create(request) };
        message.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{version}\""));
        return SendAsync<MissionResponse>(message, cancellationToken);
    }

    public async Task ArchiveMissionAsync(Guid missionId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(new Uri($"{Missions}/{missionId}", UriKind.Relative), cancellationToken);
        await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
    }

    public Task<MissionResponse> UploadMissionAsync(Guid missionId, Guid vehicleId, CancellationToken cancellationToken) =>
        SendAsync<MissionResponse>(
            new HttpRequestMessage(HttpMethod.Post, $"{Missions}/{missionId}/upload") { Content = JsonContent.Create(new UploadMissionRequest(vehicleId)) },
            cancellationToken);

    public Task<VehicleMissionResponse> DownloadVehicleMissionAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        SendAsync<VehicleMissionResponse>(new HttpRequestMessage(HttpMethod.Get, $"{Vehicles}/{vehicleId}/mission"), cancellationToken);

    // The operator name travels in the X-Operator header, set once on the HttpClient (see App).
    public Task<CommandLeaseResponse> GetCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        SendAsync<CommandLeaseResponse>(new HttpRequestMessage(HttpMethod.Get, $"{Vehicles}/{vehicleId}/command-lease"), cancellationToken);

    public Task<CommandLeaseResponse> AcquireCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        SendAsync<CommandLeaseResponse>(new HttpRequestMessage(HttpMethod.Post, $"{Vehicles}/{vehicleId}/command-lease"), cancellationToken);

    public async Task ReleaseCommandLeaseAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(new Uri($"{Vehicles}/{vehicleId}/command-lease", UriKind.Relative), cancellationToken);
        await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetFlightModesAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        (await SendAsync<FlightModesResponse>(new HttpRequestMessage(HttpMethod.Get, $"{Vehicles}/{vehicleId}/flight-modes"), cancellationToken)).Modes;

    public Task<CommandAuditResponse> SendCommandAsync(Guid vehicleId, SendCommandRequest command, CancellationToken cancellationToken) =>
        SendAsync<CommandAuditResponse>(
            new HttpRequestMessage(HttpMethod.Post, $"{Vehicles}/{vehicleId}/commands") { Content = JsonContent.Create(command) },
            cancellationToken);

    public async Task<IReadOnlyList<CommandAuditResponse>> GetCommandHistoryAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        (await SendAsync<PagedResponse<CommandAuditResponse>>(
            new HttpRequestMessage(HttpMethod.Get, $"{Vehicles}/{vehicleId}/commands?pageSize=10"), cancellationToken)).Items;

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await http.SendAsync(request, cancellationToken);
            await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new InvalidOperationException($"Empty response from {request.RequestUri}.");
        }
    }
}
