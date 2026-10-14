using System.Text.RegularExpressions;
using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Payroll;

public enum ComponentKind
{
    Earning = 0,
    Deduction = 1,

    /// <summary>Paid by the employer on top of gross (shown on the payslip, not part of net).</summary>
    EmployerContribution = 2,
}

public enum ComponentCalc
{
    /// <summary>A monthly amount.</summary>
    Fixed = 0,

    /// <summary>A percentage of the basic salary component.</summary>
    PercentOfBasic = 1,

    /// <summary>A percentage of gross (see <see cref="SalaryRules"/> for which earnings count).</summary>
    PercentOfGross = 2,

    /// <summary>An expression of numbers, + - * / ( ) and other component codes.</summary>
    Formula = 3,
}

/// <summary>A pay item such as Basic, House rent or Provident fund (doc 03 Salary and payroll).</summary>
public sealed partial class SalaryComponent : CompanyEntity, ISoftDeletable
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ComponentKind Kind { get; set; }

    public ComponentCalc Calc { get; set; }

    /// <summary>For percentage types: the default percentage (e.g. 50 for 50 %).</summary>
    public decimal DefaultRate { get; set; }

    /// <summary>For fixed types: the default monthly amount in minor units.</summary>
    public long DefaultAmountMinor { get; set; }

    /// <summary>For formula types; component codes refer to monthly (not pro-rated) amounts in currency units.</summary>
    public string? Formula { get; set; }

    /// <summary>Exactly one earning is the basic salary; PercentOfBasic refers to it.</summary>
    public bool IsBasic { get; set; }

    public bool IsTaxable { get; set; } = true;

    /// <summary>Pro-rated by payable days (doc 03 R3 step 2).</summary>
    public bool IsAttendanceLinked { get; set; } = true;

    public int DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? DeletedAtUtc { get; set; }

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,19}$")]
    private static partial Regex CodePattern();
}

/// <summary>A named template of components with default values; copied into an assignment.</summary>
public sealed class SalaryStructure : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<SalaryStructureLine> Lines { get; set; } = [];

    public DateTime? DeletedAtUtc { get; set; }
}

public sealed class SalaryStructureLine
{
    public Guid Id { get; set; }

    public Guid StructureId { get; set; }

    public Guid ComponentId { get; set; }

    public long AmountMinor { get; set; }

    public decimal Rate { get; set; }
}

/// <summary>An employee's pay from a date (doc 03): the source of truth for payroll. Never edited once used in a finalized run.</summary>
public sealed class SalaryAssignment : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public Guid? StructureId { get; set; }

    public string? Note { get; set; }

    public List<SalaryAssignmentLine> Lines { get; set; } = [];

    public DateRange Range => new(EffectiveFrom, EffectiveTo);
}

public sealed class SalaryAssignmentLine
{
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    public Guid ComponentId { get; set; }

    /// <summary>Monthly amount for fixed components.</summary>
    public long AmountMinor { get; set; }

    /// <summary>Percentage for percent components.</summary>
    public decimal Rate { get; set; }
}

/// <summary>Ordering and gross rules shared by the calculator and the preview.</summary>
public static class SalaryRules
{
    /// <summary>
    /// Calculation order: basic first, then everything except percent-of-gross in display order (formulas after
    /// the codes they use), then percent-of-gross earnings, then percent-of-gross deductions and contributions.
    /// Percent-of-gross earnings use the earnings before them; deductions and contributions use the full gross.
    /// </summary>
    public static int Stage(ComponentCalc calc, ComponentKind kind, bool isBasic) => isBasic ? 0
        : calc != ComponentCalc.PercentOfGross ? 1
        : kind == ComponentKind.Earning ? 2
        : 3;
}
