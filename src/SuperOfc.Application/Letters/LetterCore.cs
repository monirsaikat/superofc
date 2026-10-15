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

/// <summary>What kind of value a letter field holds; it decides which formats (<c>{Key:format}</c>) the field accepts.</summary>
public enum LetterFieldType
{
    /// <summary>One line of text. Formats: upper, lower, cap, title.</summary>
    Text = 0,

    /// <summary>Text that may hold several lines (address, responsibilities, typed inputs). Same formats as text.</summary>
    Lines = 1,

    /// <summary>A date. Formats: ord, or a .NET custom date pattern such as <c>d MMMM yyyy</c>.</summary>
    Date = 2,

    /// <summary>An amount. Formats: n0, n2, words.</summary>
    Money = 3,
}

/// <summary>A field that can be used in a letter. <see cref="Private"/> fields show pay or bank details.</summary>
public sealed record LetterField(string Key, string Description, bool Private = false, LetterFieldType Type = LetterFieldType.Text)
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
        new("Employee.SonDaughter", "son or daughter (from gender)"),
        new("Employee.FatherName", "Father's name"),
        new("Employee.Designation", "Designation"),
        new("Employee.Responsibilities", "Responsibilities of the designation, one per line", Type: LetterFieldType.Lines),
        new("Employee.Department", "Department"),
        new("Employee.Location", "Location"),
        new("Employee.Grade", "Grade"),
        new("Employee.JoinDate", "Join date", Type: LetterFieldType.Date),
        new("Employee.ConfirmationDate", "Confirmation date", Type: LetterFieldType.Date),
        new("Employee.ExitDate", "Last working day (employees who left)", Type: LetterFieldType.Date),
        new("Employee.ExitReason", "Reason for leaving"),
        new("Employee.Service", "Length of service, e.g. 3 years 2 months"),
        new("Employee.DateOfBirth", "Date of birth", Type: LetterFieldType.Date),
        new("Employee.Address", "Address", Type: LetterFieldType.Lines),
        new("Employee.Phone", "Phone"),
        new("Employee.Email", "E-mail"),
        new("Employee.NationalId", "National ID"),
        new("Employee.BankName", "Bank name", Private: true),
        new("Employee.BankAccount", "Bank account", Private: true),
        new("Employee.TaxId", "Tax ID", Private: true),
        new("Salary.Gross", "Monthly gross salary on the letter date", Private: true, Type: LetterFieldType.Money),
        new("Salary.GrossInWords", "Monthly gross salary in words", Private: true),
        new("Salary.Net", "Monthly net salary on the letter date", Private: true, Type: LetterFieldType.Money),
        new("Salary.NetInWords", "Monthly net salary in words", Private: true),
        new("Salary.YearlyGross", "Gross salary for 12 months", Private: true, Type: LetterFieldType.Money),
        new("Salary.EffectiveFrom", "Date the salary on the letter date started", Private: true, Type: LetterFieldType.Date),
        new("Salary.NewGross", "Latest salary, also if it starts later (increment letters)", Private: true, Type: LetterFieldType.Money),
        new("Salary.NewEffectiveFrom", "Date the latest salary starts", Private: true, Type: LetterFieldType.Date),
        new("Salary.PreviousGross", "Salary before the latest one", Private: true, Type: LetterFieldType.Money),
        new("Salary.Increase", "Latest salary minus the one before", Private: true, Type: LetterFieldType.Money),
        new("Company.Name", "Company name"),
        new("Company.Address", "Company address"),
        new("Company.Phone", "Company phone"),
        new("Company.Email", "Company e-mail"),
        new("Company.CurrencyCode", "Currency code, e.g. BDT"),
        new("Company.CurrencySymbol", "Currency symbol, e.g. ৳"),
        new("Letter.Number", "Reference number, e.g. HR/2026/0001"),
        new("Letter.Date", "Letter date", Type: LetterFieldType.Date),
        new("Today", "Today's date", Type: LetterFieldType.Date),
    ];

    /// <summary>Salary component amounts are also available as <c>{Salary.CODE}</c>, for example <c>{Salary.BASIC}</c>.</summary>
    public const string ComponentHint = "Salary.<component code>";

    public static bool IsKnown(string key, IReadOnlySet<string> componentCodes) => TypeOf(key, componentCodes) is not null;

    /// <summary>The type of a field, or null when the field does not exist.</summary>
    public static LetterFieldType? TypeOf(string key, IReadOnlySet<string> componentCodes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(componentCodes);
        if (All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) is { } field)
        {
            return field.Type;
        }

        if (key.StartsWith(LetterText.InputGroup + ".", StringComparison.OrdinalIgnoreCase))
        {
            return LetterFieldType.Lines;
        }

        return key.StartsWith("Salary.", StringComparison.OrdinalIgnoreCase) && componentCodes.Contains(key[7..]) ? LetterFieldType.Money : null;
    }

    /// <summary>
    /// Null when <paramref name="format"/> suits the field, otherwise the message for the template editor, e.g.
    /// <c>Unknown format "xyz" in {Employee.Name:xyz}.</c> Unknown fields return null here; they are reported on their own.
    /// </summary>
    public static string? FormatError(string key, string? format, IReadOnlySet<string> componentCodes)
    {
        if (format is null || TypeOf(key, componentCodes) is not { } type)
        {
            return null;
        }

        var ok = type switch
        {
            LetterFieldType.Date => LetterFormats.IsDateFormat(format),
            LetterFieldType.Money => LetterFormats.IsMoneyFormat(format),
            _ => LetterFormats.IsTextFormat(format),
        };
        return ok ? null : $"Unknown format \"{format}\" in {{{key}:{format}}}.";
    }

    /// <summary>Every format problem in the texts, each once.</summary>
    public static IReadOnlyList<string> FormatErrors(IReadOnlySet<string> componentCodes, params string?[] texts)
        => [.. texts.SelectMany(LetterText.Placeholders).Select(p => FormatError(p.Key, p.Format, componentCodes)).OfType<string>().Distinct(StringComparer.Ordinal)];

    public static bool IsPrivate(string key)
        => key.StartsWith("Salary.", StringComparison.OrdinalIgnoreCase) || All.Any(f => f.Private && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool UsesPrivate(params string?[] texts) => texts.SelectMany(LetterText.Fields).Any(IsPrivate);
}

