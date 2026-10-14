using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Loans;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<Loan> Loans { get; }

    DbSet<LoanInstallment> LoanInstallments { get; }

    DbSet<LoanTransaction> LoanTransactions { get; }

    DbSet<LoanAllocation> LoanAllocations { get; }
}
