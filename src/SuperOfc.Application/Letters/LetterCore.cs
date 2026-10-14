using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Application.Letters;

/// <summary>A field that can be used in a letter. <see cref="Private"/> fields show pay or bank details.</summary>
public sealed record LetterField(string Key, string Description, bool Private = false)
{
    public string Token => "{" + Key + "}";
}

/// <summary>Every field a letter may use, and how each is filled for one employee (doc 03 R8).</summary>
public static class LetterFields
{
    public static IReadOnlyList<LetterField> All { get; } =
    [
        new("Employee.Name", "Full name"),
        new("Employee.Code", "Employee code"),
        new("Employee.Title", "Mr. or Ms. (from gender)"),
        new("Employee.HeShe", "he or she"),
        new("Employee.HimHer", "him or her"),
        new("Employee.HisHer", "his or her"),
        new("Employee.Designation", "Designation"),
        new("Employee.Department", "Department"),
        new("Employee.Location", "Location"),
        new("Employee.Grade", "Grade"),
        new("Employee.JoinDate", "Join date"),
        new("Employee.ConfirmationDate", "Confirmation date"),
        new("Employee.ExitDate", "Last working day (employees who left)"),
        new("Employee.ExitReason", "Reason for leaving"),
        new("Employee.Service", "Length of service, e.g. 3 years 2 months"),
        new("Employee.DateOfBirth", "Date of birth"),
        new("Employee.Address", "Address"),
        new("Employee.Phone", "Phone"),
        new("Employee.Email", "E-mail"),
        new("Employee.NationalId", "National ID"),
        new("Employee.BankName", "Bank name", Private: true),
        new("Employee.BankAccount", "Bank account", Private: true),
        new("Employee.TaxId", "Tax ID", Private: true),
        new("Salary.Gross", "Monthly gross salary on the letter date", Private: true),
        new("Salary.GrossInWords", "Monthly gross salary in words", Private: true),
        new("Salary.Net", "Monthly net salary on the letter date", Private: true),
        new("Salary.NetInWords", "Monthly net salary in words", Private: true),
        new("Salary.YearlyGross", "Gross salary for 12 months", Private: true),
        new("Salary.EffectiveFrom", "Date the salary on the letter date started", Private: true),
        new("Salary.NewGross", "Latest salary, also if it starts later (increment letters)", Private: true),
        new("Salary.NewEffectiveFrom", "Date the latest salary starts", Private: true),
        new("Salary.PreviousGross", "Salary before the latest one", Private: true),
        new("Salary.Increase", "Latest salary minus the one before", Private: true),
        new("Company.Name", "Company name"),
        new("Company.Address", "Company address"),
        new("Company.Phone", "Company phone"),
        new("Company.Email", "Company e-mail"),
        new("Letter.Number", "Reference number, e.g. HR/2026/0001"),
        new("Letter.Date", "Letter date"),
        new("Today", "Today's date"),
    ];

    /// <summary>Salary component amounts are also available as <c>{Salary.CODE}</c>, for example <c>{Salary.BASIC}</c>.</summary>
    public const string ComponentHint = "Salary.<component code>";

    public static bool IsKnown(string key, IReadOnlySet<string> componentCodes)
        => All.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))
           || key.StartsWith(LetterText.InputGroup + ".", StringComparison.OrdinalIgnoreCase)
           || (key.StartsWith("Salary.", StringComparison.OrdinalIgnoreCase) && componentCodes.Contains(key[7..]));

    public static bool IsPrivate(string key)
        => key.StartsWith("Salary.", StringComparison.OrdinalIgnoreCase) || All.Any(f => f.Private && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool UsesPrivate(params string?[] texts) => texts.SelectMany(LetterText.Fields).Any(IsPrivate);
}

