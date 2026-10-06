namespace Gcs.Contracts.Diagnostics;

public sealed record SystemInfoResponse(string Service, string Version, string Environment);
