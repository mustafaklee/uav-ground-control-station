using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

/// <summary>
/// Where and how to reach a vehicle. A value object: two settings with the same values are the same settings,
/// and an instance can only be created through the factory methods, so an invalid combination
/// (a UDP link without a port, a serial link with a host name) cannot exist.
/// </summary>
public sealed record ConnectionSettings
{
    public const int MaxHostLength = 253;
    public const int MaxSerialPortNameLength = 64;
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    /// <summary>Baud rates supported by common telemetry radios and flight controllers.</summary>
    public static readonly IReadOnlySet<int> SupportedBaudRates =
        new HashSet<int> { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    private ConnectionSettings(TransportType transport, string? host, int? port, string? serialPortName, int? baudRate)
    {
        Transport = transport;
        Host = host;
        Port = port;
        SerialPortName = serialPortName;
        BaudRate = baudRate;
    }

    public TransportType Transport { get; }

    public string? Host { get; }

    public int? Port { get; }

    public string? SerialPortName { get; }

    public int? BaudRate { get; }

    public static Result<ConnectionSettings> Udp(string? host, int port) => Network(TransportType.Udp, host, port);

    public static Result<ConnectionSettings> Tcp(string? host, int port) => Network(TransportType.Tcp, host, port);

    public static Result<ConnectionSettings> Serial(string? portName, int baudRate)
    {
        var name = portName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxSerialPortNameLength)
        {
            return VehicleErrors.SerialPortName;
        }

        if (!SupportedBaudRates.Contains(baudRate))
        {
            return VehicleErrors.BaudRate;
        }

        return new ConnectionSettings(TransportType.Serial, null, null, name, baudRate);
    }

    public static ConnectionSettings Simulator() => new(TransportType.Simulator, null, null, null, null);

    private static Result<ConnectionSettings> Network(TransportType transport, string? host, int port)
    {
        var trimmed = host?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxHostLength || trimmed.Any(char.IsWhiteSpace))
        {
            return VehicleErrors.Host;
        }

        if (port is < MinPort or > MaxPort)
        {
            return VehicleErrors.Port;
        }

        return new ConnectionSettings(transport, trimmed, port, null, null);
    }
}