/// <summary>Collects the values of every field for a set of employees in a few queries.</summary>
public sealed class LetterData(IAppDbContext db, ICurrentUser user, ICompanyContext company, IPolicyProvider policies)
{
    public async Task<IReadOnlySet<string>> ComponentCodesAsync(CancellationToken cancellationToken)
        => (await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null).Select(c => c.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Field values per employee. Salary fields are filled only when <paramref name="withSalary"/> is true.</summary>
    public async Task<Dictionary<Guid, Dictionary<string, string?>>> ValuesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly letterDate, bool withSalary,
        CancellationToken cancellationToken)
    {
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        var money = MoneyFormatter.For(info);
        string D(DateOnly? d) => d?.ToString(info.DateFormat, CultureInfo.CurrentCulture) ?? string.Empty;

        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && employeeIds.Contains(e.Id)).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var names = await NamesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, List<SalaryAssignment>> salaries = [];
        List<ComponentDef> components = [];
        RoundingPolicy rounding = RoundingPolicy.None;
        if (withSalary)
        {
            salaries = (await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines).Where(a => employeeIds.Contains(a.EmployeeId)).ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.EffectiveFrom).ToList());
            components = [.. (await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ComponentDef.From)];
            rounding = (await policies.GetSnapshotAsync(letterDate, cancellationToken).ConfigureAwait(false)).Payroll.LineRounding;
        }

        var result = new Dictionary<Guid, Dictionary<string, string?>>();
        foreach (var e in employees)
        {
            var female = e.Gender == Gender.Female;
            var known = e.Gender is Gender.Male or Gender.Female;
            var v = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Employee.Name"] = e.FullName,
                ["Employee.Code"] = e.Code,
                ["Employee.Title"] = known ? (female ? "Ms." : "Mr.") : string.Empty,
                ["Employee.HeShe"] = known ? (female ? "she" : "he") : "they",
                ["Employee.HimHer"] = known ? (female ? "her" : "him") : "them",
                ["Employee.HisHer"] = known ? (female ? "her" : "his") : "their",
                ["Employee.Designation"] = Name(names, e.DesignationId),
                ["Employee.Department"] = Name(names, e.DepartmentId),
                ["Employee.Location"] = Name(names, e.LocationId),
                ["Employee.Grade"] = Name(names, e.GradeId),
                ["Employee.JoinDate"] = D(e.JoinDate),
                ["Employee.ConfirmationDate"] = D(e.ConfirmationDate),
                ["Employee.ExitDate"] = D(e.ExitDate),
                ["Employee.ExitReason"] = e.ExitReason,
                ["Employee.Service"] = LetterText.Duration(e.JoinDate, e.ExitDate is { } exit && exit < letterDate ? exit.AddDays(1) : letterDate),
                ["Employee.DateOfBirth"] = D(e.DateOfBirth),
                ["Employee.Address"] = e.Address,
                ["Employee.Phone"] = e.Phone,
                ["Employee.Email"] = e.Email,
                ["Employee.NationalId"] = e.NationalId,
                ["Company.Name"] = info.Name,
                ["Company.Address"] = info.Address,
                ["Company.Phone"] = info.Phone,
                ["Company.Email"] = info.Email,
                ["Letter.Date"] = D(letterDate),
                ["Today"] = D(letterDate),
            };

            if (withSalary)
            {
                v["Employee.BankName"] = e.BankName;
                v["Employee.BankAccount"] = e.BankAccount;
                v["Employee.TaxId"] = e.TaxIdentifier;
                var list = salaries.GetValueOrDefault(e.Id) ?? [];
                var current = list.LastOrDefault(a => a.EffectiveFrom <= letterDate);
                var latest = list.LastOrDefault();
                var previous = list.Count > 1 ? list[^2] : null;
                SalaryPreview? Preview(SalaryAssignment? a) => a is null
                    ? null
                    : SalaryLines.Preview([.. a.Lines.Select(l => new SalaryLineInput(l.ComponentId, l.AmountMinor, l.Rate))], components, rounding);
                var now = Preview(current);
                var newest = Preview(latest);
                var before = Preview(previous);
                if (now is not null)
                {
                    v["Salary.Gross"] = money.Format(now.Gross);
                    v["Salary.GrossInWords"] = AmountInWords.Format(now.Gross, info.CurrencyCode, info.Grouping);
                    v["Salary.Net"] = money.Format(now.Net);
                    v["Salary.NetInWords"] = AmountInWords.Format(now.Net, info.CurrencyCode, info.Grouping);
                    v["Salary.YearlyGross"] = money.Format(new Money(now.Gross.Minor * 12));
                    v["Salary.EffectiveFrom"] = D(current!.EffectiveFrom);
                    foreach (var line in now.Lines)
                    {
                        v["Salary." + line.Code] = money.Format(line.Amount);
                    }
                }

                if (newest is not null)
                {
                    v["Salary.NewGross"] = money.Format(newest.Gross);
                    v["Salary.NewEffectiveFrom"] = D(latest!.EffectiveFrom);
                }

                if (before is not null && newest is not null)
                {
                    v["Salary.PreviousGross"] = money.Format(before.Gross);
                    v["Salary.Increase"] = money.Format(new Money(newest.Gross.Minor - before.Gross.Minor));
                }
            }

            result[e.Id] = v;
        }

        return result;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(CancellationToken cancellationToken)
    {
        var companyId = user.CompanyId;
        var names = new Dictionary<Guid, string>();
        foreach (var (id, name) in await db.Departments.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => new ValueTuple<Guid, string>(x.Id, x.Name))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[id] = name;
        }

        foreach (var (id, name) in await db.Designations.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => new ValueTuple<Guid, string>(x.Id, x.Name))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[id] = name;
        }

        foreach (var (id, name) in await db.Locations.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => new ValueTuple<Guid, string>(x.Id, x.Name))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[id] = name;
        }

        foreach (var (id, name) in await db.Grades.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => new ValueTuple<Guid, string>(x.Id, x.Name))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[id] = name;
        }

        return names;
    }

    private static string? Name(Dictionary<Guid, string> names, Guid? id) => id is { } x ? names.GetValueOrDefault(x) : null;
}

