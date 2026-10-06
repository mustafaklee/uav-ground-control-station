namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// Same images as docker-compose.yml, so tests run against what developers run locally.
/// </summary>
internal static class ContainerImages
{
    public const string Postgres = "postgres:18-alpine";
    public const string RabbitMq = "rabbitmq:4-management-alpine";
}
