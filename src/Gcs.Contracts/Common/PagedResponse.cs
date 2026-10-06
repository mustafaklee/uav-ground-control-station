namespace Gcs.Contracts.Common;

/// <summary>One page of a larger result set. Lists are always paged so one request cannot load the whole table.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
