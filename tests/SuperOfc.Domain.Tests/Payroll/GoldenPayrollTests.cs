using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Tests.Payroll;

/// <summary>
/// Golden payslip fixtures (docs/05): tests/Fixtures/Payroll/*.json hold inputs and hand-checked results in currency
/// units. Changing an expected value needs owner approval.
/// </summary>
public class GoldenPayrollTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Payroll");

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Fact]
    public void There_are_golden_fixtures_for_every_required_case()
    {
        var names = Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).ToList();
        names.Should().HaveCountGreaterThanOrEqualTo(8);
        foreach (var required in new[] { "full-month", "joiner", "leaver", "unpaid-leave", "overtime", "half-days", "late-penalty", "mid-month-raise" })
        {
            names.Should().Contain(n => n!.Contains(required, StringComparison.Ordinal), required);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Payslip_matches_fixture(string name)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")))!.AsObject();
        var (input, componentNames) = Build(json);

        var result = PayslipCalculator.Compute(input);

        var expected = json["expected"]!.AsObject();
        Major(result.Gross).Should().Be(expected["gross"]!.GetValue<decimal>(), "gross");
        Major(result.Deductions).Should().Be(expected["deductions"]!.GetValue<decimal>(), "deductions");
        Major(result.Net).Should().Be(expected["net"]!.GetValue<decimal>(), "net");
        result.Net.Should().Be(result.Gross - result.Deductions, "net is always gross minus deductions");
        result.Days.PayableDays.Should().Be(expected["payableDays"]!.GetValue<decimal>(), "payable days");
        Check(expected, "calendarDays", result.Days.CalendarDays);
        Check(expected, "presentDays", result.Days.PresentDays);
        Check(expected, "halfDays", result.Days.HalfDays);
        Check(expected, "paidLeaveDays", result.Days.PaidLeaveDays);
        Check(expected, "unpaidLeaveDays", result.Days.UnpaidLeaveDays);
        Check(expected, "lateDays", result.Days.LateDays);
        Check(expected, "latePenaltyDays", result.Days.LatePenaltyDays);
        Check(expected, "employerContributions", Major(result.EmployerContributions));
        Check(expected, "loan", Major(result.Loan));
        Check(expected, "skipped", result.Skipped.Count);

        var actual = result.Lines.ToDictionary(l => l.Source is PayslipLineSource.Component or PayslipLineSource.Overtime or PayslipLineSource.Tax ? l.Code : l.Name, l => Major(l.Amount));
        var lines = expected["lines"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<decimal>());
        actual.Should().BeEquivalentTo(lines, "every payslip line ({0})", string.Join(", ", componentNames));

        var warnings = expected["warnings"]?.AsArray().Select(w => w!.GetValue<string>()).ToList() ?? [];
        result.Warnings.Should().BeEquivalentTo(warnings);
    }

    private static void Check(JsonObject expected, string key, decimal actual)
    {
        if (expected[key] is { } value)
        {
            actual.Should().Be(value.GetValue<decimal>(), key);
        }
    }

    private static decimal Major(Money money) => money.Minor / (decimal)Money.MinorPerMajor;

    private static (PayslipInput Input, IReadOnlyList<string> Names) Build(JsonObject json)
    {
        var month = DateOnly.ParseExact(json["period"]!.GetValue<string>() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var period = DateRange.Month(month.Year, month.Month);
        var employment = new DateRange(Date(json["employment"]!["from"])!.Value, Date(json["employment"]!["to"]));

        var components = json["components"]!.AsArray().Select((c, i) => new ComponentDef(
            Guid.NewGuid(),
            c!["code"]!.GetValue<string>(),
            c["name"]!.GetValue<string>(),
            Enum.Parse<ComponentKind>(c["kind"]!.GetValue<string>()),
            Enum.Parse<ComponentCalc>(c["calc"]!.GetValue<string>()),
            c["formula"]?.GetValue<string>(),
            c["isBasic"]?.GetValue<bool>() ?? false,
            c["taxable"]?.GetValue<bool>() ?? true,
            c["attendanceLinked"]?.GetValue<bool>() ?? true,
            c["order"]?.GetValue<int>() ?? i)).ToList();
        var byCode = components.ToDictionary(c => c.Code);

        var segments = json["segments"]!.AsArray().Select(s => new PaySegment(
            new DateRange(Date(s!["from"])!.Value, Date(s["to"])),
            s["lines"]!.AsObject().Select(l => l.Value is JsonObject o
                ? new PayLine(byCode[l.Key].Id, Rate: o["rate"]!.GetValue<decimal>())
                : new PayLine(byCode[l.Key].Id, AmountMinor: (long)(l.Value!.GetValue<decimal>() * Money.MinorPerMajor))).ToList())).ToList();

        var offs = json["weeklyOffs"]!.AsArray().Select(d => Enum.Parse<DayOfWeek>(d!.GetValue<string>())).ToHashSet();
        var overrides = json["days"]?.AsObject() ?? [];
        var days = new List<PayDay>();
        foreach (var date in period.EachDay())
        {
            var status = !employment.Contains(date)
                ? (date < employment.From ? AttendanceStatus.NotJoined : AttendanceStatus.Exited)
                : offs.Contains(date.DayOfWeek) ? AttendanceStatus.WeeklyOff : AttendanceStatus.Present;
            var day = new PayDay(date, status);
            if (overrides[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] is { } o)
            {
                day = o is JsonObject obj
                    ? new PayDay(date, Enum.Parse<AttendanceStatus>(obj["status"]!.GetValue<string>()),
                        obj["leaveDays"]?.GetValue<decimal>() ?? (obj["status"]!.GetValue<string>() == "Leave" ? 1m : 0m),
                        obj["paid"]?.GetValue<bool>() ?? false, obj["ot"]?.GetValue<int>() ?? 0)
                    : new PayDay(date, Enum.Parse<AttendanceStatus>(o.GetValue<string>()));
            }

            days.Add(day);
        }

        var payroll = new PayrollPolicy();
        if (json["policy"] is JsonObject p)
        {
            payroll = payroll with
            {
                ProrateBasis = p["prorateBasis"] is { } b ? Enum.Parse<ProrateBasis>(b.GetValue<string>()) : payroll.ProrateBasis,
                MidMonthChange = p["midMonthChange"] is { } m ? Enum.Parse<MidMonthChange>(m.GetValue<string>()) : payroll.MidMonthChange,
                MinimumNetPayMinor = p["minimumNetPay"] is { } n ? (long)(n.GetValue<decimal>() * Money.MinorPerMajor) : payroll.MinimumNetPayMinor,
            };
        }

        var attendance = new AttendancePolicy();
        if (json["attendancePolicy"]?["latePenalty"] is JsonObject lp)
        {
            attendance = attendance with { LatePenalty = new LatePenalty(lp["instances"]!.GetValue<int>(), lp["deductDays"]!.GetValue<decimal>()) };
        }

        var workingDays = days.Count(d => !offs.Contains(d.Date.DayOfWeek));
        var input = new PayslipInput(period, employment, json["overtimeEligible"]?.GetValue<bool>() ?? true, days, workingDays, components, segments, payroll, attendance)
        {
            Adjustments = (json["adjustments"]?.AsArray() ?? []).Select(a => new AdjustmentInput(Guid.NewGuid(), a!["name"]!.GetValue<string>(),
                Enum.Parse<ComponentKind>(a["kind"]!.GetValue<string>()), Money.FromMajor(a["amount"]!.GetValue<decimal>()), a["taxable"]?.GetValue<bool>() ?? false)).ToList(),
            Loans = (json["loans"]?.AsArray() ?? []).Select(l => new DueDeduction(Guid.NewGuid(), l!["code"]!.GetValue<string>(), l["name"]!.GetValue<string>(),
                Money.FromMajor(l["amount"]!.GetValue<decimal>()))).ToList(),
        };
        return (input, components.Select(c => c.Code).ToList());
    }

    private static DateOnly? Date(JsonNode? node)
        => node is null ? null : DateOnly.ParseExact(node.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
