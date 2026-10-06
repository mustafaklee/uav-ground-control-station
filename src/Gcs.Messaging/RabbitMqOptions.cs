using System.ComponentModel.DataAnnotations;

namespace Gcs.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; init; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string VirtualHost { get; init; } = "/";

    [Required]
    public string UserName { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    /// <summary>Name shown in the RabbitMQ management UI for this client connection.</summary>
    [Required]
    public string ClientProvidedName { get; init; } = "gcs-api";

    [Range(1, 120)]
    public int ConnectionTimeoutSeconds { get; init; } = 5;
}
