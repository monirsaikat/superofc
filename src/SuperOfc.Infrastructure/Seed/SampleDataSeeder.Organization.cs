using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Infrastructure.Seed;

public sealed partial class SampleDataSeeder
{
    private static readonly string[] FirstNames =
    [
        "Rahim", "Karim", "Nusrat", "Farhana", "Tanvir", "Sadia", "Imran", "Ayesha", "Mahmud", "Rumana", "Arif", "Shirin",
        "Hasan", "Nadia", "Sohel", "Tania", "Jamal", "Mitu", "Rakib", "Sumaiya",
    ];

    private static readonly string[] LastNames = ["Uddin", "Hossain", "Akter", "Rahman", "Islam", "Ahmed", "Khatun", "Chowdhury", "Sarker", "Begum"];

    private readonly Dictionary<string, Guid> _sampleIds = new(StringComparer.Ordinal);

    /// <summary>Departments, designations, locations, holidays and 40 employees.</summary>
    private async Task SeedOrganizationAsync(Guid companyId, CancellationToken ct)
    {
        if (await db.Employees.AnyAsync(e => e.CompanyId == companyId, ct).ConfigureAwait(false))
        {
            return;
        }

        Guid Add<T>(string name, int order)
            where T : MasterDataEntity, new()
        {
            var entity = new T { Id = NewId(), CompanyId = companyId, Name = name, SortOrder = order };
            db.Add(entity);
            _sampleIds[$"{typeof(T).Name}:{name}"] = entity.Id;
            return entity.Id;
        }

        var departments = new[] { "Administration", "Accounts", "Sales", "Production", "IT" }.Select((n, i) => Add<Department>(n, i)).ToArray();
        var designations = new[] { "Manager", "Officer", "Executive", "Assistant", "Operator" }.Select((n, i) => Add<Designation>(n, i)).ToArray();
        var headOffice = Add<Location>("Head office", 0);
        var factory = Add<Location>("Factory", 1);
        Add<Grade>("Grade A", 0);
        Add<Grade>("Grade B", 1);

        var year = DateOnly.FromDateTime(UtcNow).Year;
        foreach (var (month, day, name) in new[] { (2, 21, "Language Martyrs Day"), (3, 26, "Independence Day"), (5, 1, "May Day"), (12, 16, "Victory Day") })
        {
            db.Holidays.Add(new Holiday { Id = NewId(), CompanyId = companyId, Date = new DateOnly(year, month, day), Name = name, IsPaid = true });
        }

        var random = new Random(42);
        for (var i = 1; i <= 40; i++)
        {
            var joined = DateOnly.FromDateTime(UtcNow).AddDays(-random.Next(60, 1500));
            var employee = new Employee
            {
                Id = NewId(),
                CompanyId = companyId,
                Code = $"E{i:0000}",
                FullName = $"{FirstNames[random.Next(FirstNames.Length)]} {LastNames[random.Next(LastNames.Length)]}",
                Gender = i % 3 == 0 ? Gender.Female : Gender.Male,
                Phone = $"017{random.Next(10000000, 99999999)}",
                JoinDate = joined,
                ConfirmationDate = joined.AddMonths(6),
                Status = EmployeeStatus.Active,
                DepartmentId = departments[i % departments.Length],
                DesignationId = designations[Math.Min(i % 7, designations.Length - 1)],
                LocationId = i % 4 == 0 ? factory : headOffice,
                PaymentMode = i % 5 == 0 ? PaymentMode.Cash : PaymentMode.Bank,
                BankName = i % 5 == 0 ? null : "Sample Bank",
                BankAccount = i % 5 == 0 ? null : $"00{random.Next(10000000, 99999999)}",
                IsOvertimeEligible = i % 3 != 0,
            };
            db.Employees.Add(employee);
            db.EmploymentEvents.Add(new EmploymentEvent { Id = NewId(), CompanyId = companyId, EmployeeId = employee.Id, Type = EmploymentEventType.Joined, Date = joined, Description = "Joined" });
        }
    }
}
