using SuperOfc.Domain.Payroll;

namespace SuperOfc.Application.Payroll;

/// <summary>Components and a structure every company starts with (P4-T1); all editable in Settings.</summary>
public static class PayrollDefaults
{
    public static (IReadOnlyList<SalaryComponent> Components, SalaryStructure Structure) Create(Guid companyId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        SalaryComponent C(string code, string name, ComponentKind kind, ComponentCalc calc, int order, decimal rate = 0, long amount = 0, bool basic = false, bool linked = true, bool taxable = true, bool active = true)
            => new()
            {
                Id = newId(), CompanyId = companyId, Code = code, Name = name, Kind = kind, Calc = calc, DisplayOrder = order, DefaultRate = rate,
                DefaultAmountMinor = amount, IsBasic = basic, IsAttendanceLinked = linked, IsTaxable = taxable, IsActive = active,
            };

        var components = new List<SalaryComponent>
        {
            C("BASIC", "Basic salary", ComponentKind.Earning, ComponentCalc.Fixed, 1, basic: true),
            C("HRA", "House rent", ComponentKind.Earning, ComponentCalc.PercentOfBasic, 2, rate: 50),
            C("MEDICAL", "Medical allowance", ComponentKind.Earning, ComponentCalc.Fixed, 3, amount: 150_000, linked: false),
            C("CONV", "Conveyance", ComponentKind.Earning, ComponentCalc.Fixed, 4, amount: 100_000),
            Other(companyId, newId),

            // Placeholder: off until the company decides on provident fund (doc 10 Q13).
            C("PF", "Provident fund", ComponentKind.Deduction, ComponentCalc.PercentOfBasic, 20, rate: 10, taxable: false, active: false),
        };

        var structureId = newId();
        var structure = new SalaryStructure
        {
            Id = structureId,
            CompanyId = companyId,
            Name = "Standard",
            Description = "Basic, house rent 50 % of basic, medical and conveyance.",
            Lines = components.Where(c => c.IsActive && c.Code != OtherCode).Select(c => new SalaryStructureLine
            {
                Id = newId(), StructureId = structureId, ComponentId = c.Id, AmountMinor = c.DefaultAmountMinor, Rate = c.DefaultRate,
            }).ToList(),
        };
        return (components, structure);
    }

    /// <summary>Code of the "Other allowance" row printed by the standard salary certificate.</summary>
    public const string OtherCode = "OTHER";

    /// <summary>
    /// "Other allowance": a fixed earning of zero that is in no structure, so it changes no one's pay until an amount is
    /// set on an employee's salary. The standard salary certificate prints it as a row.
    /// </summary>
    public static SalaryComponent Other(Guid companyId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        return new SalaryComponent
        {
            Id = newId(), CompanyId = companyId, Code = OtherCode, Name = "Other allowance", Kind = ComponentKind.Earning, Calc = ComponentCalc.Fixed,
            DisplayOrder = 5, IsAttendanceLinked = true, IsTaxable = true, IsActive = true,
        };
    }
}
