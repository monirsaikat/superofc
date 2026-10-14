using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Tests.Payroll;

public class FormulaEvaluatorTests
{
    private static readonly HashSet<string> Codes = ["BASIC", "HRA", "CONV"];

    [Theory]
    [InlineData("BASIC * 0.1", 3000)]
    [InlineData("(BASIC + HRA) / 2", 22500)]
    [InlineData("-BASIC + 100", -29900)]
    [InlineData("basic * 2 - hra", 45000)]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("BASIC / 0", 0)]
    public void Evaluates_arithmetic_on_component_values(string formula, decimal expected)
    {
        FormulaEvaluator.Parse(formula, Codes).IsSuccess.Should().BeTrue();
        FormulaEvaluator.Evaluate(formula, code => code switch { "BASIC" => 30000, "HRA" => 15000, _ => 0 }).Should().Be(expected);
    }

    [Theory]
    [InlineData("BASIC * 0.1; DROP TABLE")]
    [InlineData("Math.Max(BASIC, 1)")]
    [InlineData("BASIC ^ 2")]
    [InlineData("BASIC % 2")]
    [InlineData("\"text\"")]
    [InlineData("BASIC *")]
    [InlineData("(BASIC + 1")]
    [InlineData("BASIC 2")]
    [InlineData("UNKNOWN + 1")]
    [InlineData("1..2")]
    [InlineData("")]
    public void Rejects_anything_but_numbers_codes_operators_and_brackets(string formula)
        => FormulaEvaluator.Parse(formula, Codes).IsFailure.Should().BeTrue();

    [Fact]
    public void Reports_the_codes_a_formula_uses()
        => FormulaEvaluator.Parse("(basic + HRA) * 0.5 + basic", Codes).Value.Should().BeEquivalentTo(["BASIC", "HRA"]);

    [Fact]
    public void Deep_nesting_is_rejected_instead_of_overflowing()
        => FormulaEvaluator.Parse(new string('(', 60) + "1" + new string(')', 60), Codes).IsFailure.Should().BeTrue();
}

public class PayslipCalculatorRuleTests
{
    private static readonly DateRange March = DateRange.Month(2026, 3);

    private static PayslipInput Input(IReadOnlyList<ComponentDef> components, IReadOnlyList<PayLine> lines, IReadOnlyList<PayDay>? days = null)
        => new(March, new DateRange(new DateOnly(2025, 1, 1), null), true, days ?? [.. March.EachDay().Select(d => new PayDay(d, AttendanceStatus.Present))], 31,
            components, [new PaySegment(new DateRange(new DateOnly(2025, 1, 1), null), lines)], new PayrollPolicy(), new AttendancePolicy());

    private static ComponentDef C(string code, ComponentKind kind, ComponentCalc calc, string? formula = null, bool basic = false, int order = 0)
        => new(Guid.NewGuid(), code, code, kind, calc, formula, basic, true, true, order);

