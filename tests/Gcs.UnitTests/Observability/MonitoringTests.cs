using System.Diagnostics;
using Gcs.Api.Observability;
using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Infrastructure.Health;
using Gcs.UnitTests.Application.Vehicles;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Trace;

namespace Gcs.UnitTests.Observability;

public sealed class MonitoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ActivityKind.Client, SamplingDecision.Drop)]       // e.g. the outbox poll's SELECT with no request around it
    [InlineData(ActivityKind.Server, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityKind.Producer, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityKind.Internal, SamplingDecision.RecordAndSample)]
    public void Only_background_client_calls_without_a_parent_are_dropped(ActivityKind kind, SamplingDecision expected)
    {
        var parameters = new SamplingParameters(default, ActivityTraceId.CreateRandom(), "span", kind);

        new BackgroundNoiseSampler().ShouldSample(parameters).Decision.ShouldBe(expected);
    }

    [Fact]
    public async Task Links_that_are_not_connected_make_the_check_degraded_and_are_named()
    {
        var faulted = VehicleId.New();
        var links = new StaticLinks(
            Status(VehicleId.New(), ConnectionState.Connected, null),
            Status(faulted, ConnectionState.Faulted, "No heartbeat within 10 s."));

        var result = await new VehicleLinksHealthCheck(links).CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data[faulted.Value.ToString()].ShouldBe("Faulted: No heartbeat within 10 s.");
        result.Data["connected"].ShouldBe(1);
    }

    [Theory]
    [InlineData(10, HealthStatus.Healthy)]
    [InlineData(120, HealthStatus.Degraded)]
    [InlineData(900, HealthStatus.Unhealthy)]
    public async Task The_outbox_check_judges_the_age_of_the_oldest_waiting_event(int ageSeconds, HealthStatus expected)
    {
        var store = new StaticOutbox(new OutboxBacklog(3, Now.AddSeconds(-ageSeconds)));
        var check = new OutboxBacklogHealthCheck(store, Options.Create(new MonitoringOptions()), new FakeTimeProvider(Now));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(expected);
        result.Data["pending"].ShouldBe(3);
    }

    private static VehicleLinkStatus Status(VehicleId id, ConnectionState state, string? reason) =>
        new(id, state, null, 0, reason, new LinkQuality(0, 0, 0, 0));

    private sealed class StaticLinks(params VehicleLinkStatus[] statuses) : IVehicleLinkManager
    {
        private readonly FakeLinkManager _inner = new();

        public Task<Gcs.Domain.Common.Result> ConnectAsync(VehicleLinkTarget target, CancellationToken cancellationToken) =>
            _inner.ConnectAsync(target, cancellationToken);

        public Task DisconnectAsync(VehicleId vehicleId, CancellationToken cancellationToken) => _inner.DisconnectAsync(vehicleId, cancellationToken);

        public VehicleLinkStatus? GetStatus(VehicleId vehicleId) => statuses.FirstOrDefault(s => s.VehicleId == vehicleId);

        public IReadOnlyList<VehicleLinkStatus> GetAll() => statuses;
    }

    private sealed class StaticOutbox(OutboxBacklog backlog) : IOutboxStore
    {
        public Task<int> PublishPendingAsync(int batchSize, Func<OutboxEntry, CancellationToken, Task> publish, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken) => Task.FromResult(backlog);
    }
}
