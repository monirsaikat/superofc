using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Payroll;

/// <summary>
/// Which dates are covered by finalized payroll (doc 03 R7). Attendance, leave and salary for those dates cannot
/// change; corrections go through a void and a new run.
/// </summary>
public sealed class PayrollLocks(IAppDbContext db, ICurrentUser user)
{
    private List<DateRange>? _locked;

    /// <summary>The end of the latest finalized period, or null when nothing is finalized.</summary>
    public async Task<DateOnly?> LockedUntilAsync(CancellationToken cancellationToken)
    {
        var ranges = await RangesAsync(cancellationToken).ConfigureAwait(false);
        return ranges.Count == 0 ? null : ranges.Max(r => r.To!.Value);
    }

    public async Task<IReadOnlyList<DateRange>> LockedRangesAsync(CancellationToken cancellationToken)
        => await RangesAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsLockedAsync(DateOnly date, CancellationToken cancellationToken)
        => (await RangesAsync(cancellationToken).ConfigureAwait(false)).Any(r => r.Contains(date));

    /// <summary>The locked period that overlaps the range, if any.</summary>
    public async Task<DateRange?> FirstLockedInAsync(DateRange range, CancellationToken cancellationToken)
    {
        foreach (var locked in await RangesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (locked.Overlaps(range))
            {
                return locked;
            }
        }

        return null;
    }

    /// <summary>The standard error for a change inside a finalized period.</summary>
    public static Error LockedError(DateRange period, string field = "Date")
        => Error.Validation(field, $"Payroll for {period.From:MMMM yyyy} is finalized, so this date cannot change. Void the payroll run first.");

    public void Invalidate() => _locked = null;

    private async Task<List<DateRange>> RangesAsync(CancellationToken cancellationToken)
    {
        if (_locked is null)
        {
            var periods = await db.PayrollPeriods.AsNoTracking()
                .Where(p => p.CompanyId == user.CompanyId && (p.Status == PayrollPeriodStatus.Locked || p.Status == PayrollPeriodStatus.Paid))
                .Select(p => new { p.FromDate, p.ToDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
            _locked = periods.Select(p => new DateRange(p.FromDate, p.ToDate)).OrderBy(r => r.From).ToList();
        }

        return _locked;
    }
}

/// <summary>The last date covered by finalized payroll, for the lock banner on attendance and leave pages (P4-T4).</summary>
[RequiresPermission(Permission.ViewData)]
public sealed record GetPayrollLockQuery : IQuery<DateOnly?>;

public sealed class GetPayrollLockHandler(PayrollLocks locks) : IRequestHandler<GetPayrollLockQuery, DateOnly?>
{
    public async Task<Result<DateOnly?>> HandleAsync(GetPayrollLockQuery request, CancellationToken cancellationToken)
        => await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false);
}
