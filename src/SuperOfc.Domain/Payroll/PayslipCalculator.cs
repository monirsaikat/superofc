using System.Globalization;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Payroll;

/// <summary>A salary component as the calculator needs it.</summary>
public sealed record ComponentDef(
    Guid Id,
    string Code,
    string Name,
    ComponentKind Kind,
    ComponentCalc Calc,
    string? Formula,
    bool IsBasic,
    bool IsTaxable,
    bool IsAttendanceLinked,
    int DisplayOrder)
{
    public static ComponentDef From(SalaryComponent c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id, c.Code, c.Name, c.Kind, c.Calc, c.Formula, c.IsBasic, c.IsTaxable, c.IsAttendanceLinked, c.DisplayOrder);
    }
}

/// <summary>One component value in an assignment: amount for fixed components, rate (percent) for percentage ones.</summary>
public sealed record PayLine(Guid ComponentId, long AmountMinor = 0, decimal Rate = 0m);

/// <summary>An assignment clipped to the period. Segments must not overlap.</summary>
public sealed record PaySegment(DateRange Range, IReadOnlyList<PayLine> Lines);

public sealed record AdjustmentInput(Guid Id, string Name, ComponentKind Kind, Money Amount, bool IsTaxable, string? Reason = null);

/// <summary>
/// An amount owed this period that may be skipped to protect minimum net pay (loan installments, R4). The amount is
/// deducted as given (the loan schedule is already rounded). With <paramref name="AllowPartial"/> (final recovery from
/// a leaver, doc 10 Q26) as much as the minimum net allows is deducted instead of skipping.
/// </summary>
public sealed record DueDeduction(Guid ReferenceId, string Code, string Name, Money Amount, PayslipLineSource Source = PayslipLineSource.Loan, bool AllowPartial = false);

/// <summary>This month's taxable earnings in total and per component code (for exemptions such as a share of house rent).</summary>
/// <summary>This month's taxable pay for the tax hook. <see cref="OneOff"/> is the part (one-off adjustments) that does not repeat.</summary>
public sealed record TaxBase(Money Taxable, IReadOnlyDictionary<string, Money> ByCode)
{
    public Money OneOff { get; init; }
}

public sealed record PayslipInput(
    DateRange Period,
    DateRange Employment,
    bool OvertimeEligible,
    IReadOnlyList<PayDay> Days,
    int MonthWorkingDays,
    IReadOnlyList<ComponentDef> Components,
    IReadOnlyList<PaySegment> Segments,
    PayrollPolicy Payroll,
    AttendancePolicy Attendance)
{
    public IReadOnlyList<AdjustmentInput> Adjustments { get; init; } = [];

    public IReadOnlyList<DueDeduction> Loans { get; init; } = [];

    /// <summary>Monthly tax for this month's taxable earnings (R5). Null means no tax.</summary>
    public Func<TaxBase, Money>? Tax { get; init; }
}

public sealed record ComputedLine(
    int Order,
    string Code,
    string Name,
    ComponentKind Kind,
    PayslipLineSource Source,
    Money Amount,
    bool IsTaxable,
    string? Basis = null,
    Money? FullAmount = null,
    decimal? Quantity = null,
    Guid? ReferenceId = null);

public sealed record SkippedDeduction(DueDeduction Deduction, string Reason);

public sealed record PayslipResult(
    DaysSummary Days,
    IReadOnlyList<ComputedLine> Lines,
    Money Gross,
    Money Deductions,
    Money Net,
    Money Tax,
    Money Loan,
    Money EmployerContributions,
    Money TaxableGross,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SkippedDeduction> Skipped);

/// <summary>
/// Computes one payslip (doc 03 R3). Pure: everything comes in through <see cref="PayslipInput"/>.
/// Each line is rounded once with the policy; totals are sums of rounded lines, so Net = Gross - Deductions exactly.
/// </summary>
public static class PayslipCalculator
{
    public const string OvertimeCode = "OT";
    public const string HolidayOvertimeCode = "OT_HOLIDAY";
    public const string TaxCode = "TAX";

