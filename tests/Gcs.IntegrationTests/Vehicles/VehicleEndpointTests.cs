using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Vehicles;

/// <summary>The vehicle REST API end to end: HTTP → handlers → EF Core → PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class VehicleEndpointTests(GcsApiFactory factory)
{
    private const string BasePath = "/api/v1/vehicles";
    private readonly HttpClient _client = factory.CreateAdminClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_returns_201_with_location_and_etag()
    {
        var response = await _client.PostAsJsonAsync(BasePath, TestData.Registration(callsign: "reg-ok-1"), Ct);
        var vehicle = await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        vehicle.ShouldNotBeNull();
        vehicle.Callsign.ShouldBe("REG-OK-1");
        vehicle.Status.ShouldBe("Active");
        response.Headers.Location!.ToString().ShouldBe($"{BasePath}/{vehicle.Id}");
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
    }

    [Fact]
    public async Task Registered_vehicle_can_be_read_back_with_its_etag()
    {
        var created = await RegisterAsync();

        var response = await _client.GetAsync(VehicleUri(created.Id), Ct);
        var vehicle = await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        vehicle.ShouldBe(created);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
    }

    [Fact]
    public async Task Unknown_vehicle_returns_404_problem()
    {
        var response = await _client.GetAsync(VehicleUri(Guid.NewGuid()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemCodeAsync(response)).ShouldBe("vehicle.not_found");
    }

    [Fact]
    public async Task Invalid_registration_returns_400_with_errors_per_field()
    {
        var request = new RegisterVehicleRequest("x", 0, "Px4", "Multirotor", new ConnectionSettingsDto("Udp", Host: "localhost", Port: 0));

        var response = await _client.PostAsJsonAsync(BasePath, request, Ct);
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("code").GetString().ShouldBe("validation.failed");
        var fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        fields.ShouldBe(["callsign", "mavlinkSystemId", "connection"], ignoreOrder: true);
    }

    [Fact]
    public async Task Duplicate_callsign_returns_409()
    {
        var first = await RegisterAsync();

        var response = await _client.PostAsJsonAsync(BasePath, TestData.Registration(callsign: first.Callsign.ToLowerInvariant()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(response)).ShouldBe("vehicle.callsign.in_use");
    }

    [Fact]
    public async Task Concurrent_registrations_of_the_same_callsign_let_exactly_one_win()
    {
        var callsign = TestData.NextCallsign("RACE");
        var requests = Enumerable.Range(0, 8)
            .Select(_ => _client.PostAsJsonAsync(BasePath, TestData.Registration(callsign: callsign), Ct));

        var responses = await Task.WhenAll(requests);

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
    }

    [Fact]
    public async Task Update_with_current_etag_succeeds_and_returns_a_new_etag()
    {
        var created = await RegisterAsync();
        var update = new UpdateVehicleRequest(created.Callsign, created.MavlinkSystemId, "ArduPilot", "FixedWing",
            new ConnectionSettingsDto("Tcp", Host: "10.0.0.7", Port: 5760));

        var response = await PutAsync(created.Id, update, ifMatch: "\"1\"");
        var vehicle = await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        vehicle!.Autopilot.ShouldBe("ArduPilot");
        vehicle.Connection.ShouldBe(new ConnectionSettingsDto("Tcp", "10.0.0.7", 5760, null, null));
        vehicle.Version.ShouldBe(2);
        response.Headers.ETag!.Tag.ShouldBe("\"2\"");
    }

    [Fact]
    public async Task Update_with_stale_etag_returns_412_and_keeps_the_newer_data()
    {
        var created = await RegisterAsync();
        var operatorA = ToUpdate(created) with { Type = "Vtol" };
        var operatorB = ToUpdate(created) with { Type = "FixedWing" };

        (await PutAsync(created.Id, operatorA, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await PutAsync(created.Id, operatorB, ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemCodeAsync(response)).ShouldBe("vehicle.version_mismatch");
        var current = await _client.GetFromJsonAsync<VehicleResponse>(VehicleUri(created.Id), Ct);
        current!.Type.ShouldBe("Vtol");
    }

    [Fact]
    public async Task Update_without_if_match_returns_428()
    {
        var created = await RegisterAsync();

        var response = await PutAsync(created.Id, ToUpdate(created), ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    public async Task Delete_retires_the_vehicle_and_is_idempotent()
    {
        var created = await RegisterAsync();

        var first = await _client.DeleteAsync(VehicleUri(created.Id), Ct);
        var second = await _client.DeleteAsync(VehicleUri(created.Id), Ct);
        var vehicle = await _client.GetFromJsonAsync<VehicleResponse>(VehicleUri(created.Id), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        vehicle!.Status.ShouldBe("Retired");
    }

    [Fact]
    public async Task Retired_vehicle_cannot_be_updated_but_its_callsign_is_free_again()
    {
        var created = await RegisterAsync();
        await _client.DeleteAsync(VehicleUri(created.Id), Ct);

        var update = await PutAsync(created.Id, ToUpdate(created), ifMatch: "\"2\"");
        var reRegister = await _client.PostAsJsonAsync(
            BasePath, TestData.Registration(callsign: created.Callsign, systemId: created.MavlinkSystemId), Ct);

        update.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(update)).ShouldBe("vehicle.retired");
        reRegister.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task List_can_search_by_callsign_and_filter_by_status()
    {
        var active = await RegisterAsync(TestData.NextCallsign("LISTX"));
        var retired = await RegisterAsync(TestData.NextCallsign("LISTX"));
        await _client.DeleteAsync(VehicleUri(retired.Id), Ct);

        var all = await _client.GetFromJsonAsync<PagedResponse<VehicleResponse>>($"{BasePath}?search=listx", Ct);
        var onlyActive = await _client.GetFromJsonAsync<PagedResponse<VehicleResponse>>($"{BasePath}?search=listx&status=active", Ct);
        var wildcard = await _client.GetFromJsonAsync<PagedResponse<VehicleResponse>>($"{BasePath}?search=%25", Ct);

        all!.Items.Select(v => v.Id).ShouldBe([active.Id, retired.Id], ignoreOrder: true);
        onlyActive!.Items.ShouldHaveSingleItem().Id.ShouldBe(active.Id);
        wildcard!.TotalCount.ShouldBe(0); // "%" is searched literally, not as "match everything"
    }

    [Fact]
    public async Task List_is_paged()
    {
        var prefix = TestData.NextCallsign("PAGE");
        for (var i = 0; i < 3; i++)
        {
            await RegisterAsync($"{prefix}-{i}");
        }

        var page = await _client.GetFromJsonAsync<PagedResponse<VehicleResponse>>($"{BasePath}?search={prefix}&pageSize=2&page=2", Ct);

        page!.TotalCount.ShouldBe(3);
        page.TotalPages.ShouldBe(2);
        page.Items.ShouldHaveSingleItem().Callsign.ShouldBe($"{prefix}-2");
    }

    private async Task<VehicleResponse> RegisterAsync(string? callsign = null)
    {
        var response = await _client.PostAsJsonAsync(BasePath, TestData.Registration(callsign), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    private async Task<HttpResponseMessage> PutAsync(Guid id, UpdateVehicleRequest body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, VehicleUri(id)) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return await _client.SendAsync(request, Ct);
    }

    private static UpdateVehicleRequest ToUpdate(VehicleResponse vehicle) =>
        new(vehicle.Callsign, vehicle.MavlinkSystemId, vehicle.Autopilot, vehicle.Type, vehicle.Connection);

    private static Uri VehicleUri(Guid id) => new($"{BasePath}/{id}", UriKind.Relative);

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
        return problem.RootElement.GetProperty("code").GetString();
    }
}
