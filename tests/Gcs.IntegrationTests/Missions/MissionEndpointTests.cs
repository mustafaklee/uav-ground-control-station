using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gcs.Contracts.Missions;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Missions;

/// <summary>Mission planning end to end: REST → PostgreSQL (JSONB items) → MAVLink mission protocol → simulated vehicle.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class MissionEndpointTests(GcsApiFactory factory)
{
    private const string Missions = "/api/v1/missions";
    private const string Vehicles = "/api/v1/vehicles";
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MissionItemDto[] Survey =
    [
        new("Takeoff", Altitude: 30),
        new("Waypoint", 39.9255, 32.8662, 50, HoldSeconds: 5, Speed: 10),
        new("Loiter", 39.9265, 32.8672, 60, HoldSeconds: 20),
        new("Waypoint", 39.9275, 32.8682, 50, Speed: 6),
        new("ReturnToLaunch"),
    ];

    [Fact]
    public async Task A_flyable_mission_is_created_with_no_issues_and_its_distance()
    {
        var response = await _client.PostAsJsonAsync(Missions, new SaveMissionRequest("Survey North", Survey), Ct);
        var mission = await response.Content.ReadFromJsonAsync<MissionResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        mission!.IsFlyable.ShouldBeTrue();
        mission.Issues.ShouldBeEmpty();
        mission.Items.ShouldBe(Survey);
        mission.TotalDistanceMetres.ShouldBeGreaterThan(200);
    }

    [Fact]
    public async Task A_draft_can_be_incomplete_and_lists_what_prevents_flying()
    {
        var response = await _client.PostAsJsonAsync(Missions, new SaveMissionRequest("Draft", [new("Waypoint", 39.92, 32.86, 50)]), Ct);
        var mission = await response.Content.ReadFromJsonAsync<MissionResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        mission!.IsFlyable.ShouldBeFalse();
        mission.Issues.ShouldHaveSingleItem().Code.ShouldBe("mission.too_short");
    }

    [Fact]
    public async Task Invalid_items_are_reported_by_position()
    {
        MissionItemDto[] items = [new("Takeoff", Altitude: 30), new("Waypoint", 39.92, 32.86, Altitude: 900), new("Fly")];

        var response = await _client.PostAsJsonAsync(Missions, new SaveMissionRequest("Bad", items), Ct);
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["items[1]", "items[2]"], ignoreOrder: true);
    }

    [Fact]
    public async Task Update_requires_the_current_version()
    {
        var created = await CreateAsync("Edit me", Survey);

        var ok = await PutAsync(created.Id, new SaveMissionRequest("Edited", Survey[..^1].Append(new("Land")).ToArray()), "\"1\"");
        var stale = await PutAsync(created.Id, new SaveMissionRequest("Stale", Survey), "\"1\"");

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<MissionResponse>(Ct))!.Items[^1].Command.ShouldBe("Land");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Upload_sends_the_mission_to_the_vehicle_and_download_reads_back_the_same_plan()
    {
        var vehicle = await ConnectedSimulatorAsync();
        var mission = await CreateAsync("Upload me", Survey);

        var upload = await _client.PostAsJsonAsync($"{Missions}/{mission.Id}/upload", new UploadMissionRequest(vehicle.Id), Ct);
        var uploaded = await upload.Content.ReadFromJsonAsync<MissionResponse>(Ct);
        var onVehicle = await _client.GetFromJsonAsync<VehicleMissionResponse>($"{Vehicles}/{vehicle.Id}/mission", Ct);

        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));
        uploaded!.LastUpload!.Succeeded.ShouldBeTrue();
        uploaded.LastUpload.VehicleId.ShouldBe(vehicle.Id);
        onVehicle!.Items.ShouldBe(Survey);

        await _client.DeleteAsync(new Uri($"{Vehicles}/{vehicle.Id}/connection", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task A_mission_that_is_not_flyable_is_not_uploaded()
    {
        var vehicle = await ConnectedSimulatorAsync();
        var mission = await CreateAsync("No takeoff", [new("Waypoint", 39.92, 32.86, 50), new("Land")]);

        var upload = await _client.PostAsJsonAsync($"{Missions}/{mission.Id}/upload", new UploadMissionRequest(vehicle.Id), Ct);

        upload.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(upload)).ShouldBe("mission.not_flyable");
        await _client.DeleteAsync(new Uri($"{Vehicles}/{vehicle.Id}/connection", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task Upload_to_a_vehicle_without_a_link_is_refused_and_recorded()
    {
        var vehicle = await RegisterSimulatorAsync();
        var mission = await CreateAsync("Offline", Survey);

        var upload = await _client.PostAsJsonAsync($"{Missions}/{mission.Id}/upload", new UploadMissionRequest(vehicle.Id), Ct);
        var after = await _client.GetFromJsonAsync<MissionResponse>($"{Missions}/{mission.Id}", Ct);

        upload.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(upload)).ShouldBe("vehicle.link.not_connected");
        after!.LastUpload!.Succeeded.ShouldBeFalse();
        after.LastUpload.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Archived_missions_leave_the_default_list_and_cannot_be_edited()
    {
        var mission = await CreateAsync("Old plan", Survey);

        (await _client.DeleteAsync(new Uri($"{Missions}/{mission.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var list = await _client.GetFromJsonAsync<Gcs.Contracts.Common.PagedResponse<MissionSummaryResponse>>($"{Missions}?pageSize=100", Ct);
        var edit = await PutAsync(mission.Id, new SaveMissionRequest("x", Survey), "\"2\"");

        list!.Items.ShouldNotContain(m => m.Id == mission.Id);
        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(edit)).ShouldBe("mission.archived");
    }

    private async Task<MissionResponse> CreateAsync(string name, IReadOnlyList<MissionItemDto> items)
    {
        var response = await _client.PostAsJsonAsync(Missions, new SaveMissionRequest(name, items), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<MissionResponse>(Ct))!;
    }

    private async Task<HttpResponseMessage> PutAsync(Guid id, SaveMissionRequest body, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Missions}/{id}") { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        return await _client.SendAsync(request, Ct);
    }

    private async Task<VehicleResponse> RegisterSimulatorAsync()
    {
        var request = TestData.Registration() with { Connection = new ConnectionSettingsDto("Simulator") };
        var response = await _client.PostAsJsonAsync(Vehicles, request, Ct);
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    private async Task<VehicleResponse> ConnectedSimulatorAsync()
    {
        var vehicle = await RegisterSimulatorAsync();
        await _client.PostAsync(new Uri($"{Vehicles}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
        await Eventually.GetAsync(
            async () => (await _client.GetFromJsonAsync<VehicleLinkResponse>($"{Vehicles}/{vehicle.Id}/connection", Ct))!,
            l => l.State == "Connected", TimeSpan.FromSeconds(15), "the simulator connects");
        return vehicle;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
        return problem.RootElement.GetProperty("code").GetString();
    }
}