/// <summary>Standard letters added at first run and on request. Offices edit them to their own wording.</summary>
public static class LetterDefaults
{
    private const string Signature = "\n\nFor **{Company.Name}**\n\n\n\nAuthorised signatory";

    public static IEnumerable<LetterTemplate> Templates(Guid companyId, Func<Guid> newId)
    {
        var order = 0;
        LetterTemplate T(string name, LetterKind kind, string subject, string body) => new()
        {
            Id = newId(), CompanyId = companyId, Name = name, Kind = kind, Subject = subject, Body = body + Signature, NumberPrefix = "HR", SortOrder = order++,
        };

        yield return T("Appointment letter", LetterKind.Appointment, "Letter of appointment",
            "Dear {Employee.Title} {Employee.Name},\n\nWe are pleased to appoint you as **{Employee.Designation}** in the {Employee.Department} department, "
            + "starting on **{Employee.JoinDate}**. Your monthly gross salary will be **{Salary.Gross}** ({Salary.GrossInWords}).\n\n"
            + "Your employee code is {Employee.Code}. You will be on probation until your appointment is confirmed in writing. "
            + "Other terms follow the company's policies as amended from time to time.\n\n"
            + "Please sign and return a copy of this letter to accept the appointment.\n\nWe look forward to working with you.");
        yield return T("Confirmation letter", LetterKind.Confirmation, "Confirmation of employment",
            "Dear {Employee.Title} {Employee.Name},\n\nWe are pleased to confirm your employment as **{Employee.Designation}** "
            + "with effect from **{Employee.ConfirmationDate}**.\n\nThank you for your work during the probation period.");
        yield return T("Increment letter", LetterKind.Increment, "Salary increment",
            "Dear {Employee.Title} {Employee.Name},\n\nIn recognition of your performance, your monthly gross salary is revised from {Salary.PreviousGross} "
            + "to **{Salary.NewGross}** with effect from **{Salary.NewEffectiveFrom}**, an increase of {Salary.Increase}.\n\n"
            + "All other terms of your employment remain the same.");
        yield return T("Salary certificate", LetterKind.SalaryCertificate, "Salary certificate",
            "To whom it may concern\n\nThis is to certify that **{Employee.Name}** ({Employee.Code}) has been working with {Company.Name} as "
            + "{Employee.Designation} since {Employee.JoinDate}.\n\n{Employee.HisHer} current monthly gross salary is **{Salary.Gross}** "
            + "({Salary.GrossInWords}).\n\nThis certificate is issued at {Employee.HisHer} request for {Input.Purpose}.");
        yield return T("Experience certificate", LetterKind.ExperienceCertificate, "Experience certificate",
            "To whom it may concern\n\nThis is to certify that **{Employee.Name}** ({Employee.Code}) worked with {Company.Name} from "
            + "**{Employee.JoinDate}** to **{Employee.ExitDate}**, a period of {Employee.Service}. At the time of leaving, {Employee.HeShe} was "
            + "{Employee.Designation} in the {Employee.Department} department.\n\nWe wish {Employee.HimHer} success in the future.");
        yield return T("Relieving letter", LetterKind.Relieving, "Relieving letter",
            "Dear {Employee.Title} {Employee.Name},\n\nThis is to confirm that you are relieved from your duties as {Employee.Designation} "
            + "at the close of business on **{Employee.ExitDate}**.\n\nWe thank you for your service and wish you well.");
        yield return T("Warning letter", LetterKind.Warning, "Warning",
            "Dear {Employee.Title} {Employee.Name},\n\nThis letter is a formal warning about **{Input.Reason}**.\n\n{Input.Details}\n\n"
            + "We expect immediate improvement. A repeat may lead to further disciplinary action under company policy.");
    }
}
