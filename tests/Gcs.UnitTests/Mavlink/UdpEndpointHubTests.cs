using System.Net;
using System.Net.Sockets;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Transports;

namespace Gcs.UnitTests.Mavlink;

/// <summary>Several vehicles on one UDP port (ADR-019), with real sockets on the loopback interface.</summary>
public sealed class UdpEndpointHubTests : IAsyncDisposable
{
    private readonly UdpEndpointHub _hub = new(TimeProvider.System);
    private readonly IPEndPoint _local = new(IPAddress.Loopback, FreeUdpPort());
    private readonly List<IAsyncDisposable> _cleanup = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_vehicle_gets_only_its_own_datagrams_and_replies_go_back_to_its_address()
    {
        var linkA = await OpenAsync(systemId: 1);
        var linkB = await OpenAsync(systemId: 2);
        using var vehicle1 = Vehicle();
        using var vehicle2 = Vehicle();

        await SendAsync(vehicle1, systemId: 1);
        await SendAsync(vehicle2, systemId: 2);

        (await ReceiveSystemIdAsync(linkA)).ShouldBe((byte)1);
        (await ReceiveSystemIdAsync(linkB)).ShouldBe((byte)2);

        await linkB.SendAsync(Heartbeat(255), Ct);
        var reply = await vehicle2.ReceiveFromAsync(new byte[512], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), Ct);
        reply.ReceivedBytes.ShouldBeGreaterThan(0);
        vehicle1.Available.ShouldBe(0); // vehicle 1 was not sent vehicle 2's reply
    }

    [Fact]
    public async Task A_second_link_with_the_same_system_id_on_the_same_port_is_refused()
    {
        await OpenAsync(systemId: 1);

        var duplicate = _hub.Create(_local, 1);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => duplicate.OpenAsync(Ct));

        error.Message.ShouldContain("System id 1 already has a link");
    }

    [Fact]
    public async Task Systems_nobody_registered_are_listed_as_heard()
    {
        await OpenAsync(systemId: 1);
        using var stranger = Vehicle();

        await SendAsync(stranger, systemId: 7);

        var endpoint = await EventuallyAsync(() => _hub.Snapshot().SingleOrDefault(e => e.Heard.Any(h => h.SystemId == 7)));
        endpoint.ClaimedSystemIds.ShouldBe([(byte)1]);
        endpoint.Heard.Single(h => h.SystemId == 7).Datagrams.ShouldBe(1);
    }

    [Fact]
    public async Task A_radio_reporting_from_the_vehicle_address_reaches_that_vehicle()
    {
        var link = await OpenAsync(systemId: 1);
        using var vehicleWithRadio = Vehicle();

        await SendAsync(vehicleWithRadio, systemId: 1);
        await ReceiveSystemIdAsync(link);
        await vehicleWithRadio.SendToAsync(
            MavlinkCodec.Encode(new RadioStatusMessage(150, 140, 100, 40, 42, 0, 0), 0, 51, 68), SocketFlags.None, _local, Ct);

        (await ReceiveSystemIdAsync(link)).ShouldBe((byte)51);
    }

    [Fact]
    public async Task The_socket_is_released_when_the_last_link_closes()
    {
        var first = await OpenAsync(systemId: 1);
        var second = await OpenAsync(systemId: 2);

        await first.DisposeAsync();
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            Should.Throw<SocketException>(() => probe.Bind(_local)); // still open for the second link
        }

        await second.DisposeAsync();
        using var after = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        after.Bind(_local);
        _hub.Snapshot().ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup)
        {
            await item.DisposeAsync();
        }
    }

    private async Task<IMavlinkTransport> OpenAsync(byte systemId)
    {
        var transport = _hub.Create(_local, systemId);
        await transport.OpenAsync(Ct);
        _cleanup.Add(transport);
        return transport;
    }

    private static Socket Vehicle()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private async Task SendAsync(Socket vehicle, byte systemId) =>
        await vehicle.SendToAsync(Heartbeat(systemId), SocketFlags.None, _local, Ct);

    private static byte[] Heartbeat(byte systemId) => MavlinkCodec.Encode(
        new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.Px4, MavBaseMode.None, 0, MavState.Active), 0, systemId, MavComponent.Autopilot1);

    private static async Task<byte> ReceiveSystemIdAsync(IMavlinkTransport transport)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[2048];
        var read = await transport.ReceiveAsync(buffer, timeout.Token);
        return MavlinkWire.PeekSystemId(buffer.AsSpan(0, read))!.Value;
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> probe)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            if (probe() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within 5 s.");
            }

            await Task.Delay(10, Ct);
        }
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