    public static PayslipResult Compute(PayslipInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var warnings = new List<string>();
        var period = input.Period;
        var employment = period.Intersect(input.Employment);
        var daysInMonth = period.IsOpenEnded ? throw new DomainException("A payroll period must have an end date.") : period.Days;
        var round = input.Payroll.LineRounding;

        var whole = PayableDaysCalculator.Summarize(input.Days, period, input.Employment, input.Attendance);
        whole = PayableDaysCalculator.ApplyLatePenalty(whole, input.Attendance.LatePenalty);

        if (employment is null)
        {
            return Empty(whole, ["Not employed in this period."]);
        }

        var segments = Segments(input, employment.Value);
        if (segments.Count == 0)
        {
            return Empty(whole, ["No salary is assigned for this period."]);
        }

        // Spread the late penalty over segments from the last one back (it is a whole-period rule).
        var segmentDays = segments.Select(s => PayableDaysCalculator.Summarize(input.Days, s.Range, input.Employment, input.Attendance)).ToList();
        var penalty = whole.LatePenaltyDays;
        for (var i = segmentDays.Count - 1; i >= 0 && penalty > 0; i--)
        {
            var take = Math.Min(penalty, segmentDays[i].PaidDaysBeforePenalty);
            segmentDays[i] = segmentDays[i] with { LatePenaltyDays = take };
            penalty -= take;
        }

        var components = input.Components.ToDictionary(c => c.Id);
        var lines = new List<ComputedLine>();
        var totals = new Dictionary<Guid, (decimal Amount, decimal Full, decimal Fraction)>();
        Dictionary<string, decimal> lastMonthly = new(StringComparer.Ordinal);
        for (var i = 0; i < segments.Count; i++)
        {
            var monthly = MonthlyAmounts(segments[i], components, warnings);
            lastMonthly = monthly.ToDictionary(m => components[m.Key].Code, m => m.Value, StringComparer.Ordinal);
            var linked = PayableDaysCalculator.LinkedFraction(segmentDays[i], daysInMonth, input.MonthWorkingDays, input.Payroll.ProrateBasis);
            var calendar = PayableDaysCalculator.CalendarFraction(segmentDays[i], daysInMonth);
            foreach (var (id, amount) in monthly)
            {
                var fraction = components[id].IsAttendanceLinked ? linked : calendar;
                var (sum, _, _) = totals.GetValueOrDefault(id);
                totals[id] = (sum + (amount * fraction), amount, fraction);
            }
        }

        var order = 0;
        foreach (var (id, (amount, full, fraction)) in totals.OrderBy(t => components[t.Key].Kind).ThenBy(t => components[t.Key].DisplayOrder).ThenBy(t => components[t.Key].Code, StringComparer.Ordinal))
        {
            var c = components[id];
            var rounded = new Money(round.Apply(amount));
            var basis = segments.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $"pro-rated over {segments.Count} salary periods")
                : fraction == 1m ? null : string.Create(CultureInfo.InvariantCulture, $"{Major(full)} × {Describe(fraction, segmentDays[0], daysInMonth, input, c.IsAttendanceLinked)}");
            lines.Add(new ComputedLine(++order, c.Code, c.Name, c.Kind, PayslipLineSource.Component, rounded, c.IsTaxable && c.Kind == ComponentKind.Earning,
                basis, new Money(round.Apply(full)), null));
        }

        // R3 step 3: overtime.
        if (input.OvertimeEligible && (whole.OvertimeMinutes > 0 || whole.HolidayOvertimeMinutes > 0))
        {
            var baseCode = input.Payroll.OvertimeRateBase == OvertimeRateBase.Gross ? null : BasicCode(input.Components);
            var baseMonthly = baseCode is null
                ? lastMonthly.Where(m => input.Components.First(c => c.Code == m.Key).Kind == ComponentKind.Earning).Sum(m => m.Value)
                : lastMonthly.GetValueOrDefault(baseCode);
            var basisDays = input.Payroll.ProrateBasis switch
            {
                ProrateBasis.Fixed30 => 30m,
                ProrateBasis.WorkingDays => Math.Max(1, input.MonthWorkingDays),
                _ => daysInMonth,
            };
            var hourly = baseMonthly / (basisDays * input.Payroll.StandardDayHours);
            if (baseMonthly <= 0)
            {
                warnings.Add("Overtime was worked but there is no basic salary to rate it.");
            }

            AddOvertime(lines, ref order, OvertimeCode, "Overtime", whole.OvertimeMinutes, hourly, input.Payroll.OvertimeMultiplier, round);
            AddOvertime(lines, ref order, HolidayOvertimeCode, "Holiday overtime", whole.HolidayOvertimeMinutes, hourly, input.Payroll.HolidayOvertimeMultiplier, round);
        }

