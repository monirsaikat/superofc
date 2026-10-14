using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Application.Payroll;

public sealed record PayrollContext(PayrollPeriod Period, IReadOnlyList<Guid> EmployeeIds, PolicySnapshot Policy);

/// <summary>What a module adds to a payroll computation for each employee.</summary>
public sealed class PayrollContribution
{
    public Dictionary<Guid, List<DueDeduction>> Deductions { get; } = [];

    public Dictionary<Guid, Func<TaxBase, Money>> Tax { get; } = [];

    public void AddDeduction(Guid employeeId, DueDeduction deduction)
    {
        if (!Deductions.TryGetValue(employeeId, out var list))
        {
            Deductions[employeeId] = list = [];
        }

        list.Add(deduction);
    }
}

/// <summary>
/// A module that takes part in payroll (loans in Phase 5, tax in Phase 6). Participants add deductions before the
/// calculation and update their own records when a run is finalized or voided, inside the same transaction. A failed
/// result cancels the finalize or void.
/// </summary>
public interface IPayrollParticipant
{
    Task<PayrollContribution> PrepareAsync(PayrollContext context, CancellationToken cancellationToken);

    /// <summary>The latest change to this module's inputs for the period; finalize refuses a draft computed before it.</summary>
    Task<DateTime?> LastInputChangeAsync(PayrollPeriod period, CancellationToken cancellationToken);

    Task<Result> OnFinalizedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken);

    Task<Result> OnVoidedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken);
}
