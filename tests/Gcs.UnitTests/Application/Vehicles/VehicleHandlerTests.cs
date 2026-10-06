using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Application.Vehicles;

public sealed class VehicleHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeVehicleRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedClock _clock = new(Now);

    [Fact]
    public async Task Register_stores_the_vehicle_and_returns_it()
    {
        var result = await Register(ValidRegistration("uav-01", systemId: 1));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Callsign.ShouldBe("UAV-01");
        result.Value.Status.ShouldBe("Active");
        result.Value.Version.ShouldBe(1);
        result.Value.CreatedAt.ShouldBe(Now);
        _repository.Vehicles.ShouldHaveSingleItem();
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Timestamps_are_truncated_to_the_microsecond_precision_postgres_stores()
    {
        var clockWithSubMicroseconds = new FixedClock(Now.AddTicks(1234567));
        var handler = new RegisterVehicleHandler(
            new VehicleFieldsValidator<RegisterVehicleRequest>(), _repository, _unitOfWork, clockWithSubMicroseconds);

        var result = await handler.HandleAsync(ValidRegistration("UAV-01", systemId: 1), CancellationToken.None);

        result.Value.CreatedAt.ShouldBe(Now.AddTicks(1234560));
    }

    [Fact]
    public async Task Register_reports_every_invalid_field_at_once()
    {
        var request = new RegisterVehicleRequest("x", 0, "Betaflight", "Blimp", new ConnectionSettingsDto("Udp", Host: "", Port: 70000));

        var result = await Register(request);

        result.Error!.Type.ShouldBe(ErrorType.Validation);
        result.Error.Details.Keys.ShouldBe(
            ["callsign", "mavlinkSystemId", "autopilot", "type", "connection"],
            ignoreOrder: true);
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Register_rejects_a_callsign_used_by_an_active_vehicle()
    {
        await Register(ValidRegistration("UAV-01", systemId: 1));

        var result = await Register(ValidRegistration("uav-01", systemId: 2));

        result.Error.ShouldBe(VehicleErrors.CallsignInUse);
        _repository.Vehicles.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Register_rejects_a_system_id_used_by_an_active_vehicle()
    {
        await Register(ValidRegistration("UAV-01", systemId: 7));

        var result = await Register(ValidRegistration("UAV-02", systemId: 7));

        result.Error.ShouldBe(VehicleErrors.SystemIdInUse);
    }

    [Fact]
    public async Task Callsign_of_a_retired_vehicle_can_be_reused()
    {
        var first = await Register(ValidRegistration("UAV-01", systemId: 1));
        await Retire(first.Value.Id, expectedVersion: null);

        var second = await Register(ValidRegistration("UAV-01", systemId: 1));

        second.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_translates_a_database_duplicate_into_a_conflict()
    {
        _unitOfWork.FailWith = new UniqueConstraintViolationException(
            VehicleConstraints.ActiveSystemIdUnique, "duplicate", new InvalidOperationException());

        var result = await Register(ValidRegistration("UAV-01", systemId: 1));

        result.Error.ShouldBe(VehicleErrors.SystemIdInUse);
    }

    [Fact]
    public async Task Update_changes_the_vehicle_when_the_version_matches()
    {
        var created = await Register(ValidRegistration("UAV-01", systemId: 1));

        var result = await Update(created.Value.Id, expectedVersion: 1, ValidUpdate("UAV-01-B", systemId: 1));

        result.Value.Callsign.ShouldBe("UAV-01-B");
        result.Value.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Update_keeping_its_own_callsign_is_not_a_conflict()
    {
        var created = await Register(ValidRegistration("UAV-01", systemId: 1));

        var result = await Update(created.Value.Id, expectedVersion: 1, ValidUpdate("UAV-01", systemId: 9));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_rejected()
    {
        var created = await Register(ValidRegistration("UAV-01", systemId: 1));

        var result = await Update(created.Value.Id, expectedVersion: 3, ValidUpdate("UAV-02", systemId: 1));

        result.Error.ShouldBe(VehicleErrors.VersionMismatch);
    }

    [Fact]
    public async Task Update_that_loses_a_race_at_save_time_is_rejected()
    {
        var created = await Register(ValidRegistration("UAV-01", systemId: 1));
        _unitOfWork.FailWith = new ConcurrencyConflictException();

        var result = await Update(created.Value.Id, expectedVersion: 1, ValidUpdate("UAV-02", systemId: 1));

        result.Error.ShouldBe(VehicleErrors.VersionMismatch);
    }

    [Fact]
    public async Task Update_of_an_unknown_vehicle_returns_not_found()
    {
        var result = await Update(Guid.NewGuid(), expectedVersion: 1, ValidUpdate("UAV-02", systemId: 1));

        result.Error.ShouldBe(VehicleErrors.NotFound);
    }

    [Fact]
    public async Task Retire_of_an_unknown_vehicle_returns_not_found()
    {
        var result = await Retire(Guid.NewGuid(), expectedVersion: null);

        result.Error.ShouldBe(VehicleErrors.NotFound);
    }

    [Theory]
    [InlineData(0, 20, null, "page")]
    [InlineData(1, 0, null, "pageSize")]
    [InlineData(1, 101, null, "pageSize")]
    [InlineData(1, 20, "Deleted", "status")]
    public async Task List_rejects_invalid_query_parameters(int page, int pageSize, string? status, string expectedField)
    {
        var handler = new ListVehiclesHandler(new ThrowingQueries());

        var result = await handler.HandleAsync(new VehicleListQuery(page, pageSize, status), CancellationToken.None);

        result.Error!.Details.Keys.ShouldContain(expectedField);
    }

    private Task<Result<VehicleResponse>> Register(RegisterVehicleRequest request) =>
        new RegisterVehicleHandler(new VehicleFieldsValidator<RegisterVehicleRequest>(), _repository, _unitOfWork, _clock)
            .HandleAsync(request, CancellationToken.None);

    private Task<Result<VehicleResponse>> Update(Guid id, int expectedVersion, UpdateVehicleRequest request) =>
        new UpdateVehicleHandler(new VehicleFieldsValidator<UpdateVehicleRequest>(), _repository, _unitOfWork, _clock)
            .HandleAsync(id, expectedVersion, request, CancellationToken.None);

    private Task<Result> Retire(Guid id, int? expectedVersion) =>
        new RetireVehicleHandler(_repository, _unitOfWork, _clock).HandleAsync(id, expectedVersion, CancellationToken.None);

    private static RegisterVehicleRequest ValidRegistration(string callsign, int systemId) =>
        new(callsign, systemId, "Px4", "Multirotor", new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: 14550));

    private static UpdateVehicleRequest ValidUpdate(string callsign, int systemId) =>
        new(callsign, systemId, "ArduPilot", "FixedWing", new ConnectionSettingsDto("Serial", SerialPort: "COM3", BaudRate: 57600));

    private sealed class ThrowingQueries : IVehicleQueries
    {
        public Task<VehicleResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid queries must not reach the database.");

        public Task<Gcs.Contracts.Common.PagedResponse<VehicleResponse>> ListAsync(
            int page, int pageSize, VehicleStatus? status, string? search, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid queries must not reach the database.");
    }
}