/// <summary>The formats a field may carry after a colon (see docs/specs/word-letter-templates.md).</summary>
public static class LetterFormats
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly string[] TextFormats = ["upper", "lower", "cap", "title"];

    private static readonly string[] MoneyFormats = ["n0", "n2", "words"];

    /// <summary>The ordinal suffix of a number: st, nd, rd or th (11, 12 and 13 give th).</summary>
    public static string OrdinalSuffix(int number)
    {
        var n = Math.Abs(number);
        if (n % 100 is >= 11 and <= 13)
        {
            return "th";
        }

        return (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

    public static bool IsTextFormat(string format) => TextFormats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsMoneyFormat(string format) => MoneyFormats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for <c>ord</c> and for any custom date pattern .NET can render.</summary>
    public static bool IsDateFormat(string format) => FormatDate(new DateOnly(2023, 6, 1), format) is not null;

    /// <summary>Text formats; null when the format is not one of them.</summary>
    public static string? FormatText(string value, string format)
    {
        ArgumentNullException.ThrowIfNull(value);
        var culture = CultureInfo.CurrentCulture;
        return format.Trim().ToUpperInvariant() switch
        {
            "UPPER" => value.ToUpper(culture),
            "LOWER" => value.ToLower(culture),
            "CAP" => value.Length == 0 ? value : char.ToUpper(value[0], culture) + value[1..],
            "TITLE" => culture.TextInfo.ToTitleCase(value),
            _ => null,
        };
    }

    /// <summary>
    /// <c>ord</c> gives the day's ordinal suffix; anything else is a custom pattern rendered in English (a one-character
    /// pattern stays custom, so <c>d</c> is the day). Null when the pattern is invalid.
    /// </summary>
    public static string? FormatDate(DateOnly date, string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (string.Equals(format.Trim(), "ord", StringComparison.OrdinalIgnoreCase))
        {
            return OrdinalSuffix(date.Day);
        }

        try
        {
            return date.ToString(format.Length == 1 ? "%" + format : format, English);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Money formats with the company's grouping; null when the format is not one of them.</summary>
    public static string? FormatMoney(Money amount, string format, MoneyFormatter money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return format.Trim().ToUpperInvariant() switch
        {
            "N0" => money.Format(amount, withSymbol: false, decimals: 0),
            "N2" => money.Format(amount, withSymbol: false, decimals: 2),
            "WORDS" => Words(amount, money.Grouping),
            _ => null,
        };
    }

    /// <summary>Whole units in words, rounded half away from zero like <c>n0</c>, so the figure and the words agree.</summary>
    private static string Words(Money amount, NumberGrouping grouping)
    {
        var whole = (long)Math.Round(Math.Abs(amount.Major), 0, MidpointRounding.AwayFromZero);
        return (amount.IsNegative && whole != 0 ? "Minus " : string.Empty) + AmountInWords.Words(whole, grouping);
    }
}

/// <summary>
/// The values of every field for one employee, kept typed (text, date, money) so each placeholder can pick its own format.
/// <see cref="Resolve"/> is the <see cref="LetterResolver"/> passed to <see cref="LetterText.Fill(string, LetterResolver, ICollection{string})"/>.
/// </summary>
public sealed class LetterValues
{
    private readonly Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dateFormat">The company's date pattern, used when a date has no format.</param>
    /// <param name="money">The company's money formatter (symbol and grouping).</param>
    public LetterValues(string dateFormat, MoneyFormatter money)
    {
        DateFormat = dateFormat;
        Money = money;
    }

    public string DateFormat { get; }

    public MoneyFormatter Money { get; }

    /// <summary>The value printed without a format; setting stores text.</summary>
    public string? this[string key]
    {
        get => Resolve(key, null);
        set => values[key] = value;
    }

    public static LetterValues For(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);
        return new LetterValues(company.DateFormat, MoneyFormatter.For(company));
    }

    public bool Contains(string key) => values.ContainsKey(key);

    public void Set(string key, string? value) => values[key] = value;

    public void Set(string key, DateOnly? value) => values[key] = value;

    public void Set(string key, Money? value) => values[key] = value;

    /// <summary>Adds the values typed when issuing as <c>{Input.Name}</c> fields.</summary>
    public void AddInputs(IReadOnlyDictionary<string, string?>? inputs)
    {
        foreach (var (name, value) in inputs ?? new Dictionary<string, string?>())
        {
            values[LetterText.InputGroup + "." + name] = value?.Trim();
        }
    }

    /// <summary>
    /// The text for <c>{key}</c> or <c>{key:format}</c>; null when the field has no value. A format that does not suit the
    /// value (only possible in an unsaved preview) is ignored.
    /// </summary>
    public string? Resolve(string key, string? format)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            null => null,
            DateOnly date => (format is null ? null : LetterFormats.FormatDate(date, format)) ?? date.ToString(DateFormat, CultureInfo.CurrentCulture),
            Money amount => (format is null ? null : LetterFormats.FormatMoney(amount, format, Money)) ?? Money.Format(amount),
            string text => (format is null || string.IsNullOrWhiteSpace(text) ? null : LetterFormats.FormatText(text, format)) ?? text,
            _ => Convert.ToString(value, CultureInfo.CurrentCulture),
        };
    }
}

