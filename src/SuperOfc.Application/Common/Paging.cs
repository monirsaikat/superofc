namespace SuperOfc.Application.Common;

public sealed record PageRequest(int PageIndex = 0, int PageSize = 100)
{
    public int Skip => Math.Max(0, PageIndex) * Math.Clamp(PageSize, 1, 1000);

    public int Take => Math.Clamp(PageSize, 1, 1000);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int PageIndex, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNext => PageIndex + 1 < PageCount;
}

public sealed record LookupItem(Guid Id, string Name);
