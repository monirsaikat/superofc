using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<SalaryComponent> SalaryComponents { get; }

    DbSet<SalaryStructure> SalaryStructures { get; }

    DbSet<SalaryStructureLine> SalaryStructureLines { get; }

    DbSet<SalaryAssignment> SalaryAssignments { get; }

    DbSet<SalaryAssignmentLine> SalaryAssignmentLines { get; }

    DbSet<PayrollPeriod> PayrollPeriods { get; }

    DbSet<PayrollRun> PayrollRuns { get; }

    DbSet<Payslip> Payslips { get; }

    DbSet<PayslipLine> PayslipLines { get; }

    DbSet<PayrollAdjustment> PayrollAdjustments { get; }
}
