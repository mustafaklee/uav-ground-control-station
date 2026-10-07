using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>Use case: read stored telemetry samples for a time window (flight replay, charts).</summary>
public sealed class TelemetryHistoryHandler(IVehicleQueries queries, ITelemetryHistoryStore history, TimeProvider clock)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);

    public async Task<Result<TelemetryHistoryResponse>> HandleAsync(Guid id, TelemetryHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var to = query.To ?? clock.GetUtcNow();
        var from = query.From ?? to - DefaultWindow;
        var errors = new Dictionary<string, string[]>();
        if (from >= to)
        {
            errors["from"] = ["'from' must be earlier than 'to'."];
        }
        else if (to - from > MaxWindow)
        {
            errors["to"] = [$"The time window may be at most {MaxWindow.TotalHours:0} hours."];
        }

        if (query.Limit is < 1 or > TelemetryHistoryQuery.MaxLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {TelemetryHistoryQuery.MaxLimit}."];
        }

        if (errors.Count > 0)
        {
            return new Error(Common.ValidationErrors.Code, "One or more query parameters are invalid.", ErrorType.Validation)
            {
                Details = errors,
            };
        }

        if (await queries.GetByIdAsync(id, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        // Ask for one extra sample to know whether the result was cut off by the limit.
        var samples = await history.QueryAsync(new VehicleId(id), from, to, query.Limit + 1, cancellationToken);
        var truncated = samples.Count > query.Limit;
        return new TelemetryHistoryResponse(
            id, from, to, [.. samples.Take(query.Limit).Select(TelemetryMapping.ToResponse)], truncated);
    }
}
