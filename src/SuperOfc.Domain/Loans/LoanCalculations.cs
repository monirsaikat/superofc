using SuperOfc.Domain.Common;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Loans;

public sealed record ScheduleRequest(Money Principal, InterestMethod Method, decimal AnnualRatePercent, int Count, int StartYear, int StartMonth, RoundingPolicy Rounding);

public sealed record ScheduledInstallment(int Sequence, int Year, int Month, Money Principal, Money Interest, Money Amount);

public sealed record LoanSchedule(Money Principal, Money Interest, Money Total, Money Installment, IReadOnlyList<ScheduledInstallment> Installments);

/// <summary>
/// Builds the repayment schedule (doc 03 R4). Flat interest = principal × rate × months / 12 / 100.
/// Installments are the total divided by the count, rounded up to the payroll rounding step so payslips need no
/// further rounding; the last installment absorbs the difference. The schedule always sums exactly to the total.
/// </summary>
public static class LoanScheduleGenerator
{
    public const int MaxInstallments = 120;

    public static Result<LoanSchedule> Generate(ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Principal.Minor <= 0)
        {
            return Error.Validation("Principal", "Enter the amount.");
        }

        if (request.Count is < 1 or > MaxInstallments)
        {
            return Error.Validation("InstallmentCount", $"Use 1 to {MaxInstallments} installments.");
        }

        if (request.Method == InterestMethod.FlatRate && request.AnnualRatePercent is <= 0 or > 100)
        {
            return Error.Validation("AnnualRate", "Enter a yearly interest rate above 0 and up to 100 %.");
        }

        if (request.StartMonth is < 1 or > 12)
        {
            return Error.Validation("StartMonth", "Choose the first month of repayment.");
        }

        var interest = request.Method == InterestMethod.FlatRate
            ? (long)Math.Round(request.Principal.Minor * request.AnnualRatePercent * request.Count / 1200m, MidpointRounding.AwayFromZero)
            : 0;
        var total = request.Principal.Minor + interest;
        var installment = InstallmentAmount(total, request.Count, Math.Max(1, request.Rounding.StepMinor));
        var last = total - (installment * (request.Count - 1));
        if (last <= 0)
        {
            return Error.Validation("InstallmentCount", "Too many installments for this amount.");
        }

        // Interest is spread in proportion to each amount; the last installment takes what is left.
        var rows = new List<(long Amount, long Interest)>();
        long interestSoFar = 0;
        for (var i = 0; i < request.Count; i++)
        {
            var amount = i == request.Count - 1 ? last : installment;
            var part = i == request.Count - 1 ? interest - interestSoFar : interest * amount / total;
            rows.Add((amount, part));
            interestSoFar += part;
        }

        // Rare with tiny last installments: never let interest exceed the installment.
        for (var i = rows.Count - 1; i > 0 && rows[i].Interest > rows[i].Amount; i--)
        {
            var excess = rows[i].Interest - rows[i].Amount;
            rows[i] = (rows[i].Amount, rows[i].Amount);
            rows[i - 1] = (rows[i - 1].Amount, rows[i - 1].Interest + excess);
        }

        var start = new DateOnly(request.StartYear, request.StartMonth, 1);
        var list = rows.Select((r, i) =>
        {
            var due = start.AddMonths(i);
            return new ScheduledInstallment(i + 1, due.Year, due.Month, new Money(r.Amount - r.Interest), new Money(r.Interest), new Money(r.Amount));
        }).ToList();
        return new LoanSchedule(request.Principal, new Money(interest), new Money(total), new Money(installment), list);
    }

    private static long InstallmentAmount(long total, int count, long step)
    {
        var exact = (total + count - 1) / count;
        var rounded = (exact + step - 1) / step * step;
        return rounded * (count - 1) < total ? rounded : exact;
    }
}

public sealed record LoanBalance(Money Total, Money Repaid, Money Outstanding, Money OutstandingPrincipal, Money OutstandingInterest);

public sealed record SettlementQuote(Money Outstanding, Money PrincipalDue, Money InterestCharged, Money InterestWaived, Money Payable);

/// <summary>Balances, payment allocation and early settlement (doc 03 R4). Pure.</summary>
public static class LoanBalanceCalculator
{
    /// <summary>Total minus repayments; zero before the loan is paid out.</summary>
    public static LoanBalance Balance(Loan loan)
    {
        ArgumentNullException.ThrowIfNull(loan);
        if (loan.Status is LoanStatus.Requested or LoanStatus.Rejected or LoanStatus.Approved)
        {
            return new LoanBalance(loan.Total, Money.Zero, Money.Zero, Money.Zero, Money.Zero);
        }

        var repaid = loan.Transactions.Where(t => t.ReducesBalance).Sum(t => t.AmountMinor);
        var open = loan.Installments.Where(i => i.Status == InstallmentStatus.Due).ToList();
        return new LoanBalance(loan.Total, new Money(repaid), new Money(loan.Total.Minor - repaid),
            new Money(open.Sum(i => i.RemainingPrincipalMinor)), new Money(open.Sum(i => i.RemainingInterestMinor)));
    }

    /// <summary>Spreads a payment over open installments, earliest first. Returns (installment, amount) pairs.</summary>
    public static IReadOnlyList<(LoanInstallment Installment, long Amount)> Allocate(IEnumerable<LoanInstallment> installments, long amountMinor)
    {
        ArgumentNullException.ThrowIfNull(installments);
        var result = new List<(LoanInstallment, long)>();
        var left = amountMinor;
        foreach (var installment in installments.Where(i => i.IsOpen).OrderBy(i => i.PeriodIndex).ThenBy(i => i.Sequence))
        {
            if (left <= 0)
            {
                break;
            }

            var part = Math.Min(left, installment.RemainingMinor);
            result.Add((installment, part));
            left -= part;
        }

        return result;
    }

    /// <summary>
    /// What it costs to close the loan now (policy SettlementInterest): remaining principal plus none, the earned
    /// share, or all of the remaining interest. ProRata charges interest for months up to and including this one.
    /// </summary>
    public static SettlementQuote Settlement(Loan loan, SettlementInterest policy, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(loan);
        var open = loan.Installments.Where(i => i.Status == InstallmentStatus.Due).ToList();
        var principal = open.Sum(i => i.RemainingPrincipalMinor);
        var remainingInterest = open.Sum(i => i.RemainingInterestMinor);
        long charged = policy switch
        {
            SettlementInterest.Full => remainingInterest,
            SettlementInterest.None => 0,
            _ => ProRata(loan, remainingInterest, year, month),
        };
        return new SettlementQuote(new Money(principal + remainingInterest), new Money(principal), new Money(charged), new Money(remainingInterest - charged), new Money(principal + charged));
    }

    private static long ProRata(Loan loan, long remainingInterest, int year, int month)
    {
        if (loan.InstallmentCount <= 0 || loan.InterestMinor == 0)
        {
            return 0;
        }

        var elapsed = Math.Clamp(((year * 12) + month) - ((loan.StartYear * 12) + loan.StartMonth) + 1, 0, loan.InstallmentCount);
        var earned = (long)Math.Round(loan.InterestMinor * (decimal)elapsed / loan.InstallmentCount, MidpointRounding.AwayFromZero);
        var paid = loan.InterestMinor - remainingInterest;
        return Math.Clamp(earned - paid, 0, remainingInterest);
    }
}