        // R3 step 4: one-off adjustments.
        foreach (var a in input.Adjustments.OrderBy(a => a.Kind).ThenBy(a => a.Name, StringComparer.CurrentCulture))
        {
            lines.Add(new ComputedLine(++order, "ADJ", a.Name, a.Kind, PayslipLineSource.Adjustment, new Money(round.Apply(a.Amount.Minor)), a.IsTaxable && a.Kind == ComponentKind.Earning,
                a.Reason, ReferenceId: a.Id));
        }

        // R3 step 5: totals, tax, then loans within the minimum net rule.
        var gross = Sum(lines, ComponentKind.Earning);
        var taxable = Money.Sum(lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable).Select(l => l.Amount));
        var byCode = lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable).GroupBy(l => l.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Money.Sum(g.Select(l => l.Amount)), StringComparer.Ordinal);
        var oneOff = Money.Sum(lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable && l.Source == PayslipLineSource.Adjustment).Select(l => l.Amount));
        var tax = input.Tax is null ? Money.Zero : new Money(round.Apply(Money.Max(Money.Zero, input.Tax(new TaxBase(taxable, byCode) { OneOff = oneOff })).Minor));
        if (tax > Money.Zero)
        {
            lines.Add(new ComputedLine(++order, TaxCode, "Income tax", ComponentKind.Deduction, PayslipLineSource.Tax, tax, false));
        }

        var skipped = new List<SkippedDeduction>();
        var loan = Money.Zero;
        foreach (var due in input.Loans)
        {
            var amount = due.Amount;
            var room = gross - Sum(lines, ComponentKind.Deduction) - input.Payroll.MinimumNetPay;
            if (amount > room && due.AllowPartial && room > Money.Zero)
            {
                warnings.Add($"{due.Name}: only {room.Major:N2} of {amount.Major:N2} could be recovered.");
                amount = room;
            }

            if (amount > room || amount.Minor <= 0)
            {
                skipped.Add(new SkippedDeduction(due, "Skipped to keep net pay at or above the minimum."));
                continue;
            }

            lines.Add(new ComputedLine(++order, due.Code, due.Name, ComponentKind.Deduction, due.Source, amount, false, ReferenceId: due.ReferenceId));
            loan += amount;
        }

        var deductions = Sum(lines, ComponentKind.Deduction);
        var net = gross - deductions;
        if (net.IsNegative)
        {
            warnings.Add("Deductions are larger than gross pay.");
        }

        return new PayslipResult(whole, lines, gross, deductions, net, tax, loan, Sum(lines, ComponentKind.EmployerContribution), taxable, warnings, skipped);
    }

    /// <summary>
    /// Full monthly amounts (minor units, unrounded) for the components of one segment, in calculation order.
    /// Used by the calculator and by the structure preview.
    /// </summary>
    public static Dictionary<Guid, decimal> MonthlyAmounts(PaySegment segment, IReadOnlyDictionary<Guid, ComponentDef> components, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(warnings);
        var lines = segment.Lines.Where(l => components.ContainsKey(l.ComponentId)).GroupBy(l => l.ComponentId).ToDictionary(g => g.Key, g => g.Last());
        var values = new Dictionary<Guid, decimal>();
        var byCode = new Dictionary<string, decimal>(StringComparer.Ordinal);

        var ordered = lines.Keys.Select(id => components[id])
            .OrderBy(c => SalaryRules.Stage(c.Calc, c.Kind, c.IsBasic)).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
        var basic = ordered.FirstOrDefault(c => c.IsBasic);

        // Stage 0 and 1 in dependency order: percent-of-basic waits for basic, formulas wait for the codes they use.
        var present = ordered.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var pending = ordered.Where(c => SalaryRules.Stage(c.Calc, c.Kind, c.IsBasic) <= 1).ToList();
        var needs = pending.ToDictionary(c => c.Id, c => c.Calc switch
        {
            ComponentCalc.PercentOfBasic when basic is not null => new List<string> { basic.Code },
            ComponentCalc.Formula => CodesIn(c.Formula).Where(present.Contains).ToList(),
            _ => new List<string>(),
        });
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(c => needs[c.Id].All(byCode.ContainsKey));
            if (ready is null)
            {
                foreach (var c in pending)
                {
                    warnings.Add($"{c.Code}: the formula depends on itself through other components and was set to 0.");
                    values[c.Id] = 0;
                    byCode[c.Code] = 0;
                }

                break;
            }

            var amount = ready.Calc switch
            {
                ComponentCalc.Fixed => lines[ready.Id].AmountMinor,
                ComponentCalc.PercentOfBasic => basic is null ? 0 : values.GetValueOrDefault(basic.Id) * lines[ready.Id].Rate / 100m,
                ComponentCalc.Formula => Formula(ready, byCode, warnings),
                _ => 0,
            };
            values[ready.Id] = Math.Max(0, amount);
            byCode[ready.Code] = values[ready.Id] / Money.MinorPerMajor;
            pending.Remove(ready);
        }

        // Stage 2: percent-of-gross earnings use the earnings before them; stage 3 uses the full gross.
        foreach (var c in ordered.Where(c => SalaryRules.Stage(c.Calc, c.Kind, c.IsBasic) >= 2))
        {
            var grossSoFar = values.Where(v => components[v.Key].Kind == ComponentKind.Earning).Sum(v => v.Value);
            values[c.Id] = Math.Max(0, grossSoFar * lines[c.Id].Rate / 100m);
            byCode[c.Code] = values[c.Id] / Money.MinorPerMajor;
        }

        return values;
    }

    private static decimal Formula(ComponentDef c, Dictionary<string, decimal> byCode, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(c.Formula))
        {
            warnings.Add($"{c.Code}: no formula, set to 0.");
            return 0;
        }

        try
        {
            return FormulaEvaluator.Evaluate(c.Formula, code => byCode.GetValueOrDefault(code)) * Money.MinorPerMajor;
        }
        catch (FormatException ex)
        {
            warnings.Add($"{c.Code}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Codes mentioned in a formula (also used to find dependencies).</summary>
    public static IEnumerable<string> CodesIn(string? formula) => string.IsNullOrEmpty(formula)
        ? []
        : System.Text.RegularExpressions.Regex.Matches(formula.ToUpperInvariant(), "[A-Z_][A-Z0-9_]*").Select(m => m.Value).Distinct(StringComparer.Ordinal);

    private static List<PaySegment> Segments(PayslipInput input, DateRange employment)
    {
        var clipped = input.Segments
            .Select(s => (Segment: s, Range: s.Range.Intersect(employment)))
            .Where(s => s.Range is not null)
            .OrderBy(s => s.Range!.Value.From)
            .Select(s => s.Segment with { Range = s.Range!.Value })
            .ToList();
        if (clipped.Count <= 1 || input.Payroll.MidMonthChange == MidMonthChange.ProRateBySegments)
        {
            return clipped;
        }

        // UseLatest: the assignment in force on the last employed day applies to the whole period.
        return [clipped[^1] with { Range = employment }];
    }

    private static void AddOvertime(List<ComputedLine> lines, ref int order, string code, string name, int minutes, decimal hourly, decimal multiplier, RoundingPolicy round)
    {
        if (minutes <= 0)
        {
            return;
        }

        var hours = minutes / 60m;
        var amount = new Money(round.Apply(hours * hourly * multiplier));
        lines.Add(new ComputedLine(++order, code, name, ComponentKind.Earning, PayslipLineSource.Overtime, amount, true,
            string.Create(CultureInfo.InvariantCulture, $"{hours:0.##} h × {Major(hourly):0.##} × {multiplier:0.##}"), Quantity: hours));
    }

    private static string? BasicCode(IReadOnlyList<ComponentDef> components) => components.FirstOrDefault(c => c.IsBasic)?.Code;

    private static string Describe(decimal fraction, DaysSummary days, int daysInMonth, PayslipInput input, bool linked)
    {
        if (!linked)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{days.CalendarDays}/{daysInMonth} days");
        }

        return input.Payroll.ProrateBasis switch
        {
            ProrateBasis.Fixed30 => string.Create(CultureInfo.InvariantCulture, $"{fraction * 30:0.##}/30 days"),
            ProrateBasis.WorkingDays => string.Create(CultureInfo.InvariantCulture, $"{fraction * input.MonthWorkingDays:0.##}/{input.MonthWorkingDays} working days"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{days.PayableDays:0.##}/{daysInMonth} days"),
        };
    }

    private static decimal Major(decimal minor) => minor / Money.MinorPerMajor;

    private static Money Sum(IEnumerable<ComputedLine> lines, ComponentKind kind) => Money.Sum(lines.Where(l => l.Kind == kind).Select(l => l.Amount));

    private static PayslipResult Empty(DaysSummary days, IReadOnlyList<string> warnings)
        => new(days, [], Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, warnings, []);
}
