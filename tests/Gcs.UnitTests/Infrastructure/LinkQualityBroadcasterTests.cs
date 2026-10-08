using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Infrastructure.LinkEvents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gcs.UnitTests.Infrastructure;

public sealed class LinkQualityBroadcasterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_active_link_is_pushed_with_its_quality_and_disconnected_ones_are_skipped()
    {
        var connected = Status(ConnectionState.Connected, LinkQualityGrade.Good, roundTrip: 42);
        var reconnecting = Status(ConnectionState.Reconnecting, LinkQualityGrade.Lost);
        var disconnected = Status(ConnectionState.Disconnected, LinkQualityGrade.Lost);
        var live = new RecordingPublisher();

        await Broadcaster([connected, reconnecting, disconnected], live).BroadcastAsync(Ct);

        live.Quality.Select(q => q.VehicleId).ShouldBe([connected.VehicleId.Value, reconnecting.VehicleId.Value]);
        var pushed = live.Quality.First();
        pushed.Quality.Grade.ShouldBe("Good");
        pushed.Quality.RoundTripMilliseconds.ShouldBe(42);
    }

    [Fact]
    public async Task One_failing_push_does_not_stop_the_others()
    {
        var first = Status(ConnectionState.Connected, LinkQualityGrade.Good);
        var second = Status(ConnectionState.Connected, LinkQualityGrade.Fair);
        var live = new RecordingPublisher { FailFor = first.VehicleId.Value };

        await Broadcaster([first, second], live).BroadcastAsync(Ct);

        live.Quality.Select(q => q.VehicleId).ShouldBe([second.VehicleId.Value]);
    }

    private static LinkQualityBroadcaster Broadcaster(IReadOnlyList<VehicleLinkStatus> statuses, RecordingPublisher live) =>
        new(new FixedLinks(statuses), live, TimeProvider.System, NullLogger<LinkQualityBroadcaster>.Instance);

    private static VehicleLinkStatus Status(ConnectionState state, LinkQualityGrade grade, double? roundTrip = null) =>
        new(VehicleId.New(), state, null, 0, null, new LinkQuality(100, 0, 0, 0, RoundTripMilliseconds: roundTrip, Grade: grade));

    private sealed class FixedLinks(IReadOnlyList<VehicleLinkStatus> statuses) : IVehicleLinkManager
    {
        public Task<Result> ConnectAsync(VehicleLinkTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DisconnectAsync(VehicleId vehicleId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public VehicleLinkStatus? GetStatus(VehicleId vehicleId) => statuses.FirstOrDefault(s => s.VehicleId == vehicleId);

        public IReadOnlyList<VehicleLinkStatus> GetAll() => statuses;
    }

    private sealed class RecordingPublisher : ILiveUpdatePublisher
    {
        private readonly ConcurrentQueue<VehicleLinkResponse> _quality = new();

        public Guid? FailFor { get; init; }

        public IReadOnlyCollection<VehicleLinkResponse> Quality => _quality;

        public Task PublishLinkQualityAsync(VehicleLinkResponse status, CancellationToken cancellationToken)
        {
            if (status.VehicleId == FailFor)
            {
                throw new InvalidOperationException("client went away");
            }

            _quality.Enqueue(status);
            return Task.CompletedTask;
        }

        public Task PublishTelemetryAsync(TelemetryResponse telemetry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishLinkStatusAsync(VehicleLinkResponse status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishCommandLeaseAsync(CommandLeaseResponse lease, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