    [Fact]
    public void Formula_cycles_are_set_to_zero_with_a_warning()
    {
        var basic = C("BASIC", ComponentKind.Earning, ComponentCalc.Fixed, basic: true);
        var a = C("A", ComponentKind.Earning, ComponentCalc.Formula, "B + 1");
        var b = C("B", ComponentKind.Earning, ComponentCalc.Formula, "A + 1");
        var result = PayslipCalculator.Compute(Input([basic, a, b], [new(basic.Id, 1_000_000), new(a.Id), new(b.Id)]));

        result.Gross.Should().Be(new Money(1_000_000));
        result.Warnings.Should().HaveCount(2).And.OnlyContain(w => w.Contains("depends on itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Formulas_are_evaluated_after_the_components_they_use_whatever_the_display_order()
    {
        var basic = C("BASIC", ComponentKind.Earning, ComponentCalc.Fixed, basic: true, order: 3);
        var total = C("TOTAL", ComponentKind.Earning, ComponentCalc.Formula, "BASIC + HRA", order: 1);
        var hra = C("HRA", ComponentKind.Earning, ComponentCalc.PercentOfBasic, order: 2);
        var result = PayslipCalculator.Compute(Input([basic, total, hra], [new(basic.Id, 2_000_000), new(total.Id), new(hra.Id, Rate: 50)]));

        result.Lines.Single(l => l.Code == "TOTAL").Amount.Should().Be(new Money(3_000_000));
    }

    [Fact]
    public void Percent_of_gross_earnings_use_earlier_earnings_and_deductions_use_the_full_gross()
    {
        var basic = C("BASIC", ComponentKind.Earning, ComponentCalc.Fixed, basic: true, order: 1);
        var first = C("G1", ComponentKind.Earning, ComponentCalc.PercentOfGross, order: 2);
        var second = C("G2", ComponentKind.Earning, ComponentCalc.PercentOfGross, order: 3);
        var ded = C("D", ComponentKind.Deduction, ComponentCalc.PercentOfGross, order: 0);
        var result = PayslipCalculator.Compute(Input([basic, first, second, ded],
            [new(basic.Id, 1_000_000), new(first.Id, Rate: 10), new(second.Id, Rate: 10), new(ded.Id, Rate: 10)]));

        Amount(result, "G1").Should().Be(1_000);      // 10 % of 10,000
        Amount(result, "G2").Should().Be(1_100);      // 10 % of 11,000
        Amount(result, "D").Should().Be(1_210);       // 10 % of 12,100
    }

    [Fact]
    public void Half_day_leave_pays_the_leave_half_and_the_worked_half()
    {
        var days = March.EachDay().Select(d => new PayDay(d, AttendanceStatus.Present)).ToList();
        days[1] = new PayDay(days[1].Date, AttendanceStatus.Present, 0.5m, LeaveIsPaid: false);
        days[2] = new PayDay(days[2].Date, AttendanceStatus.Present, 0.5m, LeaveIsPaid: true);
        days[3] = new PayDay(days[3].Date, AttendanceStatus.Absent, 0.5m, LeaveIsPaid: true);

        var summary = PayableDaysCalculator.Summarize(days, March, March, new AttendancePolicy());

        summary.UnpaidLeaveDays.Should().Be(0.5m);
        summary.PaidLeaveDays.Should().Be(1m);
        summary.AbsentDays.Should().Be(0.5m);
        summary.PayableDays.Should().Be(30m);
    }

    [Fact]
    public void Unpaid_offs_and_holidays_reduce_payable_days()
    {
        var days = March.EachDay().Select(d => new PayDay(d, d.DayOfWeek == DayOfWeek.Friday ? AttendanceStatus.WeeklyOff : d.Day == 26 ? AttendanceStatus.Holiday : AttendanceStatus.Present)).ToList();
        var summary = PayableDaysCalculator.Summarize(days, March, March, new AttendancePolicy { WeeklyOffsPaid = false, HolidaysPaid = false });

        summary.WeeklyOffs.Should().Be(4);
        summary.Holidays.Should().Be(1);
        summary.PayableDays.Should().Be(26m);
    }

    [Fact]
    public void Net_is_never_rounded_twice()
    {
        var basic = C("BASIC", ComponentKind.Earning, ComponentCalc.Fixed, basic: true);
        var thirdA = C("A", ComponentKind.Earning, ComponentCalc.Formula, "BASIC / 3");
        var thirdB = C("B", ComponentKind.Deduction, ComponentCalc.Formula, "BASIC / 3");
        var result = PayslipCalculator.Compute(Input([basic, thirdA, thirdB], [new(basic.Id, 100_000), new(thirdA.Id), new(thirdB.Id)]));

        result.Lines.Should().OnlyContain(l => l.Amount.Minor % 100 == 0);
        result.Net.Should().Be(result.Gross - result.Deductions);
    }

    private static long Amount(PayslipResult result, string code) => result.Lines.Single(l => l.Code == code).Amount.Minor / 100;
}