/// <summary>Collects the values of every field for a set of employees in a few queries.</summary>
public sealed class LetterData(IAppDbContext db, ICurrentUser user, ICompanyContext company, IPolicyProvider policies)
{
    public async Task<IReadOnlySet<string>> ComponentCodesAsync(CancellationToken cancellationToken)
        => (await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null).Select(c => c.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Field values per employee. Salary fields are filled only when <paramref name="withSalary"/> is true.</summary>
    public async Task<Dictionary<Guid, LetterValues>> ValuesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly letterDate, bool withSalary,
        CancellationToken cancellationToken)
    {
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");

        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && employeeIds.Contains(e.Id)).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var names = await NamesAsync(cancellationToken).ConfigureAwait(false);
        var designationIds = employees.Select(e => e.DesignationId).OfType<Guid>().Distinct().ToList();
        var responsibilities = (await db.Designations.AsNoTracking().Where(d => designationIds.Contains(d.Id) && d.Description != null)
                .Select(d => new { d.Id, d.Description }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Description!.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

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

        var result = new Dictionary<Guid, LetterValues>();
        foreach (var e in employees)
        {
            var female = e.Gender == Gender.Female;
            var known = e.Gender is Gender.Male or Gender.Female;
            var v = LetterValues.For(info);
            v.Set("Employee.Name", e.FullName);
            v.Set("Employee.Code", e.Code);
            v.Set("Employee.Title", known ? (female ? "Ms." : "Mr.") : string.Empty);
            v.Set("Employee.HeShe", known ? (female ? "she" : "he") : "they");
            v.Set("Employee.HimHer", known ? (female ? "her" : "him") : "them");
            v.Set("Employee.HisHer", known ? (female ? "her" : "his") : "their");
            v.Set("Employee.SonDaughter", known ? (female ? "daughter" : "son") : string.Empty);
            v.Set("Employee.FatherName", e.FatherName);
            v.Set("Employee.Designation", Name(names, e.DesignationId));
            v.Set("Employee.Responsibilities", e.DesignationId is { } designation ? responsibilities.GetValueOrDefault(designation) : null);
            v.Set("Employee.Department", Name(names, e.DepartmentId));
            v.Set("Employee.Location", Name(names, e.LocationId));
            v.Set("Employee.Grade", Name(names, e.GradeId));
            v.Set("Employee.JoinDate", e.JoinDate);
            v.Set("Employee.ConfirmationDate", e.ConfirmationDate);
            v.Set("Employee.ExitDate", e.ExitDate);
            v.Set("Employee.ExitReason", e.ExitReason);
            v.Set("Employee.Service", LetterText.Duration(e.JoinDate, e.ExitDate is { } exit && exit < letterDate ? exit.AddDays(1) : letterDate));
            v.Set("Employee.DateOfBirth", e.DateOfBirth);
            v.Set("Employee.Address", e.Address);
            v.Set("Employee.Phone", e.Phone);
            v.Set("Employee.Email", e.Email);
            v.Set("Employee.NationalId", e.NationalId);
            v.Set("Company.Name", info.Name);
            v.Set("Company.Address", info.Address);
            v.Set("Company.Phone", info.Phone);
            v.Set("Company.Email", info.Email);
            v.Set("Company.CurrencyCode", info.CurrencyCode);
            v.Set("Company.CurrencySymbol", info.CurrencySymbol);
            v.Set("Letter.Date", letterDate);
            v.Set("Today", letterDate);

            if (withSalary)
            {
                v.Set("Employee.BankName", e.BankName);
                v.Set("Employee.BankAccount", e.BankAccount);
                v.Set("Employee.TaxId", e.TaxIdentifier);
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
                    v.Set("Salary.Gross", now.Gross);
                    v.Set("Salary.GrossInWords", AmountInWords.Format(now.Gross, info.CurrencyCode, info.Grouping));
                    v.Set("Salary.Net", now.Net);
                    v.Set("Salary.NetInWords", AmountInWords.Format(now.Net, info.CurrencyCode, info.Grouping));
                    v.Set("Salary.YearlyGross", new Money(now.Gross.Minor * 12));
                    v.Set("Salary.EffectiveFrom", current!.EffectiveFrom);
                    foreach (var line in now.Lines)
                    {
                        v.Set("Salary." + line.Code, line.Amount);
                    }
                }

                if (newest is not null)
                {
                    v.Set("Salary.NewGross", newest.Gross);
                    v.Set("Salary.NewEffectiveFrom", latest!.EffectiveFrom);
                }

                if (before is not null && newest is not null)
                {
                    v.Set("Salary.PreviousGross", before.Gross);
                    v.Set("Salary.Increase", new Money(newest.Gross.Minor - before.Gross.Minor));
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
