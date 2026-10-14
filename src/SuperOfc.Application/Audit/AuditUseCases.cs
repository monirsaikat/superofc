using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Audit;

public sealed record AuditEntryDto(Guid Id, DateTime AtUtc, string UserName, string Action, string? EntityType, Guid? EntityId, string Summary, string? BeforeJson, string? AfterJson);

[RequiresPermission(Permission.ViewAuditLog)]
public sealed record GetAuditLogQuery(
    string? Search = null,
    string? UserName = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? EntityId = null,
    int PageIndex = 0,
    int PageSize = 200) : IQuery<PagedResult<AuditEntryDto>>;

public sealed class GetAuditLogHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetAuditLogQuery, PagedResult<AuditEntryDto>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> HandleAsync(GetAuditLogQuery request, CancellationToken cancellationToken)
    {
        var query = db.AuditEntries.AsNoTracking().Where(a => a.CompanyId == user.CompanyId || a.CompanyId == Guid.Empty);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(a => EF.Functions.Like(a.Summary, term) || EF.Functions.Like(a.Action, term));
        }

        if (!string.IsNullOrWhiteSpace(request.UserName))
        {
            query = query.Where(a => a.UserName == request.UserName);
        }

        if (request.From is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
            query = query.Where(a => a.AtUtc >= fromUtc);
        }

        if (request.To is { } to)
        {
            var toUtc = to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(2);
            query = query.Where(a => a.AtUtc < toUtc);
        }

        if (request.EntityId is { } entityId)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = new PageRequest(request.PageIndex, request.PageSize);
        var items = await query.OrderByDescending(a => a.AtUtc).ThenByDescending(a => a.Id)
            .Skip(page.Skip).Take(page.Take)
            .Select(a => new AuditEntryDto(a.Id, a.AtUtc, a.UserName, a.Action, a.EntityType, a.EntityId, a.Summary, a.BeforeJson, a.AfterJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<AuditEntryDto>(items, total, request.PageIndex, page.Take);
    }
}

[RequiresPermission(Permission.ViewAuditLog)]
public sealed record GetAuditUsersQuery : IQuery<IReadOnlyList<string>>;

public sealed class GetAuditUsersHandler(IAppDbContext db) : IRequestHandler<GetAuditUsersQuery, IReadOnlyList<string>>
{
    public async Task<Result<IReadOnlyList<string>>> HandleAsync(GetAuditUsersQuery request, CancellationToken cancellationToken)
        => await db.AuditEntries.AsNoTracking().Select(a => a.UserName).Distinct().OrderBy(u => u).ToListAsync(cancellationToken).ConfigureAwait(false);
}
